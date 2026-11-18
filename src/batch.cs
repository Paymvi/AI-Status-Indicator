// Claude Status - task batches: an ordered series of prompts you schedule, run and monitor together. Compiled with
// indicator.cs (C# 5). There is no second runner: each step becomes an ordinary queue task (scheduler.cs) when, and only
// when, the step before it (its prompt and its optional commit) succeeded. Batch definitions and their runs live in
// tasks.json next to the tasks and change only through the queue's locked write, so "start step 3" and "step 3 started"
// are one write, a restart never runs a finished step again, and two app instances can't both start it.
// Commits between steps use git.cs (the phone's Commit runner): your own Git, your message on stdin, `git add` of the
// files this step changed and `git commit --cleanup=verbatim --file=-`. Never a push, amend, reset, --no-verify or `add .`.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Drawing;
using System.Windows.Forms;

namespace ClaudeStatus
{
    // One prompt of a batch, as you defined it.
    public class BatchStep
    {
        public string Id = "", Title = "", Project = "", Prompt = "";
        public string Conversation = "new";   // new | continue (the previous step's conversation: same project only; step 1: ResumeSession)
        public string ResumeSession = "";     // step 1 only: an existing conversation of its project you chose
        public bool Handoff;                  // new conversations: include the previous step's final response (handoff file)
        public bool Review, Commit;           // pause before the next step / commit this step's changes with Git
        public string CommitMessage = "";
    }

    // A batch definition (editable; runs freeze a copy of it).
    public class BatchDef
    {
        public string Id = "", Name = "", Agent = "claude";
        public string Start = "manual";       // manual (Run now) | at (RunAtUtc)
        public DateTime RunAtUtc, CreatedUtc, UpdatedUtc;
        public string Runner = "vscode", PermissionMode = "acceptEdits", Model = "";
        public List<string> AllowCommands = new List<string>();
        public bool AllowDelete;
        public List<BatchStep> Steps = new List<BatchStep>();
        public int Revision;
    }

    // A step of a run: the frozen definition plus what happened.
    public class BatchRunStep : BatchStep
    {
        public string State = "pending";      // pending | prompt | commit | done | skipped | canceled
        public string Outcome = "";           // completed | accepted (you confirmed an unclear result) | skipped
        public string TaskId = "", SessionId = "", Summary = "", RunLog = "", HandoffFile = "";
        public DateTime StartedUtc, EndedUtc;
        public int Attempts;
        public bool Reviewed;
        // commit after this step
        public string CommitState = "";       // "" (off / not yet) | waiting | running | done | none | select | failed | uncertain | skipped
        public string Repo = "", Branch = "", BaseHead = "", CommitHash = "", CommitNote = "", CommitOut = "";
        public bool BaselineTaken;
        public List<string> Baseline = new List<string>();   // "path\thash\tstaged" of the files already changed when the step started
        public List<string> Picked = new List<string>();     // your explicit selection: "path\thash"
        public List<string> Choices = new List<string>();    // what you can pick from: "path\thash\tkind\twhy"
        public List<string> Committed = new List<string>();  // the files the commit included
        public int CommitAttempts;
        public string CommitOwner = "";
        public int CommitOwnerPid;
        public DateTime CommitOwnerStartedUtc, CommitStartedUtc;
    }

    public class BatchRun
    {
        public string Id = "", BatchId = "", Name = "", Agent = "claude";
        public string State = "Scheduled";    // Scheduled | Running | Paused | Completed | Canceled
        public string PauseKind = "";         // user | review | start | prompt | unknown | commit
        public string Reason = "";
        public int DefRevision, Current, Seq;
        public bool PauseAfterStep, RunNow;
        public DateTime CreatedUtc, StartAtUtc, StartedUtc, FinishedUtc;
        public string Runner = "vscode", PermissionMode = "acceptEdits", Model = "";
        public List<string> AllowCommands = new List<string>();
        public bool AllowDelete;
        public List<BatchRunStep> Steps = new List<BatchRunStep>();
        public List<string> History = new List<string>();
    }

    public class CommitPlan { public string Kind = "", Note = ""; public List<GitFile> Files = new List<GitFile>(); public List<string> Choices = new List<string>(); }
    public class CommitOutcome { public string State = "", Hash = "", Note = "", Out = "", Branch = ""; public List<string> Choices = new List<string>(), Committed = new List<string>(); }

    // ---------- pure rules (tested directly) ----------
    public static class Batching
    {
        public const string Scheduled = "Scheduled", Running = "Running", Paused = "Paused", Completed = "Completed", Canceled = "Canceled";
        public const int HandoffExcerpt = 6000, MaxSteps = 50, MaxOut = 8000;
        public static bool Finished(string s) { return s == Completed || s == Canceled; }
        public static bool Active(string s) { return s == Running || s == Paused; }

        public static void Normalize(QueueData d)
        {
            if (d.Batches == null) d.Batches = new List<BatchDef>();
            if (d.BatchRuns == null) d.BatchRuns = new List<BatchRun>();
            d.Batches.RemoveAll(b => b == null); d.BatchRuns.RemoveAll(r => r == null);
            foreach (var b in d.Batches)
            {
                QueueStore.Fix(b);
                if (b.AllowCommands == null) b.AllowCommands = new List<string>();
                if (b.Steps == null) b.Steps = new List<BatchStep>();
                b.Steps.RemoveAll(s => s == null);
                foreach (var s in b.Steps) QueueStore.Fix(s);
            }
            foreach (var r in d.BatchRuns)
            {
                QueueStore.Fix(r);
                if (r.AllowCommands == null) r.AllowCommands = new List<string>();
                if (r.History == null) r.History = new List<string>();
                if (r.Steps == null) r.Steps = new List<BatchRunStep>();
                r.Steps.RemoveAll(s => s == null);
                foreach (var s in r.Steps)
                {
                    QueueStore.Fix(s);
                    if (s.Baseline == null) s.Baseline = new List<string>();
                    if (s.Picked == null) s.Picked = new List<string>();
                    if (s.Choices == null) s.Choices = new List<string>();
                    if (s.Committed == null) s.Committed = new List<string>();
                }
            }
        }

        // The run that owns the batch slot: one batch runs (or is paused) at a time, so two batches never mix their
        // conversations or each other's changes into a commit.
        public static BatchRun ActiveRun(QueueData d) { foreach (var r in d.BatchRuns) if (Active(r.State)) return r; return null; }

        public static BatchRunStep Cur(BatchRun r) { return r != null && r.Current >= 0 && r.Current < r.Steps.Count ? r.Steps[r.Current] : null; }

        // A step task of a run that isn't finished: kept (not removed or cleared), because the run still needs it.
        public static bool Holds(QueueData d, QueueTask t)
        {
            if (t.BatchRun == "") return false;
            var r = d.FindRun(t.BatchRun);
            return r != null && !Finished(r.State);
        }

        // Why an ordinary queued task must wait for the batch (null = it may run). Its conversation is the batch's own,
        // or the batch has a step whose commit would otherwise pick up this task's changes in the same repository.
        public static string HoldFor(QueueData d, QueueTask t)
        {
            if (t.BatchRun != "") return null;
            string sid = t.Conversation == "resume" ? t.ResumeSession : t.SessionId;
            if (t.Conversation == "predecessor") { var p = d.Find(t.After); sid = p != null ? p.SessionId : ""; }
            foreach (var r in d.BatchRuns)
            {
                if (Finished(r.State)) continue;
                foreach (var x in r.Steps)
                {
                    var xt = x.TaskId != "" ? d.Find(x.TaskId) : null;
                    string xs = x.SessionId != "" ? x.SessionId : xt != null ? xt.SessionId : "";
                    if (Sched.IsGuid(sid) && string.Equals(xs, sid, StringComparison.OrdinalIgnoreCase))
                        return "Waiting: this conversation belongs to the batch \"" + r.Name + "\", which hasn't finished. It continues after the batch.";
                }
                var s = Cur(r);
                if (Active(r.State) && s != null && s.Commit && (s.State == "prompt" || s.State == "commit") && Git.Overlaps(t.Project, s.Repo != "" ? s.Repo : s.Project))
                    return "Waiting: the batch \"" + r.Name + "\" has an unfinished step (" + (r.Current + 1) + " of " + r.Steps.Count + ") that commits in this repository. This task runs after that commit, so its changes aren't mixed into it.";
            }
            return null;
        }

        public static string Name(string folder) { try { return Path.GetFileName(Sched.NormPath(folder)); } catch { return folder ?? ""; } }

        // Everything a batch needs before it can start. topOf: the Git repository root of a folder (null = none).
        public static string Validate(BatchDef b, Func<string, string> topOf)
        {
            if (b.Name.Trim() == "") return "Give the batch a name.";
            if (b.Agent != "claude") return "Only Claude Code can run batches on this laptop.";
            if (b.Steps.Count == 0) return "Add at least one step.";
            if (b.Steps.Count > MaxSteps) return "A batch can have at most " + MaxSteps + " steps.";
            if (b.PermissionMode != "acceptEdits" && b.PermissionMode != "dontAsk" && b.PermissionMode != "plan") return "Unknown permission mode.";
            if (b.Model != "" && !Regex.IsMatch(b.Model, @"^[A-Za-z0-9._\[\]-]{1,80}$")) return "The model name can only contain letters, digits and . _ - [ ].";
            foreach (var c in b.AllowCommands) { string e = Sched.CheckCommand(c); if (e != null) return e; }
            var ids = new HashSet<string>();
            for (int i = 0; i < b.Steps.Count; i++)
            {
                var s = b.Steps[i];
                string n = "Step " + (i + 1);
                if (s.Id == "" || !ids.Add(s.Id)) return n + " has no unique id (internal error). Remove it and add it again.";
                if (s.Project.Trim() == "" || !Directory.Exists(s.Project)) return n + ": choose an existing project folder.";
                if (s.Prompt.Trim() == "") return n + ": write the prompt.";
                if (s.Prompt.IndexOf('\0') >= 0) return n + ": the prompt can't contain a NUL character.";
                if (b.Runner != "headless" && s.Prompt.Length > QueueEngine.MaxArgChars - 4000 - HandoffExcerpt) return n + ": the prompt is too long to start in a VS Code terminal. Shorten it, or run the batch in the background.";
                if (s.Conversation != "new" && s.Conversation != "continue") return n + ": unknown conversation choice.";
                if (s.Conversation == "continue")
                {
                    if (i == 0) { if (!Sched.IsGuid(s.ResumeSession)) return "Step 1 has no previous step: choose the existing conversation it continues, or start a fresh one."; }
                    else if (!Sched.SamePath(b.Steps[i - 1].Project, s.Project))
                        return n + " continues step " + i + "'s conversation, but runs in a different project (" + Name(s.Project) + " instead of " + Name(b.Steps[i - 1].Project) + "). A Claude Code conversation belongs to one folder, so a step that switches projects starts a fresh conversation. Choose Fresh conversation, and tick the handoff to pass step " + i + "'s result along.";
                }
                if (s.Handoff && s.Conversation == "new" && i == 0) return "Step 1 has no previous step to hand off from. Untick its handoff.";
                if (s.Commit)
                {
                    if (s.CommitMessage.Trim() == "") return n + ": write the commit message (or untick Commit after this step).";
                    if (s.CommitMessage.IndexOf('\0') >= 0 || Encoding.UTF8.GetByteCount(s.CommitMessage) > Git.MaxMessage) return n + ": the commit message is too long or contains a NUL character.";
                    if (topOf(s.Project) == null) return n + ": " + Name(s.Project) + " isn't in a Git repository, so it can't commit. Untick Commit after this step.";
                }
            }
            return null;
        }

        // A run's frozen copy of the definition: editing the batch later never changes a run that exists.
        public static BatchRun Freeze(BatchDef b, string id, DateTime now, DateTime startAt)
        {
            var ser = QueueStore.Ser();
            var r = new BatchRun
            {
                Id = id, BatchId = b.Id, Name = b.Name, Agent = b.Agent, DefRevision = b.Revision, CreatedUtc = now, StartAtUtc = startAt,
                Runner = b.Runner, PermissionMode = b.PermissionMode, Model = b.Model, AllowCommands = new List<string>(b.AllowCommands), AllowDelete = b.AllowDelete,
            };
            foreach (var s in b.Steps)
            {
                var c = ser.Deserialize<BatchRunStep>(ser.Serialize(s));
                if (c.Conversation != "new") c.Handoff = false;   // a continued conversation has the context already
                if (c.Conversation != "continue") c.ResumeSession = "";
                if (c.Title.Trim() == "") c.Title = Sched.FirstLine(c.Prompt, 60);
                if (!c.Commit) c.CommitMessage = "";
                r.Steps.Add(c);
            }
            Normalize(new QueueData { BatchRuns = new List<BatchRun> { r } });
            return r;
        }

        public static void Note(BatchRun r, DateTime now, string text)
        {
            r.History.Add(now.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + "  " + text);
            if (r.History.Count > 300) r.History.RemoveRange(0, r.History.Count - 300);
        }

        // Which actions make sense now (the laptop refuses anything else, whatever a button showed).
        public static List<string> Ops(BatchRun r, QueueData d)
        {
            var o = new List<string>();
            if (r == null || Finished(r.State)) return o;
            var s = Cur(r);
            var t = s != null && s.TaskId != "" ? d.Find(s.TaskId) : null;
            if (r.State == Running) o.Add(r.PauseAfterStep ? "unpause" : "pause");
            if (r.State == Paused)
                switch (r.PauseKind)
                {
                    case "user": case "review": o.Add("resume"); break;
                    case "unknown": o.Add("accept"); o.Add("retry"); o.Add("skip"); break;
                    case "commit": if (s != null && s.CommitState == "select") o.Add("commit"); o.Add("retry"); o.Add("skip"); break;
                    default: o.Add("retry"); o.Add("skip"); break;   // start, prompt
                }
            if (t != null && t.State == QState.Running) o.Add("stop");
            o.Add("cancel");
            return o;
        }

        public static string OpLabel(string op, BatchRun r)
        {
            var s = Cur(r);
            bool commit = r != null && r.PauseKind == "commit";
            switch (op)
            {
                case "pause": return "Pause after current step";
                case "unpause": return "Don't pause after this step";
                case "resume": return r != null && r.PauseKind == "review" ? "Continue" : "Resume";
                case "accept": return "Accept result and continue";
                case "retry": return commit ? "Retry commit" : r != null && r.PauseKind == "start" ? "Try again" : "Retry step";
                case "skip": return commit ? "Skip commit and continue" : "Skip step…";
                case "commit": return "Commit selected files";
                case "stop": return "Stop current prompt…";
                case "cancel": return "Cancel remaining steps…";
                default: return op;
            }
        }

        // What Skip / Cancel / Stop mean, said before you confirm them.
        public static string Confirm(string op, BatchRun r)
        {
            var s = Cur(r);
            int n = r != null ? r.Current + 1 : 0;
            switch (op)
            {
                case "skip":
                    if (r != null && r.PauseKind == "commit") return null;
                    return "Skip step " + n + (s != null ? " (\"" + s.Title + "\")" : "") + " and its commit?\n\nLater steps may depend on what this step was meant to do. Changes it already made to files are kept (not undone).";
                case "cancel": return "Cancel the rest of \"" + (r != null ? r.Name : "") + "\"?\n\nNo further prompts or commits start. A prompt that is running now keeps running (use Stop current prompt to end it). Nothing is rolled back: file changes and commits already made stay.";
                case "stop": return "Stop Claude in the current step now?\n\nThe batch pauses so you can retry, skip or cancel. Changes Claude already made to files are NOT undone.";
                default: return null;
            }
        }

        // The status of one step, as a short label.
        public static string StepLabel(BatchRunStep s, QueueTask t)
        {
            switch (s.State)
            {
                case "pending": return "Waiting";
                case "prompt":
                    if (t == null) return "Task missing";
                    if (t.State == QState.Completed) return s.Outcome == "" ? "Result unclear" : "Completed";
                    return QLook.Label(QLook.Kind(t));
                case "commit": return CommitLabel(s);
                case "done": return (s.Outcome == "accepted" ? "Done (accepted)" : "Done") + (s.Commit ? " · " + CommitLabel(s) : "");
                case "skipped": return "Skipped";
                default: return "Canceled";
            }
        }

        public static string CommitLabel(BatchRunStep s)
        {
            if (!s.Commit) return "no commit";
            switch (s.CommitState)
            {
                case "waiting": return "commit next";
                case "running": return "committing…";
                case "done": return "committed " + (s.CommitHash.Length > 10 ? s.CommitHash.Substring(0, 10) : s.CommitHash);
                case "none": return "no changes to commit";
                case "select": return "commit: pick files";
                case "failed": return "commit failed";
                case "uncertain": return "commit unclear";
                case "skipped": return "commit skipped";
                default: return "commit after";
            }
        }

        public static string StateText(BatchRun r)
        {
            if (r.State == Scheduled) return "Scheduled";
            if (r.State == Completed || r.State == Canceled) return r.State;
            if (r.State == Paused) return r.PauseKind == "review" ? "Waiting for your review" : r.PauseKind == "user" ? "Paused" : "Needs you";
            return "Running";
        }

        public static string Progress(BatchRun r)
        {
            int n = r.Steps.Count, i = Math.Min(r.Current + 1, n);
            return r.State == Scheduled ? n + " step" + (n == 1 ? "" : "s") : r.State == Completed ? "All " + n + " steps done" : "Step " + i + " of " + n;
        }

        // The commit's scope: only files that changed during this step, in its project folder, with nothing staged before
        // it. Anything unclear (a file that already had edits, a file outside the project, something staged before) is
        // never committed by itself: you pick the files instead. picked: your explicit selection ("path\thash"), or null.
        public static CommitPlan PlanCommit(GitView v, BatchRunStep s, string projectRel, List<string> picked)
        {
            var p = new CommitPlan();
            Func<string, CommitPlan> stop = why => { p.Kind = "blocked"; p.Note = why; return p; };
            if (v.Error != "") return stop("Git couldn't read the repository: " + v.Error);
            if (v.Branch != s.Branch) return stop("The branch changed from \"" + s.Branch + "\" to \"" + v.Branch + "\" since this step started, so nothing was committed. Switch back to " + s.Branch + " and Retry commit, or Skip commit.");
            if (v.Oid != s.BaseHead)
                return stop("HEAD moved since this step started (" + Short(s.BaseHead) + " → " + Short(v.Oid) + "): something else committed in this repository meanwhile, so the batch can't tell which changes are this step's. Nothing was committed. Commit by hand in the Commit view if you like, then Skip commit.");
            if (v.CommitBlockers.Count > 0) return stop(v.CommitBlockers[0] + " Nothing was committed.");
            var before = new Dictionary<string, string[]>();
            foreach (var e in s.Baseline) { var f = e.Split('\t'); if (f.Length >= 3) before[f[0]] = f; }
            Func<string, bool> inProject = path => projectRel == "" || path.StartsWith(projectRel + "/", StringComparison.OrdinalIgnoreCase);
            var mine = new List<GitFile>();
            foreach (var f in v.Files)
            {
                string why = null;
                string[] b;
                if (before.TryGetValue(f.Path, out b))
                {
                    if (b[1] != f.Hash) why = "already had changes before this step, and changed again";
                    else if (b[2] == "1") why = "was already staged before this step";
                    else continue;   // untouched since before the step: not this step's, never included
                }
                else if (f.Sub) why = "is a submodule";
                else if (!inProject(f.Path) || (f.Orig != "" && !inProject(f.Orig))) why = "is outside the step's project folder";
                if (why == null) mine.Add(f);
                p.Choices.Add(f.Path + "\t" + f.Hash + "\t" + f.Kind + "\t" + (why ?? "changed during this step"));
            }
            List<GitFile> selected;
            if (picked != null && picked.Count > 0)
            {
                selected = new List<GitFile>();
                foreach (var e in picked)
                {
                    int tab = e.IndexOf('\t');
                    string path = tab > 0 ? e.Substring(0, tab) : e, hash = tab > 0 ? e.Substring(tab + 1) : "";
                    var f = v.Files.Find(x => x.Path == path);
                    if (f == null || f.Hash != hash) { p.Kind = "select"; p.Note = "\"" + path + "\" changed since you picked it. Pick the files again."; return p; }
                    if (!selected.Contains(f)) selected.Add(f);
                }
            }
            else
            {
                int unclear = p.Choices.Count - mine.Count;
                if (unclear > 0)
                {
                    p.Kind = "select";
                    var names = new List<string>();
                    foreach (var c in p.Choices) { var f = c.Split('\t'); if (f[3] != "changed during this step" && names.Count < 5) names.Add(f[0] + " (" + f[3] + ")"); }
                    p.Note = unclear + " changed file" + (unclear == 1 ? "" : "s") + " can't be attributed to this step with confidence: " + string.Join("; ", names) + (unclear > 5 ? "; …" : "") +
                        ". Nothing was committed. Pick exactly which files to commit, or Skip commit.";
                    return p;
                }
                if (mine.Count == 0) { p.Kind = "none"; p.Note = "No changes to commit: this step didn't change any files in " + Name(v.Top) + "."; return p; }
                selected = mine;
            }
            string problem = Git.SelectionProblem(v, selected, s.CommitMessage);
            if (problem != null) { p.Kind = "select"; p.Note = problem + " Nothing was committed."; return p; }
            p.Kind = "run"; p.Files = selected;
            return p;
        }

        static string Short(string sha) { return string.IsNullOrEmpty(sha) ? "(no commits)" : sha.Length > 10 ? sha.Substring(0, 10) : sha; }

        // Path of a folder relative to its repository root, Git style ("" = the root itself).
        public static string RelTo(string top, string folder)
        {
            string a = Sched.NormPath(top), b = Sched.NormPath(folder);
            if (b.Length <= a.Length) return "";
            return b.Substring(a.Length).TrimStart('\\', '/').Replace('\\', '/');
        }

        // ---------- handoff between projects ----------
        // The prompt a fresh step gets: the previous step's final response (its end, at most HandoffExcerpt characters,
        // said so when shortened), where the complete handoff is kept, then your prompt exactly as written.
        public static string HandoffPrompt(BatchRun r, int i, string prevResult, string file)
        {
            var p = r.Steps[i - 1];
            var t = (prevResult ?? "").Replace("\r\n", "\n").Trim();
            bool cut = t.Length > HandoffExcerpt;
            if (cut) t = t.Substring(t.Length - HandoffExcerpt);
            var sb = new StringBuilder();
            sb.Append("This is step ").Append(i + 1).Append(" of ").Append(r.Steps.Count).Append(" of the batch \"").Append(r.Name).Append("\". It starts a fresh conversation; the handoff from the previous step follows.\n\n");
            sb.Append("--- Handoff from step ").Append(i).Append(" \"").Append(p.Title).Append("\" (project ").Append(Name(p.Project)).Append(", folder ").Append(p.Project).Append(p.Outcome == "skipped" ? "; that step was SKIPPED, so it may be incomplete" : "").Append(") ---\n");
            sb.Append(t == "" ? "(Step " + i + " left no final response.)" : (cut ? "The END of step " + i + "'s final response (the last " + HandoffExcerpt.ToString("N0", CultureInfo.InvariantCulture) + " of " + (prevResult ?? "").Length.ToString("N0", CultureInfo.InvariantCulture) + " characters; read the full handoff file for the rest):\n" : "Step " + i + "'s final response (complete):\n") + t).Append("\n");
            sb.Append("Full handoff (complete final response, that step's prompt and its run log): ").Append(file).Append("\n");
            sb.Append("--- End of handoff ---\n\nYour task for this step:\n\n").Append(r.Steps[i].Prompt);
            return sb.ToString();
        }

        public static string HandoffFile(BatchRun r, int i, string prevResult, string prevTask, string prevLog, string prevLogText)
        {
            var p = r.Steps[i - 1];
            var sb = new StringBuilder();
            sb.Append("# Handoff: step ").Append(i).Append(" → step ").Append(i + 1).Append(" of batch \"").Append(r.Name).Append("\"\n\n");
            sb.Append("| | |\n|---|---|\n");
            sb.Append("| From step | ").Append(i).Append(" \"").Append(p.Title.Replace("|", "\\|")).Append("\" (").Append(p.Outcome == "" ? "?" : p.Outcome).Append(") |\n");
            sb.Append("| Project | ").Append(p.Project).Append(" |\n");
            sb.Append("| Conversation | `").Append(p.SessionId).Append("` |\n");
            if (prevLog != "") sb.Append("| Run log | ").Append(prevLog).Append(" |\n");
            sb.Append("| Queue task | `").Append(prevTask).Append("` |\n\n");
            sb.Append("## Step ").Append(i).Append("'s prompt\n\n").Append(p.Prompt.Trim()).Append("\n\n");
            sb.Append("## Step ").Append(i).Append("'s final response (complete)\n\n").Append((prevResult ?? "").Trim() == "" ? "(none)" : prevResult.Trim()).Append("\n");
            if (!string.IsNullOrEmpty(prevLogText)) sb.Append("\n## Step ").Append(i).Append("'s run log\n\n").Append(prevLogText.Trim()).Append("\n");
            return Sched.Redact(sb.ToString());
        }

        // Why a step's prompt stopped, from its task: the kind first (failed / needs input / usage / interrupted).
        public static string Blocker(QueueTask t, int n)
        {
            string kind;
            switch (t.Outcome)
            {
                case "blocked": case "denied": kind = "needs input or permission"; break;
                case "interrupted": kind = "was interrupted, so its outcome is unknown"; break;
                case "usage_limit": kind = "is blocked by a usage limit"; break;
                case "canceled": kind = "was stopped"; break;
                case "auth": case "billing": case "api_key": kind = "can't run (sign-in or billing)"; break;
                default: kind = "failed"; break;
            }
            return "Step " + n + " " + kind + ": " + t.Reason;
        }
    }

    // ---------- the batch runner (part of the queue engine: same locked store, same tick, same task runners) ----------
    public partial class QueueEngine
    {
        public Func<string, string> TopOf = Git.TopOf;   // a folder's Git repository root (tests may replace it)
        public event Action<BatchRun> BatchNotify;
        public bool HasActiveBatch { get { return Batching.ActiveRun(Data) != null; } }
        readonly object commitGate = new object();
        readonly Dictionary<string, CommitOutcome> commitDone = new Dictionary<string, CommitOutcome>();
        readonly HashSet<string> commitThreads = new HashSet<string>();

        static string CommitKey(BatchRun r, BatchRunStep s) { return r.Id + "|" + s.Id + "|" + s.CommitAttempts; }

        // ----- definitions -----
        public string ValidateBatch(BatchDef b) { return Batching.Validate(b, TopOf); }

        // Saves a definition (revision-checked). A run that hasn't started yet follows the new version; a start time
        // ("at") schedules a run; started runs never change.
        public string SaveBatch(BatchDef b, int expectedRevision)
        {
            if (b.Name.Trim() == "") return "Give the batch a name.";
            if (b.Start == "at") { if (b.RunAtUtc == DateTime.MinValue) return "Choose the start time."; string v = ValidateBatch(b); if (v != null) return v; }
            string err = null;
            Mutate(d =>
            {
                var cur = d.FindBatch(b.Id);
                if (cur != null && cur.Revision != expectedRevision) { err = "The batch changed while you were editing it. Close the editor and try again."; return false; }
                b.Revision = expectedRevision + 1; b.UpdatedUtc = Now();
                if (cur == null) { b.CreatedUtc = Now(); d.Batches.Add(b); } else { b.CreatedUtc = cur.CreatedUtc; d.Batches[d.Batches.IndexOf(cur)] = b; }
                d.BatchRuns.RemoveAll(r => r.BatchId == b.Id && r.State == Batching.Scheduled && !r.RunNow);
                if (b.Start == "at") d.BatchRuns.Add(Batching.Freeze(b, Guid.NewGuid().ToString("N"), Now(), b.RunAtUtc));
                return true;
            });
            if (err == null) Tick();
            return err;
        }

        public string DeleteBatch(string id)
        {
            string err = null;
            Mutate(d =>
            {
                var b = d.FindBatch(id);
                if (b == null) return false;
                if (d.BatchRuns.Exists(r => r.BatchId == id && Batching.Active(r.State))) { err = "This batch has a run that isn't finished. Cancel it first."; return false; }
                d.BatchRuns.RemoveAll(r => r.BatchId == id && r.State == Batching.Scheduled);
                d.Batches.Remove(b);
                return true;
            });
            return err;
        }

        // Run now. runId: the caller's one-time id, so a resent request finds its run instead of starting a second one.
        // One run per batch at a time (a scheduled one counts: change the batch's start time instead).
        public string StartBatch(string batchId, string runId, out string startedId)
        {
            startedId = null;
            if (string.IsNullOrEmpty(runId)) runId = null;
            var b0 = Data.FindBatch(batchId);
            if (b0 == null) return "That batch no longer exists.";
            if (runId != null && Data.FindRun(runId) != null) { startedId = runId; return null; }
            string err = ValidateBatch(b0);
            if (err != null) return err;
            string id = runId ?? Guid.NewGuid().ToString("N"), made = null;
            Mutate(d =>
            {
                if (d.FindRun(id) != null) { made = id; return false; }
                var b = d.FindBatch(batchId);
                if (b == null || b.Revision != b0.Revision) { err = "The batch changed meanwhile. Check it and start it again."; return false; }
                var open = d.BatchRuns.Find(r => r.BatchId == b.Id && !Batching.Finished(r.State));
                if (open != null) { err = "This batch already has a run that isn't finished (" + Batching.StateText(open) + (open.State == Batching.Scheduled ? " for " + Sched.LocalText(open.StartAtUtc) : "") + "). Wait for it, or cancel it."; return false; }
                var r2 = Batching.Freeze(b, id, Now(), Now());
                r2.RunNow = true;
                Batching.Note(r2, Now(), "Run now");
                d.BatchRuns.Add(r2);
                made = id;
                return true;
            });
            startedId = made;
            if (err == null) Tick();
            return err;
        }

        // Why manual commits in this repository wait (the Commit view): a batch is running a step there.
        public string BatchBusyReason(string top)
        {
            foreach (var r in Data.BatchRuns)
            {
                var s = Batching.Cur(r);
                if (r.State != Batching.Running || s == null) continue;
                if (Git.Overlaps(s.Repo != "" ? s.Repo : s.Project, top))
                    return "The batch \"" + r.Name + "\" is running in this repository (step " + (r.Current + 1) + " of " + r.Steps.Count + "). Use Pause after current step on it, then commit by hand while it's paused.";
            }
            return "";
        }

        // ----- actions on a run (desktop and phone). seq: the run's version you acted on (-1 = don't check). null = done.
        public string BatchAction(string runId, string op, int seq, List<string> picked)
        {
            string err = null, retryTask = null, cancelTask = null, stopTask = null;
            Mutate(d =>
            {
                var r = d.FindRun(runId);
                if (r == null) { err = "That batch run no longer exists."; return false; }
                if (seq >= 0 && r.Seq != seq) { err = "The batch changed since you looked (now: " + Batching.StateText(r) + ", " + Batching.Progress(r) + "). Check it, then try again."; return false; }
                if (!Batching.Ops(r, d).Contains(op)) { err = "That isn't possible now (" + Batching.StateText(r) + ")."; return false; }
                var s = Batching.Cur(r);
                var t = s != null && s.TaskId != "" ? d.Find(s.TaskId) : null;
                DateTime now = Now();
                string kind = r.PauseKind, label = Batching.OpLabel(op, r).TrimEnd('…');
                Action resume = () => { r.State = Batching.Running; r.PauseKind = ""; r.Reason = ""; };
                switch (op)
                {
                    case "pause":
                        if (s == null || s.State == "pending") { r.State = Batching.Paused; r.PauseKind = "user"; r.Reason = "Paused before step " + (r.Current + 1) + " started, as you asked."; }
                        else { r.PauseAfterStep = true; r.Reason = "Pauses after step " + (r.Current + 1) + " (its prompt and its commit) finishes."; }
                        break;
                    case "unpause": r.PauseAfterStep = false; r.Reason = ""; break;
                    case "resume": if (kind == "review") s.Reviewed = true; resume(); break;
                    case "accept":
                        s.Outcome = "accepted"; s.EndedUtc = now;
                        if (t != null) { s.SessionId = t.SessionId; s.Summary = t.Summary; s.RunLog = t.Last != null ? t.Last.RunLog : ""; s.Attempts = t.Attempts.Count; }
                        s.State = s.Commit ? "commit" : "done"; if (s.Commit) s.CommitState = "waiting";
                        resume(); break;
                    case "retry":
                        if (kind == "commit") { s.CommitState = "waiting"; s.Picked.Clear(); s.Choices.Clear(); }
                        else if (kind != "start" && t != null) retryTask = t.Id;
                        resume(); break;
                    case "skip":
                        if (kind == "commit") { s.CommitState = "skipped"; s.CommitNote = "Skipped by you."; s.State = "done"; }
                        else
                        {
                            s.State = "skipped"; s.Outcome = "skipped"; s.EndedUtc = now;
                            if (t != null) { s.SessionId = t.SessionId; s.Summary = t.Summary; if (!QState.Finished(t.State) && t.State != QState.Running) cancelTask = t.Id; }
                        }
                        resume(); break;
                    case "commit":
                        if (picked == null || picked.Count == 0) { err = "Pick at least one file, or Skip commit."; return false; }
                        s.Picked = new List<string>(picked); s.CommitState = "waiting"; resume(); break;
                    case "stop": stopTask = t.Id; break;
                    case "cancel":
                        r.State = Batching.Canceled; r.FinishedUtc = now; r.PauseKind = ""; r.PauseAfterStep = false;
                        r.Reason = "Canceled: no further prompts or commits start." + (t != null && t.State == QState.Running ? " The prompt that was running keeps running (Stop current prompt ends it)." : "") + " Nothing was rolled back.";
                        foreach (var x in r.Steps) if (x.State == "pending") x.State = "canceled";
                        if (t != null && (QState.Pending(t.State) || t.State == QState.NeedsAttention)) cancelTask = t.Id;   // nothing left for you to do there
                        break;
                }
                r.Seq++;
                Batching.Note(r, now, label + (s != null ? " (step " + (r.Current + 1) + ")" : ""));
                return true;
            });
            if (err != null) return err;
            if (retryTask != null) Retry(retryTask, true);   // continues the step's conversation when it has one; never the next step
            if (cancelTask != null) CancelPending(cancelTask);
            if (stopTask != null)
            {
                Cancel(stopTask);
                var t = Data.Find(stopTask);
                if (t != null && t.State == QState.Running) return "Claude couldn't be confirmed stopped: the step's task still shows Running. Check its VS Code terminal.";
            }
            Tick();
            return null;
        }

        // ----- the runner, called from Tick -----
        void BatchTick()
        {
            if (Data.BatchRuns.Count == 0) return;
            ApplyCommitResults();
            DateTime now = Now();
            var act = Batching.ActiveRun(Data);
            foreach (var r0 in Data.BatchRuns)
            {
                if (r0.State != Batching.Scheduled || r0.StartAtUtc > now) continue;
                string id = r0.Id, why = null;
                if (act != null) why = "Waiting: the batch \"" + act.Name + "\" is " + (act.State == Batching.Paused ? "paused; resume or cancel it first" : "running") + ". Batches run one at a time.";
                else if (!r0.RunNow && (now - r0.StartAtUtc).TotalMinutes > 2 && now < graceUntil) why = "Overdue (was due " + Sched.LocalText(r0.StartAtUtc) + "). Starts " + Sched.LocalText(graceUntil) + " unless you cancel it.";
                if (why != null) { if (r0.Reason != why) Mutate(d => { var r = d.FindRun(id); if (r == null || r.Reason == why) return false; r.Reason = why; return true; }); continue; }
                Mutate(d =>
                {
                    var r = d.FindRun(id);
                    if (r == null || r.State != Batching.Scheduled || Batching.ActiveRun(d) != null) return false;
                    r.State = Batching.Running; r.StartedUtc = Now(); r.Reason = ""; r.Seq++;
                    Batching.Note(r, Now(), "Started");
                    return true;
                });
                act = Batching.ActiveRun(Data);
                break;
            }
            if (act != null) Advance(act.Id);
        }

        // Moves the active run forward as far as it can right now: each transition is its own locked write, made only
        // if the run is still exactly as it was read (its Seq), so nothing is ever done twice.
        void Advance(string id)
        {
            for (int guard = 0; guard < 4 * Batching.MaxSteps; guard++)
            {
                var r = Data.FindRun(id);
                if (r == null) return;
                var s = Batching.Cur(r);
                if (r.State == Batching.Paused)
                {
                    // You retried or completed the step's task in the Queue yourself: the batch follows it again.
                    var pt = s != null && (r.PauseKind == "prompt" || r.PauseKind == "unknown") && s.TaskId != "" ? Data.Find(s.TaskId) : null;
                    if (pt == null || !(QState.Pending(pt.State) || pt.State == QState.Running || (pt.State == QState.Completed && pt.Outcome == "marked_completed"))) return;
                    if (!Change(r, x => { x.State = Batching.Running; x.PauseKind = ""; x.Reason = ""; Batching.Note(x, Now(), "Step " + (x.Current + 1) + " continued from the Queue"); })) return;
                    continue;
                }
                if (r.State != Batching.Running) return;
                if (s == null) { if (Change(r, x => { x.State = Batching.Completed; x.FinishedUtc = Now(); x.Reason = "All " + x.Steps.Count + " steps finished."; Batching.Note(x, Now(), "Completed"); })) RaiseBatch(id); return; }
                bool moved;
                switch (s.State)
                {
                    case "pending": StartStep(r); return;
                    case "prompt": moved = CheckPrompt(r); break;
                    case "commit": moved = CheckCommit(r); break;
                    default: moved = NextStep(r); break;   // done, skipped
                }
                if (!moved) return;
            }
        }

        // One locked change of a run, only if nobody changed it since it was read. True = written.
        bool Change(BatchRun seen, Action<BatchRun> f)
        {
            bool ok = false;
            Mutate(d =>
            {
                var r = d.FindRun(seen.Id);
                if (r == null || r.Seq != seen.Seq || r.State != seen.State) return false;
                f(r); r.Seq++; ok = true;
                return true;
            });
            return ok;
        }

        void PauseRun(BatchRun r, string kind, string reason)
        {
            if (Change(r, x => { x.State = Batching.Paused; x.PauseKind = kind; x.Reason = reason; Batching.Note(x, Now(), "Paused: " + reason); })) RaiseBatch(r.Id);
        }

        void RaiseBatch(string id)
        {
            var h = BatchNotify; var r = Data.FindRun(id);
            if (h != null && r != null) try { h(r); } catch { }
        }

        // Shows why the active step waits, without a write when nothing changed.
        void SetReason(BatchRun r, string why)
        {
            if (r.Reason == why) return;
            Mutate(d => { var x = d.FindRun(r.Id); if (x == null || x.Seq != r.Seq || x.Reason == why) return false; x.Reason = why; return true; });
        }

        void StartStep(BatchRun r)
        {
            int i = r.Current;
            var s = r.Steps[i];
            var p = i > 0 ? r.Steps[i - 1] : null;
            string why = null;
            if (!Directory.Exists(s.Project)) why = "Step " + (i + 1) + "'s project folder doesn't exist any more: " + s.Project;
            else if (s.Conversation == "continue" && p != null)
            {
                if (!Sched.IsGuid(p.SessionId)) why = "Step " + (i + 1) + " continues step " + i + "'s conversation, but that step has no conversation to continue (it was skipped before it started). Skip this step too, or cancel the batch.";
                else if (!Sched.SamePath(p.Project, s.Project)) why = "Step " + (i + 1) + " continues step " + i + "'s conversation in another project. That isn't possible; skip it or cancel.";
            }
            if (why != null) { PauseRun(r, "start", why); return; }
            var t = TaskEditor.NewTask();
            t.Title = r.Name + " · " + (i + 1) + "/" + r.Steps.Count + " " + s.Title;
            t.Project = s.Project; t.Prompt = s.Prompt;
            t.Conversation = s.Conversation == "continue" ? "resume" : "new";
            t.ResumeSession = s.Conversation != "continue" ? "" : p != null ? p.SessionId : s.ResumeSession;   // checked by the queue: this folder's, not open elsewhere
            t.Runner = r.Runner; t.PermissionMode = r.PermissionMode; t.Model = r.Model; t.AllowCommands = new List<string>(r.AllowCommands);
            t.AllowDelete = r.AllowDelete; t.AllowCommit = false; t.AllowPush = false;   // commits are the batch's own Git step, never Claude's
            t.Unattended = true;
            t.BatchRun = r.Id; t.BatchStep = s.Id;
            t.State = QState.Scheduled; t.Order = -1; t.Revision = 1; t.CreatedUtc = Now(); t.UpdatedUtc = Now();
            string handoff = "";
            if (s.Conversation == "new" && s.Handoff && p != null)
            {
                try
                {
                    var pt = p.TaskId != "" ? Data.Find(p.TaskId) : null;
                    string result = pt != null ? FullResult(pt) : "", log = pt != null ? RunLogPath(pt.Last) : "", logText = "";
                    try { if (log != "" && File.Exists(log)) logText = File.ReadAllText(log, Encoding.UTF8); } catch { }
                    // In the run's log folder, which an unattended run may read (--add-dir).
                    string dir = Path.Combine(RunsDir, t.Id, "logs");
                    Directory.CreateDirectory(dir);
                    handoff = Path.Combine(dir, "handoff-from-step-" + i + ".md");
                    File.WriteAllText(handoff, Batching.HandoffFile(r, i, result, p.TaskId, log, logText), new UTF8Encoding(false));
                    t.Prompt = Batching.HandoffPrompt(r, i, Sched.Redact(result), handoff);
                }
                catch (Exception e) { PauseRun(r, "start", "Couldn't write the handoff for step " + (i + 1) + ": " + e.Message); return; }
            }
            bool ok = false;
            Mutate(d =>
            {
                var x = d.FindRun(r.Id);
                if (x == null || x.Seq != r.Seq || x.State != Batching.Running || x.Current != i || x.Steps[i].State != "pending") return false;
                d.Tasks.Add(t);
                var xs = x.Steps[i];
                xs.State = "prompt"; xs.TaskId = t.Id; xs.StartedUtc = Now(); xs.HandoffFile = handoff;
                x.Reason = ""; x.Seq++;
                Batching.Note(x, Now(), "Step " + (i + 1) + " queued (" + (t.Conversation == "resume" ? "continues conversation " + t.ResumeSession.Substring(0, 8) : handoff != "" ? "fresh conversation with a handoff" : "fresh conversation") + ")");
                ok = true;
                return true;
            });
            if (!ok) try { Directory.Delete(Path.Combine(RunsDir, t.Id), true); } catch { }
        }

        // The step's prompt is finished only when its task completed AND Claude stated it completed. Anything else pauses.
        bool CheckPrompt(BatchRun r)
        {
            var s = r.Steps[r.Current];
            var t = Data.Find(s.TaskId);
            int n = r.Current + 1;
            if (t == null) { PauseRun(r, "start", "Step " + n + "'s task was removed from the queue. Try again starts it again, or skip it."); return false; }
            if (t.State == QState.Completed)
            {
                string word = t.Outcome == "marked_completed" ? "completed" : Sched.ResultWord(FullResult(t));
                if (word != "completed")
                {
                    PauseRun(r, "unknown", "Claude ended step " + n + "'s turn without confirming it finished (no \"" + Sched.ResultTag + " completed\" line), so the batch can't tell whether it succeeded. Read its result, then Accept result and continue, Retry step, Skip or Cancel.");
                    return false;
                }
                return Change(r, x =>
                {
                    var xs = x.Steps[x.Current];
                    xs.Outcome = "completed"; xs.SessionId = t.SessionId; xs.Summary = t.Summary; xs.RunLog = t.Last != null ? t.Last.RunLog : ""; xs.Attempts = t.Attempts.Count; xs.EndedUtc = Now();
                    xs.State = xs.Commit ? "commit" : "done"; if (xs.Commit) xs.CommitState = "waiting";
                    Batching.Note(x, Now(), "Step " + n + " completed");
                });
            }
            if (t.State == QState.NeedsAttention || t.State == QState.Failed || t.State == QState.Canceled) { PauseRun(r, "prompt", Batching.Blocker(t, n)); return false; }
            SetReason(r, "");
            return false;
        }

        bool NextStep(BatchRun r)
        {
            var s = r.Steps[r.Current];
            int n = r.Current + 1;
            if (s.State == "done" && s.Review && !s.Reviewed)
            {
                PauseRun(r, "review", "Review step " + n + " (\"" + s.Title + "\")" + (s.Commit ? ": " + Batching.CommitLabel(s) : "") + ". Continue starts " + (n < r.Steps.Count ? "step " + (n + 1) : "nothing more: it was the last step") + ".");
                return false;
            }
            if (r.PauseAfterStep && n < r.Steps.Count) { if (Change(r, x => { x.PauseAfterStep = false; x.State = Batching.Paused; x.PauseKind = "user"; x.Reason = "Paused after step " + n + ", as you asked. Resume starts step " + (n + 1) + "."; Batching.Note(x, Now(), "Paused after step " + n); })) RaiseBatch(r.Id); return false; }
            return Change(r, x => { x.Current++; x.PauseAfterStep = false; x.Reason = ""; });
        }

        bool CheckCommit(BatchRun r)
        {
            var s = r.Steps[r.Current];
            switch (s.CommitState)
            {
                case "done": case "none": case "skipped": return Change(r, x => { x.Steps[x.Current].State = "done"; });
                case "waiting": StartCommit(r); return false;
                case "running": return Reconcile(r);
                default: return false;   // select / failed / uncertain: paused for you
            }
        }

        // The commit runs on its own thread (Git hooks may take a while); its claim is written first, so a restart
        // meanwhile finds "running" with no thread and reconciles with Git instead of committing again.
        void StartCommit(BatchRun r)
        {
            var s = r.Steps[r.Current];
            string top = s.Repo != "" ? s.Repo : TopOf(s.Project);
            if (top == null) { PauseRun(r, "commit", "Step " + (r.Current + 1) + "'s project isn't in a Git repository (any more), so it can't commit. Skip commit to continue."); return; }
            if (!s.BaselineTaken) { PauseRun(r, "commit", "The batch didn't record the repository's state before step " + (r.Current + 1) + " started, so it can't tell which changes are this step's. Commit by hand in the Commit view if you like, then Skip commit."); return; }
            foreach (var x in Data.Tasks)
                if (x.State == QState.Running && Git.Overlaps(x.Project, top)) { SetReason(r, "Waiting to commit: \"" + x.Title + "\" is running in this repository."); return; }
            var act = Active();
            if (act != null)
                foreach (var a in act)
                    if (a.Status == "busy" && Git.Overlaps(a.Cwd, top)) { SetReason(r, "Waiting to commit: Claude is busy in this repository in another session (process " + a.Pid + ")."); return; }
            if (!Git.TryHold(top)) { SetReason(r, "Waiting to commit: another commit or push is running in this repository."); return; }
            BatchRunStep snap = null; string key = null;
            bool ok = Change(r, x =>
            {
                var xs = x.Steps[x.Current];
                xs.CommitState = "running"; xs.CommitAttempts++; xs.Repo = top; xs.CommitStartedUtc = Now();
                xs.CommitOwner = instance; xs.CommitOwnerPid = myPid; xs.CommitOwnerStartedUtc = myStart;
                x.Reason = "Committing step " + (x.Current + 1) + "'s changes in " + Batching.Name(top) + "…";
                Batching.Note(x, Now(), "Commit attempt " + xs.CommitAttempts + " for step " + (x.Current + 1));
                snap = QueueStore.Ser().Deserialize<BatchRunStep>(QueueStore.Ser().Serialize(xs));
                key = CommitKey(x, xs);
            });
            if (!ok) { Git.Release(top); return; }
            lock (commitGate) commitThreads.Add(key);
            new Thread(() =>
            {
                CommitOutcome o;
                try { o = DoCommit(top, snap); }
                catch (Exception e) { o = new CommitOutcome { State = "failed", Note = "Something went wrong on the laptop: " + e.Message }; }
                finally { Git.Release(top); }
                lock (commitGate) { commitDone[key] = o; commitThreads.Remove(key); }
                Raise();   // the UI thread's next tick applies it
            }) { IsBackground = true, Name = "ClaudeStatusBatchCommit" }.Start();
        }

        // Validates the repository again, decides the scope, then runs exactly: git add -- <files>, git commit (message on stdin).
        CommitOutcome DoCommit(string top, BatchRunStep s)
        {
            var o = new CommitOutcome();
            var v = Git.Inspect(top);
            o.Branch = v.Branch;
            var plan = Batching.PlanCommit(v, s, Batching.RelTo(top, s.Project), s.Picked);
            o.Choices = plan.Choices; o.Note = plan.Note;
            if (plan.Kind == "none") { o.State = "none"; return o; }
            if (plan.Kind == "select") { o.State = "select"; return o; }
            if (plan.Kind != "run") { o.State = "failed"; return o; }
            var cmds = Git.Commands(v, plan.Files);
            cmds.RemoveAt(cmds.Count - 1);   // the push: batches never push
            var log = new StringBuilder();
            foreach (var c in cmds)
            {
                var args = new string[c.Length - 1];
                Array.Copy(c, 1, args, 0, args.Length);
                bool isCommit = args[0] == "commit";
                log.Append("$ git ").Append(string.Join(" ", Array.ConvertAll(args, Git.Quote))).Append("\n");
                var res = Git.Run(top, args, isCommit ? new UTF8Encoding(false).GetBytes(s.CommitMessage) : null, Git.CommitTimeoutMs, null);
                log.Append(res.Out);
                o.Out = Cap(log.ToString());
                if (res.Code == 0) continue;
                var after = Recheck(top, s);
                if (after.State == "done") { after.Out = o.Out; after.Note = "Committed " + Sha10(after.Hash) + " (Git reported a problem afterwards; see its output)."; after.Committed = Paths(plan.Files); return after; }
                o.State = "failed";
                o.Note = (res.TimedOut || Git.AuthProblem(res.Out) ? "Git needs something on the laptop (sign-in, signing key or passphrase)" + (res.TimedOut ? " and was stopped after " + (Git.CommitTimeoutMs / 1000) + " s" : "") + "."
                    : isCommit ? "The commit failed (a hook may have rejected it; see Git's output)." : "Staging failed (see Git's output).") +
                    " No commit was created. The step's changes are kept; files this commit staged stay staged.";
                return o;
            }
            var done = Recheck(top, s);
            done.Out = o.Out; done.Committed = Paths(plan.Files);
            if (done.State == "done") done.Note = "Committed " + Sha10(done.Hash) + " on " + v.Branch + ": " + plan.Files.Count + " file" + (plan.Files.Count == 1 ? "" : "s") + ".";
            return done;
        }

        static List<string> Paths(List<GitFile> files) { var l = new List<string>(); foreach (var f in files) l.Add(f.Path); return l; }
        static string Sha10(string sha) { return sha.Length > 10 ? sha.Substring(0, 10) : sha; }
        static string Cap(string s) { s = Git.Redact(s); return s.Length > Batching.MaxOut ? s.Substring(0, Batching.MaxOut) + "\n…(shortened)" : s; }
        static string GitLine(string top, params string[] args) { var r = Git.Run(top, args, null, Git.ReadTimeoutMs, null); return r.Code == 0 ? r.Out.Trim() : null; }

        // What Git history says about this step's commit: made (HEAD's parent is the recorded HEAD and its message is
        // the intended one), not made (HEAD unchanged), or something else happened (never committed again by itself).
        CommitOutcome Recheck(string top, BatchRunStep s)
        {
            string head = GitLine(top, "rev-parse", "-q", "--verify", "HEAD") ?? "";
            if (head == s.BaseHead) return new CommitOutcome { State = "failed", Note = "No commit was made (HEAD is unchanged), so Retry commit is safe." };
            string parent = GitLine(top, "rev-parse", "-q", "--verify", head + "^") ?? "";
            var msg = Git.Run(top, new[] { "log", "-1", "--format=%B", head }, null, Git.ReadTimeoutMs, null);
            bool same = msg.Code == 0 && msg.Out.Replace("\r", "").TrimEnd('\n') == s.CommitMessage.Replace("\r", "").TrimEnd('\n');
            if (parent == s.BaseHead && same) return new CommitOutcome { State = "done", Hash = head };
            return new CommitOutcome { State = "uncertain", Note = "HEAD moved to " + Sha10(head) + ", which isn't the commit this step intended (" + (parent != s.BaseHead ? "its parent isn't the recorded " + (s.BaseHead == "" ? "start" : Sha10(s.BaseHead)) : "its message differs") + "). The batch won't commit again by itself. Check git log, then Skip commit (or Retry commit to re-check)." };
        }

        // A commit marked running that no thread here is doing: Claude Status stopped while committing (or another
        // instance did). Reconciled with Git history, never replayed blindly.
        bool Reconcile(BatchRun r)
        {
            var s = r.Steps[r.Current];
            string key = CommitKey(r, s);
            lock (commitGate) if (commitThreads.Contains(key) || commitDone.ContainsKey(key)) return false;
            if (s.CommitOwner != instance && WorkerWindows.ProcessAlive(s.CommitOwnerPid, s.CommitOwnerStartedUtc) == 1) return false;   // another instance is committing
            if (!Git.TryHold(s.Repo)) return false;
            CommitOutcome o;
            try { o = Recheck(s.Repo, s); }
            finally { Git.Release(s.Repo); }
            if (o.State == "done") o.Note = "Committed " + Sha10(o.Hash) + " (found in Git history after Claude Status restarted mid-commit).";
            else if (o.State == "failed") o.Note = "Claude Status stopped while committing, and no commit was made (HEAD is unchanged). Retry commit is safe.";
            lock (commitGate) commitDone[key] = o;
            ApplyCommitResults();
            return Data.FindRun(r.Id) != null && Data.FindRun(r.Id).Seq != r.Seq;
        }

        void ApplyCommitResults()
        {
            List<KeyValuePair<string, CommitOutcome>> done;
            lock (commitGate) { done = new List<KeyValuePair<string, CommitOutcome>>(commitDone); commitDone.Clear(); }
            foreach (var kv in done)
            {
                var parts = kv.Key.Split('|');
                var o = kv.Value;
                string pauseWhy = null; string runId = parts[0];
                Mutate(d =>
                {
                    var r = d.FindRun(runId);
                    var s = r == null ? null : r.Steps.Find(x => x.Id == parts[1]);
                    if (s == null || s.CommitState != "running" || s.CommitAttempts.ToString(CultureInfo.InvariantCulture) != parts[2]) return false;
                    s.CommitState = o.State; s.CommitNote = o.Note; s.CommitOut = o.Out; s.Choices = o.Choices; s.CommitOwner = "";
                    if (o.State == "done") { s.CommitHash = o.Hash; s.Committed = o.Committed; }
                    Batching.Note(r, Now(), "Commit for step " + (r.Steps.IndexOf(s) + 1) + ": " + Batching.CommitLabel(s) + (o.State == "done" || o.State == "none" ? "" : " (" + o.Note + ")"));
                    r.Seq++;
                    if (o.State == "done" || o.State == "none") { s.State = "done"; if (r.State == Batching.Running) r.Reason = ""; }
                    else if (r.State == Batching.Running && Batching.Cur(r) == s)
                    {
                        r.State = Batching.Paused; r.PauseKind = "commit";
                        r.Reason = "Step " + (r.Current + 1) + "'s commit " + (o.State == "select" ? "needs you to pick the files" : o.State == "uncertain" ? "has an unclear result" : "failed") + ": " + o.Note;
                        pauseWhy = r.Reason;
                    }
                    return true;
                });
                if (pauseWhy != null) RaiseBatch(runId);
            }
        }

        // Before a step's task starts Claude: what the repository looks like, so its commit can include only this step's changes.
        void BatchBaseline(QueueTask t)
        {
            var r = Data.FindRun(t.BatchRun);
            var s = r == null ? null : r.Steps.Find(x => x.Id == t.BatchStep);
            if (s == null || !s.Commit || s.BaselineTaken) return;
            string top = TopOf(s.Project);
            GitView v = top != null ? Git.Inspect(top) : null;
            Mutate(d =>
            {
                var x = d.FindRun(t.BatchRun);
                var xs = x == null ? null : x.Steps.Find(y => y.Id == t.BatchStep);
                if (xs == null || xs.BaselineTaken) return false;
                if (v == null || v.Error != "") { xs.CommitNote = top == null ? "Not in a Git repository." : "Git couldn't read the repository before the step: " + v.Error; return true; }
                xs.BaselineTaken = true; xs.Repo = top; xs.Branch = v.Branch; xs.BaseHead = v.Oid;
                xs.Baseline = new List<string>();
                foreach (var f in v.Files) xs.Baseline.Add(f.Path + "\t" + f.Hash + "\t" + (f.Staged ? "1" : "0"));
                return true;
            });
        }

        // Claude's complete final message for a task's last attempt (the task keeps a shortened copy).
        public string FullResult(QueueTask t)
        {
            var a = t.Last;
            if (a == null) return t.ResultText;
            try
            {
                if (a.InVsCode) { var act = ActivityOf(t, a); return act.LastReply != "" ? act.LastReply : t.ResultText; }
                var rep = RunParser.ParseFile(LogPath(t.Id, a.N));
                return rep.HasResult ? rep.ResultText : t.ResultText;
            }
            catch { return t.ResultText; }
        }
    }

    // ---------- desktop: the Batches tab of the Queue window ----------
    public class BatchView : UserControl
    {
        readonly QueueEngine eng;
        readonly float scale;
        readonly ListBox list, steps;
        readonly Wrap title, status, reason, facts, note;
        readonly Panel attentionBox;
        readonly FlowLayoutPanel actions;
        readonly TextBox output;
        readonly CheckedListBox pick;
        string selected = "", shownKey = "", shownActions = "";   // rebuilt only when they change (a tick every 2 s mustn't eat a click or focus)
        int stepSel = -1;
        bool historyOpen;
        BatchEditor editor;

        class Row { public string Text = "", Run = "", Def = ""; public override string ToString() { return Text; } }

        int S(float v) { return (int)Math.Round(v * scale); }

        public BatchView(QueueEngine engine, float scale)
        {
            eng = engine; this.scale = scale;
            Font = SystemFonts.MessageBoxFont; BackColor = QTheme.Bg; ForeColor = QTheme.Text;
            var top = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, WrapContents = false, Padding = new Padding(S(10), S(8), S(10), S(4)) };
            top.Controls.Add(QTheme.Primary(QTheme.Btn("+  New batch", (s, e) => Edit(null))));
            top.Controls.Add(new Label { AutoSize = true, ForeColor = QTheme.Muted, Margin = new Padding(S(8), S(6), 0, 0), Text = "An ordered series of prompts: each step starts only after the one before it (and its commit) succeeded." });
            var split = new SplitContainer { Dock = DockStyle.Fill, FixedPanel = FixedPanel.Panel1, SplitterWidth = S(5), BackColor = QTheme.Line };
            split.Panel1.BackColor = QTheme.Bg; split.Panel2.BackColor = QTheme.Bg;
            list = new ListBox { Dock = DockStyle.Fill, BorderStyle = BorderStyle.None, IntegralHeight = false, ItemHeight = S(22), AccessibleName = "Batches" };
            list.SelectedIndexChanged += (s, e) =>
            {
                var r = list.SelectedItem as Row;
                if (r == null || refreshing) return;
                if (r.Run == "" && r.Def == "") { if (r.Text.StartsWith("HISTORY")) { historyOpen = !historyOpen; RefreshView(); } return; }
                string k = r.Run != "" ? "r:" + r.Run : "d:" + r.Def;
                if (k != selected) { selected = k; stepSel = -1; shownKey = ""; shownActions = ""; note.Text = ""; }
                ShowDetail();
            };
            list.DoubleClick += (s, e) => { var r = list.SelectedItem as Row; if (r != null && r.Def != "") Edit(eng.Data.FindBatch(r.Def)); };
            split.Panel1.Controls.Add(list);

            var detail = new Panel { Dock = DockStyle.Fill, Padding = new Padding(S(14), S(10), S(14), S(10)) };
            output = new TextBox { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, WordWrap = true, Font = new Font("Consolas", Font.Size), AccessibleName = "Step details" };
            pick = new CheckedListBox { Dock = DockStyle.Top, Height = S(110), CheckOnClick = true, Visible = false, AccessibleName = "Files to commit" };
            steps = new ListBox { Dock = DockStyle.Top, Height = S(130), IntegralHeight = false, AccessibleName = "Steps" };
            steps.SelectedIndexChanged += (s, e) => { if (refreshing) return; stepSel = steps.SelectedIndex; ShowDetail(); };
            facts = new Wrap { ForeColor = QTheme.Muted, Padding = new Padding(0, S(6), 0, S(2)) };
            note = new Wrap { ForeColor = QTheme.Yellow };
            actions = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, WrapContents = true, Padding = new Padding(0, S(6), 0, 0) };
            attentionBox = new Panel { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(S(10), S(6), S(10), S(6)), BackColor = QTheme.AttentionBg };
            reason = new Wrap { ForeColor = QTheme.Text };
            attentionBox.Controls.Add(reason);
            status = new Wrap { Font = new Font(Font, FontStyle.Bold) };
            title = new Wrap { Font = new Font("Segoe UI", 16f * scale, FontStyle.Bold, GraphicsUnit.Pixel) };
            // Docked controls lay out from the last added: Fill first, then the top ones from the bottom up.
            foreach (Control c in new Control[] { output, pick, steps, facts, note, actions, attentionBox, status, title }) detail.Controls.Add(c);
            split.Panel2.Controls.Add(detail);
            Controls.Add(split); Controls.Add(top);
            QTheme.Style(this);
            // The list keeps a fixed width once there's room (the tab may be sized while hidden, so on every resize).
            EventHandler fit = (s, e) => { if (split.Width > S(560) && split.SplitterDistance < S(240)) { split.Panel1MinSize = S(180); split.SplitterDistance = Math.Min(S(320), split.Width / 2); } };
            Layout += (s, e) => fit(s, e); split.SizeChanged += fit; VisibleChanged += fit;
            eng.Changed += OnChanged;
            Disposed += (s, e) => { eng.Changed -= OnChanged; if (editor != null && !editor.IsDisposed) editor.Close(); };
            RefreshView();
        }

        bool pending, refreshing;
        void OnChanged()
        {
            if (!IsHandleCreated || pending) return;
            pending = true;
            BeginInvoke((MethodInvoker)(() => { pending = false; if (!IsDisposed && Visible) RefreshView(); }));
        }

        public void SelectRun(string id) { selected = "r:" + id; stepSel = -1; shownKey = ""; shownActions = ""; historyOpen = true; RefreshView(); }
        public string[] RowTexts { get { var l = new List<string>(); foreach (var x in list.Items) l.Add(x.ToString()); return l.ToArray(); } }
        public string DetailText { get { return title.Text + "\n" + status.Text + "\n" + reason.Text + "\n" + facts.Text + "\n" + output.Text; } }
        public string[] StepTexts { get { var l = new List<string>(); foreach (var x in steps.Items) l.Add(x.ToString()); return l.ToArray(); } }
        public string[] ActionTexts { get { var l = new List<string>(); foreach (Control c in actions.Controls) l.Add(c.Text); return l.ToArray(); } }
        public string NoteText { get { return note.Text; } }

        public void RefreshView()
        {
            var d = eng.Data;
            var rows = new List<Row>();
            var active = d.BatchRuns.FindAll(r => !Batching.Finished(r.State));
            var done = d.BatchRuns.FindAll(r => Batching.Finished(r.State));
            done.Sort((a, b) => b.FinishedUtc.CompareTo(a.FinishedUtc));
            Action<string> head = t => rows.Add(new Row { Text = t });
            Func<BatchRun, Row> runRow = r =>
            {
                var s = Batching.Cur(r);
                string icon = r.State == Batching.Paused ? "!" : r.State == Batching.Running ? "▶" : r.State == Batching.Completed ? "✓" : r.State == Batching.Scheduled ? "◷" : "–";
                return new Row { Run = r.Id, Text = "  " + icon + "  " + r.Name + "  ·  " + Batching.StateText(r) + "  ·  " + Batching.Progress(r) +
                    (s != null && Batching.Active(r.State) ? "  ·  " + Batching.Name(s.Project) : r.State == Batching.Scheduled ? "  ·  " + Sched.LocalText(r.StartAtUtc) : "") };
            };
            if (active.Count > 0) { head("RUNS  ·  " + active.Count); foreach (var r in active) rows.Add(runRow(r)); }
            head("BATCHES  ·  " + d.Batches.Count);
            foreach (var b in d.Batches) rows.Add(new Row { Def = b.Id, Text = "  ☰  " + b.Name + "  ·  " + b.Steps.Count + " step" + (b.Steps.Count == 1 ? "" : "s") + (b.Start == "at" ? "  ·  starts " + Sched.LocalText(b.RunAtUtc) : "") });
            if (done.Count > 0) { head("HISTORY  ·  " + done.Count + (historyOpen ? "   ▾" : "   ▸ show")); if (historyOpen) foreach (var r in done) rows.Add(runRow(r)); }
            refreshing = true;
            list.BeginUpdate();
            list.Items.Clear();
            int sel = -1;
            foreach (var r in rows) { list.Items.Add(r); if ((r.Run != "" && "r:" + r.Run == selected) || (r.Def != "" && "d:" + r.Def == selected)) sel = list.Items.Count - 1; }
            if (sel < 0) for (int i = 0; i < rows.Count && sel < 0; i++) if (rows[i].Run != "" || rows[i].Def != "") { sel = i; selected = rows[i].Run != "" ? "r:" + rows[i].Run : "d:" + rows[i].Def; shownKey = ""; }
            if (sel < 0) selected = "";
            if (sel >= 0) list.SelectedIndex = sel;
            list.EndUpdate();
            refreshing = false;
            ShowDetail();
        }

        Button Add(string text, EventHandler click, bool primary)
        {
            var b = QTheme.Btn(text, click);
            if (primary) QTheme.Primary(b);
            actions.Controls.Add(b);
            return b;
        }

        void ShowDetail()
        {
            var d = eng.Data;
            var run = selected.StartsWith("r:") ? d.FindRun(selected.Substring(2)) : null;
            var def = selected.StartsWith("d:") ? d.FindBatch(selected.Substring(2)) : null;
            pick.Visible = false;
            if (def != null) { ShowDef(def, d); return; }
            if (run == null)
            {
                actions.Controls.Clear(); shownActions = "";
                title.Text = "Batches"; status.Text = ""; attentionBox.Visible = false; steps.Items.Clear(); output.Text = "";
                facts.Text = "Click + New batch to put prompts in order: audit, fix, commit, document… Each step can continue the previous conversation or start fresh with a handoff, pause for your review, and commit its changes with Git.";
                return;
            }
            var cur = Batching.Cur(run);
            title.Text = run.Name;
            status.Text = Batching.StateText(run) + "  ·  " + Batching.Progress(run) + (cur != null && Batching.Active(run.State) ? "  ·  " + Batching.Name(cur.Project) + "  ·  Claude Code" : "") + (run.PauseAfterStep ? "  ·  pauses after this step" : "");
            reason.Text = run.Reason; attentionBox.Visible = run.Reason != "";
            attentionBox.BackColor = run.State == Batching.Paused && run.PauseKind != "user" && run.PauseKind != "review" ? QTheme.ErrorBg : QTheme.AttentionBg;
            var ops = Batching.Ops(run, d);
            var t = cur != null && cur.TaskId != "" ? d.Find(cur.TaskId) : null;
            string ak = "r:" + run.Id + "|" + run.Seq + "|" + string.Join(",", ops.ToArray()) + "|" + (t != null ? t.Id : "");
            if (ak != shownActions)
            {
                actions.Controls.Clear();
                bool first = true;
                foreach (var op in ops)
                {
                    string o = op;
                    Add(Batching.OpLabel(op, run), (s, e) => Act(run, o), first && op != "cancel" && op != "stop");
                    first = false;
                }
                if (t != null) Add("Show step in Queue", (s, e) => { var q = FindQueueView(); if (q != null) { q.ShowTab(0); q.SelectTask(t.Id); } }, false);
                shownActions = ak;
            }
            facts.Text = (run.State == Batching.Scheduled ? "Starts " + QLook.When(run.StartAtUtc, DateTime.UtcNow) : "Started " + (run.StartedUtc != DateTime.MinValue ? Sched.LocalText(run.StartedUtc) : "—")) +
                (run.FinishedUtc != DateTime.MinValue ? "  ·  finished " + Sched.LocalText(run.FinishedUtc) : "") +
                "  ·  " + (run.Runner == "headless" ? "in the background" : "in VS Code") + ", unattended  ·  a frozen copy of the batch (revision " + run.DefRevision + "): editing the batch doesn't change this run.";
            string key = run.Id + "|" + run.Seq + "|" + run.Reason;
            if (key != shownKey)
            {
                refreshing = true;
                steps.BeginUpdate(); steps.Items.Clear();
                for (int i = 0; i < run.Steps.Count; i++)
                {
                    var s = run.Steps[i];
                    var st = s.TaskId != "" ? d.Find(s.TaskId) : null;
                    string mark = i == run.Current && Batching.Active(run.State) ? "▶" : s.State == "done" ? "✓" : s.State == "skipped" ? "↷" : s.State == "canceled" ? "–" : "·";
                    steps.Items.Add(mark + " " + (i + 1) + ". " + s.Title + "  ·  " + Batching.Name(s.Project) + "  ·  " + (s.Conversation == "continue" ? "continues" : s.Handoff ? "fresh + handoff" : "fresh") +
                        "  ·  " + Batching.StepLabel(s, st) + (s.Review ? "  ·  review" : "") + (s.Commit && s.State != "done" && s.State != "commit" ? "  ·  " + Batching.CommitLabel(s) : ""));
                }
                if (stepSel < 0 || stepSel >= run.Steps.Count) stepSel = Math.Min(run.Current, run.Steps.Count - 1);
                if (stepSel >= 0) steps.SelectedIndex = stepSel;
                steps.EndUpdate();
                refreshing = false;
                shownKey = key;
            }
            var sel = stepSel >= 0 && stepSel < run.Steps.Count ? run.Steps[stepSel] : null;
            string text = sel == null ? "" : StepText(run, sel, d);
            if (output.Text != text) output.Text = text;
            if (sel != null && sel == cur && run.PauseKind == "commit" && sel.CommitState == "select")
            {
                string sig = string.Join("|", sel.Choices.ToArray());
                if (pick.Tag as string != sig)
                {
                    pick.Items.Clear();
                    foreach (var c in sel.Choices) { var f = c.Split('\t'); pick.Items.Add(f[0] + "   (" + f[2] + "; " + f[3] + ")", f[3] == "changed during this step"); }
                    pick.Tag = sig;
                }
                pick.Visible = true;
            }
        }

        QueueView FindQueueView() { for (Control c = Parent; c != null; c = c.Parent) if (c is QueueView) return (QueueView)c; return null; }

        string StepText(BatchRun run, BatchRunStep s, QueueData d)
        {
            var t = s.TaskId != "" ? d.Find(s.TaskId) : null;
            var sb = new StringBuilder();
            Action<string, string> line = (k, v) => { if (!string.IsNullOrEmpty(v)) sb.Append(k).Append(": ").Append(v).Append("\r\n"); };
            line("Step", (run.Steps.IndexOf(s) + 1) + " of " + run.Steps.Count + " · " + s.Title);
            line("Status", Batching.StepLabel(s, t) + (t != null && s.State == "prompt" && t.Reason != "" ? " — " + t.Reason : ""));
            line("Project", s.Project);
            line("Conversation", s.Conversation == "continue" ? "continues " + (s.ResumeSession != "" ? "conversation " + s.ResumeSession : "the previous step's conversation") : "fresh" + (s.Handoff ? ", with a handoff from the previous step" : ""));
            line("Session ID", s.SessionId != "" ? s.SessionId : t != null ? t.SessionId : "");
            line("Handoff file", s.HandoffFile);
            line("Attempts", t != null ? t.Attempts.Count.ToString() : "");
            line("Started", s.StartedUtc != DateTime.MinValue ? Sched.LocalText(s.StartedUtc) : "");
            line("Ended", s.EndedUtc != DateTime.MinValue ? Sched.LocalText(s.EndedUtc) : "");
            line("Run log", t != null ? eng.RunLogPath(t.Last) : "");
            line("Review before continuing", s.Review ? "yes" : "");
            if (s.Commit)
            {
                line("Commit", Batching.CommitLabel(s));
                line("Commit message", s.CommitMessage.Replace("\r\n", "\n").Replace("\n", " ⏎ "));
                line("Repository", s.Repo != "" ? s.Repo + (s.Branch != "" ? " (branch " + s.Branch + ")" : "") : "");
                line("Commit note", s.CommitNote);
                if (s.Committed.Count > 0) line("Committed files", string.Join(", ", s.Committed.ToArray()));
            }
            sb.Append("\r\n— Prompt (as you wrote it) —\r\n").Append(s.Prompt.Replace("\r\n", "\n").Replace("\n", "\r\n")).Append("\r\n");
            string result = t != null && t.ResultText != "" ? t.ResultText : s.Summary;
            if (result != "") sb.Append("\r\n— Result —\r\n").Append(result.Replace("\r\n", "\n").Replace("\n", "\r\n")).Append("\r\n");
            if (s.CommitOut != "") sb.Append("\r\n— Git —\r\n").Append(s.CommitOut.Replace("\n", "\r\n"));
            if (run.History.Count > 0) { sb.Append("\r\n— Batch history —\r\n"); foreach (var h in run.History) sb.Append(h).Append("\r\n"); }
            return sb.ToString();
        }

        void ShowDef(BatchDef b, QueueData d)
        {
            var open = d.BatchRuns.Find(r => r.BatchId == b.Id && !Batching.Finished(r.State));
            string dk = "d:" + b.Id + "|" + b.Revision + "|" + (open != null ? open.Id + open.State : "");
            if (dk == shownActions) return;   // unchanged (validation reads the disk and Git: not every tick)
            shownActions = dk;
            actions.Controls.Clear();
            title.Text = b.Name;
            string v = eng.ValidateBatch(b);
            status.Text = b.Steps.Count + " step" + (b.Steps.Count == 1 ? "" : "s") + "  ·  Claude Code  ·  " + (b.Runner == "headless" ? "in the background" : "in VS Code") + "  ·  " + (b.Start == "at" ? "starts " + QLook.When(b.RunAtUtc, DateTime.UtcNow) : "starts when you click Run now");
            reason.Text = v != null ? "Not ready to run: " + v : open != null ? "A run of this batch isn't finished (" + Batching.StateText(open) + ")." : "";
            attentionBox.Visible = reason.Text != "";
            attentionBox.BackColor = QTheme.AttentionBg;
            var runNow = Add("Run now", (s, e) => { string id; string err = eng.StartBatch(b.Id, null, out id); if (err != null) note.Text = err; else SelectRun(id); }, true);
            runNow.Enabled = v == null && open == null;
            Add("Edit", (s, e) => Edit(b), false);
            Add("Delete…", (s, e) =>
            {
                if (MessageBox.Show(this, "Delete the batch \"" + b.Name + "\"? Its finished runs stay in History.", "Batches", MessageBoxButtons.OKCancel) != DialogResult.OK) return;
                string err = eng.DeleteBatch(b.Id);
                if (err != null) note.Text = err; else { selected = ""; RefreshView(); }
            }, false);
            facts.Text = "Each step starts only after the previous step's prompt and (if enabled) its commit succeeded. Commits use your own Git on this laptop, with your message, only for the files that step changed; never a push.";
            refreshing = true;
            steps.Items.Clear();
            for (int i = 0; i < b.Steps.Count; i++)
            {
                var s = b.Steps[i];
                steps.Items.Add((i + 1) + ". " + (s.Title != "" ? s.Title : Sched.FirstLine(s.Prompt, 60)) + "  ·  " + Batching.Name(s.Project) + "  ·  " + (s.Conversation == "continue" ? "continues" : s.Handoff ? "fresh + handoff" : "fresh") + (s.Review ? "  ·  review" : "") + (s.Commit ? "  ·  commit: " + Sched.FirstLine(s.CommitMessage, 50) : ""));
            }
            refreshing = false;
            output.Text = "";
            shownKey = "";
        }

        void Act(BatchRun run, string op)
        {
            string ask = Batching.Confirm(op, run);
            if (ask != null && MessageBox.Show(this, ask, "Batch", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning) != DialogResult.OK) return;
            List<string> picked = null;
            if (op == "commit")
            {
                var s = Batching.Cur(run);
                picked = new List<string>();
                for (int i = 0; i < pick.Items.Count && i < s.Choices.Count; i++) if (pick.GetItemChecked(i)) { var f = s.Choices[i].Split('\t'); picked.Add(f[0] + "\t" + f[1]); }
            }
            string err = eng.BatchAction(run.Id, op, run.Seq, picked);
            note.Text = err ?? (op == "stop" ? "Claude was stopped." : "");
            shownKey = ""; shownActions = "";
            RefreshView();
        }

        void Edit(BatchDef b)
        {
            if (editor != null && !editor.IsDisposed) { editor.Activate(); return; }
            editor = new BatchEditor(eng, b, scale);
            editor.FormClosed += (s, e) => { if (editor.SavedId != null) { selected = "d:" + editor.SavedId; shownActions = ""; RefreshView(); } };
            editor.Show(FindForm());
        }
    }

    // Batch settings in a compact header (permissions folded below it), then the steps: a list on the left and the
    // selected step's fields on the right, with the prompt taking the spare height. Save / Cancel stay in a fixed footer.
    public class BatchEditor : Form
    {
        readonly QueueEngine eng;
        readonly BatchDef orig;
        readonly List<BatchStep> work = new List<BatchStep>();
        readonly TextBox name, model, commands, sTitle, sPrompt, sMessage;
        readonly ComboBox runner, mode, sProject, sConv, sSession;
        readonly CheckBox allowDelete, sHandoff, sReview, sCommit;
        readonly RadioButton startManual, startAt;
        readonly QWhen at;
        readonly ListBox stepList;
        readonly Wrap error, convNote, commitInfo;
        readonly TableLayoutPanel stepPanel, perms;
        readonly Panel stepHost;
        readonly Button permsToggle, removeBtn, upBtn, downBtn;
        readonly Label permsSummary;
        readonly ToolTip tips = new ToolTip();
        readonly Dictionary<string, string> tops = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        List<SessionMeta> sessions = new List<SessionMeta>();
        int cur = -1, promptRow;
        bool loading, fitPending;
        public string SavedId;
        static readonly string[] Modes = { "acceptEdits", "dontAsk", "plan" };
        readonly float scale;
        int S(float v) { return (int)Math.Round(v * scale); }

        public BatchEditor(QueueEngine engine, BatchDef b, float scale)
        {
            eng = engine;
            using (var g = CreateGraphics()) this.scale = Math.Max(scale, g.DpiX / 96f);
            var ser = QueueStore.Ser();
            orig = b != null ? ser.Deserialize<BatchDef>(ser.Serialize(b)) : new BatchDef { Id = Guid.NewGuid().ToString("N"), AllowCommands = new List<string>(Sched.DefaultCommands) };
            Batching.Normalize(new QueueData { Batches = new List<BatchDef> { orig } });
            foreach (var s in orig.Steps) work.Add(ser.Deserialize<BatchStep>(ser.Serialize(s)));
            Text = b == null ? "New batch" : "Edit batch";
            Font = QTheme.UiFont(this, this.scale); StartPosition = FormStartPosition.CenterScreen; ShowInTaskbar = true; MinimizeBox = false; KeyPreview = true;
            BackColor = QTheme.Bg; ForeColor = QTheme.Text;
            Rectangle wa = Screen.FromPoint(Cursor.Position).WorkingArea;
            MinimumSize = new Size(Math.Min(S(700), wa.Width), Math.Min(S(500), wa.Height));
            Size = new Size(Math.Min(S(980), wa.Width), Math.Min(S(780), wa.Height));
            int pad = S(14);
            Func<string, Label> lab = t => new Label { Text = t, AutoSize = true, ForeColor = QTheme.Muted, Anchor = AnchorStyles.Left, Margin = new Padding(0, S(3), S(10), S(3)) };
            Func<Control, int, Control> field = (c, right) => { c.Dock = DockStyle.Fill; c.Margin = new Padding(0, S(3), right, S(3)); return c; };

            // ---- the batch ----
            var top = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 4, Padding = new Padding(pad, S(10), pad, S(6)) };
            top.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize)); top.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 62));
            top.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize)); top.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 38));
            name = new TextBox { Text = orig.Name, AccessibleName = "Batch name" };
            model = new TextBox { Text = orig.Model, AccessibleName = "Model" };
            QTheme.Cue(model, "Claude Code default");
            tips.SetToolTip(model, "Blank = your Claude Code default. For example sonnet, opus, or a full model name.");
            top.Controls.Add(lab("Name"), 0, 0); top.Controls.Add(field(name, S(20)), 1, 0);
            top.Controls.Add(lab("Model"), 2, 0); top.Controls.Add(field(model, 0), 3, 0);
            runner = new QCombo { DropDownStyle = ComboBoxStyle.DropDownList, AccessibleName = "Where steps run" };
            runner.Items.Add("VS Code: a terminal in each step's project window"); runner.Items.Add("Background: no window (claude -p)");
            runner.SelectedIndex = orig.Runner == "headless" ? 1 : 0;
            top.Controls.Add(lab("Runs in"), 0, 1); top.Controls.Add(field(runner, S(20)), 1, 1);
            var agent = new Label { Text = "Claude Code", AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, S(3), 0, S(3)), AccessibleDescription = "One agent per batch: the scheduler runs Claude Code only." };
            tips.SetToolTip(agent, agent.AccessibleDescription);
            top.Controls.Add(lab("Agent"), 2, 1); top.Controls.Add(agent, 3, 1);
            startManual = new QRadio { Text = "When I click Run now", Checked = orig.Start != "at", Margin = new Padding(0, S(3), S(12), S(3)), Anchor = AnchorStyles.Left };
            startAt = new QRadio { Text = "At", Checked = orig.Start == "at", Margin = new Padding(0, S(3), S(4), S(3)), Anchor = AnchorStyles.Left };
            at = new QWhen(orig.RunAtUtc != DateTime.MinValue ? orig.RunAtUtc.ToLocalTime() : DateTime.Now.Date.AddDays(1).AddHours(2), S(150), S(100), "Start") { Margin = new Padding(0, S(3), 0, S(3)) };
            at.ValueChanged += (s, e) => startAt.Checked = true;
            var when = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0) };
            foreach (Control c in new Control[] { startManual, startAt, at }) when.Controls.Add(c);
            top.Controls.Add(lab("Start"), 0, 2); top.Controls.Add(when, 1, 2); top.SetColumnSpan(when, 3);

            // permissions: folded to a one-line summary
            permsToggle = QTheme.Btn("▸  Permissions", (s, e) => ShowPerms(!perms.Visible));
            permsToggle.AccessibleName = "Permissions"; permsToggle.Margin = new Padding(0, S(4), S(10), S(2));
            permsSummary = new Label { AutoSize = true, ForeColor = QTheme.Muted, Anchor = AnchorStyles.Left, Margin = new Padding(0, S(4), 0, S(2)) };
            var permsRow = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0) };
            permsRow.Controls.Add(permsToggle); permsRow.Controls.Add(permsSummary);
            top.Controls.Add(permsRow, 0, 3); top.SetColumnSpan(permsRow, 4);
            perms = new TableLayoutPanel { AutoSize = true, ColumnCount = 2, Dock = DockStyle.Fill, Visible = false, Margin = new Padding(S(18), S(2), 0, S(2)) };
            perms.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize)); perms.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            Action<string, Control> prow = (label, c) =>
            {
                int r = perms.RowCount++;
                if (label != null) { var l = lab(label); if (c is TextBox && ((TextBox)c).Multiline) { l.Anchor = AnchorStyles.Left | AnchorStyles.Top; l.Margin = new Padding(0, S(6), S(10), 0); } perms.Controls.Add(l, 0, r); }
                if (!(c is CheckBox)) c.Dock = DockStyle.Fill;
                c.Margin = new Padding(0, S(2), 0, S(2));
                perms.Controls.Add(c, 1, r);
            };
            mode = new QCombo { DropDownStyle = ComboBoxStyle.DropDownList, AccessibleName = "Claude may" };
            foreach (var m in Modes) mode.Items.Add(Sched.ModeLabel(m));
            mode.SelectedIndex = Math.Max(0, Array.IndexOf(Modes, orig.PermissionMode));
            prow("Claude may", mode);
            commands = new TextBox { Multiline = true, AcceptsReturn = true, ScrollBars = ScrollBars.Vertical, Height = S(78), Text = string.Join("\r\n", orig.AllowCommands), AccessibleName = "Allowed commands" };
            commands.Font = QTheme.Mono(Font);
            prow("Allowed commands", commands);
            prow(null, new Wrap { ForeColor = QTheme.Muted, Text = "One pattern per line (\"npm test *\"), allowed in both Bash and PowerShell; or a full Claude Code rule such as WebFetch(domain:docs.python.org). Read-only commands are always allowed." });
            allowDelete = new QCheck { Text = "Allow deleting files", Checked = orig.AllowDelete };
            prow("Also authorize", allowDelete);
            prow(null, new Wrap { ForeColor = QTheme.Muted, Text = "Every step runs unattended: Claude decides routine questions itself and logs each decision in its run's Markdown log. Claude is never allowed to commit or push; commits between steps are the batch's own Git step." });
            top.Controls.Add(perms, 0, 4); top.SetColumnSpan(perms, 4);
            mode.SelectedIndexChanged += (s, e) => Summarize(); commands.TextChanged += (s, e) => Summarize(); allowDelete.CheckedChanged += (s, e) => Summarize();

            // ---- the steps ----
            var head = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, WrapContents = false, Padding = new Padding(pad, S(9), pad, S(5)) };
            head.Controls.Add(new Label { Text = "Steps", AutoSize = true, Font = new Font(Font, FontStyle.Bold), Margin = new Padding(0, 0, S(10), 0) });
            head.Controls.Add(new Label { Text = "Each starts only after the previous one, and its commit, succeeded.", AutoSize = true, ForeColor = QTheme.Muted, Margin = new Padding(0) });
            head.Paint += (s, e) => { using (var p = new Pen(QTheme.Line)) e.Graphics.DrawLine(p, pad, 0, head.Width - pad, 0); };

            var split = new SplitContainer { Dock = DockStyle.Fill, FixedPanel = FixedPanel.Panel1, TabStop = false, SplitterWidth = S(11) };
            split.Paint += (s, e) => { var r = split.SplitterRectangle; using (var p = new Pen(QTheme.Line)) e.Graphics.DrawLine(p, r.X + r.Width / 2, r.Y, r.X + r.Width / 2, r.Bottom); };
            var area = new Panel { Dock = DockStyle.Fill, Padding = new Padding(pad, 0, pad, S(6)) };
            area.Controls.Add(split);
            Load += (s, e) => { try { split.Panel1MinSize = S(200); split.Panel2MinSize = S(340); split.SplitterDistance = S(260); } catch (InvalidOperationException) { } };

            var tools = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, WrapContents = false, Padding = new Padding(0, 0, 0, S(2)), TabIndex = 0 };
            Func<string, string, EventHandler, Button> tool = (t, what, click) => { var x = QTheme.Btn(t, click); x.AccessibleName = what; x.Margin = new Padding(0, 0, S(4), S(4)); tips.SetToolTip(x, what); return x; };
            tools.Controls.Add(tool("+ Add", "Add a step after the last one", (s, e) => { Store(); var p = cur >= 0 && cur < work.Count ? work[cur] : null; work.Add(new BatchStep { Id = Guid.NewGuid().ToString("N"), Project = p != null ? p.Project : "", Conversation = p != null ? "continue" : "new" }); cur = work.Count - 1; Fill(); sPrompt.Focus(); }));
            tools.Controls.Add(removeBtn = tool("Remove", "Remove the selected step", (s, e) => { if (cur < 0 || cur >= work.Count) return; work.RemoveAt(cur); cur = Math.Min(cur, work.Count - 1); Fill(); }));
            tools.Controls.Add(upBtn = tool("↑", "Move the step up (Alt+Up)", (s, e) => MoveStep(-1)));
            tools.Controls.Add(downBtn = tool("↓", "Move the step down (Alt+Down)", (s, e) => MoveStep(1)));
            var listFrame = new Panel { Dock = DockStyle.Fill, Padding = new Padding(1), BackColor = QTheme.Line, TabIndex = 1 };
            stepList = new ListBox { Dock = DockStyle.Fill, BorderStyle = BorderStyle.None, IntegralHeight = false, DrawMode = DrawMode.OwnerDrawFixed, ItemHeight = Math.Min(255, Font.Height * 2 + S(12)), AccessibleName = "Steps" };
            stepList.SelectedIndexChanged += (s, e) => { if (loading) return; Store(); cur = stepList.SelectedIndex; ShowStep(); UpdateTools(); };
            stepList.DrawItem += (s, e) => DrawStep(e);
            stepList.Resize += (s, e) => stepList.Invalidate();
            stepList.Enter += (s, e) => { listFrame.BackColor = QTheme.Focus; stepList.Invalidate(); };
            stepList.Leave += (s, e) => { listFrame.BackColor = QTheme.Line; stepList.Invalidate(); };
            stepList.KeyDown += (s, e) => { if (e.Alt && (e.KeyCode == Keys.Up || e.KeyCode == Keys.Down)) { MoveStep(e.KeyCode == Keys.Up ? -1 : 1); e.Handled = true; } };
            listFrame.Controls.Add(stepList);
            split.Panel1.Controls.Add(listFrame); split.Panel1.Controls.Add(tools);

            // the selected step
            stepHost = new Panel { Dock = DockStyle.Fill, AutoScroll = true, Padding = new Padding(S(4), 0, S(4), 0) };
            stepPanel = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2 };
            stepPanel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize)); stepPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            Func<string, Control, int, int> row = (label, c, gap) =>
            {
                // Explicit cells: a hidden row (step 1's conversation picker) must not shift the rows after it.
                int r = stepPanel.RowCount++;
                stepPanel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
                if (label != null)
                {
                    var l = lab(label); l.Margin = new Padding(0, S(3) + gap, S(10), S(3));
                    if (c is TextBox && ((TextBox)c).Multiline) { l.Anchor = AnchorStyles.Left | AnchorStyles.Top; l.Margin = new Padding(0, S(6) + gap, S(10), 0); }
                    stepPanel.Controls.Add(l, 0, r);
                }
                if (!(c is CheckBox) && !(c is FlowLayoutPanel)) c.Dock = DockStyle.Fill;
                c.Margin = new Padding(0, S(3) + gap, 0, S(3));
                stepPanel.Controls.Add(c, 1, r);
                return r;
            };
            sTitle = new TextBox { AccessibleName = "Step title" };
            row("Title", sTitle, 0);
            var projRow = new TableLayoutPanel { ColumnCount = 2, AutoSize = true };
            projRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); projRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            sProject = new QCombo { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDown, AccessibleName = "Step project folder", Margin = new Padding(0) };
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var x in eng.Data.Tasks) if (x.Project != "" && seen.Add(Sched.NormPath(x.Project))) sProject.Items.Add(x.Project);
            foreach (var x in eng.Data.Batches) foreach (var y in x.Steps) if (y.Project != "" && seen.Add(Sched.NormPath(y.Project))) sProject.Items.Add(y.Project);
            sProject.TextChanged += (s, e) => { if (!loading) { Store(); Explain(); } };
            var browse = QTheme.Btn("Browse…", (s, e) => { using (var fb = new FolderBrowserDialog { Description = "Project folder Claude works in", SelectedPath = Directory.Exists(sProject.Text) ? sProject.Text : "" }) if (fb.ShowDialog(this) == DialogResult.OK) sProject.Text = fb.SelectedPath; });
            browse.Margin = new Padding(S(6), 0, 0, 0); browse.AccessibleName = "Browse for the step's project folder";
            projRow.Controls.Add(sProject, 0, 0); projRow.Controls.Add(browse, 1, 0);
            row("Project", projRow, 0);
            sPrompt = new TextBox { Multiline = true, AcceptsReturn = true, ScrollBars = ScrollBars.Vertical, WordWrap = true, AccessibleName = "Step prompt" };
            sPrompt.Font = QTheme.Mono(Font);
            promptRow = row("Prompt", sPrompt, 0);
            stepPanel.RowStyles[promptRow].SizeType = SizeType.Percent; stepPanel.RowStyles[promptRow].Height = 100;
            sConv = new QCombo { DropDownStyle = ComboBoxStyle.DropDownList, AccessibleName = "Conversation" };
            sConv.SelectedIndexChanged += (s, e) => { if (!loading) { Store(); Explain(); } };
            row("Conversation", sConv, S(8));
            sSession = new QCombo { DropDownStyle = ComboBoxStyle.DropDownList, AccessibleName = "Existing conversation to continue" };
            sSession.SelectedIndexChanged += (s, e) => { if (!loading) Store(); };
            row(null, sSession, 0);
            sHandoff = new QCheck { Text = "Include a handoff from the previous step", AccessibleDescription = "Its final response, project and run log." };
            sHandoff.CheckedChanged += (s, e) => { if (!loading) { Store(); Explain(); } };
            row(null, sHandoff, 0);
            convNote = new Wrap { ForeColor = QTheme.Muted };
            row(null, convNote, 0);
            sCommit = new QCheck { Text = "Commit after this step", Margin = new Padding(0, 0, S(16), 0) };
            sCommit.CheckedChanged += (s, e) => { if (!loading) { Store(); Explain(); } };
            sReview = new QCheck { Text = "Review before continuing", Margin = new Padding(0), AccessibleDescription = "Pause after this step and its commit, until you continue the batch." };
            tips.SetToolTip(sReview, sReview.AccessibleDescription);
            var afterRow = new FlowLayoutPanel { AutoSize = true, WrapContents = true };   // wraps in a narrow window
            afterRow.Controls.Add(sCommit); afterRow.Controls.Add(sReview);
            row("After the step", afterRow, S(8));
            afterRow.Dock = DockStyle.Fill;
            sMessage = new TextBox { Multiline = true, AcceptsReturn = true, Height = S(46), AccessibleName = "Commit message" };
            row("Message", sMessage, 0);
            commitInfo = new Wrap { ForeColor = QTheme.Muted };
            row(null, commitInfo, 0);
            // after the layout pass (the Layout event comes before it, with the old row heights)
            stepPanel.Layout += (s, e) => { if (!fitPending && IsHandleCreated) { fitPending = true; BeginInvoke((Action)(() => { fitPending = false; FitHost(); })); } };
            stepHost.Controls.Add(stepPanel);
            split.Panel2.Controls.Add(stepHost);

            // ---- footer ----
            var bottom = new Panel { Dock = DockStyle.Bottom, AutoSize = true, Padding = new Padding(pad, S(8), pad, S(6)) };
            bottom.Paint += (s, e) => { using (var p = new Pen(QTheme.Line)) e.Graphics.DrawLine(p, 0, 0, bottom.Width, 0); };
            error = new Wrap { ForeColor = QTheme.Red };
            var buttons = new FlowLayoutPanel { Dock = DockStyle.Top, FlowDirection = FlowDirection.RightToLeft, AutoSize = true };
            var cancel = QTheme.Btn("Cancel", (s, e) => Close());
            var save = QTheme.Btn("Save", (s, e) => Save());
            cancel.MinimumSize = save.MinimumSize = new Size(S(84), 0);
            buttons.Controls.Add(cancel); buttons.Controls.Add(save);
            save.TabIndex = 0; cancel.TabIndex = 1;   // left to right, though laid out right to left
            buttons.Controls.Add(new Label { Text = "Ctrl+Enter saves", AutoSize = true, ForeColor = QTheme.Muted, Margin = new Padding(0, S(6), S(12), 0) });
            CancelButton = cancel;
            bottom.Controls.Add(buttons); bottom.Controls.Add(error);

            // Dock order: the last added docks first (outermost); the tab order follows reading order.
            Controls.Add(area); Controls.Add(head); Controls.Add(top); Controls.Add(bottom);
            top.TabIndex = 0; head.TabIndex = 1; area.TabIndex = 2; bottom.TabIndex = 3;
            QTheme.Style(this);
            QTheme.FocusTint(this);
            QTheme.Primary(save); save.Font = new Font(Font, FontStyle.Bold);
            // the disclosure reads as a heading, not a button: no fill or border until it has the focus
            permsToggle.BackColor = QTheme.Bg; permsToggle.FlatAppearance.BorderSize = 0; permsToggle.FlatAppearance.MouseOverBackColor = QTheme.Panel;
            permsToggle.Padding = new Padding(0); permsToggle.Font = new Font(Font, FontStyle.Bold);
            permsToggle.LostFocus += (s, e) => permsToggle.FlatAppearance.BorderSize = 0;
            if (work.Count == 0) work.Add(new BatchStep { Id = Guid.NewGuid().ToString("N") });
            cur = 0;
            Summarize();
            Fill();
        }

        public List<BatchStep> Steps { get { Store(); return work; } }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (keyData == (Keys.Control | Keys.Enter)) { Save(); return true; }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        void ShowPerms(bool open)
        {
            perms.Visible = open;
            permsToggle.Text = (open ? "▾" : "▸") + permsToggle.Text.Substring(1);
        }

        void Summarize()
        {
            int n = 0;
            foreach (var l in commands.Text.Split('\n')) if (l.Trim() != "") n++;
            string m = Modes[Math.Max(0, mode.SelectedIndex)];
            permsSummary.Text = Sched.ModeLabel(m) + (m == "plan" ? "" : "  ·  " + n + " allowed command" + (n == 1 ? "" : "s")) + (allowDelete.Checked ? "  ·  may delete files" : "");
            permsToggle.AccessibleDescription = permsSummary.Text;
        }

        // The prompt takes the spare height; below its minimum the step's fields scroll (not the whole window).
        void FitHost()
        {
            int[] h = stepPanel.GetRowHeights();
            int need = S(130) + stepPanel.Padding.Vertical;
            for (int i = 0; i < h.Length; i++) if (i != promptRow) need += h[i];
            if (stepHost.AutoScrollMinSize.Height != need) stepHost.AutoScrollMinSize = new Size(0, need);
        }

        void UpdateTools()
        {
            bool has = cur >= 0 && cur < work.Count;
            removeBtn.Enabled = has; upBtn.Enabled = has && cur > 0; downBtn.Enabled = has && cur < work.Count - 1;
        }

        // Two lines per step: number and title; then its project and what happens after it (commit, review).
        void DrawStep(DrawItemEventArgs e)
        {
            if (e.Index < 0 || e.Index >= work.Count) return;
            var g = e.Graphics; var r = e.Bounds; var st = work[e.Index];
            bool sel = (e.State & DrawItemState.Selected) != 0;
            using (var bg = new SolidBrush(sel ? QTheme.Select : stepList.BackColor)) g.FillRectangle(bg, r);
            if (sel) using (var bar = new SolidBrush(stepList.Focused ? QTheme.Focus : QTheme.Accent)) g.FillRectangle(bar, r.X, r.Y, S(3), r.Height);
            const TextFormatFlags f = TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding;
            int lh = Font.Height, y = r.Y + (r.Height - 2 * lh - S(2)) / 2, x = r.X + S(10);
            const TextFormatFlags m = TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding;   // measuring with EndEllipsis comes out too narrow
            int nw = TextRenderer.MeasureText(Math.Max(10, work.Count) + ".", Font, Size.Empty, m).Width + S(5);
            TextRenderer.DrawText(g, (e.Index + 1) + ".", Font, new Rectangle(x, y, nw, lh), QTheme.Muted, f);
            x += nw;
            string title = st.Title != "" ? st.Title : st.Prompt != "" ? Sched.FirstLine(st.Prompt, 60) : "(new step)";
            TextRenderer.DrawText(g, title, Font, new Rectangle(x, y, r.Right - x - S(6), lh), sel ? QTheme.SelectText : QTheme.Text, f);
            int y2 = y + lh + S(2), right = r.Right - S(6);
            foreach (var tag in new[] { st.Review ? "review" : null, st.Commit ? "commit" : null })
            {
                if (tag == null) continue;
                int w = TextRenderer.MeasureText(tag, Font, Size.Empty, m).Width + S(10);
                right -= w;
                var pill = new Rectangle(right, y2, w, lh);
                using (var pb = new SolidBrush(QTheme.Button)) g.FillRectangle(pb, pill);
                TextRenderer.DrawText(g, tag, Font, pill, QTheme.Text, f | TextFormatFlags.HorizontalCenter);
                right -= S(4);
            }
            string proj = st.Project != "" ? Batching.Name(st.Project) : "no project yet";
            TextRenderer.DrawText(g, proj, Font, new Rectangle(x, y2, Math.Max(0, right - x - S(2)), lh), QTheme.Muted, f);
        }

        string RepoOf(string folder)
        {
            string t;
            if (folder == "" || !Directory.Exists(folder)) return null;
            if (!tops.TryGetValue(folder, out t)) tops[folder] = t = Git.TopOf(folder) ?? "";
            return t == "" ? null : t;
        }

        void MoveStep(int dir)
        {
            Store();
            int j = cur + dir;
            if (cur < 0 || j < 0 || j >= work.Count) return;
            var x = work[cur]; work[cur] = work[j]; work[j] = x; cur = j;
            Fill();
        }

        static string ListText(int i, BatchStep s) { return (i + 1) + ". " + (s.Title != "" ? s.Title : s.Prompt != "" ? Sched.FirstLine(s.Prompt, 40) : "(new step)") + (s.Commit ? "  · commit" : "") + (s.Review ? "  · review" : ""); }

        void Fill()
        {
            loading = true;
            stepList.Items.Clear();
            for (int i = 0; i < work.Count; i++) stepList.Items.Add(ListText(i, work[i]));
            if (cur >= 0 && cur < work.Count) stepList.SelectedIndex = cur;
            loading = false;
            ShowStep();
            UpdateTools();
        }

        void ShowStep()
        {
            loading = true;
            var s = cur >= 0 && cur < work.Count ? work[cur] : null;
            stepPanel.Enabled = s != null;
            if (s != null)
            {
                sTitle.Text = s.Title; sProject.Text = s.Project; sPrompt.Text = s.Prompt.Replace("\r\n", "\n").Replace("\n", "\r\n");
                sConv.Items.Clear();
                sConv.Items.Add("Fresh conversation");
                sConv.Items.Add(cur == 0 ? "Continue an existing conversation of this project" : "Continue the previous step's conversation (same project only)");
                sConv.SelectedIndex = s.Conversation == "continue" ? 1 : 0;
                sHandoff.Checked = s.Handoff; sReview.Checked = s.Review; sCommit.Checked = s.Commit; sMessage.Text = s.CommitMessage;
            }
            loading = false;
            Explain();
        }

        void Store()
        {
            if (loading || cur < 0 || cur >= work.Count) return;
            var s = work[cur];
            s.Title = sTitle.Text.Trim(); s.Project = sProject.Text.Trim().Trim('"'); s.Prompt = sPrompt.Text;
            s.Conversation = sConv.SelectedIndex == 1 ? "continue" : "new";
            if (cur == 0 && s.Conversation == "continue" && sSession.Visible) s.ResumeSession = sSession.SelectedIndex >= 0 && sSession.SelectedIndex < sessions.Count ? sessions[sSession.SelectedIndex].Id : "";
            if (cur > 0 || s.Conversation != "continue") s.ResumeSession = "";
            s.Handoff = s.Conversation == "new" && cur > 0 && sHandoff.Checked;
            s.Review = sReview.Checked; s.Commit = sCommit.Checked; s.CommitMessage = sMessage.Text;
            if (cur < stepList.Items.Count && stepList.Items[cur].ToString() != ListText(cur, s)) { loading = true; stepList.Items[cur] = ListText(cur, s); loading = false; }
            stepList.Invalidate();   // the drawn row also shows the project
        }

        // What the conversation and commit choices mean for this step, kept current as you edit.
        void Explain()
        {
            var s = cur >= 0 && cur < work.Count ? work[cur] : null;
            if (s == null) return;
            var p = cur > 0 ? work[cur - 1] : null;
            bool cont = s.Conversation == "continue";
            loading = true;
            sSession.Visible = cur == 0 && cont;
            if (sSession.Visible)
            {
                sSession.Items.Clear();
                sessions = Directory.Exists(s.Project) ? Sessions.List(eng.ProjectsDir, s.Project, 40) : new List<SessionMeta>();
                foreach (var m in sessions) sSession.Items.Add(m.Display);
                for (int i = 0; i < sessions.Count; i++) if (sessions[i].Id == s.ResumeSession) sSession.SelectedIndex = i;
            }
            sHandoff.Visible = cur > 0;   // step 1 has nothing to hand off from
            sHandoff.Enabled = !cont && cur > 0;
            if (!sHandoff.Enabled) sHandoff.Checked = false;
            loading = false;
            string n = cur.ToString(CultureInfo.InvariantCulture);
            convNote.Text = cur == 0
                ? (cont ? "Step 1 continues the conversation you pick. When the batch starts it must belong to this project and not be open in another Claude Code session." : "Step 1 starts a new conversation.")
                : cont ? (p != null && !Sched.SamePath(p.Project, s.Project)
                    ? "⚠ Step " + n + " runs in " + Batching.Name(p.Project) + ". A Claude Code conversation belongs to one folder, so a step that switches projects must start a fresh conversation. Choose Fresh conversation and tick the handoff to pass step " + n + "'s result along."
                    : "Continues step " + n + "'s real Claude Code conversation (resumed by its session ID), in the same project folder. The handoff is only for fresh conversations.")
                : "Fresh conversation" + (s.Handoff ? ". Your prompt follows a handoff: step " + n + "'s final response (its last " + Batching.HandoffExcerpt.ToString("N0") + " characters if longer, marked as shortened) and the path of a file with the complete response, that step's prompt and its run log." : ". Tick the handoff to pass step " + n + "'s result along.");
            QTheme.Dim(sMessage, !s.Commit);   // kept, not cleared, while Commit is off
            commitInfo.AccessibleDescription = null; tips.SetToolTip(commitInfo, null);
            if (!s.Commit) { commitInfo.Text = "No commit. While a batch is paused you can still review and commit by hand in the phone's Commit view."; return; }
            string top = RepoOf(s.Project);
            if (top == null) { commitInfo.Text = "⚠ " + (s.Project == "" ? "Choose the project first." : Batching.Name(s.Project) + " isn't in a Git repository, so it can't commit."); return; }
            var v = Git.Inspect(top);
            int staged = v.Files.FindAll(f => f.Staged).Count;
            string rel = Batching.RelTo(top, s.Project);
            string full = "Commits in " + top + (v.Branch != "" && v.Branch != "(detached)" ? " on branch " + v.Branch : "") + " with your own Git and exactly this message (no AI attribution; hooks and signing as usual; never a push). " +
                "It includes only files that change during this step" + (rel != "" ? " inside " + rel : "") + ". " +
                (v.Files.Count > 0 ? "Right now " + v.Files.Count + " file" + (v.Files.Count == 1 ? " is" : "s are") + " already changed" + (staged > 0 ? " (" + staged + " staged)" : "") + ": they're left out, and if the step changes one of them again (or anything is staged) the commit pauses for you to pick the files. " : "The working tree is clean right now. ") +
                "If nothing changed, it records \"No changes to commit\" and continues. Saving with this ticked authorizes this commit during the run.";
            commitInfo.Text = "Commits in " + Batching.Name(top) + (v.Branch != "" && v.Branch != "(detached)" ? " on " + v.Branch : "") + " with this message, only files this step changes" + (rel != "" ? " inside " + rel : "") + "; never a push. " +
                (v.Files.Count > 0 ? v.Files.Count + " file" + (v.Files.Count == 1 ? " is" : "s are") + " already changed" + (staged > 0 ? " (" + staged + " staged)" : "") + " and left out; if the step changes one again, the commit pauses for you to pick. " : "") +
                "Saving with this ticked authorizes the commit.";
            commitInfo.AccessibleDescription = full; tips.SetToolTip(commitInfo, full);
        }

        BatchDef Collect()
        {
            Store();
            var ser = QueueStore.Ser();
            var b = ser.Deserialize<BatchDef>(ser.Serialize(orig));
            Batching.Normalize(new QueueData { Batches = new List<BatchDef> { b } });
            b.Name = name.Text.Trim();
            b.Runner = runner.SelectedIndex == 1 ? "headless" : "vscode";
            b.PermissionMode = Modes[Math.Max(0, mode.SelectedIndex)];
            b.Model = model.Text.Trim();
            b.AllowCommands = new List<string>();
            foreach (var l in commands.Text.Split('\n')) if (l.Trim() != "") b.AllowCommands.Add(l.Trim());
            b.AllowDelete = allowDelete.Checked;
            b.Start = startAt.Checked ? "at" : "manual";
            b.RunAtUtc = startAt.Checked ? Sched.LocalToUtc(at.Value, TimeZoneInfo.Local) : DateTime.MinValue;
            b.Steps = new List<BatchStep>();
            foreach (var s in work) { var c = ser.Deserialize<BatchStep>(ser.Serialize(s)); if (c.Title == "") c.Title = Sched.FirstLine(c.Prompt, 60); b.Steps.Add(c); }
            return b;
        }

        public bool Save()
        {
            var b = Collect();
            if (b.Start == "at" && !at.Valid) { error.Text = "Enter the start date and time like " + DateTime.Now.Date.AddDays(1).AddHours(2).ToString("ddd d MMM yyyy") + " and " + DateTime.Today.AddHours(2).ToString("t") + ", or choose \"When I click Run now\"."; at.DateBox.Focus(); return false; }
            if (b.Start == "at" && b.RunAtUtc < DateTime.UtcNow.AddMinutes(-1)) { error.Text = "That start time has passed. Choose a later time, or \"When I click Run now\"."; return false; }
            string err = eng.SaveBatch(b, orig.Revision);
            if (err != null) { error.Text = err; Reveal(err); return false; }
            SavedId = b.Id;
            string warn = b.Start == "manual" ? eng.ValidateBatch(b) : null;
            if (warn != null && Visible) MessageBox.Show(this, "Saved. Before it can run: " + warn, "Batches", MessageBoxButtons.OK, MessageBoxIcon.Information);
            Close();
            return true;
        }

        // Shows where a save error points: that step, or the folded permissions.
        void Reveal(string err)
        {
            var m = Regex.Match(err, @"^Step (\d+)");
            int k = m.Success ? int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) - 1 : -1;
            if (k >= 0 && k < work.Count && k != cur) { Store(); cur = k; Fill(); }
            if (!m.Success && (err.Contains("rule") || err.Contains("command") || err.Contains("permission"))) ShowPerms(true);
        }
    }
}
