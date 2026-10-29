// Claude Status - Claude Usage (account-wide plan limits). Compiled together with indicator.cs (C# 5).
//
// Where the numbers come from (both are files Claude Code itself writes on this computer; Claude Status
// never reads credentials, never calls Anthropic and never starts a Claude conversation):
//   1. state\usage-statusline.json - written by statusline.ps1, which Claude Code runs as its status line
//      command. Claude Code passes the documented "rate_limits" data (5-hour and 7-day windows: percent
//      used + reset time; a spend limit behind a Claude apps gateway) after each API response.
//   2. "cachedUsageUtilization" in Claude Code's global config (.claude.json) - Claude Code's own copy of
//      the last /usage result. Undocumented: read defensively, used only when it is tagged with the
//      account currently signed in, and ignored if its shape changes.
// The account signed in to Claude Code ("oauthAccount.accountUuid" in .claude.json) decides what is shown:
// data recorded for another account (or while signed out) is never presented as current.
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace ClaudeStatus
{
    public enum UsageState { Loading, Fresh, Stale, Unavailable }

    public class UsageLimit
    {
        public string Key = "", Title = "", Source = "", Extra = "";
        public double Percent;                              // used, 0..100 (a spend limit can pass 100)
        public DateTime ResetsUtc = DateTime.MinValue;      // MinValue = no reset time reported
        public DateTime ObservedUtc = DateTime.MinValue;    // when Claude Code reported this value
        public UsageLimit Copy() { return (UsageLimit)MemberwiseClone(); }
    }

    public class UsageSnapshot
    {
        public UsageState State = UsageState.Loading;
        public string Message = "";                        // why unavailable / stale, or a hint
        public List<UsageLimit> Limits = new List<UsageLimit>();
        public List<string> Notes = new List<string>();    // other /usage details (extra usage, breakdown)
        public List<string> Sources = new List<string>();  // which sources the shown values came from
        public DateTime ObservedUtc = DateTime.MinValue;   // newest observation among the shown values
        public DateTime CheckedUtc = DateTime.MinValue;    // last time the local files were read
    }

    // ---------- pure logic (no I/O) ----------
    public static class Usage
    {
        public const double FreshMinutes = 15;
        public const string FeedSource = "status line", CacheSource = "/usage cache";
        static readonly DateTime Epoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        // A one-way, shortened hash of the account ID, so the status line feed can say which account it
        // belongs to without storing the ID itself. statusline.ps1 computes the same value.
        public static string AccountKey(string accountUuid)
        {
            if (string.IsNullOrEmpty(accountUuid)) return "";
            using (var sha = SHA256.Create())
            {
                var h = sha.ComputeHash(Encoding.UTF8.GetBytes("claude-status|" + accountUuid.Trim().ToLowerInvariant()));
                var sb = new StringBuilder();
                for (int i = 0; i < 8; i++) sb.Append(h[i].ToString("x2"));
                return sb.ToString();
            }
        }

        public static Dictionary<string, object> Obj(Dictionary<string, object> d, string k)
        {
            object v; return d != null && d.TryGetValue(k, out v) ? v as Dictionary<string, object> : null;
        }

        static bool Num(Dictionary<string, object> d, string k, out double x)
        {
            x = 0; object v;
            if (d == null || !d.TryGetValue(k, out v) || v == null || v is string || v is bool) return false;
            try { x = Convert.ToDouble(v, CultureInfo.InvariantCulture); return !double.IsNaN(x) && !double.IsInfinity(x); } catch { return false; }
        }

        static DateTime IsoTime(Dictionary<string, object> d, string k)
        {
            DateTimeOffset t;
            string s = Board.Str(d, k);
            return s != "" && DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out t) ? t.UtcDateTime : DateTime.MinValue;
        }

        public static DateTime FromUnixSeconds(double s) { return s > 0 && s < 4e10 ? Epoch.AddSeconds(s) : DateTime.MinValue; }
        public static DateTime FromUnixMs(double ms) { return ms > 0 && ms < 4e13 ? Epoch.AddMilliseconds(ms) : DateTime.MinValue; }
        public static long Ms(DateTime utc) { return utc == DateTime.MinValue ? 0 : (long)(utc - Epoch).TotalMilliseconds; }

        // The account Claude Code is signed in with ("" = signed out, or not a Claude account sign-in).
        public static string CurrentAccount(Dictionary<string, object> claudeJson)
        {
            return Board.Str(Obj(claudeJson, "oauthAccount"), "accountUuid").Trim();
        }

        // Titles follow Claude Code's /usage panel.
        public static string TitleFor(string key)
        {
            switch (key)
            {
                case "five_hour": return "Current session (5-hour window)";
                case "seven_day": return "Current week (all models)";
                case "seven_day_sonnet": return "Current week (Sonnet only)";
                case "seven_day_opus": return "Current week (Opus only)";
                case "spend_limit": return "Spend limit";
                default: return key;
            }
        }

        // state\usage-statusline.json (statusline.ps1). null if it belongs to another account.
        public static List<UsageLimit> FromFeed(Dictionary<string, object> feed, string accountKey)
        {
            if (feed == null || accountKey == "" || Board.Str(feed, "account") != accountKey) return null;
            var list = new List<UsageLimit>();
            var w = Obj(feed, "windows");
            if (w == null) return list;
            foreach (var key in new[] { "five_hour", "seven_day", "spend_limit" })
            {
                var o = Obj(w, key);
                double pct, resets;
                if (o == null || !Num(o, "pct", out pct)) continue;
                var l = new UsageLimit { Key = key, Title = TitleFor(key), Percent = pct, Source = FeedSource, ObservedUtc = IsoTime(o, "at") };
                if (Num(o, "resets", out resets)) l.ResetsUtc = FromUnixSeconds(resets);
                if (key == "spend_limit")
                {
                    double used, limit;
                    string period = Board.Str(o, "period");
                    if (period != "") l.Title = "Spend limit (" + period + ")";
                    if (Num(o, "used_usd", out used) && Num(o, "limit_usd", out limit))
                        l.Extra = "$" + used.ToString("0.00", CultureInfo.InvariantCulture) + " of $" + limit.ToString("0.00", CultureInfo.InvariantCulture);
                }
                list.Add(l);
            }
            return list;
        }

        // Claude Code's cached /usage result. null unless it is tagged with this account and parses.
        public static List<UsageLimit> FromCache(Dictionary<string, object> cache, string accountUuid, List<string> notes)
        {
            if (cache == null || accountUuid == "" || !string.Equals(Board.Str(cache, "accountUuid"), accountUuid, StringComparison.OrdinalIgnoreCase)) return null;
            double fetched;
            var u = Obj(cache, "utilization");
            if (u == null || !Num(cache, "fetchedAtMs", out fetched)) return null;
            DateTime at = FromUnixMs(fetched);
            var list = new List<UsageLimit>();
            foreach (var key in new[] { "five_hour", "seven_day", "seven_day_sonnet", "seven_day_opus" })
            {
                var o = Obj(u, key);
                double pct;
                if (o == null || !Num(o, "utilization", out pct)) continue;
                list.Add(new UsageLimit { Key = key, Title = TitleFor(key), Percent = pct, ResetsUtc = IsoTime(o, "resets_at"), Source = CacheSource, ObservedUtc = at });
            }

            // "limits": the same windows plus any others (e.g. model-scoped). Add only ones not already shown.
            object lv;
            if (u.TryGetValue("limits", out lv) && lv is System.Collections.IEnumerable && !(lv is string))
                foreach (var item in (System.Collections.IEnumerable)lv)
                {
                    var o = item as Dictionary<string, object>;
                    double pct;
                    if (o == null || !Num(o, "percent", out pct)) continue;
                    string kind = Board.Str(o, "kind");
                    if (kind == "session" || kind == "weekly_all") continue;
                    DateTime resets = IsoTime(o, "resets_at");
                    bool dup = false;
                    foreach (var l in list) if (Math.Abs(l.Percent - pct) < 0.01 && l.ResetsUtc == resets && l.Key.StartsWith("seven_day_")) dup = true;
                    if (dup) continue;
                    var scope = Obj(o, "scope");
                    string model = Board.Str(Obj(scope, "model"), "display_name"), surface = Board.Str(Obj(scope, "surface"), "display_name");
                    string weekly = Board.Str(o, "group") == "weekly" ? "Current week" : "Limit";
                    string title = model != "" ? weekly + " (" + model + " only)" : surface != "" ? weekly + " (" + surface + ")" : Humanize(kind);
                    list.Add(new UsageLimit { Key = "limit:" + kind, Title = title, Percent = pct, ResetsUtc = resets, Source = CacheSource, ObservedUtc = at });
                }

            if (notes != null)
            {
                // Extra usage (pay-as-you-go beyond the plan): amounts only from the unambiguous "spend" block.
                var spend = Obj(u, "spend");
                var extra = Obj(u, "extra_usage");
                object en;
                bool? enabled = null;
                if (spend != null && spend.TryGetValue("enabled", out en) && en is bool) enabled = (bool)en;
                else if (extra != null && extra.TryGetValue("is_enabled", out en) && en is bool) enabled = (bool)en;
                if (enabled == false) notes.Add("Extra usage: off");
                else if (enabled == true)
                {
                    string used = Money(Obj(spend, "used")), limit = Money(Obj(spend, "limit"));
                    double p;
                    string s = "Extra usage: on";
                    if (used != "") s += " · " + used + (limit != "" ? " of " + limit : "") + " used";
                    if (Num(spend, "percent", out p) && limit != "") s += " (" + PercentText(p) + ")";
                    notes.Add(s);
                }
                // What this week's usage went to (Claude Code, chats, ...), as /usage shows it.
                var bd = Obj(u, "seven_day_breakdown");
                object rows;
                if (bd != null && bd.TryGetValue("rows", out rows) && rows is System.Collections.IEnumerable)
                {
                    var parts = new List<string>();
                    foreach (var item in (System.Collections.IEnumerable)rows)
                    {
                        var o = item as Dictionary<string, object>;
                        double p;
                        string name = Board.Str(o, "display_name");
                        if (o != null && name != "" && Num(o, "percent", out p) && p > 0) parts.Add(name + " " + PercentText(p));
                    }
                    if (parts.Count > 0) notes.Add("This week's usage by product: " + string.Join(", ", parts));
                }
            }
            return list;
        }

        static string Money(Dictionary<string, object> m)
        {
            double minor, exp;
            if (m == null || !Num(m, "amount_minor", out minor)) return "";
            if (!Num(m, "exponent", out exp) || exp < 0 || exp > 6) exp = 2;
            string cur = Board.Str(m, "currency").ToUpperInvariant();
            string amount = (minor / Math.Pow(10, exp)).ToString("N" + (int)exp, CultureInfo.InvariantCulture);
            return cur == "USD" || cur == "" ? "$" + amount : amount + " " + cur;
        }

        static string Humanize(string kind)
        {
            string s = (kind ?? "").Replace('_', ' ').Trim();
            return s == "" ? "Limit" : char.ToUpperInvariant(s[0]) + s.Substring(1);
        }

        // Per window, the newest observation wins; primary windows first, in /usage order.
        public static List<UsageLimit> Merge(List<UsageLimit> feed, List<UsageLimit> cache)
        {
            var byKey = new Dictionary<string, UsageLimit>();
            var order = new List<string>();
            foreach (var src in new[] { feed, cache })
                if (src != null)
                    foreach (var l in src)
                    {
                        UsageLimit have;
                        if (!byKey.TryGetValue(l.Key, out have)) { byKey[l.Key] = l.Copy(); order.Add(l.Key); }
                        else if (l.ObservedUtc > have.ObservedUtc) byKey[l.Key] = l.Copy();
                    }
            // primary windows in /usage order, then any others in the order they were found
            var rank = new List<string> { "five_hour", "seven_day", "seven_day_sonnet", "seven_day_opus" };
            var result = new List<UsageLimit>();
            foreach (var k in rank) if (byKey.ContainsKey(k)) result.Add(byKey[k]);
            foreach (var k in order) if (!rank.Contains(k)) result.Add(byKey[k]);
            return result;
        }

        public static bool PastReset(UsageLimit l, DateTime nowUtc) { return l.ResetsUtc != DateTime.MinValue && l.ResetsUtc <= nowUtc; }

        // Fresh while the newest shown value is recent; otherwise stale. Values are never zeroed or guessed.
        public static UsageState StateFor(List<UsageLimit> limits, DateTime observedUtc, DateTime nowUtc, bool readFailed)
        {
            if (limits == null || limits.Count == 0) return UsageState.Unavailable;
            if (readFailed || observedUtc == DateTime.MinValue || (nowUtc - observedUtc).TotalMinutes > FreshMinutes) return UsageState.Stale;
            return UsageState.Fresh;
        }

        public static string StateLabel(UsageState s)
        {
            switch (s) { case UsageState.Fresh: return "Fresh"; case UsageState.Stale: return "Stale"; case UsageState.Loading: return "Loading…"; default: return "Unavailable"; }
        }

        // 14% / <1% / 100%: whole numbers like /usage; anything above zero shows as at least <1%.
        public static string PercentText(double p)
        {
            if (p > 0 && p < 0.5) return "<1%";
            return Math.Round(p, MidpointRounding.AwayFromZero).ToString("0", CultureInfo.InvariantCulture) + "%";
        }

        public static string RemainingText(double used)
        {
            double left = 100 - used;
            if (left <= 0) return "0% left";
            if (left < 0.5) return "<1% left";
            return Math.Round(left, MidpointRounding.AwayFromZero).ToString("0", CultureInfo.InvariantCulture) + "% left";
        }

        // Countdown to a reset: <1m, 12m, 4h 07m, 2d 3h. (phone.js countdown() matches this.)
        public static string Countdown(TimeSpan t)
        {
            if (t < TimeSpan.Zero) t = TimeSpan.Zero;
            long s = (long)Math.Floor(t.TotalSeconds);
            long m = s / 60, h = s / 3600, d = s / 86400;
            if (m < 1) return "<1m";
            if (h < 1) return m + "m";
            if (d < 1) return h + "h " + (m % 60).ToString("00") + "m";
            return d + "d " + (h % 24) + "h";
        }

        // Local reset time: "today 1:30 AM", "tomorrow 9:00 AM", "Sun Nov 1, 9:00 AM".
        public static string ResetClock(DateTime resetUtc, DateTime nowUtc)
        {
            DateTime r = resetUtc.ToLocalTime(), n = nowUtc.ToLocalTime();
            string time = r.ToString("t", CultureInfo.CurrentCulture);
            if (r.Date == n.Date) return "today " + time;
            if (r.Date == n.Date.AddDays(1)) return "tomorrow " + time;
            return r.ToString("ddd MMM d", CultureInfo.CurrentCulture) + ", " + time;
        }

        // "Resets today 1:30 AM (in 4h 07m)", or, once the time has passed, a note that the new allowance
        // hasn't been reported yet (the shown percentage is the last one Claude Code reported).
        public static string ResetText(UsageLimit l, DateTime nowUtc)
        {
            if (l.ResetsUtc == DateTime.MinValue) return "No reset time reported";
            if (PastReset(l, nowUtc))
                return "Reset " + ResetClock(l.ResetsUtc, nowUtc) + " (" + Countdown(nowUtc - l.ResetsUtc) + " ago) · waiting for Claude Code to report the new allowance";
            return "Resets " + ResetClock(l.ResetsUtc, nowUtc) + " (in " + Countdown(l.ResetsUtc - nowUtc) + ")";
        }

        public static string AgeText(DateTime observedUtc, DateTime nowUtc)
        {
            if (observedUtc == DateTime.MinValue) return "unknown";
            return Board.Ago(nowUtc - observedUtc);
        }

        // Short form for the desktop strip / status line: "5h 14% · wk 57%".
        public static string ShortLabel(string key)
        {
            switch (key) { case "five_hour": return "5h"; case "seven_day": return "Wk"; case "seven_day_sonnet": return "Son"; case "seven_day_opus": return "Opus"; case "spend_limit": return "$"; default: return "•"; }
        }

        // What the phone receives: display data only (no account IDs, hashes, paths or file contents).
        public static Dictionary<string, object> ToPhone(UsageSnapshot s)
        {
            var lim = new List<object>();
            foreach (var l in s.Limits)
                lim.Add(new Dictionary<string, object> {
                    { "k", ShortLabel(l.Key) }, { "t", l.Title }, { "p", Math.Round(l.Percent, 1) }, { "r", Ms(l.ResetsUtc) },
                    { "o", Ms(l.ObservedUtc) }, { "src", l.Source }, { "x", l.Extra } });
            return new Dictionary<string, object> {
                { "st", s.State.ToString().ToLowerInvariant() }, { "msg", s.Message }, { "obs", Ms(s.ObservedUtc) }, { "chk", Ms(s.CheckedUtc) },
                { "fresh", (long)(FreshMinutes * 60000) }, { "lim", lim }, { "notes", new List<string>(s.Notes) }, { "src", new List<string>(s.Sources) } };
        }

        // Changes the phone needs to be told about (not the "checked" time, which moves every read).
        public static string Signature(UsageSnapshot s)
        {
            var sb = new StringBuilder(s.State + "|" + s.Message + "|" + Ms(s.ObservedUtc));
            foreach (var l in s.Limits) sb.Append('\n').Append(l.Key).Append('|').Append(l.Percent).Append('|').Append(Ms(l.ResetsUtc)).Append('|').Append(Ms(l.ObservedUtc)).Append('|').Append(l.Extra);
            foreach (var n in s.Notes) sb.Append('\n').Append(n);
            return sb.ToString();
        }
    }

    // ---------- reading the local files and deciding when to look again ----------
    // Everything runs on the UI thread (WinForms timers, watchers synchronised to the form), so reads never
    // overlap. There's nothing remote to poll: the files are re-read when they change, once a minute as a
    // safety net, shortly after a reset time passes, and on Refresh. Failed reads back off (2 s .. 60 s).
    public sealed class UsageMonitor : IDisposable
    {
        readonly string feedPath, claudeJsonPath, settingsPath;
        readonly Func<DateTime> clock;
        public event Action Changed;
        UsageSnapshot snap = new UsageSnapshot();
        string lastSig = "", account = "", accountKey = "";
        bool haveAccountFile, statusLineInstalled;
        List<UsageLimit> feedLimits, cacheLimits;
        List<string> cacheNotes = new List<string>();
        DateTime feedStamp = DateTime.MinValue, jsonStamp = DateTime.MinValue, settingsStamp = DateTime.MinValue, lastManual = DateTime.MinValue;
        int failures;
        bool reading, readFailed, disposed;
        FileSystemWatcher feedWatch, jsonWatch;
        Timer debounce, safety, retry, resetTimer;

        public UsageMonitor(string root, Func<DateTime> clock)
        {
            this.clock = clock ?? (() => DateTime.UtcNow);
            feedPath = Path.Combine(root, "state\\usage-statusline.json");
            string claudeDir = Path.GetDirectoryName(Path.GetFullPath(root).TrimEnd('\\'));
            settingsPath = Path.Combine(claudeDir, "settings.json");
            claudeJsonPath = ClaudeJsonPath(claudeDir);
        }

        // Claude Code keeps its global config next to the default config folder (~\.claude.json), or inside a
        // custom CLAUDE_CONFIG_DIR. statusline.ps1 uses the same rule.
        public static string ClaudeJsonPath(string claudeDir)
        {
            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            string def = Path.Combine(home, ".claude");
            if (string.Equals(Path.GetFullPath(claudeDir).TrimEnd('\\'), Path.GetFullPath(def).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
                return Path.Combine(home, ".claude.json");
            return Path.Combine(claudeDir, ".claude.json");
        }

        public UsageSnapshot Snapshot { get { return snap; } }
        public string FeedPath { get { return feedPath; } }
        public string ClaudeJsonFile { get { return claudeJsonPath; } }
        public bool StatusLineInstalled { get { return statusLineInstalled; } }
        public int Failures { get { return failures; } }
        public int RetryDelayMs { get { return failures == 0 ? 0 : (int)Math.Min(60000, 2000 * Math.Pow(2, Math.Min(failures - 1, 5))); } }

        // Watches and timers (not used by the tests, which call ReadNow / Evaluate directly).
        public void Start(ISynchronizeInvoke ui)
        {
            debounce = new Timer { Interval = 1500 };
            debounce.Tick += (s, e) => { debounce.Stop(); ReadNow(false); };
            safety = new Timer { Interval = 60000 };
            safety.Tick += (s, e) => ReadNow(false);
            retry = new Timer();
            retry.Tick += (s, e) => { retry.Stop(); ReadNow(true); };
            resetTimer = new Timer();
            resetTimer.Tick += (s, e) => { resetTimer.Stop(); ReadNow(true); };
            FileSystemEventHandler h = (s, e) => { if (!debounce.Enabled) debounce.Start(); };   // at most one read per 1.5 s
            RenamedEventHandler rh = (s, e) => { if (!debounce.Enabled) debounce.Start(); };
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(feedPath));
                feedWatch = new FileSystemWatcher(Path.GetDirectoryName(feedPath), Path.GetFileName(feedPath)) { SynchronizingObject = ui, NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite };
                feedWatch.Changed += h; feedWatch.Created += h; feedWatch.Deleted += h; feedWatch.Renamed += rh;
                feedWatch.EnableRaisingEvents = true;
            }
            catch { feedWatch = null; }
            try
            {
                jsonWatch = new FileSystemWatcher(Path.GetDirectoryName(claudeJsonPath), Path.GetFileName(claudeJsonPath)) { SynchronizingObject = ui, NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite };
                jsonWatch.Changed += h; jsonWatch.Created += h; jsonWatch.Deleted += h; jsonWatch.Renamed += rh;
                jsonWatch.EnableRaisingEvents = true;
            }
            catch { jsonWatch = null; }
            safety.Start();
            ReadNow(true);
        }

        // The Refresh button (desktop or phone): re-read now. At most once every 2 seconds.
        public bool RefreshNow()
        {
            DateTime now = clock();
            if ((now - lastManual).TotalSeconds < 2) return false;
            lastManual = now;
            failures = 0;
            if (retry != null) retry.Stop();
            ReadNow(true);
            return true;
        }

        static DateTime Stamp(string path) { try { return File.Exists(path) ? File.GetLastWriteTimeUtc(path) : DateTime.MinValue; } catch { return DateTime.MinValue; } }

        static string ReadShared(string path)
        {
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var sr = new StreamReader(fs, new UTF8Encoding(false), true)) return sr.ReadToEnd();
        }

        static Dictionary<string, object> Parse(string text)
        {
            var ser = new JavaScriptSerializer { MaxJsonLength = 64 * 1024 * 1024, RecursionLimit = 256 };
            return ser.Deserialize<Dictionary<string, object>>(text);
        }

        // Re-reads whichever files changed (all of them when forced), then re-evaluates.
        public void ReadNow(bool force)
        {
            if (reading || disposed) return;
            reading = true;
            bool failed = false;
            try
            {
                DateTime ss = Stamp(settingsPath);
                if (force || ss != settingsStamp)
                {
                    settingsStamp = ss;
                    try { statusLineInstalled = ss != DateTime.MinValue && ReadShared(settingsPath).IndexOf("status-indicator/statusline.ps1", StringComparison.OrdinalIgnoreCase) >= 0; }
                    catch { }
                }

                // 1. Which account is signed in (and Claude Code's /usage cache, from the same file).
                DateTime js = Stamp(claudeJsonPath);
                if (js == DateTime.MinValue) { haveAccountFile = false; SetAccount(""); cacheLimits = null; cacheNotes = new List<string>(); jsonStamp = js; }
                else if (force || js != jsonStamp)
                {
                    try
                    {
                        var d = Parse(ReadShared(claudeJsonPath));
                        if (d == null) throw new InvalidDataException();
                        haveAccountFile = true;
                        jsonStamp = js;
                        SetAccount(Usage.CurrentAccount(d));
                        var notes = new List<string>();
                        try { cacheLimits = Usage.FromCache(Usage.Obj(d, "cachedUsageUtilization"), account, notes); }
                        catch { cacheLimits = null; notes.Clear(); }   // unknown cache shape: ignore that source
                        cacheNotes = cacheLimits != null ? notes : new List<string>();
                    }
                    catch { failed = true; }   // Claude Code mid-write: keep what we had and try again soon
                }

                // 2. The status line feed.
                DateTime fs = Stamp(feedPath);
                if (fs == DateTime.MinValue) { feedLimits = null; feedStamp = fs; }
                else if (force || fs != feedStamp)
                {
                    try
                    {
                        var d = Parse(ReadShared(feedPath));
                        feedStamp = fs;
                        feedLimits = Usage.FromFeed(d, accountKey);
                    }
                    catch { failed = true; }
                }
            }
            finally { reading = false; }

            readFailed = failed;
            if (failed)
            {
                failures++;
                if (retry != null) { retry.Stop(); retry.Interval = RetryDelayMs; retry.Start(); }
            }
            else failures = 0;
            snap.CheckedUtc = clock();
            Evaluate();
        }

        // A different account (or signing out) drops everything recorded so far for the previous one.
        void SetAccount(string a)
        {
            if (a == account) return;
            account = a;
            accountKey = Usage.AccountKey(a);
            feedLimits = null; cacheLimits = null; cacheNotes = new List<string>();
            feedStamp = DateTime.MinValue;   // re-read the feed against the new account
        }

        // Recomputes what to show (also called once a minute so "fresh" turns "stale" on time).
        public void Evaluate()
        {
            DateTime now = clock();
            var s = new UsageSnapshot { CheckedUtc = snap.CheckedUtc };
            if (!haveAccountFile && account == "")
            {
                s.State = snap.CheckedUtc == DateTime.MinValue ? UsageState.Loading : UsageState.Unavailable;
                if (s.State == UsageState.Unavailable)
                    s.Message = readFailed ? "Couldn't read Claude Code's account information yet. Retrying in " + Math.Max(1, RetryDelayMs / 1000) + " s."
                        : "Couldn't find Claude Code's account information on this computer (" + Path.GetFileName(claudeJsonPath) + ").";
            }
            else if (account == "")
            {
                s.State = UsageState.Unavailable;
                s.Message = "Claude Code isn't signed in with a Claude subscription account, so there are no plan limits to show. (Sign in with /login; API-key and cloud-provider sign-ins have no plan limits.)";
            }
            else
            {
                s.Limits = Usage.Merge(feedLimits, cacheLimits);
                foreach (var l in s.Limits)
                {
                    if (l.ObservedUtc > s.ObservedUtc) s.ObservedUtc = l.ObservedUtc;
                    if (!s.Sources.Contains(l.Source)) s.Sources.Add(l.Source);
                }
                if (cacheLimits != null) s.Notes.AddRange(cacheNotes);
                s.State = Usage.StateFor(s.Limits, s.ObservedUtc, now, readFailed);
                if (s.State == UsageState.Unavailable)
                    s.Message = statusLineInstalled
                        ? "No usage reported for this account yet. It appears after Claude Code's next response in a session (or after you open /usage once)."
                        : "No usage reported for this account yet, and the Claude Status status line isn't set up in Claude Code's settings.json. Re-run install.ps1 to add it.";
                else if (readFailed) s.Message = "Couldn't read the latest data; showing the last values. Retrying in " + Math.Max(1, RetryDelayMs / 1000) + " s.";
                else if (s.State == UsageState.Stale) s.Message = "These are the latest numbers Claude Code reported. They update after a Claude Code response in any session (or when you open /usage).";
            }
            if (readFailed && s.State == UsageState.Unavailable && snap.Limits.Count > 0 && account != "")
            {
                // A failed read never hides the last good values for the same account.
                s.Limits = snap.Limits; s.Notes = snap.Notes; s.Sources = snap.Sources; s.ObservedUtc = snap.ObservedUtc;
                s.State = UsageState.Stale;
                s.Message = "Couldn't read the latest data; showing the last values. Retrying in " + Math.Max(1, RetryDelayMs / 1000) + " s.";
            }
            snap = s;
            ScheduleResetCheck(now);
            string sig = Usage.Signature(s);
            if (sig != lastSig) { lastSig = sig; var c = Changed; if (c != null) c(); }
        }

        // Look again shortly after the next reset time, and once more a few minutes later, to pick up the
        // new allowance as soon as Claude Code reports it. The shown values only change when it does.
        void ScheduleResetCheck(DateTime now)
        {
            if (resetTimer == null) return;
            DateTime next = DateTime.MaxValue;
            foreach (var l in snap.Limits)
            {
                if (l.ResetsUtc == DateTime.MinValue) continue;
                foreach (var t in new[] { l.ResetsUtc.AddSeconds(15), l.ResetsUtc.AddMinutes(3) })
                    if (t > now && t < next) next = t;
            }
            resetTimer.Stop();
            if (next == DateTime.MaxValue) return;
            double ms = (next - now).TotalMilliseconds;
            if (ms > 50000) return;   // far away: the once-a-minute check gets closer first
            resetTimer.Interval = Math.Max(250, (int)ms);
            resetTimer.Start();
        }

        public void Dispose()
        {
            disposed = true;
            foreach (var w in new[] { feedWatch, jsonWatch }) if (w != null) try { w.EnableRaisingEvents = false; w.Dispose(); } catch { }
            foreach (var t in new[] { debounce, safety, retry, resetTimer }) if (t != null) { t.Stop(); t.Dispose(); }
        }
    }

    // ---------- the detail window (click the usage strip on the Worker Board) ----------
    public class UsageForm : Form
    {
        static readonly Color Bg = Color.FromArgb(30, 30, 30), Muted = Color.FromArgb(160, 160, 160), NameColor = Color.FromArgb(228, 228, 228);
        static readonly Color Line = Color.FromArgb(52, 52, 52), Track = Color.FromArgb(52, 52, 52), Grey = Color.FromArgb(128, 128, 128);
        static readonly Color Green = Color.FromArgb(63, 185, 80), Red = Color.FromArgb(248, 81, 73), Orange = Color.FromArgb(240, 136, 62);
        public static readonly Color Blue = Color.FromArgb(88, 166, 255);
        const TextFormatFlags Wrap = TextFormatFlags.Left | TextFormatFlags.Top | TextFormatFlags.WordBreak | TextFormatFlags.NoPadding;
        const TextFormatFlags One = TextFormatFlags.Left | TextFormatFlags.Top | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis;

        readonly UsageMonitor monitor;
        readonly float scale;
        readonly Font head, body, small, bold;
        readonly Button refresh;
        readonly Timer tick;

        public UsageForm(UsageMonitor monitor, float scale)
        {
            this.monitor = monitor; this.scale = scale;
            Text = "Claude Usage";
            FormBorderStyle = FormBorderStyle.FixedToolWindow;
            MaximizeBox = false; MinimizeBox = false; ShowInTaskbar = false; TopMost = true;
            StartPosition = FormStartPosition.Manual;
            BackColor = Bg; ForeColor = NameColor;
            DoubleBuffered = true;
            head = new Font("Segoe UI", 12.5f * scale, GraphicsUnit.Pixel);
            body = new Font("Segoe UI", 12f * scale, GraphicsUnit.Pixel);
            small = new Font("Segoe UI", 11f * scale, GraphicsUnit.Pixel);
            bold = new Font("Segoe UI", 10.5f * scale, FontStyle.Bold, GraphicsUnit.Pixel);
            ClientSize = new Size(S(340), S(220));
            refresh = new Button { Text = "Refresh", FlatStyle = FlatStyle.Flat, BackColor = Color.FromArgb(44, 44, 44), ForeColor = NameColor, Font = small, AutoSize = true, UseVisualStyleBackColor = false };
            refresh.FlatAppearance.BorderColor = Color.FromArgb(70, 70, 70);
            refresh.Click += (s, e) => { if (!monitor.RefreshNow()) Invalidate(); };
            Controls.Add(refresh);
            KeyPreview = true;
            KeyDown += (s, e) => { if (e.KeyCode == Keys.Escape) Close(); };
            tick = new Timer { Interval = 1000 };
            tick.Tick += (s, e) => Invalidate();   // countdowns
            tick.Start();
            monitor.Changed += OnChanged;
        }

        void OnChanged() { if (!IsDisposed) Invalidate(); }

        int S(float v) { return (int)Math.Round(v * scale); }

        public static Color BarColor(UsageLimit l, UsageState st, DateTime nowUtc)
        {
            if (st == UsageState.Stale || Usage.PastReset(l, nowUtc)) return Grey;
            return l.Percent >= 95 ? Red : l.Percent >= 80 ? Orange : Blue;
        }

        static Color StateColor(UsageState s) { return s == UsageState.Fresh ? Green : s == UsageState.Stale ? Orange : s == UsageState.Unavailable ? Red : Grey; }

        int Draw(Graphics g, string text, Font f, Color c, int x, int y, int w)
        {
            var sz = TextRenderer.MeasureText(g, text, f, new Size(w, int.MaxValue), Wrap);
            TextRenderer.DrawText(g, text, f, new Rectangle(x, y, w, sz.Height), c, Wrap);
            return sz.Height;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            var s = monitor.Snapshot;
            DateTime now = DateTime.UtcNow;
            int pad = S(12), w = ClientSize.Width - 2 * pad, y = S(10);

            y += Draw(g, "ACCOUNT-WIDE · shared by all your Claude workers and devices", bold, Muted, pad, y, w) + S(6);

            // status: Fresh / Stale / Loading / Unavailable, and when the numbers were last reported
            int dot = S(8);
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            using (var b = new SolidBrush(StateColor(s.State))) g.FillEllipse(b, pad, y + S(4), dot, dot);
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.None;
            string upd = Usage.StateLabel(s.State);
            if (s.ObservedUtc != DateTime.MinValue)
                upd += " · Last updated " + s.ObservedUtc.ToLocalTime().ToString("t", CultureInfo.CurrentCulture) + " (" + Usage.AgeText(s.ObservedUtc, now) + ")";
            y += Draw(g, upd, body, NameColor, pad + dot + S(6), y, w - dot - S(6)) + S(4);
            if (s.Message != "") y += Draw(g, s.Message, small, Muted, pad, y, w) + S(4);

            foreach (var l in s.Limits)
            {
                y += S(8);
                using (var pen = new Pen(Line)) g.DrawLine(pen, pad, y, pad + w, y);
                y += S(8);
                string used = Usage.PercentText(l.Percent) + " used";
                int uw = TextRenderer.MeasureText(g, used, body, Size.Empty, One).Width + S(3);
                TextRenderer.DrawText(g, l.Title, head, new Rectangle(pad, y, Math.Max(0, w - uw - S(8)), S(18)), NameColor, One);
                // right-aligned over the whole row, so it is never cut short by a measuring difference
                TextRenderer.DrawText(g, used, body, new Rectangle(pad, y + S(1), w, S(18)), NameColor, TextFormatFlags.Right | TextFormatFlags.Top | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding);
                y += S(20);
                var bar = new Rectangle(pad, y, w, S(8));
                using (var b = new SolidBrush(Track)) g.FillRectangle(b, bar);
                int fill = (int)Math.Round(bar.Width * Math.Max(0, Math.Min(100, l.Percent)) / 100.0);
                if (l.Percent > 0 && fill < S(2)) fill = S(2);
                using (var b = new SolidBrush(BarColor(l, s.State, now))) g.FillRectangle(b, new Rectangle(bar.Left, bar.Top, fill, bar.Height));
                y += S(12);
                y += Draw(g, Usage.RemainingText(l.Percent) + " · " + Usage.ResetText(l, now), small, Muted, pad, y, w);
                if (l.Extra != "") y += Draw(g, l.Extra, small, Muted, pad, y, w);
                // say so when this value is older than the newest one shown
                if (s.Limits.Count > 1 && (s.ObservedUtc - l.ObservedUtc).TotalMinutes > 5)
                    y += Draw(g, "From " + l.Source + ", as of " + Usage.AgeText(l.ObservedUtc, now), small, Muted, pad, y, w);
            }

            if (s.Notes.Count > 0)
            {
                y += S(8);
                using (var pen = new Pen(Line)) g.DrawLine(pen, pad, y, pad + w, y);
                y += S(8);
                foreach (var n in s.Notes) y += Draw(g, n, small, Muted, pad, y, w) + S(2);
            }

            y += S(10);
            string src = s.Sources.Count == 0 ? "Sources: Claude Code's status line data and /usage cache (local files only)"
                : "Source: " + string.Join(" + ", s.Sources.ConvertAll(x => x == Usage.FeedSource ? "Claude Code status line (documented)" : "Claude Code /usage cache (undocumented)"));
            y += Draw(g, src, small, Muted, pad, y, w) + S(2);
            if (s.CheckedUtc != DateTime.MinValue)
                y += Draw(g, "Checked " + s.CheckedUtc.ToLocalTime().ToString("T", CultureInfo.CurrentCulture) + ". Checking never uses your Claude allowance.", small, Muted, pad, y, w);
            y += S(8);
            refresh.Location = new Point(pad, y);
            y += refresh.Height + S(10);

            if (Math.Abs(ClientSize.Height - y) > 1) ClientSize = new Size(ClientSize.Width, y);   // repaints once at the new size
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            monitor.Changed -= OnChanged;
            tick.Stop(); tick.Dispose();
            base.OnFormClosed(e);
        }
    }
}
