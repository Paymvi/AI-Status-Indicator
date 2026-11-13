// Claude Status - phone dashboard (optional; off unless "phoneDashboard": true in config.json).
// Compiled together with indicator.cs (C# 5, Windows PowerShell 5.1's Add-Type).
//
// A deliberately tiny HTTP/1.1 server bound to 127.0.0.1 only, inside the indicator process. The phone
// reaches it through `tailscale serve`, which forwards tailnet requests to this loopback port; it is never
// bound to a network interface. It serves the board the indicator itself shows (published after every
// reload, so aliases, numbering, ordering, cleanup and dismissal are the desktop's own) and nothing else:
//   GET  /  /phone.css  /phone.js        the page (fixed list; request paths never reach the file system)
//   GET  /favicon.ico  /worker-192.png  /apple-touch-icon.png   the default worker, as the page's icons
//   GET  /api/board[?v=N&wait=1]          the Worker Board; with wait=1 it answers when it changes (<= 20 s)
//   POST /api/dismiss  {"k":..,"d":..}    dismiss one specific DONE occurrence
//   POST /api/focus    {"k":..}           bring that worker's window forward on the laptop (the desktop's
//                                         own click-on-a-worker action; the laptop picks the window)
//   POST /api/usage-refresh {}            re-read the Claude Usage data on the laptop now (local files)
//   GET  /sw.js                           the service worker that shows push notifications (push.cs)
//   POST /api/push {"op":..}              this phone's notifications: on/off, preferences, a test
//                                         (only the public VAPID key is ever sent to the page)
//   GET  /api/git?op=repos|review|diff|job  Git review for the workers' repositories (git.cs; read-only)
//   POST /api/git {"op":"commit"|"push"}  YOUR explicit Send: commit the files you picked with your message
//                                         and push, or retry the push (git.cs). Repositories are named by
//                                         tokens; the review screen shows the repository's path and diffs.
//   GET  /api/queue?op=projects           the projects a task can be scheduled in (friendly names + tokens)
//   POST /api/queue {"op":"save"|"cancel"|"stop"}  schedule / edit a task, cancel one that hasn't started, or
//                                         stop a running one: the desktop's own QueueEngine actions, run on its
//                                         UI thread. Projects and tasks are named by tokens; never a path.
// Only display data leaves the process: friendly name, state, when that state began, a short error type,
// a creature look number, that session's context-window percentage (and whether that reading is older than
// its latest activity), and opaque tokens. No session IDs, process IDs, folder paths or window data.
// The board also carries the account-wide Claude Usage figures (titles, percentages, reset times, status
// text); never account IDs, credentials or file contents. And the prompt scheduler's tasks (title, prompt text,
// project folder name, state, times; see PublishQueue), which the phone can schedule, edit and cancel (QueuePost).
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web.Script.Serialization;
using Microsoft.Win32.SafeHandles;

namespace ClaudeStatus
{
    public enum DismissResult { Dismissed, Gone, Changed, NotDone }

    // Outcome of bringing a worker's window forward. Focused only when Windows confirms that window is now
    // in front. NotYet / Ended / Unknown / Ambiguous are the desktop's own "couldn't identify" reasons.
    public enum FocusResult { Focused, Refused, Locked, NotYet, Ended, Unknown, Ambiguous, Gone, Busy, Unavailable }

    // Dismissing a DONE worker = deleting Claude Status's own state file for it, and only while that file
    // still describes the very DONE occurrence the user saw. The check and the delete happen on one open
    // handle that doesn't share write/delete access, so the hook can't replace the file in between (its
    // atomic replace waits and retries); a newer prompt is therefore never dismissed by an older click.
    public static class Dismissal
    {
        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool SetFileInformationByHandle(SafeFileHandle h, int infoClass, ref byte deleteFile, int size);
        const int FileDispositionInfo = 4;
        static readonly Regex SafeId = new Regex("^[A-Za-z0-9_-]{1,128}$");

        // Identifies one particular stay in a state: any later hook write changes 'updated'.
        public static string Occurrence(SessionInfo s) { return s.State + "|" + s.StateSince.Ticks + "|" + s.Updated.Ticks; }

        public static DismissResult DismissDone(string sessionsDir, string id, string occurrence)
        {
            if (id == null || !SafeId.IsMatch(id)) return DismissResult.Gone;
            string path = Path.Combine(sessionsDir, id + ".json");
            for (int attempt = 0; ; attempt++)
            {
                FileStream fs;
                try { fs = new FileStream(path, FileMode.Open, FileSystemRights.Read | FileSystemRights.Delete, FileShare.Read, 4096, FileOptions.None); }
                catch (FileNotFoundException) { return DismissResult.Gone; }
                catch (DirectoryNotFoundException) { return DismissResult.Gone; }
                catch (UnauthorizedAccessException) { return DismissResult.Gone; } // already being deleted
                catch (IOException) { if (attempt < 25) { Thread.Sleep(8); continue; } return DismissResult.Changed; } // hook mid-write
                using (fs)
                {
                    SessionInfo cur;
                    try
                    {
                        string text;
                        using (var sr = new StreamReader(fs, new UTF8Encoding(false), true, 4096, true)) text = sr.ReadToEnd();
                        var d = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(text);
                        if (d == null) return DismissResult.Changed;
                        cur = Board.Parse(id, d, DateTime.MinValue, File.GetLastWriteTimeUtc(path));
                    }
                    catch { return DismissResult.Changed; }
                    if (cur.State != Board.Done) return DismissResult.NotDone;
                    if (Occurrence(cur) != occurrence) return DismissResult.Changed;
                    byte del = 1;   // delete when this (exclusive-for-writers) handle closes
                    return SetFileInformationByHandle(fs.SafeFileHandle, FileDispositionInfo, ref del, 1) ? DismissResult.Dismissed : DismissResult.Changed;
                }
            }
        }
    }

    // One session's context-window fullness, as statusline.ps1 recorded it from Claude Code's documented
    // status line data (state\context\<session id>.json). Telemetry only: it never touches the session's
    // state file, so lifecycle timers, ordering and dismissal tokens are unaffected by it.
    public class ContextReading
    {
        public double? Percent;   // null = Claude Code didn't report it (early in a session, right after /compact)
        public long Size;
        public DateTime At;

        // An older reading than the session's latest lifecycle event (minus slack for the status line's
        // debounce): e.g. a session now continued somewhere that doesn't run the status line.
        public const int OldAfterSeconds = 120;
        public bool IsOldFor(SessionInfo s) { return At < s.Updated.AddSeconds(-OldAfterSeconds); }

        public static ContextReading Parse(string json)
        {
            try
            {
                var d = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(json);
                if (d == null || Board.Str(d, "v") != "1") return null;
                DateTime at;
                if (!DateTime.TryParse(Board.Str(d, "at"), System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind, out at)) return null;
                var c = new ContextReading { At = at.ToUniversalTime() };
                object p;
                if (d.TryGetValue("pct", out p) && p != null)
                {
                    if (!(p is int || p is long || p is double || p is decimal)) return null;
                    double v = Convert.ToDouble(p);
                    if (double.IsNaN(v) || v < 0 || v > 100) return null;
                    c.Percent = v;
                }
                long size;
                if (long.TryParse(Board.Str(d, "size"), out size) && size > 0) c.Size = size;
                return c;
            }
            catch { return null; }
        }
    }

    // Notification pictures: a worker's creature in the pose of its status, cropped from workers.png (every look
    // in every pose; tests\worker-icons.mjs draws it from phone.js's art). Each call opens its own copy, so the
    // push thread and the UI thread share nothing and one worker's picture can't end up on another's message.
    public static class WorkerArt
    {
        public const int DefaultLook = 0, PushSize = 128;   // 128 px: ~2 KB of Base64 at most, within a push message

        // Throws if the sheet can't be read. A look outside the sheet (no character) is the default worker.
        public static Bitmap Draw(string root, int look, string state, int size)
        {
            if (look < 0 || look >= PhoneServer.Looks) look = DefaultLook;
            int pose = state == Board.NeedsYou ? 1 : state == Board.Done ? 2 : state == Board.Error ? 3 : 0;
            var cell = new Rectangle(look % PhoneServer.Hats * 16, (pose * PhoneServer.Bodies + look / PhoneServer.Hats) * 16, 16, 16);
            var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
            using (var sheet = new Bitmap(Path.Combine(root, "workers.png")))
            using (var g = Graphics.FromImage(bmp))
            {
                g.InterpolationMode = InterpolationMode.NearestNeighbor;
                g.PixelOffsetMode = PixelOffsetMode.Half;
                g.DrawImage(sheet, new Rectangle(0, 0, size, size), cell, GraphicsUnit.Pixel);
            }
            return bmp;
        }

        // The tray icon: the default worker in the pose of its status, scaled by a whole number (crisp pixels;
        // at 20 or 24 px that leaves a small margin) and centred, plus a status dot in the bottom-right corner
        // inside a transparent cut-out, so it reads on light and dark taskbars. No dot when idle (null badge).
        public static Bitmap Tray(string root, string state, Color? badge, int size)
        {
            int k = Math.Max(1, size / 16), art = 16 * k, off = (size - art) / 2;
            var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
            using (var cell = Draw(root, DefaultLook, state, art))
            using (var g = Graphics.FromImage(bmp))
            {
                // Plain Clawde: the default look's party hat (columns 4-11 above the body's top edge, row 6)
                // is erased, and the body (rows 6-15) moved up 2 rows to sit in the middle.
                using (var cg = Graphics.FromImage(cell)) { cg.CompositingMode = CompositingMode.SourceCopy; using (var b = new SolidBrush(Color.Transparent)) cg.FillRectangle(b, 4 * k, 0, 8 * k, 6 * k); }
                g.DrawImageUnscaled(cell, off, off - 2 * k);
                if (badge == null) return bmp;
                float d = size * 6 / 16f, cut = Math.Max(1f, size / 16f), x = size - d, y = size - d;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.CompositingMode = CompositingMode.SourceCopy;
                using (var b = new SolidBrush(Color.Transparent)) g.FillEllipse(b, x - cut, y - cut, d + cut * 2, d + cut * 2);
                using (var b = new SolidBrush(badge.Value)) g.FillEllipse(b, x, y, d - 0.5f, d - 0.5f);
            }
            return bmp;
        }

        // Base64 PNG, or null if the sheet can't be read.
        public static string Png(string root, int look, string state)
        {
            try
            {
                using (var bmp = Draw(root, look, state, PushSize))
                using (var ms = new MemoryStream())
                {
                    bmp.Save(ms, ImageFormat.Png);
                    return Convert.ToBase64String(ms.ToArray());
                }
            }
            catch { return null; }
        }
    }

    public class PhoneRow
    {
        public string Key = "", Occ = "", Name = "", State = "", Err = "";
        public long Since;
        public int Look;
        public Dictionary<string, object> Ctx;   // {p, o} or null (no reported reading)
        internal string SessionId = "", Occurrence = "";
    }

    public sealed class PhoneServer
    {
        public const int DefaultPort = 48521;
        const int MaxHeader = 8192, MaxBody = 1024, MaxGitBody = 512 * 1024, MaxConnections = 32, WaitMs = 20000, RepeatMs = 1500;
        // Creature looks: phone.js draws look L as hat (L % Hats) on body colour (L / Hats); see HATS and
        // BODIES there. Bodies 0 .. OrangeBodies-1 are the orange family.
        public const int Hats = 13, Bodies = 8, OrangeBodies = 4, Looks = Hats * Bodies;
        public static bool IsOrange(int look) { return look / Hats < OrangeBodies; }
        static readonly Regex TokenRx = new Regex("^[A-Za-z0-9_-]{22}$");
        static readonly DateTime Epoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        static readonly Dictionary<string, string[]> Pages = new Dictionary<string, string[]>
        {
            { "/", new[] { "phone.html", "text/html; charset=utf-8" } },
            { "/phone.css", new[] { "phone.css", "text/css; charset=utf-8" } },
            { "/phone.js", new[] { "phone.js", "text/javascript; charset=utf-8" } },
            { "/sw.js", new[] { "sw.js", "text/javascript; charset=utf-8" } },
            { "/favicon.ico", new[] { "favicon.ico", "image/x-icon" } },
            { "/worker-192.png", new[] { "worker-192.png", "image/png" } },
            { "/apple-touch-icon.png", new[] { "apple-touch-icon.png", "image/png" } },
        };
        const string Csp = "default-src 'none'; script-src 'self'; style-src 'self'; connect-src 'self'; img-src 'self'; worker-src 'self'; base-uri 'none'; form-action 'none'; frame-ancestors 'none'";

        readonly int port;
        readonly string root;
        readonly Func<string, string, DismissResult> dismiss;
        readonly Func<string, FocusResult> focus;
        readonly Func<string[]> tailnetLookup;
        readonly byte[] secret = new byte[32];
        readonly object gate = new object(), hostGate = new object(), focusGate = new object();
        readonly Dictionary<string, int> looks = new Dictionary<string, int>();   // session -> look (UI thread only: Publish, LookOf)
        readonly Dictionary<string, byte[]> files = new Dictionary<string, byte[]>();

        List<PhoneRow> rows = new List<PhoneRow>();
        bool summaryOn, published;
        string summaryText = "", signature = "";
        long version;
        TcpListener listener;
        volatile bool running;
        int active;
        string[] tailnetNames = new string[0];
        DateTime tailnetCheckedUtc = DateTime.MinValue;
        bool focusBusy;
        string lastFocusKey = "";
        DateTime lastFocusUtc = DateTime.MinValue;
        FocusResult lastFocus;
        Dictionary<string, object> usageData;
        string usageSig = "";
        Dictionary<string, object> queueData;   // null until the indicator publishes the scheduler
        string queueSig = "";
        DateTime lastUsageRefreshUtc = DateTime.MinValue;

        // Asks the indicator to re-read Claude Usage now (null = not available).
        public Func<bool> UsageRefresh;
        // Phone push notifications (null = not available).
        public PushNotifier Push;
        // Git review / commit & push for the workers' repositories (git.cs).
        public readonly GitDesk GitOps;
        List<string> queueProjects = new List<string>();
        // Scheduling from the phone (null Queue = not available). OnUi runs the engine action on the indicator's UI
        // thread and waits (null result = it didn't answer in time); null OnUi = call directly (tests).
        public QueueEngine Queue;
        public Func<Func<object>, object> OnUi;
        public IDictionary<string, object> Aliases;           // "projectAliases", for friendly project names
        public string ClaudeProjectsDir = Sessions.ProjectsDir();
        readonly object opsGate = new object();
        readonly Dictionary<string, Dictionary<string, object>> queueOps = new Dictionary<string, Dictionary<string, object>>();   // request -> its answer (null = in progress)
        readonly Queue<string> queueOpOrder = new Queue<string>();
        List<string[]> projCache;   // [token, path, name]
        DateTime projAt = DateTime.MinValue;

        public int Port { get { return port; } }
        public bool Running { get { return running; } }
        public string Error { get; private set; }
        public long Version { get { lock (gate) return version; } }

        public PhoneServer(string root, int port, Func<string, string, DismissResult> dismiss, Func<string[]> tailnetLookup)
            : this(root, port, dismiss, null, tailnetLookup) { }

        // dismiss(sessionId, occurrence) performs the dismissal (see Dismissal); focus(sessionId) brings that
        // worker's window forward (null = not available). tailnetLookup returns this machine's Tailscale
        // names/IPs; null = ask tailscale.exe (only when a non-loopback Host shows up).
        public PhoneServer(string root, int port, Func<string, string, DismissResult> dismiss, Func<string, FocusResult> focus, Func<string[]> tailnetLookup)
        {
            this.root = root; this.port = port; this.dismiss = dismiss; this.focus = focus;
            this.tailnetLookup = tailnetLookup ?? LookupTailnetNames;
            using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(secret);
            Error = "";
            GitOps = new GitDesk(Token, () =>
            {
                var list = new List<GitWorker>();
                lock (gate) foreach (var r in rows) list.Add(new GitWorker { Key = r.Key, Name = r.Name, State = r.State, SessionId = r.SessionId });
                return list;
            });
            GitOps.Projects = () => { lock (gate) return new List<string>(queueProjects); };
        }

        public bool Start()
        {
            foreach (var p in Pages.Values)
                try { files[p[0]] = File.ReadAllBytes(Path.Combine(root, p[0])); } catch { }
            try
            {
                listener = new TcpListener(IPAddress.Loopback, port);
                listener.ExclusiveAddressUse = true;   // no other program can bind this port alongside us
                listener.Start(16);
                NotInherited(listener.Server);
            }
            catch (SocketException e)
            {
                Error = e.SocketErrorCode == SocketError.AddressAlreadyInUse || e.SocketErrorCode == SocketError.AccessDenied
                    ? "port " + port + " is already in use" : "couldn't listen on port " + port + " (" + e.SocketErrorCode + ")";
                try { listener.Stop(); } catch { }
                listener = null;
                return false;
            }
            running = true;
            new Thread(AcceptLoop) { IsBackground = true, Name = "ClaudeStatusPhone" }.Start();
            return true;
        }

        // .NET Framework sockets are inheritable: a child process started with inherited handles (e.g. the
        // tailscale.exe lookup below) would otherwise keep the port open and silently accepting after Stop().
        [DllImport("kernel32.dll")] static extern bool SetHandleInformation(IntPtr h, int mask, int flags);
        static void NotInherited(Socket s) { try { SetHandleInformation(s.Handle, 1 /* HANDLE_FLAG_INHERIT */, 0); } catch { } }

        public void Stop()
        {
            running = false;
            try { if (listener != null) listener.Stop(); } catch { }
            lock (gate) Monitor.PulseAll(gate);
        }

        // Called by the indicator after every reload with exactly the rows it shows.
        public void Publish(List<SessionInfo> ordered, bool summary, string summaryLine) { Publish(ordered, summary, summaryLine, null); }

        // context: session id -> its latest context reading (from statusline.ps1); only the percentage and
        // whether it predates the session's latest activity reach the phone.
        public void Publish(List<SessionInfo> ordered, bool summary, string summaryLine, Dictionary<string, ContextReading> context)
        {
            var next = new List<PhoneRow>();
            var sig = new StringBuilder(summary ? "S" + summaryLine : "-");
            AssignLooks(ordered);
            foreach (var s in ordered)
            {
                bool done = s.State == Board.Done;
                string occ = Dismissal.Occurrence(s);
                var r = new PhoneRow
                {
                    Key = Token("k|" + s.Id), Occ = done ? Token("o|" + s.Id + "|" + occ) : "",
                    Name = s.DisplayName, State = s.State, Err = s.State == Board.Error ? Board.ShortError(s.Detail) : "",
                    Since = Ms(s.StateSince), Look = looks[s.Id], SessionId = s.Id, Occurrence = occ,
                };
                ContextReading c;
                if (context != null && context.TryGetValue(s.Id, out c) && c != null && c.Percent.HasValue)
                    r.Ctx = new Dictionary<string, object> { { "p", Math.Round(c.Percent.Value, 1) }, { "o", c.IsOldFor(s) } };
                next.Add(r);
                sig.Append('\n').Append(r.Key).Append('|').Append(r.Occ).Append('|').Append(r.Name).Append('|').Append(r.State).Append('|').Append(r.Err).Append('|').Append(r.Since).Append('|').Append(r.Look)
                   .Append('|').Append(r.Ctx == null ? "-" : r.Ctx["p"] + "/" + r.Ctx["o"]);
            }
            lock (gate)
            {
                string s2 = sig.ToString();
                if (published && s2 == signature) return;
                rows = next; summaryOn = summary; summaryText = summaryLine; signature = s2;
                published = true; version++;
                Monitor.PulseAll(gate);
            }
        }

        // Called by the indicator when the Claude Usage figures change (see Usage.ToPhone).
        public void PublishUsage(Dictionary<string, object> data, string signature)
        {
            lock (gate)
            {
                if (signature == usageSig && usageData != null) { usageData = data; return; }   // only "checked" moved
                usageData = data; usageSig = signature;
                if (!published) return;
                version++;
                Monitor.PulseAll(gate);
            }
        }

        // Called by the indicator when the prompt scheduler changes (read-only: the phone never edits or runs tasks).
        // Display data only: titles, prompts, the project's friendly name (alias or folder name; not its path), states and times.
        public const int QueuePromptMax = 20000, QueuePastMax = 30;
        public void PublishQueue(QueueData d, string warning)
        {
            var tasks = new List<object>();
            var past = new List<QueueTask>();
            foreach (var t in d.Tasks) if (QState.Finished(t.State)) past.Add(t); else tasks.Add(QueueRow(t, d));
            past.Sort((a, b) => b.FinishedUtc.CompareTo(a.FinishedUtc));
            for (int i = 0; i < past.Count && i < QueuePastMax; i++) tasks.Add(QueueRow(past[i], d));
            // The laptop's time zone (its name and current UTC offset), so the phone can say when its own clock differs.
            var tz = TimeZoneInfo.Local;
            DateTime nowLocal = DateTime.Now;
            var q = new Dictionary<string, object> { { "paused", d.Paused }, { "warn", warning ?? "" }, { "tasks", tasks }, { "can", Queue != null },
                { "tz", tz.IsDaylightSavingTime(nowLocal) ? tz.DaylightName : tz.StandardName }, { "tzo", (int)tz.GetUtcOffset(nowLocal).TotalMinutes } };
            string sig = new JavaScriptSerializer { MaxJsonLength = int.MaxValue }.Serialize(q);
            var projects = new List<string>();
            foreach (var t in d.Tasks) if (!string.IsNullOrEmpty(t.Project) && !projects.Exists(p => Sched.SamePath(p, t.Project))) projects.Add(t.Project);
            lock (gate)
            {
                queueProjects = projects;
                if (sig == queueSig) return;
                queueData = q; queueSig = sig;
                if (!published) return;
                version++;
                Monitor.PulseAll(gate);
            }
        }

        Dictionary<string, object> QueueRow(QueueTask t, QueueData d)
        {
            string kind = QLook.Kind(t), prompt = t.Prompt ?? "", project = "";
            try { project = Board.FriendlyName(Path.GetFileName((t.Project ?? "").TrimEnd('\\', '/')), Aliases); } catch { }
            var a = t.Attempts.Count > 0 ? t.Attempts[t.Attempts.Count - 1] : null;
            var pre = t.Schedule == "after" ? d.Find(t.After) : null;
            return new Dictionary<string, object>
            {
                { "k", Token("q|" + t.Id) }, { "ti", t.Title }, { "pj", project },
                { "p", prompt.Length > QueuePromptMax ? prompt.Substring(0, QueuePromptMax) : prompt }, { "pc", prompt.Length > QueuePromptMax },
                { "kd", kind }, { "lb", QLook.Label(kind) }, { "sc", t.Schedule },
                { "at", MsOr0(QueueView.NextTime(t)) }, { "ra", MsOr0(t.RunAtUtc) }, { "cr", MsOr0(t.CreatedUtc) },
                { "sa", a == null ? 0 : MsOr0(a.StartedUtc) }, { "fi", MsOr0(t.FinishedUtc) },
                { "why", t.Reason ?? "" }, { "sum", t.Summary ?? "" }, { "n", t.Attempts.Count },
                { "rn", t.Runner }, { "md", t.Model ?? "" }, { "pm", t.PermissionMode }, { "af", pre == null ? "" : pre.Title }, { "im", t.IfMissed },
                // for editing from the phone: the project's token, the revision the edit is based on, whether it may be
                // edited / canceled there (not started; full prompt known), and the run time on the laptop's own clock
                { "pk", ProjectToken(t.Project) }, { "rv", t.Revision }, { "lt", t.Schedule == "at" ? t.RunAtLocal : "" },
                { "ed", (QState.Pending(t.State) || t.State == QState.Draft) && prompt.Length <= QueuePromptMax },
                { "cx", QState.Pending(t.State) || t.State == QState.Draft || t.State == QState.NeedsAttention },
                { "cp", t.AllowCommit || t.AllowPush || t.AllowDelete },
            };
        }

        string ProjectToken(string path) { return string.IsNullOrEmpty(path) ? "" : Token("p|" + Sched.NormPath(path).Normalize(NormalizationForm.FormC).ToLowerInvariant()); }

        static long MsOr0(DateTime utc) { return utc == DateTime.MinValue ? 0 : Ms(utc.Kind == DateTimeKind.Local ? utc.ToUniversalTime() : utc); }

        // Each worker keeps its creature for as long as this process runs (also after a dismissal, so a worker
        // that comes back looks the same). A new worker starts from a look picked by its token and takes the
        // first one whose hat no worker on the board is wearing, so up to 13 workers all wear different hats.
        // Orange leads the palette: a new worker gets an orange body while the board has at most twice as
        // many orange creatures as others (so the first is orange, then about two in three).
        void AssignLooks(List<SessionInfo> ordered)
        {
            var hats = new bool[Hats];
            var taken = new HashSet<int>();
            int orange = 0, other = 0;
            foreach (var s in ordered)
            {
                int l;
                if (looks.TryGetValue(s.Id, out l)) { hats[l % Hats] = true; taken.Add(l); if (IsOrange(l)) orange++; else other++; }
            }
            foreach (var s in ordered)
            {
                if (looks.ContainsKey(s.Id)) continue;
                if (looks.Count > 500)
                {
                    var present = new HashSet<string>();
                    foreach (var o in ordered) present.Add(o.Id);
                    foreach (var id in new List<string>(looks.Keys)) if (!present.Contains(id)) looks.Remove(id);
                }
                byte[] h;
                using (var mac = new HMACSHA256(secret)) h = mac.ComputeHash(Encoding.UTF8.GetBytes("c|" + s.Id));
                int start = (int)(BitConverter.ToUInt32(h, 0) % Looks), pick = -1;
                bool wantOrange = orange <= 2 * other;
                for (int i = 0; i < Looks && pick < 0; i++) { int l = (start + i) % Looks; if (IsOrange(l) == wantOrange && !hats[l % Hats] && !taken.Contains(l)) pick = l; }
                for (int i = 0; i < Looks && pick < 0; i++) { int l = (start + i) % Looks; if (!hats[l % Hats] && !taken.Contains(l)) pick = l; }
                for (int i = 0; i < Looks && pick < 0; i++) { int l = (start + i) % Looks; if (!taken.Contains(l)) pick = l; }
                if (pick < 0) pick = start;
                looks[s.Id] = pick; hats[pick % Hats] = true; taken.Add(pick);
                if (IsOrange(pick)) orange++; else other++;
            }
        }

        // That worker's creature, as the board shows it; -1 if it has none yet. UI thread only.
        public int LookOf(string sessionId) { int l; return looks.TryGetValue(sessionId, out l) ? l : -1; }

        // The board token of that worker's row (what a tapped notification opens).
        public string KeyFor(string sessionId) { return Token("k|" + sessionId); }

        string Token(string value)
        {
            using (var h = new HMACSHA256(secret))
                return Convert.ToBase64String(h.ComputeHash(Encoding.UTF8.GetBytes(value)), 0, 16).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        }

        static long Ms(DateTime utc) { return (long)(utc - Epoch).TotalMilliseconds; }

        // ---------- HTTP ----------
        void AcceptLoop()
        {
            while (running)
            {
                TcpClient c;
                try { c = listener.AcceptTcpClient(); NotInherited(c.Client); }
                catch { if (!running) return; Thread.Sleep(100); continue; }
                if (Interlocked.Increment(ref active) > MaxConnections)
                {
                    Interlocked.Decrement(ref active);
                    try { using (c) Send(c.GetStream(), 503, "text/plain; charset=utf-8", Encoding.UTF8.GetBytes("Busy"), false); } catch { }
                    continue;
                }
                try { new Thread(() => { try { Serve(c); } catch { } finally { Interlocked.Decrement(ref active); try { c.Close(); } catch { } } }) { IsBackground = true }.Start(); }
                catch { Interlocked.Decrement(ref active); try { c.Close(); } catch { } }
            }
        }

        class Request
        {
            public string Method = "", Path = "", Query = "";
            public Dictionary<string, string> Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            public byte[] Body = new byte[0];
        }

        void Serve(TcpClient c)
        {
            c.ReceiveTimeout = 10000; c.SendTimeout = 10000;
            var ns = c.GetStream();
            int status;
            var req = Read(ns, out status);
            if (req == null) { Text(ns, status, status == 413 ? "Too large" : "Bad request"); return; }

            string host;
            if (!req.Headers.TryGetValue("Host", out host) || !HostAllowed(host, true)) { Text(ns, 421, "Unknown host"); return; }

            if (req.Method == "GET")
            {
                string[] page;
                if (Pages.TryGetValue(req.Path, out page))
                {
                    byte[] body;
                    if (files.TryGetValue(page[0], out body)) Send(ns, 200, page[1], body, page[0] == "phone.html");
                    else Text(ns, 500, "The phone page files are missing. Re-run install.ps1.");
                    return;
                }
                if (req.Path == "/api/board") { BoardRequest(ns, req); return; }
                if (req.Path == "/api/git") { GitGet(ns, req); return; }
                if (req.Path == "/api/queue") { QueueGet(ns, req); return; }
                Text(ns, 404, "Not found");
                return;
            }
            if (req.Method == "POST" && req.Path == "/api/git") { GitPost(ns, req); return; }
            if (req.Method == "POST" && req.Path == "/api/queue") { QueuePost(ns, req); return; }
            if (req.Method == "POST" && req.Path == "/api/dismiss") { DismissRequest(ns, req); return; }
            if (req.Method == "POST" && req.Path == "/api/focus") { FocusRequest(ns, req); return; }
            if (req.Method == "POST" && req.Path == "/api/usage-refresh") { UsageRefreshRequest(ns, req); return; }
            if (req.Method == "POST" && req.Path == "/api/push") { PushRequest(ns, req); return; }
            if (req.Path == "/api/dismiss" || req.Path == "/api/focus" || req.Path == "/api/usage-refresh" || req.Path == "/api/push" || req.Path == "/api/git" || req.Path == "/api/queue" || Pages.ContainsKey(req.Path) || req.Path == "/api/board") { Text(ns, 405, "Method not allowed"); return; }
            Text(ns, 404, "Not found");
        }

        static Request Read(NetworkStream ns, out int status)
        {
            status = 400;
            var buf = new byte[MaxHeader];
            int n = 0, end = -1;
            while (end < 0)
            {
                if (n == buf.Length) { status = 431; return null; }
                int r = ns.Read(buf, n, buf.Length - n);
                if (r <= 0) return null;
                n += r;
                for (int i = Math.Max(0, n - r - 3); i + 3 < n; i++)
                    if (buf[i] == 13 && buf[i + 1] == 10 && buf[i + 2] == 13 && buf[i + 3] == 10) { end = i; break; }
            }
            string head = Encoding.ASCII.GetString(buf, 0, end);
            var lines = head.Split(new[] { "\r\n" }, StringSplitOptions.None);
            var first = lines[0].Split(' ');
            if (first.Length != 3 || !first[2].StartsWith("HTTP/1.")) return null;
            var req = new Request { Method = first[0] };
            string target = first[1];
            if (!target.StartsWith("/")) return null;
            int q = target.IndexOf('?');
            req.Path = q >= 0 ? target.Substring(0, q) : target;
            req.Query = q >= 0 ? target.Substring(q + 1) : "";
            for (int i = 1; i < lines.Length; i++)
            {
                int colon = lines[i].IndexOf(':');
                if (colon <= 0) return null;
                string k = lines[i].Substring(0, colon).Trim(), v = lines[i].Substring(colon + 1).Trim();
                if (req.Headers.ContainsKey(k)) { if (k.Equals("Host", StringComparison.OrdinalIgnoreCase) || k.Equals("Content-Length", StringComparison.OrdinalIgnoreCase)) return null; continue; }
                req.Headers[k] = v;
            }
            if (req.Headers.ContainsKey("Transfer-Encoding")) { status = 411; return null; }
            string cl;
            int len = 0;
            if (req.Headers.TryGetValue("Content-Length", out cl) && (!int.TryParse(cl, out len) || len < 0)) return null;
            if (len > (req.Path == "/api/git" || req.Path == "/api/queue" ? MaxGitBody : MaxBody)) { status = 413; return null; }   // a commit / a task carries its message / prompt
            var body = new byte[len];
            int have = Math.Min(len, n - end - 4);
            Array.Copy(buf, end + 4, body, 0, have);
            while (have < len) { int r = ns.Read(body, have, len - have); if (r <= 0) return null; have += r; }
            req.Body = body;
            return req;
        }

        void BoardRequest(NetworkStream ns, Request req)
        {
            long want = -1; bool wait = false;
            foreach (var part in req.Query.Split('&'))
            {
                if (part.StartsWith("v=")) long.TryParse(part.Substring(2), out want);
                else if (part == "wait=1") wait = true;
            }
            string json;
            lock (gate)
            {
                if (!published) { json = null; }
                else
                {
                    if (wait)
                    {
                        var deadline = DateTime.UtcNow.AddMilliseconds(WaitMs);
                        while (running && version == want)
                        {
                            int left = (int)(deadline - DateTime.UtcNow).TotalMilliseconds;
                            if (left <= 0) break;
                            Monitor.Wait(gate, left);
                        }
                    }
                    json = BoardJson();
                }
            }
            if (json == null) { Json(ns, 503, "{\"error\":\"starting\"}"); return; }
            Json(ns, 200, json);
        }

        string BoardJson()
        {
            var list = new List<object>();
            foreach (var r in rows)
                list.Add(new Dictionary<string, object> { { "k", r.Key }, { "n", r.Name }, { "s", r.State }, { "t", r.Since }, { "e", r.Err }, { "d", r.Occ }, { "c", r.Look }, { "x", r.Ctx } });
            var o = new Dictionary<string, object> { { "v", version }, { "now", Ms(DateTime.UtcNow) }, { "summary", summaryOn }, { "text", summaryOn ? summaryText : "" }, { "rows", list }, { "usage", usageData }, { "queue", queueData } };
            return new JavaScriptSerializer { MaxJsonLength = int.MaxValue }.Serialize(o);
        }

        // Cross-site protection for the action endpoints: a page on another site can't send these (JSON + a
        // custom header force a CORS preflight, which this server never approves), and Origin / Fetch metadata
        // must name this dashboard. The header names the one action, so neither endpoint accepts the other's.
        bool ActionAllowed(NetworkStream ns, Request req, string action)
        {
            string origin, site, ctype, marker;
            if (!req.Headers.TryGetValue("Origin", out origin) || !OriginAllowed(origin)) { Json(ns, 403, "{\"error\":\"forbidden\"}"); return false; }
            if (req.Headers.TryGetValue("Sec-Fetch-Site", out site) && site != "same-origin") { Json(ns, 403, "{\"error\":\"forbidden\"}"); return false; }
            if (!req.Headers.TryGetValue("X-Claude-Status", out marker) || marker != action) { Json(ns, 403, "{\"error\":\"forbidden\"}"); return false; }
            if (!req.Headers.TryGetValue("Content-Type", out ctype) || !ctype.StartsWith("application/json", StringComparison.OrdinalIgnoreCase)) { Json(ns, 415, "{\"error\":\"json required\"}"); return false; }
            return true;
        }

        // The named fields of a small JSON body, or null if any is missing, not a string, or not token-shaped.
        static string[] Tokens(Request req, params string[] names)
        {
            try
            {
                var d = new JavaScriptSerializer { MaxJsonLength = MaxBody, RecursionLimit = 4 }.Deserialize<Dictionary<string, object>>(Encoding.UTF8.GetString(req.Body));
                if (d == null) return null;
                var vals = new string[names.Length];
                for (int i = 0; i < names.Length; i++)
                {
                    object v;
                    vals[i] = d.TryGetValue(names[i], out v) ? v as string : null;
                    if (vals[i] == null || !TokenRx.IsMatch(vals[i])) return null;
                }
                return vals;
            }
            catch { return null; }
        }

        PhoneRow FindRow(string key)
        {
            lock (gate) foreach (var r in rows) if (r.Key == key) return r;
            return null;
        }

        void DismissRequest(NetworkStream ns, Request req)
        {
            if (!ActionAllowed(ns, req, "dismiss")) return;
            var t = Tokens(req, "k", "d");
            if (t == null) { Json(ns, 400, "{\"error\":\"bad request\"}"); return; }
            string key = t[0], occ = t[1];

            PhoneRow row = FindRow(key);
            DismissResult result;
            if (row == null) result = DismissResult.Gone;                  // already dismissed (or ended): nothing to do
            else if (row.Occ == "") result = DismissResult.NotDone;        // WORKING / NEEDS YOU / ERROR can't be dismissed
            else if (row.Occ != occ) result = DismissResult.Changed;       // the phone saw an older DONE
            else
            {
                try { result = dismiss(row.SessionId, row.Occurrence); }  // re-validated against the file itself
                catch { result = DismissResult.Changed; }
            }
            int code = result == DismissResult.Dismissed || result == DismissResult.Gone ? 200 : 409;
            Json(ns, code, "{\"result\":\"" + (result == DismissResult.Dismissed ? "dismissed" : result == DismissResult.Gone ? "gone" : result == DismissResult.NotDone ? "not_done" : "changed") + "\"}");
        }

        // Brings up the worker the phone tapped, named by its board token only: the laptop looks the worker up
        // and picks its window exactly as for a click on the desktop board. One at a time; a repeat of the
        // same tap within RepeatMs gets the previous answer instead of a second activation.
        void FocusRequest(NetworkStream ns, Request req)
        {
            if (!ActionAllowed(ns, req, "focus")) return;
            var t = Tokens(req, "k");
            if (t == null) { Json(ns, 400, "{\"error\":\"bad request\"}"); return; }
            string key = t[0];
            FocusResult result = FocusResult.Unavailable;
            bool run = false;
            lock (focusGate)
            {
                if (focusBusy) result = FocusResult.Busy;
                else if (key == lastFocusKey && (DateTime.UtcNow - lastFocusUtc).TotalMilliseconds < RepeatMs) result = lastFocus;
                else { focusBusy = true; run = true; }
            }
            if (run)
            {
                try
                {
                    PhoneRow row = FindRow(key);
                    result = row == null ? FocusResult.Gone : focus == null ? FocusResult.Unavailable : focus(row.SessionId);
                }
                catch { result = FocusResult.Unavailable; }
                finally { lock (focusGate) { focusBusy = false; lastFocusKey = key; lastFocusUtc = DateTime.UtcNow; lastFocus = result; } }
            }
            int code = result == FocusResult.Focused ? 200 : result == FocusResult.Busy ? 429 : result == FocusResult.Unavailable ? 503 : 409;
            Json(ns, code, "{\"result\":\"" + FocusName(result) + "\"}");
        }

        // Re-reads local files on the laptop (no Claude request, nothing to rate-limit upstream); still at
        // most once every 2 seconds. The new figures arrive through the board's long-poll.
        void UsageRefreshRequest(NetworkStream ns, Request req)
        {
            if (!ActionAllowed(ns, req, "usage-refresh")) return;
            bool run;
            lock (focusGate)
            {
                run = (DateTime.UtcNow - lastUsageRefreshUtc).TotalMilliseconds >= 2000;
                if (run) lastUsageRefreshUtc = DateTime.UtcNow;
            }
            if (!run) { Json(ns, 429, "{\"result\":\"busy\"}"); return; }
            bool ok;
            try { ok = UsageRefresh != null && UsageRefresh(); } catch { ok = false; }
            Json(ns, ok ? 200 : 503, ok ? "{\"result\":\"refreshing\"}" : "{\"result\":\"unavailable\"}");
        }

        // This phone's notification setup. A phone names itself by its own push endpoint (an unguessable URL
        // only it and this laptop know). Nothing here takes notification text; the only thing it sends is the
        // fixed test message to that same phone (push.cs allows one every few seconds).
        void PushRequest(NetworkStream ns, Request req)
        {
            if (!ActionAllowed(ns, req, "push")) return;
            var push = Push;
            if (push == null) { Json(ns, 503, "{\"result\":\"unavailable\"}"); return; }
            Dictionary<string, object> d = null;
            try { d = new JavaScriptSerializer { MaxJsonLength = MaxBody, RecursionLimit = 4 }.Deserialize<Dictionary<string, object>>(Encoding.UTF8.GetString(req.Body)); } catch { }
            object o;
            string op = d != null && d.TryGetValue("op", out o) ? o as string : null;
            string endpoint = d != null && d.TryGetValue("endpoint", out o) ? o as string : null;
            bool needs = d != null && d.TryGetValue("needs", out o) && o is bool && (bool)o;
            bool done = d != null && d.TryGetValue("done", out o) && o is bool && (bool)o;
            var ser = new JavaScriptSerializer();
            if (op == "key") { Json(ns, 200, ser.Serialize(new Dictionary<string, object> { { "key", push.PublicKey } })); return; }
            if (!PushNotifier.ValidEndpoint(endpoint)) { Json(ns, 400, "{\"result\":\"bad_request\"}"); return; }
            switch (op)
            {
                case "status":
                    {
                        var s = push.Find(endpoint);
                        Json(ns, 200, ser.Serialize(s == null ? new Dictionary<string, object> { { "known", false } }
                            : new Dictionary<string, object> { { "known", true }, { "needs", s.Needs }, { "done", s.Done }, { "last", s.Last } }));
                        return;
                    }
                case "subscribe":
                    {
                        string p256dh = d.TryGetValue("p256dh", out o) ? o as string : null, auth = d.TryGetValue("auth", out o) ? o as string : null;
                        string err = push.Subscribe(endpoint, p256dh, auth, needs, done);
                        if (err != null) { Json(ns, err == "full" ? 409 : 400, ser.Serialize(new Dictionary<string, object> { { "result", err } })); return; }
                        int code = push.Test(endpoint);
                        Json(ns, 200, ser.Serialize(new Dictionary<string, object> { { "result", "subscribed" }, { "test", TestName(code) }, { "code", code }, { "last", LastOf(push, endpoint) } }));
                        return;
                    }
                case "prefs":
                    if (push.SetPrefs(endpoint, needs, done)) Json(ns, 200, "{\"result\":\"saved\"}");
                    else Json(ns, 404, "{\"result\":\"unknown\"}");
                    return;
                case "test":
                    {
                        int code = push.Test(endpoint);
                        Json(ns, code == PushNotifier.Unknown ? 404 : code == PushNotifier.TooSoon ? 429 : 200,
                            ser.Serialize(new Dictionary<string, object> { { "result", TestName(code) }, { "code", code }, { "last", LastOf(push, endpoint) } }));
                        return;
                    }
                case "unsubscribe":
                    push.Unsubscribe(endpoint);
                    Json(ns, 200, "{\"result\":\"removed\"}");
                    return;
            }
            Json(ns, 400, "{\"result\":\"bad_request\"}");
        }

        static string LastOf(PushNotifier push, string endpoint) { var s = push.Find(endpoint); return s == null ? "" : s.Last; }

        // "accepted" = the push service took the message; it says nothing about whether the phone showed it.
        static string TestName(int code)
        {
            if (code == PushNotifier.Unknown) return "unknown";
            if (code == PushNotifier.TooSoon) return "too_soon";
            if (code == 0) return "unreachable";
            if (code >= 200 && code < 300) return "accepted";
            if (code == 404 || code == 410) return "expired";
            return "rejected";
        }

        static string FocusName(FocusResult r)
        {
            switch (r)
            {
                case FocusResult.Focused: return "focused"; case FocusResult.Refused: return "refused"; case FocusResult.Locked: return "locked";
                case FocusResult.NotYet: return "not_yet"; case FocusResult.Ended: return "ended"; case FocusResult.Unknown: return "unknown";
                case FocusResult.Ambiguous: return "ambiguous"; case FocusResult.Gone: return "gone"; case FocusResult.Busy: return "busy";
                default: return "unavailable";
            }
        }

        // ---------- Git (git.cs) ----------
        // Looking (GET) never changes anything; it still needs the page's own header (a cross-site page can't
        // send one without a preflight this server never approves), since it shows paths and diffs.
        void GitGet(NetworkStream ns, Request req)
        {
            string marker, site;
            if (!req.Headers.TryGetValue("X-Claude-Status", out marker) || marker != "git" || (req.Headers.TryGetValue("Sec-Fetch-Site", out site) && site != "same-origin")) { Json(ns, 403, "{\"error\":\"forbidden\"}"); return; }
            var q = new Dictionary<string, string>();
            foreach (var part in req.Query.Split('&'))
            {
                int eq = part.IndexOf('=');
                if (eq > 0) try { q[part.Substring(0, eq)] = Uri.UnescapeDataString(part.Substring(eq + 1).Replace('+', ' ')); } catch { }
            }
            string op, r, id, p;
            q.TryGetValue("op", out op); q.TryGetValue("r", out r); q.TryGetValue("id", out id); q.TryGetValue("p", out p);
            Dictionary<string, object> res;
            try
            {
                if (op == "repos") res = GitOps.Repos(q.ContainsKey("fresh"));
                else if (op == "review" && r != null && TokenRx.IsMatch(r)) res = GitOps.Review(r);
                else if (op == "diff" && r != null && TokenRx.IsMatch(r) && !string.IsNullOrEmpty(p) && p.Length <= 4096) res = GitOps.Diff(r, p);
                else if (op == "job" && id != null && TokenRx.IsMatch(id)) res = GitOps.Job(id);
                else { Json(ns, 400, "{\"result\":\"bad_request\"}"); return; }
            }
            catch { Json(ns, 500, "{\"result\":\"error\"}"); return; }
            Json(ns, 200, new JavaScriptSerializer { MaxJsonLength = int.MaxValue }.Serialize(res));
        }

        // The only Git actions: Send (commit the reviewed selection, then push) and Retry Push.
        void GitPost(NetworkStream ns, Request req)
        {
            if (!ActionAllowed(ns, req, "git")) return;
            Dictionary<string, object> d = null;
            try { d = new JavaScriptSerializer { MaxJsonLength = MaxGitBody, RecursionLimit = 6 }.Deserialize<Dictionary<string, object>>(Encoding.UTF8.GetString(req.Body)); } catch { }
            object o;
            string op = d != null && d.TryGetValue("op", out o) ? o as string : null;
            string id = d != null && d.TryGetValue("id", out o) ? o as string : null;
            if (op == null || id == null || !TokenRx.IsMatch(id)) { Json(ns, 400, "{\"result\":\"bad_request\"}"); return; }
            Dictionary<string, object> res = null;
            if (op == "push")
            {
                if (d.TryGetValue("n", out o) && o is int) res = GitOps.RetryPush(id, (int)o);
            }
            else if (op == "commit")
            {
                string r = d.TryGetValue("r", out o) ? o as string : null, fp = d.TryGetValue("fp", out o) ? o as string : null, msg = d.TryGetValue("msg", out o) ? o as string : null;
                var picked = new List<KeyValuePair<string, string>>();
                var cmds = new List<string[]>();
                bool ok = r != null && TokenRx.IsMatch(r) && fp != null && msg != null;
                if (ok && d.TryGetValue("files", out o) && o is System.Collections.IList)
                    foreach (var f in (System.Collections.IList)o)
                    {
                        var fd = f as Dictionary<string, object>;
                        object pv, hv;
                        if (fd == null || !fd.TryGetValue("p", out pv) || !fd.TryGetValue("h", out hv) || !(pv is string) || !(hv is string)) { ok = false; break; }
                        picked.Add(new KeyValuePair<string, string>((string)pv, (string)hv));
                    }
                else ok = false;
                if (ok && d.TryGetValue("cmds", out o) && o is System.Collections.IList)
                    foreach (var c in (System.Collections.IList)o)
                    {
                        var arr = c as System.Collections.IList;
                        if (arr == null) { ok = false; break; }
                        var args = new string[arr.Count];
                        for (int i = 0; i < arr.Count; i++) { args[i] = arr[i] as string; if (args[i] == null) ok = false; }
                        cmds.Add(args);
                    }
                else ok = false;
                if (ok) res = GitOps.Commit(id, r, fp, picked, msg, cmds);
            }
            if (res == null) { Json(ns, 400, "{\"result\":\"bad_request\"}"); return; }
            Json(ns, 200, new JavaScriptSerializer { MaxJsonLength = int.MaxValue }.Serialize(res));
        }

        // ---------- scheduling from the phone (the desktop's QueueEngine; scheduler.cs) ----------
        // The projects a task can run in: the scheduler's own projects plus the folders Claude Code worked in most
        // recently (its transcripts' "cwd"), existing folders only. The phone gets friendly names and tokens.
        public const int MaxProjects = 30;
        List<string[]> KnownProjects(bool fresh)
        {
            lock (opsGate) if (!fresh && projCache != null && (DateTime.UtcNow - projAt).TotalSeconds < 30) return projCache;
            var paths = new List<string>();
            lock (gate) paths.AddRange(queueProjects);
            foreach (var p in RecentCwds(ClaudeProjectsDir, MaxProjects)) if (!paths.Exists(x => Sched.SamePath(x, p))) paths.Add(p);
            var list = new List<string[]>();
            var aliases = Aliases;
            foreach (var p in paths)
            {
                if (list.Count >= MaxProjects) break;
                bool ok = false;
                try { ok = Path.IsPathRooted(p) && Directory.Exists(p); } catch { }
                if (!ok) continue;
                string folder = Path.GetFileName(Sched.NormPath(p));
                list.Add(new[] { ProjectToken(p), Sched.NormPath(p), Board.FriendlyName(folder, aliases) });
            }
            // Two folders with one friendly name: tell them apart by the folder above.
            foreach (var a in list)
                if (list.FindAll(b => b[2] == a[2]).Count > 1)
                    try { a[2] += " (" + Path.GetFileName(Path.GetDirectoryName(a[1])) + ")"; } catch { }
            lock (opsGate) { projCache = list; projAt = DateTime.UtcNow; }
            return list;
        }

        // The working folders of the most recently used Claude Code conversations (one per transcript folder).
        static List<string> RecentCwds(string projectsDir, int max)
        {
            var newest = new List<FileInfo>();
            try
            {
                foreach (var d in new DirectoryInfo(projectsDir).GetDirectories())
                {
                    FileInfo n = null;
                    try { foreach (var f in d.GetFiles("*.jsonl")) if (n == null || f.LastWriteTimeUtc > n.LastWriteTimeUtc) n = f; } catch { }
                    if (n != null) newest.Add(n);
                }
            }
            catch { }
            newest.Sort((a, b) => b.LastWriteTimeUtc.CompareTo(a.LastWriteTimeUtc));
            var list = new List<string>();
            foreach (var f in newest)
            {
                if (list.Count >= max) break;
                string cwd = FirstCwd(f.FullName);
                if (cwd != null && !list.Exists(x => Sched.SamePath(x, cwd))) list.Add(cwd);
            }
            return list;
        }

        static readonly Regex CwdRx = new Regex("\"cwd\"\\s*:\\s*(\"(?:[^\"\\\\]|\\\\.)*\")");
        static string FirstCwd(string file)
        {
            try
            {
                var buf = new byte[64 * 1024];
                int n;
                using (var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete)) n = fs.Read(buf, 0, buf.Length);
                var m = CwdRx.Match(Encoding.UTF8.GetString(buf, 0, n));
                return m.Success ? new JavaScriptSerializer().Deserialize<string>(m.Groups[1].Value) : null;
            }
            catch { return null; }
        }

        // Looking needs the page's own header (like the Git view), though it only shows names.
        void QueueGet(NetworkStream ns, Request req)
        {
            string marker, site;
            if (!req.Headers.TryGetValue("X-Claude-Status", out marker) || marker != "queue" || (req.Headers.TryGetValue("Sec-Fetch-Site", out site) && site != "same-origin")) { Json(ns, 403, "{\"error\":\"forbidden\"}"); return; }
            if (!req.Query.StartsWith("op=projects")) { Json(ns, 400, "{\"result\":\"bad_request\"}"); return; }
            if (Queue == null) { Json(ns, 503, "{\"result\":\"unavailable\"}"); return; }
            var list = new List<object>();
            foreach (var p in KnownProjects(req.Query.Contains("fresh=1")))
                list.Add(new Dictionary<string, object> { { "k", p[0] }, { "n", p[2] }, { "f", Path.GetFileName(p[1]) } });
            Json(ns, 200, new JavaScriptSerializer().Serialize(new Dictionary<string, object> { { "projects", list } }));
        }

        // One request = one change. Each carries a one-time id from the page; a resend of the same request (a double
        // tap, a retry after a dropped connection or a reload) gets the first answer instead of a second change. A new
        // task's id comes from that request id, so even after a laptop restart a resend finds the task it already saved.
        void QueuePost(NetworkStream ns, Request req)
        {
            if (!ActionAllowed(ns, req, "queue")) return;
            Dictionary<string, object> d = null;
            try { d = new JavaScriptSerializer { MaxJsonLength = MaxGitBody, RecursionLimit = 4 }.Deserialize<Dictionary<string, object>>(Encoding.UTF8.GetString(req.Body)); } catch { }
            string op = Str(d, "op"), id = Str(d, "id");
            if (id == null || !TokenRx.IsMatch(id) || (op != "save" && op != "cancel" && op != "stop")) { Json(ns, 400, "{\"result\":\"bad_request\",\"msg\":\"The laptop didn't understand that request.\"}"); return; }
            if (Queue == null) { Json(ns, 503, "{\"result\":\"unavailable\",\"msg\":\"Scheduling isn't available on the laptop.\"}"); return; }
            string key;
            using (var sha = SHA256.Create()) key = id + "|" + Convert.ToBase64String(sha.ComputeHash(req.Body));
            lock (opsGate)
            {
                Dictionary<string, object> prev;
                if (queueOps.TryGetValue(key, out prev)) { Reply(ns, prev ?? Fail("pending", "The laptop is still saving it…")); return; }
                queueOps[key] = null;
                queueOpOrder.Enqueue(key);
                while (queueOpOrder.Count > 300) queueOps.Remove(queueOpOrder.Dequeue());
            }
            Func<object> work = () =>
            {
                Dictionary<string, object> r;
                try { r = QueueAction(op, id, d); }
                catch (Exception e) { r = Fail("error", "The laptop couldn't do that: " + e.Message); }
                // Only done changes are remembered; a refusal can be fixed and sent again.
                lock (opsGate) { if (r.ContainsKey("task") && Str(r, "result") != null && !r.ContainsKey("msg")) queueOps[key] = r; else queueOps.Remove(key); }
                return r;
            };
            object res;
            try { res = OnUi != null ? OnUi(work) : work(); }
            catch { lock (opsGate) queueOps.Remove(key); Json(ns, 503, "{\"result\":\"unavailable\",\"msg\":\"Claude Status on the laptop isn't ready.\"}"); return; }
            // Not answered in time: it may still happen, so the slot stays and a resend asks again.
            Reply(ns, res as Dictionary<string, object> ?? Fail("pending", "The laptop is busy. Checking again…"));
        }

        void Reply(NetworkStream ns, Dictionary<string, object> r)
        {
            string result = Str(r, "result");
            int code = !r.ContainsKey("msg") ? 200 : result == "pending" ? 503 : result == "error" ? 500 : result == "bad_request" ? 400 : 409;
            Json(ns, code, new JavaScriptSerializer { MaxJsonLength = int.MaxValue }.Serialize(r));
        }

        static string Str(Dictionary<string, object> d, string k) { object v; return d != null && d.TryGetValue(k, out v) ? v as string : null; }
        static Dictionary<string, object> Fail(string result, string msg) { return new Dictionary<string, object> { { "result", result }, { "msg", msg } }; }

        Dictionary<string, object> Done(string result, string taskId, bool created)
        {
            var eng = Queue;
            var t = eng.Data.Find(taskId);
            return new Dictionary<string, object> { { "result", result }, { "created", created }, { "task", t == null ? null : QueueRow(t, eng.Data) } };
        }

        public static readonly string[] PhoneModes = { "acceptEdits", "dontAsk", "plan" };

        // Runs on the indicator's UI thread, like every other scheduler action. The engine re-checks state inside its
        // own locked write (Save's revision check, CancelPending), so a task that started meanwhile is never changed.
        Dictionary<string, object> QueueAction(string op, string opId, Dictionary<string, object> d)
        {
            var eng = Queue;
            string k = Str(d, "k");
            QueueTask cur = null;
            if (k != null)
            {
                foreach (var t in eng.Data.Tasks) if (Token("q|" + t.Id) == k) cur = t;
                if (cur == null) return Fail("gone", "This task is no longer on the laptop's schedule. It may have been removed there.");
            }
            else if (op != "save") return Fail("bad_request", "The laptop didn't understand that request.");

            if (op == "cancel")
            {
                if (cur.State == QState.Canceled) return Done("canceled", cur.Id, false);
                if (cur.State == QState.Running) return Fail("running", "It has already started, so it wasn't canceled. Use Stop run to stop it.");
                if (QState.Finished(cur.State)) return Fail("finished", "It has already finished (" + QState.Label(cur.State) + ").");
                return eng.CancelPending(cur.Id) ? Done("canceled", cur.Id, false) : Fail("changed", "It changed on the laptop, so it wasn't canceled. Check it and try again.");
            }
            if (op == "stop")
            {
                if (cur.State != QState.Running) return Fail("not_running", QState.Finished(cur.State) ? "It isn't running any more (" + QState.Label(cur.State) + ")." : "It hasn't started. Use Cancel task instead.");
                eng.Cancel(cur.Id);
                return Done("stopped", cur.Id, false);
            }

            // ---- save: a new task, or an edit of one that hasn't started ----
            string prompt = Str(d, "p"), title = (Str(d, "ti") ?? "").Trim(), runner = Str(d, "rn") ?? "vscode", mode = Str(d, "pm") ?? "acceptEdits",
                model = (Str(d, "md") ?? "").Trim(), missed = Str(d, "im") ?? "run", ai = Str(d, "ai") ?? "claude";
            if (ai != "claude") return Fail("invalid", "Only Claude Code can run scheduled tasks on this laptop.");
            if (runner != "vscode" && runner != "headless" || Array.IndexOf(PhoneModes, mode) < 0 || (missed != "run" && missed != "skip")) return Fail("bad_request", "The laptop didn't understand that request.");
            if (prompt == null || prompt.Trim() == "") return Fail("invalid", "Write the prompt.");
            if (prompt.Length > QueuePromptMax) return Fail("invalid", "The prompt is too long to schedule from the phone (at most " + QueuePromptMax.ToString("N0") + " characters). Schedule it on the laptop.");
            if (prompt.IndexOf('\0') >= 0 || title.IndexOf('\0') >= 0) return Fail("invalid", "The prompt can't contain a NUL character.");
            if (title.Length > 200) title = title.Substring(0, 200);

            string path = null, pk = Str(d, "pj");
            if (pk != null)
                for (int pass = 0; pass < 2 && path == null; pass++)
                    foreach (var p in KnownProjects(pass == 1)) if (p[0] == pk) path = p[1];
            if (path == null) return Fail("project", "That project isn't on the laptop's list (any more). Choose the project again.");

            object atv;
            bool hasAt = d.TryGetValue("at", out atv) && atv != null && !(atv is string);
            DateTime runAt = DateTime.MinValue, now = eng.Now();
            if (hasAt)
            {
                double ms;
                try { ms = Convert.ToDouble(atv, System.Globalization.CultureInfo.InvariantCulture); } catch { return Fail("bad_request", "The laptop didn't understand that time."); }
                if (double.IsNaN(ms) || ms < 0 || ms > 32503680000000) return Fail("bad_request", "The laptop didn't understand that time.");
                runAt = Epoch.AddMilliseconds(Math.Floor(ms / 60000) * 60000);   // whole minutes, like the desktop editor
                if (runAt < now.AddMinutes(-2)) return Fail("time", "That time has already passed on the laptop (it's " + Sched.LocalText(now) + " there). Choose a later time.");
                if (runAt > now.AddDays(366)) return Fail("time", "Choose a time within the next year.");
            }

            QueueTask t2;
            int rev;
            bool created = false;
            if (cur == null)
            {
                // The task's id comes from the request id: sending this draft again updates the task it already saved.
                string tid;
                using (var sha = SHA256.Create()) tid = BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes("phone-task|" + opId))).Replace("-", "").Substring(0, 32).ToLowerInvariant();
                var existing = eng.Data.Find(tid);
                if (existing != null && !QState.Pending(existing.State) && existing.State != QState.Draft) return Done("saved", tid, false);   // already started: leave it alone
                if (!hasAt) return Fail("time", "Choose when it should run.");
                t2 = existing != null ? QueueStore.Clone(existing) : TaskEditor.NewTask();
                t2.Id = tid;
                rev = existing != null ? existing.Revision : 0;
                created = existing == null;
            }
            else
            {
                if (cur.State == QState.Running) return Fail("running", "It has already started, so it can't be edited. Stop it, or duplicate it on the laptop.");
                if (!QState.Pending(cur.State) && cur.State != QState.Draft) return Fail("finished", "It has already finished (" + QState.Label(cur.State) + "), so it can't be edited.");
                if ((cur.Prompt ?? "").Length > QueuePromptMax) return Fail("invalid", "This task's prompt is too long to edit on the phone. Edit it on the laptop.");
                object rv;
                if (!d.TryGetValue("rv", out rv) || !(rv is int) || (int)rv != cur.Revision)
                    return new Dictionary<string, object> { { "result", "changed" }, { "msg", "This task changed on the laptop after you opened it. Check the current version below, then edit again." }, { "task", QueueRow(cur, eng.Data) } };
                t2 = QueueStore.Clone(cur);
                rev = cur.Revision;
            }

            t2.Project = path;
            t2.Prompt = prompt;
            t2.Title = title != "" ? title : Sched.FirstLine(prompt, 60);
            t2.Runner = runner; t2.PermissionMode = mode; t2.Model = model; t2.IfMissed = missed;
            if (hasAt)
            {
                t2.Schedule = "at"; t2.After = ""; t2.UsageResetUtc = DateTime.MinValue;
                t2.RunAtUtc = runAt; t2.TimeZone = TimeZoneInfo.Local.Id; t2.RunAtLocal = runAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm", System.Globalization.CultureInfo.InvariantCulture);
            }
            t2.State = QState.Scheduled;
            string err = eng.Save(t2, rev);   // validated again and written (locked) before this returns
            if (err != null) return Fail("invalid", err);
            return Done("saved", t2.Id, created);
        }

        // ---------- allowed hosts / origins ----------
        // Loopback (as reached directly or as forwarded by tailscale serve) on our port, or this machine's own
        // Tailscale name / IP (any port: serve may listen on 80, 443, ...). Anything else, e.g. a DNS-rebinding
        // name pointed at 127.0.0.1, is refused.
        bool HostAllowed(string hostPort, bool refresh)
        {
            string h; int p;
            if (!SplitHost(hostPort, out h, out p)) return false;
            if (h == "127.0.0.1" || h == "localhost" || h == "::1") return p == port;
            lock (hostGate)
            {
                if (Array.IndexOf(tailnetNames, h) >= 0) return true;
                if (!refresh || (DateTime.UtcNow - tailnetCheckedUtc).TotalSeconds < 30) return false;
                tailnetCheckedUtc = DateTime.UtcNow;
                try
                {
                    var names = new List<string>();
                    foreach (var n in tailnetLookup() ?? new string[0]) if (!string.IsNullOrEmpty(n)) names.Add(n.Trim().TrimEnd('.').ToLowerInvariant());
                    tailnetNames = names.ToArray();
                }
                catch { }
                return Array.IndexOf(tailnetNames, h) >= 0;
            }
        }

        bool OriginAllowed(string origin)
        {
            Uri u;
            if (!Uri.TryCreate(origin, UriKind.Absolute, out u) || (u.Scheme != "http" && u.Scheme != "https") || u.PathAndQuery != "/") return false;
            string h = u.Host.Trim('[', ']');
            return HostAllowed((h.Contains(":") ? "[" + h + "]" : h) + ":" + u.Port, false);
        }

        internal static bool SplitHost(string hostPort, out string host, out int port)
        {
            host = ""; port = -1;
            if (string.IsNullOrEmpty(hostPort) || hostPort.Length > 255) return false;
            string s = hostPort.Trim().ToLowerInvariant();
            if (s.StartsWith("["))
            {
                int close = s.IndexOf(']');
                if (close < 0) return false;
                host = s.Substring(1, close - 1);
                s = s.Substring(close + 1);
                if (s.StartsWith(":")) { if (!int.TryParse(s.Substring(1), out port)) return false; } else if (s != "") return false;
            }
            else
            {
                int colon = s.LastIndexOf(':');
                if (colon >= 0) { if (!int.TryParse(s.Substring(colon + 1), out port)) return false; host = s.Substring(0, colon); }
                else host = s;
            }
            host = host.TrimEnd('.');
            return host != "" && Regex.IsMatch(host, "^[a-z0-9.:-]+$");
        }

        // This machine's MagicDNS name (full and short) and Tailscale IPs, from `tailscale status --json`.
        static string[] LookupTailnetNames()
        {
            string exe = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Tailscale\\tailscale.exe");
            if (!File.Exists(exe)) exe = "tailscale.exe";
            var psi = new ProcessStartInfo(exe, "status --json --peers=false") { UseShellExecute = false, RedirectStandardOutput = true, CreateNoWindow = true, StandardOutputEncoding = Encoding.UTF8 };
            using (var p = Process.Start(psi))
            {
                var read = p.StandardOutput.ReadToEndAsync();
                if (!read.Wait(4000)) { try { p.Kill(); } catch { } return new string[0]; }
                var d = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(read.Result);
                var self = d != null && d.ContainsKey("Self") ? d["Self"] as Dictionary<string, object> : null;
                var names = new List<string>();
                if (self == null) return names.ToArray();
                string dns = Board.Str(self, "DNSName").TrimEnd('.');
                if (dns != "") { names.Add(dns); names.Add(dns.Split('.')[0]); }
                object ips;
                if (self.TryGetValue("TailscaleIPs", out ips) && ips is System.Collections.IEnumerable)
                    foreach (var ip in (System.Collections.IEnumerable)ips) if (ip != null) names.Add(ip.ToString());
                return names.ToArray();
            }
        }

        // ---------- responses ----------
        static void Text(Stream s, int code, string text) { Send(s, code, "text/plain; charset=utf-8", Encoding.UTF8.GetBytes(text), false); }
        static void Json(Stream s, int code, string json) { Send(s, code, "application/json; charset=utf-8", Encoding.UTF8.GetBytes(json), false); }

        static void Send(Stream s, int code, string type, byte[] body, bool html)
        {
            var sb = new StringBuilder();
            sb.Append("HTTP/1.1 ").Append(code).Append(' ').Append(Reason(code)).Append("\r\n");
            sb.Append("Content-Type: ").Append(type).Append("\r\n");
            sb.Append("Content-Length: ").Append(body.Length).Append("\r\n");
            sb.Append("Cache-Control: no-store\r\n");
            sb.Append("X-Content-Type-Options: nosniff\r\n");
            sb.Append("Referrer-Policy: no-referrer\r\n");
            sb.Append("X-Frame-Options: DENY\r\n");
            sb.Append("Cross-Origin-Resource-Policy: same-origin\r\n");
            if (html) sb.Append("Content-Security-Policy: ").Append(Csp).Append("\r\n");
            sb.Append("Connection: close\r\n\r\n");
            var head = Encoding.ASCII.GetBytes(sb.ToString());
            s.Write(head, 0, head.Length);
            s.Write(body, 0, body.Length);
            s.Flush();
        }

        static string Reason(int code)
        {
            switch (code)
            {
                case 200: return "OK"; case 400: return "Bad Request"; case 403: return "Forbidden"; case 404: return "Not Found";
                case 405: return "Method Not Allowed"; case 409: return "Conflict"; case 411: return "Length Required"; case 413: return "Payload Too Large";
                case 429: return "Too Many Requests";
                case 415: return "Unsupported Media Type"; case 421: return "Misdirected Request"; case 431: return "Request Header Fields Too Large";
                case 500: return "Internal Server Error"; case 503: return "Service Unavailable"; default: return "Status";
            }
        }
    }
}
