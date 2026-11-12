// Claude Status Indicator - tiny always-on-top window. Compiled at launch by indicator.ps1 via Add-Type
// (uses only .NET Framework, which ships with Windows). Watches state\sessions\*.json written by hook.ps1.
// Two views: Status (the Worker Board, "Claude Workers", one row per session) and Queue (queue.cs: scheduled prompts,
// run by scheduler.cs). Switching views never pauses the scheduler or a running task.
// Written for the C# 5 compiler that Windows PowerShell 5.1's Add-Type uses (no C# 6+ syntax).
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.IO;
using System.Media;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using Microsoft.Win32;

namespace ClaudeStatus
{
    public class SessionInfo
    {
        public string Id = "", State = "", Detail = "", Project = "", Event = "";
        public DateTime Updated, StateSince, FirstSeen, ClaudeStarted;
        public int Version, ClaudePid;
        // Presentation only (derived from config, never stored): friendly name and "#n" for duplicates.
        public string Name = "";
        public int Number;
        public string DisplayName { get { return Number > 0 ? Name + " #" + Number : Name; } }
    }

    // ---------- pure logic (no UI, no I/O) ----------
    public static class Board
    {
        public const string Working = "WORKING", Done = "DONE", NeedsYou = "NEEDS_YOU", Error = "ERROR";
        const string Dot = " · ";

        public static string Str(Dictionary<string, object> d, string k) { object v; return d != null && d.TryGetValue(k, out v) && v != null ? v.ToString() : ""; }

        static DateTime Time(Dictionary<string, object> d, string k, DateTime fallback)
        {
            DateTime t;
            return DateTime.TryParse(Str(d, k), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out t) ? t.ToUniversalTime() : fallback;
        }

        // Reads a v1 or v2 state file. v1 files lack state_since / first_seen / process identity,
        // so those are inferred (last update, file creation time) or left empty.
        public static SessionInfo Parse(string id, Dictionary<string, object> d, DateTime fileCreatedUtc, DateTime fileWrittenUtc)
        {
            var si = new SessionInfo { Id = id, State = Str(d, "state"), Detail = Str(d, "detail"), Project = Str(d, "project"), Event = Str(d, "event") };
            int v;
            si.Version = int.TryParse(Str(d, "v"), out v) ? v : 1;
            si.Updated = Time(d, "updated", fileWrittenUtc);
            si.StateSince = Time(d, "state_since", si.Updated);
            DateTime firstFallback = fileCreatedUtc > DateTime.MinValue && fileCreatedUtc < si.StateSince ? fileCreatedUtc : si.StateSince;
            si.FirstSeen = Time(d, "first_seen", firstFallback);
            int pid;
            if (int.TryParse(Str(d, "claude_pid"), out pid) && pid > 0)
            {
                si.ClaudeStarted = Time(d, "claude_started", DateTime.MinValue);
                if (si.ClaudeStarted != DateTime.MinValue) si.ClaudePid = pid;
            }
            return si;
        }

        // Alias from config ("projectAliases": {"Folder_Name": "Friendly"}), else the folder name with underscores as spaces.
        public static string FriendlyName(string folder, IDictionary<string, object> aliases)
        {
            folder = (folder ?? "").Trim();
            if (folder == "") return "Claude Code";
            if (aliases != null)
                foreach (var kv in aliases)
                    if (string.Equals(kv.Key, folder, StringComparison.OrdinalIgnoreCase) && kv.Value != null && kv.Value.ToString().Trim() != "")
                        return kv.Value.ToString().Trim();
            string s = folder.Replace('_', ' ');
            while (s.Contains("  ")) s = s.Replace("  ", " ");
            s = s.Trim();
            return s == "" ? folder : s;
        }

        public static string Label(string st)
        {
            switch (st) { case Working: return "WORKING"; case Done: return "DONE"; case NeedsYou: return "NEEDS YOU"; case Error: return "ERROR"; default: return "IDLE"; }
        }

        public static int Rank(string st)
        {
            switch (st) { case NeedsYou: return 0; case Error: return 1; case Working: return 2; case Done: return 3; default: return 4; }
        }

        public static bool IsRecent(string mode) { return string.Equals(mode, "recent", StringComparison.OrdinalIgnoreCase); }

        // "urgency": NEEDS YOU, ERROR, WORKING, DONE; newest state change first within each.
        // "recent":  most recently updated first.  Ties are broken deterministically (first seen, then id).
        public static void Sort(List<SessionInfo> list, string mode)
        {
            bool recent = IsRecent(mode);
            list.Sort((a, b) =>
            {
                int c;
                if (recent) { c = b.Updated.CompareTo(a.Updated); if (c == 0) c = Rank(a.State).CompareTo(Rank(b.State)); }
                else { c = Rank(a.State).CompareTo(Rank(b.State)); if (c == 0) c = b.StateSince.CompareTo(a.StateSince); }
                if (c == 0) c = a.FirstSeen.CompareTo(b.FirstSeen);
                if (c == 0) c = string.CompareOrdinal(a.Id, b.Id);
                return c;
            });
        }

        // WORKING timer: 04:32, then 1h 05m, then 2d 3h.
        public static string Clock(TimeSpan t)
        {
            if (t.TotalHours < 1) return ((int)t.TotalMinutes).ToString("00") + ":" + t.Seconds.ToString("00");
            if (t.TotalDays < 1) return (int)t.TotalHours + "h " + t.Minutes.ToString("00") + "m";
            return (int)t.TotalDays + "d " + t.Hours + "h";
        }

        // Waiting time: <1m, 2m, 1h 5m, 3d.
        public static string Span(TimeSpan t)
        {
            if (t.TotalMinutes < 1) return "<1m";
            if (t.TotalHours < 1) return (int)t.TotalMinutes + "m";
            if (t.TotalDays < 1) return (int)t.TotalHours + "h" + (t.Minutes > 0 ? " " + t.Minutes + "m" : "");
            return (int)t.TotalDays + "d";
        }

        public static string Ago(TimeSpan t)
        {
            if (t.TotalMinutes < 1) return "just now";
            if (t.TotalHours < 1) return (int)t.TotalMinutes + "m ago";
            if (t.TotalDays < 1) return (int)t.TotalHours + "h ago";
            return (int)t.TotalDays + "d ago";
        }

        // Only a short, identifier-like error type is ever shown (never messages or bodies).
        public static string ShortError(string detail)
        {
            string d = (detail ?? "").Trim();
            switch (d.ToLowerInvariant())
            {
                case "": return "";
                case "authentication_failed": return "auth";
                case "billing_error": return "billing";
                case "server_error": return "server";
                case "max_output_tokens": return "max_tokens";
            }
            var sb = new StringBuilder();
            foreach (char ch in d) if (char.IsLetterOrDigit(ch) || ch == '_' || ch == '-' || ch == '.') sb.Append(ch);
            string s = sb.ToString();
            return s.Length > 16 ? s.Substring(0, 15) + "…" : s;
        }

        // Row status, e.g. "WORKING · 04:32", "NEEDS YOU · 2m", "DONE · 6m ago", "ERROR · rate_limit · 3m".
        // brief = true drops the error type (used when the row is too narrow).
        public static string StatusText(SessionInfo s, DateTime nowUtc, bool brief)
        {
            TimeSpan t = nowUtc - s.StateSince;
            if (t < TimeSpan.Zero) t = TimeSpan.Zero;
            switch (s.State)
            {
                case Working: return "WORKING" + Dot + Clock(t);
                case NeedsYou: return "NEEDS YOU" + Dot + Span(t);
                case Done: return "DONE" + Dot + Ago(t);
                case Error:
                    string e = brief ? "" : ShortError(s.Detail);
                    return "ERROR" + Dot + (e != "" ? e + Dot : "") + Span(t);
                default: return Label(s.State);
            }
        }

        public static string Summary(List<SessionInfo> list)
        {
            int n = 0, e = 0, w = 0, d = 0;
            foreach (var s in list)
                switch (s.State) { case NeedsYou: n++; break; case Error: e++; break; case Working: w++; break; case Done: d++; break; }
            var parts = new List<string>();
            if (n > 0) parts.Add(n + " needs you");
            if (e > 0) parts.Add(e + (e == 1 ? " error" : " errors"));
            if (w > 0) parts.Add(w + " working");
            if (d > 0) parts.Add(d + " done");
            return parts.Count == 0 ? "No workers" : string.Join(Dot, parts);
        }

        // processAlive: 1 = the recorded Claude process is running, 0 = verifiably gone, -1 = unknown.
        // DONE is never pruned automatically (it stays until dismissed). Active states are pruned once
        // their Claude process is gone (crash / killed terminal) or after staleHours without any update.
        public static bool ShouldPrune(SessionInfo s, DateTime nowUtc, double staleHours, int processAlive)
        {
            if (s.State == Done) return false;
            if ((nowUtc - s.Updated).TotalHours > staleHours) return true;
            return processAlive == 0;
        }

        // Where to put a window of the desired size whose remembered top-left is 'home'.
        // It grows down from home until the bottom of the working area (then the list scrolls), but is
        // moved up just enough that at least minHeight stays visible, and never leaves the working area.
        public static Rectangle Place(Point home, Size desired, Rectangle wa, int minHeight)
        {
            int w = Math.Min(desired.Width, wa.Width);
            int x = Math.Max(wa.Left, Math.Min(home.X, wa.Right - w));
            int need = Math.Min(Math.Min(desired.Height, minHeight), wa.Height);
            int y = Math.Max(wa.Top, Math.Min(home.Y, wa.Bottom - need));
            int h = Math.Min(desired.Height, wa.Bottom - y);
            return new Rectangle(x, y, w, h);
        }
    }

    // Assigns "#1, #2, ..." to sessions that share a friendly name. A session keeps its number while it
    // stays on the board; newcomers get the lowest free number (in first-seen order). A name with a single
    // session shows no number.
    public class WorkerNumbering
    {
        readonly Dictionary<string, Dictionary<string, int>> groups = new Dictionary<string, Dictionary<string, int>>(StringComparer.OrdinalIgnoreCase);

        public void Apply(List<SessionInfo> list)
        {
            var byName = new Dictionary<string, List<SessionInfo>>(StringComparer.OrdinalIgnoreCase);
            foreach (var s in list)
            {
                List<SessionInfo> g;
                if (!byName.TryGetValue(s.Name, out g)) byName[s.Name] = g = new List<SessionInfo>();
                g.Add(s);
            }
            foreach (var key in new List<string>(groups.Keys))
                if (!byName.ContainsKey(key) || byName[key].Count < 2) groups.Remove(key);

            foreach (var kv in byName)
            {
                var members = kv.Value;
                if (members.Count < 2) { members[0].Number = 0; continue; }
                Dictionary<string, int> nums;
                if (!groups.TryGetValue(kv.Key, out nums)) groups[kv.Key] = nums = new Dictionary<string, int>();
                var present = new HashSet<string>();
                foreach (var s in members) present.Add(s.Id);
                foreach (var id in new List<string>(nums.Keys)) if (!present.Contains(id)) nums.Remove(id);
                members.Sort((a, b) => { int c = a.FirstSeen.CompareTo(b.FirstSeen); return c != 0 ? c : string.CompareOrdinal(a.Id, b.Id); });
                var used = new HashSet<int>(nums.Values);
                foreach (var s in members)
                {
                    int n;
                    if (!nums.TryGetValue(s.Id, out n)) { n = 1; while (used.Contains(n)) n++; nums[s.Id] = n; used.Add(n); }
                    s.Number = n;
                }
            }
        }
    }

    public static class Json
    {
        // Re-indents compact JSON with 2 spaces (same output style as install.ps1's Format-JsonText).
        public static string Pretty(string json)
        {
            var sb = new StringBuilder();
            int indent = 0; bool inStr = false, esc = false;
            for (int i = 0; i < json.Length; i++)
            {
                char c = json[i];
                if (inStr)
                {
                    sb.Append(c);
                    if (esc) esc = false; else if (c == '\\') esc = true; else if (c == '"') inStr = false;
                    continue;
                }
                if (c == '"') { inStr = true; sb.Append(c); continue; }
                if (c == '{' || c == '[')
                {
                    char close = c == '{' ? '}' : ']';
                    if (i + 1 < json.Length && json[i + 1] == close) { sb.Append(c).Append(close); i++; continue; }
                    indent++; sb.Append(c).Append("\r\n").Append(' ', indent * 2); continue;
                }
                if (c == '}' || c == ']') { indent--; sb.Append("\r\n").Append(' ', indent * 2).Append(c); continue; }
                if (c == ',') { sb.Append(",\r\n").Append(' ', indent * 2); continue; }
                if (c == ':') { sb.Append(": "); continue; }
                if (char.IsWhiteSpace(c)) continue;
                sb.Append(c);
            }
            return sb.Append("\r\n").ToString();
        }

        public static void WriteAtomic(string path, string text)
        {
            string tmp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllText(tmp, text, new UTF8Encoding(false));
            try { if (File.Exists(path)) File.Replace(tmp, path, null); else File.Move(tmp, path); }
            finally { if (File.Exists(tmp)) try { File.Delete(tmp); } catch { } }
        }
    }

    // ---------- worker -> window identification (local Win32 process/window metadata only) ----------
    static class Native
    {
        public delegate bool EnumWindowsProc(IntPtr h, IntPtr l);
        [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct PROCESSENTRY32
        {
            public uint dwSize, cntUsage, th32ProcessID; public IntPtr th32DefaultHeapID; public uint th32ModuleID, cntThreads, th32ParentProcessID;
            public int pcPriClassBase; public uint dwFlags; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szExeFile;
        }
        [DllImport("user32.dll")] public static extern bool EnumWindows(EnumWindowsProc f, IntPtr l);
        [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
        [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
        [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr h);
        [DllImport("user32.dll")] public static extern IntPtr GetWindow(IntPtr h, uint cmd);
        [DllImport("user32.dll")] public static extern IntPtr GetAncestor(IntPtr h, uint flags);
        [DllImport("user32.dll")] public static extern int GetWindowLong(IntPtr h, int index);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetClassName(IntPtr h, StringBuilder s, int n);
        [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
        [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(IntPtr h, int attr, out int value, int size);
        [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
        [DllImport("user32.dll")] public static extern bool BringWindowToTop(IntPtr h);
        [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int cmd);
        [DllImport("user32.dll")] public static extern bool AttachThreadInput(uint a, uint b, bool attach);
        [DllImport("kernel32.dll")] public static extern uint GetCurrentThreadId();
        [DllImport("user32.dll")] public static extern IntPtr OpenInputDesktop(uint flags, bool inherit, uint access);
        [DllImport("user32.dll")] public static extern bool CloseDesktop(IntPtr h);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern bool GetUserObjectInformation(IntPtr h, int index, StringBuilder info, int length, out int needed);
        [DllImport("kernel32.dll", SetLastError = true)] public static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
        [DllImport("kernel32.dll")] public static extern bool GetProcessTimes(IntPtr h, out long creation, out long exit, out long kernel, out long user);
        [DllImport("kernel32.dll")] public static extern bool GetExitCodeProcess(IntPtr h, out uint code);
        [DllImport("kernel32.dll")] public static extern bool CloseHandle(IntPtr h);
        [DllImport("kernel32.dll", SetLastError = true)] public static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint pid);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] public static extern bool Process32FirstW(IntPtr h, ref PROCESSENTRY32 e);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] public static extern bool Process32NextW(IntPtr h, ref PROCESSENTRY32 e);
        [DllImport("kernel32.dll")] public static extern bool FreeConsole();
        [DllImport("kernel32.dll")] public static extern bool AttachConsole(int pid);
        [DllImport("kernel32.dll")] public static extern IntPtr GetConsoleWindow();
        [DllImport("kernel32.dll")] public static extern bool SetConsoleCtrlHandler(IntPtr handler, bool add);
    }

    // A window the user was typing into when they sent a session a prompt, verified to belong to that
    // session's host process. Used only when the host app has several windows (e.g. two VS Code windows).
    public class Anchor { public long Hwnd; public int Pid; public DateTime Started; }

    public static class WorkerWindows
    {
        const uint GW_OWNER = 4, GA_ROOTOWNER = 3, PROCESS_QUERY_LIMITED_INFORMATION = 0x1000, STILL_ACTIVE = 259;
        const int GWL_EXSTYLE = -20, WS_EX_TOOLWINDOW = 0x80, WS_EX_NOACTIVATE = 0x08000000, SW_RESTORE = 9;
        const double StartToleranceMs = 50;

        // 1 = running (start time returned), 0 = no such running process, -1 = can't tell (e.g. access denied).
        public static int StartTime(int pid, out DateTime startedUtc)
        {
            startedUtc = DateTime.MinValue;
            if (pid <= 0) return 0;
            IntPtr h = Native.OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
            if (h == IntPtr.Zero) return Marshal.GetLastWin32Error() == 87 ? 0 : -1; // 87: no process with that ID
            try
            {
                uint code;
                if (Native.GetExitCodeProcess(h, out code) && code != STILL_ACTIVE) return 0;
                long c, e, k, u;
                if (!Native.GetProcessTimes(h, out c, out e, out k, out u)) return -1;
                startedUtc = DateTime.FromFileTimeUtc(c);
                return 1;
            }
            finally { Native.CloseHandle(h); }
        }

        // Process identity is PID + start time, so a reused PID never matches.
        // 1 = the recorded process is running, 0 = it is gone (or the PID now belongs to another process), -1 = unknown.
        public static int ProcessAlive(int pid, DateTime startedUtc)
        {
            if (pid <= 0 || startedUtc == DateTime.MinValue) return -1;
            DateTime actual;
            int r = StartTime(pid, out actual);
            if (r != 1) return r;
            return Math.Abs((actual - startedUtc).TotalMilliseconds) <= StartToleranceMs ? 1 : 0;
        }

        public static Dictionary<int, int> ParentMap()
        {
            var map = new Dictionary<int, int>();
            IntPtr snap = Native.CreateToolhelp32Snapshot(2 /* TH32CS_SNAPPROCESS */, 0);
            if (snap == IntPtr.Zero || snap == new IntPtr(-1)) return map;
            try
            {
                var e = new Native.PROCESSENTRY32 { dwSize = (uint)Marshal.SizeOf(typeof(Native.PROCESSENTRY32)) };
                for (bool ok = Native.Process32FirstW(snap, ref e); ok; ok = Native.Process32NextW(snap, ref e))
                    map[(int)e.th32ProcessID] = (int)e.th32ParentProcessID;
            }
            finally { Native.CloseHandle(snap); }
            return map;
        }

        // pid and its ancestors, nearest first. A parent is trusted only if it started before its child;
        // otherwise the parent PID was reused by an unrelated process and the walk stops.
        public static List<int> Chain(int pid, Func<int, int> parentOf, Func<int, DateTime> startOf)
        {
            var chain = new List<int>();
            var seen = new HashSet<int>();
            int cur = pid;
            DateTime curStart = startOf(pid);
            while (cur > 0 && chain.Count < 16 && seen.Add(cur))
            {
                chain.Add(cur);
                int parent = parentOf(cur);
                if (parent <= 0) break;
                DateTime ps = startOf(parent);
                if (ps == DateTime.MinValue || curStart == DateTime.MinValue || ps > curStart) break;
                cur = parent; curStart = ps;
            }
            return chain;
        }

        static string ClassName(IntPtr h) { var sb = new StringBuilder(64); Native.GetClassName(h, sb, 64); return sb.ToString(); }

        // A real, user-facing top-level window (minimized counts; hidden, owned, tool and cloaked windows don't).
        static bool IsUsable(IntPtr h)
        {
            if (!Native.IsWindowVisible(h) || Native.GetWindow(h, GW_OWNER) != IntPtr.Zero) return false;
            if ((Native.GetWindowLong(h, GWL_EXSTYLE) & (WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE)) != 0) return false;
            if (ClassName(h) == "PseudoConsoleWindow") return false;
            try { int cloaked; if (Native.DwmGetWindowAttribute(h, 14 /* DWMWA_CLOAKED */, out cloaked, 4) == 0 && cloaked != 0) return false; } catch { }
            if (Native.IsIconic(h)) return true;
            Native.RECT r;
            return Native.GetWindowRect(h, out r) && r.Right > r.Left && r.Bottom > r.Top;
        }

        static Dictionary<int, List<IntPtr>> WindowsByPid()
        {
            var map = new Dictionary<int, List<IntPtr>>();
            Native.EnumWindowsProc cb = (h, l) =>
            {
                uint p; Native.GetWindowThreadProcessId(h, out p);
                List<IntPtr> list;
                if (!map.TryGetValue((int)p, out list)) map[(int)p] = list = new List<IntPtr>();
                list.Add(h);
                return true;
            };
            Native.EnumWindows(cb, IntPtr.Zero);
            GC.KeepAlive(cb);
            return map;
        }

        // The windows that show process 'pid' on screen: its own real top-level windows, or - for a console
        // hosted by Windows Terminal - the exact terminal window that owns its pseudo-console window.
        static List<IntPtr> Candidates(int pid, Dictionary<int, List<IntPtr>> byPid)
        {
            var result = new List<IntPtr>();
            List<IntPtr> wins;
            if (!byPid.TryGetValue(pid, out wins)) return result;
            foreach (var h in wins)
            {
                IntPtr c = h;
                if (ClassName(h) == "PseudoConsoleWindow") { c = Native.GetWindow(h, GW_OWNER); if (c == IntPtr.Zero) continue; }
                if (IsUsable(c) && !result.Contains(c)) result.Add(c);
            }
            return result;
        }

        static readonly object consoleLock = new object();
        static bool ownConsoleReleased;

        // The console window a process is attached to (Windows' own answer, exact even when the console is
        // hosted by Windows Terminal and the window belongs to its OpenConsole.exe). To ask, the indicator
        // briefly attaches to that console, which requires giving up its own console first: it only ever
        // releases its own *hidden* console (how start-indicator.vbs launches it), never a visible one.
        public static IntPtr ConsoleWindowOf(int pid)
        {
            lock (consoleLock)
            {
                if (!ownConsoleReleased)
                {
                    IntPtr own = Native.GetConsoleWindow();
                    if (own != IntPtr.Zero)
                    {
                        if (ClassName(own) != "ConsoleWindowClass" || Native.IsWindowVisible(own)) return IntPtr.Zero;
                        Native.FreeConsole();
                    }
                    ownConsoleReleased = true;
                }
                if (!Native.AttachConsole(pid)) return IntPtr.Zero;
                try { Native.SetConsoleCtrlHandler(IntPtr.Zero, true); return Native.GetConsoleWindow(); } // ignore Ctrl+C while attached
                finally { Native.FreeConsole(); }
            }
        }

        // A terminal session's own console window (or the Windows Terminal window hosting it) if it is on
        // screen; otherwise the windows of the nearest ancestor process that has any (VS Code, etc.).
        public static List<IntPtr> WorkerWindowsFor(int claudePid)
        {
            IntPtr con = ConsoleWindowOf(claudePid);
            if (con != IntPtr.Zero)
            {
                IntPtr c = ClassName(con) == "PseudoConsoleWindow" ? Native.GetWindow(con, GW_OWNER) : con;
                if (c != IntPtr.Zero && IsUsable(c)) return new List<IntPtr> { c };
            }
            return HostWindows(claudePid);
        }

        // Walks up from the Claude process to the nearest process that has windows on screen ("the host").
        public static List<IntPtr> HostWindows(int claudePid)
        {
            var parents = ParentMap();
            var chain = Chain(claudePid,
                p => { int pp; return parents.TryGetValue(p, out pp) ? pp : 0; },
                p => { DateTime t; return StartTime(p, out t) == 1 ? t : DateTime.MinValue; });
            var byPid = WindowsByPid();
            foreach (int p in chain)
            {
                var c = Candidates(p, byPid);
                if (c.Count > 0) return c;
            }
            return new List<IntPtr>();
        }

        static bool AnchorStillValid(Anchor a, IntPtr h)
        {
            uint p; Native.GetWindowThreadProcessId(h, out p);
            return (int)p == a.Pid && ProcessAlive(a.Pid, a.Started) == 1;
        }

        // Finds the window of a worker, or returns IntPtr.Zero with a reason. Never guesses: it returns a
        // window only when it is the host's only window, or the verified anchor among several.
        public static IntPtr Resolve(int claudePid, DateTime claudeStarted, Anchor anchor, out string reason)
        {
            FocusResult kind;
            return Resolve(claudePid, claudeStarted, anchor, out reason, out kind);
        }

        // Same, also saying which kind of "couldn't identify" it was (for the phone, which shows its own text).
        public static IntPtr Resolve(int claudePid, DateTime claudeStarted, Anchor anchor, out string reason, out FocusResult kind)
        {
            reason = ""; kind = FocusResult.Unknown;
            if (claudePid <= 0 || claudeStarted == DateTime.MinValue)
            {
                reason = "Couldn't identify that worker's window yet.\nIt will work after the worker's next prompt.";
                kind = FocusResult.NotYet;
                return IntPtr.Zero;
            }
            int alive = ProcessAlive(claudePid, claudeStarted);
            if (alive == 0) { reason = "That Claude session is no longer running."; kind = FocusResult.Ended; return IntPtr.Zero; }
            if (alive < 0) { reason = "Couldn't identify that worker's window."; return IntPtr.Zero; }
            var c = WorkerWindowsFor(claudePid);
            if (c.Count == 1) { kind = FocusResult.Focused; return c[0]; }
            if (c.Count == 0) { reason = "Couldn't identify that worker's window."; return IntPtr.Zero; }
            if (anchor != null)
                foreach (var h in c)
                    if (h.ToInt64() == anchor.Hwnd && AnchorStillValid(anchor, h)) { kind = FocusResult.Focused; return h; }
            reason = "Couldn't identify that worker's window: its app has " + c.Count + " windows open.\nSend it a prompt from its window once, then try again.";
            kind = FocusResult.Ambiguous;
            return IntPtr.Zero;
        }

        // True while the lock screen (or another secure desktop, e.g. a UAC prompt) has the input. Windows
        // doesn't let any app switch windows then; the phone says so instead of trying.
        public static bool InputDesktopLocked()
        {
            IntPtr d = Native.OpenInputDesktop(0, false, 0x0001 /* DESKTOP_READOBJECTS */);
            if (d == IntPtr.Zero) return true;
            try
            {
                var name = new StringBuilder(64);
                int needed;
                return Native.GetUserObjectInformation(d, 2 /* UOI_NAME */, name, name.Capacity * 2, out needed)
                    && !string.Equals(name.ToString(), "Default", StringComparison.OrdinalIgnoreCase);
            }
            finally { Native.CloseDesktop(d); }
        }

        // Called right after a session submits a prompt: if the foreground window is one of that session's
        // host windows, remember it. Returns null when the foreground window isn't one of them.
        public static Anchor CaptureForeground(int claudePid, DateTime claudeStarted)
        {
            IntPtr fg = Native.GetForegroundWindow();
            if (fg == IntPtr.Zero || ProcessAlive(claudePid, claudeStarted) != 1) return null;
            IntPtr root = Native.GetAncestor(fg, GA_ROOTOWNER);
            if (root != IntPtr.Zero) fg = root;
            if (!WorkerWindowsFor(claudePid).Contains(fg)) return null;
            uint p; Native.GetWindowThreadProcessId(fg, out p);
            DateTime t;
            if (StartTime((int)p, out t) != 1) return null;
            return new Anchor { Hwnd = fg.ToInt64(), Pid = (int)p, Started = t };
        }

        // Brings a window to the foreground (only ever for an explicit click: a worker row, the tray icon, a notification).
        public static bool Activate(IntPtr h)
        {
            if (Native.IsIconic(h)) Native.ShowWindow(h, SW_RESTORE);
            if (Native.SetForegroundWindow(h) && Native.GetForegroundWindow() == h) return true;
            IntPtr fg = Native.GetForegroundWindow();
            uint dummy, me = Native.GetCurrentThreadId();
            uint fgThread = fg != IntPtr.Zero ? Native.GetWindowThreadProcessId(fg, out dummy) : 0;
            bool attached = fgThread != 0 && fgThread != me && Native.AttachThreadInput(me, fgThread, true);
            try { Native.BringWindowToTop(h); Native.SetForegroundWindow(h); }
            finally { if (attached) Native.AttachThreadInput(me, fgThread, false); }
            return Native.GetForegroundWindow() == h;
        }
    }

    // ---------- settings window ----------
    public class SettingsForm : Form
    {
        public SettingsForm(string sort, bool summary, string startup, Action<string, object> set, Action editAliases)
            : this(sort, summary, startup, false, set, editAliases) { }

        public SettingsForm(string sort, bool summary, string startup, bool phoneOn, Action<string, object> set, Action editAliases)
            : this(sort, summary, startup, phoneOn, true, set, editAliases) { }

        public SettingsForm(string sort, bool summary, string startup, bool phoneOn, bool usageOn, Action<string, object> set, Action editAliases)
            : this(sort, summary, startup, phoneOn, usageOn, false, null, set, editAliases) { }

        public SettingsForm(string sort, bool summary, string startup, bool phoneOn, bool usageOn, bool autostartOn, Action<bool> setAutostart, Action<string, object> set, Action editAliases)
        {
            Text = "Claude Status Settings";
            FormBorderStyle = FormBorderStyle.FixedToolWindow;
            MaximizeBox = false; MinimizeBox = false; ShowInTaskbar = false; TopMost = true;
            StartPosition = FormStartPosition.Manual;
            AutoSize = true; AutoSizeMode = AutoSizeMode.GrowAndShrink;
            Font = SystemFonts.MessageBoxFont;
            Padding = new Padding(10, 6, 10, 10);

            var flow = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false };
            Controls.Add(flow);

            flow.Controls.Add(Heading("Worker ordering"));
            var urgency = Radio("Urgency  (needs you, error, working, done)", !Board.IsRecent(sort));
            var recent = Radio("Most recent", Board.IsRecent(sort));
            urgency.CheckedChanged += (s, e) => { if (urgency.Checked) set("workerSort", "urgency"); };
            recent.CheckedChanged += (s, e) => { if (recent.Checked) set("workerSort", "recent"); };
            flow.Controls.Add(Group(urgency, recent));

            var sum = new CheckBox { Text = "Show worker summary", Checked = summary, AutoSize = true, Margin = new Padding(3, 8, 3, 3) };
            sum.CheckedChanged += (s, e) => set("showWorkerSummary", sum.Checked);
            flow.Controls.Add(sum);

            // Only hides the section; usage keeps being tracked (and the phone has its own switch).
            var use = new CheckBox { Text = "Show usage  (Claude plan limits, account-wide)", Checked = usageOn, AutoSize = true, Margin = new Padding(3, 4, 3, 3) };
            use.CheckedChanged += (s, e) => set("showUsage", use.Checked);
            flow.Controls.Add(use);

            flow.Controls.Add(Heading("Startup view"));
            bool queue = string.Equals(startup, "queue", StringComparison.OrdinalIgnoreCase);
            var wb = Radio("Status  (the worker board)", !queue);
            var cp = Radio("Queue  (scheduled prompts)", queue);
            wb.CheckedChanged += (s, e) => { if (wb.Checked) set("startupView", "workerBoard"); };
            cp.CheckedChanged += (s, e) => { if (cp.Checked) set("startupView", "queue"); };
            flow.Controls.Add(Group(wb, cp));

            // Scheduled tasks only run while Claude Status runs, so starting it with Windows keeps them going.
            var auto = new CheckBox { Text = "Start Claude Status when I sign in to Windows", Checked = autostartOn, AutoSize = true, Margin = new Padding(3, 8, 3, 3), Enabled = setAutostart != null };
            auto.CheckedChanged += (s, e) => { if (setAutostart != null) setAutostart(auto.Checked); };
            flow.Controls.Add(auto);

            var ph = new CheckBox { Text = "Phone dashboard (private, via Tailscale)", Checked = phoneOn, AutoSize = true, Margin = new Padding(3, 10, 3, 3) };
            ph.CheckedChanged += (s, e) => set("phoneDashboard", ph.Checked);
            flow.Controls.Add(ph);

            var link = new LinkLabel { Text = "Edit project aliases (config.json)…", AutoSize = true, Margin = new Padding(3, 10, 3, 3) };
            link.LinkClicked += (s, e) => editAliases();
            flow.Controls.Add(link);

            var close = new Button { Text = "Close", AutoSize = true, Anchor = AnchorStyles.Right, Margin = new Padding(3, 10, 3, 0) };
            close.Click += (s, e) => Close();
            flow.Controls.Add(close);
            CancelButton = close;
        }

        static Label Heading(string text)
        {
            return new Label { Text = text, AutoSize = true, Font = new Font(SystemFonts.MessageBoxFont, FontStyle.Bold), Margin = new Padding(3, 8, 3, 0) };
        }

        static RadioButton Radio(string text, bool on) { return new RadioButton { Text = text, Checked = on, AutoSize = true, Margin = new Padding(12, 2, 3, 0) }; }

        static FlowLayoutPanel Group(params Control[] items)
        {
            var p = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false, Margin = new Padding(0) };
            p.Controls.AddRange(items);
            return p;
        }
    }

    // ---------- the indicator window ----------
    public class IndicatorForm : Form
    {
        [DllImport("user32.dll")] static extern bool ReleaseCapture();
        [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr h, int msg, IntPtr w, IntPtr l);
        [DllImport("user32.dll")] static extern bool SetProcessDPIAware();
        [DllImport("user32.dll")] static extern bool DestroyIcon(IntPtr h);
        [DllImport("shell32.dll", CharSet = CharSet.Unicode)] static extern bool Shell_NotifyIcon(int message, ref NotifyIconData data);
        const int NIM_MODIFY = 1, NIF_INFO = 0x10, NIIF_USER = 4, NIIF_LARGE_ICON = 0x20;
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct NotifyIconData
        {
            public int cbSize; public IntPtr hWnd; public int uID, uFlags, uCallbackMessage; public IntPtr hIcon;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
            public int dwState, dwStateMask;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
            public int uTimeoutOrVersion;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
            public int dwInfoFlags; public Guid guidItem; public IntPtr hBalloonIcon;
        }
        const int WS_EX_TOPMOST = 0x8, WS_EX_TOOLWINDOW = 0x80, WS_EX_NOACTIVATE = 0x08000000, WS_EX_APPWINDOW = 0x40000;
        const int WM_NCLBUTTONDOWN = 0xA1, HTCAPTION = 2;

        static readonly Color Bg = Color.FromArgb(30, 30, 30);
        static readonly Color Muted = Color.FromArgb(160, 160, 160);
        static readonly Color NameColor = Color.FromArgb(228, 228, 228);
        static readonly Color Line = Color.FromArgb(52, 52, 52);
        static readonly Color HoverBg = Color.FromArgb(44, 44, 44);
        static readonly Color Yellow = Color.FromArgb(255, 200, 61);
        static readonly Color Green = Color.FromArgb(63, 185, 80);
        static readonly Color Red = Color.FromArgb(248, 81, 73);
        static readonly Color Grey = Color.FromArgb(128, 128, 128);

        // Unscaled layout (px at 100%).
        const int QueueW = 820, QueueH = 560, QueueMinW = 560, QueueMinH = 380, Edge = 5;
        const int BoardW = 276, HeadH = 26, SumH = 14, UsageH = 20, RowH = 22, PadTop = 3, PadBot = 5, MinRows = 4;
        const TextFormatFlags LeftText = TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine | TextFormatFlags.PreserveGraphicsClipping;
        const TextFormatFlags CenterText = TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine;

        readonly string root, sessionsDir, contextDir, posPath, cfgPath, anchorsPath;
        readonly bool testMode;
        Dictionary<string, object> cfg;
        bool cfgOk = true;
        readonly FileSystemWatcher watcher, cfgWatcher, ctxWatcher;
        readonly System.Windows.Forms.Timer debounce, slow, tick, cfgDebounce, ctxDebounce;
        readonly NotifyIcon tray;
        readonly ContextMenuStrip menu, trayMenu;
        readonly ToolTip tip, note;
        readonly Font smallFont, bigFont, titleFont, rowFont, statusFont, closeFont;
        readonly float scale;

        Dictionary<string, SessionInfo> sessions = new Dictionary<string, SessionInfo>();
        List<SessionInfo> ordered = new List<SessionInfo>();
        readonly WorkerNumbering numbering = new WorkerNumbering();
        Dictionary<string, Anchor> anchors = new Dictionary<string, Anchor>();
        Dictionary<string, ContextReading> contextKept = new Dictionary<string, ContextReading>();
        SessionInfo current;
        bool initialized, board = true, mouseInside, dragging, thumbDrag;   // board: Status view (true) or Queue
        string trayState, hover = "", pressed = "", tipText = "";
        Point home;
        int scroll, thumbStartY, thumbStartScroll;
        SettingsForm settingsForm;
        EventHandler displayChanged;
        PhoneServer phone;
        readonly Queue<IntPtr> balloonIcons = new Queue<IntPtr>();   // pictures of recent balloons (see WorkerBalloon)
        string phoneError = "";
        DateTime phoneRetryUtc = DateTime.MinValue;
        int phoneFailedPort;
        readonly UsageMonitor usage;
        readonly PushNotifier push;
        UsageForm usageForm;
        readonly QueueEngine queue;
        readonly System.Windows.Forms.Timer queueTimer;
        readonly string queuePosPath;
        QueueView queueView;
        Rectangle queueBounds;
        PowerModeChangedEventHandler powerChanged;
        EventHandler timeChanged;
        bool queueChangePending, quitConfirmed;

        protected override bool ShowWithoutActivation { get { return true; } }
        // Status never takes focus; the Queue view is a normal window you type in (and isn't always on top).
        protected override CreateParams CreateParams
        {
            get { var cp = base.CreateParams; cp.ExStyle |= board ? WS_EX_TOOLWINDOW | WS_EX_TOPMOST | WS_EX_NOACTIVATE : WS_EX_APPWINDOW; return cp; }
        }
        [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr h, int i);
        [DllImport("user32.dll")] static extern int SetWindowLong(IntPtr h, int i, int v);

        // Queue view: the thin border around it resizes the window.
        protected override void WndProc(ref Message m)
        {
            base.WndProc(ref m);
            if (m.Msg != 0x84 || board || (int)m.Result != 1) return;   // WM_NCHITTEST answered HTCLIENT
            long lp = (long)m.LParam;
            var p = PointToClient(new Point((short)(lp & 0xFFFF), (short)((lp >> 16) & 0xFFFF)));
            int e = S(Edge);
            bool l = p.X < e, r = p.X >= ClientSize.Width - e, t = p.Y < e, b = p.Y >= ClientSize.Height - e;
            int hit = t && l ? 13 : t && r ? 14 : b && l ? 16 : b && r ? 17 : l ? 10 : r ? 11 : t ? 12 : b ? 15 : 0;
            if (hit != 0) m.Result = (IntPtr)hit;
        }

        public static void Run(string root)
        {
            SetProcessDPIAware();
            bool created;
            using (var mutex = new Mutex(true, "Local\\ClaudeStatusIndicator", out created))
            {
                if (!created) return; // already running
                Application.EnableVisualStyles();
                Application.Run(new IndicatorForm(root));
            }
        }

        public IndicatorForm(string root) : this(root, 0f, false) { }

        // scaleOverride / testMode exist for the automated tests (render at 125-200% DPI, no tray icon or watchers).
        public IndicatorForm(string root, float scaleOverride, bool testMode)
        {
            this.root = root;
            this.testMode = testMode;
            sessionsDir = Path.Combine(root, "state\\sessions");
            posPath = Path.Combine(root, "position.txt");
            cfgPath = Path.Combine(root, "config.json");
            anchorsPath = Path.Combine(root, "state\\windows.json");
            contextDir = Path.Combine(root, "state\\context");
            Directory.CreateDirectory(sessionsDir);
            try { Directory.CreateDirectory(contextDir); } catch { }
            cfg = LoadConfig(cfgPath, out cfgOk);
            anchors = LoadAnchors(anchorsPath);

            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            StartPosition = FormStartPosition.Manual;
            BackColor = Bg;
            DoubleBuffered = true;
            Text = "Claude Status";
            try { string ico = Path.Combine(root, "favicon.ico"); if (File.Exists(ico)) Icon = new Icon(ico); } catch { }   // the Queue's taskbar button
            if (scaleOverride > 0) scale = scaleOverride;
            else using (var g = CreateGraphics()) scale = g.DpiX / 96f;
            // Pixel-sized fonts (equal to the original 7.5pt / 9pt at any DPI) so layout scales predictably.
            smallFont = new Font("Segoe UI", 10f * scale, GraphicsUnit.Pixel);
            bigFont = new Font("Segoe UI", 12f * scale, FontStyle.Bold, GraphicsUnit.Pixel);
            titleFont = new Font("Segoe UI", 10f * scale, FontStyle.Bold, GraphicsUnit.Pixel);
            rowFont = new Font("Segoe UI", 12f * scale, GraphicsUnit.Pixel);
            statusFont = new Font("Segoe UI", 11f * scale, GraphicsUnit.Pixel);
            closeFont = new Font("Segoe UI", 15f * scale, GraphicsUnit.Pixel);

            board = true;
            Size = new Size(S(BoardW), BoardHeight(1));
            home = LoadPosition();
            Location = home;

            menu = new ContextMenuStrip();
            menu.Opening += (s, e) => BuildMenu();
            // The tray icon lives as long as the app (one icon, never recreated); minimizing only hides the window.
            trayMenu = new ContextMenuStrip();
            trayMenu.Items.Add("Open", null, (s, e) => RestoreFromTray());
            trayMenu.Items.Add("Quit", null, (s, e) => Close());
            tray = new NotifyIcon { Visible = !testMode, ContextMenuStrip = trayMenu, Text = "Claude Status: IDLE" };
            tray.MouseClick += (s, e) => { if (e.Button == MouseButtons.Left) RestoreFromTray(); };
            tray.BalloonTipClicked += (s, e) => OpenToastTarget();
            SetTrayIcon(Grey);
            tip = new ToolTip { ShowAlways = true, InitialDelay = 500, ReshowDelay = 200, AutoPopDelay = 8000 };
            note = new ToolTip { ShowAlways = true };

            watcher = new FileSystemWatcher(sessionsDir, "*.*") { SynchronizingObject = this, IncludeSubdirectories = false,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.CreationTime };
            FileSystemEventHandler h = (s, e) => { debounce.Stop(); debounce.Start(); };
            watcher.Changed += h; watcher.Created += h; watcher.Deleted += h;
            watcher.Renamed += (s, e) => { debounce.Stop(); debounce.Start(); };
            debounce = new System.Windows.Forms.Timer { Interval = 60 };
            debounce.Tick += (s, e) => { debounce.Stop(); Reload(); };
            slow = new System.Windows.Forms.Timer { Interval = 5000 }; // safety net + stale pruning
            slow.Tick += (s, e) => Reload();

            // Settings and aliases apply as soon as config.json is saved (by the Settings window or an editor).
            cfgWatcher = new FileSystemWatcher(root, "config.json") { SynchronizingObject = this, IncludeSubdirectories = false,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size };
            FileSystemEventHandler ch = (s, e) => { cfgDebounce.Stop(); cfgDebounce.Start(); };
            cfgWatcher.Changed += ch; cfgWatcher.Created += ch;
            cfgWatcher.Renamed += (s, e) => { cfgDebounce.Stop(); cfgDebounce.Start(); };
            cfgDebounce = new System.Windows.Forms.Timer { Interval = 250 };
            cfgDebounce.Tick += (s, e) => { cfgDebounce.Stop(); bool ok; cfg = LoadConfig(cfgPath, out ok); cfgOk = ok; ApplyQueueConfig(); Reload(); };

            // Context-window readings (phone only) republish the phone board without re-reading any session.
            ctxWatcher = new FileSystemWatcher(contextDir, "*.json") { SynchronizingObject = this, IncludeSubdirectories = false,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite };
            FileSystemEventHandler xh = (s, e) => { ctxDebounce.Stop(); ctxDebounce.Start(); };
            ctxWatcher.Changed += xh; ctxWatcher.Created += xh; ctxWatcher.Deleted += xh;
            ctxWatcher.Renamed += (s, e) => { ctxDebounce.Stop(); ctxDebounce.Start(); };
            ctxDebounce = new System.Windows.Forms.Timer { Interval = 250 };
            ctxDebounce.Tick += (s, e) => { ctxDebounce.Stop(); PublishPhone(); };

            // Timers are presentation only: labels are recomputed from stored timestamps, nothing is rewritten.
            tick = new System.Windows.Forms.Timer { Interval = 1000 };
            tick.Tick += (s, e) => { if (board && Visible) Invalidate(); AdjustTick(); };

            // Claude Usage (usage.cs): local files only, tracked even while the section is hidden.
            usage = new UsageMonitor(root, null);
            usage.Changed += OnUsageChanged;

            // Phone push notifications (push.cs): inert until a phone turns them on from the dashboard.
            push = new PushNotifier(Path.Combine(root, "state\\push.dat"));
            push.Icon = (look, state) => WorkerArt.Png(root, look, state);

            // The prompt scheduler (scheduler.cs) runs whichever view is shown, also minimized to the tray.
            queuePosPath = Path.Combine(root, "queue-window.txt");
            queueBounds = LoadQueueBounds();
            queue = new QueueEngine(root);
            ApplyQueueConfig();
            queue.UsageBlockedUntil = UsageBlockedUntil;
            queue.Notify += QueueNotify;
            queue.Changed += QueueChanged;
            queueTimer = new System.Windows.Forms.Timer { Interval = 10000 };
            queueTimer.Tick += (s, e) => QueueTick();

            Reload();
            // Settings from the removed Compact view open in Status (the regular board).
            if (string.Equals(CfgStr("startupView", ""), "compact", StringComparison.OrdinalIgnoreCase)) SetCfg("startupView", "workerBoard");
            if (string.Equals(CfgStr("startupView", ""), "queue", StringComparison.OrdinalIgnoreCase)) SetView(false);
            if (!testMode)
            {
                queue.Start();
                queue.Tick();
                queueTimer.Start();
                powerChanged = (s, e) => { if (e.Mode == PowerModes.Resume) try { BeginInvoke((MethodInvoker)(() => { queue.NoteWake(); QueueTick(); })); } catch { } };
                SystemEvents.PowerModeChanged += powerChanged;
                timeChanged = (s, e) => { try { BeginInvoke((MethodInvoker)(() => { queue.NoteWake(); QueueTick(); })); } catch { } };
                SystemEvents.TimeChanged += timeChanged;
                push.Start();
                usage.Start(this);
                watcher.EnableRaisingEvents = true;
                cfgWatcher.EnableRaisingEvents = true;
                try { ctxWatcher.EnableRaisingEvents = true; } catch { }
                slow.Start();
                tick.Start();
                displayChanged = (s, e) => { try { BeginInvoke((MethodInvoker)UpdateLayout); } catch { } };
                SystemEvents.DisplaySettingsChanged += displayChanged;
            }
        }

        int S(float v) { return (int)Math.Round(v * scale); }

        // ---------- public surface used by the tests ----------
        public bool IsBoard { get { return board; } }
        public bool IsQueue { get { return !board; } }
        public QueueEngine Queue { get { return queue; } }
        public QueueView QueueViewControl { get { return queueView; } }
        public void ShowQueue(bool on) { SetView(!on); }
        public List<SessionInfo> Workers { get { return new List<SessionInfo>(ordered); } }
        public SessionInfo Surfaced { get { return current; } }
        public string TrayText { get { return tray.Text; } }
        public Point Home { get { return home; } set { home = value; UpdateLayout(); } }
        public void ReloadNow() { Reload(); }
        public void PublishContextNow() { PublishPhone(); }
        public void PaintTo(Graphics g) { Render(g); }
        public bool Dismiss(string id) { SessionInfo si; return sessions.TryGetValue(id, out si) && DismissDone(si); }
        public int ScrollOffset { get { return scroll; } set { scroll = value; ClampScroll(); } }
        public int MaxScroll { get { return Math.Max(0, ContentHeight() - ViewHeight()); } }
        public Rectangle ListViewport { get { return new Rectangle(0, TopH, ClientSize.Width, ViewHeight()); } }
        public Rectangle[] HeaderButtons { get { return new[] { BoardToggleRect, BoardGearRect, BoardMinRect, BoardCloseRect }; } }
        public ContextMenuStrip TrayMenu { get { return trayMenu; } }
        public void MinimizeNow() { MinimizeToTray(); }
        public void RestoreNow() { RestoreFromTray(); }
        public Rectangle RowBounds(int i) { return RowRect(i); }
        public Rectangle DismissBounds(int i) { return XRect(i); }
        public Rectangle StatusBounds(int i, Graphics g) { Rectangle n, s; string st; RowText(g, i, out n, out s, out st); return s; }
        public Rectangle NameBounds(int i, Graphics g) { Rectangle n, s; string st; RowText(g, i, out n, out s, out st); return n; }
        public string RowStatus(int i, Graphics g) { Rectangle n, s; string st; RowText(g, i, out n, out s, out st); return st; }
        public string TooltipFor(string hitKey) { return TipFor(hitKey); }
        public string HitAt(Point p) { return HitTest(p); }
        public Dictionary<string, Anchor> Anchors { get { return anchors; } }
        public void SetSetting(string key, object value) { SetCfg(key, value); }
        public PhoneServer Phone { get { return phone; } }
        public string PhoneStatus { get { return PhoneStatusText; } }
        public void StopPhone() { if (phone != null) { phone.Stop(); phone = null; } }
        public UsageMonitor Usage { get { return usage; } }
        public PushNotifier Push { get { return push; } }
        public bool UsageShown { get { return ShowUsage; } }
        public Rectangle UsageBounds { get { return UsageRect; } }
        // Replaces the window lookup + activation inside FocusCore, so tests see which worker a click or a
        // phone tap would bring up without moving the real focus.
        public Func<SessionInfo, FocusResult> FocusHook;

        // ---------- state ----------
        void Reload()
        {
            var next = new Dictionary<string, SessionInfo>();
            double staleHours = CfgNum("staleHours", 24);
            DateTime now = DateTime.UtcNow;
            string[] files;
            try { files = Directory.GetFiles(sessionsDir, "*.json"); } catch { files = new string[0]; }
            var ser = new JavaScriptSerializer();
            foreach (var f in files)
            {
                string id = Path.GetFileNameWithoutExtension(f);
                try
                {
                    var d = ser.Deserialize<Dictionary<string, object>>(ReadShared(f));
                    DateTime created;
                    try { created = File.GetCreationTimeUtc(f); } catch { created = DateTime.MinValue; }
                    var si = Board.Parse(id, d, created, File.GetLastWriteTimeUtc(f));
                    if (d == null || Board.Rank(si.State) > 3) throw new InvalidDataException(); // empty / unknown state
                    int alive = si.State == Board.Done ? -1 : WorkerWindows.ProcessAlive(si.ClaudePid, si.ClaudeStarted);
                    if (Board.ShouldPrune(si, now, staleHours, alive)) { try { File.Delete(f); } catch { } continue; }
                    next[id] = si;
                }
                catch
                {
                    SessionInfo prev; // file mid-replace or unreadable: keep what we had
                    if (sessions.TryGetValue(id, out prev)) next[id] = prev;
                }
            }

            var aliases = CfgDict("projectAliases");
            foreach (var si in next.Values) si.Name = Board.FriendlyName(si.Project, aliases);
            NameScheduled(next.Values);
            var list = new List<SessionInfo>(next.Values);
            numbering.Apply(list);
            Board.Sort(list, CfgStr("workerSort", "urgency"));

            var announce = new List<SessionInfo>();
            if (initialized)
                foreach (var si in list)
                {
                    SessionInfo prev;
                    bool had = sessions.TryGetValue(si.Id, out prev);
                    if (!had || prev.State != si.State) announce.Add(si);
                    // The user just sent this session a prompt, so the window they typed in is (very likely) its
                    // window. It is only kept if it verifiably belongs to this session's host process.
                    if (si.Event == "UserPromptSubmit" && si.ClaudePid > 0 && (!had || prev.Updated != si.Updated)
                        && Math.Abs((now - si.Updated).TotalSeconds) < 10)
                    {
                        var a = WorkerWindows.CaptureForeground(si.ClaudePid, si.ClaudeStarted);
                        if (a != null) { anchors[si.Id] = a; SaveAnchors(); }
                    }
                }

            sessions = next;
            ordered = list;
            initialized = true;
            PruneAnchors();
            ApplyPhone();
            PublishPhone();
            foreach (var si in announce) Announce(si);   // after PublishPhone: a brand-new worker has its creature by now
            PublishUsage();
            // Only reads the board: which state each worker is in is decided above, as before (see push.cs).
            push.Observe(ordered, DateTime.UtcNow, phone != null ? (Func<string, string>)phone.KeyFor : null, phone != null ? (Func<string, int>)phone.LookOf : null);
            current = ordered.Count > 0 ? ordered[0] : null;

            string st = current == null ? "IDLE" : current.State;
            if (st != trayState) { trayState = st; SetTrayIcon(ColorFor(st)); }
            string tipLine = "Claude Status: " + Board.Label(st) + (current != null ? " (" + current.DisplayName + ")" : "");
            tray.Text = tipLine.Length > 63 ? tipLine.Substring(0, 63) : tipLine;
            UpdateLayout();
            AdjustTick();
            Invalidate();
        }

        // Open without blocking the hook's atomic replace/delete of the same file.
        static string ReadShared(string path)
        {
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var sr = new StreamReader(fs)) return sr.ReadToEnd();
        }

        void Announce(SessionInfo si)
        {
            string where = si.DisplayName != "" ? si.DisplayName : "Claude Code";
            switch (si.State)
            {
                case Board.Done:
                    if (CfgBool("soundOnDone", true)) SystemSounds.Asterisk.Play();
                    if (CfgBool("toastOnDone", false)) Toast("Claude is done", where, ToolTipIcon.Info, si);
                    break;
                case Board.NeedsYou:
                    if (CfgBool("soundOnNeedsYou", true)) SystemSounds.Exclamation.Play();
                    if (CfgBool("toastOnNeedsYou", true)) Toast("Claude needs you", where + " is waiting for your input or approval", ToolTipIcon.Warning, si);
                    break;
                case Board.Error:
                    if (CfgBool("soundOnError", true)) SystemSounds.Hand.Play();
                    if (CfgBool("toastOnError", true)) Toast("Claude stopped with an error", where + (si.Detail != "" ? ": " + si.Detail : ""), ToolTipIcon.Error, si);
                    break;
            }
        }

        string toastTask = "", toastSession = "";

        // Clicking a notification: a scheduled task's opens its details in the Queue; any other worker's focuses it.
        void OpenToastTarget()
        {
            var t = toastTask != "" ? queue.Data.Find(toastTask) : queue.TaskForSession(toastSession);
            if (t != null) { ShowTask(t.Id); return; }
            SessionInfo si;
            if (toastSession != "" && sessions.TryGetValue(toastSession, out si)) { RestoreFromTray(); FocusWorker(si); }
            else RestoreFromTray();
        }

        // Shows the Queue with one task selected, and brings it forward.
        public void ShowTask(string id)
        {
            if (!Visible) Show();
            SetView(false);
            if (queueView != null) queueView.SelectTask(id);
            RestoreFromTray();
        }

        void Toast(string title, string text, ToolTipIcon icon, SessionInfo si) { Toast(title, text, icon, si, ""); }

        void Toast(string title, string text, ToolTipIcon icon, SessionInfo si, string taskId)
        {
            if (testMode) return;
            toastTask = taskId ?? ""; toastSession = si != null ? si.Id : "";
            try { if (WorkerBalloon(title, text, si)) return; } catch { }
            try { tray.ShowBalloonTip(5000, title, text, icon); } catch { }
        }

        // The same balloon with the worker's creature as its picture. NotifyIcon has no way to set one, so this asks
        // the shell directly, naming the tray icon by NotifyIcon's private window and id; if those ever change, the
        // exception sends Toast back to the plain balloon. Windows reads the picture when the toast appears, possibly
        // behind other toasts, so each balloon's own handle outlives the call (destroying it at once loses the toast);
        // and it refuses any size but the system icon size.
        bool WorkerBalloon(string title, string text, SessionInfo si)
        {
            const BindingFlags Private = BindingFlags.NonPublic | BindingFlags.Instance;
            var window = (NativeWindow)typeof(NotifyIcon).GetField("window", Private).GetValue(tray);
            using (var bmp = WorkerArt.Draw(root, phone != null ? phone.LookOf(si.Id) : -1, si.State, SystemInformation.IconSize.Width))
            {
                IntPtr h = bmp.GetHicon();
                var d = new NotifyIconData { hWnd = window.Handle, uID = (int)typeof(NotifyIcon).GetField("id", Private).GetValue(tray), uFlags = NIF_INFO,
                    uTimeoutOrVersion = 5000, szInfoTitle = title, szInfo = text, dwInfoFlags = NIIF_USER | NIIF_LARGE_ICON, hBalloonIcon = h };
                d.cbSize = Marshal.SizeOf(d);
                if (!Shell_NotifyIcon(NIM_MODIFY, ref d)) { DestroyIcon(h); return false; }
                balloonIcons.Enqueue(h);
                if (balloonIcons.Count > 16) DestroyIcon(balloonIcons.Dequeue());
                return true;
            }
        }

        // Dismissing only deletes Claude Status's own state file for a DONE worker; Claude Code is not touched.
        // If the session has meanwhile moved on (a new prompt), the file is left alone (see Dismissal).
        bool DismissDone(SessionInfo si)
        {
            if (si.State != Board.Done) return false;
            bool removed = Dismissal.DismissDone(sessionsDir, si.Id, Dismissal.Occurrence(si)) == DismissResult.Dismissed;
            Reload();
            return removed;
        }

        // ---------- phone dashboard (phone.cs; off unless "phoneDashboard": true) ----------
        // Runs inside this process so the phone sees exactly this board; it never affects the desktop:
        // if it can't start (e.g. port in use) the reason is shown in the menu and it retries later.
        void ApplyPhone()
        {
            bool want = CfgBool("phoneDashboard", false);
            int port = (int)CfgNum("phonePort", PhoneServer.DefaultPort);
            if (port < 1024 || port > 65535) port = PhoneServer.DefaultPort;
            if (phone != null && (!want || phone.Port != port)) { phone.Stop(); phone = null; }
            if (!want) { phoneError = ""; return; }
            if (phone != null || (port == phoneFailedPort && DateTime.UtcNow < phoneRetryUtc)) return;
            try
            {
                var p = new PhoneServer(root, port, PhoneDismiss, PhoneFocus, null);
                p.UsageRefresh = PhoneUsageRefresh;
                p.Push = push;
                if (p.Start()) { phone = p; phoneError = ""; phoneFailedPort = 0; return; }
                phoneError = p.Error;
            }
            catch (Exception e) { phoneError = e.Message; }
            phoneFailedPort = port;
            phoneRetryUtc = DateTime.UtcNow.AddSeconds(30);
        }

        void PublishPhone()
        {
            if (phone == null) return;
            phone.Publish(ordered, CfgBool("showWorkerSummary", false), Board.Summary(ordered), LoadContexts());
        }

        // Per-session context-window readings (state\context\<id>.json, written by statusline.ps1). Only read
        // for the phone; they never change sessions, timers, ordering or dismissal. A reading whose session has
        // left the board is kept for a day (the session may come back), then removed.
        Dictionary<string, ContextReading> LoadContexts()
        {
            var map = new Dictionary<string, ContextReading>();
            string[] files;
            try { files = Directory.GetFiles(contextDir, "*.json"); } catch { return map; }
            DateTime now = DateTime.UtcNow;
            foreach (var f in files)
            {
                string id = Path.GetFileNameWithoutExtension(f);
                if (!sessions.ContainsKey(id))
                {
                    try { if ((now - File.GetLastWriteTimeUtc(f)).TotalHours > 24) File.Delete(f); } catch { }
                    continue;
                }
                ContextReading c = null;
                try { c = ContextReading.Parse(ReadShared(f)); } catch { }
                if (c == null) contextKept.TryGetValue(id, out c);   // mid-replace or unreadable: keep what we had
                if (c != null) map[id] = c;
            }
            contextKept = map;
            return map;
        }

        // Called on a server thread; the file-level check makes it safe without the UI thread.
        DismissResult PhoneDismiss(string id, string occurrence)
        {
            var r = Dismissal.DismissDone(sessionsDir, id, occurrence);
            if (r == DismissResult.Dismissed && IsHandleCreated) try { BeginInvoke((MethodInvoker)Reload); } catch { }
            return r;
        }

        // ---------- Claude Usage (usage.cs) ----------
        void OnUsageChanged()
        {
            PublishUsage();
            if (ShowUsage) Invalidate();
        }

        // The phone always gets usage (it has its own Show usage switch); display data only.
        void PublishUsage()
        {
            if (phone == null || usage == null) return;
            var s = usage.Snapshot;
            phone.PublishUsage(ClaudeStatus.Usage.ToPhone(s), ClaudeStatus.Usage.Signature(s));
        }

        // Called on a server thread: the read itself happens on the UI thread, like every other read.
        bool PhoneUsageRefresh()
        {
            if (!IsHandleCreated || IsDisposed) return false;
            try { BeginInvoke((MethodInvoker)(() => usage.RefreshNow())); return true; } catch { return false; }
        }

        void OpenUsage()
        {
            if (usageForm != null && !usageForm.IsDisposed) { usageForm.Activate(); return; }
            usageForm = new UsageForm(usage, scale);
            usageForm.Load += (s, e) =>
            {
                Rectangle wa = Screen.FromControl(this).WorkingArea;
                int x = Math.Max(wa.Left, Math.Min(Left, wa.Right - usageForm.Width));
                int y = Bottom + S(6) + usageForm.Height <= wa.Bottom ? Bottom + S(6) : Math.Max(wa.Top, Top - S(6) - usageForm.Height);
                usageForm.Location = new Point(x, y);
            };
            usageForm.Show(this);
        }

        string UsageTip()
        {
            var s = usage.Snapshot;
            DateTime now = DateTime.UtcNow;
            var sb = new StringBuilder("Claude usage for your whole account (shared by all workers)");
            if (s.Limits.Count == 0) sb.Append("\n").Append(ClaudeStatus.Usage.StateLabel(s.State)).Append(s.Message != "" ? ": " + s.Message : "");
            foreach (var l in s.Limits)
                sb.Append("\n").Append(l.Title).Append(": ").Append(ClaudeStatus.Usage.PercentText(l.Percent)).Append(" used · ").Append(ClaudeStatus.Usage.ResetText(l, now));
            if (s.ObservedUtc != DateTime.MinValue)
                sb.Append("\n").Append(ClaudeStatus.Usage.StateLabel(s.State)).Append(" · last updated ").Append(ClaudeStatus.Usage.AgeText(s.ObservedUtc, now));
            return sb.Append("\nClick for the full breakdown").ToString();
        }

        string PhoneStatusText
        {
            get
            {
                if (!CfgBool("phoneDashboard", false)) return "Phone dashboard: off";
                if (phone != null) return "Phone dashboard: on (127.0.0.1:" + phone.Port + ")";
                return "Phone dashboard: not running: " + (phoneError != "" ? phoneError : "starting");
            }
        }

        // A click on a worker's text: bring its window forward, or say why not.
        void FocusWorker(SessionInfo si)
        {
            string why;
            if (FocusCore(si, false, out why) != FocusResult.Focused && why != "") ShowNote(why);
        }

        // The one implementation behind the desktop click and the phone's creature tap (UI thread only).
        // From the phone nothing is attempted while the laptop is locked.
        FocusResult FocusCore(SessionInfo si, bool fromPhone, out string why)
        {
            why = "";
            if (FocusHook != null) return FocusHook(si);
            if (fromPhone && WorkerWindows.InputDesktopLocked()) return FocusResult.Locked;
            Anchor a;
            anchors.TryGetValue(si.Id, out a);
            FocusResult kind;
            IntPtr h = WorkerWindows.Resolve(si.ClaudePid, si.ClaudeStarted, a, out why, out kind);
            if (h == IntPtr.Zero) return kind;
            if (WorkerWindows.Activate(h)) return FocusResult.Focused;
            why = "Windows didn't allow bringing that window forward.";
            return FocusResult.Refused;
        }

        // Called on a server thread: runs the desktop's own focus on the UI thread (where a click would) for
        // the worker as the board knows it right now, and waits for Windows' answer.
        FocusResult PhoneFocus(string id)
        {
            if (!IsHandleCreated || IsDisposed) return FocusResult.Unavailable;
            var result = FocusResult.Unavailable;
            var done = new ManualResetEvent(false);
            try
            {
                BeginInvoke((MethodInvoker)(() =>
                {
                    try { SessionInfo si; string why; result = sessions.TryGetValue(id, out si) ? FocusCore(si, true, out why) : FocusResult.Gone; }
                    catch { result = FocusResult.Unavailable; }
                    finally { try { done.Set(); } catch { } }
                }));
            }
            catch { return FocusResult.Unavailable; }
            return done.WaitOne(8000) ? result : FocusResult.Unavailable;
        }

        void ShowNote(string text)
        {
            if (testMode) return;
            var p = PointToClient(Cursor.Position);
            note.Show(text, this, p.X + S(8), p.Y + S(18), 4500);
        }

        static Color ColorFor(string st)
        {
            switch (st) { case Board.Working: return Yellow; case Board.Done: return Green; case Board.NeedsYou: case Board.Error: return Red; default: return Grey; }
        }

        // ---------- layout ----------
        int SummaryH { get { return board && CfgBool("showWorkerSummary", false) ? S(SumH) : 0; } }
        bool ShowUsage { get { return board && CfgBool("showUsage", true); } }
        // Hidden usage takes no space at all.
        int TopH { get { return S(HeadH) + SummaryH + (ShowUsage ? S(UsageH) : 0) + 1; } }
        Rectangle UsageRect { get { return ShowUsage ? new Rectangle(S(1), S(HeadH) + SummaryH - S(2), ClientSize.Width - S(2), S(UsageH)) : Rectangle.Empty; } }
        int RowCount { get { return Math.Max(1, ordered.Count); } }
        int ContentHeight() { return S(PadTop) + RowCount * S(RowH) + S(PadBot); }
        int BoardHeight(int rows) { return TopH + S(PadTop) + rows * S(RowH) + S(PadBot) + 1; }
        int ViewHeight() { return Math.Max(0, ClientSize.Height - TopH - 1); }
        bool Scrolls { get { return board && ContentHeight() > ViewHeight(); } }

        void UpdateLayout()
        {
            if (dragging) return;
            if (!board) { Rectangle q = FitQueue(queueBounds); if (Bounds != q) Bounds = q; return; }
            Size want = new Size(S(BoardW), BoardHeight(RowCount));
            Rectangle wa = Screen.FromRectangle(new Rectangle(home, want)).WorkingArea;
            int minH = BoardHeight(MinRows);
            Rectangle r = Board.Place(home, want, wa, minH);
            if (Bounds != r) Bounds = r;   // SetWindowPos with SWP_NOACTIVATE: never takes focus
            ClampScroll();
        }

        void ClampScroll() { scroll = Math.Max(0, Math.Min(scroll, MaxScroll)); }

        // Status <-> Queue. Each view keeps its own bounds (Status: its saved spot and auto height; Queue: its own
        // saved size and position), both kept on a monitor. Only the window changes: the scheduler, a running task
        // and the board's sessions are untouched.
        void SetView(bool toBoard)
        {
            if (board == toBoard) return;
            if (!board) SaveQueueBounds();
            board = toBoard;
            scroll = 0; hover = ""; pressed = "";
            tip.SetToolTip(this, null); tipText = "";
            if (!board)
            {
                if (queueView == null) { queueView = new QueueView(queue, scale) { Dock = DockStyle.Fill }; Controls.Add(queueView); }
                Padding = new Padding(S(Edge), S(HeadH), S(Edge), S(Edge));
                queueView.Visible = true;
                queueView.RefreshView();
            }
            else if (queueView != null) { queueView.Visible = false; Padding = Padding.Empty; }
            if (IsHandleCreated)
            {
                // Status: a tool window that never takes focus. Queue: a normal window with a taskbar button and an
                // Alt+Tab entry, so it can always be found again. The taskbar only notices the change while hidden.
                int ex = GetWindowLong(Handle, -20);
                ex = board ? (ex | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW) & ~WS_EX_APPWINDOW : (ex & ~(WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW)) | WS_EX_APPWINDOW;
                bool shown = Visible;
                if (shown && !testMode) Hide();
                SetWindowLong(Handle, -20, ex);
                if (shown && !testMode) Show();
            }
            TopMost = board;
            MinimumSize = board ? Size.Empty : new Size(S(QueueMinW), S(QueueMinH));
            UpdateLayout();
            AdjustTick();
            Invalidate();
            if (!board && Visible && !testMode) Activate();
        }

        // Default Queue bounds: next to the Status window, on its monitor.
        Rectangle FitQueue(Rectangle r)
        {
            if (r.Width <= 0) r = new Rectangle(home.X, home.Y, S(QueueW), S(QueueH));
            Rectangle wa = Screen.FromRectangle(r).WorkingArea;
            int w = Math.Min(Math.Max(r.Width, S(QueueMinW)), wa.Width), h = Math.Min(Math.Max(r.Height, S(QueueMinH)), wa.Height);
            int x = Math.Max(wa.Left, Math.Min(r.X, wa.Right - w)), y = Math.Max(wa.Top, Math.Min(r.Y, wa.Bottom - h));
            return new Rectangle(x, y, w, h);
        }

        Rectangle LoadQueueBounds()
        {
            if (testMode) return Rectangle.Empty;
            try
            {
                var p = File.ReadAllText(queuePosPath).Trim().Split(',');
                var r = new Rectangle(int.Parse(p[0]), int.Parse(p[1]), int.Parse(p[2]), int.Parse(p[3]));
                foreach (var scr in Screen.AllScreens) if (scr.WorkingArea.IntersectsWith(r)) return r;
            }
            catch { }
            return Rectangle.Empty;
        }

        void SaveQueueBounds()
        {
            if (board) return;
            queueBounds = Bounds;
            if (testMode) return;
            try { File.WriteAllText(queuePosPath, Left + "," + Top + "," + Width + "," + Height); } catch { }
        }

        protected override void OnResizeEnd(EventArgs e) { base.OnResizeEnd(e); if (!board) { SaveQueueBounds(); Invalidate(); } }
        protected override void OnResize(EventArgs e) { base.OnResize(e); if (!board) Invalidate(); }

        // ---------- scheduler (scheduler.cs) ----------
        void ApplyQueueConfig()
        {
            if (queue == null) return;
            object v;
            var list = cfg.TryGetValue("queueClaudeCommand", out v) ? v as System.Collections.ArrayList : null;
            string[] cmd = null;
            if (list != null && list.Count > 0) { cmd = new string[list.Count]; for (int i = 0; i < list.Count; i++) cmd[i] = Convert.ToString(list[i]); }
            queue.ClaudeCommand = cmd;
            queue.AllowApiBilling = CfgBool("queueAllowApiBilling", false);
            queue.KeepAwake = CfgBool("queueKeepAwake", true);
            queue.HistoryDays = (int)CfgNum("queueHistoryDays", 30);
            var vs = cfg.TryGetValue("queueVSCodeCommand", out v) ? v as System.Collections.ArrayList : null;
            string[] code = null;
            if (vs != null && vs.Count > 0) { code = new string[vs.Count]; for (int i = 0; i < vs.Count; i++) code[i] = Convert.ToString(vs[i]); }
            queue.Bridge.CodeCommand = code;
        }

        void QueueTick()
        {
            try { queue.Tick(); } catch { }
            int want = queue.HasRun ? 2000 : 10000;
            if (queueTimer.Interval != want) queueTimer.Interval = want;
        }

        // Coalesces bursts of scheduler changes into one repaint of the board names (and the Queue view itself).
        void QueueChanged()
        {
            if (queueChangePending || !IsHandleCreated) return;
            queueChangePending = true;
            BeginInvoke((MethodInvoker)(() =>
            {
                queueChangePending = false;
                if (IsDisposed) return;
                bool changed = false;
                foreach (var si in ordered) { string before = si.Name; NameScheduled(new[] { si }); if (si.Name != before) changed = true; }
                if (changed) Invalidate();
            }));
        }

        // A scheduled run fires the same Claude Code hooks as any session, so it already is one worker row; it is
        // only named after its task (never added twice). Only while the row shows that task's run.
        void NameScheduled(IEnumerable<SessionInfo> list)
        {
            if (queue == null) return;
            var aliases = CfgDict("projectAliases");
            foreach (var si in list)
            {
                var t = ScheduledTaskOf(si);
                si.Name = t != null ? "⏱ " + t.Title : Board.FriendlyName(si.Project, aliases);
            }
        }

        QueueTask ScheduledTaskOf(SessionInfo si)
        {
            var t = queue != null ? queue.TaskForSession(si.Id) : null;
            if (t == null || t.Last == null) return null;
            var a = t.Last;
            if (t.State == QState.Running && a.SessionId == si.Id) return t;
            return a.SessionId == si.Id && si.StateSince >= a.StartedUtc.AddSeconds(-5) && (a.EndedUtc == DateTime.MinValue || si.StateSince <= a.EndedUtc.AddMinutes(1)) ? t : null;
        }

        // Account-wide limit Claude Code reported as fully used (100%) whose reset is still ahead, if any.
        DateTime UsageBlockedUntil()
        {
            DateTime until = DateTime.MinValue, now = DateTime.UtcNow;
            var s = usage != null ? usage.Snapshot : null;
            if (s == null) return until;
            foreach (var l in s.Limits)
                if (l.Percent >= 100 && l.ResetsUtc > now && l.ResetsUtc > until && !l.Key.ToLowerInvariant().Contains("spend")) until = l.ResetsUtc;
            return until;
        }

        void QueueNotify(QueueTask t)
        {
            if (!CfgBool("toastOnQueue", true)) return;
            string title, state; ToolTipIcon icon;
            switch (t.State)
            {
                case QState.Completed: title = "Scheduled task finished"; state = Board.Done; icon = ToolTipIcon.Info; break;
                case QState.Failed: title = "Scheduled task failed"; state = Board.Error; icon = ToolTipIcon.Error; break;
                case QState.WaitingUsage: title = "Scheduled task waiting for usage"; state = Board.NeedsYou; icon = ToolTipIcon.Warning; break;
                case QState.Blocked: title = "Scheduled task blocked"; state = Board.NeedsYou; icon = ToolTipIcon.Warning; break;
                default: title = "Scheduled task needs you"; state = Board.NeedsYou; icon = ToolTipIcon.Warning; break;
            }
            string text = t.Title + ": " + (t.State == QState.Completed ? (t.Summary != "" ? t.Summary : "Claude reported it's done (not verified).") : t.Reason);
            if (text.Length > 250) text = text.Substring(0, 249) + "…";
            if (t.State == QState.NeedsAttention && CfgBool("soundOnNeedsYou", true) && !testMode) SystemSounds.Exclamation.Play();
            Toast(title, text, icon, new SessionInfo { Id = t.SessionId, State = state }, t.Id);
        }

        static bool AutostartOn() { try { return File.Exists(AutostartLink); } catch { return false; } }
        static string AutostartLink { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Startup), "Claude Status Indicator.lnk"); } }

        // Uses the existing autostart.ps1 (a shortcut in the Startup folder; nothing else is changed).
        void SetAutostart(bool on)
        {
            string script = Path.Combine(root, "autostart.ps1");
            if (!File.Exists(script)) return;
            try
            {
                var psi = new ProcessStartInfo("powershell.exe", "-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File \"" + script + "\" " + (on ? "-Enable" : "-Disable")) { UseShellExecute = false, CreateNoWindow = true };
                using (var p = Process.Start(psi)) p.WaitForExit(15000);
            }
            catch { }
        }

        // Fully quitting stops the scheduler, so say what happens to waiting and running tasks.
        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            base.OnFormClosing(e);
            if (e.Cancel || testMode || quitConfirmed || e.CloseReason == CloseReason.WindowsShutDown || e.CloseReason == CloseReason.TaskManagerClosing) return;
            int waiting = 0; QueueTask running = null;
            foreach (var t in queue.Data.Tasks) { if (QState.Pending(t.State)) waiting++; if (t.State == QState.Running) running = t; }
            if (waiting == 0 && running == null) return;
            string msg = "Scheduled tasks only run while Claude Status is running.\r\n\r\n";
            if (waiting > 0) msg += waiting + " waiting task(s) will wait until you start Claude Status again. Overdue ones then start one at a time (you get a minute to skip them).\r\n\r\n";
            if (running != null) msg += "\"" + running.Title + "\" keeps running in the background; its result is picked up the next time Claude Status starts.\r\n\r\n";
            msg += "Quit anyway?\r\n(No = keep it running in the tray instead)";
            var r = MessageBox.Show(msg, "Claude Status", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2);
            if (r == DialogResult.Yes) { quitConfirmed = true; return; }
            e.Cancel = true;
            if (r == DialogResult.No) MinimizeToTray();
        }

        void AdjustTick()
        {
            bool fast = false;
            if (board) foreach (var si in ordered) if (si.State == Board.Working) { fast = true; break; }
            int want = fast ? 1000 : 15000;
            if (tick.Interval != want) tick.Interval = want;
        }

        Rectangle BoardCloseRect { get { return new Rectangle(ClientSize.Width - S(23), S(4), S(18), S(18)); } }
        Rectangle BoardMinRect { get { return new Rectangle(ClientSize.Width - S(42), S(4), S(18), S(18)); } }
        Rectangle BoardGearRect { get { return new Rectangle(ClientSize.Width - S(61), S(4), S(18), S(18)); } }
        Rectangle BoardToggleRect { get { return new Rectangle(ClientSize.Width - S(80), S(4), S(18), S(18)); } }

        Rectangle RowRect(int i) { return new Rectangle(S(1), TopH + S(PadTop) + i * S(RowH) - scroll, ClientSize.Width - S(2), S(RowH)); }

        Rectangle XRect(int i)
        {
            Rectangle rr = RowRect(i);
            int right = ClientSize.Width - S(5) - (Scrolls ? S(5) : 0);
            return new Rectangle(right - S(16), rr.Top + (rr.Height - S(16)) / 2, S(16), S(16));
        }

        Rectangle TrackRect { get { return new Rectangle(ClientSize.Width - S(7), TopH + S(2), S(4), ViewHeight() - S(4)); } }

        Rectangle ThumbRect
        {
            get
            {
                Rectangle t = TrackRect;
                int content = ContentHeight(), view = ViewHeight();
                int h = Math.Max(S(18), content > 0 ? t.Height * view / content : t.Height);
                h = Math.Min(h, t.Height);
                int max = MaxScroll;
                int y = t.Top + (max > 0 ? (t.Height - h) * scroll / max : 0);
                return new Rectangle(t.Left, y, t.Width, h);
            }
        }

        // Name on the left, status on the right; the name gives way first, then the error type is dropped.
        void RowText(Graphics g, int i, out Rectangle nameRect, out Rectangle statusRect, out string status)
        {
            var si = ordered[i];
            Rectangle rr = RowRect(i), xr = XRect(i);
            int nameLeft = S(24), statusRight = xr.Left - S(3);
            status = Board.StatusText(si, DateTime.UtcNow, false);
            int sw = TextRenderer.MeasureText(g, status, statusFont, Size.Empty, TextFormatFlags.NoPadding | TextFormatFlags.SingleLine).Width;
            if (statusRight - sw - nameLeft < S(70))
            {
                status = Board.StatusText(si, DateTime.UtcNow, true);
                sw = TextRenderer.MeasureText(g, status, statusFont, Size.Empty, TextFormatFlags.NoPadding | TextFormatFlags.SingleLine).Width;
            }
            statusRect = new Rectangle(statusRight - sw, rr.Top, sw, rr.Height);
            nameRect = new Rectangle(nameLeft, rr.Top, Math.Max(0, statusRect.Left - S(8) - nameLeft), rr.Height);
        }

        // ---------- drawing ----------
        protected override void OnPaint(PaintEventArgs e) { Render(e.Graphics); }

        void Render(Graphics g)
        {
            g.Clear(Bg);
            if (board) RenderBoard(g); else RenderQueueHeader(g);
        }

        // The Queue view's own header (the rest of the window is QueueView's controls).
        void RenderQueueHeader(Graphics g)
        {
            int w = ClientSize.Width, h = ClientSize.Height;
            using (var pen = new Pen(Color.FromArgb(120, Grey), Math.Max(1, S(1)))) g.DrawRectangle(pen, 0, 0, w - 1, h - 1);
            string head = "CLAUDE QUEUE";
            TextRenderer.DrawText(g, head, titleFont, new Rectangle(S(10), 0, BoardToggleRect.Left - S(12), S(HeadH)), Muted, LeftText);
            int hw = TextRenderer.MeasureText(g, head, titleFont, Size.Empty, TextFormatFlags.NoPadding | TextFormatFlags.SingleLine).Width;
            string sub = "scheduled prompts · runs while Claude Status is open or in the tray";
            TextRenderer.DrawText(g, sub, smallFont, new Rectangle(S(18) + hw, 0, Math.Max(0, BoardToggleRect.Left - S(24) - hw), S(HeadH)), Grey, LeftText);
            ButtonBg(g, BoardToggleRect, "toggle"); DrawStatusIcon(g, BoardToggleRect, hover == "toggle" ? Color.White : Grey);
            ButtonBg(g, BoardGearRect, "gear"); DrawGear(g, BoardGearRect, hover == "gear" ? Color.White : Grey);
            ButtonBg(g, BoardMinRect, "minimize"); DrawMinimize(g, BoardMinRect, hover == "minimize" ? Color.White : Grey);
            ButtonBg(g, BoardCloseRect, "close");
            TextRenderer.DrawText(g, "×", closeFont, BoardCloseRect, hover == "close" ? Color.White : Grey, CenterText);
            // resize grip
            using (var pen = new Pen(Grey)) for (int k = 1; k <= 3; k++) g.DrawLine(pen, w - S(3), h - S(3) - k * S(3), w - S(3) - k * S(3), h - S(3));
        }

        void RenderBoard(Graphics g)
        {
            int w = ClientSize.Width, h = ClientSize.Height;
            Color border = current == null ? Grey : ColorFor(current.State);
            using (var pen = new Pen(Color.FromArgb(120, border), Math.Max(1, S(1))))
                g.DrawRectangle(pen, 0, 0, w - 1, h - 1);

            // header: title + [view toggle] [settings] [minimize] [x]
            TextRenderer.DrawText(g, "CLAUDE WORKERS", titleFont, new Rectangle(S(10), 0, BoardToggleRect.Left - S(12), S(HeadH)), Muted, LeftText);
            ButtonBg(g, BoardToggleRect, "toggle"); DrawQueueIcon(g, BoardToggleRect, hover == "toggle" ? Color.White : Grey);
            ButtonBg(g, BoardGearRect, "gear"); DrawGear(g, BoardGearRect, hover == "gear" ? Color.White : Grey);
            ButtonBg(g, BoardMinRect, "minimize"); DrawMinimize(g, BoardMinRect, hover == "minimize" ? Color.White : Grey);
            ButtonBg(g, BoardCloseRect, "close");
            TextRenderer.DrawText(g, "×", closeFont, BoardCloseRect, hover == "close" ? Color.White : Grey, CenterText);
            if (CfgBool("showWorkerSummary", false))
                TextRenderer.DrawText(g, Board.Summary(ordered), smallFont, new Rectangle(S(10), S(HeadH) - S(4), w - S(20), S(SumH)), Muted, LeftText);
            if (ShowUsage) RenderUsage(g, UsageRect);
            using (var pen = new Pen(Line)) g.DrawLine(pen, S(1), TopH - 1, w - S(2), TopH - 1);

            // rows (only the list scrolls; the header stays put)
            Rectangle view = ListViewport;
            g.SetClip(view);
            if (ordered.Count == 0)
            {
                Rectangle rr = RowRect(0);
                int dot = S(8);
                g.SmoothingMode = SmoothingMode.AntiAlias;
                using (var pen = new Pen(Grey, Math.Max(1f, scale))) g.DrawEllipse(pen, S(10), rr.Top + (rr.Height - dot) / 2, dot, dot);
                g.SmoothingMode = SmoothingMode.None;
                TextRenderer.DrawText(g, "No Claude workers", rowFont, new Rectangle(S(24), rr.Top, w - S(30), rr.Height), Muted, LeftText);
            }
            for (int i = 0; i < ordered.Count; i++)
            {
                Rectangle rr = RowRect(i);
                if (rr.Bottom < view.Top || rr.Top > view.Bottom) continue;
                var si = ordered[i];
                Color c = ColorFor(si.State);
                if (hover == "row:" + si.Id || hover == "x:" + si.Id)
                    using (var b = new SolidBrush(HoverBg)) g.FillRectangle(b, rr);

                int dot = S(8);
                g.SmoothingMode = SmoothingMode.AntiAlias;
                using (var b = new SolidBrush(c)) g.FillEllipse(b, S(10), rr.Top + (rr.Height - dot) / 2, dot, dot);
                g.SmoothingMode = SmoothingMode.None;

                Rectangle nameRect, statusRect; string status;
                RowText(g, i, out nameRect, out statusRect, out status);
                TextRenderer.DrawText(g, si.DisplayName, rowFont, nameRect, NameColor, LeftText);
                // state word in the state colour, the timer muted
                int cut = status.IndexOf(" · ", StringComparison.Ordinal);
                string word = cut > 0 ? status.Substring(0, cut) : status, rest = cut > 0 ? status.Substring(cut) : "";
                int ww = TextRenderer.MeasureText(g, word, statusFont, Size.Empty, TextFormatFlags.NoPadding | TextFormatFlags.SingleLine).Width;
                TextRenderer.DrawText(g, word, statusFont, new Rectangle(statusRect.Left, statusRect.Top, ww, statusRect.Height), c, LeftText & ~TextFormatFlags.EndEllipsis);
                if (rest != "")
                    TextRenderer.DrawText(g, rest, statusFont, new Rectangle(statusRect.Left + ww, statusRect.Top, statusRect.Width - ww + S(2), statusRect.Height), Muted, LeftText & ~TextFormatFlags.EndEllipsis);

                if (si.State == Board.Done)
                    TextRenderer.DrawText(g, "×", rowFont, XRect(i), hover == "x:" + si.Id ? Color.White : Grey, CenterText | TextFormatFlags.PreserveGraphicsClipping);
            }
            g.ResetClip();

            if (Scrolls)
            {
                using (var b = new SolidBrush(Color.FromArgb(40, 40, 40))) g.FillRectangle(b, TrackRect);
                using (var b = new SolidBrush(hover == "thumb" || thumbDrag ? Color.FromArgb(150, 150, 150) : Color.FromArgb(95, 95, 95))) g.FillRectangle(b, ThumbRect);
            }
        }

        // One line under the heading: "USAGE  5h ▬▬ 14%   Wk ▬▬▬ 57%" for the account (not per worker).
        // Stale values are grey and marked; nothing is shown that Claude Code didn't report.
        void RenderUsage(Graphics g, Rectangle r)
        {
            var s = usage.Snapshot;
            DateTime now = DateTime.UtcNow;
            if (hover == "usage") using (var b = new SolidBrush(HoverBg)) g.FillRectangle(b, r);
            const TextFormatFlags Measure = TextFormatFlags.NoPadding | TextFormatFlags.SingleLine;
            int x = S(10), right = r.Right - S(8);
            string label = "USAGE";
            int lw = TextRenderer.MeasureText(g, label, titleFont, Size.Empty, Measure).Width;
            TextRenderer.DrawText(g, label, titleFont, new Rectangle(x, r.Top, lw + S(2), r.Height), Muted, LeftText & ~TextFormatFlags.EndEllipsis);
            x += lw + S(8);

            if (s.Limits.Count == 0)
            {
                string t = s.State == UsageState.Loading ? "Loading…" : "Unavailable · click for details";
                TextRenderer.DrawText(g, t, statusFont, new Rectangle(x, r.Top, right - x, r.Height), Muted, LeftText);
                return;
            }

            bool stale = s.State == UsageState.Stale;
            string tag = stale ? "stale" : "";
            int tagW = tag != "" ? TextRenderer.MeasureText(g, tag, smallFont, Size.Empty, Measure).Width + S(6) : 0;
            var shown = s.Limits.GetRange(0, Math.Min(2, s.Limits.Count));
            int more = s.Limits.Count - shown.Count;
            string moreText = more > 0 ? "+" + more : "";
            int moreW = more > 0 ? TextRenderer.MeasureText(g, moreText, smallFont, Size.Empty, Measure).Width + S(6) : 0;
            // each item: short name, a small bar (dropped if the line is too narrow), percent used
            int barW = S(34), gap = S(10);
            int need = 0;
            foreach (var l in shown)
                need += TextRenderer.MeasureText(g, ClaudeStatus.Usage.ShortLabel(l.Key) + " " + ClaudeStatus.Usage.PercentText(l.Percent), statusFont, Size.Empty, Measure).Width + S(4) + barW + gap;
            bool bars = x + need + tagW + moreW <= right;
            foreach (var l in shown)
            {
                bool dim = stale || ClaudeStatus.Usage.PastReset(l, now);
                string name = ClaudeStatus.Usage.ShortLabel(l.Key), pct = ClaudeStatus.Usage.PercentText(l.Percent);
                int nw = TextRenderer.MeasureText(g, name, statusFont, Size.Empty, Measure).Width;
                TextRenderer.DrawText(g, name, statusFont, new Rectangle(x, r.Top, nw + S(2), r.Height), Muted, LeftText & ~TextFormatFlags.EndEllipsis);
                x += nw + S(4);
                if (bars)
                {
                    var bar = new Rectangle(x, r.Top + (r.Height - S(5)) / 2, barW, S(5));
                    using (var b = new SolidBrush(Color.FromArgb(58, 58, 58))) g.FillRectangle(b, bar);
                    int fill = (int)Math.Round(barW * Math.Max(0, Math.Min(100, l.Percent)) / 100.0);
                    if (l.Percent > 0 && fill < S(2)) fill = S(2);
                    using (var b = new SolidBrush(UsageForm.BarColor(l, s.State, now))) g.FillRectangle(b, new Rectangle(bar.Left, bar.Top, fill, bar.Height));
                    x += barW + S(4);
                }
                else x += S(1);
                int pw = TextRenderer.MeasureText(g, pct, statusFont, Size.Empty, Measure).Width;
                TextRenderer.DrawText(g, pct, statusFont, new Rectangle(x, r.Top, Math.Max(0, Math.Min(pw + S(2), right - x)), r.Height), dim ? Muted : NameColor, LeftText);
                x += pw + gap;
            }
            if (more > 0 && x + moreW <= right)
            {
                TextRenderer.DrawText(g, moreText, smallFont, new Rectangle(x - gap + S(6), r.Top, moreW, r.Height), Muted, LeftText);
                x += moreW;
            }
            if (tag != "")
                TextRenderer.DrawText(g, tag, smallFont, new Rectangle(right - tagW + S(6), r.Top, tagW, r.Height), Color.FromArgb(240, 136, 62), LeftText & ~TextFormatFlags.EndEllipsis);
        }

        void ButtonBg(Graphics g, Rectangle r, string key)
        {
            if (hover != key) return;
            using (var b = new SolidBrush(HoverBg)) g.FillRectangle(b, r);
        }

        // "Queue" icon: a small list with a clock dot (scheduled items).
        void DrawQueueIcon(Graphics g, Rectangle r, Color c)
        {
            DrawListIcon(g, new Rectangle(r.Left - S(2), r.Top, r.Width, r.Height), c);
            int cx = r.Right - S(4), cy = r.Bottom - S(5), rad = S(3);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var b = new SolidBrush(hover == "toggle" ? HoverBg : Bg)) g.FillEllipse(b, cx - rad - 1, cy - rad - 1, rad * 2 + 2, rad * 2 + 2);
            using (var pen = new Pen(c, Math.Max(1f, scale))) { g.DrawEllipse(pen, cx - rad, cy - rad, rad * 2, rad * 2); g.DrawLine(pen, cx, cy, cx, cy - rad + 1); g.DrawLine(pen, cx, cy, cx + rad - 1, cy); }
            g.SmoothingMode = SmoothingMode.None;
        }

        // A small list (dot + line, three times).
        void DrawListIcon(Graphics g, Rectangle r, Color c)
        {
            int cx = r.Left + r.Width / 2, cy = r.Top + r.Height / 2;
            using (var b = new SolidBrush(c))
            using (var pen = new Pen(c, Math.Max(1f, 1.2f * scale)))
                for (int k = -1; k <= 1; k++)
                {
                    int y = cy + k * S(4);
                    g.FillRectangle(b, cx - S(5), y - S(1), Math.Max(2, S(2)), Math.Max(2, S(2)));
                    g.DrawLine(pen, cx - S(1), y, cx + S(5), y);
                }
        }

        // "Status" icon: a little pill with a status dot.
        void DrawStatusIcon(Graphics g, Rectangle r, Color c)
        {
            int cx = r.Left + r.Width / 2, cy = r.Top + r.Height / 2;
            var box = new Rectangle(cx - S(6), cy - S(4), S(12), S(8));
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var pen = new Pen(c, Math.Max(1f, 1.1f * scale))) g.DrawRectangle(pen, box);
            using (var b = new SolidBrush(c)) g.FillEllipse(b, box.Left + S(2), cy - S(2), S(4), S(4));
            g.SmoothingMode = SmoothingMode.None;
        }

        // "Minimize to tray" icon: a short bar in the lower half, like a title-bar minimize button.
        void DrawMinimize(Graphics g, Rectangle r, Color c)
        {
            int cx = r.Left + r.Width / 2, y = r.Top + r.Height / 2 + S(2);
            using (var pen = new Pen(c, Math.Max(1f, 1.3f * scale))) g.DrawLine(pen, cx - S(4), y, cx + S(4), y);
        }

        void DrawGear(Graphics g, Rectangle r, Color c)
        {
            float cx = r.Left + r.Width / 2f, cy = r.Top + r.Height / 2f;
            float outer = 5.2f * scale, body = 3.7f * scale, hole = 1.6f * scale;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var pen = new Pen(c, 1.7f * scale))
                for (int k = 0; k < 8; k++)
                {
                    double a = k * Math.PI / 4;
                    g.DrawLine(pen, cx + (float)Math.Cos(a) * body * 0.8f, cy + (float)Math.Sin(a) * body * 0.8f, cx + (float)Math.Cos(a) * outer, cy + (float)Math.Sin(a) * outer);
                }
            using (var b = new SolidBrush(c)) g.FillEllipse(b, cx - body, cy - body, body * 2, body * 2);
            using (var b = new SolidBrush(hover == "gear" ? HoverBg : Bg)) g.FillEllipse(b, cx - hole, cy - hole, hole * 2, hole * 2);
            g.SmoothingMode = SmoothingMode.None;
        }

        void SetTrayIcon(Color c)
        {
            using (var bmp = new Bitmap(16, 16))
            {
                using (var g = Graphics.FromImage(bmp))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    using (var b = new SolidBrush(c)) g.FillEllipse(b, 2, 2, 12, 12);
                }
                IntPtr h = bmp.GetHicon();
                var old = tray.Icon;
                tray.Icon = (Icon)Icon.FromHandle(h).Clone();
                DestroyIcon(h);
                if (old != null) old.Dispose();
            }
        }

        // ---------- interaction ----------
        // Hit keys: close, minimize, gear, toggle, drag, thumb, track, row:<session id>, x:<session id>.
        string HitTest(Point p)
        {
            if (BoardCloseRect.Contains(p)) return "close";
            if (BoardMinRect.Contains(p)) return "minimize";
            if (BoardGearRect.Contains(p)) return "gear";
            if (BoardToggleRect.Contains(p)) return "toggle";
            if (!board) return p.Y < S(HeadH) ? "drag" : "";
            if (ShowUsage && UsageRect.Contains(p)) return "usage";
            if (p.Y < TopH) return "drag";
            if (Scrolls && p.X >= TrackRect.Left - S(3) && p.Y < ClientSize.Height - 1)
                return ThumbRect.Contains(new Point(ThumbRect.Left, p.Y)) ? "thumb" : "track";
            for (int i = 0; i < ordered.Count; i++)
            {
                Rectangle rr = RowRect(i);
                if (!rr.Contains(p) || p.Y >= TopH + ViewHeight()) continue;
                var si = ordered[i];
                return si.State == Board.Done && XRect(i).Contains(p) ? "x:" + si.Id : "row:" + si.Id;
            }
            return "drag";
        }

        SessionInfo FromKey(string key)
        {
            int c = key.IndexOf(':');
            SessionInfo si;
            return c > 0 && sessions.TryGetValue(key.Substring(c + 1), out si) ? si : null;
        }

        string TipFor(string key)
        {
            switch (key)
            {
                case "toggle": return board ? "Switch to Queue (schedule prompts for Claude)" : "Switch to Status";
                case "gear": return "Settings";
                case "minimize": return "Minimize to tray (keeps running, scheduled tasks too; click the tray icon to reopen)";
                case "close": return "Exit Claude Status (scheduled tasks wait until it runs again)";
                case "usage": return UsageTip();
            }
            var si = FromKey(key);
            if (si == null) return "";
            if (key.StartsWith("x:")) return "Dismiss (only removes this row; Claude is not affected)";
            var task = ScheduledTaskOf(si);
            if (task != null) return "Scheduled task: " + task.Title + "  (" + si.Project + ")\n" + Board.StatusText(si, DateTime.UtcNow, false) + "\nClick to open it in the Queue";
            string t = si.DisplayName;
            if (si.Project != "" && !string.Equals(si.Project, si.Name, StringComparison.OrdinalIgnoreCase)) t += "  (" + si.Project + ")";
            t += "\n" + Board.StatusText(si, DateTime.UtcNow, false);
            if (si.State == Board.NeedsYou && si.Detail != "") t += "\nWaiting for: " + si.Detail;
            return t + "\nClick to bring its window to the front";
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Right) { menu.Show(this, e.Location); return; }
            if (e.Button != MouseButtons.Left) return;
            string hit = HitTest(e.Location);
            switch (hit)
            {
                case "close": Close(); return;
                case "minimize": MinimizeToTray(); return;
                case "toggle": SetView(!board); return;
                case "": return;
                case "gear": OpenSettings(); return;
                case "thumb": thumbDrag = true; thumbStartY = e.Y; thumbStartScroll = scroll; Capture = true; return;
                case "track": scroll += (e.Y < ThumbRect.Top ? -1 : 1) * Math.Max(S(RowH), ViewHeight() - S(RowH)); ClampScroll(); Invalidate(); return;
                case "drag":
                    dragging = true;
                    ReleaseCapture();
                    SendMessage(Handle, WM_NCLBUTTONDOWN, (IntPtr)HTCAPTION, IntPtr.Zero); // native drag; returns when released
                    dragging = false;
                    if (!board) { SaveQueueBounds(); return; }
                    home = Location;
                    SavePosition();
                    UpdateLayout();
                    return;
                default: pressed = hit; return; // row / dismiss act on release
            }
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            if (thumbDrag) { thumbDrag = false; Capture = false; Invalidate(); return; }
            string was = pressed;
            pressed = "";
            if (e.Button != MouseButtons.Left || was == "" || HitTest(e.Location) != was) return;
            if (was == "usage") { OpenUsage(); return; }
            var si = FromKey(was);
            if (si == null) return;
            if (was.StartsWith("x:")) DismissDone(si);
            else
            {
                // A scheduled run has no window of its own: its row opens the task in the Queue.
                var task = ScheduledTaskOf(si);
                if (task != null) ShowTask(task.Id);
                else FocusWorker(si);
            }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            if (thumbDrag)
            {
                Rectangle t = TrackRect, th = ThumbRect;
                int range = t.Height - th.Height;
                if (range > 0) { scroll = thumbStartScroll + (e.Y - thumbStartY) * MaxScroll / range; ClampScroll(); Invalidate(); }
                return;
            }
            bool entered = !mouseInside;
            mouseInside = true;
            string h = HitTest(e.Location);
            if (h != hover || entered) { hover = h; Invalidate(); }
            Cursor = h == "drag" ? Cursors.SizeAll : h == "thumb" || h == "track" || h == "" ? Cursors.Default : Cursors.Hand;
            string t2 = TipFor(h);
            if (t2 != tipText) { tipText = t2; tip.Hide(this); tip.SetToolTip(this, t2 == "" ? null : t2); }
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            mouseInside = false;
            if (hover != "") hover = "";
            tipText = "";
            tip.SetToolTip(this, null);
            Invalidate();
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            if (!Scrolls) return;
            scroll -= Math.Sign(e.Delta) * S(RowH) * Math.Max(1, Math.Abs(e.Delta) / 120);
            ClampScroll();
            Invalidate();
        }

        void BuildMenu()
        {
            menu.Items.Clear();
            if (ordered.Count == 0) menu.Items.Add(new ToolStripMenuItem("No active sessions") { Enabled = false });
            foreach (var si in ordered)
            {
                string name = si.DisplayName + " — " + Board.Label(si.State) + "  (" + si.Updated.ToLocalTime().ToString("HH:mm") + ")";
                menu.Items.Add(new ToolStripMenuItem(name) { Enabled = false });
            }
            menu.Items.Add(new ToolStripSeparator());
            var view = new ToolStripMenuItem("View");
            view.DropDownItems.Add(new ToolStripMenuItem("Status", null, (s, e) => { SetView(true); Show(); }) { Checked = board });
            view.DropDownItems.Add(new ToolStripMenuItem("Queue", null, (s, e) => { Show(); SetView(false); }) { Checked = !board });
            menu.Items.Add(view);
            menu.Items.Add("Settings…", null, (s, e) => OpenSettings());
            menu.Items.Add("Claude usage…", null, (s, e) => OpenUsage());
            int waiting = 0; foreach (var t in queue.Data.Tasks) if (QState.Pending(t.State) || t.State == QState.Running) waiting++;
            menu.Items.Add(new ToolStripMenuItem("Queue: " + (queue.HasRun ? "running a task" : waiting > 0 ? waiting + " waiting" : "nothing scheduled") + (queue.Data.Paused ? " (paused)" : "")) { Enabled = false });
            menu.Items.Add(new ToolStripMenuItem(PhoneStatusText) { Enabled = false });
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Show window", null, (s, e) => { Show(); BringToFrontNoActivate(); });
            menu.Items.Add("Reset position", null, (s, e) => { home = DefaultPosition(); SavePosition(); queueBounds = Rectangle.Empty; UpdateLayout(); Show(); });
            menu.Items.Add("Exit", null, (s, e) => Close());
        }

        void OpenSettings()
        {
            if (settingsForm != null && !settingsForm.IsDisposed) { settingsForm.Activate(); return; }
            settingsForm = new SettingsForm(CfgStr("workerSort", "urgency"), CfgBool("showWorkerSummary", false), CfgStr("startupView", "workerBoard"),
                CfgBool("phoneDashboard", false), CfgBool("showUsage", true), AutostartOn(), SetAutostart, SetCfg, () => { try { Process.Start("notepad.exe", "\"" + cfgPath + "\""); } catch { } });
            settingsForm.Load += (s, e) =>
            {
                Rectangle wa = Screen.FromControl(this).WorkingArea;
                int x = Math.Max(wa.Left, Math.Min(Left, wa.Right - settingsForm.Width));
                int y = Bottom + S(6) + settingsForm.Height <= wa.Bottom ? Bottom + S(6) : Math.Max(wa.Top, Top - S(6) - settingsForm.Height);
                settingsForm.Location = new Point(x, y);
            };
            settingsForm.Show(this);
        }

        void BringToFrontNoActivate() { if (!board) { BringToFront(); return; } TopMost = false; TopMost = true; }

        // Hides the window only: the form, its timers, file watchers, phone dashboard and tray icon keep
        // running, so the board, tray color and toasts stay current. View, size and position are untouched.
        void MinimizeToTray()
        {
            if (settingsForm != null && !settingsForm.IsDisposed) settingsForm.Close();
            if (usageForm != null && !usageForm.IsDisposed) usageForm.Close();
            hover = ""; pressed = ""; tipText = ""; mouseInside = false;
            tip.SetToolTip(this, null);
            note.Hide(this);
            Hide();
        }

        // Shows the same window again (never a new one): un-minimized, moved back onto a connected monitor if it's off
        // screen (monitor unplugged, resolution changed), and in front. Status stays on top without taking keyboard
        // focus; the Queue (a normal window) is activated, because merely raising it leaves it behind the active app.
        void RestoreFromTray()
        {
            if (WindowState != FormWindowState.Normal) WindowState = FormWindowState.Normal;
            if (!board && !OnAnyScreen(queueBounds)) queueBounds = Rectangle.Empty;
            if (board && !OnAnyScreen(new Rectangle(home, new Size(S(BoardW), S(HeadH))))) { home = DefaultPosition(); SavePosition(); }
            UpdateLayout();
            if (!Visible) Show();
            if (board) BringToFrontNoActivate();
            else if (!testMode) WorkerWindows.Activate(Handle);
            Invalidate();
        }

        static bool OnAnyScreen(Rectangle r)
        {
            if (r.Width <= 0) return true;   // no saved bounds: the default spot is used
            foreach (var scr in Screen.AllScreens) { var x = Rectangle.Intersect(scr.WorkingArea, r); if (x.Width >= 60 && x.Height >= 30) return true; }
            return false;
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            SaveQueueBounds();
            queueTimer.Stop();
            if (powerChanged != null) SystemEvents.PowerModeChanged -= powerChanged;
            if (timeChanged != null) SystemEvents.TimeChanged -= timeChanged;
            queue.Dispose();   // a running task keeps running and is picked up again next time
            SavePosition();
            StopPhone();
            push.Stop();
            usage.Dispose();
            watcher.EnableRaisingEvents = false;
            cfgWatcher.EnableRaisingEvents = false;
            ctxWatcher.EnableRaisingEvents = false;
            if (displayChanged != null) SystemEvents.DisplaySettingsChanged -= displayChanged;
            debounce.Stop(); slow.Stop(); tick.Stop(); cfgDebounce.Stop(); ctxDebounce.Stop();
            tray.Visible = false;
            tray.Dispose();
            trayMenu.Dispose();
            base.OnFormClosed(e);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) { StopPhone(); if (usage != null) usage.Dispose(); if (queue != null) queue.Dispose(); if (queueTimer != null) queueTimer.Dispose(); }
            base.Dispose(disposing);
        }

        // ---------- persistence (window position, config, window anchors; nothing sensitive) ----------
        Point DefaultPosition()
        {
            var wa = Screen.PrimaryScreen.WorkingArea;
            // Bottom-right, but left of where Windows toasts appear so they don't cover it.
            return new Point(wa.Right - S(BoardW) - S(400), wa.Bottom - BoardHeight(5) - S(12));
        }

        Point LoadPosition()
        {
            if (testMode) return DefaultPosition();
            try
            {
                var p = File.ReadAllText(posPath).Trim().Split(',');
                var pt = new Point(int.Parse(p[0]), int.Parse(p[1]));
                foreach (var scr in Screen.AllScreens)
                    if (scr.WorkingArea.IntersectsWith(new Rectangle(pt, new Size(S(BoardW), S(HeadH))))) return pt;
            }
            catch { }
            return DefaultPosition();
        }

        void SavePosition()
        {
            if (testMode) return;
            try { File.WriteAllText(posPath, home.X + "," + home.Y); } catch { }
        }

        static Dictionary<string, object> LoadConfig(string path, out bool ok)
        {
            ok = true;
            if (!File.Exists(path)) return new Dictionary<string, object>();
            try { return new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(File.ReadAllText(path)) ?? new Dictionary<string, object>(); }
            catch { ok = false; return new Dictionary<string, object>(); }
        }

        // Settings window changes: update one key, keep every other key (and the aliases) as they are.
        void SetCfg(string key, object value)
        {
            bool ok;
            var fresh = LoadConfig(cfgPath, out ok);
            if (!ok) { if (!testMode) MessageBox.Show("config.json contains an error, so the setting wasn't saved. Fix the file and try again.", "Claude Status"); return; }
            fresh[key] = value;
            cfg = fresh; cfgOk = true;
            try { Json.WriteAtomic(cfgPath, Json.Pretty(new JavaScriptSerializer().Serialize(fresh))); } catch { }
            Reload();
        }

        bool CfgBool(string k, bool def) { object v; return cfg.TryGetValue(k, out v) && v is bool ? (bool)v : def; }
        double CfgNum(string k, double def) { object v; try { return cfg.TryGetValue(k, out v) ? Convert.ToDouble(v, CultureInfo.InvariantCulture) : def; } catch { return def; } }
        string CfgStr(string k, string def) { object v; return cfg.TryGetValue(k, out v) && v is string ? (string)v : def; }
        Dictionary<string, object> CfgDict(string k) { object v; return cfg.TryGetValue(k, out v) ? v as Dictionary<string, object> : null; }

        static Dictionary<string, Anchor> LoadAnchors(string path)
        {
            var result = new Dictionary<string, Anchor>();
            try
            {
                if (!File.Exists(path)) return result;
                var d = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(File.ReadAllText(path));
                foreach (var kv in d)
                {
                    var o = kv.Value as Dictionary<string, object>;
                    if (o == null) continue;
                    DateTime t;
                    if (!DateTime.TryParse(Board.Str(o, "started"), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out t)) continue;
                    result[kv.Key] = new Anchor { Hwnd = Convert.ToInt64(o["hwnd"]), Pid = Convert.ToInt32(o["pid"]), Started = t.ToUniversalTime() };
                }
            }
            catch { }
            return result;
        }

        void SaveAnchors()
        {
            if (testMode) return;
            var d = new Dictionary<string, object>();
            foreach (var kv in anchors)
                d[kv.Key] = new Dictionary<string, object> { { "hwnd", kv.Value.Hwnd }, { "pid", kv.Value.Pid }, { "started", kv.Value.Started.ToString("o") } };
            try { Json.WriteAtomic(anchorsPath, new JavaScriptSerializer().Serialize(d)); } catch { }
        }

        void PruneAnchors()
        {
            bool changed = false;
            foreach (var id in new List<string>(anchors.Keys))
                if (!sessions.ContainsKey(id)) { anchors.Remove(id); changed = true; }
            if (changed) SaveAnchors();
        }
    }
}
