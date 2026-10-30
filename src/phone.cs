// Claude Status - phone dashboard (optional; off unless "phoneDashboard": true in config.json).
// Compiled together with indicator.cs (C# 5, Windows PowerShell 5.1's Add-Type).
//
// A deliberately tiny HTTP/1.1 server bound to 127.0.0.1 only, inside the indicator process. The phone
// reaches it through `tailscale serve`, which forwards tailnet requests to this loopback port; it is never
// bound to a network interface. It serves the board the indicator itself shows (published after every
// reload, so aliases, numbering, ordering, cleanup and dismissal are the desktop's own) and nothing else:
//   GET  /  /phone.css  /phone.js        the page (fixed list; request paths never reach the file system)
//   GET  /api/board[?v=N&wait=1]          the Worker Board; with wait=1 it answers when it changes (<= 20 s)
//   POST /api/dismiss  {"k":..,"d":..}    dismiss one specific DONE occurrence
//   POST /api/focus    {"k":..}           bring that worker's window forward on the laptop (the desktop's
//                                         own click-on-a-worker action; the laptop picks the window)
//   POST /api/usage-refresh {}            re-read the Claude Usage data on the laptop now (local files)
//   GET  /sw.js                           the service worker that shows push notifications (push.cs)
//   POST /api/push {"op":..}              this phone's notifications: on/off, preferences, a test
//                                         (only the public VAPID key is ever sent to the page)
// Only display data leaves the process: friendly name, state, when that state began, a short error type,
// a creature look number, that session's context-window percentage (and whether that reading is older than
// its latest activity), and opaque tokens. No session IDs, process IDs, folder paths or window data.
// The board also carries the account-wide Claude Usage figures (titles, percentages, reset times, status
// text); never account IDs, credentials or file contents.
using System;
using System.Collections.Generic;
using System.Diagnostics;
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
        const int MaxHeader = 8192, MaxBody = 1024, MaxConnections = 32, WaitMs = 20000, RepeatMs = 1500;
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
        };
        const string Csp = "default-src 'none'; script-src 'self'; style-src 'self'; connect-src 'self'; img-src 'self'; worker-src 'self'; base-uri 'none'; form-action 'none'; frame-ancestors 'none'";

        readonly int port;
        readonly string root;
        readonly Func<string, string, DismissResult> dismiss;
        readonly Func<string, FocusResult> focus;
        readonly Func<string[]> tailnetLookup;
        readonly byte[] secret = new byte[32];
        readonly object gate = new object(), hostGate = new object(), focusGate = new object();
        readonly Dictionary<string, int> looks = new Dictionary<string, int>();   // session -> look (Publish only)
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
        DateTime lastUsageRefreshUtc = DateTime.MinValue;

        // Asks the indicator to re-read Claude Usage now (null = not available).
        public Func<bool> UsageRefresh;
        // Phone push notifications (null = not available).
        public PushNotifier Push;

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
                Text(ns, 404, "Not found");
                return;
            }
            if (req.Method == "POST" && req.Path == "/api/dismiss") { DismissRequest(ns, req); return; }
            if (req.Method == "POST" && req.Path == "/api/focus") { FocusRequest(ns, req); return; }
            if (req.Method == "POST" && req.Path == "/api/usage-refresh") { UsageRefreshRequest(ns, req); return; }
            if (req.Method == "POST" && req.Path == "/api/push") { PushRequest(ns, req); return; }
            if (req.Path == "/api/dismiss" || req.Path == "/api/focus" || req.Path == "/api/usage-refresh" || req.Path == "/api/push" || Pages.ContainsKey(req.Path) || req.Path == "/api/board") { Text(ns, 405, "Method not allowed"); return; }
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
            if (len > MaxBody) { status = 413; return null; }
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
            var o = new Dictionary<string, object> { { "v", version }, { "now", Ms(DateTime.UtcNow) }, { "summary", summaryOn }, { "text", summaryOn ? summaryText : "" }, { "rows", list }, { "usage", usageData } };
            return new JavaScriptSerializer().Serialize(o);
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
