// Claude Status - Calendar: when your AI workers were working, one block per prompt run, plus upcoming scheduled
// tasks. Compiled with indicator.cs (C# 5).
//
// Where the history comes from (all local files, nothing is sent anywhere):
//   state\activity\<UTC day>.jsonl   one line per lifecycle event. hook.ps1 writes Claude Code's (start, wait, resume,
//                                    tick, done, fail, end); CodexWatch copies Codex's own task_started /
//                                    task_complete / turn_aborted from its session logs. Every line has a unique id,
//                                    so a line written twice (a re-read, a retry) never makes a second block.
//   the Queue (tasks.json)           scheduled tasks: planned times, and its run attempts, linked to the runs above.
// PromptRuns.Fold turns events into prompt runs; Calendar.Build lays one week out. The desktop view (CalendarView,
// below) and the phone (phone.cs /api/calendar) show the same Build result.
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.AccessControl;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace ClaudeStatus
{
    public class ActEvent { public string Id = "", P = "", S = "", E = "", Pj = "", D = ""; public DateTime T; }

    public class ActWait { public DateTime From, To; public string Detail = ""; }   // To = MinValue: still waiting

    // One prompt: from the moment the worker started on it until it finished.
    public class PromptRun
    {
        public string Id = "", Provider = "claude", Session = "", Project = "", Status = "", Detail = "", Source = "hooks";
        public DateTime Start, End, LastSeen;   // End: MinValue while it runs. LastSeen: the last event seen for it.
        public bool EndKnown;                   // End is a recorded finish (or session close), not just the last thing seen
        public List<ActWait> Waits = new List<ActWait>();
        public bool WaitsKnown = true;          // false: this source can't tell waiting from working (Codex, scheduler records)
        // set when this run was a scheduled task's
        public string TaskId = "", Title = "", TaskOutcome = "";
        public DateTime PlannedUtc;

        public bool Live { get { return Status == "running" || Status == "waiting"; } }
        public DateTime Until(DateTime now) { return End != DateTime.MinValue ? End : now; }
        public TimeSpan Waited(DateTime now)
        {
            var t = TimeSpan.Zero;
            foreach (var w in Waits) { var to = w.To != DateTime.MinValue ? w.To : Until(now); if (to > w.From) t += to - w.From; }
            return t;
        }
    }

    // ---------- the event log ----------
    public class ActivityLog
    {
        public readonly string Dir;
        readonly object gate = new object();
        readonly Dictionary<string, Cached> cache = new Dictionary<string, Cached>(StringComparer.OrdinalIgnoreCase);
        class Cached { public long Len; public DateTime Written; public List<ActEvent> Events; }

        public ActivityLog(string dir) { Dir = dir; }

        public static string DayFile(DateTime utc) { return utc.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + ".jsonl"; }

        public static string Line(ActEvent e)
        {
            var d = new Dictionary<string, object> { { "id", e.Id }, { "p", e.P }, { "s", e.S }, { "e", e.E }, { "t", e.T.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture) }, { "pj", e.Pj }, { "d", e.D } };
            return new JavaScriptSerializer().Serialize(d);
        }

        public static ActEvent Parse(string line, JavaScriptSerializer ser)
        {
            try
            {
                var d = ser.Deserialize<Dictionary<string, object>>(line);
                DateTime t;
                if (d == null || !DateTime.TryParse(Board.Str(d, "t"), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out t)) return null;
                var e = new ActEvent { Id = Board.Str(d, "id"), P = Board.Str(d, "p"), S = Board.Str(d, "s"), E = Board.Str(d, "e"), Pj = Board.Str(d, "pj"), D = Board.Str(d, "d"), T = t.ToUniversalTime() };
                return e.Id == "" || e.S == "" || e.E == "" ? null : e;
            }
            catch { return null; }
        }

        // Appends with AppendData-only access, as hook.ps1 does, so writers in other processes never overwrite each other.
        public void Append(IEnumerable<ActEvent> events)
        {
            Directory.CreateDirectory(Dir);
            foreach (var g in events.GroupBy(e => DayFile(e.T)))
            {
                var sb = new StringBuilder();
                foreach (var e in g) sb.Append(Line(e)).Append('\n');
                byte[] bytes = new UTF8Encoding(false).GetBytes(sb.ToString());
                for (int i = 0; ; i++)
                {
                    try
                    {
                        using (var fs = new FileStream(Path.Combine(Dir, g.Key), FileMode.Append, FileSystemRights.AppendData, FileShare.ReadWrite, 4096, FileOptions.None))
                            fs.Write(bytes, 0, bytes.Length);
                        break;
                    }
                    catch (IOException) { if (i >= 20) throw; System.Threading.Thread.Sleep(15); }
                }
            }
        }

        // Every event from fromUtc to toUtc. A day file is parsed once and cached until it changes; a half-written
        // last line (a hook still writing it) is skipped until it's complete.
        public List<ActEvent> Read(DateTime fromUtc, DateTime toUtc)
        {
            var all = new List<ActEvent>();
            if (toUtc < fromUtc) return all;
            var ser = new JavaScriptSerializer();
            for (DateTime day = fromUtc.Date; day <= toUtc.Date; day = day.AddDays(1))
            {
                string path = Path.Combine(Dir, DayFile(day));
                List<ActEvent> evs;
                try
                {
                    var fi = new FileInfo(path);
                    if (!fi.Exists) continue;
                    lock (gate)
                    {
                        Cached c;
                        if (!cache.TryGetValue(path, out c) || c.Len != fi.Length || c.Written != fi.LastWriteTimeUtc)
                        {
                            byte[] bytes;
                            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                            { bytes = new byte[fs.Length]; int n = 0; while (n < bytes.Length) { int r = fs.Read(bytes, n, bytes.Length - n); if (r <= 0) break; n += r; } }
                            string text = Encoding.UTF8.GetString(bytes);
                            int end = text.LastIndexOf('\n');
                            var list = new List<ActEvent>();
                            if (end >= 0)
                                foreach (var line in text.Substring(0, end).Split('\n'))
                                {
                                    if (line.Trim() == "") continue;
                                    var e = Parse(line.Trim(), ser);
                                    if (e != null) list.Add(e);
                                }
                            cache[path] = c = new Cached { Len = fi.Length, Written = fi.LastWriteTimeUtc, Events = list };
                        }
                        evs = c.Events;
                    }
                }
                catch (IOException) { continue; }
                catch (UnauthorizedAccessException) { continue; }
                foreach (var e in evs) if (e.T >= fromUtc && e.T <= toUtc) all.Add(e);
            }
            return all;
        }
    }

    // ---------- events -> prompt runs (pure; tested directly) ----------
    public static class PromptRuns
    {
        // live(run): is that worker verifiably still on this prompt (desktop: its session is WORKING / NEEDS YOU and
        // its Claude process runs)? A run without a finish that isn't live ends at the last thing seen for it.
        public static List<PromptRun> Fold(IEnumerable<ActEvent> events, DateTime nowUtc, Func<PromptRun, bool> live, double staleHours)
        {
            var seen = new HashSet<string>();
            var bySession = new Dictionary<string, List<ActEvent>>();
            foreach (var e in events)
            {
                if (e == null || !seen.Add(e.P + "|" + e.Id)) continue;
                List<ActEvent> l;
                string k = e.P + "|" + e.S;
                if (!bySession.TryGetValue(k, out l)) bySession[k] = l = new List<ActEvent>();
                l.Add(e);
            }
            var runs = new List<PromptRun>();
            foreach (var group in bySession.Values)
            {
                PromptRun open = null;
                foreach (var e in group.OrderBy(x => x.T))   // stable: same-instant events keep their written order
                {
                    if (e.E == "start")
                    {
                        if (open != null)
                        {
                            // the same prompt reported twice in a row (nothing in between): one run
                            if ((e.T - open.Start).TotalSeconds < 2 && open.LastSeen == open.Start && open.Waits.Count == 0) continue;
                            Close(open, open.LastSeen, "interrupted", false);
                        }
                        open = new PromptRun { Id = e.P + ":" + e.S + ":" + e.Id, Provider = e.P, Session = e.S, Project = e.Pj, Start = e.T, LastSeen = e.T, Status = "running", WaitsKnown = e.P != "codex" };
                        runs.Add(open);
                        continue;
                    }
                    if (open == null) continue;   // a finish (or a late async event) with no prompt we saw start
                    if (e.T > open.LastSeen) open.LastSeen = e.T;
                    if (open.Project == "" && e.Pj != "") open.Project = e.Pj;
                    bool waiting = open.Waits.Count > 0 && open.Waits[open.Waits.Count - 1].To == DateTime.MinValue;
                    switch (e.E)
                    {
                        case "wait": if (!waiting) open.Waits.Add(new ActWait { From = e.T, Detail = e.D }); break;
                        case "resume": case "tick": if (waiting) open.Waits[open.Waits.Count - 1].To = e.T; break;
                        case "done": Close(open, e.T, "completed", true); open = null; break;
                        case "fail": open.Detail = e.D; Close(open, e.T, "failed", true); open = null; break;
                        case "abort": Close(open, e.T, "canceled", true); open = null; break;
                        case "end": Close(open, e.T, "interrupted", true); open = null; break;
                    }
                }
                if (open != null)
                {
                    bool stale = (nowUtc - open.LastSeen).TotalHours > staleHours;
                    if (!stale && live(open))
                        open.Status = open.Waits.Count > 0 && open.Waits[open.Waits.Count - 1].To == DateTime.MinValue ? "waiting" : "running";
                    else Close(open, open.LastSeen, "lost", false);
                }
            }
            runs.Sort((a, b) => a.Start.CompareTo(b.Start));
            return runs;
        }

        static void Close(PromptRun r, DateTime at, string status, bool known)
        {
            if (at < r.Start) at = r.Start;
            foreach (var w in r.Waits) if (w.To == DateTime.MinValue || w.To > at) w.To = at;
            r.End = at; r.Status = status; r.EndKnown = known;
            if (at > r.LastSeen) r.LastSeen = at;
        }
    }

    // ---------- Codex: its own session logs (rollout-*.jsonl), read incrementally ----------
    // Only the turn markers matter: task_started / task_complete / turn_aborted (and each later line's timestamp as
    // "still working" evidence). Lines are classified from their first few hundred characters, so large tool outputs
    // are never parsed. How far each file was read is saved, so Codex history is read once, then only what's new.
    public class CodexWatch
    {
        public readonly string Home, SessionsDir, IndexPath;
        readonly ActivityLog log;
        Dictionary<string, CxFile> files;
        readonly HashSet<string> dirty = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        FileSystemWatcher watcher;
        DateTime lastFull = DateTime.MinValue;
        public class CxFile { public long Off; public string Sid = "", Pj = "", Turn = ""; public bool Meta, Skip; public long Tick; }

        static readonly Regex HeadRx = new Regex("^\\{\"timestamp\":\"([^\"]+)\"(?:,\"ordinal\":\\d+)?,\"type\":\"(\\w+)\"(?:,\"payload\":\\{\"type\":\"(\\w+)\")?");

        public static string DefaultHome()
        {
            string h = Environment.GetEnvironmentVariable("CODEX_HOME");
            return string.IsNullOrEmpty(h) ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex") : h;
        }

        public CodexWatch(string home, ActivityLog log, string indexPath)
        {
            Home = home; SessionsDir = Path.Combine(home, "sessions"); IndexPath = indexPath; this.log = log;
        }

        public void Watch(ISynchronizeInvoke sync)
        {
            if (watcher != null || !Directory.Exists(SessionsDir)) return;
            try
            {
                watcher = new FileSystemWatcher(SessionsDir, "*.jsonl") { IncludeSubdirectories = true, SynchronizingObject = sync, NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size };
                FileSystemEventHandler h = (s, e) => { lock (dirty) dirty.Add(e.FullPath); };
                watcher.Changed += h; watcher.Created += h;
                watcher.EnableRaisingEvents = true;
            }
            catch { watcher = null; }
        }

        public void Stop() { if (watcher != null) { watcher.Dispose(); watcher = null; } }

        // Reads what's new. Every file is checked at the first call and every 10 minutes (sizes only); in between only
        // the files the watcher saw change. Returns how many events were added.
        public int Poll(DateTime nowUtc)
        {
            if (!Directory.Exists(SessionsDir)) return 0;
            if (files == null) files = LoadIndex();
            var todo = new List<string>();
            if (watcher == null || (nowUtc - lastFull).TotalMinutes >= 10)
            {
                lastFull = nowUtc;
                try { todo.AddRange(Directory.GetFiles(SessionsDir, "rollout-*.jsonl", SearchOption.AllDirectories)); } catch { }
            }
            lock (dirty) { todo.AddRange(dirty); dirty.Clear(); }
            var events = new List<ActEvent>();
            bool changed = false;
            foreach (var path in todo.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                string key = path.Substring(SessionsDir.Length).TrimStart('\\', '/');
                CxFile f;
                if (!files.TryGetValue(key, out f)) files[key] = f = new CxFile();
                try
                {
                    var fi = new FileInfo(path);
                    if (!fi.Exists || fi.Length < f.Off) { if (fi.Exists) { files[key] = f = new CxFile(); } else continue; }   // replaced: read again (ids keep it from doubling)
                    if (fi.Length == f.Off) continue;
                    using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                    {
                        fs.Position = f.Off;
                        var buf = new byte[Math.Min(fi.Length - f.Off, 64L << 20)];
                        int n = 0; while (n < buf.Length) { int r = fs.Read(buf, n, buf.Length - n); if (r <= 0) break; n += r; }
                        int start = 0;
                        for (int i = 0; i < n; i++)
                        {
                            if (buf[i] != (byte)'\n') continue;
                            // only the start of a line is decoded unless it's one of the few lines that matter
                            int len = i - start;
                            string head = Encoding.UTF8.GetString(buf, start, Math.Min(len, 400));
                            Feed(f, head, () => Encoding.UTF8.GetString(buf, start, len), events);
                            start = i + 1;
                        }
                        if (start > 0) { f.Off += start; changed = true; }
                    }
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
            if (events.Count > 0) log.Append(events);
            if (changed) SaveIndex();
            return events.Count;
        }

        // One line of a Codex session log. head: its first characters; full(): the whole line (only decoded when needed).
        public static void Feed(CxFile f, string head, Func<string> full, List<ActEvent> output)
        {
            var m = HeadRx.Match(head);
            if (!m.Success) return;
            DateTime t;
            if (!DateTime.TryParse(m.Groups[1].Value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out t)) return;
            t = t.ToUniversalTime();
            string type = m.Groups[2].Value, sub = m.Groups[3].Value;
            if (type == "session_meta" && !f.Meta)
            {
                f.Meta = true;
                try
                {
                    var d = new JavaScriptSerializer { MaxJsonLength = int.MaxValue }.Deserialize<Dictionary<string, object>>(full());
                    var p = d != null && d.ContainsKey("payload") ? d["payload"] as Dictionary<string, object> : null;
                    f.Sid = Board.Str(p, "id");
                    string cwd = Board.Str(p, "cwd").TrimEnd('\\', '/');
                    f.Pj = cwd == "" ? "" : Path.GetFileName(cwd);
                    string ts = Board.Str(p, "thread_source");
                    // reviewers and other sub-agents run inside a turn of their parent: counting them would double it
                    f.Skip = (ts != "" && ts != "user") || (p != null && p.ContainsKey("source") && p["source"] is Dictionary<string, object>);
                }
                catch { f.Skip = true; }
                return;
            }
            if (f.Sid == "" || f.Skip) return;
            if (type == "event_msg" && (sub == "task_started" || sub == "task_complete" || sub == "turn_aborted"))
            {
                string turn = "";
                try
                {
                    var d = new JavaScriptSerializer { MaxJsonLength = int.MaxValue }.Deserialize<Dictionary<string, object>>(full());
                    turn = Board.Str(d != null && d.ContainsKey("payload") ? d["payload"] as Dictionary<string, object> : null, "turn_id");
                }
                catch { }
                if (turn == "") turn = sub == "task_started" ? t.Ticks.ToString(CultureInfo.InvariantCulture) : f.Turn;
                string e = sub == "task_started" ? "start" : sub == "task_complete" ? "done" : "abort";
                output.Add(new ActEvent { Id = "cx:" + turn + ":" + e, P = "codex", S = f.Sid, E = e, T = t, Pj = f.Pj });
                f.Turn = sub == "task_started" ? turn : "";
                return;
            }
            if (f.Turn != "")
            {
                long bucket = t.Ticks / TimeSpan.FromMinutes(2).Ticks;   // "still working" evidence, at most one per 2 minutes
                if (bucket != f.Tick) { f.Tick = bucket; output.Add(new ActEvent { Id = "cx:" + f.Turn + ":k" + bucket, P = "codex", S = f.Sid, E = "tick", T = t, Pj = f.Pj }); }
            }
        }

        Dictionary<string, CxFile> LoadIndex()
        {
            try { if (File.Exists(IndexPath)) { var d = new JavaScriptSerializer().Deserialize<Dictionary<string, CxFile>>(File.ReadAllText(IndexPath)); if (d != null) return new Dictionary<string, CxFile>(d, StringComparer.OrdinalIgnoreCase); } }
            catch { }
            return new Dictionary<string, CxFile>(StringComparer.OrdinalIgnoreCase);
        }

        void SaveIndex()
        {
            try { Directory.CreateDirectory(Path.GetDirectoryName(IndexPath)); Json.WriteAtomic(IndexPath, new JavaScriptSerializer().Serialize(files)); } catch { }
        }
    }

    // ---------- one week, laid out ----------
    public class CalPlan
    {
        public string TaskId = "", Title = "", Project = "", Kind = "planned", Label = "", Note = "";
        public DateTime At;
    }

    public class CalBlock
    {
        public PromptRun Run;          // an actual prompt run, or
        public CalPlan Plan;           // a scheduled task's planned start (tentative)
        public int Day, Col, Cols = 1;
        public double StartMin, EndMin;            // minutes after that day's local midnight (wall clock)
        public bool FromPrev, ToNext;              // continues from the day before / into the next day
        public List<double[]> Waits = new List<double[]>();   // waiting parts inside this day's piece, [from, to] minutes
        public string Cat = "other";
    }

    public class CalWeek
    {
        public DateTime[] Days;                    // local dates, Days[0] = first day of the week
        public int Today = -1;
        public double NowMin;
        public DateTime NowUtc, TrackingSince;
        public string Zone = "";
        public bool CodexShown;
        public List<CalBlock> Blocks = new List<CalBlock>();
        public List<PromptRun> Runs = new List<PromptRun>();
        public List<CalPlan> Plans = new List<CalPlan>();
    }

    public static class Calendar
    {
        public const double MinVisual = 12;        // minutes: the shortest a block is drawn, so a 20-second prompt stays visible
        public const double PlanVisual = 30;       // minutes: a planned task's estimated block (its real duration is unknown)

        // "toahnipi" or "other". An explicit "projectCategories" entry (folder name) wins; otherwise folders whose
        // name says Toah Nipi ("Toah_Nipi_Website", "toah-nipi-staff") count. Aliases ("Portal") never decide it.
        public static string Category(string folder, IDictionary<string, object> overrides)
        {
            folder = (folder ?? "").Trim();
            if (overrides != null)
                foreach (var kv in overrides)
                    if (string.Equals(kv.Key, folder, StringComparison.OrdinalIgnoreCase) && kv.Value != null)
                        return Norm(kv.Value.ToString()) == "toahnipi" ? "toahnipi" : "other";
            return Norm(folder).Contains("toahnipi") ? "toahnipi" : "other";
        }

        static string Norm(string s) { var sb = new StringBuilder(); foreach (char c in s.ToLowerInvariant()) if (char.IsLetterOrDigit(c)) sb.Append(c); return sb.ToString(); }

        public static string CategoryLabel(string cat) { return cat == "toahnipi" ? "Toah Nipi" : "Other project"; }

        public static DateTime WeekStart(DateTime localDate)
        {
            var first = CultureInfo.CurrentCulture.DateTimeFormat.FirstDayOfWeek;
            return localDate.Date.AddDays(-(((int)localDate.DayOfWeek - (int)first + 7) % 7));
        }

        // runs: already folded (see CalendarSource). Links each scheduler attempt to the prompt run it started, adds a
        // record-only run for attempts without one (e.g. before tracking existed), and the tasks still to come.
        public static CalWeek Build(DateTime weekStart, List<PromptRun> runs, QueueData queue, IDictionary<string, object> categories, DateTime nowUtc, TimeZoneInfo tz, bool showCodex)
        {
            var w = new CalWeek { NowUtc = nowUtc, CodexShown = showCodex };
            w.Days = new DateTime[7];
            for (int i = 0; i < 7; i++) w.Days[i] = weekStart.Date.AddDays(i);
            var bounds = new DateTime[8];
            for (int i = 0; i < 8; i++) bounds[i] = Sched.LocalToUtc(weekStart.Date.AddDays(i), tz);
            DateTime nowLocal = TimeZoneInfo.ConvertTimeFromUtc(nowUtc, tz);
            w.Today = Array.IndexOf(w.Days, nowLocal.Date);
            w.NowMin = nowLocal.TimeOfDay.TotalMinutes;
            w.Zone = tz.IsDaylightSavingTime(nowLocal) ? tz.DaylightName : tz.StandardName;

            var all = new List<PromptRun>(runs);
            if (queue != null) LinkQueue(all, queue, nowUtc, w.Plans);
            foreach (var r in all)
            {
                if (r.Provider == "codex" && !showCodex) continue;
                if (r.Start >= bounds[7] || r.Until(nowUtc) < bounds[0]) continue;
                w.Runs.Add(r);
            }
            w.Runs.Sort((a, b) => a.Start.CompareTo(b.Start));
            w.Plans.RemoveAll(p => p.At < bounds[0] || p.At >= bounds[7]);
            w.Plans.Sort((a, b) => a.At.CompareTo(b.At));

            for (int day = 0; day < 7; day++)
            {
                DateTime ds = bounds[day], de = bounds[day + 1];
                var list = new List<CalBlock>();
                foreach (var r in w.Runs)
                {
                    DateTime s = r.Start, e = r.Until(nowUtc);
                    if (s >= de || e < ds || (e == ds && s < ds)) continue;
                    DateTime ps = s < ds ? ds : s, pe = e > de ? de : e;
                    var b = new CalBlock { Run = r, Day = day, FromPrev = s < ds, ToNext = e > de, Cat = Category(r.Project, categories) };
                    b.StartMin = Min(ps, w.Days[day], tz);
                    b.EndMin = pe >= de ? 1440 : Min(pe, w.Days[day], tz);
                    if (b.EndMin < b.StartMin) b.EndMin = Math.Min(1440, b.StartMin + (pe - ps).TotalMinutes);   // inside the repeated fall-back hour
                    foreach (var wt in r.Waits)
                    {
                        DateTime wf = wt.From, wtTo = wt.To != DateTime.MinValue ? wt.To : e;
                        if (wtTo <= ds || wf >= de) continue;
                        double a = Min(wf < ds ? ds : wf, w.Days[day], tz), z = wtTo >= de ? 1440 : Min(wtTo, w.Days[day], tz);
                        b.Waits.Add(new[] { Math.Max(b.StartMin, a), Math.Min(b.EndMin, Math.Max(a, z)) });
                    }
                    list.Add(b);
                }
                foreach (var p in w.Plans)
                    if (p.At >= ds && p.At < de)
                    {
                        double m = Min(p.At, w.Days[day], tz);
                        list.Add(new CalBlock { Plan = p, Day = day, StartMin = m, EndMin = Math.Min(1440, m + PlanVisual), Cat = Category(p.Project, categories) });
                    }
                Columns(list);
                w.Blocks.AddRange(list);
            }
            return w;
        }

        static double Min(DateTime utc, DateTime localDay, TimeZoneInfo tz)
        {
            var l = TimeZoneInfo.ConvertTimeFromUtc(utc, tz);
            return Math.Max(0, Math.Min(1440, (l - localDay).TotalMinutes));
        }

        // Side by side: blocks that overlap (as drawn, with the minimum height) share the day's width in columns.
        public static void Columns(List<CalBlock> day)
        {
            day.Sort((a, b) => { int c = a.StartMin.CompareTo(b.StartMin); return c != 0 ? c : b.EndMin.CompareTo(a.EndMin); });
            var cluster = new List<CalBlock>();
            var colEnds = new List<double>();
            double clusterEnd = double.MinValue;
            Action finish = () => { foreach (var x in cluster) x.Cols = colEnds.Count; cluster.Clear(); colEnds.Clear(); };
            foreach (var b in day)
            {
                double vs = b.StartMin, ve = Math.Max(b.EndMin, b.StartMin + MinVisual);
                if (cluster.Count > 0 && vs >= clusterEnd) finish();
                int col = colEnds.FindIndex(end => end <= vs);
                if (col < 0) { col = colEnds.Count; colEnds.Add(ve); } else colEnds[col] = ve;
                b.Col = col;
                cluster.Add(b);
                clusterEnd = cluster.Count == 1 ? ve : Math.Max(clusterEnd, ve);
            }
            if (cluster.Count > 0) finish();
        }

        static void LinkQueue(List<PromptRun> runs, QueueData q, DateTime nowUtc, List<CalPlan> plans)
        {
            foreach (var t in q.Tasks)
            {
                string folder = "";
                try { folder = Path.GetFileName((t.Project ?? "").TrimEnd('\\', '/')); } catch { }
                for (int i = 0; i < t.Attempts.Count; i++)
                {
                    var a = t.Attempts[i];
                    if (a.StartedUtc == DateTime.MinValue) continue;
                    DateTime until = (a.EndedUtc != DateTime.MinValue ? a.EndedUtc : nowUtc).AddSeconds(10);
                    PromptRun hit = null;
                    if (a.SessionId != "")
                        foreach (var r in runs)
                            if (r.Provider == "claude" && r.TaskId == "" && r.Session == a.SessionId && r.Start >= a.StartedUtc.AddSeconds(-10) && r.Start <= until) { hit = r; break; }
                    bool noStart = a.Outcome == "launch" || a.Outcome == "vscode";
                    bool starting = t.State == QState.Running && a == t.Last && a.InVsCode && (a.Phase == "opening" || a.Phase == "terminal" || a.Phase == "");
                    if (hit == null && (noStart || starting))
                    {
                        plans.Add(new CalPlan { TaskId = t.Id, Title = t.Title, Project = folder, At = a.StartedUtc, Kind = noStart ? "nostart" : "starting",
                            Label = noStart ? "Didn't start" : "Starting", Note = noStart ? t.Reason : "Waiting for Claude to start in VS Code." });
                        continue;
                    }
                    if (hit == null)
                    {
                        // no lifecycle events for it (before tracking began, or a background run): the scheduler's own record
                        hit = new PromptRun { Id = "q:" + t.Id + ":" + a.N, Provider = "claude", Session = a.SessionId, Project = folder, Start = a.StartedUtc, Source = "scheduler", WaitsKnown = false };
                        if (a.EndedUtc != DateTime.MinValue) { hit.End = a.EndedUtc; hit.EndKnown = true; hit.LastSeen = a.EndedUtc; }
                        else if (t.State == QState.Running && a == t.Last) { hit.Status = "running"; hit.LastSeen = nowUtc; }
                        else { hit.End = hit.LastSeen = a.StartedUtc; hit.Status = "lost"; }
                        if (hit.Status == "")
                            hit.Status = a.Outcome == "completed" || a.Outcome == "marked_completed" ? "completed" : a.Outcome == "canceled" ? "canceled" : a.Outcome == "interrupted" ? "interrupted" : "failed";
                        runs.Add(hit);
                    }
                    hit.TaskId = t.Id; hit.Title = t.Title; hit.TaskOutcome = a.Outcome;
                    if (i == 0 && t.Schedule == "at") hit.PlannedUtc = t.RunAtUtc;
                }
                if (t.Attempts.Count > 0) continue;   // it started: its run is shown, not the plan
                DateTime at = QueueView.NextTime(t);
                if (QState.Pending(t.State) && at != DateTime.MinValue)
                    plans.Add(new CalPlan { TaskId = t.Id, Title = t.Title, Project = folder, At = at, Kind = t.State == QState.WaitingUsage ? "usage" : "planned",
                        Label = t.State == QState.WaitingUsage ? "Waiting for usage" : t.State == QState.Blocked ? "Blocked" : "Planned", Note = t.Reason });
                else if (QState.Finished(t.State) && t.Schedule == "at" && t.RunAtUtc != DateTime.MinValue)
                    plans.Add(new CalPlan { TaskId = t.Id, Title = t.Title, Project = folder, At = t.RunAtUtc, Kind = t.Outcome == "missed" ? "missed" : t.Outcome == "skipped" ? "skipped" : "canceled",
                        Label = t.Outcome == "missed" ? "Missed" : t.Outcome == "skipped" ? "Skipped" : "Canceled", Note = t.Reason });
            }
        }

        // ---------- text (the desktop and the phone show exactly these) ----------
        public static string Dur(TimeSpan t)
        {
            if (t < TimeSpan.Zero) t = TimeSpan.Zero;
            if (t.TotalMinutes < 1) return (int)t.TotalSeconds + " s";
            if (t.TotalHours < 1) return (int)t.TotalMinutes + " min";
            return (int)t.TotalHours + " h " + t.Minutes.ToString("00") + " min";
        }

        public static string Clock(DateTime utc, TimeZoneInfo tz) { return TimeZoneInfo.ConvertTimeFromUtc(utc, tz).ToString("t", CultureInfo.CurrentCulture); }
        public static string DayClock(DateTime utc, TimeZoneInfo tz) { return TimeZoneInfo.ConvertTimeFromUtc(utc, tz).ToString("ddd d MMM, ", CultureInfo.CurrentCulture) + Clock(utc, tz); }
        public static string Provider(PromptRun r) { return r.Provider == "codex" ? "Codex" : "Claude"; }

        public static string WaitLabel(string d)
        {
            switch (d ?? "")
            {
                case "": return "input";
                case "permission_prompt": return "permission";
                case "AskUserQuestion": return "a question";
                case "elicitation_dialog": case "elicitation_url_dialog": case "agent_needs_input": return "input";
                default: return "permission: " + d;
            }
        }

        public static string StatusLabel(PromptRun r, DateTime now)
        {
            switch (r.Status)
            {
                case "running": return "Running";
                case "waiting": return "Waiting for you (" + WaitLabel(r.Waits[r.Waits.Count - 1].Detail) + ")";
                case "completed": return r.TaskOutcome == "marked_completed" ? "Completed (marked by you)" : "Completed";
                case "failed": return "Failed" + (r.Detail != "" ? " (" + r.Detail + ")" : r.TaskOutcome != "" ? " (" + r.TaskOutcome + ")" : "");
                case "canceled": return "Canceled";
                case "interrupted": return r.EndKnown ? "Interrupted: the session was closed" : "Interrupted: no finish was reported";
                default: return "Ending not recorded";
            }
        }

        // What a block says in one line under the project name.
        public static string BlockLine(CalBlock b, TimeZoneInfo tz, DateTime now)
        {
            if (b.Plan != null) return b.Plan.Label + " · " + Clock(b.Plan.At, tz);
            var r = b.Run;
            string p = Provider(r) + " · ";
            if (r.Status == "waiting") return p + "WAITING";
            if (r.Live) return p + "since " + Clock(r.Start, tz);
            return p + Clock(r.Start, tz) + "–" + Clock(r.End, tz) + (r.EndKnown ? "" : "?");
        }

        public static string Title(CalBlock b) { return b.Plan != null ? b.Plan.Title : b.Run.Title; }

        // What the week can't show (said above the grid on both the desktop and the phone).
        public static List<string> Notes(CalWeek w, TimeZoneInfo tz)
        {
            var notes = new List<string>();
            if (w.TrackingSince != DateTime.MinValue && Sched.LocalToUtc(w.Days[0], tz) < w.TrackingSince)
                notes.Add("Claude Code prompts are recorded from " + DayClock(w.TrackingSince, tz) + " on; earlier blocks come from the Queue's history" + (w.CodexShown ? " and Codex's own logs." : "."));
            if (!w.CodexShown) notes.Add("Codex activity is hidden (Settings: Include Codex).");
            if (w.Blocks.Count == 0) notes.Add("No AI activity or scheduled tasks this week.");
            return notes;
        }

        public static string Note(CalBlock b) { return b.Plan == null && b.Run.Provider == "claude" && b.Run.TaskId == "" ? "Prompt text isn't recorded: Claude Status never stores your prompts." : ""; }

        public static List<string[]> Facts(PromptRun r, string name, string cat, DateTime now, TimeZoneInfo tz)
        {
            var f = new List<string[]>();
            Action<string, string> add = (k, v) => f.Add(new[] { k, v });
            add("Project", name + " · " + CategoryLabel(cat));
            add("AI", r.Provider == "codex" ? "Codex" : "Claude Code");
            if (r.Title != "") add(r.TaskId != "" ? "Scheduled task" : "Task", r.Title);
            add("Status", StatusLabel(r, now));
            add("Started", DayClock(r.Start, tz));
            DateTime until = r.Until(now);
            if (r.Live) add("Ended", "Still running" + ((now - r.LastSeen).TotalMinutes >= 5 && r.Status == "running" ? " · last activity seen " + Clock(r.LastSeen, tz) : ""));
            else if (r.EndKnown) add("Ended", DayClock(r.End, tz));
            else add("Ended", "Not recorded · last activity seen " + DayClock(r.LastSeen, tz));
            string atLeast = r.EndKnown || r.Live ? "" : "at least ";
            add("Elapsed", atLeast + Dur(until - r.Start) + (r.Live ? " so far" : ""));
            if (r.WaitsKnown)
            {
                var waited = r.Waited(now);
                add("Active", atLeast + Dur(until - r.Start - waited));
                add("Waiting", r.Waits.Count == 0 ? "none recorded" : Dur(waited) + " (" + r.Waits.Count + (r.Waits.Count == 1 ? " pause)" : " pauses)"));
            }
            else add("Waiting", r.Provider == "codex" ? "not recorded (Codex's logs don't mark waiting for approval)" : "not recorded for this run");
            if (r.PlannedUtc != DateTime.MinValue)
            {
                double late = (r.Start - r.PlannedUtc).TotalMinutes;
                add("Planned for", DayClock(r.PlannedUtc, tz) + (late >= 1 ? " · started " + Dur(r.Start - r.PlannedUtc) + " late" : ""));
            }
            if (r.TaskId != "" && r.TaskOutcome != "" && r.TaskOutcome != "completed") add("Queue result", r.TaskOutcome);
            add("Recorded by", r.Source == "scheduler" ? "the Queue's own record (no lifecycle events for this run)" : r.Provider == "codex" ? "Codex's session log" : "Claude Code hooks");
            return f;
        }

        public static List<string[]> Facts(CalPlan p, string name, string cat, TimeZoneInfo tz)
        {
            var f = new List<string[]>();
            f.Add(new[] { "Project", name + " · " + CategoryLabel(cat) });
            f.Add(new[] { "AI", "Claude Code" });
            f.Add(new[] { "Status", p.Label });
            f.Add(new[] { p.Kind == "nostart" || p.Kind == "starting" ? "Tried at" : "Planned start", DayClock(p.At, tz) });
            if (p.Kind == "planned" || p.Kind == "usage") f.Add(new[] { "Duration", "Unknown: the block shows " + (int)PlanVisual + " min as an estimate" });
            if (!string.IsNullOrEmpty(p.Note)) f.Add(new[] { "Queue says", p.Note });
            return f;
        }
    }

    // Everything the calendar needs, safe to call from any thread (the phone server asks from its own).
    public class CalendarSource
    {
        public ActivityLog Log;
        public Func<QueueData> Queue = () => null;
        public Func<Dictionary<string, object>> Config = () => new Dictionary<string, object>();
        public Func<string, bool> ClaudeLive = sid => false;    // session id -> is it WORKING / NEEDS YOU with its process running
        public Func<DateTime> Now = () => DateTime.UtcNow;
        public TimeZoneInfo Zone = TimeZoneInfo.Local;
        public string CodexHome = "", SincePath = "";
        public TimeSpan CodexQuiet = TimeSpan.FromMinutes(20);   // an open Codex turn with no new log line for this long isn't shown as running
        readonly object titleGate = new object();
        Dictionary<string, string> codexTitles = new Dictionary<string, string>();
        DateTime titlesWritten;

        public bool ShowCodex { get { object v; return !(Config().TryGetValue("includeCodex", out v) && v is bool && !(bool)v); } }
        public IDictionary<string, object> Categories { get { object v; var c = Config(); return c.TryGetValue("projectCategories", out v) ? v as Dictionary<string, object> : null; } }
        public IDictionary<string, object> Aliases { get { object v; var c = Config(); return c.TryGetValue("projectAliases", out v) ? v as Dictionary<string, object> : null; } }
        public string Name(string folder) { return Board.FriendlyName(folder, Aliases); }

        public CalWeek Week(DateTime anyLocalDate)
        {
            DateTime now = Now(), start = Calendar.WeekStart(anyLocalDate);
            double stale = 24;
            object v;
            try { if (Config().TryGetValue("staleHours", out v)) stale = Math.Max(1, Math.Min(72, Convert.ToDouble(v, CultureInfo.InvariantCulture))); } catch { }
            DateTime from = Sched.LocalToUtc(start, Zone).AddHours(-stale - 1), to = Sched.LocalToUtc(start.AddDays(7), Zone).AddHours(stale + 1);
            var events = Log.Read(from, to < now ? to : now.AddMinutes(1));
            var runs = PromptRuns.Fold(events, now, r => r.Provider == "codex" ? now - r.LastSeen < CodexQuiet : ClaudeLive(r.Session), stale);
            NameCodex(runs);
            var w = Calendar.Build(start, runs, Queue(), Categories, now, Zone, ShowCodex);
            w.TrackingSince = Since();
            return w;
        }

        // Codex's own short thread names (session_index.jsonl), only when a Codex run is on screen.
        void NameCodex(List<PromptRun> runs)
        {
            if (CodexHome == "" || !runs.Exists(r => r.Provider == "codex")) return;
            string path = Path.Combine(CodexHome, "session_index.jsonl");
            lock (titleGate)
            {
                try
                {
                    var fi = new FileInfo(path);
                    if (fi.Exists && fi.LastWriteTimeUtc != titlesWritten)
                    {
                        var d = new Dictionary<string, string>();
                        var ser = new JavaScriptSerializer();
                        using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                        using (var sr = new StreamReader(fs))
                            for (string line; (line = sr.ReadLine()) != null; )
                                try { var o = ser.Deserialize<Dictionary<string, object>>(line); string id = Board.Str(o, "id"), n = Board.Str(o, "thread_name"); if (id != "" && n != "") d[id] = n.Length > 120 ? n.Substring(0, 119) + "…" : n; } catch { }
                        codexTitles = d; titlesWritten = fi.LastWriteTimeUtc;
                    }
                }
                catch { }
                foreach (var r in runs) { string n; if (r.Provider == "codex" && r.Title == "" && codexTitles.TryGetValue(r.Session, out n)) r.Title = n; }
            }
        }

        // When this app started recording Claude Code's prompts (written once, the first time it runs).
        public DateTime Since()
        {
            if (SincePath == "") return DateTime.MinValue;
            try
            {
                DateTime t;
                if (File.Exists(SincePath) && DateTime.TryParse(File.ReadAllText(SincePath).Trim(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out t)) return t.ToUniversalTime();
                Directory.CreateDirectory(Path.GetDirectoryName(SincePath));
                File.WriteAllText(SincePath, Now().ToString("o", CultureInfo.InvariantCulture));
                return Now();
            }
            catch { return DateTime.MinValue; }
        }
    }

    // ---------- the desktop Calendar (the Queue window's Calendar tab) ----------
    public class CalendarView : UserControl
    {
        readonly CalendarSource src;
        readonly float scale;
        readonly Grid grid;
        readonly VScrollBar vbar;
        readonly Label range, zone;
        readonly Wrap notice;
        readonly Panel side;
        readonly Wrap sTitle, sStatus, sFacts, sNote;
        readonly FlowLayoutPanel sActions;
        readonly Timer clock;
        DateTime anchor = DateTime.Today;
        CalWeek week;
        string selected = "";
        bool scrolled;
        public Action<string> OpenTask;                   // a planned block's "Open in Queue"
        public Action<string, string> SetCategory;        // (folder, "toahnipi" | "other")

        int S(float v) { return (int)Math.Round(v * scale); }
        public CalWeek Week { get { return week; } }
        public DateTime ShownDate { get { return anchor; } set { anchor = value.Date; RefreshData(); } }
        public string SelectedId { get { return selected; } }

        public CalendarView(CalendarSource source, float scale)
        {
            src = source; this.scale = scale;
            Font = SystemFonts.MessageBoxFont;
            BackColor = QTheme.Bg; ForeColor = QTheme.Text;

            // one row when it fits; the legend wraps under the navigation in a narrow window
            var bar = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, WrapContents = true, Padding = new Padding(S(10), S(4), S(10), S(2)) };
            var nav = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0, 0, S(16), 0) };
            var prev = QTheme.Btn("‹", (s, e) => GoDays(-7)); prev.AccessibleName = "Previous week";
            var today = QTheme.Btn("Today", (s, e) => { anchor = DateTime.Today; scrolled = false; RefreshData(); });
            var next = QTheme.Btn("›", (s, e) => GoDays(7)); next.AccessibleName = "Next week";
            range = new Label { AutoSize = true, Font = new Font(Font, FontStyle.Bold), Margin = new Padding(S(6), S(6), 0, 0) };
            zone = new Label { AutoSize = true, ForeColor = QTheme.Muted, Margin = new Padding(S(8), S(6), 0, 0) };
            nav.Controls.AddRange(new Control[] { prev, today, next, range, zone });
            var legend = new Legend(scale) { Margin = new Padding(S(8), S(4), 0, 0) };
            bar.Controls.Add(nav); bar.Controls.Add(legend);
            notice = new Wrap { ForeColor = QTheme.Muted, Padding = new Padding(S(12), 0, S(12), S(2)) };

            // details of the selected block
            side = new Panel { Dock = DockStyle.Right, Width = S(290), Padding = new Padding(S(12), S(8), S(10), S(8)), BackColor = QTheme.Panel, Visible = false, AutoScroll = true };
            var close = QTheme.Btn("×", (s, e) => SelectBlock("")); close.AccessibleName = "Close details";
            var closeRow = new Panel { Dock = DockStyle.Top, Height = close.PreferredSize.Height + S(2) };
            close.Dock = DockStyle.Right; closeRow.Controls.Add(close);
            sTitle = new Wrap { Font = new Font(Font.FontFamily, Font.Size * 1.15f, FontStyle.Bold) };
            sStatus = new Wrap { Font = new Font(Font, FontStyle.Bold) };
            sFacts = new Wrap { ForeColor = QTheme.Text };
            sNote = new Wrap { ForeColor = QTheme.Muted };
            sActions = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, WrapContents = true, Padding = new Padding(0, S(6), 0, 0) };
            foreach (Control c in new Control[] { sActions, sNote, sFacts, sStatus, sTitle, closeRow }) side.Controls.Add(c);

            grid = new Grid(this) { Dock = DockStyle.Fill, TabStop = true, AccessibleName = "Calendar week", AccessibleRole = AccessibleRole.Table };
            vbar = new VScrollBar { Dock = DockStyle.Right, SmallChange = S(20) };
            vbar.Scroll += (s, e) => grid.Invalidate();
            vbar.ValueChanged += (s, e) => grid.Invalidate();
            var body = new Panel { Dock = DockStyle.Fill };
            body.Controls.Add(grid); body.Controls.Add(vbar);

            Controls.Add(body); Controls.Add(side); Controls.Add(notice); Controls.Add(bar);
            QTheme.Style(bar); QTheme.Style(side);
            side.BackColor = QTheme.Panel;
            grid.Resize += (s, e) => UpdateScroll();

            clock = new Timer { Interval = 30000 };   // running blocks grow; the now line moves
            clock.Tick += (s, e) => { if (Visible) RefreshData(); };
            clock.Start();
            Disposed += (s, e) => clock.Dispose();
            VisibleChanged += (s, e) => { if (Visible) RefreshData(); };
        }

        void GoDays(int days) { anchor = anchor.AddDays(days); scrolled = false; RefreshData(); }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            switch (keyData)
            {
                case Keys.PageUp: GoDays(-7); return true;
                case Keys.PageDown: GoDays(7); return true;
                case Keys.Home: anchor = DateTime.Today; scrolled = false; RefreshData(); return true;
                case Keys.Escape: if (selected != "") { SelectBlock(""); return true; } break;
            }
            if (grid.Focused && (keyData == Keys.Left || keyData == Keys.Right)) { Step(keyData == Keys.Right ? 1 : -1); return true; }
            if (grid.Focused && (keyData == Keys.Up || keyData == Keys.Down)) { ScrollBy(keyData == Keys.Down ? S(40) : -S(40)); return true; }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        // Left / Right walk through the week's blocks in time order (keyboard access to their details).
        void Step(int d)
        {
            if (week == null || week.Blocks.Count == 0) return;
            var order = week.Blocks.OrderBy(b => b.Day).ThenBy(b => b.StartMin).ToList();
            int i = order.FindIndex(b => Key(b) == selected);
            i = i < 0 ? (d > 0 ? 0 : order.Count - 1) : Math.Max(0, Math.Min(order.Count - 1, i + d));
            SelectBlock(Key(order[i]));
            grid.Reveal(order[i]);
        }

        public static string Key(CalBlock b) { return b.Plan != null ? "plan:" + b.Plan.TaskId + ":" + b.Plan.At.Ticks : b.Run.Id; }

        public void RefreshData()
        {
            try { week = src.Week(anchor); } catch (Exception e) { notice.Text = "Couldn't read the activity history: " + e.Message; return; }
            var d0 = week.Days[0]; var d6 = week.Days[6];
            range.Text = d0.ToString("MMM d", CultureInfo.CurrentCulture) + " – " + (d0.Month == d6.Month ? d6.ToString("%d", CultureInfo.CurrentCulture) : d6.ToString("MMM d", CultureInfo.CurrentCulture)) + ", " + d6.ToString("yyyy", CultureInfo.CurrentCulture);
            zone.Text = "Times: " + week.Zone;
            notice.Text = string.Join("  ", Calendar.Notes(week, src.Zone).ToArray());
            if (selected != "" && Find(selected) == null) SelectBlock(""); else if (selected != "") ShowDetails();
            UpdateScroll();
            if (!scrolled && grid.Height > 0)
            {
                scrolled = true;
                // open where something is: the earliest block this week, or a little before now
                double first = week.Today >= 0 ? Math.Max(0, week.NowMin - 120) : 8 * 60;
                foreach (var b in week.Blocks) if (b.StartMin < first && week.Today < 0) first = b.StartMin;
                vbar.Value = Math.Max(0, Math.Min(MaxScroll, (int)(first / 60 * grid.HourH)));
            }
            grid.Invalidate();
        }

        int MaxScroll { get { return Math.Max(0, grid.HourH * 24 - (grid.Height - grid.HeadH)); } }

        void UpdateScroll()
        {
            vbar.Maximum = Math.Max(0, grid.HourH * 24 - 1);
            vbar.LargeChange = Math.Max(1, grid.Height - grid.HeadH);
            if (vbar.Value > MaxScroll) vbar.Value = MaxScroll;
            vbar.Enabled = MaxScroll > 0;
        }

        internal void ScrollBy(int dy) { vbar.Value = Math.Max(0, Math.Min(MaxScroll, vbar.Value + dy)); }
        internal void ScrollTo(int y) { vbar.Value = Math.Max(0, Math.Min(MaxScroll, y)); }
        internal int ScrollY { get { return vbar.Value; } }

        CalBlock Find(string key) { if (week == null) return null; foreach (var b in week.Blocks) if (Key(b) == key) return b; return null; }

        internal void SelectBlock(string key)
        {
            selected = key ?? "";
            side.Visible = selected != "";
            if (selected != "") ShowDetails();
            grid.Invalidate();
        }

        void ShowDetails()
        {
            var b = Find(selected);
            if (b == null) return;
            DateTime now = src.Now();
            string folder = b.Plan != null ? b.Plan.Project : b.Run.Project, name = src.Name(folder);
            var facts = b.Plan != null ? Calendar.Facts(b.Plan, name, b.Cat, src.Zone) : Calendar.Facts(b.Run, name, b.Cat, now, src.Zone);
            string title = Calendar.Title(b);
            sTitle.Text = title != "" ? title : name;
            sStatus.Text = b.Plan != null ? "Planned · tentative" : Calendar.StatusLabel(b.Run, now);
            sStatus.ForeColor = Cat(b.Cat);
            var sb = new StringBuilder();
            foreach (var f in facts) sb.Append(f[0]).Append(":  ").Append(f[1]).Append("\r\n");
            sFacts.Text = sb.ToString().TrimEnd();
            sNote.Text = Calendar.Note(b);
            sActions.Controls.Clear();
            if (b.Plan != null && OpenTask != null) { string id = b.Plan.TaskId; sActions.Controls.Add(QTheme.Primary(QTheme.Btn("Open in Queue", (s, e) => OpenTask(id)))); }
            else if (b.Run.TaskId != "" && OpenTask != null) { string id = b.Run.TaskId; sActions.Controls.Add(QTheme.Btn("Open task in Queue", (s, e) => OpenTask(id))); }
            if (SetCategory != null && folder != "")
            {
                string other = b.Cat == "toahnipi" ? "other" : "toahnipi";
                sActions.Controls.Add(QTheme.Btn("Count " + name + " as " + (other == "toahnipi" ? "Toah Nipi" : "other"), (s, e) => { SetCategory(folder, other); RefreshData(); }));
            }
            QTheme.Style(sActions);
            grid.AccessibleDescription = sTitle.Text + ". " + sStatus.Text;
        }

        internal static Color Cat(string cat) { return cat == "toahnipi" ? QTheme.Green : QTheme.Yellow; }
        static Color Mix(Color a, Color b, float t) { return Color.FromArgb((int)(a.R * t + b.R * (1 - t)), (int)(a.G * t + b.G * (1 - t)), (int)(a.B * t + b.B * (1 - t))); }

        // The legend: color = project category; fill style = what the block is.
        class Legend : Control
        {
            readonly float scale;
            public Legend(float scale)
            {
                this.scale = scale; DoubleBuffered = true;
                Font = new Font("Segoe UI", 12f * scale, GraphicsUnit.Pixel);
                int w = 0;
                foreach (var t in new[] { "Toah Nipi", "Other", "Waiting", "Planned" }) w += (int)(28 * scale) + TextRenderer.MeasureText(t, Font, Size.Empty, TextFormatFlags.NoPadding).Width;
                Size = new Size(w, (int)(26 * scale));
                AccessibleName = "Legend: green Toah Nipi, yellow other projects, hatched waiting for you, dashed planned";
            }
            protected override void OnPaint(PaintEventArgs e)
            {
                var g = e.Graphics; g.Clear(QTheme.Bg);   // (AccessibleName set above)
                int x = 0, h = Height, sw = (int)(14 * scale), sh = (int)(11 * scale), y = (h - sh) / 2;
                Action<string, Action<Rectangle>> item = (text, draw) =>
                {
                    var r = new Rectangle(x, y, sw, sh); draw(r);
                    x += sw + (int)(4 * scale);
                    int tw = TextRenderer.MeasureText(g, text, Font, Size.Empty, TextFormatFlags.NoPadding).Width;
                    TextRenderer.DrawText(g, text, Font, new Point(x, (h - Font.Height) / 2), QTheme.Muted, TextFormatFlags.NoPadding);
                    x += tw + (int)(10 * scale);
                };
                item("Toah Nipi", r => { using (var b = new SolidBrush(Mix(QTheme.Green, QTheme.Bg, .45f))) g.FillRectangle(b, r); using (var b = new SolidBrush(QTheme.Green)) g.FillRectangle(b, r.X, r.Y, (int)(3 * scale), r.Height); });
                item("Other", r => { using (var b = new SolidBrush(Mix(QTheme.Yellow, QTheme.Bg, .45f))) g.FillRectangle(b, r); using (var b = new SolidBrush(QTheme.Yellow)) g.FillRectangle(b, r.X, r.Y, (int)(3 * scale), r.Height); });
                item("Waiting", r => { using (var b = new HatchBrush(HatchStyle.WideUpwardDiagonal, QTheme.Muted, Mix(QTheme.Muted, QTheme.Bg, .25f))) g.FillRectangle(b, r); });
                item("Planned", r => { using (var p = new Pen(QTheme.Muted) { DashStyle = DashStyle.Dash }) g.DrawRectangle(p, r.X, r.Y, r.Width - 1, r.Height - 1); });
            }
        }

        // The week grid: day headers (fixed) over 24 hours of time (scrolls).
        internal class Grid : Control
        {
            readonly CalendarView v;
            readonly Font small, bold, head;
            readonly List<KeyValuePair<Rectangle, CalBlock>> hits = new List<KeyValuePair<Rectangle, CalBlock>>();
            public int HourH { get { return v.S(44); } }
            public int HeadH { get { return v.S(34); } }
            int Gutter { get { return v.S(54); } }

            public Grid(CalendarView view)
            {
                v = view;
                DoubleBuffered = true; ResizeRedraw = true;
                SetStyle(ControlStyles.Selectable, true);
                small = new Font("Segoe UI", 10.5f * v.scale, GraphicsUnit.Pixel);
                bold = new Font("Segoe UI", 11.5f * v.scale, FontStyle.Bold, GraphicsUnit.Pixel);
                head = new Font("Segoe UI", 12f * v.scale, FontStyle.Bold, GraphicsUnit.Pixel);
            }

            float ColW { get { return Math.Max(1f, (Width - Gutter) / 7f); } }
            int Y(double min) { return HeadH + (int)Math.Round(min / 60.0 * HourH) - v.ScrollY; }

            public Rectangle BlockRect(CalBlock b)
            {
                float cw = ColW, inner = cw - v.S(6), w = inner / b.Cols;
                int x = Gutter + (int)(b.Day * cw + v.S(2) + b.Col * w);
                int y0 = Y(b.StartMin), y1 = Y(Math.Max(b.EndMin, b.StartMin + Calendar.MinVisual));
                return new Rectangle(x, y0, Math.Max(v.S(6), (int)w - v.S(2)), Math.Max(v.S(6), y1 - y0 - 1));
            }

            public void Reveal(CalBlock b)
            {
                int top = (int)(b.StartMin / 60 * HourH), bottom = (int)(Math.Max(b.EndMin, b.StartMin + Calendar.MinVisual) / 60 * HourH), view = Height - HeadH;
                if (top < v.ScrollY || bottom > v.ScrollY + view) v.ScrollTo(top - v.S(30));
            }

            protected override void OnMouseWheel(MouseEventArgs e) { v.ScrollBy(-e.Delta * HourH / 120); }
            protected override void OnMouseDown(MouseEventArgs e)
            {
                Focus();
                for (int i = hits.Count - 1; i >= 0; i--) if (hits[i].Key.Contains(e.Location)) { v.SelectBlock(Key(hits[i].Value)); return; }
                if (e.Y > HeadH) v.SelectBlock("");
            }
            protected override void OnMouseMove(MouseEventArgs e)
            {
                bool over = false;
                foreach (var h in hits) if (h.Key.Contains(e.Location)) { over = true; break; }
                Cursor = over ? Cursors.Hand : Cursors.Default;
            }
            protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
            protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }

            protected override void OnPaint(PaintEventArgs e)
            {
                var g = e.Graphics;
                g.Clear(QTheme.Bg);
                hits.Clear();
                var w = v.week;
                if (w == null) return;
                float cw = ColW;
                DateTime now = v.src.Now();
                // today's column, hour lines and labels
                if (w.Today >= 0) using (var b = new SolidBrush(Mix(QTheme.Panel, QTheme.Bg, .9f))) g.FillRectangle(b, Gutter + w.Today * cw, HeadH, cw, Height - HeadH);
                using (var hour = new Pen(QTheme.Line)) using (var half = new Pen(Mix(QTheme.Line, QTheme.Bg, .45f)) { DashStyle = DashStyle.Dot })
                {
                    for (int h = 0; h <= 24; h++)
                    {
                        int y = Y(h * 60);
                        if (y >= HeadH - 1 && y <= Height) g.DrawLine(hour, Gutter, y, Width, y);
                        int y2 = Y(h * 60 + 30);
                        if (h < 24 && y2 > HeadH && y2 < Height) g.DrawLine(half, Gutter, y2, Width, y2);
                        if (h > 0 && h < 24 && y - small.Height / 2 > HeadH)
                            TextRenderer.DrawText(g, DateTime.Today.AddHours(h).ToString("h tt", CultureInfo.CurrentCulture), small, new Rectangle(0, y - small.Height / 2 - 1, Gutter - v.S(6), small.Height + 2), QTheme.Muted, TextFormatFlags.Right | TextFormatFlags.NoPadding);
                    }
                    for (int d = 0; d <= 7; d++) g.DrawLine(hour, Gutter + d * cw, HeadH, Gutter + d * cw, Height);
                }
                // blocks
                g.SetClip(new Rectangle(Gutter, HeadH, Width - Gutter, Height - HeadH));
                foreach (var b in w.Blocks)
                {
                    var r = BlockRect(b);
                    if (r.Bottom < HeadH || r.Top > Height) { hits.Add(new KeyValuePair<Rectangle, CalBlock>(r, b)); continue; }
                    DrawBlock(g, b, r, now);
                    hits.Add(new KeyValuePair<Rectangle, CalBlock>(r, b));
                }
                // now
                if (w.Today >= 0)
                {
                    int y = Y(w.NowMin + (now - w.NowUtc).TotalMinutes);
                    using (var p = new Pen(QTheme.Red, Math.Max(2, v.S(2)))) g.DrawLine(p, Gutter + w.Today * cw, y, Gutter + (w.Today + 1) * cw, y);
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    using (var b = new SolidBrush(QTheme.Red)) g.FillEllipse(b, Gutter + w.Today * cw - v.S(4), y - v.S(4), v.S(8), v.S(8));
                    g.SmoothingMode = SmoothingMode.None;
                }
                g.ResetClip();
                // day headers (on top of the scrolled grid)
                using (var b = new SolidBrush(QTheme.Bg)) g.FillRectangle(b, 0, 0, Width, HeadH);
                for (int d = 0; d < 7; d++)
                {
                    var rect = new Rectangle(Gutter + (int)(d * cw), 0, (int)cw, HeadH);
                    string text = w.Days[d].ToString("ddd d", CultureInfo.CurrentCulture);
                    if (d == w.Today)
                    {
                        int tw = TextRenderer.MeasureText(g, text, head).Width;
                        var pill = new Rectangle(rect.X + (rect.Width - tw) / 2 - v.S(2), rect.Y + v.S(6), tw + v.S(4), HeadH - v.S(12));
                        using (var pb = new SolidBrush(QTheme.Accent)) g.FillRectangle(pb, pill);
                        TextRenderer.DrawText(g, text, head, pill, QTheme.AccentText, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                    }
                    else TextRenderer.DrawText(g, text, head, rect, QTheme.Text, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
                }
                using (var p = new Pen(QTheme.Line)) g.DrawLine(p, 0, HeadH - 1, Width, HeadH - 1);
                if (Focused) ControlPaint.DrawFocusRectangle(g, new Rectangle(1, 1, Width - 2, HeadH - 2), QTheme.Text, QTheme.Bg);
            }

            void DrawBlock(Graphics g, CalBlock b, Rectangle r, DateTime now)
            {
                Color c = Cat(b.Cat);
                bool sel = Key(b) == v.selected;
                if (b.Plan != null)
                {
                    // tentative: faint fill, dashed outline, fading out (the duration isn't known)
                    using (var lg = new LinearGradientBrush(new Rectangle(r.X, r.Y, r.Width, r.Height + 1), Color.FromArgb(70, c), Color.FromArgb(8, c), LinearGradientMode.Vertical)) g.FillRectangle(lg, r);
                    bool bad = b.Plan.Kind == "missed" || b.Plan.Kind == "canceled" || b.Plan.Kind == "skipped" || b.Plan.Kind == "nostart";
                    using (var p = new Pen(bad ? QTheme.Grey : c, Math.Max(1, v.S(1))) { DashStyle = DashStyle.Dash }) g.DrawRectangle(p, r.X, r.Y, r.Width - 1, r.Height - 1);
                }
                else
                {
                    var run = b.Run;
                    using (var fill = new SolidBrush(Mix(c, QTheme.Bg, run.Live ? .5f : .38f))) g.FillRectangle(fill, r);
                    foreach (var wt in b.Waits)
                    {
                        int y0 = Math.Max(r.Top, Y(wt[0])), y1 = Math.Min(r.Bottom, Y(wt[1]));
                        if (y1 - y0 < 2) y1 = Math.Min(r.Bottom, y0 + 2);
                        using (var hb = new HatchBrush(HatchStyle.WideUpwardDiagonal, Color.FromArgb(200, c), Mix(c, QTheme.Bg, .16f))) g.FillRectangle(hb, r.X, y0, r.Width, y1 - y0);
                        if (y1 - y0 >= small.Height + v.S(2) && r.Width > v.S(50))
                        {
                            var lr = new Rectangle(r.X + v.S(5), y0 + (y1 - y0 - small.Height) / 2, Math.Min(r.Width - v.S(8), TextRenderer.MeasureText("WAITING", small).Width), small.Height);
                            using (var bb = new SolidBrush(QTheme.Bg)) g.FillRectangle(bb, lr);
                            TextRenderer.DrawText(g, "WAITING", small, lr, QTheme.Text, TextFormatFlags.NoPadding | TextFormatFlags.HorizontalCenter);
                        }
                    }
                    using (var bar = new SolidBrush(c)) g.FillRectangle(bar, r.X, r.Y, Math.Max(2, v.S(3)), r.Height);
                    if (!run.EndKnown && !run.Live)   // the ending wasn't recorded: a dashed bottom edge instead of a solid one
                        using (var p = new Pen(QTheme.Text, Math.Max(1, v.S(1))) { DashStyle = DashStyle.Dot }) g.DrawLine(p, r.X, r.Bottom - 1, r.Right - 1, r.Bottom - 1);
                }
                // text: project, then the AI and times
                int pad = v.S(6);
                var tr = new Rectangle(r.X + pad, r.Y + v.S(1), r.Width - pad - v.S(2), r.Height - v.S(2));
                if (tr.Height >= small.Height - v.S(2) && tr.Width > v.S(14))
                {
                    var flags = TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine;
                    string folder = b.Plan != null ? b.Plan.Project : b.Run.Project;
                    string line1 = b.Plan != null && b.Plan.Title != "" ? b.Plan.Title : v.src.Name(folder);   // a plan: which task
                    if (QTheme.HighContrast) line1 = (b.Cat == "toahnipi" ? "TN · " : "") + line1;
                    TextRenderer.DrawText(g, line1, bold, new Rectangle(tr.X, tr.Y, tr.Width, bold.Height), b.Plan != null ? QTheme.Muted : QTheme.Text, flags);
                    if (tr.Height >= bold.Height + small.Height)
                        TextRenderer.DrawText(g, Calendar.BlockLine(b, v.src.Zone, now), small, new Rectangle(tr.X, tr.Y + bold.Height, tr.Width, small.Height), QTheme.Muted, flags);
                    string title = Calendar.Title(b);
                    if (title != "" && tr.Height >= bold.Height + small.Height * 2)
                        TextRenderer.DrawText(g, title, small, new Rectangle(tr.X, tr.Y + bold.Height + small.Height, tr.Width, small.Height), QTheme.Muted, flags);
                }
                if (sel) using (var p = new Pen(QTheme.Focus, Math.Max(2, v.S(2)))) g.DrawRectangle(p, r.X, r.Y, r.Width - 1, r.Height - 1);
            }
        }
    }
}
