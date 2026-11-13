// Claude Status - phone-controlled Git commit & push (optional; part of the phone dashboard, phone.cs).
// Compiled together with indicator.cs (C# 5). A manual remote control: the phone shows what changed, YOU pick
// the files and write the message, and only an explicit Send runs Git here on the laptop with your own Git
// installation and credentials. No AI is involved and nothing is chosen for you.
//
// What it will ever run: read-only inspection (status, rev-parse, config, remote get-url, diff), and on Send
// exactly the commands the phone previewed: `git --literal-pathspecs add -- <selected files>` (when needed),
// `git commit --cleanup=verbatim --file=-` (your message on stdin, never on a command line) and
// `git push <remote> refs/heads/<branch>:<upstream ref>`. Never add ., force, amend, reset, clean, stash,
// pull, rebase, merge or --no-verify. Processes get structured arguments (no shell), one step at a time,
// stopping at the first failure. The phone names repositories by opaque tokens only, never by path.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace ClaudeStatus
{
    public class GitWorker { public string Key = "", Name = "", State = "", SessionId = ""; }

    public class GitFile
    {
        public string Path = "", Orig = "", X = ".", Y = ".", Kind = "", Hash = "";
        public bool Untracked, Conflict, Sub;
        public bool Staged { get { return !Untracked && X != "."; } }
        public bool Unstaged { get { return Untracked || Y != "."; } }
        public bool NeedsAdd { get { return Unstaged; } }   // whole-file: `git add` takes the file as it is on disk
    }

    public class GitView
    {
        public string Top = "", GitDir = "", Branch = "", Oid = "", Remote = "", Merge = "", PushUrl = "", Error = "", Raw = "", Fp = "";
        public int Ahead, Behind;
        public List<GitFile> Files = new List<GitFile>();
        public List<string> Blockers = new List<string>(), Warnings = new List<string>();
        public string PushRef { get { return "refs/heads/" + Branch + ":" + Merge; } }
        public string Dest { get { return Merge.StartsWith("refs/heads/") ? Merge.Substring(11) : Merge; } }
    }

    public class GitResult { public int Code = -1; public string Out = ""; public bool TimedOut; }

    public class GitStep
    {
        public string[] Args = new string[0];
        public string Label = "", State = "waiting";   // waiting | running | ok | failed | skipped
        public StringBuilder Out = new StringBuilder();
        public int Code = -1;
    }

    public class GitJob
    {
        public string Id = "", Token = "", Top = "", Name = "", State = "running", Result = "", Message = "", Commit = "", Branch = "", Remote = "", Merge = "", PushUrl = "";
        public int PushAttempts;
        public bool Moved;   // the repository changed after the commit: no Retry Push of a destination you didn't review
        public List<GitStep> Steps = new List<GitStep>();
        public DateTime StartedUtc = DateTime.UtcNow;
        public bool CanRetry { get { return State == "finished" && Commit != "" && Result != "pushed" && !Moved; } }
    }

    public static class Git
    {
        public const int MaxFiles = 1000, MaxDiff = 256 * 1024, MaxOut = 64 * 1024, MaxMessage = 64 * 1024, MaxCmdChars = 30000;
        public static int ReadTimeoutMs = 30000, CommitTimeoutMs = 300000, PushTimeoutMs = 300000;
        public static string Exe = "git";   // your own Git, found on PATH like in a terminal

        static readonly string[] Isolate = { "GIT_DIR", "GIT_WORK_TREE", "GIT_INDEX_FILE", "GIT_OBJECT_DIRECTORY", "GIT_ALTERNATE_OBJECT_DIRECTORIES", "GIT_COMMON_DIR", "GIT_NAMESPACE", "GIT_PREFIX" };

        // Windows command-line quoting (the CommandLineToArgvW / MSVC rules git.exe parses with): each argument
        // arrives exactly as given, whatever it contains. Not a shell: nothing is ever interpreted.
        public static string Quote(string a)
        {
            if (a.Length > 0 && Regex.IsMatch(a, "^[A-Za-z0-9_./:=@+,-]+$")) return a;
            var sb = new StringBuilder("\"");
            for (int i = 0; ; i++)
            {
                int bs = 0;
                while (i < a.Length && a[i] == '\\') { bs++; i++; }
                if (i == a.Length) { sb.Append('\\', bs * 2); break; }
                if (a[i] == '"') sb.Append('\\', bs * 2 + 1).Append('"');
                else sb.Append('\\', bs).Append(a[i]);
            }
            return sb.Append('"').ToString();
        }

        // Runs git with these arguments in that folder; stdin: bytes for its standard input (the commit message), or
        // null. onOut gets output as it arrives (stdout and stderr in arrival order). Started with CreateProcess, the
        // way the scheduler starts Claude: stdin is a file holding exactly these bytes (.NET's own redirect would
        // first write the console code page's byte-order mark, e.g. under the UTF-8 system code page), and the
        // output goes to a file read as it grows.
        public static GitResult Run(string dir, string[] args, byte[] stdin, int timeoutMs, Action<string> onOut)
        {
            var res = new GitResult();
            var sb = new StringBuilder();
            Action<string> take = text =>
            {
                string line = Redact(text.TrimEnd('\r')) + "\n";
                if (sb.Length < MaxDiff * 2) sb.Append(line);
                if (onOut != null) try { onOut(line); } catch { }
            };
            var parts = new List<string> { Exe };
            foreach (var a in args) parts.Add(Quote(a));
            string tmp = Path.Combine(Path.GetTempPath(), "claude-status-git-" + Guid.NewGuid().ToString("N"));
            try
            {
                File.WriteAllBytes(tmp + ".in", stdin ?? new byte[0]);
                using (var fin = new FileStream(tmp + ".in", FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 4096, FileOptions.DeleteOnClose))
                using (var fout = new FileStream(tmp + ".out", FileMode.Create, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.DeleteOnClose))
                using (var rd = new FileStream(tmp + ".out", FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                {
                    IntPtr hin = fin.SafeFileHandle.DangerousGetHandle(), hout = fout.SafeFileHandle.DangerousGetHandle();
                    var si = new PN.STARTUPINFO { dwFlags = 0x100, hStdInput = hin, hStdOutput = hout, hStdError = hout };   // STARTF_USESTDHANDLES
                    si.cb = System.Runtime.InteropServices.Marshal.SizeOf(si);
                    PN.PROCESS_INFORMATION pi;
                    IntPtr env = EnvBlock();
                    bool ok;
                    try
                    {
                        PN.SetHandleInformation(hin, 1, 1); PN.SetHandleInformation(hout, 1, 1);   // HANDLE_FLAG_INHERIT, for this child only
                        ok = PN.CreateProcessW(null, new StringBuilder(string.Join(" ", parts)), IntPtr.Zero, IntPtr.Zero, true, 0x400 | 0x08000000, env, dir, ref si, out pi);   // Unicode env, no window
                    }
                    finally { PN.SetHandleInformation(hin, 1, 0); PN.SetHandleInformation(hout, 1, 0); System.Runtime.InteropServices.Marshal.FreeHGlobal(env); }
                    if (!ok) throw new System.ComponentModel.Win32Exception(System.Runtime.InteropServices.Marshal.GetLastWin32Error());
                    PN.CloseHandle(pi.hThread);
                    try
                    {
                        var pending = new MemoryStream();
                        var buf = new byte[8192];
                        Action drain = () =>
                        {
                            int n;
                            while ((n = rd.Read(buf, 0, buf.Length)) > 0)
                                for (int i = 0; i < n; i++)
                                {
                                    if (buf[i] != (byte)'\n') { pending.WriteByte(buf[i]); continue; }
                                    take(Encoding.UTF8.GetString(pending.ToArray())); pending.SetLength(0);
                                }
                        };
                        var sw = Stopwatch.StartNew();
                        while (PN.WaitForSingleObject(pi.hProcess, 100) != 0)
                        {
                            drain();
                            if (sw.ElapsedMilliseconds < timeoutMs) continue;
                            res.TimedOut = true;
                            KillTree(pi.dwProcessId);
                            PN.TerminateProcess(pi.hProcess, 1);
                            PN.WaitForSingleObject(pi.hProcess, 5000);
                            break;
                        }
                        drain();
                        if (pending.Length > 0) take(Encoding.UTF8.GetString(pending.ToArray()));
                        uint code;
                        if (!res.TimedOut && PN.GetExitCodeProcess(pi.hProcess, out code)) res.Code = (int)code;
                    }
                    finally { PN.CloseHandle(pi.hProcess); }
                }
            }
            catch (Exception e) { sb.Append("Couldn't start Git (" + e.Message + "). Is Git installed and on PATH?\n"); res.Code = -1; }
            finally { try { File.Delete(tmp + ".in"); } catch { } try { File.Delete(tmp + ".out"); } catch { } }
            res.Out = sb.ToString();
            return res;
        }

        // Our environment, minus anything that would point Git at another repository, plus no prompts.
        static IntPtr EnvBlock()
        {
            var vars = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (System.Collections.DictionaryEntry e in Environment.GetEnvironmentVariables()) vars[(string)e.Key] = (string)e.Value;
            foreach (var k in Isolate) vars.Remove(k);
            vars["GIT_TERMINAL_PROMPT"] = "0";   // never wait on an invisible terminal prompt
            var sb = new StringBuilder();
            foreach (var kv in vars) if (!kv.Key.StartsWith("=")) sb.Append(kv.Key).Append('=').Append(kv.Value).Append('\0');
            sb.Append('\0');
            return System.Runtime.InteropServices.Marshal.StringToHGlobalUni(sb.ToString());
        }
        static void KillTree(int pid)
        {
            try
            {
                var k = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "taskkill.exe"), "/PID " + pid + " /T /F") { UseShellExecute = false, CreateNoWindow = true };
                using (var p = Process.Start(k)) p.WaitForExit(5000);
            }
            catch { }
        }

        // Plain stdout of a read-only command (null when it failed).
        static string Read(string dir, params string[] args) { string err; return ReadRaw(dir, args, out err); }

        // Raw stdout (keeps NULs and exact bytes) for -z output; null when it failed.
        static string ReadRaw(string dir, string[] args, out string err)
        {
            err = "";
            var parts = new List<string>();
            foreach (var a in args) parts.Add(Quote(a));
            var psi = new ProcessStartInfo(Exe, string.Join(" ", parts))
            {
                WorkingDirectory = dir, UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
                StandardOutputEncoding = new UTF8Encoding(false), StandardErrorEncoding = new UTF8Encoding(false),
            };
            foreach (var k in Isolate) psi.EnvironmentVariables.Remove(k);
            psi.EnvironmentVariables["GIT_TERMINAL_PROMPT"] = "0";
            psi.EnvironmentVariables["GIT_OPTIONAL_LOCKS"] = "0";
            try
            {
                using (var p = Process.Start(psi))
                {
                    p.StandardInput.Close();
                    var o = p.StandardOutput.ReadToEndAsync();
                    var e = p.StandardError.ReadToEndAsync();
                    if (!p.WaitForExit(ReadTimeoutMs)) { KillTree(p.Id); err = "Git took too long."; return null; }
                    p.WaitForExit();
                    err = Redact(e.Result.Trim());
                    return p.ExitCode == 0 ? o.Result : null;
                }
            }
            catch (Exception ex) { err = "Couldn't start Git (" + ex.Message + "). Is Git installed and on PATH?"; return null; }
        }

        // Credentials never leave the laptop: "scheme://user:token@host" -> "scheme://***@host".
        public static string Redact(string s) { return s == null ? "" : Regex.Replace(s, @"(?i)\b([a-z][a-z0-9+.-]*://)[^/@\s]+@", "$1***@"); }

        static string Line(string s) { return s == null ? "" : s.Trim(); }

        // The worktree root that contains this folder (nested folders and linked worktrees resolve to their own root).
        public static string TopOf(string folder)
        {
            if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder)) return null;
            string t = Line(Read(folder, "rev-parse", "--show-toplevel"));
            if (t == "") return null;
            try { return Path.GetFullPath(t.Replace('/', '\\')).TrimEnd('\\'); } catch { return null; }
        }

        public static string Norm(string p) { try { return Path.GetFullPath(p).TrimEnd('\\', '/').ToLowerInvariant(); } catch { return (p ?? "").ToLowerInvariant(); } }
        // Either folder inside the other (a worker in a parent folder can change this repository too).
        public static bool Overlaps(string a, string b)
        {
            string x = Norm(a), y = Norm(b);
            return x == y || x.StartsWith(y + "\\") || y.StartsWith(x + "\\");
        }

        public static string Sha(string s)
        {
            using (var h = SHA256.Create()) return BitConverter.ToString(h.ComputeHash(Encoding.UTF8.GetBytes(s))).Replace("-", "").Substring(0, 32).ToLowerInvariant();
        }

        // Content of a file on disk, as of now.
        // shortcut: files over 50 MB are identified by size + write time instead of content; hash them if that ever matters.
        static string FileHash(string full)
        {
            try
            {
                var fi = new FileInfo(full);
                if (!fi.Exists) return Directory.Exists(full) ? "dir" : "none";
                if (fi.Length > 50L * 1024 * 1024) return "big|" + fi.Length + "|" + fi.LastWriteTimeUtc.Ticks;
                using (var h = SHA256.Create())
                using (var fs = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                    return BitConverter.ToString(h.ComputeHash(fs)).Replace("-", "");
            }
            catch { return "unreadable"; }
        }

        public static string ReadRawStatus(string top, out string err)
        {
            return ReadRaw(top, new[] { "-c", "core.quotepath=off", "status", "--porcelain=v2", "-z", "--branch", "--untracked-files=all" }, out err);
        }

        // Everything the review screen shows, plus fingerprints to catch any change before Send.
        public static GitView Inspect(string top)
        {
            var v = new GitView { Top = top };
            string err, raw = ReadRawStatus(top, out err);
            if (raw == null) { v.Error = err != "" ? err : "Git couldn't read this repository."; v.Blockers.Add("Git couldn't read this repository: " + v.Error); v.Fp = Sha("error|" + v.Error); return v; }
            v.Raw = raw;
            var f = raw.Split('\0');
            for (int i = 0; i < f.Length; i++)
            {
                string r = f[i];
                if (r == "") continue;
                if (r.StartsWith("# branch.oid ")) { v.Oid = r.Substring(13); if (v.Oid == "(initial)") v.Oid = ""; }
                else if (r.StartsWith("# branch.head ")) v.Branch = r.Substring(14);
                else if (r.StartsWith("# branch.ab "))
                {
                    var m = Regex.Match(r, @"\+(\d+) -(\d+)");
                    if (m.Success) { v.Ahead = int.Parse(m.Groups[1].Value); v.Behind = int.Parse(m.Groups[2].Value); }
                }
                else if (r.StartsWith("1 ") || r.StartsWith("2 ") || r.StartsWith("u "))
                {
                    int n = r[0] == '1' ? 9 : r[0] == '2' ? 10 : 11;
                    var p = r.Split(new[] { ' ' }, n);
                    if (p.Length < n) continue;
                    var gf = new GitFile { X = p[1].Substring(0, 1), Y = p[1].Substring(1, 1), Sub = p[2].StartsWith("S"), Path = p[n - 1], Conflict = r[0] == 'u' };
                    gf.Hash = p[1] + "|" + (r[0] == 'u' ? "" : p[6] + "|" + p[7]);
                    if (r[0] == '2' && i + 1 < f.Length) gf.Orig = f[++i];
                    v.Files.Add(gf);
                }
                else if (r.StartsWith("? ")) v.Files.Add(new GitFile { Path = r.Substring(2), Untracked = true, X = "?", Y = "?", Hash = "?" });
            }
            foreach (var gf in v.Files)
            {
                gf.Kind = gf.Conflict ? "conflict" : gf.Untracked ? "untracked" : gf.X == "D" || gf.Y == "D" ? "deleted" : gf.X == "A" ? "added"
                    : gf.X == "R" || gf.X == "C" ? "renamed" : "modified";
                gf.Hash = Sha(gf.Hash + "|" + gf.Path + "|" + gf.Orig + "|" + FileHash(Path.Combine(top, gf.Path.Replace('/', '\\'))));
            }
            v.GitDir = Line(Read(top, "rev-parse", "--absolute-git-dir"));
            bool detached = v.Branch == "(detached)" || v.Branch == "";
            if (!detached)
            {
                v.Remote = Line(Read(top, "config", "--get", "branch." + v.Branch + ".remote"));
                v.Merge = Line(Read(top, "config", "--get", "branch." + v.Branch + ".merge"));
                if (v.Remote != "" && v.Remote != ".") v.PushUrl = Line(Read(top, "remote", "get-url", "--push", v.Remote));
            }
            // Fingerprint of where the commit goes and what is staged (unselected files may change freely;
            // selected ones are checked by their own per-file hash).
            var fp = new StringBuilder(top + "|" + v.GitDir + "|" + v.Branch + "|" + v.Oid + "|" + v.Remote + "|" + v.Merge + "|" + v.PushUrl + "|" + v.Behind);
            foreach (var gf in v.Files) if (gf.Staged || gf.Conflict) fp.Append('\n').Append(gf.Hash);
            v.Fp = Sha(fp.ToString());
            v.PushUrl = Redact(v.PushUrl);

            if (detached) v.Blockers.Add("HEAD is detached (not on a branch). Check out a branch on the laptop first.");
            else if (v.Remote == "" || v.Merge == "") v.Blockers.Add("Branch “" + v.Branch + "” has no upstream to push to. Set one up on the laptop first (for example: git push -u origin " + v.Branch + ").");
            else if (v.Remote == ".") v.Blockers.Add("Branch “" + v.Branch + "” tracks another local branch, not a remote. Set a remote upstream on the laptop first.");
            else if (!v.Merge.StartsWith("refs/heads/")) v.Blockers.Add("The upstream of “" + v.Branch + "” isn't a branch (" + v.Merge + ").");
            else if (v.PushUrl == "") v.Blockers.Add("Remote “" + v.Remote + "” has no URL.");
            var conflicts = v.Files.FindAll(x => x.Conflict);
            if (conflicts.Count > 0) v.Blockers.Add("Git reports conflicts in " + Names(conflicts) + ". Resolve them on the laptop first.");
            foreach (var op in new[] { new[] { "MERGE_HEAD", "A merge" }, new[] { "rebase-merge", "A rebase" }, new[] { "rebase-apply", "A rebase or am" }, new[] { "CHERRY_PICK_HEAD", "A cherry-pick" }, new[] { "REVERT_HEAD", "A revert" }, new[] { "BISECT_LOG", "A bisect" } })
                if (v.GitDir != "" && (File.Exists(Path.Combine(v.GitDir, op[0])) || Directory.Exists(Path.Combine(v.GitDir, op[0]))))
                    v.Blockers.Add(op[1] + " is in progress in this repository. Finish or abort it on the laptop first.");
            if (v.Behind > 0) v.Blockers.Add("“" + v.Branch + "” is " + v.Behind + " commit" + (v.Behind == 1 ? "" : "s") + " behind " + v.Remote + "/" + v.Dest + " (as of the last fetch), so a push would be rejected. Bring it up to date on the laptop first.");
            if (v.Files.Count > MaxFiles) v.Blockers.Add("There are more than " + MaxFiles + " changed files. Commit this one from the laptop.");
            if (v.Ahead > 0) v.Warnings.Add(v.Ahead + " earlier commit" + (v.Ahead == 1 ? "" : "s") + " on “" + v.Branch + "” not yet pushed will be pushed too.");
            return v;
        }

        static string Names(List<GitFile> list)
        {
            var n = new List<string>();
            for (int i = 0; i < list.Count && i < 5; i++) n.Add(list[i].Path);
            return string.Join(", ", n) + (list.Count > 5 ? " and " + (list.Count - 5) + " more" : "");
        }

        // The exact commands Send runs for this selection (the phone builds the same list and shows it; the
        // laptop refuses to run anything else).
        public static List<string[]> Commands(GitView v, List<GitFile> selected)
        {
            var cmds = new List<string[]>();
            var add = new List<string> { "git", "--literal-pathspecs", "add", "--" };
            foreach (var f in selected) if (f.NeedsAdd) add.Add(f.Path);
            if (add.Count > 4) cmds.Add(add.ToArray());
            cmds.Add(new[] { "git", "commit", "--cleanup=verbatim", "--file=-" });
            cmds.Add(PushCommand(v.Remote, v.Branch, v.Merge));
            return cmds;
        }

        public static string[] PushCommand(string remote, string branch, string merge) { return new[] { "git", "push", remote, "refs/heads/" + branch + ":" + merge }; }

        // Why this selection can't be sent (null = fine). The phone shows the same reasons before enabling Send.
        public static string SelectionProblem(GitView v, List<GitFile> selected, string message)
        {
            if (selected.Count == 0) return "Select at least one file.";
            var picked = new HashSet<string>();
            foreach (var f in selected) picked.Add(f.Path);
            var other = v.Files.FindAll(f => f.Staged && !picked.Contains(f.Path));
            if (other.Count > 0) return "Already staged on the laptop but not selected: " + Names(other) + ". Select them too, or unstage them on the laptop.";
            if (message == null || message.Trim() == "") return "Write a commit message.";
            if (message.IndexOf('\0') >= 0) return "The message can't contain a NUL character.";
            if (Encoding.UTF8.GetByteCount(message) > MaxMessage) return "The message is too long.";
            int len = 0;
            foreach (var f in selected) len += f.Path.Length + 3;
            if (len > MaxCmdChars) return "Too many files selected for one command. Select fewer, or commit from the laptop.";
            return null;
        }

        // Diff of one changed file, as Git shows it (staged and unstaged separately; untracked files as their content).
        public static Dictionary<string, object> Diff(string top, GitFile f)
        {
            var o = new Dictionary<string, object> { { "result", "ok" }, { "p", f.Path }, { "kind", f.Kind } };
            var parts = new List<object>();
            if (f.Untracked)
            {
                string full = Path.Combine(top, f.Path.Replace('/', '\\'));
                try
                {
                    var fi = new FileInfo(full);
                    byte[] head;
                    using (var fs = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                    {
                        head = new byte[(int)Math.Min(fi.Length, MaxDiff)];
                        int n = 0, r;
                        while (n < head.Length && (r = fs.Read(head, n, head.Length - n)) > 0) n += r;
                    }
                    bool bin = Array.IndexOf(head, (byte)0, 0, Math.Min(head.Length, 8000)) >= 0;
                    parts.Add(new Dictionary<string, object> { { "t", "new" }, { "bin", bin }, { "size", fi.Length }, { "trunc", fi.Length > MaxDiff },
                        { "text", bin ? "" : Encoding.UTF8.GetString(head) } });
                }
                catch (Exception e) { parts.Add(new Dictionary<string, object> { { "t", "new" }, { "err", "Couldn't read the file: " + e.Message } }); }
            }
            else
            {
                foreach (var staged in new[] { true, false })
                {
                    if (staged ? f.X == "." : f.Y == ".") continue;
                    var args = new List<string> { "--literal-pathspecs", "-c", "core.quotepath=off", "diff", "--no-color", "--no-ext-diff", "--no-textconv", "-M" };
                    if (staged) args.Add("--cached");
                    args.Add("--"); args.Add(f.Path);
                    if (f.Orig != "") args.Add(f.Orig);
                    string err, text = ReadRaw(top, args.ToArray(), out err);
                    bool trunc = text != null && text.Length > MaxDiff;
                    parts.Add(new Dictionary<string, object> { { "t", staged ? "staged" : "unstaged" }, { "trunc", trunc },
                        { "bin", text != null && Regex.IsMatch(text, "^Binary files .* differ$", RegexOptions.Multiline) },
                        { "text", text == null ? "" : Redact(trunc ? text.Substring(0, MaxDiff) : text) }, { "err", text == null ? err : "" } });
                }
            }
            o["parts"] = parts;
            return o;
        }

        public static bool AuthProblem(string output)
        {
            return Regex.IsMatch(output ?? "", @"(?i)authentication failed|could not read (username|password)|terminal prompts disabled|permission denied \(publickey|host key verification failed|invalid username or password|logon failed|access denied|returned error: 40[13]|gpg failed to sign|passphrase|credential");
        }
    }

    // The phone's /api/git requests (phone.cs routes them here). Repositories come only from the workers on the
    // board (their folders, from Claude Code's own transcripts) and the scheduler's projects.
    public sealed class GitDesk
    {
        readonly Func<string, string> token;
        readonly Func<List<GitWorker>> workers;
        readonly object gate = new object();
        readonly Dictionary<string, string> cwdCache = new Dictionary<string, string>();          // session -> folder
        readonly Dictionary<string, string> topCache = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);   // folder -> repo root ("" = none)
        readonly Dictionary<string, DateTime> topMiss = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        readonly Dictionary<string, string> tokens = new Dictionary<string, string>();            // token -> repo root
        readonly Dictionary<string, GitJob> jobs = new Dictionary<string, GitJob>();
        Dictionary<string, object> reposCache;
        DateTime reposAt = DateTime.MinValue;

        // session id -> its project folder (null = unknown). Default: Claude Code's transcript ("cwd").
        public Func<string, string> CwdOf = TranscriptCwd;
        // Scheduler project folders (offered by the manual Commit view too).
        public Func<List<string>> Projects;

        public GitDesk(Func<string, string> token, Func<List<GitWorker>> workers) { this.token = token; this.workers = workers; }

        static string TranscriptCwd(string sid)
        {
            var f = Sessions.Find(Sessions.ProjectsDir(), "", sid);
            if (f == null) return null;
            string c = Sessions.Read(f).Cwd;
            return c == "" ? null : c;
        }

        static bool Active(string state) { return state == Board.Working || state == Board.NeedsYou; }

        string Cwd(string sid)
        {
            lock (gate) { string c; if (cwdCache.TryGetValue(sid, out c)) return c; }
            string cwd = null;
            try { cwd = CwdOf(sid); } catch { }
            if (cwd != null) lock (gate) cwdCache[sid] = cwd;
            return cwd;
        }

        string Top(string folder)
        {
            lock (gate)
            {
                string t; DateTime miss;
                if (topCache.TryGetValue(folder, out t)) return t;
                if (topMiss.TryGetValue(folder, out miss) && (DateTime.UtcNow - miss).TotalSeconds < 30) return null;
            }
            string top = Git.TopOf(folder);
            lock (gate) { if (top != null) topCache[folder] = top; else topMiss[folder] = DateTime.UtcNow; }
            return top;
        }

        string TokenOf(string top) { string t = token("g|" + Git.Norm(top)); lock (gate) tokens[t] = top; return t; }

        string TopFor(string t)
        {
            string top;
            lock (gate) if (tokens.TryGetValue(t, out top)) return top;
            Repos(true);
            lock (gate) return tokens.TryGetValue(t, out top) ? top : null;
        }

        // Why a worker blocks committing in this repository right now ("" = none).
        string BusyReason(string top)
        {
            foreach (var w in workers())
            {
                if (!Active(w.State)) continue;
                string cwd = Cwd(w.SessionId);
                if (cwd == null) return "“" + w.Name + "” is working and its folder couldn't be identified, so it might be changing this repository. Wait until it finishes, then refresh.";
                if (Git.Overlaps(cwd, top)) return "“" + w.Name + "” is " + (w.State == Board.NeedsYou ? "waiting for you" : "working") + " in this repository. Wait until it finishes, then refresh.";
            }
            return "";
        }

        // Repositories of the board's workers (and scheduler projects), with how many files changed.
        public Dictionary<string, object> Repos(bool fresh)
        {
            lock (gate) if (!fresh && reposCache != null && (DateTime.UtcNow - reposAt).TotalSeconds < 3) return reposCache;
            var byTop = new Dictionary<string, List<object>>(StringComparer.OrdinalIgnoreCase);
            var order = new List<string>();
            Action<string, GitWorker> addTo = (folder, w) =>
            {
                if (string.IsNullOrEmpty(folder)) return;
                string top = Top(folder);
                if (top == null) return;
                List<object> list;
                if (!byTop.TryGetValue(top, out list)) { byTop[top] = list = new List<object>(); order.Add(top); }
                if (w != null) list.Add(new Dictionary<string, object> { { "k", w.Key }, { "n", w.Name }, { "s", w.State } });
            };
            var ws = workers();
            foreach (var w in ws) addTo(Cwd(w.SessionId), w);
            if (Projects != null) try { foreach (var p in Projects()) addTo(p, null); } catch { }
            var repos = new List<object>();
            foreach (var top in order)
            {
                string err, raw = Git.ReadRawStatus(top, out err);
                int n = 0;
                if (raw != null) foreach (var r in raw.Split('\0')) if (r.Length > 1 && (r[0] == '1' || r[0] == '2' || r[0] == 'u' || r[0] == '?') && r[1] == ' ') n++;
                string busy = BusyReason(top);
                repos.Add(new Dictionary<string, object> { { "r", TokenOf(top) }, { "name", Path.GetFileName(top) }, { "path", top }, { "n", n }, { "ok", raw != null },
                    { "sig", Git.Sha(raw ?? "") }, { "busy", busy != "" }, { "workers", byTop[top] } });
            }
            var o = new Dictionary<string, object> { { "result", "ok" }, { "repos", repos } };
            lock (gate) { reposCache = o; reposAt = DateTime.UtcNow; }
            return o;
        }

        public Dictionary<string, object> Review(string t)
        {
            string top = TopFor(t);
            if (top == null) return Unknown();
            var v = Git.Inspect(top);
            string busy = BusyReason(top);
            if (busy != "") v.Blockers.Add(busy);
            var files = new List<object>();
            foreach (var f in v.Files)
                files.Add(new Dictionary<string, object> { { "p", f.Path }, { "o", f.Orig }, { "x", f.X }, { "y", f.Y }, { "kind", f.Kind }, { "st", f.Staged }, { "un", f.Unstaged },
                    { "a", f.NeedsAdd }, { "h", f.Hash }, { "sub", f.Sub }, { "cf", f.Conflict } });
            return new Dictionary<string, object>
            {
                { "result", "ok" }, { "r", t }, { "name", Path.GetFileName(top) }, { "path", top }, { "branch", v.Branch == "(detached)" ? "" : v.Branch },
                { "oid", v.Oid }, { "remote", v.Remote }, { "merge", v.Merge }, { "dest", v.Dest }, { "url", v.PushUrl }, { "ahead", v.Ahead }, { "behind", v.Behind },
                { "fp", v.Fp }, { "files", files }, { "block", v.Blockers }, { "warn", v.Warnings }, { "busy", busy != "" }, { "job", RunningJobId(top) },
            };
        }

        public Dictionary<string, object> Diff(string t, string path)
        {
            string top = TopFor(t);
            if (top == null) return Unknown();
            var v = Git.Inspect(top);
            var f = v.Files.Find(x => x.Path == path);   // only files Git itself lists as changed; never any other path
            if (f == null) return new Dictionary<string, object> { { "result", "gone" } };
            var d = Git.Diff(top, f);
            d["h"] = f.Hash;
            return d;
        }

        static Dictionary<string, object> Unknown() { return new Dictionary<string, object> { { "result", "unknown" } }; }
        static Dictionary<string, object> Says(string result, string msg) { return new Dictionary<string, object> { { "result", result }, { "msg", msg } }; }

        string RunningJobId(string top)
        {
            lock (gate) foreach (var j in jobs.Values) if (j.State == "running" && Git.Norm(j.Top) == Git.Norm(top)) return j.Id;
            return "";
        }

        public Dictionary<string, object> Job(string id)
        {
            lock (gate)
            {
                GitJob j;
                if (!jobs.TryGetValue(id, out j)) return Unknown();
                return Snapshot(j);
            }
        }

        // Send: Commit & Push. id = the phone's one-time id for this Send; a repeat (double tap, retry after a
        // dropped connection) gets the same job back instead of a second run.
        public Dictionary<string, object> Commit(string id, string t, string fp, List<KeyValuePair<string, string>> picked, string message, List<string[]> cmds)
        {
            lock (gate)
            {
                GitJob have;
                if (jobs.TryGetValue(id, out have)) return Snapshot(have);
            }
            string top = TopFor(t);
            if (top == null) return Unknown();
            GitJob job;
            lock (gate)
            {
                GitJob have;
                if (jobs.TryGetValue(id, out have)) return Snapshot(have);   // the same Send arriving twice at once
                if (RunningJobId(top) != "") return Says("busy", "Another commit or push is already running in this repository.");
                job = new GitJob { Id = id, Token = t, Top = top, Name = Path.GetFileName(top) };
                jobs[id] = job;
                Prune();
            }
            new Thread(() => RunCommit(job, fp, picked, message, cmds)) { IsBackground = true, Name = "ClaudeStatusGit" }.Start();
            lock (gate) return Snapshot(job);
        }

        void RunCommit(GitJob job, string fp, List<KeyValuePair<string, string>> picked, string message, List<string[]> cmds)
        {
            var check = new GitStep { Label = "Check nothing changed since your review", State = "running" };
            lock (gate) job.Steps.Add(check);
            try
            {
                var v = Git.Inspect(job.Top);
                string why = null;
                var selected = new List<GitFile>();
                if (v.Fp != fp) why = "The branch, destination or staged changes changed since your review.";
                foreach (var kv in picked)
                {
                    if (why != null) break;
                    var f = v.Files.Find(x => x.Path == kv.Key);
                    if (f == null || f.Hash != kv.Value) why = "“" + kv.Key + "” changed since your review.";
                    else if (selected.Contains(f)) why = "A file was selected twice.";
                    else selected.Add(f);
                }
                if (why != null) { Finish(job, check, "stale", why + " Nothing was run. Review the changes again."); return; }
                string busy = BusyReason(job.Top);
                if (busy != "") v.Blockers.Add(busy);
                if (v.Blockers.Count > 0) { Finish(job, check, "blocked", v.Blockers[0] + " Nothing was run."); return; }
                string problem = Git.SelectionProblem(v, selected, message);
                if (problem != null) { Finish(job, check, "blocked", problem + " Nothing was run."); return; }
                var mine = Git.Commands(v, selected);
                if (!SameCommands(mine, cmds)) { Finish(job, check, "stale", "The commands you reviewed don't match what would run now. Nothing was run. Review again."); return; }
                lock (gate)
                {
                    check.State = "ok";
                    job.Branch = v.Branch; job.Remote = v.Remote; job.Merge = v.Merge; job.PushUrl = v.PushUrl;
                    foreach (var c in mine) job.Steps.Add(new GitStep { Args = c });
                }
                int last = mine.Count - 1;
                for (int i = 0; i < mine.Count; i++)
                {
                    GitStep step;
                    lock (gate) step = job.Steps[i + 1];
                    bool isCommit = mine[i][1] == "commit", isPush = i == last;
                    if (isPush)
                    {
                        string head = Head(job.Top);
                        lock (gate) job.Commit = head != v.Oid ? head : "";
                        if (job.Commit == "") { Finish(job, null, "commit_failed", "Git reported success but HEAD didn't move, so nothing was pushed."); return; }
                    }
                    var r = Exec(job, step, isCommit ? new UTF8Encoding(false).GetBytes(message) : null, isPush ? Git.PushTimeoutMs : Git.CommitTimeoutMs);
                    if (r.Code == 0) continue;
                    SkipRest(job);
                    if (isPush) { PushFailed(job, r); return; }
                    string staged = mine[0][2] == "add" ? " Files staged by this Send stay staged; nothing was unstaged or discarded." : "";
                    if (r.TimedOut || Git.AuthProblem(r.Out))
                        Finish(job, null, "attention", (r.TimedOut ? "Git was still waiting after " + (Git.CommitTimeoutMs / 1000) + " s and was stopped." : "Git needs something on the laptop (sign-in, signing key or passphrase).") + " No commit was created." + staged);
                    else Finish(job, null, "commit_failed", (isCommit ? "The commit failed (see Git's output above; a hook may have rejected it)." : "Staging failed.") + " No commit was created." + staged);
                    return;
                }
                Finish(job, null, "pushed", "Committed " + Short(job.Commit) + " and pushed to " + job.Remote + "/" + Dest(job.Merge) + ".");
            }
            catch (Exception e) { Finish(job, check.State == "running" ? check : null, "commit_failed", "Something went wrong on the laptop: " + e.Message); }
        }

        void PushFailed(GitJob job, GitResult r)
        {
            string kept = "The commit " + Short(job.Commit) + " was created and is kept on the laptop.";
            if (r.TimedOut) Finish(job, null, "attention", kept + " The push was still waiting after " + (Git.PushTimeoutMs / 1000) + " s (a sign-in window on the laptop?) and was stopped. Finish signing in there, then Retry Push.");
            else if (Git.AuthProblem(r.Out)) Finish(job, null, "attention", kept + " The push needs attention on the laptop (sign-in or credentials). Sort it out there, then Retry Push.");
            else Finish(job, null, "push_failed", kept + " The push failed (see Git's output above).");
        }

        // Retry Push: only the push, for a job whose commit exists; n = the attempts the phone has seen, so a
        // double tap can't start two pushes.
        public Dictionary<string, object> RetryPush(string id, int n)
        {
            GitJob job;
            lock (gate)
            {
                if (!jobs.TryGetValue(id, out job)) return Unknown();
                if (!job.CanRetry || job.PushAttempts != n) return Snapshot(job);
                job.State = "running"; job.Result = ""; job.Message = ""; job.PushAttempts++; job.StartedUtc = DateTime.UtcNow;
                foreach (var s in job.Steps) if (s.State != "ok") s.State = "skipped";
            }
            new Thread(() =>
            {
                var check = new GitStep { Label = "Check the commit and destination are unchanged", State = "running" };
                lock (gate) job.Steps.Add(check);
                try
                {
                    var v = Git.Inspect(job.Top);
                    if (v.Branch != job.Branch || v.Oid != job.Commit || v.Remote != job.Remote || v.Merge != job.Merge || v.PushUrl != job.PushUrl)
                    {
                        lock (gate) job.Moved = true;
                        Finish(job, check, "push_failed", "The repository changed since the commit (branch, HEAD or destination), so nothing was pushed. The commit " + Short(job.Commit) + " is kept; it goes out with your next Send from a fresh review, or push it from the laptop.");
                        return;
                    }
                    var step = new GitStep { Args = Git.PushCommand(job.Remote, job.Branch, job.Merge) };
                    lock (gate) { check.State = "ok"; job.Steps.Add(step); }
                    var r = Exec(job, step, null, Git.PushTimeoutMs);
                    if (r.Code == 0) Finish(job, null, "pushed", "Pushed " + Short(job.Commit) + " to " + job.Remote + "/" + Dest(job.Merge) + ".");
                    else PushFailed(job, r);
                }
                catch (Exception e) { Finish(job, check.State == "running" ? check : null, "push_failed", "Something went wrong on the laptop: " + e.Message); }
            }) { IsBackground = true, Name = "ClaudeStatusGitPush" }.Start();
            lock (gate) return Snapshot(job);
        }

        GitResult Exec(GitJob job, GitStep step, byte[] stdin, int timeout)
        {
            lock (gate) step.State = "running";
            var args = new string[step.Args.Length - 1];
            Array.Copy(step.Args, 1, args, 0, args.Length);
            var r = Git.Run(job.Top, args, stdin, timeout, line => { lock (gate) if (step.Out.Length < Git.MaxOut) step.Out.Append(line); });
            lock (gate) { step.Code = r.Code; step.State = r.Code == 0 ? "ok" : "failed"; if (r.TimedOut) step.Out.Append("(stopped: no answer after " + (timeout / 1000) + " s)\n"); }
            return r;
        }

        static string Head(string top) { var r = Git.Run(top, new[] { "rev-parse", "-q", "--verify", "HEAD" }, null, Git.ReadTimeoutMs, null); return r.Code == 0 ? r.Out.Trim() : ""; }
        static string Short(string sha) { return sha.Length > 10 ? sha.Substring(0, 10) : sha; }
        static string Dest(string merge) { return merge.StartsWith("refs/heads/") ? merge.Substring(11) : merge; }

        void SkipRest(GitJob job) { lock (gate) foreach (var s in job.Steps) if (s.State == "waiting") s.State = "skipped"; }

        void Finish(GitJob job, GitStep check, string result, string msg)
        {
            lock (gate)
            {
                if (check != null) check.State = result == "pushed" ? "ok" : "failed";
                foreach (var s in job.Steps) if (s.State == "waiting") s.State = "skipped";
                job.Result = result; job.Message = msg; job.State = "finished";
            }
            lock (gate) reposCache = null;
        }

        static bool SameCommands(List<string[]> a, List<string[]> b)
        {
            if (b == null || a.Count != b.Count) return false;
            for (int i = 0; i < a.Count; i++)
            {
                if (a[i].Length != b[i].Length) return false;
                for (int j = 0; j < a[i].Length; j++) if (a[i][j] != b[i][j]) return false;
            }
            return true;
        }

        void Prune()
        {
            if (jobs.Count <= 50) return;
            foreach (var k in new List<string>(jobs.Keys))
                if (jobs[k].State != "running" && (DateTime.UtcNow - jobs[k].StartedUtc).TotalHours > 24) jobs.Remove(k);
        }

        // Under gate.
        Dictionary<string, object> Snapshot(GitJob j)
        {
            var steps = new List<object>();
            foreach (var s in j.Steps)
                steps.Add(new Dictionary<string, object> { { "c", s.Args }, { "l", s.Label }, { "st", s.State }, { "out", s.Out.ToString() }, { "code", s.Code } });
            return new Dictionary<string, object>
            {
                { "result", "job" }, { "id", j.Id }, { "r", j.Token }, { "name", j.Name }, { "state", j.State }, { "outcome", j.Result }, { "msg", j.Message },
                { "commit", j.Commit }, { "retry", j.CanRetry }, { "n", j.PushAttempts }, { "steps", steps },
                { "push", j.Remote != "" ? Git.PushCommand(j.Remote, j.Branch, j.Merge) : null }, { "running", (DateTime.UtcNow - j.StartedUtc).TotalSeconds },
            };
        }
    }
}
