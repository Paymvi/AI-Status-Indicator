// Claude Status - the Queue view: schedule prompts for Claude Code and review what they did.
// Hosted inside the indicator window when you switch to Queue (created on first use). The scheduling itself is
// QueueEngine (scheduler.cs); this file only shows it and calls its actions. C# 5 (Windows PowerShell's Add-Type).
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Text;
using System.Windows.Forms;

namespace ClaudeStatus
{
    static class QTheme
    {
        public static readonly Color Bg = Color.FromArgb(30, 30, 30), Panel = Color.FromArgb(37, 37, 38), Field = Color.FromArgb(45, 45, 48),
            Text = Color.FromArgb(228, 228, 228), Muted = Color.FromArgb(160, 160, 160), Line = Color.FromArgb(62, 62, 66), Button = Color.FromArgb(55, 55, 58),
            Hover = Color.FromArgb(44, 44, 44), Select = Color.FromArgb(52, 58, 70), Yellow = Color.FromArgb(255, 200, 61), Green = Color.FromArgb(63, 185, 80),
            Red = Color.FromArgb(248, 81, 73), Blue = Color.FromArgb(88, 166, 255), Orange = Color.FromArgb(240, 136, 62), Grey = Color.FromArgb(128, 128, 128);

        public static Color For(string state)
        {
            switch (state)
            {
                case QState.Running: return Yellow;
                case QState.Completed: return Green;
                case QState.NeedsAttention: case QState.Failed: case QState.Blocked: return Red;
                case QState.WaitingUsage: return Orange;
                case QState.Scheduled: case QState.Queued: return Blue;
                default: return Grey;
            }
        }

        // Dark styling for standard controls (the indicator itself is custom-drawn in the same colors).
        public static void Style(Control root)
        {
            foreach (Control c in root.Controls) Style(c);
            if (root is TextBox || root is ComboBox || root is ListBox || root is DateTimePicker) { root.BackColor = Field; root.ForeColor = Text; }
            var tb = root as TextBox; if (tb != null) tb.BorderStyle = BorderStyle.FixedSingle;
            var cb = root as ComboBox; if (cb != null) cb.FlatStyle = FlatStyle.Flat;
            var b = root as Button;
            if (b != null) { b.FlatStyle = FlatStyle.Flat; b.BackColor = Button; b.ForeColor = Text; b.FlatAppearance.BorderColor = Line; b.UseVisualStyleBackColor = false; }
            if (root is Label || root is CheckBox || root is RadioButton) root.ForeColor = root.ForeColor == SystemColors.ControlText ? Text : root.ForeColor;
            if (root is Panel || root is UserControl || root is Form || root is TableLayoutPanel || root is FlowLayoutPanel || root is SplitContainer)
                if (root.BackColor == SystemColors.Control) root.BackColor = Bg;
        }

        public static Button Btn(string text, EventHandler click)
        {
            var b = new Button { Text = text, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Margin = new Padding(0, 0, 6, 6), Padding = new Padding(6, 1, 6, 1) };
            b.Click += click;
            return b;
        }
    }

    // A label that wraps to its width and grows to fit (Dock = Top).
    class Wrap : Label
    {
        public Wrap() { AutoSize = false; Dock = DockStyle.Top; Padding = new Padding(0, 2, 0, 4); }
        protected override void OnTextChanged(EventArgs e) { base.OnTextChanged(e); Fit(); }
        protected override void OnResize(EventArgs e) { base.OnResize(e); Fit(); }
        protected override void OnFontChanged(EventArgs e) { base.OnFontChanged(e); Fit(); }
        void Fit()
        {
            if (Width <= 0) return;
            int h = Text == "" ? 0 : TextRenderer.MeasureText(Text, Font, new Size(Math.Max(10, Width - Padding.Horizontal), int.MaxValue), TextFormatFlags.WordBreak | TextFormatFlags.TextBoxControl).Height + Padding.Vertical;
            if (Height != h) Height = h;
        }
    }

    public class QueueView : UserControl
    {
        readonly QueueEngine eng;
        readonly float scale;
        readonly ListBox list;
        readonly Label summary, warn;
        readonly Button pause;
        readonly Wrap title, state, reason, facts, report;
        readonly FlowLayoutPanel actions;
        readonly ComboBox attempts;
        readonly TextBox output;
        readonly Panel detail, empty;
        readonly Font bold, small, rowFont, titleFont;
        readonly List<object> rows = new List<object>();   // string = section header, QueueTask = task
        string selected = "", shownActionsKey = "", shownOutputKey = "";
        bool splitSet;
        TaskEditor editor;

        int S(float v) { return (int)Math.Round(v * scale); }

        public QueueView(QueueEngine engine, float scale)
        {
            eng = engine;
            this.scale = scale;
            Font = SystemFonts.MessageBoxFont;
            BackColor = QTheme.Bg; ForeColor = QTheme.Text;
            bold = new Font(Font, FontStyle.Bold);
            small = new Font(Font.FontFamily, Font.Size * 0.92f);
            rowFont = new Font("Segoe UI", 12f * scale, GraphicsUnit.Pixel);
            titleFont = new Font("Segoe UI", 15f * scale, FontStyle.Bold, GraphicsUnit.Pixel);

            // top bar: everyday actions
            var top = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(S(8), S(6), S(8), 0), WrapContents = true };
            top.Controls.Add(QTheme.Btn("+  New task", (s, e) => Edit(null)));
            pause = QTheme.Btn("Pause queue", (s, e) => eng.SetPaused(!eng.Data.Paused));
            top.Controls.Add(pause);
            top.Controls.Add(QTheme.Btn("Clear history…", (s, e) => ClearHistory()));
            summary = new Label { AutoSize = true, ForeColor = QTheme.Muted, Margin = new Padding(S(6), S(5), 0, 0) };
            top.Controls.Add(summary);
            warn = new Wrap { ForeColor = QTheme.Red, Padding = new Padding(S(10), 0, S(10), S(2)) };

            var split = new SplitContainer { Dock = DockStyle.Fill, FixedPanel = FixedPanel.Panel1, SplitterWidth = S(5), BackColor = QTheme.Line };
            split.Panel1.BackColor = QTheme.Bg; split.Panel2.BackColor = QTheme.Bg;

            list = new ListBox { Dock = DockStyle.Fill, DrawMode = DrawMode.OwnerDrawVariable, BorderStyle = BorderStyle.None, IntegralHeight = false, BackColor = QTheme.Bg, ForeColor = QTheme.Text };
            list.MeasureItem += (s, e) => { e.ItemHeight = e.Index >= 0 && e.Index < rows.Count && rows[e.Index] is string ? S(24) : S(42); };
            list.DrawItem += DrawRow;
            list.SelectedIndexChanged += (s, e) =>
            {
                int i = list.SelectedIndex;
                if (i >= 0 && i < rows.Count && rows[i] is string) { list.SelectedIndex = i + 1 < rows.Count ? i + 1 : -1; return; }
                var t = i >= 0 && i < rows.Count ? rows[i] as QueueTask : null;
                selected = t != null ? t.Id : "";
                ShowDetail();
            };
            list.DoubleClick += (s, e) => { var t = Selected(); if (t != null) Edit(t); };
            split.Panel1.Controls.Add(list);

            detail = new Panel { Dock = DockStyle.Fill, Padding = new Padding(S(12), S(8), S(12), S(10)), BackColor = QTheme.Bg };
            output = new TextBox { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, WordWrap = true, Font = new Font("Consolas", Font.Size), BackColor = QTheme.Field, ForeColor = QTheme.Text, BorderStyle = BorderStyle.FixedSingle };
            attempts = new ComboBox { Dock = DockStyle.Top, DropDownStyle = ComboBoxStyle.DropDownList, FlatStyle = FlatStyle.Flat, BackColor = QTheme.Field, ForeColor = QTheme.Text };
            attempts.SelectedIndexChanged += (s, e) => { shownOutputKey = ""; ShowOutput(); };
            var outLabel = new Label { Dock = DockStyle.Top, Text = "Output (readable view of Claude's stream; the full log is kept locally)", ForeColor = QTheme.Muted, AutoSize = false, Height = S(22), TextAlign = ContentAlignment.BottomLeft };
            report = new Wrap { ForeColor = QTheme.Text };
            actions = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, WrapContents = true, Padding = new Padding(0, S(6), 0, S(2)) };
            facts = new Wrap { ForeColor = QTheme.Muted };
            reason = new Wrap { ForeColor = QTheme.Text };
            state = new Wrap { Font = bold };
            title = new Wrap { Font = titleFont, ForeColor = QTheme.Text };
            // Docked controls lay out from the last added: Fill first, then the top ones from the bottom up.
            foreach (Control c in new Control[] { output, attempts, outLabel, report, actions, facts, reason, state, title }) detail.Controls.Add(c);
            empty = new Panel { Dock = DockStyle.Fill, BackColor = QTheme.Bg };
            empty.Controls.Add(new Wrap
            {
                Dock = DockStyle.Fill, ForeColor = QTheme.Muted, Padding = new Padding(S(16)),
                Text = "Queue prompts for Claude Code to run while you're away.\r\n\r\n" +
                    "1. Click \"+ New task\", choose the project folder and write the prompt.\r\n" +
                    "2. Pick when it runs: as soon as possible, at a time, after another task, or when usage is available again.\r\n" +
                    "3. Check the permission scope and click \"Add to queue\".\r\n\r\n" +
                    "Tasks run one at a time with `claude -p` in the project folder, using your Claude Code sign-in. " +
                    "Schedules only run while Claude Status is running (minimized to the tray is fine). Quitting it, or the computer sleeping, makes them wait; " +
                    "overdue tasks then start one at a time, and you can skip them.\r\n\r\n" +
                    "Select a task to see its status, Claude's report and the output."
            });
            split.Panel2.Controls.Add(detail);
            split.Panel2.Controls.Add(empty);

            Controls.Add(split);
            Controls.Add(warn);
            Controls.Add(top);
            QTheme.Style(top);
            // The list keeps a fixed share of the width; set once the view has its real size.
            // A narrow window gives way in the list, so the details stay readable.
            Layout += (s, e) =>
            {
                if (split.Width < S(500)) return;
                if (!splitSet) { splitSet = true; split.Panel1MinSize = S(180); split.SplitterDistance = Math.Min(S(320), (int)(split.Width * 0.38)); }
                if (split.Width - split.SplitterDistance < S(320)) split.SplitterDistance = Math.Max(S(180), split.Width - S(320));
            };

            eng.Changed += OnChanged;
            RefreshView();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) { eng.Changed -= OnChanged; if (editor != null && !editor.IsDisposed) editor.Close(); }
            base.Dispose(disposing);
        }

        bool pendingRefresh;
        void OnChanged()
        {
            if (!IsHandleCreated || pendingRefresh) return;
            pendingRefresh = true;
            BeginInvoke((MethodInvoker)(() => { pendingRefresh = false; if (!IsDisposed) RefreshView(); }));
        }

        QueueTask Selected() { return selected == "" ? null : eng.Data.Find(selected); }

        public void SelectTask(string id) { selected = id ?? ""; RefreshView(); }

        // ---------- list ----------
        public void RefreshView()
        {
            var d = eng.Data;
            var attention = new List<QueueTask>(); var running = new List<QueueTask>(); var upcoming = new List<QueueTask>(); var drafts = new List<QueueTask>(); var history = new List<QueueTask>();
            foreach (var t in d.Tasks)
            {
                if (t.State == QState.NeedsAttention || t.State == QState.Blocked) attention.Add(t);
                else if (t.State == QState.Running) running.Add(t);
                else if (QState.Pending(t.State)) upcoming.Add(t);
                else if (t.State == QState.Draft) drafts.Add(t);
                else history.Add(t);
            }
            history.Sort((a, b) => b.FinishedUtc.CompareTo(a.FinishedUtc));
            rows.Clear();
            Action<string, List<QueueTask>> section = (name, items) => { if (items.Count == 0) return; rows.Add(name + "  (" + items.Count + ")"); foreach (var t in items) rows.Add(t); };
            section("NEEDS ATTENTION", attention);
            section("RUNNING", running);
            section("UPCOMING", upcoming);
            section("DRAFTS", drafts);
            section("HISTORY", history);

            list.BeginUpdate();
            int top = list.TopIndex;
            list.Items.Clear();
            int sel = -1;
            for (int i = 0; i < rows.Count; i++) { list.Items.Add(rows[i] is string ? (object)rows[i] : ((QueueTask)rows[i]).Title); var t = rows[i] as QueueTask; if (t != null && t.Id == selected) sel = i; }
            if (sel < 0) { selected = ""; for (int i = 0; i < rows.Count; i++) if (rows[i] is QueueTask) { sel = i; selected = ((QueueTask)rows[i]).Id; break; } }
            if (sel >= 0) list.SelectedIndex = sel;
            if (top < list.Items.Count) list.TopIndex = top;
            list.EndUpdate();

            pause.Text = d.Paused ? "Resume queue" : "Pause queue";
            int nUp = upcoming.Count, nAtt = attention.Count;
            summary.Text = (d.Paused ? "Paused: nothing new starts (a running task continues)" : running.Count > 0 ? "Running 1 task" : nUp > 0 ? "Waiting" : "Idle") +
                (nUp > 0 ? " · " + nUp + " upcoming" : "") + (nAtt > 0 ? " · " + nAtt + " need attention" : "");
            warn.Text = eng.Store.Warning;
            ShowDetail();
        }

        void DrawRow(object sender, DrawItemEventArgs e)
        {
            if (e.Index < 0 || e.Index >= rows.Count) return;
            var g = e.Graphics;
            var r = e.Bounds;
            var head = rows[e.Index] as string;
            if (head != null)
            {
                using (var b = new SolidBrush(QTheme.Bg)) g.FillRectangle(b, r);
                TextRenderer.DrawText(g, head, small, new Rectangle(r.Left + S(10), r.Top, r.Width - S(12), r.Height), QTheme.Muted, TextFormatFlags.Left | TextFormatFlags.Bottom | TextFormatFlags.SingleLine);
                return;
            }
            var t = (QueueTask)rows[e.Index];
            bool sel = (e.State & DrawItemState.Selected) != 0;
            using (var b = new SolidBrush(sel ? QTheme.Select : QTheme.Bg)) g.FillRectangle(b, r);
            Color c = QTheme.For(t.State);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            int dot = S(8);
            using (var b = new SolidBrush(c)) g.FillEllipse(b, r.Left + S(10), r.Top + S(8), dot, dot);
            g.SmoothingMode = SmoothingMode.None;
            string st = QState.Label(t.State).ToUpperInvariant();
            int sw = TextRenderer.MeasureText(g, st, small, Size.Empty, TextFormatFlags.NoPadding | TextFormatFlags.SingleLine).Width;
            var flags = TextFormatFlags.Left | TextFormatFlags.Top | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding;
            TextRenderer.DrawText(g, st, small, new Rectangle(r.Right - sw - S(8), r.Top + S(5), sw + S(2), S(16)), c, flags);
            TextRenderer.DrawText(g, t.Title, rowFont, new Rectangle(r.Left + S(24), r.Top + S(3), r.Width - S(24) - sw - S(14), S(18)), QTheme.Text, flags);
            TextRenderer.DrawText(g, Line2(t), small, new Rectangle(r.Left + S(24), r.Top + S(22), r.Width - S(30), S(16)), QTheme.Muted, flags);
            using (var p = new Pen(QTheme.Line)) g.DrawLine(p, r.Left + S(8), r.Bottom - 1, r.Right - S(8), r.Bottom - 1);
        }

        static string Folder(string p) { try { return Path.GetFileName(Sched.NormPath(p)); } catch { return p; } }

        string Line2(QueueTask t)
        {
            string when = "";
            if (t.State == QState.Running) when = "since " + Sched.LocalText(t.Last != null ? t.Last.StartedUtc : DateTime.MinValue);
            else if (QState.Finished(t.State) || t.State == QState.NeedsAttention) when = t.Summary != "" ? t.Summary : t.Reason;
            else when = t.Reason != "" ? t.Reason : ScheduleText(t);
            return Folder(t.Project) + "  ·  " + when;
        }

        string ScheduleText(QueueTask t)
        {
            switch (t.Schedule)
            {
                case "at": return "At " + Sched.LocalText(t.RunAtUtc) + (t.TimeZone != "" && t.TimeZone != TimeZoneInfo.Local.Id ? " (set in " + t.TimeZone + ")" : "");
                case "after": var p = eng.Data.Find(t.After); return "After \"" + (p != null ? p.Title : "(removed task)") + "\" finishes successfully";
                case "usage": return "When usage is available" + (t.UsageResetUtc != DateTime.MinValue ? " (you said it resets " + Sched.LocalText(t.UsageResetUtc) + ")" : "");
                default: return "As soon as possible, in queue order";
            }
        }

        string ConversationText(QueueTask t)
        {
            string s;
            if (t.Conversation == "resume") s = "Continues conversation " + Short(t.ResumeSession);
            else if (t.Conversation == "predecessor") { var p = eng.Data.Find(t.After); s = "Continues the conversation of \"" + (p != null ? p.Title : "?") + "\""; }
            else s = "New conversation";
            if (t.SessionId != "") s += "  ·  session " + t.SessionId;
            return s;
        }

        static string Short(string sid) { return sid.Length > 8 ? sid.Substring(0, 8) : sid; }

        // ---------- detail ----------
        void ShowDetail()
        {
            var t = Selected();
            detail.Visible = t != null;
            empty.Visible = t == null;
            if (t == null) return;
            title.Text = t.Title;
            state.Text = QState.Label(t.State).ToUpperInvariant() + (t.State == QState.Completed ? "  ·  run finished; result reported by Claude, not verified" : "");
            state.ForeColor = QTheme.For(t.State);
            reason.Text = t.Reason;
            var sb = new StringBuilder();
            sb.Append("Project: ").Append(t.Project).Append("\r\n");
            sb.Append("Conversation: ").Append(ConversationText(t)).Append("\r\n");
            sb.Append("When: ").Append(ScheduleText(t)).Append("\r\n");
            sb.Append("Permissions: ").Append(Sched.PermissionShort(t)).Append("\r\n");
            var a = t.Last;
            if (a != null)
            {
                sb.Append("Runs: ").Append(t.Attempts.Count).Append("  ·  last started ").Append(Sched.LocalText(a.StartedUtc));
                if (a.EndedUtc != DateTime.MinValue) sb.Append(", ended ").Append(Sched.LocalText(a.EndedUtc)).Append(a.ExitCode != int.MinValue ? " (exit code " + a.ExitCode + ")" : " (exit code unknown)");
                sb.Append("\r\n");
            }
            if (t.SessionId != "" && t.State != QState.Running)
                sb.Append("Open it yourself: scheduled conversations are started by the Claude Code CLI and aren't guaranteed to appear in VS Code's conversation list. Use \"Open in terminal\", or run the copied command in any terminal (also VS Code's).");
            facts.Text = sb.ToString().TrimEnd();
            report.Text = t.Summary != "" || t.Denials.Count > 0 ? "Claude's report: " + (t.Summary != "" ? t.Summary : "(none)") + (t.Denials.Count > 0 ? "\r\nBlocked actions: " + string.Join("; ", t.Denials) : "") : "";

            string key = t.Id + "|" + t.State + "|" + t.SessionId + "|" + t.Attempts.Count + "|" + eng.Data.Paused;
            if (key != shownActionsKey) { shownActionsKey = key; BuildActions(t); }
            string akey = t.Id + "|" + t.Attempts.Count;
            if ((string)attempts.Tag != akey)
            {
                attempts.Tag = akey;
                attempts.Items.Clear();
                for (int i = 0; i < t.Attempts.Count; i++)
                {
                    var x = t.Attempts[i];
                    attempts.Items.Add("Run " + x.N + "  ·  " + Sched.LocalText(x.StartedUtc) + "  ·  " + (x.Outcome != "" ? x.Outcome.Replace('_', ' ') : t.State == QState.Running && i == t.Attempts.Count - 1 ? "running" : "?") + (x.Mode == "resume" ? "  ·  resumed" : ""));
                }
                if (attempts.Items.Count > 0) attempts.SelectedIndex = attempts.Items.Count - 1;
                attempts.Visible = attempts.Items.Count > 0;
                shownOutputKey = "";
            }
            ShowOutput();
        }

        void ShowOutput()
        {
            var t = Selected();
            if (t == null) return;
            int idx = attempts.SelectedIndex;
            string text;
            if (t.Attempts.Count == 0) text = t.State == QState.Draft ? "Draft: nothing has run yet.\r\n\r\nPrompt:\r\n" + t.Prompt : "Not run yet.\r\n\r\nPrompt:\r\n" + t.Prompt;
            else
            {
                var a = idx >= 0 && idx < t.Attempts.Count ? t.Attempts[idx] : t.Last;
                bool live = t.State == QState.Running && a == t.Last;
                string key = t.Id + "|" + a.N + "|" + (live ? "" : a.Outcome);
                string body = eng.OutputOf(t, t.Attempts.IndexOf(a));
                if (!live && key == shownOutputKey) return;
                shownOutputKey = key;
                text = "Submitted prompt" + (a.Mode == "resume" ? " (resumed conversation " + Short(a.SessionId) + ")" : "") + ":\r\n" + a.Prompt + "\r\n" + new string('─', 40) + "\r\n" + body;
                if (!live && t.ResultText != "" && a == t.Last && t.State != QState.Failed) text += "\r\n" + new string('─', 40) + "\r\nClaude's final message:\r\n" + t.ResultText;
            }
            text = text.Replace("\r\n", "\n").Replace("\n", "\r\n");
            if (output.Text == text) return;
            bool atEnd = output.SelectionStart >= output.TextLength - 2;
            output.Text = text;
            if (atEnd && t.State == QState.Running) { output.SelectionStart = output.TextLength; output.ScrollToCaret(); }
        }

        void BuildActions(QueueTask t)
        {
            actions.SuspendLayout();
            foreach (Control c in actions.Controls) c.Dispose();
            actions.Controls.Clear();
            Action<string, Action> add = (text, act) => actions.Controls.Add(QTheme.Btn(text, (s, e) => { try { act(); } catch (Exception ex) { MessageBox.Show(this, ex.Message, "Claude Status"); } }));
            string id = t.Id;
            bool pending = QState.Pending(t.State);
            if (t.State == QState.Draft) add("Add to queue", () => eng.Enqueue(id));
            if (t.State == QState.Running) add("Cancel run…", () => CancelRun(t));
            if (pending || t.State == QState.Draft) add("Run now", () => eng.RunNow(id));
            if (t.State == QState.WaitingUsage || (pending && t.NextRetryUtc != DateTime.MinValue)) add("Change next try…", () => PickRetry(t));
            if (t.State == QState.NeedsAttention || t.State == QState.Failed || t.State == QState.Canceled || t.State == QState.Completed)
            {
                if (t.SessionId != "" && t.State != QState.Completed) add("Continue conversation", () => eng.Retry(id, true));
                add(t.Conversation == "new" ? "Run again (new conversation)" : "Run again", () => { if (Confirm("Send the original prompt again? Work already done may be repeated.")) eng.Retry(id, false); });
                if (t.State != QState.Completed && t.State != QState.Canceled) add("Mark completed", () => eng.MarkCompleted(id));
            }
            if (t.SessionId != "" && t.State != QState.Running)
            {
                add("Follow up…", () => FollowUp(t));
                add("Open in terminal", () => OpenTerminal(t));
                add("Copy resume command", () => { try { Clipboard.SetText("cd /d \"" + t.Project + "\" && claude --resume " + t.SessionId); } catch { } });
            }
            if (t.State != QState.Running) add("Edit…", () => Edit(t));
            add("Duplicate", () => { var c = eng.Duplicate(id); if (c != null) SelectTask(c.Id); });
            if (pending || t.State == QState.Draft) { add("▲", () => eng.Move(id, -1)); add("▼", () => eng.Move(id, 1)); }
            if (pending) add("Back to draft", () => eng.ToDraft(id));
            if (pending || t.State == QState.Draft) add("Skip", () => eng.Skip(id));
            if (t.State != QState.Running) add("Remove…", () => { if (Confirm("Remove \"" + t.Title + "\" and its output? This can't be undone.")) { string e = eng.Remove(id); if (e != null) MessageBox.Show(this, e, "Claude Status"); } });
            add("Open folder", () => { if (Directory.Exists(t.Project)) Process.Start("explorer.exe", "\"" + t.Project + "\""); });
            QTheme.Style(actions);
            actions.ResumeLayout();
        }

        bool Confirm(string text) { return MessageBox.Show(this, text, "Claude Status", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) == DialogResult.OK; }

        void CancelRun(QueueTask t)
        {
            if (Confirm("Stop \"" + t.Title + "\" now?\r\n\r\nClaude and everything it started are stopped. Output so far is kept. File edits it already made are NOT undone."))
                eng.Cancel(t.Id);
        }

        void PickRetry(QueueTask t)
        {
            using (var f = new Form { Text = "Next try", FormBorderStyle = FormBorderStyle.FixedDialog, StartPosition = FormStartPosition.CenterParent, MinimizeBox = false, MaximizeBox = false, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Font = Font, Padding = new Padding(S(10)) })
            {
                var flow = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false };
                flow.Controls.Add(new Label { Text = "Try again at (your local time):", AutoSize = true });
                var dt = new DateTimePicker { Format = DateTimePickerFormat.Custom, CustomFormat = "ddd d MMM yyyy   HH:mm", Width = S(260), Value = t.NextRetryUtc > DateTime.UtcNow ? t.NextRetryUtc.ToLocalTime() : DateTime.Now.AddMinutes(30) };
                flow.Controls.Add(dt);
                flow.Controls.Add(new Label { Text = "Claude Status doesn't know your remaining usage; it only knows\r\nreset times Claude Code reports.", AutoSize = true, ForeColor = QTheme.Muted });
                var ok = QTheme.Btn("Set", (s, e) => f.DialogResult = DialogResult.OK);
                flow.Controls.Add(ok);
                f.Controls.Add(flow); f.AcceptButton = ok;
                QTheme.Style(f); f.BackColor = QTheme.Bg;
                if (f.ShowDialog(this) == DialogResult.OK) eng.SetRetryTime(t.Id, Sched.LocalToUtc(dt.Value, TimeZoneInfo.Local));
            }
        }

        void ClearHistory()
        {
            int n = 0; foreach (var t in eng.Data.Tasks) if (QState.Finished(t.State)) n++;
            if (n == 0) { MessageBox.Show(this, "There's no finished task to clear.", "Claude Status"); return; }
            if (Confirm("Delete " + n + " finished task(s) (completed, failed, canceled) and their saved output? Tasks that others still wait for are kept.\r\n\r\nFinished tasks are also deleted automatically after " + eng.HistoryDays + " days (\"queueHistoryDays\" in config.json)."))
                eng.ClearHistory();
        }

        void OpenTerminal(QueueTask t)
        {
            if (!Sched.IsGuid(t.SessionId) || !Directory.Exists(t.Project)) return;
            var cmd = QueueEngine.ResolveClaude(null);
            if (cmd == null) { MessageBox.Show(this, "claude.exe wasn't found.", "Claude Status"); return; }
            // A new console window running the interactive CLI in that conversation (fixed arguments: a UUID only).
            Process.Start(new ProcessStartInfo(cmd[0], "--resume " + t.SessionId) { UseShellExecute = true, WorkingDirectory = t.Project });
        }

        void FollowUp(QueueTask t)
        {
            var n = TaskEditor.NewTask();
            n.Title = "Follow-up: " + t.Title; n.Project = t.Project;
            n.PermissionMode = t.PermissionMode; n.AllowCommands = new List<string>(t.AllowCommands); n.AllowCommit = t.AllowCommit; n.AllowPush = t.AllowPush; n.AllowDelete = t.AllowDelete; n.Model = t.Model;
            if (QState.Finished(t.State) && t.State == QState.Completed) { n.Conversation = "predecessor"; n.Schedule = "after"; n.After = t.Id; }
            else { n.Conversation = "resume"; n.ResumeSession = t.SessionId; }
            Edit(n, true);
        }

        void Edit(QueueTask t) { Edit(t, false); }

        void Edit(QueueTask t, bool isNew)
        {
            if (editor != null && !editor.IsDisposed) { editor.Activate(); return; }
            if (t == null) { t = TaskEditor.NewTask(); isNew = true; var last = eng.Data.Tasks.Count > 0 ? eng.Data.Tasks[eng.Data.Tasks.Count - 1] : null; if (last != null) t.Project = last.Project; }
            editor = new TaskEditor(eng, t, isNew, scale);
            editor.FormClosed += (s, e) => { if (editor.SavedId != null) SelectTask(editor.SavedId); editor = null; };
            editor.Show(FindForm());
        }
    }

    // ---------- the task editor (a normal, activatable window: the indicator itself never takes focus) ----------
    public class TaskEditor : Form
    {
        readonly QueueEngine eng;
        readonly QueueTask orig;
        readonly bool isNew;
        readonly float scale;
        readonly int revision;
        readonly TextBox title, prompt, sessionId, commands, model;
        readonly ComboBox project, sessions, after, mode, missed;
        readonly RadioButton cNew, cResume, cPred, wAsap, wAt, wAfter, wUsage;
        readonly DateTimePicker at, reset;
        readonly CheckBox resetKnown, allowCommit, allowPush, allowDelete, advanced;
        readonly Wrap scope, sessionNote;
        readonly Panel adv;
        readonly Label tzLabel;
        readonly List<QueueTask> afterChoices = new List<QueueTask>();
        List<SessionMeta> found = new List<SessionMeta>();
        public string SavedId;

        int S(float v) { return (int)Math.Round(v * scale); }

        public static QueueTask NewTask()
        {
            var t = new QueueTask { Id = Guid.NewGuid().ToString("N") };
            t.AllowCommands.AddRange(Sched.DefaultCommands);
            return t;
        }

        public TaskEditor(QueueEngine engine, QueueTask task, bool isNew, float scale)
        {
            eng = engine; orig = task; this.isNew = isNew; revision = task.Revision;
            using (var g = CreateGraphics()) this.scale = Math.Max(scale, g.DpiX / 96f);
            bool running = task.State == QState.Running;
            Text = isNew ? "New scheduled task" : "Edit task";
            Font = SystemFonts.MessageBoxFont;
            StartPosition = FormStartPosition.CenterScreen;
            ShowInTaskbar = true; MinimizeBox = false;
            BackColor = QTheme.Bg; ForeColor = QTheme.Text;
            Size = new Size(S(700), S(760));
            MinimumSize = new Size(S(520), S(480));
            Rectangle wa = Screen.FromPoint(Cursor.Position).WorkingArea;
            if (Height > wa.Height) Height = wa.Height;

            var body = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, AutoScroll = true, Padding = new Padding(S(12), S(10), S(16), S(10)), BackColor = QTheme.Bg };
            body.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            Action<string, Control> row = (label, c) =>
            {
                var l = new Label { Text = label, AutoSize = true, ForeColor = QTheme.Muted, Margin = new Padding(0, S(6), S(10), 0) };
                c.Dock = DockStyle.Fill; c.Margin = new Padding(0, S(3), 0, S(3));
                body.Controls.Add(l); body.Controls.Add(c);
            };
            Action<Control> full = c => { c.Dock = DockStyle.Fill; c.Margin = new Padding(0, S(2), 0, S(2)); body.Controls.Add(c); body.SetColumnSpan(c, 2); };

            title = new TextBox { Text = task.Title };
            row("Title", title);

            var projRow = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, Height = S(28) };
            projRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); projRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            project = new ComboBox { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDown, Text = task.Project };
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var x in eng.Data.Tasks) if (x.Project != "" && seen.Add(x.Project)) project.Items.Add(x.Project);
            project.Text = task.Project;
            project.TextChanged += (s, e) => { LoadSessions(); UpdateScope(); };
            var browse = QTheme.Btn("Browse…", (s, e) =>
            {
                using (var fb = new FolderBrowserDialog { Description = "Project folder Claude works in", SelectedPath = Directory.Exists(project.Text) ? project.Text : "" })
                    if (fb.ShowDialog(this) == DialogResult.OK) project.Text = fb.SelectedPath;
            });
            browse.Margin = new Padding(S(6), 0, 0, 0);
            projRow.Controls.Add(project, 0, 0); projRow.Controls.Add(browse, 1, 0);
            row("Project folder", projRow);

            prompt = new TextBox { Multiline = true, AcceptsReturn = true, AcceptsTab = true, ScrollBars = ScrollBars.Vertical, WordWrap = true, Height = S(170), Text = (task.Prompt ?? "").Replace("\r\n", "\n").Replace("\n", "\r\n"), Font = new Font("Consolas", Font.Size + 0.5f) };
            row("Prompt", prompt);

            // conversation
            cNew = new RadioButton { Text = "New conversation", AutoSize = true, Checked = task.Conversation == "new" };
            cResume = new RadioButton { Text = "Continue a conversation in this project:", AutoSize = true, Checked = task.Conversation == "resume" };
            cPred = new RadioButton { Text = "Continue the conversation of the task it runs after", AutoSize = true, Checked = task.Conversation == "predecessor" };
            sessions = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = S(440) };
            sessions.SelectedIndexChanged += (s, e) => { int i = sessions.SelectedIndex; if (i >= 0 && i < found.Count) { sessionId.Text = found[i].Id; cResume.Checked = true; } };
            sessionId = new TextBox { Text = task.ResumeSession, Width = S(300) };
            sessionId.TextChanged += (s, e) => { if (sessionId.Text.Trim() != "") cResume.Checked = true; UpdateScope(); };
            sessionNote = new Wrap { ForeColor = QTheme.Muted };
            var conv = Stack(cNew, cResume, Indent(sessions), Indent(Pair(new Label { Text = "or session ID:", AutoSize = true, ForeColor = QTheme.Muted, Margin = new Padding(0, S(4), S(6), 0) }, sessionId)), cPred, sessionNote);
            row("Conversation", conv);

            // when
            wAsap = new RadioButton { Text = "As soon as possible (in queue order)", AutoSize = true, Checked = task.Schedule == "asap" };
            wAt = new RadioButton { Text = "At", AutoSize = true, Checked = task.Schedule == "at" };
            wAfter = new RadioButton { Text = "After this task finishes successfully:", AutoSize = true, Checked = task.Schedule == "after" };
            wUsage = new RadioButton { Text = "When usage is available again (after a usage limit)", AutoSize = true, Checked = task.Schedule == "usage" };
            at = new DateTimePicker { Format = DateTimePickerFormat.Custom, CustomFormat = "ddd d MMM yyyy   HH:mm", Width = S(230), Value = task.RunAtUtc != DateTime.MinValue ? task.RunAtUtc.ToLocalTime() : DateTime.Now.Date.AddDays(1).AddHours(2) };
            at.ValueChanged += (s, e) => { wAt.Checked = true; UpdateScope(); };
            tzLabel = new Label { AutoSize = true, ForeColor = QTheme.Muted, Margin = new Padding(S(6), S(4), 0, 0), Text = TimeZoneInfo.Local.IsDaylightSavingTime(DateTime.Now) ? TimeZoneInfo.Local.DaylightName : TimeZoneInfo.Local.StandardName };
            after = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = S(380) };
            foreach (var x in eng.Data.Tasks)
                if (x.Id != task.Id && !QState.Finished(x.State) || x.Id != task.Id && x.Id == task.After)
                { afterChoices.Add(x); after.Items.Add(x.Title + "  ·  " + QState.Label(x.State)); if (x.Id == task.After) after.SelectedIndex = after.Items.Count - 1; }
            after.SelectedIndexChanged += (s, e) => { wAfter.Checked = true; UpdateScope(); };
            resetKnown = new CheckBox { Text = "I know when it resets:", AutoSize = true, Checked = task.UsageResetUtc != DateTime.MinValue };
            reset = new DateTimePicker { Format = DateTimePickerFormat.Custom, CustomFormat = "ddd d MMM yyyy   HH:mm", Width = S(230), Value = task.UsageResetUtc != DateTime.MinValue ? task.UsageResetUtc.ToLocalTime() : DateTime.Now.AddHours(1) };
            reset.ValueChanged += (s, e) => { resetKnown.Checked = true; wUsage.Checked = true; };
            var usageNote = new Wrap { ForeColor = QTheme.Muted, Dock = DockStyle.None, Width = S(440), Text = "Runs when no usage limit is known to be in effect. Claude Status only knows reset times that Claude Code reports (or that you enter); it can't see your remaining tokens." };
            var when = Stack(wAsap, Pair(wAt, at, tzLabel), wAfter, Indent(after), wUsage, Indent(Pair(resetKnown, reset)), Indent(usageNote));
            row("When", when);

            // permission scope (always visible) + advanced
            scope = new Wrap { ForeColor = QTheme.Text, BackColor = QTheme.Panel, Padding = new Padding(S(8), S(6), S(8), S(6)) };
            row("Permissions", scope);
            advanced = new CheckBox { Text = "Advanced options (permissions, model, missed schedules)", AutoSize = true };
            full(advanced);
            adv = new Panel { AutoSize = true, Visible = false };
            var at2 = new TableLayoutPanel { Dock = DockStyle.Top, ColumnCount = 2, AutoSize = true };
            at2.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize)); at2.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            Action<string, Control> arow = (label, c) =>
            {
                var l = new Label { Text = label, AutoSize = true, ForeColor = QTheme.Muted, Margin = new Padding(0, S(6), S(10), 0) };
                c.Dock = DockStyle.Fill; c.Margin = new Padding(0, S(3), 0, S(3));
                at2.Controls.Add(l); at2.Controls.Add(c);
            };
            mode = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList };
            foreach (var m in new[] { "acceptEdits", "dontAsk", "plan" }) mode.Items.Add(Sched.ModeLabel(m));
            mode.SelectedIndex = Math.Max(0, Array.IndexOf(new[] { "acceptEdits", "dontAsk", "plan" }, task.PermissionMode));
            mode.SelectedIndexChanged += (s, e) => UpdateScope();
            arow("Claude may", mode);
            commands = new TextBox { Multiline = true, AcceptsReturn = true, ScrollBars = ScrollBars.Vertical, Height = S(120), Text = string.Join("\r\n", task.AllowCommands), Font = new Font("Consolas", Font.Size) };
            commands.TextChanged += (s, e) => UpdateScope();
            arow("Allowed commands", commands);
            arow("", new Wrap { ForeColor = QTheme.Muted, Text = "One command pattern per line (\"npm test *\"), allowed in both Bash and PowerShell; or a full Claude Code rule such as WebFetch(domain:docs.python.org). Read-only commands are always allowed. Everything else is denied without asking." });
            allowCommit = new CheckBox { Text = "Allow git commit", AutoSize = true, Checked = task.AllowCommit };
            allowPush = new CheckBox { Text = "Allow git push (publishes your code)", AutoSize = true, Checked = task.AllowPush };
            allowDelete = new CheckBox { Text = "Allow deleting files (rm, Remove-Item, git clean, git reset --hard, …)", AutoSize = true, Checked = task.AllowDelete };
            foreach (var cb in new[] { allowCommit, allowPush, allowDelete }) cb.CheckedChanged += (s, e) => UpdateScope();
            arow("Also authorize", Stack(allowCommit, allowPush, allowDelete));
            model = new TextBox { Text = task.Model };
            arow("Model", model);
            arow("", new Wrap { ForeColor = QTheme.Muted, Text = "Blank = your Claude Code default. For example sonnet, opus, or a full model name." });
            missed = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList };
            missed.Items.Add("Run it once Claude Status is running again (after a 1-minute chance to skip)"); missed.Items.Add("Skip it");
            missed.SelectedIndex = task.IfMissed == "skip" ? 1 : 0;
            arow("If the time is missed", missed);
            adv.Controls.Add(at2);
            full(adv);
            advanced.CheckedChanged += (s, e) => { adv.Visible = advanced.Checked; };
            full(new Wrap { ForeColor = QTheme.Muted, Text = "Project instructions (CLAUDE.md) and your Claude Code settings apply, as when you run claude yourself. The prompt is passed to Claude Code exactly as written (on its input, never on a command line)." });

            // buttons
            var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Padding = new Padding(S(10), S(6), S(10), S(10)), BackColor = QTheme.Bg };
            var cancel = QTheme.Btn(running ? "Close" : "Cancel", (s, e) => Close());
            buttons.Controls.Add(cancel);
            if (!running)
            {
                bool draft = isNew || task.State == QState.Draft;
                var queue = QTheme.Btn(draft ? "Add to queue" : "Save", (s, e) => SaveAs(false));
                queue.Font = new Font(Font, FontStyle.Bold);
                buttons.Controls.Add(queue);
                AcceptButton = null;   // Enter belongs to the prompt
                buttons.Controls.Add(QTheme.Btn(draft ? "Save as draft" : "Save as draft (won't run)", (s, e) => SaveAs(true)));
            }
            else
                buttons.Controls.Add(new Label { AutoSize = true, ForeColor = QTheme.Yellow, Margin = new Padding(0, S(6), S(12), 0), Text = "Running: the submitted prompt can't change. Duplicate the task to queue a revised version." });
            CancelButton = cancel;

            Controls.Add(body);
            Controls.Add(buttons);
            QTheme.Style(this);
            if (running) foreach (Control c in new Control[] { title, project, prompt, conv, when, adv }) c.Enabled = false;
            LoadSessions();
            UpdateScope();
        }

        Control Stack(params Control[] items)
        {
            var p = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false };
            foreach (var c in items) { c.Margin = new Padding(0, S(2), 0, S(2)); if (c is Wrap) { c.Dock = DockStyle.None; c.Width = S(470); } p.Controls.Add(c); }
            return p;
        }

        Control Pair(params Control[] items)
        {
            var p = new FlowLayoutPanel { FlowDirection = FlowDirection.LeftToRight, AutoSize = true, WrapContents = false, Margin = new Padding(0) };
            foreach (var c in items) p.Controls.Add(c);
            return p;
        }

        Control Indent(Control c) { var p = Pair(c); p.Padding = new Padding(S(18), 0, 0, 0); return p; }

        void LoadSessions()
        {
            sessions.Items.Clear();
            found = Directory.Exists(project.Text) ? Sessions.List(eng.ProjectsDir, project.Text, 40) : new List<SessionMeta>();
            foreach (var m in found) sessions.Items.Add(m.Display);
            for (int i = 0; i < found.Count; i++) if (found[i].Id == sessionId.Text.Trim()) sessions.SelectedIndex = i;
            sessionNote.Text = (found.Count == 0 ? (Directory.Exists(project.Text) ? "No saved conversations found for this folder. " : "") : "") +
                "The list comes from Claude Code's local transcripts for this folder (their format isn't documented, so titles are best effort). " +
                "A conversation that is open in another Claude Code window right now won't be resumed: the task waits as Needs attention instead. " +
                "Scheduled runs use the Claude Code CLI; they aren't guaranteed to show up in VS Code's conversation list (use Open in terminal or `claude --resume <id>`).";
        }

        // Builds the task from the form (without saving).
        QueueTask Collect(bool draft)
        {
            var t = QueueStore.Clone(orig);
            t.Title = title.Text.Trim();
            t.Project = project.Text.Trim().Trim('"');
            t.Prompt = prompt.Text;
            t.Conversation = cResume.Checked ? "resume" : cPred.Checked ? "predecessor" : "new";
            t.ResumeSession = cResume.Checked ? sessionId.Text.Trim() : "";
            t.Schedule = wAt.Checked ? "at" : wAfter.Checked ? "after" : wUsage.Checked ? "usage" : "asap";
            t.After = wAfter.Checked && after.SelectedIndex >= 0 ? afterChoices[after.SelectedIndex].Id : "";
            if (t.Schedule == "at")
            {
                DateTime wall = at.Value; wall = new DateTime(wall.Year, wall.Month, wall.Day, wall.Hour, wall.Minute, 0);
                t.RunAtLocal = wall.ToString("yyyy-MM-dd HH:mm"); t.TimeZone = TimeZoneInfo.Local.Id;
                t.RunAtUtc = Sched.LocalToUtc(wall, TimeZoneInfo.Local);
            }
            else { t.RunAtUtc = DateTime.MinValue; t.RunAtLocal = ""; }
            t.UsageResetUtc = t.Schedule == "usage" && resetKnown.Checked ? Sched.LocalToUtc(reset.Value, TimeZoneInfo.Local) : DateTime.MinValue;
            t.PermissionMode = new[] { "acceptEdits", "dontAsk", "plan" }[Math.Max(0, mode.SelectedIndex)];
            t.AllowCommands = new List<string>();
            foreach (var l in commands.Text.Split('\n')) if (l.Trim() != "") t.AllowCommands.Add(l.Trim());
            t.AllowCommit = allowCommit.Checked; t.AllowPush = allowPush.Checked; t.AllowDelete = allowDelete.Checked;
            t.Model = model.Text.Trim();
            t.IfMissed = missed.SelectedIndex == 1 ? "skip" : "run";
            t.State = draft ? QState.Draft : QState.Scheduled;
            if (draft) t.Reason = "Draft: won't run until you add it to the queue.";
            if (t.Title == "") t.Title = Sched.FirstLine(t.Prompt, 60);
            return t;
        }

        void UpdateScope()
        {
            if (scope == null) return;
            var t = Collect(true);
            scope.Text = Sched.PermissionSummary(t);
            if (t.Conversation != "new" && t.PermissionMode == "plan") scope.Text += " (Plan mode: Claude only reads and proposes.)";
        }

        void SaveAs(bool draft)
        {
            var t = Collect(draft);
            if (!draft && t.Schedule == "at" && t.RunAtUtc < DateTime.UtcNow.AddMinutes(-1) &&
                MessageBox.Show(this, "That time is in the past, so the task will start right away. Continue?", "Claude Status", MessageBoxButtons.OKCancel) != DialogResult.OK) return;
            if (!draft && !Confirm(t)) return;
            string err = eng.Save(t, revision);
            if (err != null) { MessageBox.Show(this, err, "Claude Status", MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
            SavedId = t.Id;
            Close();
        }

        // The permission scope is shown once more, explicitly, before anything can run unattended.
        bool Confirm(QueueTask t)
        {
            string extra = t.AllowPush || t.AllowDelete ? "\r\n\r\nYou authorized " + (t.AllowPush ? "git push" : "") + (t.AllowPush && t.AllowDelete ? " and " : "") + (t.AllowDelete ? "deleting files" : "") + " without asking." : "";
            return MessageBox.Show(this, "\"" + t.Title + "\" will run unattended in\r\n" + t.Project + "\r\n\r\n" + Sched.PermissionSummary(t) + extra +
                "\r\n\r\nThis is enforced by Claude Code's own permission rules. It limits which tools and commands Claude may use; it is not a sandbox (an allowed command such as a test script can still do anything that script does).",
                "Add to queue", MessageBoxButtons.OKCancel, MessageBoxIcon.Information) == DialogResult.OK;
        }
    }
}
