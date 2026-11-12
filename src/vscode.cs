// Claude Status - the app side of the VS Code bridge (src\vscode-bridge\extension.js has the other side).
// Everything is a file in state\vscode: each VS Code window registers its folders there, the app drops requests in
// inbox\, exactly one window claims a request (by renaming it) and reports in status\. No network, no window titles,
// no UI automation. C# 5 (Windows PowerShell's Add-Type).
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Web.Script.Serialization;

namespace ClaudeStatus
{
    public class VsWindow
    {
        public string Id = "", WorkspaceFile = "";
        public List<string> Folders = new List<string>(), Terminals = new List<string>();
        public int Pid;
        public bool Focused;
        public double FocusedAt;
        public DateTime Beat;
        public string Display { get { return WorkspaceFile != "" ? Path.GetFileName(WorkspaceFile) : Folders.Count == 1 ? Folders[0] : Folders.Count + " folders"; } }
    }

    public class VsStatus { public string State = "", Error = "", Window = "", Name = ""; public int Pid; public DateTime At; }

    public class VsBridge
    {
        public readonly string Dir;
        public string[] CodeCommand;                  // null: find Code.exe (tests pass Code.exe + an isolated profile)
        public Func<bool> InstalledOverride;          // tests: the bridge runs from --extensionDevelopmentPath
        public static readonly TimeSpan Fresh = TimeSpan.FromSeconds(12);   // windows beat every 2 s
        public const string ExtensionId = "claude-status.claude-status-bridge";

        public VsBridge(string root)
        {
            Dir = Path.Combine(root, "state\\vscode");
            foreach (var d in new[] { "windows", "inbox", "status" }) try { Directory.CreateDirectory(Path.Combine(Dir, d)); } catch { }
        }

        static Dictionary<string, object> ReadJson(string file)
        {
            try
            {
                using (var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (var sr = new StreamReader(fs, Encoding.UTF8))
                    return new JavaScriptSerializer().DeserializeObject(sr.ReadToEnd()) as Dictionary<string, object>;
            }
            catch { return null; }
        }
        static string S(Dictionary<string, object> d, string k) { object v; return d != null && d.TryGetValue(k, out v) && v != null ? Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture) : ""; }
        static List<string> L(Dictionary<string, object> d, string k)
        {
            var r = new List<string>(); object v;
            if (d != null && d.TryGetValue(k, out v) && v is object[]) foreach (var x in (object[])v) if (x is string) r.Add((string)x);
            return r;
        }
        static DateTime T(string s)
        {
            DateTime t;
            return DateTime.TryParse(s, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out t) ? t : DateTime.MinValue;
        }

        // Windows that beat recently (a closed or crashed window stops beating; its file is cleaned up later).
        public List<VsWindow> Windows()
        {
            var list = new List<VsWindow>();
            DateTime now = DateTime.UtcNow;
            string[] files;
            try { files = Directory.GetFiles(Path.Combine(Dir, "windows"), "*.json"); } catch { return list; }
            foreach (var f in files)
            {
                var d = ReadJson(f);
                var w = new VsWindow { Id = S(d, "id"), WorkspaceFile = S(d, "workspaceFile"), Folders = L(d, "folders"), Terminals = L(d, "terminals"), Beat = T(S(d, "beat")), Focused = S(d, "focused") == "True" };
                int.TryParse(S(d, "pid"), out w.Pid);
                double.TryParse(S(d, "focusedAt"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out w.FocusedAt);
                if (d != null && w.Id + ".json" == Path.GetFileName(f) && now - w.Beat < Fresh) list.Add(w);
                else if (now - File.GetLastWriteTimeUtc(f) > TimeSpan.FromDays(1)) try { File.Delete(f); } catch { }
            }
            return list;
        }

        public VsWindow Window(string id) { foreach (var w in Windows()) if (w.Id == id) return w; return null; }

        // The window to run a project's task in: one that has exactly this folder open (the most recently focused if
        // there are several), else the one multi-root window that contains it. null: open a dedicated window, never
        // guess between ambiguous windows.
        public static VsWindow Pick(IList<VsWindow> live, string project)
        {
            VsWindow best = null;
            foreach (var w in live)
                if (w.WorkspaceFile == "" && w.Folders.Count == 1 && Sched.SamePath(w.Folders[0], project) && (best == null || w.FocusedAt > best.FocusedAt)) best = w;
            if (best != null) return best;
            VsWindow only = null; int n = 0;
            foreach (var w in live) foreach (var f in w.Folders) if (Sched.SamePath(f, project)) { only = w; n++; break; }
            return n == 1 ? only : null;
        }

        // A terminal this window created for request `req` is still open.
        public bool TerminalOpen(string windowId, string req)
        {
            var w = Window(windowId);
            return w != null && w.Terminals.Contains(req);
        }

        public void Send(Dictionary<string, object> req)
        {
            req["v"] = 1;
            Json.WriteAtomic(Path.Combine(Dir, "inbox", (string)req["req"] + ".json"), new JavaScriptSerializer { MaxJsonLength = int.MaxValue }.Serialize(req));
        }

        // Takes back an unclaimed request. false: a window already claimed it (so it is running there).
        public bool Retract(string req)
        {
            string f = Path.Combine(Dir, "inbox", req + ".json");
            try { File.Move(f, f + ".expired"); } catch { return false; }
            try { File.Delete(f + ".expired"); } catch { }
            return true;
        }

        // A request no window has claimed yet.
        public bool Pending(string req) { return File.Exists(Path.Combine(Dir, "inbox", req + ".json")); }

        public VsStatus Status(string req)
        {
            var d = ReadJson(Path.Combine(Dir, "status", req + ".json"));
            if (d == null) return null;
            var s = new VsStatus { State = S(d, "state"), Error = S(d, "error"), Window = S(d, "window"), Name = S(d, "name"), At = T(S(d, "at")) };
            int.TryParse(S(d, "pid"), out s.Pid);
            return s;
        }

        public void Forget(string req) { try { File.Delete(Path.Combine(Dir, "status", req + ".json")); } catch { } }

        public bool Installed()
        {
            if (InstalledOverride != null) return InstalledOverride();
            try
            {
                string ext = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".vscode\\extensions");
                return Directory.Exists(ext) && Directory.GetDirectories(ext, ExtensionId + "-*").Length > 0;
            }
            catch { return false; }
        }

        // Code.exe (the VS Code application, not the code.cmd wrapper, so no cmd.exe parses the folder path).
        public string[] FindCode()
        {
            if (CodeCommand != null && CodeCommand.Length > 0 && CodeCommand[0] != "") return CodeCommand;
            var cands = new List<string>();
            foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';'))
                try { string d = Environment.ExpandEnvironmentVariables(dir.Trim().Trim('"')); if (d != "" && File.Exists(Path.Combine(d, "code.cmd"))) cands.Add(Path.Combine(Path.GetDirectoryName(d.TrimEnd('\\')), "Code.exe")); } catch { }
            cands.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs\\Microsoft VS Code\\Code.exe"));
            cands.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Microsoft VS Code\\Code.exe"));
            foreach (var c in cands) if (File.Exists(c)) return new[] { c };
            return null;
        }

        [DllImport("user32.dll")] static extern bool AllowSetForegroundWindow(int pid);

        // Opens (or, if it's already open in a window, brings forward) a folder or .code-workspace file in VS Code.
        // --new-window: never reuse a window that shows something else. Returns an error or null.
        public string Open(string target)
        {
            var code = FindCode();
            if (code == null) return "VS Code (Code.exe) wasn't found. Install VS Code, or set \"queueVSCodeCommand\" in config.json.";
            var args = new List<string>();
            for (int i = 1; i < code.Length; i++) args.Add(code[i]);
            args.Add("--new-window"); args.Add(target);
            try
            {
                var psi = new ProcessStartInfo(code[0], QueueEngine.JoinArgs(args)) { UseShellExecute = false, CreateNoWindow = false };
                psi.EnvironmentVariables.Remove("ELECTRON_RUN_AS_NODE");
                AllowSetForegroundWindow(-1);   // let VS Code come to the front (we are in the foreground when you click)
                using (Process.Start(psi)) { }
                return null;
            }
            catch (Exception e) { return "Couldn't start VS Code: " + e.Message; }
        }

        // Brings a registered window forward: through its workspace file or its single folder. false: it can't be
        // addressed that way (an unsaved multi-root workspace), so only the terminal is revealed inside it.
        public bool Focus(VsWindow w)
        {
            if (w == null) return false;
            string target = w.WorkspaceFile != "" ? w.WorkspaceFile : w.Folders.Count == 1 ? w.Folders[0] : null;
            return target != null && Open(target) == null;
        }
    }
}
