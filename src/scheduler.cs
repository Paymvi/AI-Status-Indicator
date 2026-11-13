// Claude Status - prompt scheduler (the Queue). No UI here: queue.cs draws it. Compiled with indicator.cs (C# 5).
// Tasks run one at a time through Claude Code's documented headless CLI:
//   claude -p --output-format stream-json --verbose --permission-prompts none --settings <file> (--session-id|--resume) <uuid>
// The prompt goes in on stdin (never on a command line); permissions come from a generated --settings file; and
// --permission-prompts none makes anything outside them a recorded denial instead of a question nobody can answer.
// Claude's output goes straight into a log file this app tails, so a run keeps going (and is picked up again) when
// the app quits or crashes. Everything here runs on the caller's thread from Tick(); nothing is threaded.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;

namespace ClaudeStatus
{
    public static class QState
    {
        public const string Draft = "Draft", Scheduled = "Scheduled", Queued = "Queued", Running = "Running", WaitingUsage = "WaitingUsage",
            Blocked = "Blocked", NeedsAttention = "NeedsAttention", Completed = "Completed", Failed = "Failed", Canceled = "Canceled";
        // Waiting to run (the engine decides when).
        public static bool Pending(string s) { return s == Scheduled || s == Queued || s == WaitingUsage || s == Blocked; }
        public static bool Finished(string s) { return s == Completed || s == Failed || s == Canceled; }
        public static string Label(string s)
        {
            switch (s) { case WaitingUsage: return "Waiting for usage"; case NeedsAttention: return "Needs attention"; case Queued: return "Ready"; default: return s ?? ""; }
        }
    }

    public class RunAttempt
    {
        public int N, Pid, ExitCode = int.MinValue;   // MinValue: exit code unknown (e.g. finished while the app wasn't running)
        public string SessionId = "", Mode = "", Prompt = "", Outcome = "", Owner = "", Job = "", Log = "";
        public DateTime StartedUtc, EndedUtc, PidStartedUtc, OwnerStartedUtc;
        public int OwnerPid;
        // VS Code runs: "" = headless (claude -p; every attempt made before the VS Code runner existed).
        // Phase: opening (asked VS Code) -> terminal (terminal open, Claude not started yet) -> working / needs_you -> ended.
        public string Runner = "", Phase = "", Req = "", Window = "", Terminal = "", Attention = "";
        public int ClaudePid;
        public DateTime ClaudeStartedUtc, HookBefore;   // HookBefore: the session's last hook event before this run (older ones are never this run's)
        [ScriptIgnore] public bool InVsCode { get { return Runner == "vscode"; } }
    }

    public class QueueTask
    {
        public string Id = "", Title = "", Project = "", Prompt = "";
        public string Conversation = "new";            // new | resume (ResumeSession) | predecessor (the After task's conversation)
        public string ResumeSession = "", SessionId = "";   // SessionId: the conversation this task runs in (kept for follow-ups)
        public string Schedule = "asap";               // asap | at | after | usage
        public string After = "", RunAtLocal = "", TimeZone = "", IfMissed = "run";
        public DateTime RunAtUtc, UsageResetUtc, NextRetryUtc, CreatedUtc, UpdatedUtc, FinishedUtc;
        public string PermissionMode = "acceptEdits";  // acceptEdits | dontAsk | plan (never bypassPermissions)
        public List<string> AllowCommands = new List<string>();
        public bool AllowCommit, AllowPush, AllowDelete, RunNow, NeedsContinue;
        public string Model = "", ContinueWhy = "";
        public string Runner = "vscode";               // vscode (a terminal in the project's VS Code window) | headless (claude -p, no window)
        public string ManualReq = "";                  // a VS Code terminal you opened on this conversation yourself (the queue leaves it alone)
        public string State = QState.Draft, Reason = "", Outcome = "", ResultText = "", Summary = "", ResetSource = "", Notified = "";
        public List<string> Denials = new List<string>();
        public double Order;
        public int RetryCount, Revision;
        public List<RunAttempt> Attempts = new List<RunAttempt>();

        [ScriptIgnore] public RunAttempt Last { get { return Attempts.Count > 0 ? Attempts[Attempts.Count - 1] : null; } }
    }

    public class QueueData
    {
        public int Version = 1;
        public bool Paused;
        public List<QueueTask> Tasks = new List<QueueTask>();
        public QueueTask Find(string id) { foreach (var t in Tasks) if (t.Id == id) return t; return null; }
    }

    // ---------- persistence: one JSON file, changed only under an exclusive cross-process lock ----------
    public class QueueStore
    {
        public readonly string Dir, FilePath;
        readonly string lockPath;
        public string Warning = "";

        public QueueStore(string dir)
        {
            Dir = dir;
            Directory.CreateDirectory(dir);
            FilePath = Path.Combine(dir, "tasks.json");
            lockPath = Path.Combine(dir, "tasks.lock");
        }

        public static JavaScriptSerializer Ser() { return new JavaScriptSerializer { MaxJsonLength = int.MaxValue }; }

        public QueueData Read()
        {
            if (!File.Exists(FilePath)) return new QueueData();
            string text = File.ReadAllText(FilePath, Encoding.UTF8);
            try
            {
                var d = Ser().Deserialize<QueueData>(text) ?? new QueueData();
                if (d.Tasks == null) d.Tasks = new List<QueueTask>();
                d.Tasks.RemoveAll(t => t == null);
                foreach (var t in d.Tasks) Normalize(t);
                return d;
            }
            catch (Exception e)
            {
                // Never lose tasks silently: the unreadable file is kept next to the new one.
                string keep = FilePath + ".unreadable-" + DateTime.Now.ToString("yyyyMMdd-HHmmss");
                try { File.Copy(FilePath, keep, true); } catch { }
                Warning = "tasks.json couldn't be read (" + e.Message + "). It was kept as " + Path.GetFileName(keep) + ".";
                return new QueueData();
            }
        }

        public static QueueTask Clone(QueueTask t)
        {
            var ser = Ser();
            var c = ser.Deserialize<QueueTask>(ser.Serialize(t));
            Normalize(c);
            return c;
        }

        static void Normalize(QueueTask t)
        {
            if (t.AllowCommands == null) t.AllowCommands = new List<string>();
            if (t.Denials == null) t.Denials = new List<string>();
            if (t.Attempts == null) t.Attempts = new List<RunAttempt>();
            t.Attempts.RemoveAll(a => a == null);
            Fix(t);
            if (t.Runner != "headless") t.Runner = "vscode";   // tasks saved before the VS Code runner existed run in VS Code from now on
            foreach (var a in t.Attempts) Fix(a);
        }

        // Missing strings become "", and an unset time comes back as exactly DateTime.MinValue (the serializer
        // writes MinValue as a local time, so it reads back a few hours off).
        static void Fix(object o)
        {
            foreach (var f in o.GetType().GetFields())
            {
                if (f.FieldType == typeof(string) && f.GetValue(o) == null) f.SetValue(o, "");
                if (f.FieldType == typeof(DateTime))
                {
                    var v = (DateTime)f.GetValue(o);
                    f.SetValue(o, v.Year <= 1 ? DateTime.MinValue : DateTime.SpecifyKind(v.ToUniversalTime(), DateTimeKind.Utc));
                }
            }
        }

        // Reads the newest file, applies the change and writes it back (only if the change says it changed
        // something), all while holding the lock, so two app instances can never both claim one task.
        public QueueData Mutate(Func<QueueData, bool> change)
        {
            using (Lock())
            {
                var d = Read();
                if (change(d)) Json.WriteAtomic(FilePath, Json.Pretty(Ser().Serialize(d)));
                return d;
            }
        }

        IDisposable Lock()
        {
            var sw = Stopwatch.StartNew();
            while (true)
            {
                try { return new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
                catch (IOException) { if (sw.ElapsedMilliseconds > 10000) throw new TimeoutException("The queue file is locked by another program."); System.Threading.Thread.Sleep(20); }
            }
        }
    }

    // ---------- pure scheduling rules (tested directly) ----------
    public static class Sched
    {
        // Wall-clock time in a time zone -> UTC. A time inside a spring-forward gap moves to the first valid minute
        // after it; an ambiguous fall-back time means its first occurrence.
        public static DateTime LocalToUtc(DateTime wall, TimeZoneInfo tz)
        {
            wall = DateTime.SpecifyKind(wall, DateTimeKind.Unspecified);
            for (int i = 0; i < 240 && tz.IsInvalidTime(wall); i++) wall = wall.AddMinutes(1);
            if (tz.IsAmbiguousTime(wall))
            {
                TimeSpan max = TimeSpan.MinValue;
                foreach (var o in tz.GetAmbiguousTimeOffsets(wall)) if (o > max) max = o;
                return DateTime.SpecifyKind(wall - max, DateTimeKind.Utc);
            }
            return TimeZoneInfo.ConvertTimeToUtc(wall, tz);
        }

        public static string LocalText(DateTime utc)
        {
            if (utc == DateTime.MinValue) return "";
            DateTime l = utc.ToLocalTime(), today = DateTime.Now.Date;
            string day = l.Date == today ? "today" : l.Date == today.AddDays(1) ? "tomorrow" : l.Date == today.AddDays(-1) ? "yesterday" : l.ToString("ddd d MMM", CultureInfo.CurrentCulture);
            return day + " " + l.ToString("t", CultureInfo.CurrentCulture);
        }

        // True if making `id` run after `after` would close a loop (A after B after ... after A).
        public static bool CreatesCycle(IList<QueueTask> tasks, string id, string after)
        {
            var byId = new Dictionary<string, QueueTask>();
            foreach (var t in tasks) byId[t.Id] = t;
            var seen = new HashSet<string>();
            for (string cur = after; !string.IsNullOrEmpty(cur);)
            {
                if (cur == id || !seen.Add(cur)) return true;
                QueueTask t;
                if (!byId.TryGetValue(cur, out t) || t.Schedule != "after") return false;
                cur = t.After;
            }
            return false;
        }

        static readonly int[] UsageBackoffMin = { 15, 30, 60, 120, 180 };
        static readonly int[] NetBackoffMin = { 2, 10, 30 };
        public const int MaxUsageRetries = 12, MaxNetRetries = 3;
        public static TimeSpan UsageBackoff(int retry) { return TimeSpan.FromMinutes(UsageBackoffMin[Math.Min(Math.Max(retry, 0), UsageBackoffMin.Length - 1)]); }
        public static TimeSpan NetBackoff(int retry) { return TimeSpan.FromMinutes(NetBackoffMin[Math.Min(Math.Max(retry, 0), NetBackoffMin.Length - 1)]); }

        static readonly Regex ResetRx = new Regex(@"resets?\s+(?:at\s+)?(?:(mon|tue|wed|thu|fri|sat|sun)[a-z]*\.?\s+)?(\d{1,2})(?::(\d{2}))?\s*([ap]\.?m\.?)?", RegexOptions.IgnoreCase);

        // "resets 3:45pm", "resets Mon 12:00am", "resets 3pm (Europe/London)" -> the next such local time.
        public static DateTime? ParseResetText(string text, DateTime nowLocal)
        {
            if (string.IsNullOrEmpty(text)) return null;
            var m = ResetRx.Match(text);
            if (!m.Success) return null;
            int h = int.Parse(m.Groups[2].Value), min = m.Groups[3].Success ? int.Parse(m.Groups[3].Value) : 0;
            string ap = m.Groups[4].Value.ToLowerInvariant();
            if (ap != "") { if (h < 1 || h > 12) return null; h = h % 12 + (ap.StartsWith("p") ? 12 : 0); }
            if (h > 23 || min > 59) return null;
            DateTime t = nowLocal.Date.AddHours(h).AddMinutes(min);
            if (m.Groups[1].Success)
            {
                var days = new[] { "sun", "mon", "tue", "wed", "thu", "fri", "sat" };
                int want = Array.IndexOf(days, m.Groups[1].Value.ToLowerInvariant());
                int add = (want - (int)nowLocal.DayOfWeek + 7) % 7;
                t = t.AddDays(add);
                if (t <= nowLocal) t = t.AddDays(7);
            }
            else if (t <= nowLocal) t = t.AddDays(1);
            return t;
        }

        public static bool IsGuid(string s) { Guid g; return !string.IsNullOrEmpty(s) && s.Length == 36 && Guid.TryParse(s, out g); }

        public static string NormPath(string p)
        {
            if (string.IsNullOrEmpty(p)) return "";
            try { p = Path.GetFullPath(p); } catch { }
            return p.TrimEnd('\\', '/');
        }
        // Full paths, case-insensitive, Unicode-normalized (a folder typed as "é" and one stored as "e" + accent match).
        public static bool SamePath(string a, string b)
        {
            if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return false;
            return string.Equals(NormPath(a).Normalize(NormalizationForm.FormC), NormPath(b).Normalize(NormalizationForm.FormC), StringComparison.OrdinalIgnoreCase);
        }

        // ----- permissions: what a task may do unattended -----
        public static readonly string[] DefaultCommands = {
            "npm test *", "npm run test *", "npm run build *", "npm run lint *", "npx tsc *",
            "dotnet build *", "dotnet test *", "pytest *", "python -m pytest *",
            "cargo build *", "cargo test *", "go build *", "go test *",
            "git status *", "git diff *", "git log *" };
        static readonly string[] CommitCmds = { "git commit *" };
        static readonly string[] PushCmds = { "git push *" };
        static readonly string[] DeleteCmds = { "rm *", "rmdir *", "del *", "rd *", "Remove-Item *", "git clean *", "git reset --hard *", "git checkout -- *", "git restore *" };
        static readonly Regex RuleRx = new Regex(@"^[A-Za-z][A-Za-z0-9_]*(\(.+\))?$");

        // One line per allowed command ("npm test *"), or a full Claude Code rule ("WebFetch(domain:docs.rs)").
        public static string CheckCommand(string line)
        {
            string s = (line ?? "").Trim();
            if (s == "") return null;
            if (s == "*" || s.StartsWith("* ")) return "\"" + s + "\" would allow every command.";
            if (s.Contains("(") && !RuleRx.IsMatch(s)) return "\"" + s + "\" isn't a valid rule (Tool(pattern)).";
            if (s.IndexOfAny(new[] { '\r', '\n', '\0' }) >= 0) return "Rules must be single lines.";
            return null;
        }

        static bool IsRule(string s) { return RuleRx.IsMatch(s) && s.Contains("("); }

        // Allow rules for both shell tools (Claude Code on Windows has Bash and PowerShell); deny rules for the
        // actions that need separate authorization. Deny always wins over allow, also over the user's own settings.
        public static void Rules(QueueTask t, List<string> allow, List<string> deny)
        {
            foreach (var c in t.AllowCommands)
            {
                string s = (c ?? "").Trim();
                if (s == "" || CheckCommand(s) != null) continue;
                if (IsRule(s)) allow.Add(s); else { allow.Add("Bash(" + s + ")"); allow.Add("PowerShell(" + s + ")"); }
            }
            Action<string[], bool> gate = (cmds, ok) =>
            {
                foreach (var c in cmds)
                {
                    var target = ok ? allow : deny;
                    target.Add("Bash(" + c + ")"); target.Add("PowerShell(" + c + ")");
                }
            };
            gate(CommitCmds, t.AllowCommit);
            gate(PushCmds, t.AllowPush);
            gate(DeleteCmds, t.AllowDelete);
        }

        public static string ModeLabel(string mode)
        {
            switch (mode) { case "dontAsk": return "Only the allowed commands (no file edits)"; case "plan": return "Read and plan only (no edits, no commands)"; default: return "Edit files in the project + allowed commands"; }
        }

        public static string PermissionSummary(QueueTask t)
        {
            var sb = new StringBuilder();
            sb.Append(ModeLabel(t.PermissionMode)).Append(".");
            if (t.PermissionMode != "plan")
            {
                var cmds = new List<string>();
                foreach (var c in t.AllowCommands) if ((c ?? "").Trim() != "") cmds.Add(c.Trim());
                sb.Append(" Commands: read-only ones").Append(cmds.Count > 0 ? ", " + string.Join(", ", cmds) : "").Append(".");
            }
            var extra = new List<string>();
            if (t.AllowCommit) extra.Add("git commit"); if (t.AllowPush) extra.Add("git push"); if (t.AllowDelete) extra.Add("deleting files");
            sb.Append(extra.Count > 0 ? " Also authorized: " + string.Join(", ", extra) + "." : " Commits, pushes and deletions are denied.");
            sb.Append(t.Runner == "headless"
                ? " Anything else (network, deploys, other tools, questions) is denied automatically and the task is marked Needs attention."
                : " Anything else (network, deploys, other tools) makes Claude ask you in the task's VS Code terminal; the task waits as Needs you until you answer there.");
            return sb.ToString();
        }

        public static string PermissionShort(QueueTask t)
        {
            int n = 0; foreach (var c in t.AllowCommands) if ((c ?? "").Trim() != "") n++;
            var extra = new List<string>();
            if (t.AllowCommit) extra.Add("commit"); if (t.AllowPush) extra.Add("push"); if (t.AllowDelete) extra.Add("delete");
            return ModeLabel(t.PermissionMode) + (t.PermissionMode != "plan" ? " (" + n + " allowed command patterns)" : "") +
                "; " + (extra.Count > 0 ? "also authorized: " + string.Join(", ", extra) : "no commit, push or delete") +
                (t.Runner == "headless" ? "; everything else is denied." : "; anything else is asked in VS Code.");
        }

        public static string ContinuationPrompt(string why)
        {
            return "Continue the task from your previous turn in this conversation. It stopped early" + (why != "" ? " (" + why + ")" : "") +
                ". First check what is already done (files, test results) and don't repeat completed steps. When you finish, summarise what you did and what you verified.";
        }

        public static string FirstLine(string s, int max)
        {
            foreach (var raw in (s ?? "").Split('\n'))
            {
                string l = raw.Trim().TrimStart('#', '*', '-', ' ').Trim();
                if (l == "") continue;
                return l.Length > max ? l.Substring(0, max - 1) + "…" : l;
            }
            return "";
        }
    }

    // ---------- reading Claude's stream-json output ----------
    public class RunReport
    {
        public string SessionId = "", ApiKeySource = null, Subtype = "", ResultText = "";
        public bool HasResult, IsError;
        public int ApiErrorStatus, Turns;
        public double DurationMs;
        public List<string> Denials = new List<string>(), Errors = new List<string>(), RetryErrors = new List<string>();
        public DateTime RejectedResetUtc;   // reset time of a limit Claude reported as exhausted ("rejected")
        public string RejectedType = "";
        public StringBuilder Readable = new StringBuilder();
    }

    public static class RunParser
    {
        static string S(Dictionary<string, object> d, string k) { object v; return d != null && d.TryGetValue(k, out v) && v != null ? Convert.ToString(v, CultureInfo.InvariantCulture) : ""; }
        static Dictionary<string, object> D(Dictionary<string, object> d, string k) { object v; return d != null && d.TryGetValue(k, out v) ? v as Dictionary<string, object> : null; }
        static object[] A(Dictionary<string, object> d, string k) { object v; return d != null && d.TryGetValue(k, out v) ? v as object[] : null; }

        public static void Feed(RunReport r, string line)
        {
            line = line.Trim();
            if (line == "" || line[0] != '{') { if (line != "") r.Readable.Append(line).Append("\r\n"); return; }
            Dictionary<string, object> o;
            try { o = QueueStore.Ser().DeserializeObject(line) as Dictionary<string, object>; } catch { return; }
            if (o == null) return;
            string type = S(o, "type"), sub = S(o, "subtype");
            if (S(o, "session_id") != "") r.SessionId = S(o, "session_id");
            switch (type)
            {
                case "system":
                    if (sub == "init")
                    {
                        object v; r.ApiKeySource = o.TryGetValue("apiKeySource", out v) && v != null ? v.ToString() : "";
                        r.Readable.Append("▶ Claude Code ").Append(S(o, "claude_code_version")).Append(" · ").Append(S(o, "model")).Append(" · permissions: ").Append(S(o, "permissionMode")).Append("\r\n");
                    }
                    else if (sub == "permission_denied")
                        r.Readable.Append("⛔ Blocked by permissions: ").Append(S(o, "tool_name")).Append("\r\n");
                    else if (sub == "api_retry")
                    {
                        string e = S(o, "error");
                        if (e != "") r.RetryErrors.Add(e);
                        r.Readable.Append("↻ API retry ").Append(S(o, "attempt")).Append("/").Append(S(o, "max_retries")).Append(": ").Append(e).Append("\r\n");
                    }
                    break;
                case "assistant":
                    var content = A(D(o, "message"), "content");
                    if (content == null) break;
                    foreach (var item in content)
                    {
                        var c = item as Dictionary<string, object>;
                        if (c == null) continue;
                        string ct = S(c, "type");
                        if (ct == "text" && S(c, "text").Trim() != "") r.Readable.Append(S(c, "text").Trim()).Append("\r\n\r\n");
                        else if (ct == "tool_use") r.Readable.Append("• ").Append(S(c, "name")).Append(ToolBrief(D(c, "input"))).Append("\r\n");
                    }
                    break;
                case "rate_limit_event":
                    var info = D(o, "rate_limit_info");
                    if (info != null && S(info, "status") == "rejected")
                    {
                        long secs;
                        if (long.TryParse(S(info, "resetsAt"), out secs) && secs > 0) r.RejectedResetUtc = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(secs);
                        r.RejectedType = S(info, "rateLimitType");
                        r.Readable.Append("⏸ Usage limit reached (").Append(r.RejectedType).Append(")").Append(r.RejectedResetUtc != DateTime.MinValue ? ", resets " + Sched.LocalText(r.RejectedResetUtc) : "").Append("\r\n");
                    }
                    break;
                case "result":
                    r.HasResult = true;
                    r.Subtype = sub;
                    r.IsError = S(o, "is_error").ToLowerInvariant() == "true";
                    r.ResultText = S(o, "result");
                    int st; if (int.TryParse(S(o, "api_error_status"), out st)) r.ApiErrorStatus = st;
                    int n; if (int.TryParse(S(o, "num_turns"), out n)) r.Turns = n;
                    double ms; if (double.TryParse(S(o, "duration_ms"), NumberStyles.Float, CultureInfo.InvariantCulture, out ms)) r.DurationMs = ms;
                    var errs = A(o, "errors");
                    if (errs != null) foreach (var e in errs) if (e != null) r.Errors.Add(e.ToString());
                    var den = A(o, "permission_denials");
                    if (den != null)
                        foreach (var item in den)
                        {
                            var dd = item as Dictionary<string, object>;
                            if (dd != null) r.Denials.Add(S(dd, "tool_name") + ToolBrief(D(dd, "tool_input")));
                        }
                    r.Readable.Append("■ Result: ").Append(sub).Append(r.IsError ? " (error)" : "").Append(" · ").Append(r.Turns).Append(" turns · ")
                        .Append(Math.Round(r.DurationMs / 1000)).Append(" s\r\n");
                    if (r.ResultText != "" && r.IsError) r.Readable.Append(r.ResultText).Append("\r\n");
                    foreach (var e in r.Errors) r.Readable.Append("  ").Append(e).Append("\r\n");
                    break;
            }
        }

        // ": npm test" / ": src\app.ts" / ": Which database?" - the one field that says what a tool call was.
        public static string ToolBrief(Dictionary<string, object> input)
        {
            if (input == null) return "";
            foreach (var k in new[] { "command", "file_path", "path", "pattern", "url", "query", "description" })
            {
                string v = S(input, k);
                if (v != "") { v = v.Replace("\r", " ").Replace("\n", " "); return ": " + (v.Length > 200 ? v.Substring(0, 199) + "…" : v); }
            }
            var qs = A(input, "questions");
            if (qs != null && qs.Length > 0) { var q = qs[0] as Dictionary<string, object>; if (q != null) return ": " + S(q, "question"); }
            return "";
        }

        public static RunReport ParseFile(string path)
        {
            var r = new RunReport();
            if (!File.Exists(path)) return r;
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var sr = new StreamReader(fs, new UTF8Encoding(false)))
            {
                string line;
                while ((line = sr.ReadLine()) != null) Feed(r, line);
            }
            return r;
        }
    }

    public class Verdict { public string Kind = "", Message = ""; public DateTime ResetUtc; public bool ResetReported; }

    public static class Outcome
    {
        static readonly Regex LimitRx = new Regex(@"(hit|reached|exceeded|out of)\b.{0,30}\b(usage|session|weekly|5-hour|opus|sonnet|fable)?\s*limit|usage limit|limit (reached|exceeded)|rate_limit_error", RegexOptions.IgnoreCase);
        static readonly Regex NotUsageRx = new Regex(@"not your usage limit|temporarily limiting", RegexOptions.IgnoreCase);
        static readonly Regex AuthRx = new Regex(@"authentication_failed|not logged in|please run /login|invalid api key|oauth token|token (has )?expired|unauthori[sz]ed|\b401\b|/login", RegexOptions.IgnoreCase);
        static readonly Regex BillingRx = new Regex(@"billing_error|credit balance|payment required|\b402\b", RegexOptions.IgnoreCase);
        static readonly Regex NetRx = new Regex(@"ENOTFOUND|ECONNRESET|ECONNREFUSED|ETIMEDOUT|EAI_AGAIN|fetch failed|connection error|socket hang up|network|overloaded|server_error|temporarily limiting|\b(500|502|503|504|529)\b", RegexOptions.IgnoreCase);

        // What a finished run means. exitCode = int.MinValue when unknown. An exit code alone is never success:
        // "completed" needs Claude's own success result, and it still only means "Claude reported it's done".
        public static Verdict Classify(RunReport r, int exitCode, string stderr, bool canceled, bool apiKeyGuard, DateTime nowUtc)
        {
            var v = new Verdict();
            if (apiKeyGuard)
            {
                v.Kind = "api_key";
                v.Message = "Stopped before it started: Claude Code would have used " + (r.ApiKeySource ?? "an API key") + " (API billing) instead of your Claude subscription. Remove that credential or allow API billing in config.json (\"queueAllowApiBilling\").";
                return v;
            }
            if (canceled) { v.Kind = "canceled"; v.Message = "Canceled. Partial output is kept below. File edits Claude already made are not undone."; return v; }
            string text = r.ResultText + "\n" + string.Join("\n", r.Errors) + "\n" + string.Join("\n", r.RetryErrors) + "\n" + (stderr ?? "");
            bool success = r.HasResult && !r.IsError && r.Subtype == "success";
            if (success && r.RejectedResetUtc == DateTime.MinValue)
            {
                if (r.Denials.Count > 0)
                {
                    v.Kind = "denied";
                    v.Message = "Claude finished, but " + r.Denials.Count + " action(s) were blocked by the permission scope: " + string.Join("; ", r.Denials.ToArray(), 0, Math.Min(3, r.Denials.Count)) +
                        (r.Denials.Count > 3 ? "; …" : "") + ". Review the result, then continue the conversation yourself or mark it completed.";
                    return v;
                }
                v.Kind = "completed";
                v.Message = "Run finished and Claude reported its result (below). That's Claude's own report: the work isn't independently verified.";
                return v;
            }
            bool limit = r.RejectedResetUtc != DateTime.MinValue || (LimitRx.IsMatch(text) && !NotUsageRx.IsMatch(text)) || (r.ApiErrorStatus == 429 && !NotUsageRx.IsMatch(text));
            if (limit)
            {
                v.Kind = "usage_limit";
                if (r.RejectedResetUtc > nowUtc) { v.ResetUtc = r.RejectedResetUtc; v.ResetReported = true; }
                else
                {
                    var local = Sched.ParseResetText(text, nowUtc.ToLocalTime());
                    if (local.HasValue) { v.ResetUtc = Sched.LocalToUtc(local.Value, TimeZoneInfo.Local); v.ResetReported = true; }
                }
                v.Message = "Usage limit reached" + (v.ResetReported ? "; Claude reported it resets " + Sched.LocalText(v.ResetUtc) : "; Claude didn't report a reset time") + ".";
                return v;
            }
            if (AuthRx.IsMatch(text) || r.ApiErrorStatus == 401 || r.ApiErrorStatus == 403)
            { v.Kind = "auth"; v.Message = "Claude Code isn't signed in (or its sign-in expired). Run `claude` in a terminal and use /login, then retry. Details: " + Sched.FirstLine(text, 200); return v; }
            if (BillingRx.IsMatch(text))
            { v.Kind = "billing"; v.Message = "Billing problem reported by Claude Code: " + Sched.FirstLine(text, 200); return v; }
            if (NetRx.IsMatch(text) || r.ApiErrorStatus >= 500)
            { v.Kind = "network"; v.Message = "Network or service problem: " + Sched.FirstLine(text, 200); return v; }
            if (!r.HasResult)
            {
                if (exitCode == int.MinValue || exitCode == 0)
                { v.Kind = "interrupted"; v.Message = "The run ended without Claude's final result (the app or Claude Code was interrupted), so its outcome is uncertain. Check the conversation before continuing."; return v; }
                v.Kind = "failed"; v.Message = "Claude Code exited with code " + exitCode + " without a result. " + Sched.FirstLine(stderr, 300); return v;
            }
            v.Kind = "failed";
            v.Message = "Claude ended with an error (" + r.Subtype + "): " + Sched.FirstLine(r.ResultText != "" ? r.ResultText : string.Join(" ", r.Errors), 300);
            return v;
        }
    }

    // ---------- Claude Code's local session transcripts (conversation discovery and safety checks) ----------
    public class SessionMeta
    {
        public string Id = "", Cwd = "", Title = "", FirstPrompt = "", Entrypoint = "", File = "";
        public DateTime LastUtc;
        public string Where { get { return Entrypoint == "claude-vscode" ? "VS Code" : Entrypoint == "sdk-cli" ? "scheduled / -p" : Entrypoint == "cli" ? "terminal" : Entrypoint; } }
        public string Display
        {
            get
            {
                string t = Title != "" ? Title : FirstPrompt != "" ? FirstPrompt : "(no prompt yet)";
                if (t.Length > 70) t = t.Substring(0, 69) + "…";
                return t + "  ·  " + Sched.LocalText(LastUtc) + (Where != "" ? "  ·  " + Where : "") + "  ·  " + Id.Substring(0, 8);
            }
        }
    }

    public static class Sessions
    {
        // The documented layout is ~/.claude/projects/<cwd with every non-alphanumeric character as '-'>/<id>.jsonl.
        // The line format inside isn't documented, so reading titles is best effort and failures just show less.
        public static string ProjectsDir()
        {
            string c = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR");
            if (string.IsNullOrEmpty(c)) c = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude");
            return Path.Combine(c, "projects");
        }

        public static string Mangle(string path)
        {
            var sb = new StringBuilder();
            foreach (char ch in Sched.NormPath(path)) sb.Append((ch < 128 && char.IsLetterOrDigit(ch)) ? ch : '-');
            return sb.ToString();
        }

        public static string Find(string projectsDir, string project, string sid)
        {
            if (!Sched.IsGuid(sid)) return null;
            try
            {
                string p = Path.Combine(projectsDir, Mangle(project), sid + ".jsonl");
                if (File.Exists(p)) return p;
                if (!Directory.Exists(projectsDir)) return null;
                foreach (var d in Directory.GetDirectories(projectsDir))
                {
                    p = Path.Combine(d, sid + ".jsonl");
                    if (File.Exists(p)) return p;
                }
            }
            catch { }
            return null;
        }

        public static SessionMeta Read(string file)
        {
            var m = new SessionMeta { File = file, Id = Path.GetFileNameWithoutExtension(file) };
            try
            {
                m.LastUtc = File.GetLastWriteTimeUtc(file);
                using (var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                {
                    var ser = QueueStore.Ser();
                    Action<string> take = line =>
                    {
                        if (!line.StartsWith("{")) return;
                        Dictionary<string, object> o;
                        try { o = ser.DeserializeObject(line) as Dictionary<string, object>; } catch { return; }
                        if (o == null) return;
                        object v;
                        string type = o.TryGetValue("type", out v) && v != null ? v.ToString() : "";
                        if (m.Cwd == "" && o.TryGetValue("cwd", out v) && v != null) m.Cwd = v.ToString();
                        if (m.Entrypoint == "" && o.TryGetValue("entrypoint", out v) && v != null) m.Entrypoint = v.ToString();
                        if (type == "custom-title" && o.TryGetValue("customTitle", out v) && v != null) m.Title = v.ToString();
                        else if (type == "ai-title" && o.TryGetValue("aiTitle", out v) && v != null && m.Title == "") m.Title = v.ToString();
                        else if (type == "summary" && o.TryGetValue("summary", out v) && v != null && m.Title == "") m.Title = v.ToString();
                        else if (type == "user" && m.FirstPrompt == "" && o.TryGetValue("message", out v))
                        {
                            var msg = v as Dictionary<string, object>;
                            object c = null;
                            if (msg != null) msg.TryGetValue("content", out c);
                            string text = c as string;
                            var arr = c as object[];
                            if (text == null && arr != null)
                                foreach (var it in arr) { var d = it as Dictionary<string, object>; object tv; if (d != null && d.TryGetValue("text", out tv) && tv != null) { text = tv.ToString(); break; } }
                            if (text != null && !text.TrimStart().StartsWith("<")) m.FirstPrompt = text.Replace("\r", " ").Replace("\n", " ").Trim();
                        }
                    };
                    foreach (var line in Lines(fs, 0, 512 * 1024)) take(line);
                    if (fs.Length > 512 * 1024) foreach (var line in Lines(fs, Math.Max(512 * 1024, fs.Length - 128 * 1024), 128 * 1024)) take(line);
                }
            }
            catch { }
            return m;
        }

        static List<string> Lines(FileStream fs, long from, int max)
        {
            var buf = new byte[(int)Math.Min(max, Math.Max(0, fs.Length - from))];
            fs.Seek(from, SeekOrigin.Begin);
            int n = 0, r;
            while (n < buf.Length && (r = fs.Read(buf, n, buf.Length - n)) > 0) n += r;
            var list = new List<string>(Encoding.UTF8.GetString(buf, 0, n).Split('\n'));
            if (from > 0 && list.Count > 0) list.RemoveAt(0);                     // first line is cut
            if (fs.Length > from + n && list.Count > 0) list.RemoveAt(list.Count - 1); // last line is cut
            return list;
        }

        // Conversations whose transcript says they ran in this project folder, newest first.
        public static List<SessionMeta> List(string projectsDir, string project, int max)
        {
            var list = new List<SessionMeta>();
            try
            {
                string dir = Path.Combine(projectsDir, Mangle(project));
                if (!Directory.Exists(dir)) return list;
                var files = new List<FileInfo>(new DirectoryInfo(dir).GetFiles("*.jsonl"));
                files.Sort((a, b) => b.LastWriteTimeUtc.CompareTo(a.LastWriteTimeUtc));
                foreach (var f in files)
                {
                    if (list.Count >= max) break;
                    if (!Sched.IsGuid(Path.GetFileNameWithoutExtension(f.Name))) continue;
                    var m = Read(f.FullName);
                    if (m.Cwd == "" || Sched.SamePath(m.Cwd, project)) list.Add(m);
                }
            }
            catch { }
            return list;
        }

        // What happened in a conversation since `sinceUtc` (best effort: the transcript format isn't documented).
        // Text: a readable log; LastReply: Claude's last message; Pending: the newest tool call still waiting for its
        // result, which is what Claude is asking about while the hooks say it needs you.
        public static Activity ReadActivity(string file, DateTime sinceUtc, string skipPrompt)
        {
            var act = new Activity();
            if (file == null || !File.Exists(file)) return act;
            var rep = new RunReport();
            var open = new List<KeyValuePair<string, string>>();
            bool skipped = false;
            try
            {
                using (var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                {
                    const int Max = 4 * 1024 * 1024;
                    var ser = QueueStore.Ser();
                    foreach (var line in Lines(fs, Math.Max(0, fs.Length - Max), Max))
                    {
                        if (!line.StartsWith("{")) continue;
                        Dictionary<string, object> o;
                        try { o = ser.DeserializeObject(line) as Dictionary<string, object>; } catch { continue; }
                        if (o == null) continue;
                        object v;
                        DateTime ts;
                        if (o.TryGetValue("timestamp", out v) && v != null && DateTime.TryParse(v.ToString(), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out ts) && ts < sinceUtc) continue;
                        string type = o.TryGetValue("type", out v) && v != null ? v.ToString() : "";
                        var msg = o.TryGetValue("message", out v) ? v as Dictionary<string, object> : null;
                        object c = null;
                        if (msg != null) msg.TryGetValue("content", out c);
                        if (type == "assistant")
                        {
                            RunParser.Feed(rep, line);
                            var arr = c as object[];
                            if (arr != null)
                                foreach (var it in arr)
                                {
                                    var d = it as Dictionary<string, object>;
                                    if (d == null) continue;
                                    string ct = d.TryGetValue("type", out v) && v != null ? v.ToString() : "";
                                    if (ct == "text" && d.TryGetValue("text", out v) && v != null && v.ToString().Trim() != "") act.LastReply = v.ToString().Trim();
                                    else if (ct == "tool_use" && d.TryGetValue("id", out v) && v != null)
                                    {
                                        object nm, inp; d.TryGetValue("name", out nm); d.TryGetValue("input", out inp);
                                        open.Add(new KeyValuePair<string, string>(v.ToString(), (nm ?? "").ToString() + RunParser.ToolBrief(inp as Dictionary<string, object>)));
                                    }
                                }
                        }
                        else if (type == "user")
                        {
                            string text = c as string;
                            var arr = c as object[];
                            if (arr != null)
                                foreach (var it in arr)
                                {
                                    var d = it as Dictionary<string, object>;
                                    if (d == null) continue;
                                    string ct = d.TryGetValue("type", out v) && v != null ? v.ToString() : "";
                                    if (ct == "tool_result" && d.TryGetValue("tool_use_id", out v) && v != null) { string tid = v.ToString(); open.RemoveAll(k => k.Key == tid); }
                                    else if (ct == "text" && d.TryGetValue("text", out v) && v != null && text == null) text = v.ToString();
                                }
                            if (text == null || text.TrimStart().StartsWith("<")) continue;
                            if (!skipped && skipPrompt != null && text.Trim() == skipPrompt.Trim()) { skipped = true; continue; }
                            rep.Readable.Append("› You: ").Append(text.Trim()).Append("\r\n\r\n");
                        }
                    }
                }
            }
            catch { }
            act.Text = rep.Readable.ToString();
            if (open.Count > 0) act.Pending = open[open.Count - 1].Value;
            return act;
        }
    }

    public class Activity { public string Text = "", LastReply = "", Pending = ""; }

    public class ActiveSession { public string SessionId = "", Cwd = "", Status = "", Kind = ""; public int Pid; }

    // ---------- the engine ----------
    public class QueueEngine : IDisposable
    {
        public readonly QueueStore Store;
        public readonly string RunsDir, SessionsStateDir;
        public QueueData Data { get; private set; }
        public Func<DateTime> Now = () => DateTime.UtcNow;
        public string[] ClaudeCommand;                      // null: find claude.exe; tests use a mock
        public string ProjectsDir = Sessions.ProjectsDir();
        public bool AllowApiBilling, KeepAwake = true;
        public int HistoryDays = 30;
        public TimeSpan Grace = TimeSpan.FromSeconds(60);   // overdue tasks wait this long after start / wake, so they can be skipped
        public Func<DateTime> UsageBlockedUntil;            // from Claude Usage: MinValue when no exhausted limit is known
        public Func<List<ActiveSession>> ActiveSessionsOverride;
        public event Action Changed;
        public event Action<QueueTask> Notify;
        public string BusyNote = "";
        public readonly VsBridge Bridge;
        public TimeSpan OpenTimeout = TimeSpan.FromSeconds(90);     // VS Code must claim a launch within this
        public TimeSpan StartNotice = TimeSpan.FromMinutes(2);      // then "Claude hasn't started yet: check the terminal"

        readonly string instance = Guid.NewGuid().ToString("N");
        readonly int myPid;
        readonly DateTime myStart;
        Run run;
        DateTime graceUntil, lastTick, lastPrune, knownBlockedUntil, activeAt = DateTime.MinValue;
        Stopwatch clock = Stopwatch.StartNew();
        List<ActiveSession> activeCache;
        bool awake;

        class Run
        {
            public string TaskId = "", Log = "", Err = "";
            public int N, Pid;
            public DateTime PidStarted;
            public IntPtr Process = IntPtr.Zero, Job = IntPtr.Zero;
            public long Offset;
            public byte[] Pending = new byte[0];
            public RunReport Report = new RunReport();
            public bool Canceled, Guard, Vs;
            public DateTime SeenUtc;   // VS Code: last time its terminal was seen open
        }

        public QueueEngine(string root)
        {
            string dir = Path.Combine(root, "state\\queue");
            Store = new QueueStore(dir);
            RunsDir = Path.Combine(dir, "runs");
            SessionsStateDir = Path.Combine(root, "state\\sessions");
            Directory.CreateDirectory(RunsDir);
            Bridge = new VsBridge(root);
            Data = Store.Read();
            var me = System.Diagnostics.Process.GetCurrentProcess();
            myPid = me.Id;
            myStart = me.StartTime.ToUniversalTime();
            graceUntil = DateTime.UtcNow + Grace;
        }

        public void Start() { graceUntil = Now() + Grace; lastTick = Now(); clock.Restart(); }

        // After sleep/hibernate (or a clock change) overdue tasks wait a little so they can be skipped.
        public void NoteWake() { graceUntil = Now() + Grace; activeCache = null; }

        public bool HasRun { get { return run != null; } }
        public string RunningTaskId { get { return run != null ? run.TaskId : ""; } }
        public string LiveOutput(string id) { return run != null && run.TaskId == id ? run.Report.Readable.ToString() : null; }

        public string OutputOf(QueueTask t, int attemptIndex)
        {
            if (t == null) return "";
            if (attemptIndex < 0) attemptIndex = t.Attempts.Count - 1;
            if (attemptIndex < 0) return "";
            var a = t.Attempts[attemptIndex];
            if (run != null && run.TaskId == t.Id && run.N == a.N) return run.Report.Readable.ToString();
            string log = LogPath(t.Id, a.N);
            if (!File.Exists(log)) return "(no output recorded)";
            try { return RunParser.ParseFile(log).Readable.ToString(); } catch (Exception e) { return "(couldn't read the output: " + e.Message + ")"; }
        }

        public string LogPath(string id, int n) { return Path.Combine(RunsDir, id, n + ".jsonl"); }
        string ErrPath(string id, int n) { return Path.Combine(RunsDir, id, n + ".err.txt"); }

        public QueueTask TaskForSession(string sid)
        {
            if (string.IsNullOrEmpty(sid)) return null;
            foreach (var t in Data.Tasks) if (t.SessionId == sid) return t;
            return null;
        }

        void Raise() { var h = Changed; if (h != null) h(); }

        QueueData Mutate(Func<QueueData, bool> f)
        {
            var notify = new List<QueueTask>();
            Data = Store.Mutate(d =>
            {
                bool dirty = f(d);
                foreach (var t in d.Tasks)
                {
                    bool loud = t.State == QState.Completed || t.State == QState.Failed || t.State == QState.NeedsAttention || t.State == QState.WaitingUsage || t.State == QState.Blocked;
                    if (loud && t.Notified != t.State) { t.Notified = t.State; notify.Add(t); dirty = true; }
                }
                return dirty;
            });
            var h = Notify;
            if (h != null) foreach (var t in notify) try { h(t); } catch { }
            Raise();
            return Data;
        }

        // ---------- user actions (each one is a single locked read-change-write) ----------
        public string Validate(QueueTask t, IList<QueueTask> all)
        {
            if (t.Title.Trim() == "") return "Give the task a title.";
            if (t.Project.Trim() == "" || !Directory.Exists(t.Project)) return "Choose an existing project folder.";
            if (t.Prompt.Trim() == "") return "Write the prompt.";
            if (t.Conversation == "resume" && !Sched.IsGuid(t.ResumeSession)) return "Choose the conversation to continue (or paste its session ID).";
            if (t.Conversation == "predecessor" && t.Schedule != "after") return "To continue another task's conversation, schedule it to run after that task.";
            if (t.Schedule == "after")
            {
                bool found = false; foreach (var x in all) if (x.Id == t.After && x.Id != t.Id) found = true;
                if (!found) return "Choose the task it should run after.";
                if (Sched.CreatesCycle(all, t.Id, t.After)) return "That would make tasks wait for each other in a loop.";
            }
            if (t.Schedule == "at" && t.RunAtUtc == DateTime.MinValue) return "Choose the date and time.";
            if (t.Model != "" && !Regex.IsMatch(t.Model, @"^[A-Za-z0-9._\[\]-]{1,80}$")) return "The model name can only contain letters, digits and . _ - [ ].";
            if (t.PermissionMode != "acceptEdits" && t.PermissionMode != "dontAsk" && t.PermissionMode != "plan") return "Unknown permission mode.";
            foreach (var c in t.AllowCommands) { string e = Sched.CheckCommand(c); if (e != null) return e; }
            return null;
        }

        // Adds or replaces a task (not while it runs). expectedRevision guards against saving over a change made
        // meanwhile (e.g. the task started while the editor was open). Returns an error or null.
        public string Save(QueueTask t, int expectedRevision)
        {
            string err = null;
            Mutate(d =>
            {
                // A draft may be incomplete (it never runs); only a loop between tasks is refused. Enqueue checks the rest.
                err = t.State == QState.Draft ? (t.Schedule == "after" && t.After != "" && Sched.CreatesCycle(d.Tasks, t.Id, t.After) ? "That would make tasks wait for each other in a loop." : null) : Validate(t, d.Tasks);
                if (err != null) return false;
                var cur = d.Find(t.Id);
                if (cur != null && cur.Revision != expectedRevision) { err = "The task changed while you were editing it (" + QState.Label(cur.State) + "). Close the editor and try again."; return false; }
                if (cur != null && cur.State == QState.Running) { err = "The task is running; its prompt can't change now. Duplicate it to queue a revised version."; return false; }
                t.Revision = expectedRevision + 1;
                t.UpdatedUtc = Now();
                t.Notified = "";
                t.RunNow = false;
                if (t.State != QState.Draft) { t.State = QState.Scheduled; t.Reason = ""; t.NextRetryUtc = DateTime.MinValue; }
                if (cur == null)
                {
                    t.CreatedUtc = Now();
                    double max = 0; foreach (var x in d.Tasks) max = Math.Max(max, x.Order);
                    t.Order = max + 1;
                    d.Tasks.Add(t);
                }
                else
                {
                    // Keep run history and the conversation; only the definition changes.
                    t.Attempts = cur.Attempts; t.SessionId = cur.SessionId; t.Order = cur.Order; t.CreatedUtc = cur.CreatedUtc;
                    t.ResultText = cur.ResultText; t.Summary = cur.Summary; t.Outcome = cur.Outcome; t.Denials = cur.Denials; t.FinishedUtc = cur.FinishedUtc;
                    d.Tasks[d.Tasks.IndexOf(cur)] = t;
                }
                return true;
            });
            if (err == null) Tick();
            return err;
        }

        public QueueTask Duplicate(string id)
        {
            QueueTask copy = null;
            Mutate(d =>
            {
                var t = d.Find(id);
                if (t == null) return false;
                copy = QueueStore.Clone(t);
                copy.Id = Guid.NewGuid().ToString("N");
                copy.Title = t.Title + " (copy)";
                copy.State = QState.Draft; copy.Reason = "Draft: edit it, then add it to the queue.";
                copy.Attempts = new List<RunAttempt>(); copy.SessionId = ""; copy.ResultText = ""; copy.Summary = ""; copy.Outcome = "";
                copy.Denials = new List<string>(); copy.Notified = ""; copy.RetryCount = 0; copy.NeedsContinue = false; copy.RunNow = false;
                copy.NextRetryUtc = DateTime.MinValue; copy.FinishedUtc = DateTime.MinValue; copy.Revision = 0; copy.CreatedUtc = Now();
                copy.Order = t.Order + 0.5;
                d.Tasks.Add(copy);
                Renumber(d);
                return true;
            });
            return copy;
        }

        static void Renumber(QueueData d)
        {
            d.Tasks.Sort((a, b) => a.Order.CompareTo(b.Order));
            for (int i = 0; i < d.Tasks.Count; i++) d.Tasks[i].Order = i + 1;
        }

        public string Remove(string id)
        {
            string err = null;
            Mutate(d =>
            {
                var t = d.Find(id);
                if (t == null) return false;
                if (t.State == QState.Running) { err = "Cancel the task before removing it."; return false; }
                d.Tasks.Remove(t);
                return true;
            });
            if (err == null) { DeleteRuns(id); Tick(); }
            return err;
        }

        public void Move(string id, int delta)
        {
            Mutate(d =>
            {
                Renumber(d);
                var t = d.Find(id);
                if (t == null) return false;
                int i = d.Tasks.IndexOf(t), j = i + Math.Sign(delta);
                if (j < 0 || j >= d.Tasks.Count) return false;
                double o = d.Tasks[j].Order; d.Tasks[j].Order = t.Order; t.Order = o;
                Renumber(d);
                return true;
            });
        }

        // Runs as soon as nothing else is running: skips its time / usage wait, but not its predecessor.
        public void RunNow(string id) { Edit(id, t => { if (t.State == QState.Running || QState.Finished(t.State)) return false; t.RunNow = true; if (t.State == QState.Draft || t.State == QState.NeedsAttention) t.State = QState.Scheduled; t.NextRetryUtc = DateTime.MinValue; t.Notified = ""; return true; }); Tick(); }

        // A draft joins the queue once it's complete. Returns what's missing, or null.
        public string Enqueue(string id)
        {
            string err = null;
            Mutate(d =>
            {
                var t = d.Find(id);
                if (t == null || t.State != QState.Draft) return false;
                err = Validate(t, d.Tasks);
                if (err != null) return false;
                t.State = QState.Scheduled; t.Reason = ""; t.Revision++;
                return true;
            });
            if (err == null) Tick();
            return err;
        }
        public void ToDraft(string id) { Edit(id, t => { if (!QState.Pending(t.State)) return false; t.State = QState.Draft; t.RunNow = false; t.Reason = "Draft: won't run until you add it to the queue."; return true; }); }
        public void Skip(string id) { Edit(id, t => { if (!QState.Pending(t.State) && t.State != QState.Draft) return false; Finish(t, QState.Canceled, "skipped", "Skipped. It won't run."); return true; }); Tick(); }

        // Continue: resume the task's conversation with a "continue where you stopped" prompt (no repeated prompt).
        // Fresh: send the task's prompt again (a new conversation for "new" tasks).
        public void Retry(string id, bool continueConversation)
        {
            Edit(id, t =>
            {
                if (t.State == QState.Running) return false;
                t.NeedsContinue = continueConversation && t.SessionId != "";
                if (!continueConversation && t.Conversation == "new") t.SessionId = "";
                if (t.NeedsContinue && t.ContinueWhy == "") t.ContinueWhy = "the previous run didn't finish";
                t.State = QState.Scheduled; t.RunNow = true; t.Reason = ""; t.Notified = ""; t.RetryCount = 0; t.NextRetryUtc = DateTime.MinValue;
                return true;
            });
            Tick();
        }

        // Your own resolution. Also for a VS Code run (Claude keeps running there; the queue just stops waiting for it).
        public void MarkCompleted(string id)
        {
            if (run != null && run.TaskId == id && run.Vs) run = null;
            Edit(id, t =>
            {
                if (t.State == QState.Running && (t.Last == null || !t.Last.InVsCode)) return false;
                if (t.State == QState.Running) { t.Last.EndedUtc = Now(); t.Last.Outcome = "marked_completed"; t.Last.Phase = "ended"; t.Last.Attention = ""; }
                Finish(t, QState.Completed, "marked_completed", "You marked this completed.");
                t.Notified = QState.Completed;
                return true;
            });
            Tick();
        }

        // Your explicit choice of where a task runs next (VS Code, or headless in the background).
        public void SetRunner(string id, string runner) { Edit(id, t => { if (t.State == QState.Running || (runner != "vscode" && runner != "headless")) return false; t.Runner = runner; return true; }); }

        public void SetRetryTime(string id, DateTime utc) { Edit(id, t => { if (!QState.Pending(t.State)) return false; t.NextRetryUtc = utc; t.ResetSource = "you"; t.Reason = "Waiting for usage; next try " + Sched.LocalText(utc) + " (time you set)."; return true; }); }

        public void SetPaused(bool paused) { Mutate(d => { if (d.Paused == paused) return false; d.Paused = paused; return true; }); Tick(); }

        public int ClearHistory()
        {
            var gone = new List<string>();
            Mutate(d =>
            {
                foreach (var t in d.Tasks) if (QState.Finished(t.State) && !NeededBy(d, t.Id)) gone.Add(t.Id);
                d.Tasks.RemoveAll(t => gone.Contains(t.Id));
                return gone.Count > 0;
            });
            foreach (var id in gone) DeleteRuns(id);
            return gone.Count;
        }

        static bool NeededBy(QueueData d, string id)
        {
            foreach (var t in d.Tasks) if (t.After == id && !QState.Finished(t.State)) return true;
            return false;
        }

        void Edit(string id, Func<QueueTask, bool> f) { Mutate(d => { var t = d.Find(id); return t != null && f(t); }); }

        void DeleteRuns(string id)
        {
            try { string p = Path.Combine(RunsDir, id); if (Directory.Exists(p)) Directory.Delete(p, true); } catch { }
            try { foreach (var f in Directory.GetFiles(Path.Combine(Bridge.Dir, "status"), id + "-*.json")) File.Delete(f); } catch { }
        }

        static void Finish(QueueTask t, string state, string outcome, string reason)
        {
            t.State = state; t.Outcome = outcome; t.Reason = reason; t.RunNow = false; t.NextRetryUtc = DateTime.MinValue;
            if (QState.Finished(state)) t.FinishedUtc = DateTime.UtcNow;
        }

        // Stops the running task's process tree (or marks a waiting task canceled).
        public void Cancel(string id)
        {
            if (run != null && run.TaskId == id && run.Vs)
            {
                var vt = Data.Find(id);
                if (vt != null && vt.Last != null) StopVs(vt.Last);
                run = null;
                Edit(id, x => { if (x.State != QState.Running) return false; x.Last.EndedUtc = Now(); x.Last.Outcome = "canceled"; x.Last.Phase = "ended"; Finish(x, QState.Canceled, "canceled", "Canceled: Claude in the VS Code terminal was stopped. The conversation is kept; file edits Claude already made are not undone."); return true; });
                Tick(); return;
            }
            if (run != null && run.TaskId == id)
            {
                run.Canceled = true; Kill(run);
                if (run.Process != IntPtr.Zero) PN.WaitForSingleObject(run.Process, 5000);
                Poll(); Tick(); return;
            }
            var t = Data.Find(id);
            if (t != null && t.State == QState.Running)
            {
                // Running under another app instance or found after a restart: stop it by its recorded identity.
                var a = t.Last;
                if (a != null && a.InVsCode) StopVs(a);
                else if (a != null) KillTree(a.Pid, a.PidStartedUtc, a.Job);
                Edit(id, x => { if (x.State != QState.Running) return false; Finish(x, QState.Canceled, "canceled", "Canceled. Partial output is kept below. File edits Claude already made are not undone."); return true; });
                return;
            }
            CancelPending(id);
        }

        // Cancels a task that hasn't started, decided inside the locked write: if it started meanwhile, nothing changes
        // (false), so this can never stop a run. The phone's "Cancel task" uses only this.
        public bool CancelPending(string id)
        {
            bool done = false;
            Edit(id, x => { if (!QState.Pending(x.State) && x.State != QState.Draft && x.State != QState.NeedsAttention) return false; Finish(x, QState.Canceled, "canceled", "Canceled before it ran."); done = true; return true; });
            Tick();
            return done;
        }

        // ---------- the scheduler loop ----------
        public void Tick()
        {
            DateTime now = Now();
            // A wall-clock jump (clock changed, or the timer didn't run during sleep) counts like waking up.
            if (lastTick != DateTime.MinValue && Math.Abs(((now - lastTick) - clock.Elapsed).TotalSeconds) > 120) NoteWake();
            lastTick = now; clock.Restart();

            if (run != null) Poll();
            if (run == null) Adopt();
            if (run != null && run.Vs) Poll();   // just picked up again: look at it now
            if ((now - lastPrune).TotalHours >= 1) { lastPrune = now; Prune(); }
            if (run != null) { SetAwake(true); return; }
            SetAwake(false);
            now = Now();   // after finishing a run, so the next one never starts "before" it ended

            DateTime blocked = knownBlockedUntil;
            if (UsageBlockedUntil != null) { try { DateTime u = UsageBlockedUntil(); if (u > blocked) blocked = u; } catch { } }
            QueueTask pick = null;
            Mutate(d =>
            {
                bool dirty = Refresh(d, now, blocked);
                if (d.Paused) return dirty;
                foreach (var t in d.Tasks) if (t.State == QState.Running) return dirty;   // one at a time, across instances too
                var ready = d.Tasks.FindAll(t => t.State == QState.Queued);
                ready.Sort((a, b) => a.RunNow != b.RunNow ? (a.RunNow ? -1 : 1) : a.Order.CompareTo(b.Order));
                foreach (var t in ready)
                {
                    string busy = t.RunNow ? null : ProjectBusy(t, d);
                    if (busy == null) busy = Held(t, d, now);
                    if (busy != null && busy.StartsWith("!")) { Finish(t, QState.NeedsAttention, "conversation", busy.Substring(1)); dirty = true; continue; }
                    if (busy != null) { if (t.Reason != busy) { t.Reason = busy; dirty = true; } continue; }
                    string why;
                    string mode, sid, prompt;
                    if (!Plan(t, d, out mode, out sid, out prompt, out why))
                    {
                        Finish(t, QState.NeedsAttention, "conversation", why);
                        dirty = true;
                        continue;
                    }
                    // Claim it in the same locked write, so no other tick, timer or instance can launch it too.
                    var a = new RunAttempt { N = t.Attempts.Count + 1, SessionId = sid, Mode = mode, Prompt = prompt, StartedUtc = now, Owner = instance, OwnerPid = myPid, OwnerStartedUtc = myStart, Runner = t.Runner == "headless" ? "" : "vscode" };
                    a.Log = Path.Combine(t.Id, a.N + ".jsonl");
                    a.Job = "Local\\ClaudeStatusQueue-" + t.Id + "-" + a.N;
                    t.Attempts.Add(a);
                    t.SessionId = sid;
                    t.State = QState.Running; t.RunNow = false;
                    t.Reason = a.InVsCode ? "Opening VS Code for this project…" : "Running in the background since " + Sched.LocalText(now) + ".";
                    t.Revision++;
                    pick = t;
                    return true;
                }
                return dirty;
            });
            if (pick != null) Launch(pick);
        }

        // Works out each waiting task's state from its schedule, predecessor and the usage situation.
        bool Refresh(QueueData d, DateTime now, DateTime blockedUntil)
        {
            bool dirty = false;
            Renumber(d);
            foreach (var t in d.Tasks)
            {
                if (!QState.Pending(t.State)) continue;
                string state = QState.Queued, reason = "";
                DateTime retry = t.NextRetryUtc;
                if (t.Schedule == "after")
                {
                    var p = d.Find(t.After);
                    if (p == null) { state = QState.Blocked; reason = "Blocked: the task it waits for was removed."; }
                    else if (p.State == QState.Failed || p.State == QState.Canceled || p.State == QState.NeedsAttention || p.State == QState.Blocked)
                    { state = QState.Blocked; reason = "Blocked: \"" + p.Title + "\" " + (p.State == QState.NeedsAttention ? "needs attention" : p.State == QState.Blocked ? "is blocked" : "was " + QState.Label(p.State).ToLowerInvariant()) + ". Fix or complete it first."; }
                    else if (p.State != QState.Completed) { state = QState.Scheduled; reason = "Waits for \"" + p.Title + "\" to finish" + (p.State == QState.Draft ? " (still a draft)" : "") + "."; }
                }
                if (state == QState.Queued && !t.RunNow)
                {
                    if (retry > now)
                    {
                        state = t.State == QState.WaitingUsage ? QState.WaitingUsage : QState.Scheduled;
                        reason = t.Reason != "" ? t.Reason : "Next try " + Sched.LocalText(retry) + ".";
                    }
                    else if (t.Schedule == "at" && t.Attempts.Count == 0)
                    {
                        if (t.RunAtUtc > now) { state = QState.Scheduled; reason = "Starts " + Sched.LocalText(t.RunAtUtc) + "."; }
                        else if ((now - t.RunAtUtc).TotalMinutes > 2)
                        {
                            if (t.IfMissed == "skip") { Finish(t, QState.Canceled, "missed", "Skipped: it was due " + Sched.LocalText(t.RunAtUtc) + " while Claude Status wasn't running or the computer was asleep."); dirty = true; continue; }
                            if (now < graceUntil) { state = QState.Scheduled; reason = "Overdue (was due " + Sched.LocalText(t.RunAtUtc) + "). Starts " + Sched.LocalText(graceUntil) + " unless you skip it."; }
                            else reason = "Overdue (was due " + Sched.LocalText(t.RunAtUtc) + ").";
                        }
                    }
                    else if (t.Schedule == "usage" && t.UsageResetUtc > now) { state = QState.WaitingUsage; reason = "Waiting for usage: you said it resets " + Sched.LocalText(t.UsageResetUtc) + "."; }
                    if (state == QState.Queued && blockedUntil > now)
                    {
                        state = QState.WaitingUsage;
                        reason = "Usage limit reached. Claude reported it resets " + Sched.LocalText(blockedUntil) + "; the task starts after that.";
                    }
                }
                if (state == QState.Queued && reason == "")
                    reason = d.Paused ? "Queue paused." : t.RunNow ? "Starting now." : t.NeedsContinue ? "Ready to continue its conversation." : "Ready; runs when nothing else is running.";
                if (state == QState.Queued && t.State == QState.Queued && t.Reason.StartsWith("Waiting:")) reason = t.Reason;
                if (t.State != state || t.Reason != reason) { t.State = state; t.Reason = reason; dirty = true; }
            }
            return dirty;
        }

        // Decides how to talk to Claude for this run: a new conversation, or resuming one that is verifiably this
        // project's and not open elsewhere. Never "whatever ran last" (no --continue).
        bool Plan(QueueTask t, QueueData d, out string mode, out string sid, out string prompt, out string why)
        {
            mode = "new"; sid = t.SessionId; prompt = t.Prompt; why = "";
            if (!Directory.Exists(t.Project)) { why = "The project folder doesn't exist any more: " + t.Project; return false; }
            if (t.Conversation == "resume") sid = t.ResumeSession;
            else if (t.Conversation == "predecessor")
            {
                var p = d.Find(t.After);
                sid = p != null ? p.SessionId : "";
                if (sid == "") { why = "The task it continues has no conversation to resume."; return false; }
                if (p != null && !Sched.SamePath(p.Project, t.Project)) { why = "The task it continues ran in another project folder (" + p.Project + ")."; return false; }
            }
            if (t.Conversation == "new" && !Sched.IsGuid(sid)) sid = Guid.NewGuid().ToString();
            if (!Sched.IsGuid(sid)) { why = "\"" + sid + "\" isn't a Claude Code session ID."; return false; }
            string file = Sessions.Find(ProjectsDir, t.Project, sid);
            // Claude already ran this conversation in VS Code but saved no transcript: it can't be resumed, and sending the
            // original prompt again would repeat the work. (Run again clears the session first, so it never lands here.)
            string planned = sid;
            if (file == null && t.Attempts.Exists(x => x.InVsCode && x.ClaudePid > 0 && string.Equals(x.SessionId, planned, StringComparison.OrdinalIgnoreCase)))
            { why = "Claude Code didn't save conversation " + sid.Substring(0, 8) + " on this computer, so it can't be continued, and the original prompt isn't sent again by itself. Read the result in its VS Code terminal if it's still open, then Mark completed, or use Run again to start over."; return false; }
            if (t.Conversation != "new" || file != null)
            {
                if (file == null) { why = "Conversation " + sid + " wasn't found on this computer, so it can't be resumed."; return false; }
                var m = Sessions.Read(file);
                if (m.Cwd != "" && !Sched.SamePath(m.Cwd, t.Project)) { why = "Conversation " + sid.Substring(0, 8) + " belongs to another folder (" + m.Cwd + "), not " + t.Project + "."; return false; }
                var act = ActiveFor(sid);
                if (act != null) { why = "That conversation is open in another Claude Code session right now (" + (act.Kind != "" ? act.Kind + ", " : "") + "process " + act.Pid + "). Close it there, then choose Retry."; return false; }
                mode = "resume";
                if (t.Conversation == "new" || t.NeedsContinue) prompt = t.NeedsContinue || t.Attempts.Count > 0 ? Sched.ContinuationPrompt(t.ContinueWhy) : t.Prompt;
            }
            return true;
        }

        List<ActiveSession> Active()
        {
            if (ActiveSessionsOverride != null) { try { return ActiveSessionsOverride(); } catch { return null; } }
            if (activeCache != null && (Now() - activeAt).TotalSeconds < 10) return activeCache;
            activeAt = Now();
            activeCache = ReadAgents();
            return activeCache;
        }

        // `claude agents --json` (documented "for scripting") lists open interactive and background sessions with
        // their folder and busy/idle status. null = couldn't tell.
        List<ActiveSession> ReadAgents()
        {
            try
            {
                var cmd = ResolveClaude(ClaudeCommand);
                if (cmd == null) return null;
                var args = new List<string>(cmd); args.RemoveAt(0); args.Add("agents"); args.Add("--json");
                var psi = new ProcessStartInfo(cmd[0], JoinArgs(args)) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true, StandardOutputEncoding = Encoding.UTF8 };
                using (var p = System.Diagnostics.Process.Start(psi))
                {
                    var outTask = p.StandardOutput.ReadToEndAsync();
                    p.StandardError.ReadToEndAsync();
                    if (!p.WaitForExit(8000)) { try { p.Kill(); } catch { } return null; }
                    if (p.ExitCode != 0) return null;
                    var arr = QueueStore.Ser().DeserializeObject(outTask.Result) as object[];
                    if (arr == null) return null;
                    var list = new List<ActiveSession>();
                    foreach (var it in arr)
                    {
                        var o = it as Dictionary<string, object>;
                        if (o == null) continue;
                        object v; int pid = 0;
                        if (o.TryGetValue("pid", out v) && v != null) int.TryParse(v.ToString(), out pid);
                        list.Add(new ActiveSession { SessionId = o.TryGetValue("sessionId", out v) && v != null ? v.ToString() : "", Cwd = o.TryGetValue("cwd", out v) && v != null ? v.ToString() : "",
                            Status = o.TryGetValue("status", out v) && v != null ? v.ToString() : "", Kind = o.TryGetValue("kind", out v) && v != null ? v.ToString() : "", Pid = pid });
                    }
                    return list;
                }
            }
            catch { return null; }
        }

        // Fallback when `claude agents` can't be asked: the status hooks' own files (state\sessions), which know a
        // session's state and folder name (not the full path).
        List<SessionInfo> HookSessions()
        {
            var list = new List<SessionInfo>();
            try
            {
                var ser = new JavaScriptSerializer();
                foreach (var f in Directory.GetFiles(SessionsStateDir, "*.json"))
                {
                    try
                    {
                        var d = ser.Deserialize<Dictionary<string, object>>(File.ReadAllText(f));
                        var si = Board.Parse(Path.GetFileNameWithoutExtension(f), d, DateTime.MinValue, File.GetLastWriteTimeUtc(f));
                        if ((si.State == Board.Working || si.State == Board.NeedsYou) && WorkerWindows.ProcessAlive(si.ClaudePid, si.ClaudeStarted) != 0) list.Add(si);
                    }
                    catch { }
                }
            }
            catch { }
            return list;
        }

        ActiveSession ActiveFor(string sid)
        {
            var act = Active();
            if (act != null) { foreach (var a in act) if (string.Equals(a.SessionId, sid, StringComparison.OrdinalIgnoreCase)) return a; return null; }
            foreach (var si in HookSessions()) if (string.Equals(si.Id, sid, StringComparison.OrdinalIgnoreCase)) return new ActiveSession { SessionId = sid, Pid = si.ClaudePid, Kind = "status hooks" };
            return null;
        }

        // Manual Claude activity in the same folder defers the task (it keeps its place in line).
        string ProjectBusy(QueueTask t, QueueData d)
        {
            var mine = new HashSet<string>();
            foreach (var x in d.Tasks) if (x.SessionId != "") mine.Add(x.SessionId.ToLowerInvariant());
            var act = Active();
            if (act != null)
            {
                BusyNote = "Manual Claude Code sessions are detected with `claude agents --json`.";
                foreach (var a in act)
                    if (a.Status == "busy" && Sched.SamePath(a.Cwd, t.Project) && !mine.Contains(a.SessionId.ToLowerInvariant()))
                        return "Waiting: Claude is busy in this project in another session (process " + a.Pid + "). It starts when that session goes idle.";
                return null;
            }
            BusyNote = "`claude agents` didn't answer, so only sessions reported by the status hooks (by folder name) are detected. Pause the queue while you work in a project by hand.";
            string folder = Path.GetFileName(Sched.NormPath(t.Project));
            foreach (var si in HookSessions())
                if (string.Equals(si.Project, folder, StringComparison.OrdinalIgnoreCase) && !mine.Contains(si.Id.ToLowerInvariant()))
                    return "Waiting: Claude is busy in a folder named \"" + folder + "\" (status hooks). It starts when that session is done.";
            return null;
        }

        // ---------- running Claude ----------
        public static string[] ResolveClaude(string[] configured)
        {
            if (configured != null && configured.Length > 0 && !string.IsNullOrEmpty(configured[0])) return configured;
            var dirs = new List<string>((Environment.GetEnvironmentVariable("PATH") ?? "").Split(';'));
            dirs.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local\\bin"));
            foreach (var name in new[] { "claude.exe", "claude.cmd" })
                foreach (var dir in dirs)
                {
                    try
                    {
                        string d = dir.Trim().Trim('"');
                        if (d == "") continue;
                        string p = Path.Combine(Environment.ExpandEnvironmentVariables(d), name);
                        if (File.Exists(p)) return new[] { p };
                    }
                    catch { }
                }
            return null;
        }

        public static string QuoteArg(string a)
        {
            if (a.Length > 0 && a.IndexOfAny(new[] { ' ', '\t', '\n', '\v', '"' }) < 0) return a;
            var sb = new StringBuilder("\"");
            for (int i = 0; i < a.Length; i++)
            {
                int bs = 0;
                while (i < a.Length && a[i] == '\\') { bs++; i++; }
                if (i == a.Length) { sb.Append('\\', bs * 2); break; }
                if (a[i] == '"') sb.Append('\\', bs * 2 + 1).Append('"');
                else sb.Append('\\', bs).Append(a[i]);
            }
            return sb.Append('"').ToString();
        }

        public static string JoinArgs(IEnumerable<string> args)
        {
            var parts = new List<string>();
            foreach (var a in args) parts.Add(QuoteArg(a));
            return string.Join(" ", parts);
        }

        // The full command line: only fixed flags, a UUID, our own settings path and a validated model name.
        // A .cmd install needs cmd.exe, so there every argument is checked for cmd metacharacters first.
        public static string CommandLine(string[] claude, List<string> args, out string error)
        {
            error = null;
            var all = new List<string>(claude); all.AddRange(args);
            string exe = all[0];
            if (exe.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase) || exe.EndsWith(".bat", StringComparison.OrdinalIgnoreCase))
            {
                foreach (var a in all) if (a.IndexOfAny("&|<>^%!\"\r\n".ToCharArray()) >= 0) { error = "Can't safely run " + exe + " with the argument " + a + "."; return null; }
                return QuoteArg(Path.Combine(Environment.SystemDirectory, "cmd.exe")) + " /d /s /c \"" + JoinArgs(all) + "\"";
            }
            return JoinArgs(all);
        }

        // The task's permission scope as a Claude Code --settings file (allow + deny rules; deny always wins).
        string WriteSettings(QueueTask t, string name)
        {
            string dir = Path.Combine(RunsDir, t.Id);
            Directory.CreateDirectory(dir);
            string settingsFile = Path.Combine(dir, name + ".settings.json");
            var allow = new List<string>(); var deny = new List<string>();
            Sched.Rules(t, allow, deny);
            var perms = new Dictionary<string, object> { { "allow", allow }, { "deny", deny } };
            File.WriteAllText(settingsFile, Json.Pretty(QueueStore.Ser().Serialize(new Dictionary<string, object> { { "permissions", perms } })), new UTF8Encoding(false));
            return settingsFile;
        }

        void Launch(QueueTask t) { if (t.Last.InVsCode) LaunchVs(t); else LaunchHeadless(t); }

        void LaunchHeadless(QueueTask t)
        {
            var a = t.Last;
            string dir = Path.Combine(RunsDir, t.Id);
            string err = null;
            var r = new Run { TaskId = t.Id, N = a.N, Log = LogPath(t.Id, a.N), Err = ErrPath(t.Id, a.N) };
            try
            {
                string settingsFile = WriteSettings(t, a.N.ToString());
                string promptFile = Path.Combine(dir, a.N + ".prompt.txt");
                File.WriteAllText(promptFile, a.Prompt, new UTF8Encoding(false));

                var claude = ResolveClaude(ClaudeCommand);
                if (claude == null) throw new FileNotFoundException("Claude Code (claude.exe) wasn't found on PATH or in %USERPROFILE%\\.local\\bin.");
                var args = new List<string> { "-p", "--output-format", "stream-json", "--verbose", "--permission-mode", t.PermissionMode, "--permission-prompts", "none", "--settings", settingsFile };
                args.Add(a.Mode == "resume" ? "--resume" : "--session-id"); args.Add(a.SessionId);
                if (t.Model != "") { args.Add("--model"); args.Add(t.Model); }
                string cmdErr;
                string cmdline = CommandLine(claude, args, out cmdErr);
                if (cmdline == null) throw new InvalidOperationException(cmdErr);
                Start(r, cmdline, t.Project, promptFile, a.Job);
            }
            catch (Exception e) { err = e.Message; }

            Mutate(d =>
            {
                var x = d.Find(t.Id);
                if (x == null) return false;
                var xa = x.Last;
                if (err != null) { Finish(x, QState.NeedsAttention, "launch", "Couldn't start Claude Code: " + err); xa.EndedUtc = Now(); xa.Outcome = "launch"; return true; }
                xa.Pid = r.Pid; xa.PidStartedUtc = r.PidStarted;
                return true;
            });
            if (err == null) { run = r; SetAwake(true); }
        }

        // ---------- VS Code runner: interactive Claude in a named terminal of the project's VS Code window ----------
        // Progress comes from Claude Code's own hooks for the task's session (state\sessions\<id>.json, written by
        // hook.ps1): no hook yet = Claude hasn't started the prompt; WORKING / NEEDS_YOU; DONE (Stop) = it finished its
        // turn; ERROR (StopFailure) = it stopped with that error. The terminal opening or closing never counts as success.
        public const int MaxArgChars = 30000;   // Windows command lines are limited to 32,767 characters

        // Arguments after the executable. The prompt is the last one, after "--" (so a prompt starting with "-" isn't
        // read as an option), quoted with the Windows argv rules: it reaches Claude as one exact argument, no shell.
        public static string VsArgs(string[] claude, QueueTask t, string settingsFile, string mode, string sid, string prompt, out string error)
        {
            error = null;
            var args = new List<string>();
            for (int i = 1; i < claude.Length; i++) args.Add(claude[i]);
            args.Add("--permission-mode"); args.Add(t.PermissionMode);
            args.Add("--settings"); args.Add(settingsFile);
            args.Add(mode == "resume" ? "--resume" : "--session-id"); args.Add(sid);
            if (t.Model != "") { args.Add("--model"); args.Add(t.Model); }
            if (mode != "resume" && t.Title.Trim() != "") { args.Add("--name"); args.Add(Sched.FirstLine(t.Title, 80)); }
            if (!string.IsNullOrEmpty(prompt)) { args.Add("--"); args.Add(prompt); }
            string line = JoinArgs(args);
            if (line.Length > MaxArgChars)
            {
                error = "The prompt is too long to start in a VS Code terminal (" + (prompt ?? "").Length + " characters; Windows allows about " + (MaxArgChars - 2000) + " here). Shorten it, or run this task in the background.";
                return null;
            }
            return line;
        }

        public static string TerminalName(QueueTask t) { return "⏱ " + Sched.FirstLine(t.Title, 50); }

        void LaunchVs(QueueTask t)
        {
            var a = t.Last;
            string err = null, req = t.Id + "-" + a.N, target = "", name = TerminalName(t);
            var before = HookOf(a.SessionId, DateTime.MinValue);
            DateTime hookBefore = before != null ? before.Updated : DateTime.MinValue;
            try { target = StartVs(t, req, a.Mode, a.SessionId, a.Prompt, name); }
            catch (Exception e) { err = e.Message; }
            Mutate(d =>
            {
                var x = d.Find(t.Id);
                if (x == null) return false;
                var xa = x.Last;
                if (err != null) { Finish(x, QState.NeedsAttention, "vscode", "Couldn't start it in VS Code: " + err); xa.EndedUtc = Now(); xa.Outcome = "vscode"; xa.Phase = "ended"; return true; }
                xa.Req = req; xa.Window = target; xa.Terminal = name; xa.Phase = "opening"; xa.HookBefore = hookBefore;
                x.Reason = target != "" ? "Opening a terminal in the project's VS Code window…" : "Opening a VS Code window for this project…";
                return true;
            });
            if (err == null) { run = new Run { TaskId = t.Id, N = a.N, Vs = true, SeenUtc = Now() }; SetAwake(true); }
        }

        // Asks VS Code for a terminal running Claude in the project: in the window that has the project open, or in a
        // dedicated new window. Returns the window it was addressed to ("" = the window VS Code is opening now).
        string StartVs(QueueTask t, string req, string mode, string sid, string prompt, string name)
        {
            var claude = ResolveClaude(ClaudeCommand);
            if (claude == null) throw new FileNotFoundException("Claude Code (claude.exe) wasn't found on PATH or in %USERPROFILE%\\.local\\bin.");
            if (!claude[0].EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("VS Code runs need claude.exe (found " + claude[0] + "). Run this task in the background instead.");
            if (!Directory.Exists(t.Project)) throw new DirectoryNotFoundException("The project folder doesn't exist: " + t.Project);
            if (!Bridge.Installed()) throw new InvalidOperationException("the Claude Status Bridge extension isn't installed in VS Code. Click \"Install VS Code bridge\" in the Queue (or run install.ps1), then Retry. Or choose \"Run in background instead\".");
            string error, line = VsArgs(claude, t, WriteSettings(t, req), mode, sid, prompt, out error);
            if (line == null) throw new InvalidOperationException(error);
            var env = new Dictionary<string, object> { { "CLAUDECODE", null }, { "CLAUDE_CODE_ENTRYPOINT", null }, { "CLAUDE_STATUS_TASK", t.Id } };
            if (!AllowApiBilling) { env["ANTHROPIC_API_KEY"] = null; env["ANTHROPIC_AUTH_TOKEN"] = null; }
            var w = VsBridge.Pick(Bridge.Windows(), t.Project);
            string target = w != null ? w.Id : "";
            Bridge.Send(new Dictionary<string, object> { { "req", req }, { "type", "launch" }, { "target", target }, { "folder", t.Project }, { "cwd", t.Project }, { "exe", claude[0] }, { "args", line }, { "env", env }, { "name", name } });
            if (w == null)
            {
                string e = Bridge.Open(t.Project);
                if (e != null) { Bridge.Retract(req); throw new InvalidOperationException(e); }
            }
            return target;
        }

        // Claude Code's hook state for a session, if written at or after sinceUtc.
        public SessionInfo HookOf(string sid, DateTime sinceUtc)
        {
            if (!Sched.IsGuid(sid)) return null;
            string f = Path.Combine(SessionsStateDir, sid + ".json");
            try
            {
                if (!File.Exists(f)) return null;
                var d = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(File.ReadAllText(f));
                var si = Board.Parse(sid, d, DateTime.MinValue, File.GetLastWriteTimeUtc(f));
                return sinceUtc == DateTime.MinValue || si.Updated >= sinceUtc.AddSeconds(-2) ? si : null;   // (MinValue - 2 s would throw)
            }
            catch { return null; }
        }

        public Activity ActivityOf(QueueTask t, RunAttempt a)
        {
            return Sessions.ReadActivity(Sessions.Find(ProjectsDir, t.Project, a.SessionId), a.StartedUtc.AddSeconds(-2), a.Prompt);
        }

        void PollVs(Run r)
        {
            var t = Data.Find(r.TaskId);
            RunAttempt a = null;
            if (t != null) foreach (var x in t.Attempts) if (x.N == r.N) a = x;
            if (t == null || a == null || t.State != QState.Running) { run = null; return; }
            DateTime now = Now();
            var st = Bridge.Status(a.Req);
            if (st == null)
            {
                // Not claimed by any window yet. Taking the request back is atomic, so it can't also start late.
                if (now - a.StartedUtc > OpenTimeout && Bridge.Retract(a.Req))
                    EndVs(r, Fail("vscode", "VS Code didn't take the task within " + (int)OpenTimeout.TotalSeconds + " seconds, so nothing was started. Check that VS Code opened " + t.Project +
                        " and that the Claude Status Bridge extension is enabled there, then Retry (or choose Run in background instead)."), null);
                else Raise();
                return;
            }
            if (st.State == "error") { EndVs(r, Fail("vscode", "VS Code couldn't open the task's terminal: " + st.Error), null); return; }

            var h = HookOf(a.SessionId, a.StartedUtc);
            // Still the previous run's last event (tasks.json keeps times to the millisecond; hook files are finer).
            if (h != null && a.HookBefore != DateTime.MinValue && h.Updated <= a.HookBefore.AddMilliseconds(1)) h = null;
            if (h != null && h.ClaudePid > 0 && a.ClaudePid != h.ClaudePid)
            {
                Edit(t.Id, x => { var xa = x.Last; if (xa == null || xa.N != a.N) return false; xa.ClaudePid = h.ClaudePid; xa.ClaudeStartedUtc = h.ClaudeStarted; return true; });
                a = Data.Find(t.Id).Last;
            }
            if (h != null && h.State == Board.Done)
            {
                var act = ActivityOf(t, a);
                var rep = new RunReport { HasResult = act.LastReply != "", ResultText = act.LastReply, SessionId = a.SessionId };
                EndVs(r, Fail("completed", "Claude finished its turn in VS Code; its last message is below. That's Claude's own report: the work isn't independently verified. The conversation stays open in the terminal for follow-ups."), rep);
                return;
            }
            if (h != null && h.State == Board.Error) { EndVs(r, VsError(h.Detail, now), null); return; }

            if (Bridge.TerminalOpen(st.Window, a.Req)) r.SeenUtc = now;
            bool started = h != null || a.Phase == "working" || a.Phase == "needs_you";
            int cpid = h != null ? h.ClaudePid : a.ClaudePid;
            DateTime cst = h != null ? h.ClaudeStarted : a.ClaudeStartedUtc;
            bool claudeGone = cpid > 0 && WorkerWindows.ProcessAlive(cpid, cst) == 0;
            if (st.State == "closed" || claudeGone || now - r.SeenUtc > TimeSpan.FromSeconds(60))
            {
                EndVs(r, started
                    ? Fail("interrupted", "Claude stopped before finishing its turn: its VS Code terminal was closed or Claude exited. The conversation is kept: Continue resumes it (without resending the original prompt), or Mark completed if you finished it yourself.")
                    : Fail("interrupted", "The VS Code terminal closed before Claude started the prompt, so nothing ran. Retry starts it again."), null);
                return;
            }

            string phase = h == null ? (started ? a.Phase : "terminal") : h.State == Board.NeedsYou ? "needs_you" : "working", attention = "", reason;
            string where = "terminal \"" + a.Terminal + "\"";
            if (phase == "needs_you")
            {
                string ask = ActivityOf(t, a).Pending;
                attention = ask != "" ? "Claude is asking: " + ask : "Claude is waiting for your input or approval" + (h != null && h.Detail != "" ? " (" + h.Detail + ")" : "") + ".";
                reason = "Needs you: answer Claude in VS Code (" + where + ").";
            }
            else if (phase == "working") reason = "Claude is working in VS Code (" + where + ").";
            else if (now - a.StartedUtc > StartNotice)
            {
                attention = "Claude hasn't started the prompt yet. Open the terminal in VS Code: Claude may be asking you to trust the folder, or to sign in.";
                reason = "Waiting for Claude to start in VS Code (" + where + ").";
            }
            else reason = "Terminal open in VS Code; Claude is starting…";
            if (a.Phase != phase || a.Attention != attention || t.Reason != reason || a.Window != st.Window || a.ClaudePid != cpid)
                Edit(t.Id, x =>
                {
                    var xa = x.Last;
                    if (x.State != QState.Running || xa == null || xa.N != a.N) return false;
                    xa.Phase = phase; xa.Attention = attention; xa.Window = st.Window; xa.ClaudePid = cpid; xa.ClaudeStartedUtc = cst; x.Reason = reason;
                    return true;
                });
            else Raise();
        }

        static Verdict Fail(string kind, string message) { return new Verdict { Kind = kind, Message = message }; }

        // A StopFailure error type (hook detail: rate_limit, authentication_failed, billing_error, server_error, ...).
        Verdict VsError(string detail, DateTime now)
        {
            string d = (detail ?? "").ToLowerInvariant();
            if (d.Contains("rate_limit"))
            {
                var v = Fail("usage_limit", "");
                DateTime u = UsageBlockedUntil != null ? UsageBlockedUntil() : DateTime.MinValue;
                if (u > now) { v.ResetUtc = u; v.ResetReported = true; }
                v.Message = "Usage limit reached in VS Code" + (v.ResetReported ? "; Claude Code reported it resets " + Sched.LocalText(u) : "; no reset time is known") + ".";
                return v;
            }
            if (d.Contains("auth")) return Fail("auth", "Claude Code isn't signed in (or its sign-in expired). Use /login in the task's VS Code terminal, then Continue.");
            if (d.Contains("billing")) return Fail("billing", "Billing problem reported by Claude Code (" + detail + "). Details are in the task's VS Code terminal.");
            if (d.Contains("server") || d.Contains("overload") || d.Contains("network") || d.Contains("connection") || d.Contains("timeout"))
                return Fail("network", "Network or service problem (" + detail + ").");
            return Fail("error", "Claude stopped with an error (" + (detail != "" ? detail : "unknown") + ") in VS Code. Read it in the terminal, then Continue, or Mark completed if you finished it there.");
        }

        void EndVs(Run r, Verdict v, RunReport rep)
        {
            run = null;
            Apply(r.TaskId, r.N, v, int.MinValue, rep ?? new RunReport());
        }

        // Stops Claude for a VS Code run: an unclaimed request is taken back; otherwise Claude's process tree is ended
        // (the terminal stays, so its output can still be read) or, if its process isn't known yet, the terminal closes.
        void StopVs(RunAttempt a)
        {
            if (a.Req == "" || Bridge.Retract(a.Req)) return;
            var h = HookOf(a.SessionId, a.StartedUtc);
            int pid = h != null ? h.ClaudePid : a.ClaudePid;
            DateTime started = h != null ? h.ClaudeStarted : a.ClaudeStartedUtc;
            if (pid > 0 && WorkerWindows.ProcessAlive(pid, started) == 1) { KillTree(pid, started, ""); return; }
            var st = Bridge.Status(a.Req);
            if (st != null && st.Window != "") Bridge.Send(new Dictionary<string, object> { { "req", "close-" + Guid.NewGuid().ToString("N").Substring(0, 12) }, { "type", "close" }, { "target", st.Window }, { "of", a.Req } });
        }

        // A manual VS Code terminal (Open in VS Code on a task that isn't running) that is still open.
        public bool ManualOpen(QueueTask t)
        {
            if (t.ManualReq == "") return false;
            if (Bridge.Pending(t.ManualReq)) return true;
            var st = Bridge.Status(t.ManualReq);
            return st != null && st.State == "open" && Bridge.TerminalOpen(st.Window, t.ManualReq);
        }

        static string ResumeSid(QueueTask t, QueueData d)
        {
            if (t.Conversation == "resume") return t.ResumeSession;
            if (t.Conversation == "predecessor") { var p = d.Find(t.After); return p != null ? p.SessionId : ""; }
            return t.SessionId;
        }

        readonly Dictionary<string, DateTime> releasing = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);

        // Before a run continues a conversation, nothing else may hold it (two Claude processes on one conversation
        // mix up its messages). A finished run's own VS Code terminal that nobody used since is closed first, and
        // Claude in it must have exited. A conversation you used yourself in VS Code is left alone: the task waits.
        // null = free; "Waiting: ..." = not yet; "!..." = needs you.
        string Held(QueueTask t, QueueData d, DateTime now)
        {
            if (ManualOpen(t)) return "Waiting: you opened this task's conversation in VS Code yourself. The queue continues it after you close that terminal.";
            string sid = ResumeSid(t, d);
            if (!Sched.IsGuid(sid)) return null;
            foreach (var x in d.Tasks)
            {
                if (x != t && string.Equals(x.SessionId, sid, StringComparison.OrdinalIgnoreCase) && ManualOpen(x)) return "Waiting: you have this conversation open in VS Code (from \"" + x.Title + "\"). The queue continues it after you close that terminal.";
                foreach (var a in x.Attempts)
                {
                    if (!a.InVsCode || a.Req == "" || !string.Equals(a.SessionId, sid, StringComparison.OrdinalIgnoreCase) || a.EndedUtc == DateTime.MinValue) continue;
                    var st = Bridge.Status(a.Req);
                    if (st == null || st.State != "open" || !Bridge.TerminalOpen(st.Window, a.Req)) continue;
                    var h = HookOf(sid, DateTime.MinValue);
                    if (h != null && h.Updated > a.EndedUtc.AddSeconds(30))
                        return "Waiting: this conversation is still open in VS Code (terminal \"" + a.Terminal + "\") and was used after its run ended, so the queue won't take it over. Close that terminal when you're done.";
                    DateTime asked;
                    if (!releasing.TryGetValue(sid, out asked)) { releasing[sid] = now; Bridge.Send(new Dictionary<string, object> { { "req", "close-" + Guid.NewGuid().ToString("N").Substring(0, 12) }, { "type", "close" }, { "target", st.Window }, { "of", a.Req } }); }
                    else if (now - asked > TimeSpan.FromSeconds(45)) { releasing.Remove(sid); return "!The VS Code terminal \"" + a.Terminal + "\" still holds this conversation and didn't close. Close it yourself, then Retry."; }
                    return "Waiting: closing the finished VS Code terminal of this conversation before continuing it…";
                }
            }
            if (releasing.ContainsKey(sid))
            {
                // Its terminal is gone; Claude in it must have exited before the conversation is used again.
                var h = HookOf(sid, DateTime.MinValue);
                if (h != null && h.ClaudePid > 0 && WorkerWindows.ProcessAlive(h.ClaudePid, h.ClaudeStarted) == 1)
                {
                    if (now - releasing[sid] > TimeSpan.FromSeconds(45)) { releasing.Remove(sid); return "!Claude (process " + h.ClaudePid + ") still holds this conversation after its terminal closed. Close it, then Retry."; }
                    return "Waiting: closing the finished VS Code terminal of this conversation before continuing it…";
                }
                releasing.Remove(sid);
                activeCache = null;   // ask `claude agents` afresh
            }
            return null;
        }

        // The prominent "Open in VS Code" action. Reveals the task's own terminal (never starts a second Claude for a
        // conversation that's already open); otherwise resumes the task's conversation in a new terminal without any
        // prompt. Returns a note for you, or null when it simply worked.
        public string OpenInVsCode(string id)
        {
            var t = Data.Find(id);
            if (t == null) return "That task no longer exists.";
            var a = t.Last;
            if (t.State == QState.Running && a != null && !a.InVsCode)
                return "This task is running in the background (claude -p), not in VS Code. One conversation must never have two Claude processes, so it can't move while it runs: let it finish, or stop it with Cancel run. " +
                    "Afterwards, Open in VS Code resumes the same conversation (" + Short(t.SessionId) + ") with its progress, without resending the prompt.";
            string note;
            for (int i = t.Attempts.Count - 1; i >= 0; i--)
            {
                var x = t.Attempts[i];
                // A terminal whose Claude has exited isn't worth revealing: resume the conversation instead.
                bool claudeLeft = x.EndedUtc != DateTime.MinValue && x.ClaudePid > 0 && WorkerWindows.ProcessAlive(x.ClaudePid, x.ClaudeStartedUtc) == 0;
                if (x.InVsCode && x.Req != "" && !claudeLeft && Reveal(x.Req, out note)) return note;
            }
            if (t.ManualReq != "" && Reveal(t.ManualReq, out note)) return note;
            if (t.State == QState.Running && a != null)
            {
                if (a.Window != "" && Bridge.Focus(Bridge.Window(a.Window))) return null;
                return "VS Code is still opening this task's terminal. Try again in a moment.";
            }
            if (ManualOpen(t)) return "VS Code is opening this conversation. Try again in a moment.";
            if (!Sched.IsGuid(t.SessionId)) return "This task has no conversation yet because it hasn't started. Use Run now to start it in VS Code.";
            if (Sessions.Find(ProjectsDir, t.Project, t.SessionId) == null)
                return "Conversation " + Short(t.SessionId) + " wasn't found on this computer: Claude Code didn't save it (or the run stopped before it started), so it can't be resumed. Use Run again to start fresh.";
            activeCache = null;
            var act = ActiveFor(t.SessionId);
            if (act != null) return "That conversation is already open in another Claude Code session (" + (act.Kind != "" ? act.Kind + ", " : "") + "process " + act.Pid + "). Switch to it there: a second copy would mix up its messages.";
            string req = "manual-" + t.Id + "-" + Guid.NewGuid().ToString("N").Substring(0, 8);
            try { StartVs(t, req, "resume", t.SessionId, null, TerminalName(t)); }
            catch (Exception e) { return "Couldn't open it in VS Code: " + e.Message; }
            Edit(id, x => { x.ManualReq = req; return true; });
            return null;
        }

        bool Reveal(string req, out string note)
        {
            note = null;
            var st = Bridge.Status(req);
            if (st == null || st.State != "open") return false;
            var w = Bridge.Window(st.Window);
            if (w == null || !w.Terminals.Contains(req)) return false;
            Bridge.Send(new Dictionary<string, object> { { "req", "reveal-" + Guid.NewGuid().ToString("N").Substring(0, 12) }, { "type", "reveal" }, { "target", w.Id }, { "of", req } });
            if (!Bridge.Focus(w)) note = "The terminal is shown in its VS Code window (" + w.Display + "), but that window can't be brought forward from here (an unsaved multi-root workspace). Switch to it yourself.";
            return true;
        }

        // Where the task runs (or will run), for the details view.
        public string Where(QueueTask t)
        {
            var a = t.Last;
            bool vs = a != null && a.EndedUtc == DateTime.MinValue && t.State == QState.Running ? a.InVsCode : t.Runner != "headless";
            if (!vs) return "In the background (claude -p, no window)";
            var b = a != null && a.InVsCode && a.Req != "" ? a : null;
            if (b == null) return "VS Code: a terminal in the project's window";
            var w = b.Window != "" ? Bridge.Window(b.Window) : null;
            return "VS Code terminal \"" + b.Terminal + "\"" + (w != null ? " in the window for " + w.Display : "");
        }

        static string Short(string sid) { return sid != null && sid.Length > 8 ? sid.Substring(0, 8) : sid ?? ""; }

        void Start(Run r, string cmdline, string cwd, string promptFile, string jobName)
        {
            using (var fin = new FileStream(promptFile, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var fout = new FileStream(r.Log, FileMode.Create, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete))
            using (var ferr = new FileStream(r.Err, FileMode.Create, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete))
            {
                IntPtr hin = fin.SafeFileHandle.DangerousGetHandle(), hout = fout.SafeFileHandle.DangerousGetHandle(), herr = ferr.SafeFileHandle.DangerousGetHandle();
                foreach (var h in new[] { hin, hout, herr }) PN.SetHandleInformation(h, 1, 1);   // HANDLE_FLAG_INHERIT
                var si = new PN.STARTUPINFO { dwFlags = 0x100, hStdInput = hin, hStdOutput = hout, hStdError = herr };
                si.cb = Marshal.SizeOf(si);
                IntPtr env = EnvBlock();
                try
                {
                    PN.PROCESS_INFORMATION pi;
                    const uint Suspended = 0x4, Unicode = 0x400, NoWindow = 0x08000000, Breakaway = 0x01000000, NewGroup = 0x200;
                    bool ok = PN.CreateProcessW(null, new StringBuilder(cmdline), IntPtr.Zero, IntPtr.Zero, true, Suspended | Unicode | NoWindow | NewGroup | Breakaway, env, cwd, ref si, out pi);
                    if (!ok && Marshal.GetLastWin32Error() == 5)   // our own job doesn't allow breakaway: start inside it
                        ok = PN.CreateProcessW(null, new StringBuilder(cmdline), IntPtr.Zero, IntPtr.Zero, true, Suspended | Unicode | NoWindow | NewGroup, env, cwd, ref si, out pi);
                    if (!ok) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
                    // A named job owns Claude and everything it starts, so Cancel can stop the whole tree (also after a restart).
                    r.Job = PN.CreateJobObjectW(IntPtr.Zero, jobName);
                    if (r.Job != IntPtr.Zero && !PN.AssignProcessToJobObject(r.Job, pi.hProcess)) { PN.CloseHandle(r.Job); r.Job = IntPtr.Zero; }
                    PN.ResumeThread(pi.hThread);
                    PN.CloseHandle(pi.hThread);
                    r.Process = pi.hProcess;
                    r.Pid = pi.dwProcessId;
                    DateTime st; WorkerWindows.StartTime(r.Pid, out st); r.PidStarted = st;
                }
                finally { foreach (var h in new[] { hin, hout, herr }) PN.SetHandleInformation(h, 1, 0); Marshal.FreeHGlobal(env); }
            }
        }

        // Claude's environment is ours minus API credentials (so a stray ANTHROPIC_API_KEY can't silently switch the
        // run to API billing) and minus the variables that tie a process to a running Claude Code session or IDE.
        IntPtr EnvBlock()
        {
            var vars = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (System.Collections.DictionaryEntry e in Environment.GetEnvironmentVariables()) vars[(string)e.Key] = (string)e.Value;
            var drop = new List<string> { "CLAUDECODE", "CLAUDE_CODE_ENTRYPOINT", "CLAUDE_CODE_SSE_PORT", "CLAUDE_PID", "CLAUDE_CODE_IDE_HOST_OVERRIDE" };
            if (!AllowApiBilling) { drop.Add("ANTHROPIC_API_KEY"); drop.Add("ANTHROPIC_AUTH_TOKEN"); }
            foreach (var k in drop) vars.Remove(k);
            var sb = new StringBuilder();
            foreach (var kv in vars) if (!kv.Key.StartsWith("=")) sb.Append(kv.Key).Append('=').Append(kv.Value).Append('\0');
            sb.Append('\0');
            return Marshal.StringToHGlobalUni(sb.ToString());
        }

        void ReadOutput(Run r, bool final)
        {
            try
            {
                using (var fs = new FileStream(r.Log, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                {
                    if (fs.Length > r.Offset)
                    {
                        fs.Seek(r.Offset, SeekOrigin.Begin);
                        var buf = new byte[fs.Length - r.Offset];
                        int n = 0, k;
                        while (n < buf.Length && (k = fs.Read(buf, n, buf.Length - n)) > 0) n += k;
                        r.Offset += n;
                        var all = new byte[r.Pending.Length + n];
                        Buffer.BlockCopy(r.Pending, 0, all, 0, r.Pending.Length);
                        Buffer.BlockCopy(buf, 0, all, r.Pending.Length, n);
                        int start = 0;
                        for (int i = 0; i < all.Length; i++)
                            if (all[i] == (byte)'\n') { RunParser.Feed(r.Report, Encoding.UTF8.GetString(all, start, i - start)); start = i + 1; }
                        r.Pending = new byte[all.Length - start];
                        Buffer.BlockCopy(all, start, r.Pending, 0, r.Pending.Length);
                    }
                }
            }
            catch { }
            if (final && r.Pending.Length > 0) { RunParser.Feed(r.Report, Encoding.UTF8.GetString(r.Pending)); r.Pending = new byte[0]; }
            // Billing guard: the init message names the credential before the first request is sent.
            if (!AllowApiBilling && !r.Guard && r.Report.ApiKeySource != null && r.Report.ApiKeySource != "" && r.Report.ApiKeySource != "none")
            { r.Guard = true; Kill(r); }
        }

        void Poll()
        {
            var r = run;
            if (r == null) return;
            if (r.Vs) { PollVs(r); return; }
            ReadOutput(r, false);
            bool exited;
            uint code = 0;
            if (r.Process != IntPtr.Zero) { exited = PN.WaitForSingleObject(r.Process, 0) == 0; if (exited) PN.GetExitCodeProcess(r.Process, out code); }
            else exited = WorkerWindows.ProcessAlive(r.Pid, r.PidStarted) == 0;
            if (!exited) { Raise(); return; }
            ReadOutput(r, true);
            int exitCode = r.Process != IntPtr.Zero ? (int)code : int.MinValue;
            if (r.Job != IntPtr.Zero) { PN.TerminateJobObject(r.Job, 1); PN.CloseHandle(r.Job); }   // leftovers Claude started
            if (r.Process != IntPtr.Zero) PN.CloseHandle(r.Process);
            run = null;
            string stderr = "";
            try { using (var fs = new FileStream(r.Err, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete)) using (var sr = new StreamReader(fs)) { stderr = sr.ReadToEnd(); } } catch { }
            if (stderr.Length > 4000) stderr = stderr.Substring(stderr.Length - 4000);
            Complete(r.TaskId, r.N, r.Report, exitCode, stderr, r.Canceled, r.Guard);
        }

        void Complete(string id, int n, RunReport rep, int exitCode, string stderr, bool canceled, bool guard)
        {
            var v = Outcome.Classify(rep, exitCode, stderr, canceled, guard, Now());
            try { File.Delete(Path.Combine(RunsDir, id, n + ".prompt.txt")); } catch { }
            Apply(id, n, v, exitCode, rep);
        }

        // Records a run's outcome (both runners): finished, retried later, or needs you.
        void Apply(string id, int n, Verdict v, int exitCode, RunReport rep)
        {
            DateTime now = Now();
            if (v.Kind == "usage_limit" && v.ResetReported && v.ResetUtc > knownBlockedUntil) knownBlockedUntil = v.ResetUtc;
            Mutate(d =>
            {
                var t = d.Find(id);
                if (t == null) return false;
                RunAttempt a = null;
                foreach (var x in t.Attempts) if (x.N == n) a = x;
                if (a == null) return false;
                a.EndedUtc = now; a.ExitCode = exitCode; a.Outcome = v.Kind; a.Phase = "ended"; a.Attention = "";
                if (rep.SessionId != "" && Sched.IsGuid(rep.SessionId)) t.SessionId = rep.SessionId;
                if (rep.HasResult) { t.ResultText = rep.ResultText.Length > 20000 ? rep.ResultText.Substring(0, 20000) + "\n…(full output in the log)" : rep.ResultText; t.Summary = Sched.FirstLine(rep.ResultText, 160); }
                t.Denials = new List<string>(rep.Denials);
                bool hasConversation = Sessions.Find(ProjectsDir, t.Project, t.SessionId) != null;
                t.NeedsContinue = false; t.ContinueWhy = "";
                if (t.State != QState.Running) return true;   // canceled from elsewhere meanwhile
                switch (v.Kind)
                {
                    case "completed": Finish(t, QState.Completed, v.Kind, v.Message); t.RetryCount = 0; break;
                    case "usage_limit":
                        t.RetryCount++;
                        if (t.RetryCount > Sched.MaxUsageRetries) { Finish(t, QState.NeedsAttention, v.Kind, v.Message + " Gave up after " + Sched.MaxUsageRetries + " tries; choose Continue when usage is back."); break; }
                        t.State = QState.WaitingUsage; t.Outcome = v.Kind;
                        t.NextRetryUtc = v.ResetReported ? v.ResetUtc.AddMinutes(1) : now + Sched.UsageBackoff(t.RetryCount - 1);
                        t.ResetSource = v.ResetReported ? "reported" : "backoff";
                        t.NeedsContinue = hasConversation; t.ContinueWhy = "a usage limit";
                        t.Reason = v.Message + " Next try " + Sched.LocalText(t.NextRetryUtc) + (v.ResetReported ? "." : " (estimated; you can change it).") + (hasConversation ? " It continues the same conversation instead of starting over." : "");
                        break;
                    case "network":
                        t.RetryCount++;
                        if (t.RetryCount > Sched.MaxNetRetries) { Finish(t, QState.Failed, v.Kind, v.Message + " Gave up after " + Sched.MaxNetRetries + " retries."); break; }
                        t.State = QState.Scheduled; t.Outcome = v.Kind;
                        t.NextRetryUtc = now + Sched.NetBackoff(t.RetryCount - 1);
                        t.NeedsContinue = hasConversation; t.ContinueWhy = "a network problem";
                        t.Reason = v.Message + " Retrying " + Sched.LocalText(t.NextRetryUtc) + ".";
                        break;
                    case "canceled": Finish(t, QState.Canceled, v.Kind, v.Message); break;
                    case "failed": Finish(t, QState.Failed, v.Kind, v.Message); break;
                    default: Finish(t, QState.NeedsAttention, v.Kind, v.Message); break;   // denied, auth, billing, api_key, interrupted
                }
                return true;
            });
        }

        // A task marked Running that this engine isn't watching: after a restart or a crash (ours or another
        // instance's). Reattach if Claude is still running; otherwise finish it from its log. Never rerun it.
        void Adopt()
        {
            foreach (var t in Data.Tasks)
            {
                if (t.State != QState.Running) continue;
                var a = t.Last;
                if (a == null) { Edit(t.Id, x => { Finish(x, QState.NeedsAttention, "interrupted", "Interrupted before it started."); return true; }); continue; }
                bool ownerAlive = a.Owner != "" && a.Owner != instance && WorkerWindows.ProcessAlive(a.OwnerPid, a.OwnerStartedUtc) == 1;
                if (ownerAlive) continue;   // another running instance is watching it
                if (a.InVsCode)
                {
                    // Interactive Claude lives in VS Code, not under this app: just watch it again (hooks + bridge).
                    Edit(t.Id, x => { var xa = x.Last; if (xa == null) return false; xa.Owner = instance; xa.OwnerPid = myPid; xa.OwnerStartedUtc = myStart; return true; });
                    run = new Run { TaskId = t.Id, N = a.N, Vs = true, SeenUtc = Now() };
                    return;
                }
                string log = LogPath(t.Id, a.N);
                if (a.Pid == 0)
                {
                    bool started = File.Exists(log) && new FileInfo(log).Length > 0;
                    if (!started) { Edit(t.Id, x => { if (x.State != QState.Running) return false; x.Attempts.Remove(x.Last); x.State = QState.Scheduled; x.Reason = ""; return true; }); continue; }
                }
                var r = new Run { TaskId = t.Id, N = a.N, Log = log, Err = ErrPath(t.Id, a.N), Pid = a.Pid, PidStarted = a.PidStartedUtc };
                if (a.Pid > 0 && WorkerWindows.ProcessAlive(a.Pid, a.PidStartedUtc) == 1)
                {
                    r.Process = PN.OpenProcess(0x00100000 | 0x1000 | 0x0001, false, a.Pid);   // SYNCHRONIZE | QUERY_LIMITED | TERMINATE
                    r.Job = a.Job != "" ? PN.OpenJobObjectW(0x0008 | 0x0004, false, a.Job) : IntPtr.Zero;
                    Edit(t.Id, x => { var xa = x.Last; if (xa == null) return false; xa.Owner = instance; xa.OwnerPid = myPid; xa.OwnerStartedUtc = myStart; x.Reason = "Running (picked up again after Claude Status restarted)."; return true; });
                    run = r;
                    ReadOutput(r, false);
                    return;
                }
                // Claude has ended: its log says how (exit code unknown now).
                ReadOutput(r, true);
                string stderr = "";
                try { stderr = File.ReadAllText(r.Err); } catch { }
                Complete(t.Id, a.N, r.Report, int.MinValue, stderr, false, false);
            }
        }

        void Kill(Run r)
        {
            if (r.Job != IntPtr.Zero && PN.TerminateJobObject(r.Job, 1)) return;
            KillTree(r.Pid, r.PidStarted, "");
        }

        // Stops a recorded process and its descendants (identified by PID + start time, so reused PIDs are safe).
        static void KillTree(int pid, DateTime started, string jobName)
        {
            if (jobName != "")
            {
                IntPtr job = PN.OpenJobObjectW(0x0008, false, jobName);
                if (job != IntPtr.Zero) { bool ok = PN.TerminateJobObject(job, 1); PN.CloseHandle(job); if (ok) return; }
            }
            if (pid <= 0 || WorkerWindows.ProcessAlive(pid, started) != 1) return;
            var parents = WorkerWindows.ParentMap();
            var tree = new List<int> { pid };
            for (int i = 0; i < tree.Count; i++)
            {
                DateTime ps; WorkerWindows.StartTime(tree[i], out ps);
                foreach (var kv in parents)
                    if (kv.Value == tree[i] && !tree.Contains(kv.Key)) { DateTime cs; if (WorkerWindows.StartTime(kv.Key, out cs) == 1 && cs >= ps) tree.Add(kv.Key); }
            }
            foreach (var p in tree)
            {
                IntPtr h = PN.OpenProcess(0x0001, false, p);
                if (h != IntPtr.Zero) { PN.TerminateProcess(h, 1); PN.CloseHandle(h); }
            }
        }

        // Old history (and orphaned run folders) are deleted after HistoryDays.
        void Prune()
        {
            if (HistoryDays <= 0) return;
            DateTime cut = Now().AddDays(-HistoryDays);
            var gone = new List<string>();
            Mutate(d =>
            {
                foreach (var t in d.Tasks) if (QState.Finished(t.State) && t.FinishedUtc != DateTime.MinValue && t.FinishedUtc < cut && !NeededBy(d, t.Id)) gone.Add(t.Id);
                d.Tasks.RemoveAll(t => gone.Contains(t.Id));
                return gone.Count > 0;
            });
            try
            {
                foreach (var dir in Directory.GetDirectories(RunsDir))
                    if (Data.Find(Path.GetFileName(dir)) == null) gone.Add(Path.GetFileName(dir));
            }
            catch { }
            foreach (var id in gone) DeleteRuns(id);
        }

        // While a task runs, ask Windows not to sleep (a request that ends with the run; no power setting changes).
        void SetAwake(bool on)
        {
            on = on && KeepAwake;
            if (on == awake) return;
            awake = on;
            PN.SetThreadExecutionState(on ? 0x80000001u : 0x80000000u);   // ES_CONTINUOUS (| ES_SYSTEM_REQUIRED)
        }

        // Stops watching (the app is quitting). A running Claude keeps running and is picked up next time.
        public void Dispose()
        {
            SetAwake(false);
            if (run != null)
            {
                string id = run.TaskId;
                try { Store.Mutate(d => { var t = d.Find(id); if (t == null || t.Last == null || t.Last.Owner != instance) return false; t.Last.Owner = ""; return true; }); } catch { }
                if (run.Process != IntPtr.Zero) PN.CloseHandle(run.Process);
                if (run.Job != IntPtr.Zero) PN.CloseHandle(run.Job);   // the job lives on while Claude runs
                run = null;
            }
        }

        // For the tests: the running Claude process, and forgetting it without stopping it (like a crash).
        public int RunningPid { get { return run != null ? run.Pid : 0; } }
        public void AbandonForTest() { if (run != null) { if (run.Process != IntPtr.Zero) PN.CloseHandle(run.Process); run = null; } }
    }

    static class PN
    {
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct STARTUPINFO
        {
            public int cb; public string lpReserved, lpDesktop, lpTitle;
            public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
            public short wShowWindow, cbReserved2; public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
        }
        [StructLayout(LayoutKind.Sequential)] public struct PROCESS_INFORMATION { public IntPtr hProcess, hThread; public int dwProcessId, dwThreadId; }
        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern bool CreateProcessW(string app, StringBuilder cmd, IntPtr pa, IntPtr ta, bool inherit, uint flags, IntPtr env, string cwd, ref STARTUPINFO si, out PROCESS_INFORMATION pi);
        [DllImport("kernel32.dll", SetLastError = true)] public static extern bool SetHandleInformation(IntPtr h, uint mask, uint flags);
        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)] public static extern IntPtr CreateJobObjectW(IntPtr sa, string name);
        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)] public static extern IntPtr OpenJobObjectW(uint access, bool inherit, string name);
        [DllImport("kernel32.dll", SetLastError = true)] public static extern bool AssignProcessToJobObject(IntPtr job, IntPtr proc);
        [DllImport("kernel32.dll", SetLastError = true)] public static extern bool TerminateJobObject(IntPtr job, uint code);
        [DllImport("kernel32.dll")] public static extern uint ResumeThread(IntPtr h);
        [DllImport("kernel32.dll")] public static extern uint WaitForSingleObject(IntPtr h, uint ms);
        [DllImport("kernel32.dll", SetLastError = true)] public static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
        [DllImport("kernel32.dll")] public static extern bool GetExitCodeProcess(IntPtr h, out uint code);
        [DllImport("kernel32.dll")] public static extern bool TerminateProcess(IntPtr h, uint code);
        [DllImport("kernel32.dll")] public static extern bool CloseHandle(IntPtr h);
        [DllImport("kernel32.dll")] public static extern uint SetThreadExecutionState(uint flags);
    }
}
