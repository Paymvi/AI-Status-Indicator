// Claude Status - the Queue view: schedule prompts for Claude Code, see what runs, what's next, what needs you and what
// finished. Hosted inside the indicator window when you switch to Queue (created on first use). The scheduling itself
// is QueueEngine (scheduler.cs); this file only shows it and calls its actions. C# 5 (Windows PowerShell's Add-Type).
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
        // Dark, like the Status view; Windows high-contrast themes get their system colors instead.
        public static readonly bool HighContrast = SystemInformation.HighContrast;
        public static readonly Color Bg = HighContrast ? SystemColors.Window : Color.FromArgb(30, 30, 30),
            Panel = HighContrast ? SystemColors.Window : Color.FromArgb(37, 37, 38),
            Field = HighContrast ? SystemColors.Window : Color.FromArgb(45, 45, 48),
            Text = HighContrast ? SystemColors.WindowText : Color.FromArgb(228, 228, 228),
            Muted = HighContrast ? SystemColors.WindowText : Color.FromArgb(170, 170, 170),
            Line = HighContrast ? SystemColors.WindowText : Color.FromArgb(62, 62, 66),
            Button = HighContrast ? SystemColors.Control : Color.FromArgb(55, 55, 58),
            ButtonText = HighContrast ? SystemColors.ControlText : Color.FromArgb(228, 228, 228),
            Select = HighContrast ? SystemColors.Highlight : Color.FromArgb(52, 58, 70),
            SelectText = HighContrast ? SystemColors.HighlightText : Color.FromArgb(240, 240, 240),
            Accent = HighContrast ? SystemColors.Highlight : Color.FromArgb(0, 95, 184),
            AccentText = HighContrast ? SystemColors.HighlightText : Color.White,
            Focus = HighContrast ? SystemColors.Highlight : Color.FromArgb(88, 166, 255),
            Yellow = HighContrast ? SystemColors.WindowText : Color.FromArgb(255, 200, 61),
            Green = HighContrast ? SystemColors.WindowText : Color.FromArgb(63, 185, 80),
            Red = HighContrast ? SystemColors.WindowText : Color.FromArgb(248, 81, 73),
            Blue = HighContrast ? SystemColors.WindowText : Color.FromArgb(88, 166, 255),
            Orange = HighContrast ? SystemColors.WindowText : Color.FromArgb(240, 136, 62),
            Grey = HighContrast ? SystemColors.GrayText : Color.FromArgb(150, 150, 150),
            AttentionBg = HighContrast ? SystemColors.Window : Color.FromArgb(58, 44, 24),
            ErrorBg = HighContrast ? SystemColors.Window : Color.FromArgb(64, 30, 30);

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
            if (b != null && !(b.Tag is string && (string)b.Tag == "primary")) Normal(b);
            if (root is Label || root is CheckBox || root is RadioButton) root.ForeColor = root.ForeColor == SystemColors.ControlText ? Text : root.ForeColor;
            if (root is Panel || root is UserControl || root is Form || root is TableLayoutPanel || root is FlowLayoutPanel || root is SplitContainer)
                if (root.BackColor == SystemColors.Control) root.BackColor = Bg;
        }

        public static Button Btn(string text, EventHandler click)
        {
            var b = new Button { Text = text, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Margin = new Padding(0, 0, 6, 6), Padding = new Padding(6, 2, 6, 2), FlatStyle = FlatStyle.Flat };
            b.FlatAppearance.BorderColor = Line;
            if (click != null) b.Click += click;
            // A visible keyboard focus ring (flat buttons otherwise show almost none on a dark background).
            b.GotFocus += (s, e) => { b.FlatAppearance.BorderColor = Focus; b.FlatAppearance.BorderSize = 2; };
            b.LostFocus += (s, e) => { b.FlatAppearance.BorderColor = b.Tag is string && (string)b.Tag == "primary" ? Accent : Line; b.FlatAppearance.BorderSize = 1; };
            return b;
        }

        // The one most useful action in a place: filled with the accent color.
        public static Button Primary(Button b)
        {
            b.Tag = "primary";
            b.FlatStyle = FlatStyle.Flat; b.UseVisualStyleBackColor = false;
            b.BackColor = Accent; b.ForeColor = AccentText; b.FlatAppearance.BorderColor = Accent;
            if (!b.Font.Bold) b.Font = new Font(SystemFonts.MessageBoxFont, FontStyle.Bold);   // the font the Queue and editor use
            return b;
        }

        public static Button Normal(Button b)
        {
            if (b.Tag is string && (string)b.Tag == "primary") b.Tag = null;
            b.FlatStyle = FlatStyle.Flat; b.UseVisualStyleBackColor = false;
            b.BackColor = Button; b.ForeColor = ButtonText; b.FlatAppearance.BorderColor = Line;
            if (b.Font.Bold && b.Parent != null) b.Font = b.Parent.Font;
            return b;
        }
    }

    // How a task looks everywhere (list, details, notifications): one label, color and icon per situation.
    public static class QLook
    {
        public static string Kind(QueueTask t)
        {
            var a = t.Last;
            switch (t.State)
            {
                case QState.Running:
                    if (a != null && a.InVsCode && (a.Phase == "needs_you" || a.Attention != "")) return "needs_you";
                    return a != null && a.InVsCode && (a.Phase == "opening" || a.Phase == "terminal" || a.Phase == "") ? "starting" : "running";
                case QState.NeedsAttention: return "attention";
                case QState.Blocked: return "blocked";
                case QState.WaitingUsage: return "usage";
                case QState.Scheduled: return t.Schedule == "after" ? "waiting" : "scheduled";
                case QState.Queued: return "ready";
                case QState.Draft: return "draft";
                case QState.Completed: return "done";
                case QState.Failed: return "failed";
                default: return t.Outcome == "skipped" || t.Outcome == "missed" ? "skipped" : "canceled";
            }
        }

        public static string Label(string kind)
        {
            switch (kind)
            {
                case "needs_you": return "Needs you"; case "starting": return "Starting"; case "running": return "Running";
                case "attention": return "Needs attention"; case "blocked": return "Blocked"; case "usage": return "Waiting for usage";
                case "waiting": return "Waiting"; case "scheduled": return "Scheduled"; case "ready": return "Ready"; case "draft": return "Draft";
                case "done": return "Completed"; case "failed": return "Failed"; case "skipped": return "Skipped"; default: return "Canceled";
            }
        }

        // Segoe UI Symbol glyphs, so the meaning doesn't depend on color alone.
        public static string Icon(string kind)
        {
            switch (kind)
            {
                case "needs_you": case "attention": return "!"; case "starting": case "running": return "▶"; case "blocked": return "⛔";
                case "usage": return "⏸"; case "waiting": return "⧗"; case "scheduled": return "◷"; case "ready": return "●"; case "draft": return "✎";
                case "done": return "✓"; case "failed": return "✕"; default: return "–";
            }
        }

        public static Color Color(string kind)
        {
            switch (kind)
            {
                case "needs_you": case "attention": case "blocked": case "failed": return QTheme.Red;
                case "starting": case "running": return QTheme.Yellow;
                case "usage": return QTheme.Orange;
                case "waiting": case "scheduled": case "ready": return QTheme.Blue;
                case "done": return QTheme.Green;
                default: return QTheme.Grey;
            }
        }

        // "in 2 h 5 min" / "12 min ago" / "just now".
        public static string Rel(DateTime utc, DateTime nowUtc)
        {
            if (utc == DateTime.MinValue) return "";
            var d = utc - nowUtc;
            bool future = d.TotalSeconds > 0;
            double m = Math.Abs(d.TotalMinutes);
            string s = m < 1 ? (future ? "in under a minute" : "just now") :
                m < 60 ? (int)m + " min" : m < 60 * 24 ? (int)(m / 60) + " h" + ((int)m % 60 > 0 && m < 600 ? " " + (int)m % 60 + " min" : "") : (int)(m / 1440) + " d";
            return m < 1 ? s : future ? "in " + s : s + " ago";
        }

        // "tomorrow 2:00 AM (in 9 h)": the actual local date and time, with the relative time next to it.
        public static string When(DateTime utc, DateTime nowUtc)
        {
            if (utc == DateTime.MinValue) return "";
            return Sched.LocalText(utc) + " (" + Rel(utc, nowUtc) + ")";
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
        readonly Label needsLabel, summary, next;
        readonly Wrap banner;
        readonly Button pause, newTask, bannerAction;
        readonly Wrap title, status, reason, attention, facts, note;
        readonly Panel attentionBox;
        readonly FlowLayoutPanel actions;
        readonly ComboBox attempts;
        readonly Button showPrompt, showActivity;
        readonly TextBox output;
        readonly Panel detail, empty;
        readonly Wrap emptyText;
        readonly Font bold, small, rowFont, titleFont, iconFont;
        readonly List<object> rows = new List<object>();   // string = section header, QueueTask = task
        readonly ContextMenuStrip more = new ContextMenuStrip();
        string selected = "", shownActionsKey = "", shownOutputKey = "";
        bool splitSet, historyOpen, promptTab = true;
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
            titleFont = new Font("Segoe UI", 16f * scale, FontStyle.Bold, GraphicsUnit.Pixel);
            iconFont = new Font("Segoe UI Symbol", 11f * scale, FontStyle.Bold, GraphicsUnit.Pixel);

            // ---- toolbar: New task (primary), pause/resume, at-a-glance summary and what runs next ----
            var top = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 2, Padding = new Padding(S(10), S(8), S(10), S(4)) };
            top.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize)); top.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            var buttons = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0) };
            newTask = QTheme.Primary(QTheme.Btn("+  New task", (s, e) => Edit(null)));
            pause = QTheme.Btn("⏸  Pause queue", (s, e) => eng.SetPaused(!eng.Data.Paused));
            buttons.Controls.Add(newTask); buttons.Controls.Add(pause);
            var info = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false, Margin = new Padding(S(8), 0, 0, 0) };
            var line1 = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0) };
            needsLabel = new Label { AutoSize = true, ForeColor = QTheme.Red, Font = bold, Margin = new Padding(0, S(1), 0, 0) };
            summary = new Label { AutoSize = true, ForeColor = QTheme.Text, Font = bold, Margin = new Padding(0, S(1), 0, 0) };
            line1.Controls.Add(needsLabel); line1.Controls.Add(summary);
            next = new Label { AutoSize = true, ForeColor = QTheme.Muted, Margin = new Padding(0, S(2), 0, 0) };
            info.Controls.Add(line1); info.Controls.Add(next);
            top.Controls.Add(buttons, 0, 0); top.Controls.Add(info, 1, 0);

            // ---- banner: paused / VS Code bridge missing / unreadable tasks file ----
            var bannerPanel = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 2, Padding = new Padding(S(10), S(6), S(10), S(6)), Visible = false, BackColor = QTheme.AttentionBg };
            bannerPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); bannerPanel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            banner = new Wrap { ForeColor = QTheme.Text, Dock = DockStyle.Fill, Margin = new Padding(0, S(4), 0, 0) };
            bannerAction = QTheme.Btn("", null); bannerAction.Margin = new Padding(S(8), 0, 0, 0);
            bannerAction.Click += (s, e) => { var a = bannerAction.Tag as Action; if (a != null) a(); };
            bannerPanel.Controls.Add(banner, 0, 0); bannerPanel.Controls.Add(bannerAction, 1, 0);
            banner.Tag = bannerPanel;

            var split = new SplitContainer { Dock = DockStyle.Fill, FixedPanel = FixedPanel.Panel1, SplitterWidth = S(5), BackColor = QTheme.Line };
            split.Panel1.BackColor = QTheme.Bg; split.Panel2.BackColor = QTheme.Bg;

            // ---- the list: NEEDS YOU, RUNNING, UPCOMING, DRAFTS, HISTORY (collapsed) ----
            list = new ListBox { Dock = DockStyle.Fill, DrawMode = DrawMode.OwnerDrawVariable, BorderStyle = BorderStyle.None, IntegralHeight = false, BackColor = QTheme.Bg, ForeColor = QTheme.Text, TabIndex = 0 };
            list.AccessibleName = "Scheduled tasks";
            list.MeasureItem += (s, e) => { e.ItemHeight = e.Index >= 0 && e.Index < rows.Count && rows[e.Index] is string ? S(28) : S(46); };
            list.DrawItem += DrawRow;
            list.SelectedIndexChanged += (s, e) =>
            {
                int i = list.SelectedIndex;
                var t = i >= 0 && i < rows.Count ? rows[i] as QueueTask : null;
                if (t != null) { if (selected != t.Id) note.Text = ""; selected = t.Id; ShowDetail(); }
            };
            list.MouseClick += (s, e) =>
            {
                int i = list.IndexFromPoint(e.Location);
                if (i >= 0 && i < rows.Count && rows[i] is string && ((string)rows[i]).StartsWith("HISTORY")) { historyOpen = !historyOpen; RefreshView(); }
            };
            list.KeyDown += (s, e) =>
            {
                int i = list.SelectedIndex;
                if (e.KeyCode == Keys.Enter && i >= 0 && i < rows.Count && rows[i] is string) { historyOpen = !historyOpen; RefreshView(); e.Handled = true; }
                else if (e.KeyCode == Keys.Enter && Selected() != null) { var p = actions.Controls.Count > 0 ? actions.Controls[0] as Button : null; if (p != null) p.PerformClick(); e.Handled = true; }
                else if (e.KeyCode == Keys.Apps || (e.KeyCode == Keys.F10 && e.Shift)) { ShowMore(); e.Handled = true; }
            };
            list.DoubleClick += (s, e) => { var t = Selected(); if (t != null && t.State != QState.Running) Edit(t); };
            split.Panel1.Controls.Add(list);

            // ---- details ----
            detail = new Panel { Dock = DockStyle.Fill, Padding = new Padding(S(14), S(10), S(14), S(10)), BackColor = QTheme.Bg, AutoScroll = false };
            output = new TextBox { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, WordWrap = true, Font = new Font("Consolas", Font.Size), BackColor = QTheme.Field, ForeColor = QTheme.Text, BorderStyle = BorderStyle.FixedSingle };
            output.AccessibleName = "Prompt and activity";
            var tabs = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, WrapContents = false, Padding = new Padding(0, S(8), 0, 0) };
            showPrompt = QTheme.Btn("Prompt", (s, e) => { promptTab = true; shownOutputKey = ""; ShowOutput(); });
            showActivity = QTheme.Btn("Activity and result", (s, e) => { promptTab = false; shownOutputKey = ""; ShowOutput(); });
            attempts = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, FlatStyle = FlatStyle.Flat, BackColor = QTheme.Field, ForeColor = QTheme.Text, Width = S(300), Margin = new Padding(S(6), S(1), 0, 0) };
            attempts.AccessibleName = "Run";
            attempts.SelectedIndexChanged += (s, e) => { shownOutputKey = ""; ShowOutput(); };
            tabs.Controls.Add(showPrompt); tabs.Controls.Add(showActivity); tabs.Controls.Add(attempts);
            facts = new Wrap { ForeColor = QTheme.Muted, Padding = new Padding(0, S(6), 0, S(2)) };
            note = new Wrap { ForeColor = QTheme.Yellow };
            actions = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, WrapContents = true, Padding = new Padding(0, S(6), 0, 0) };
            attentionBox = new Panel { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(S(10), S(6), S(10), S(6)), BackColor = QTheme.AttentionBg, Margin = new Padding(0, S(6), 0, 0) };
            attention = new Wrap { ForeColor = QTheme.Text, Dock = DockStyle.Top };
            attentionBox.Controls.Add(attention);
            var gap = new Panel { Dock = DockStyle.Top, Height = S(6) };
            reason = new Wrap { ForeColor = QTheme.Text };
            status = new Wrap { Font = bold };
            title = new Wrap { Font = titleFont, ForeColor = QTheme.Text };
            // Docked controls lay out from the last added: Fill first, then the top ones from the bottom up.
            foreach (Control c in new Control[] { output, tabs, facts, note, actions, gap, attentionBox, reason, status, title }) detail.Controls.Add(c);
            empty = new Panel { Dock = DockStyle.Fill, BackColor = QTheme.Bg, Padding = new Padding(S(20)) };
            emptyText = new Wrap { Dock = DockStyle.Top, ForeColor = QTheme.Muted };
            var emptyNew = QTheme.Primary(QTheme.Btn("+  New task", (s, e) => Edit(null)));
            var emptyButtons = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(0, S(10), 0, 0) };
            emptyButtons.Controls.Add(emptyNew);
            empty.Controls.Add(emptyButtons); empty.Controls.Add(emptyText);
            split.Panel2.Controls.Add(detail);
            split.Panel2.Controls.Add(empty);

            Controls.Add(split);
            Controls.Add(bannerPanel);
            Controls.Add(top);
            QTheme.Style(top); QTheme.Style(bannerPanel); QTheme.Style(tabs); QTheme.Style(emptyButtons);
            QTheme.Primary(newTask);
            newTask.MinimumSize = new Size(0, pause.PreferredSize.Height);
            // The list keeps a fixed share of the width; a narrow window gives way in the list so details stay readable.
            Layout += (s, e) =>
            {
                if (split.Width < S(500)) return;
                if (!splitSet) { splitSet = true; split.Panel1MinSize = S(200); split.SplitterDistance = Math.Min(S(340), (int)(split.Width * 0.4)); }
                if (split.Width - split.SplitterDistance < S(340)) split.SplitterDistance = Math.Max(S(200), split.Width - S(340));
            };

            eng.Changed += OnChanged;
            var clock = new Timer { Interval = 30000 };   // relative times ("in 9 h") stay current
            clock.Tick += (s, e) => { if (Visible) RefreshView(); };
            clock.Start();
            Disposed += (s, e) => clock.Dispose();
            RefreshView();
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (keyData == (Keys.Control | Keys.N)) { Edit(null); return true; }
            return base.ProcessCmdKey(ref msg, keyData);
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

        public void SelectTask(string id)
        {
            selected = id ?? "";
            var t = Selected();
            if (t != null && QState.Finished(t.State)) historyOpen = true;
            RefreshView();
        }

        public string SelectedId { get { return selected; } }
        public string[] RowTexts { get { var r = new List<string>(); foreach (var x in rows) r.Add(x is string ? (string)x : "  " + ((QueueTask)x).Title + " · " + QLook.Label(QLook.Kind((QueueTask)x))); return r.ToArray(); } }
        public string[] ActionTexts { get { var r = new List<string>(); foreach (Control c in actions.Controls) r.Add(c.Text); return r.ToArray(); } }
        public string DetailText { get { return title.Text + "\n" + status.Text + "\n" + reason.Text + "\n" + (attention.Text != "" ? attention.Text + "\n" : "") + facts.Text; } }
        public string SummaryText { get { return needsLabel.Text + " " + summary.Text + "\n" + next.Text; } }
        public string NoteText { get { return note.Text; } }

        // ---------- list ----------
        public void RefreshView()
        {
            var d = eng.Data;
            DateTime now = DateTime.UtcNow;
            var needs = new List<QueueTask>(); var running = new List<QueueTask>(); var upcoming = new List<QueueTask>(); var drafts = new List<QueueTask>(); var history = new List<QueueTask>();
            foreach (var t in d.Tasks)
            {
                string k = QLook.Kind(t);
                if (k == "needs_you" || k == "attention" || k == "blocked") needs.Add(t);
                else if (t.State == QState.Running) running.Add(t);
                else if (QState.Pending(t.State)) upcoming.Add(t);
                else if (t.State == QState.Draft) drafts.Add(t);
                else history.Add(t);
            }
            history.Sort((a, b) => b.FinishedUtc.CompareTo(a.FinishedUtc));
            var sel = Selected();
            if (sel != null && history.Contains(sel)) historyOpen = true;
            rows.Clear();
            Action<string, List<QueueTask>, bool> section = (name, items, open) =>
            {
                if (items.Count == 0) return;
                rows.Add(name + "  ·  " + items.Count + (name == "HISTORY" ? (open ? "   ▾" : "   ▸ show") : ""));
                if (open) foreach (var t in items) rows.Add(t);
            };
            section("NEEDS YOU", needs, true);
            section("RUNNING", running, true);
            section("UPCOMING", upcoming, true);
            section("DRAFTS", drafts, true);
            section("HISTORY", history, historyOpen);

            list.BeginUpdate();
            int topIndex = list.TopIndex;
            list.Items.Clear();
            int si = -1;
            for (int i = 0; i < rows.Count; i++) { list.Items.Add(rows[i] is string ? (object)rows[i] : ((QueueTask)rows[i]).Title); var t = rows[i] as QueueTask; if (t != null && t.Id == selected) si = i; }
            if (si < 0) { selected = ""; for (int i = 0; i < rows.Count; i++) if (rows[i] is QueueTask) { si = i; selected = ((QueueTask)rows[i]).Id; break; } }
            if (si >= 0) list.SelectedIndex = si;
            if (topIndex < list.Items.Count) list.TopIndex = topIndex;
            list.EndUpdate();

            // what's going on, in one line, and what runs next
            pause.Text = d.Paused ? "▶  Resume queue" : "⏸  Pause queue";
            var parts = new List<string>();
            needsLabel.Text = needs.Count > 0 ? "! " + needs.Count + " need" + (needs.Count == 1 ? "s" : "") + " you  ·" : "";
            needsLabel.Visible = needs.Count > 0;
            if (running.Count > 0) parts.Add(running.Count + " running");
            parts.Add(upcoming.Count + " upcoming");
            if (drafts.Count > 0) parts.Add(drafts.Count + " draft" + (drafts.Count == 1 ? "" : "s"));
            summary.Text = (d.Paused ? "Paused  ·  " : "") + string.Join("  ·  ", parts.ToArray());
            next.Text = NextText(upcoming, running, d.Paused, now);

            // banner: the one thing that keeps tasks from running as you'd expect
            var bp = (Control)banner.Tag;
            string bText = "", bButton = ""; Action bAct = null;
            if (eng.Store.Warning != "") bText = eng.Store.Warning;
            else if (!eng.Bridge.Installed() && HasVsWork(d))
            {
                bText = "VS Code bridge isn't installed, so tasks can't open in VS Code. Install it once (it adds a small \"Claude Status Bridge\" extension to VS Code).";
                bButton = "Install VS Code bridge"; bAct = InstallBridge;
            }
            else if (d.Paused && upcoming.Count > 0) { bText = "The queue is paused: nothing new starts. A task that is already running continues."; bButton = "▶  Resume queue"; bAct = () => eng.SetPaused(false); }
            bp.Visible = bText != "";
            banner.Text = bText;
            bannerAction.Visible = bButton != ""; bannerAction.Text = bButton; bannerAction.Tag = bAct;
            bp.BackColor = eng.Store.Warning != "" ? QTheme.ErrorBg : QTheme.AttentionBg;

            emptyText.Text = d.Tasks.Count > 0 ? "Select a task to see its status, prompt and result." :
                "Nothing scheduled yet.\r\n\r\nQueue prompts for Claude Code to run while you're away: at a time, after another task succeeds, or when your usage is available again.\r\n\r\n" +
                "Each task runs in a terminal in its project's VS Code window, so you can watch Claude work, answer its questions and keep the conversation going there. " +
                "This window is where you organize and monitor them.\r\n\r\nTasks only start while Claude Status is running (minimized to the tray is fine).";
            ShowDetail();
        }

        bool HasVsWork(QueueData d)
        {
            foreach (var t in d.Tasks) if (t.Runner != "headless" && (QState.Pending(t.State) || t.State == QState.Running || t.State == QState.NeedsAttention || t.State == QState.Draft)) return true;
            return d.Tasks.Count == 0;
        }

        string NextText(List<QueueTask> upcoming, List<QueueTask> running, bool paused, DateTime now)
        {
            if (paused) return "Nothing new starts until you resume the queue.";
            QueueTask soonest = null; DateTime at = DateTime.MaxValue;
            foreach (var t in upcoming)
            {
                DateTime w = NextTime(t);
                if (t.State == QState.Queued) { if (soonest == null || at != DateTime.MinValue) { soonest = t; at = DateTime.MinValue; } continue; }
                if (w != DateTime.MinValue && w < at) { soonest = t; at = w; }
            }
            if (soonest == null)
            {
                foreach (var t in upcoming) if (t.Schedule == "after") return "Next: \"" + Sched.FirstLine(t.Title, 50) + "\" after its predecessor succeeds.";
                return running.Count > 0 ? "Nothing else is waiting." : "Nothing is waiting to run.";
            }
            if (at == DateTime.MinValue) return "Next: \"" + Sched.FirstLine(soonest.Title, 50) + "\" " + (running.Count > 0 ? "when the running task finishes." : "now.");
            return "Next: \"" + Sched.FirstLine(soonest.Title, 50) + "\" " + QLook.When(at, now) + ".";
        }

        // When a waiting task is due (MinValue: no particular time).
        static DateTime NextTime(QueueTask t)
        {
            if (t.NextRetryUtc != DateTime.MinValue) return t.NextRetryUtc;
            if (t.Schedule == "at" && t.Attempts.Count == 0) return t.RunAtUtc;
            if (t.Schedule == "usage" && t.UsageResetUtc != DateTime.MinValue) return t.UsageResetUtc;
            return DateTime.MinValue;
        }

        void DrawRow(object sender, DrawItemEventArgs e)
        {
            if (e.Index < 0 || e.Index >= rows.Count) return;
            var g = e.Graphics;
            var r = e.Bounds;
            var head = rows[e.Index] as string;
            bool sel = (e.State & DrawItemState.Selected) != 0;
            bool focus = (e.State & DrawItemState.Focus) != 0 && list.Focused;
            if (head != null)
            {
                using (var b = new SolidBrush(QTheme.Bg)) g.FillRectangle(b, r);
                TextRenderer.DrawText(g, head, small, new Rectangle(r.Left + S(10), r.Top, r.Width - S(12), r.Height - S(4)), QTheme.Muted, TextFormatFlags.Left | TextFormatFlags.Bottom | TextFormatFlags.SingleLine);
                if (focus) ControlPaint.DrawFocusRectangle(g, Rectangle.Inflate(r, -2, -2), QTheme.Text, QTheme.Bg);
                return;
            }
            var t = (QueueTask)rows[e.Index];
            using (var b = new SolidBrush(sel ? QTheme.Select : QTheme.Bg)) g.FillRectangle(b, r);
            string kind = QLook.Kind(t);
            Color c = QLook.Color(kind);
            Color text = sel ? QTheme.SelectText : QTheme.Text, muted = sel ? QTheme.SelectText : QTheme.Muted;
            var flags = TextFormatFlags.Left | TextFormatFlags.Top | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding;
            TextRenderer.DrawText(g, QLook.Icon(kind), iconFont, new Rectangle(r.Left + S(8), r.Top + S(5), S(18), S(18)), c, TextFormatFlags.HorizontalCenter | TextFormatFlags.Top | TextFormatFlags.NoPadding);
            string st = QLook.Label(kind).ToUpperInvariant();
            int sw = TextRenderer.MeasureText(g, st, small, Size.Empty, TextFormatFlags.NoPadding | TextFormatFlags.SingleLine).Width;
            TextRenderer.DrawText(g, st, small, new Rectangle(r.Right - sw - S(8), r.Top + S(6), sw + S(2), S(16)), sel && QTheme.HighContrast ? text : c, flags);
            TextRenderer.DrawText(g, t.Title, rowFont, new Rectangle(r.Left + S(30), r.Top + S(4), r.Width - S(30) - sw - S(14), S(18)), text, flags);
            TextRenderer.DrawText(g, Line2(t, kind), small, new Rectangle(r.Left + S(30), r.Top + S(24), r.Width - S(36), S(16)), muted, flags);
            using (var p = new Pen(QTheme.Line)) g.DrawLine(p, r.Left + S(8), r.Bottom - 1, r.Right - S(8), r.Bottom - 1);
            if (focus) ControlPaint.DrawFocusRectangle(g, Rectangle.Inflate(r, -2, -2), text, sel ? QTheme.Select : QTheme.Bg);
        }

        static string Folder(string p) { try { return Path.GetFileName(Sched.NormPath(p)); } catch { return p; } }

        string Line2(QueueTask t, string kind)
        {
            DateTime now = DateTime.UtcNow;
            string when;
            switch (kind)
            {
                case "needs_you": when = "Answer Claude in VS Code"; break;
                case "starting": case "running": when = (t.Last != null && t.Last.InVsCode ? "VS Code · " : "background · ") + "started " + QLook.Rel(t.Last != null ? t.Last.StartedUtc : now, now); break;
                case "attention": case "blocked": case "failed": when = Sched.FirstLine(t.Reason, 120); break;
                case "done": when = (t.FinishedUtc != DateTime.MinValue ? QLook.Rel(t.FinishedUtc, now) + " · " : "") + (t.Summary != "" ? t.Summary : "finished"); break;
                case "canceled": case "skipped": when = Sched.FirstLine(t.Reason, 120); break;
                default:
                    DateTime w = NextTime(t);
                    when = w != DateTime.MinValue ? QLook.When(w, now) : ScheduleText(t);
                    break;
            }
            return Folder(t.Project) + "  ·  " + when;
        }

        string ScheduleText(QueueTask t)
        {
            DateTime now = DateTime.UtcNow;
            switch (t.Schedule)
            {
                case "at": return "At " + QLook.When(t.RunAtUtc, now) + (t.TimeZone != "" && t.TimeZone != TimeZoneInfo.Local.Id ? " (set in " + t.TimeZone + ")" : "");
                case "after": var p = eng.Data.Find(t.After); return "After \"" + (p != null ? p.Title : "(removed task)") + "\" completes successfully";
                case "usage": return "When usage is available again" + (t.UsageResetUtc != DateTime.MinValue ? " (you said it resets " + QLook.When(t.UsageResetUtc, now) + ")" : "");
                default: return "As soon as possible, in queue order";
            }
        }

        string ConversationText(QueueTask t)
        {
            string s;
            if (t.Conversation == "resume") s = "Continues an existing conversation";
            else if (t.Conversation == "predecessor") { var p = eng.Data.Find(t.After); s = "Continues the conversation of \"" + (p != null ? p.Title : "?") + "\""; }
            else s = "New conversation";
            string sid = t.SessionId != "" ? t.SessionId : t.Conversation == "resume" ? t.ResumeSession : "";
            return s + (sid != "" ? "  ·  session " + sid : "");
        }

        // ---------- detail ----------
        void ShowDetail()
        {
            var t = Selected();
            detail.Visible = t != null;
            empty.Visible = t == null;
            if (t == null) return;
            DateTime now = DateTime.UtcNow;
            string kind = QLook.Kind(t);
            var a = t.Last;
            title.Text = t.Title;
            string timing = "";
            if (t.State == QState.Running && a != null) timing = "started " + QLook.When(a.StartedUtc, now);
            else if (QState.Finished(t.State) && t.FinishedUtc != DateTime.MinValue) timing = "finished " + QLook.When(t.FinishedUtc, now);
            else { DateTime w = NextTime(t); if (w != DateTime.MinValue) timing = (t.State == QState.WaitingUsage ? "next try " : "starts ") + QLook.When(w, now); }
            status.Text = QLook.Icon(kind) + "  " + QLook.Label(kind).ToUpperInvariant() + (timing != "" ? "  ·  " + timing : "");
            status.ForeColor = QLook.Color(kind);
            // The engine's "Starts …" / "Next try …" just repeats the timing shown above.
            reason.Text = timing != "" && (t.Reason.StartsWith("Starts ") || t.Reason.StartsWith("Next try ")) ? "" : t.Reason;

            // what needs you, in a box that stands out (the actual request when Claude states it)
            string att = "";
            if (kind == "needs_you" && a != null)
                att = (a.Attention != "" ? a.Attention : "Claude is waiting for your input or approval.") + "\r\nRespond in VS Code: click Open in VS Code to go to its terminal \"" + a.Terminal + "\".";
            else if (kind == "starting" && a != null && a.Attention != "") att = a.Attention;
            else if (kind == "attention" || kind == "failed") att = t.Reason + (t.Denials.Count > 0 ? "\r\nBlocked actions: " + string.Join("; ", t.Denials) : "");
            else if (kind == "blocked") att = t.Reason;
            attentionBox.Visible = att != "";
            attentionBox.BackColor = kind == "failed" ? QTheme.ErrorBg : QTheme.AttentionBg;
            attention.Text = att;
            if (att != "" && (kind == "attention" || kind == "failed" || kind == "blocked")) reason.Text = "";

            var sb = new StringBuilder();
            sb.Append("Project:  ").Append(t.Project).Append("\r\n");
            sb.Append("Runs in:  ").Append(eng.Where(t)).Append("\r\n");
            sb.Append("Conversation:  ").Append(ConversationText(t)).Append("\r\n");
            sb.Append("When:  ").Append(ScheduleText(t)).Append("\r\n");
            sb.Append("Permissions:  ").Append(Sched.PermissionShort(t));
            if (t.Attempts.Count > 0) sb.Append("\r\nRuns:  ").Append(t.Attempts.Count).Append(t.Attempts.Count == 1 ? " run" : " runs");
            if (t.State == QState.Running && a != null && !a.InVsCode)
                sb.Append("\r\n\r\nThis task started in the background (claude -p) before it could run in VS Code. It keeps running there: let it finish, or stop it with Cancel run. Afterwards Open in VS Code resumes the same conversation without resending the prompt.");
            facts.Text = sb.ToString();

            string key = t.Id + "|" + t.State + "|" + kind + "|" + t.SessionId + "|" + t.Attempts.Count + "|" + eng.Data.Paused + "|" + t.Runner + "|" + eng.Bridge.Installed();
            if (key != shownActionsKey) { shownActionsKey = key; BuildActions(t, kind); }
            string akey = t.Id + "|" + t.Attempts.Count + "|" + (a != null ? a.Outcome : "");
            if ((string)attempts.Tag != akey)
            {
                attempts.Tag = akey;
                attempts.Items.Clear();
                for (int i = 0; i < t.Attempts.Count; i++)
                {
                    var x = t.Attempts[i];
                    attempts.Items.Add("Run " + x.N + "  ·  " + Sched.LocalText(x.StartedUtc) + "  ·  " + (x.Outcome != "" ? x.Outcome.Replace('_', ' ') : t.State == QState.Running && i == t.Attempts.Count - 1 ? "running" : "?") +
                        (x.InVsCode ? "  ·  VS Code" : "  ·  background") + (x.Mode == "resume" ? "  ·  resumed" : ""));
                }
                if (attempts.Items.Count > 0) attempts.SelectedIndex = attempts.Items.Count - 1;
                shownOutputKey = "";
                if (t.Attempts.Count == 0) promptTab = true;
            }
            attempts.Visible = !promptTab && attempts.Items.Count > 0;
            showActivity.Enabled = t.Attempts.Count > 0;
            ShowOutput();
        }

        void ShowOutput()
        {
            var t = Selected();
            if (t == null) return;
            QTheme.Normal(promptTab ? showActivity : showPrompt);
            QTheme.Primary(promptTab ? showPrompt : showActivity);
            attempts.Visible = !promptTab && attempts.Items.Count > 0;
            string text;
            if (promptTab || t.Attempts.Count == 0)
            {
                if (shownOutputKey == "prompt|" + t.Id + "|" + t.Revision) return;
                shownOutputKey = "prompt|" + t.Id + "|" + t.Revision;
                text = t.Prompt;
            }
            else
            {
                int idx = attempts.SelectedIndex;
                var a = idx >= 0 && idx < t.Attempts.Count ? t.Attempts[idx] : t.Last;
                bool live = t.State == QState.Running && a == t.Last;
                string key = t.Id + "|" + a.N + "|" + (live ? DateTime.UtcNow.Ticks / TimeSpan.TicksPerSecond / 3 : 0) + "|" + a.Outcome;
                if (key == shownOutputKey) return;
                shownOutputKey = key;
                string body;
                if (a.InVsCode)
                {
                    var act = eng.ActivityOf(t, a);
                    body = act.Text != "" ? act.Text : live ? "Claude's activity appears here as it works (read from its conversation transcript). Watch and answer it in the VS Code terminal." :
                        "(Claude Code hasn't saved this conversation where Claude Status can read it, so its activity and reply can't be shown here. Read them in the task's VS Code terminal.)";
                }
                else body = eng.OutputOf(t, t.Attempts.IndexOf(a));
                text = "Sent to Claude" + (a.Mode == "resume" ? " (resumed conversation " + Short(a.SessionId) + ")" : "") + ":\r\n" + (a.Prompt != "" ? a.Prompt : "(nothing: you continued the conversation yourself)") + "\r\n" + new string('─', 40) + "\r\n" + body;
                if (!live && t.ResultText != "" && a == t.Last && t.State != QState.Failed) text += "\r\n" + new string('─', 40) + "\r\nClaude's final message:\r\n" + t.ResultText;
            }
            text = text.Replace("\r\n", "\n").Replace("\n", "\r\n");
            if (output.Text == text) return;
            bool atEnd = output.SelectionStart >= output.TextLength - 2;
            output.Text = text;
            if (atEnd && t.State == QState.Running && !promptTab) { output.SelectionStart = output.TextLength; output.ScrollToCaret(); }
        }

        static string Short(string sid) { return sid.Length > 8 ? sid.Substring(0, 8) : sid; }

        // The first button is the primary action for the task's situation; the rest of the actions are under More.
        void BuildActions(QueueTask t, string kind)
        {
            actions.SuspendLayout();
            foreach (Control c in actions.Controls) c.Dispose();
            actions.Controls.Clear();
            more.Items.Clear();
            string id = t.Id;
            var a = t.Last;
            bool pending = QState.Pending(t.State), hasSession = Sched.IsGuid(t.SessionId), vs = t.Runner != "headless";
            bool runningHeadless = t.State == QState.Running && a != null && !a.InVsCode;
            bool primaryDone = false;
            Action<string, Action> button = (text, act) =>
            {
                var b = QTheme.Btn(text, (s, e) => Do(act));
                if (!primaryDone) { QTheme.Primary(b); primaryDone = true; }
                actions.Controls.Add(b);
            };
            Action<string, Action> menu = (text, act) => more.Items.Add(text, null, (s, e) => Do(act));
            Action openVs = () => OpenVs(id);
            bool bridgeMissing = vs && !eng.Bridge.Installed();

            switch (kind)
            {
                case "needs_you": case "starting": case "running":
                    if (!runningHeadless) button("Open in VS Code", openVs);
                    button("Cancel run…", () => CancelRun(t));
                    if (!runningHeadless) menu("Mark completed (stop tracking; Claude keeps running in VS Code)", () => { if (Confirm("Mark \"" + t.Title + "\" completed? The queue stops waiting for it and tasks after it can start. Claude keeps running in its VS Code terminal.")) eng.MarkCompleted(id); });
                    break;
                case "attention": case "failed":
                    if (t.Outcome == "vscode" && bridgeMissing) button("Install VS Code bridge", InstallBridge);
                    if (t.Outcome == "vscode") { button("Retry in VS Code", () => eng.Retry(id, false)); button("Run in background instead", () => RunInBackground(t)); }
                    else if (hasSession && t.Outcome != "conversation")
                    {
                        button("Open in VS Code", openVs);
                        button("Continue task", () => eng.Retry(id, true));
                        button("Mark completed", () => eng.MarkCompleted(id));
                    }
                    else button("Retry", () => eng.Retry(id, hasSession && t.Conversation != "new"));
                    break;
                case "blocked":
                    button("Edit…", () => Edit(t));
                    var p = eng.Data.Find(t.After);
                    if (p != null) button("Show \"" + Sched.FirstLine(p.Title, 30) + "\"", () => SelectTask(p.Id));
                    break;
                case "draft":
                    button("Add to queue", () => { string e = eng.Enqueue(id); if (e != null) { note.ForeColor = QTheme.Red; note.Text = e + " Edit the draft to fix it."; } });
                    button("Edit…", () => Edit(t));
                    break;
                case "done": case "canceled": case "skipped":
                    if (hasSession) button("Open in VS Code", openVs);
                    if (hasSession) button("Follow up…", () => FollowUp(t));
                    if (hasSession && kind != "done") button("Continue task", () => eng.Retry(id, true));
                    break;
                default:   // upcoming
                    button("Run now", () => eng.RunNow(id));
                    button("Edit…", () => Edit(t));
                    break;
            }
            if (bridgeMissing && kind != "attention" && kind != "failed" && t.State != QState.Running) menu("Install VS Code bridge", InstallBridge);
            if (t.State == QState.WaitingUsage || (pending && t.NextRetryUtc != DateTime.MinValue)) menu("Change next try…", () => PickRetry(t));
            if (pending || t.State == QState.Draft) { menu("Move up", () => eng.Move(id, -1)); menu("Move down", () => eng.Move(id, 1)); }
            if (pending) menu("Back to draft (won't run)", () => eng.ToDraft(id));
            if (pending || t.State == QState.Draft) menu("Skip (won't run)", () => eng.Skip(id));
            if (t.State != QState.Running && kind != "blocked" && kind != "draft" && pending == false) menu("Edit…", () => Edit(t));
            if (QState.Finished(t.State) || t.State == QState.NeedsAttention)
            {
                menu(t.Conversation == "new" ? "Run again (new conversation)…" : "Run again…", () => { if (Confirm("Send the original prompt again? Work already done may be repeated.")) eng.Retry(id, false); });
                if (t.State != QState.Completed && t.State != QState.Canceled) menu("Mark completed", () => eng.MarkCompleted(id));
            }
            if (t.State != QState.Running)
            {
                if (vs) menu("Run in background next time (no VS Code window)", () => RunInBackground(t, false));
                else menu("Run in VS Code next time", () => eng.SetRunner(id, "vscode"));
            }
            menu("Duplicate", () => { var c = eng.Duplicate(id); if (c != null) SelectTask(c.Id); });
            if (hasSession) menu("Copy session ID", () => { try { Clipboard.SetText(t.SessionId); } catch { } });
            menu("Open project folder", () => { if (Directory.Exists(t.Project)) Process.Start("explorer.exe", "\"" + t.Project + "\""); });
            if (t.State != QState.Running) menu("Remove…", () => { if (Confirm("Remove \"" + t.Title + "\" and its saved output? This can't be undone.")) { string e = eng.Remove(id); if (e != null) MessageBox.Show(this, e, "Claude Status"); } });
            if (QState.Finished(t.State)) menu("Clear all history…", ClearHistory);
            var moreBtn = QTheme.Btn("More  ▾", null);
            moreBtn.Click += (s, e) => ShowMore();
            moreBtn.AccessibleName = "More actions";
            actions.Controls.Add(moreBtn);
            QTheme.Style(actions);
            if (actions.Controls.Count > 1) QTheme.Primary((Button)actions.Controls[0]);
            more.BackColor = QTheme.Panel; more.ForeColor = QTheme.Text; more.ShowImageMargin = false;
            actions.ResumeLayout();
        }

        void ShowMore()
        {
            Control b = actions.Controls.Count > 0 ? actions.Controls[actions.Controls.Count - 1] : list;
            more.Show(b, new Point(0, b.Height));
        }

        void Do(Action act)
        {
            try { note.Text = ""; act(); }
            catch (Exception ex) { note.ForeColor = QTheme.Red; note.Text = ex.Message; }
        }

        void OpenVs(string id)
        {
            string msg = eng.OpenInVsCode(id);
            note.ForeColor = msg == null ? QTheme.Muted : QTheme.Yellow;
            note.Text = msg ?? "Opening it in VS Code…";
        }

        void RunInBackground(QueueTask t) { RunInBackground(t, true); }

        // An explicit choice: never a silent fallback. Background runs can't ask you anything.
        void RunInBackground(QueueTask t, bool retryNow)
        {
            if (!Confirm("Run \"" + t.Title + "\" in the background instead of VS Code?\r\n\r\nIt runs without a window (claude -p). Claude can't ask you anything there: actions outside the task's permissions are denied, and the task is marked Needs attention. " +
                "You can see its output here and continue its conversation in VS Code afterwards.")) return;
            eng.SetRunner(t.Id, "headless");
            if (retryNow) eng.Retry(t.Id, Sched.IsGuid(t.SessionId) && t.Attempts.Count > 0 && t.Last.Outcome != "vscode");
        }

        void InstallBridge()
        {
            string script = Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(eng.Bridge.Dir)), "vscode-bridge\\install-bridge.ps1");
            if (!File.Exists(script)) { note.ForeColor = QTheme.Red; note.Text = "The installer isn't there (" + script + "). Run install.ps1 again."; return; }
            note.ForeColor = QTheme.Muted; note.Text = "Installing the VS Code bridge…";
            Refresh();
            string output;
            int code = -1;
            try
            {
                var psi = new ProcessStartInfo("powershell.exe", "-NoProfile -ExecutionPolicy Bypass -File " + QueueEngine.QuoteArg(script)) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
                using (var p = Process.Start(psi)) { output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd(); p.WaitForExit(120000); code = p.ExitCode; }
            }
            catch (Exception e) { output = e.Message; }
            note.ForeColor = code == 0 ? QTheme.Green : QTheme.Red;
            note.Text = code == 0 ? "VS Code bridge installed. Open VS Code windows pick it up within a few seconds; then use Retry." : "Couldn't install the VS Code bridge: " + Sched.FirstLine(output, 300);
            shownActionsKey = "";
            RefreshView();
        }

        bool Confirm(string text) { return MessageBox.Show(this, text, "Claude Status", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) == DialogResult.OK; }

        void CancelRun(QueueTask t)
        {
            var a = t.Last;
            string where = a != null && a.InVsCode ? "Claude in its VS Code terminal is stopped (the terminal stays open so you can read it)." : "Claude and everything it started are stopped. Output so far is kept.";
            if (Confirm("Stop \"" + t.Title + "\" now?\r\n\r\n" + where + " File edits it already made are NOT undone. Tasks waiting for it will be blocked."))
                eng.Cancel(t.Id);
        }

        void PickRetry(QueueTask t)
        {
            using (var f = new Form { Text = "Next try", FormBorderStyle = FormBorderStyle.FixedDialog, StartPosition = FormStartPosition.CenterParent, MinimizeBox = false, MaximizeBox = false, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Font = Font, Padding = new Padding(S(10)) })
            {
                var flow = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false };
                flow.Controls.Add(new Label { Text = "Try again at (your local time):", AutoSize = true });
                var dt = new DateTimePicker { Format = DateTimePickerFormat.Custom, CustomFormat = "ddd d MMM yyyy   h:mm tt", Width = S(260), Value = t.NextRetryUtc > DateTime.UtcNow ? t.NextRetryUtc.ToLocalTime() : DateTime.Now.AddMinutes(30) };
                flow.Controls.Add(dt);
                flow.Controls.Add(new Label { Text = "Claude Status doesn't know your remaining usage; it only knows\r\nreset times Claude Code reports.", AutoSize = true, ForeColor = QTheme.Muted });
                var ok = QTheme.Primary(QTheme.Btn("Set", (s, e) => f.DialogResult = DialogResult.OK));
                flow.Controls.Add(ok);
                f.Controls.Add(flow); f.AcceptButton = ok;
                QTheme.Style(f); f.BackColor = QTheme.Bg;
                if (f.ShowDialog(this) == DialogResult.OK) eng.SetRetryTime(t.Id, Sched.LocalToUtc(dt.Value, TimeZoneInfo.Local));
            }
        }

        void ClearHistory()
        {
            int n = 0; foreach (var t in eng.Data.Tasks) if (QState.Finished(t.State)) n++;
            if (n == 0) return;
            if (Confirm("Delete " + n + " finished task(s) (completed, failed, canceled) and their saved output? Tasks that others still wait for are kept.\r\n\r\nFinished tasks are also deleted automatically after " + eng.HistoryDays + " days (\"queueHistoryDays\" in config.json)."))
                eng.ClearHistory();
        }

        void FollowUp(QueueTask t)
        {
            var n = TaskEditor.NewTask();
            n.Title = "Follow-up: " + t.Title; n.Project = t.Project; n.Runner = t.Runner;
            n.PermissionMode = t.PermissionMode; n.AllowCommands = new List<string>(t.AllowCommands); n.AllowCommit = t.AllowCommit; n.AllowPush = t.AllowPush; n.AllowDelete = t.AllowDelete; n.Model = t.Model;
            if (t.State == QState.Completed) { n.Conversation = "predecessor"; n.Schedule = "after"; n.After = t.Id; }
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

    // ---------- the task editor: 1 project, 2 prompt, 3 conversation, 4 when, 5 review (a normal, activatable window) ----------
    public class TaskEditor : Form
    {
        readonly QueueEngine eng;
        readonly QueueTask orig;
        readonly bool isNew;
        readonly float scale;
        readonly int revision;
        readonly TextBox title, prompt, sessionId, commands, model;
        readonly ComboBox project, sessions, after, mode, missed, whenKind, runner;
        readonly RadioButton cNew, cResume, cPred;
        readonly DateTimePicker atDate, atTime, reset;
        readonly CheckBox resetKnown, allowCommit, allowPush, allowDelete;
        readonly Button advancedToggle;
        readonly Wrap review, sessionNote, error, whenNote, promptInfo;
        readonly Panel adv, whenAt, whenAfter, whenUsage;
        readonly Label tzLabel;
        readonly List<QueueTask> afterChoices = new List<QueueTask>();
        List<SessionMeta> found = new List<SessionMeta>();
        readonly Control[] sessionRows;
        readonly string initial;
        bool saved;
        public string SavedId;

        int S(float v) { return (int)Math.Round(v * scale); }
        static readonly string[] Modes = { "acceptEdits", "dontAsk", "plan" };

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
            Text = isNew ? "New task" : running ? "Task (running)" : "Edit task";
            Font = SystemFonts.MessageBoxFont;
            StartPosition = FormStartPosition.CenterScreen;
            ShowInTaskbar = true; MinimizeBox = false;
            BackColor = QTheme.Bg; ForeColor = QTheme.Text;
            KeyPreview = true;
            Size = new Size(S(720), S(800));
            MinimumSize = new Size(S(540), S(480));
            Rectangle wa = Screen.FromPoint(Cursor.Position).WorkingArea;
            if (Height > wa.Height) Height = wa.Height;
            var bold = new Font(Font, FontStyle.Bold);
            var heading = new Font(Font.FontFamily, Font.Size * 1.15f, FontStyle.Bold);

            var body = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, AutoScroll = true, Padding = new Padding(S(16), S(10), S(20), S(10)), BackColor = QTheme.Bg };
            body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            Action<string, string> step = (n, label) => body.Controls.Add(new Label { Text = n + "   " + label, AutoSize = true, Font = heading, ForeColor = QTheme.Text, Margin = new Padding(0, S(14), 0, S(4)) });
            Action<Control> add = c => { c.Margin = new Padding(S(22), S(2), 0, S(2)); if (!(c is CheckBox) && !(c is RadioButton) && !(c is Button)) c.Dock = DockStyle.Fill; body.Controls.Add(c); };

            // 1. project
            step("1", "Project");
            var projRow = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, Height = S(28) };
            projRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); projRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            project = new ComboBox { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDown, AccessibleName = "Project folder" };
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var x in eng.Data.Tasks) if (x.Project != "" && seen.Add(Sched.NormPath(x.Project))) project.Items.Add(x.Project);
            project.Text = task.Project;
            project.TextChanged += (s, e) => { LoadSessions(); UpdateReview(); };
            var browse = QTheme.Btn("Browse…", (s, e) =>
            {
                using (var fb = new FolderBrowserDialog { Description = "Project folder Claude works in", SelectedPath = Directory.Exists(project.Text) ? project.Text : "" })
                    if (fb.ShowDialog(this) == DialogResult.OK) project.Text = fb.SelectedPath;
            });
            browse.Margin = new Padding(S(6), 0, 0, 0);
            projRow.Controls.Add(project, 0, 0); projRow.Controls.Add(browse, 1, 0);
            add(projRow);

            // 2. prompt
            step("2", "Prompt");
            prompt = new TextBox { Multiline = true, AcceptsReturn = true, AcceptsTab = false, ScrollBars = ScrollBars.Vertical, WordWrap = true, Height = S(190), Text = (task.Prompt ?? "").Replace("\r\n", "\n").Replace("\n", "\r\n"), Font = new Font("Consolas", Font.Size + 0.5f), AccessibleName = "Prompt" };
            prompt.TextChanged += (s, e) => UpdateReview();
            add(prompt);
            promptInfo = new Wrap { ForeColor = QTheme.Muted, Dock = DockStyle.None };
            add(promptInfo);
            var titleRow = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, Height = S(28) };
            titleRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize)); titleRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            title = new TextBox { Dock = DockStyle.Fill, Text = task.Title, AccessibleName = "Title" };
            title.TextChanged += (s, e) => UpdateReview();
            titleRow.Controls.Add(new Label { Text = "Title (optional)", AutoSize = true, ForeColor = QTheme.Muted, Margin = new Padding(0, S(6), S(8), 0) }, 0, 0);
            titleRow.Controls.Add(title, 1, 0);
            add(titleRow);

            // 3. conversation
            step("3", "Conversation");
            cNew = new RadioButton { Text = "New conversation", AutoSize = true, Checked = task.Conversation == "new" };
            cResume = new RadioButton { Text = "Continue an existing conversation in this project", AutoSize = true, Checked = task.Conversation == "resume" };
            cPred = new RadioButton { Text = "Continue the conversation of the task it runs after", AutoSize = true, Checked = task.Conversation == "predecessor" };
            sessions = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = S(480), AccessibleName = "Conversation to continue" };
            sessions.SelectedIndexChanged += (s, e) => { int i = sessions.SelectedIndex; if (i >= 0 && i < found.Count) { sessionId.Text = found[i].Id; cResume.Checked = true; } };
            sessionId = new TextBox { Text = task.ResumeSession, Width = S(300), AccessibleName = "Session ID" };
            sessionId.TextChanged += (s, e) => { if (sessionId.Text.Trim() != "") cResume.Checked = true; UpdateReview(); };
            sessionNote = new Wrap { ForeColor = QTheme.Muted };
            foreach (var r in new[] { cNew, cResume, cPred }) r.CheckedChanged += (s, e) => UpdateVisibility();
            add(cNew); add(cResume);
            sessionRows = new Control[] { Indent(sessions), Indent(Pair(new Label { Text = "or paste a session ID:", AutoSize = true, ForeColor = QTheme.Muted, Margin = new Padding(0, S(4), S(6), 0) }, sessionId)), sessionNote };
            foreach (var c in sessionRows) add(c);
            sessionNote.Margin = new Padding(S(40), S(2), 0, S(2));
            add(cPred);

            // 4. when
            step("4", "When");
            whenKind = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = S(420), AccessibleName = "When it runs" };
            whenKind.Items.AddRange(new object[] { "As soon as possible (in queue order)", "At a date and time", "After another task completes successfully", "When usage is available again" });
            whenKind.SelectedIndex = Math.Max(0, Array.IndexOf(new[] { "asap", "at", "after", "usage" }, task.Schedule));
            whenKind.SelectedIndexChanged += (s, e) => { UpdateVisibility(); UpdateReview(); };
            whenKind.Dock = DockStyle.None;
            body.Controls.Add(whenKind); whenKind.Margin = new Padding(S(22), S(2), 0, S(2));
            DateTime atLocal = task.RunAtUtc != DateTime.MinValue ? task.RunAtUtc.ToLocalTime() : DateTime.Now.Date.AddDays(1).AddHours(2);
            atDate = new DateTimePicker { Format = DateTimePickerFormat.Custom, CustomFormat = "ddd d MMM yyyy", Width = S(170), Value = atLocal.Date, AccessibleName = "Date" };
            atTime = new DateTimePicker { Format = DateTimePickerFormat.Custom, CustomFormat = "h:mm tt", ShowUpDown = true, Width = S(110), Value = atLocal, AccessibleName = "Time" };
            atDate.ValueChanged += (s, e) => UpdateReview(); atTime.ValueChanged += (s, e) => UpdateReview();
            tzLabel = new Label { AutoSize = true, ForeColor = QTheme.Muted, Margin = new Padding(S(6), S(4), 0, 0), Text = TimeZoneInfo.Local.IsDaylightSavingTime(DateTime.Now) ? TimeZoneInfo.Local.DaylightName : TimeZoneInfo.Local.StandardName };
            whenAt = Indent(Pair(atDate, atTime, tzLabel));
            after = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = S(440), AccessibleName = "Task it runs after" };
            foreach (var x in eng.Data.Tasks)
                if (x.Id != task.Id && (!QState.Finished(x.State) || x.Id == task.After))
                { afterChoices.Add(x); after.Items.Add(x.Title + "  ·  " + QLook.Label(QLook.Kind(x))); if (x.Id == task.After) after.SelectedIndex = after.Items.Count - 1; }
            after.SelectedIndexChanged += (s, e) => UpdateReview();
            whenAfter = Indent(after);
            resetKnown = new CheckBox { Text = "I know when it resets:", AutoSize = true, Checked = task.UsageResetUtc != DateTime.MinValue };
            reset = new DateTimePicker { Format = DateTimePickerFormat.Custom, CustomFormat = "ddd d MMM yyyy   h:mm tt", Width = S(240), Value = task.UsageResetUtc != DateTime.MinValue ? task.UsageResetUtc.ToLocalTime() : DateTime.Now.AddHours(1), AccessibleName = "Usage reset time" };
            reset.ValueChanged += (s, e) => { resetKnown.Checked = true; UpdateReview(); };
            resetKnown.CheckedChanged += (s, e) => UpdateReview();
            whenUsage = Indent(Pair(resetKnown, reset));
            whenNote = new Wrap { ForeColor = QTheme.Muted };
            add(whenAt); add(whenAfter); add(whenUsage); add(whenNote);

            // advanced (collapsed)
            advancedToggle = QTheme.Btn("▸  Advanced: where it runs, permissions, model, missed times", null);
            advancedToggle.Margin = new Padding(0, S(14), 0, S(2));
            body.Controls.Add(advancedToggle);
            adv = new Panel { AutoSize = true, Visible = false, Dock = DockStyle.Fill, Margin = new Padding(S(22), 0, 0, 0) };
            advancedToggle.Click += (s, e) => { adv.Visible = !adv.Visible; advancedToggle.Text = (adv.Visible ? "▾" : "▸") + advancedToggle.Text.Substring(1); };
            var at2 = new TableLayoutPanel { Dock = DockStyle.Top, ColumnCount = 2, AutoSize = true };
            at2.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize)); at2.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            Action<string, Control> arow = (label, c) =>
            {
                var l = new Label { Text = label, AutoSize = true, ForeColor = QTheme.Muted, Margin = new Padding(0, S(6), S(10), 0) };
                c.Dock = DockStyle.Fill; c.Margin = new Padding(0, S(3), 0, S(3));
                at2.Controls.Add(l); at2.Controls.Add(c);
            };
            runner = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, AccessibleName = "Where it runs" };
            runner.Items.Add("VS Code: a terminal in the project's window (watch it, answer it there)");
            runner.Items.Add("Background: no window (claude -p); Claude can't ask you anything");
            runner.SelectedIndex = task.Runner == "headless" ? 1 : 0;
            runner.SelectedIndexChanged += (s, e) => UpdateReview();
            arow("Runs in", runner);
            mode = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, AccessibleName = "Claude may" };
            foreach (var m in Modes) mode.Items.Add(Sched.ModeLabel(m));
            mode.SelectedIndex = Math.Max(0, Array.IndexOf(Modes, task.PermissionMode));
            mode.SelectedIndexChanged += (s, e) => UpdateReview();
            arow("Claude may", mode);
            commands = new TextBox { Multiline = true, AcceptsReturn = true, ScrollBars = ScrollBars.Vertical, Height = S(120), Text = string.Join("\r\n", task.AllowCommands), Font = new Font("Consolas", Font.Size), AccessibleName = "Allowed commands" };
            commands.TextChanged += (s, e) => UpdateReview();
            arow("Allowed commands", commands);
            arow("", new Wrap { ForeColor = QTheme.Muted, Text = "One command pattern per line (\"npm test *\"), allowed in both Bash and PowerShell; or a full Claude Code rule such as WebFetch(domain:docs.python.org). Read-only commands are always allowed." });
            allowCommit = new CheckBox { Text = "Allow git commit", AutoSize = true, Checked = task.AllowCommit };
            allowPush = new CheckBox { Text = "Allow git push (publishes your code)", AutoSize = true, Checked = task.AllowPush };
            allowDelete = new CheckBox { Text = "Allow deleting files (rm, Remove-Item, git clean, git reset --hard, …)", AutoSize = true, Checked = task.AllowDelete };
            foreach (var cb in new[] { allowCommit, allowPush, allowDelete }) cb.CheckedChanged += (s, e) => UpdateReview();
            arow("Also authorize", Stack(allowCommit, allowPush, allowDelete));
            model = new TextBox { Text = task.Model, AccessibleName = "Model" };
            model.TextChanged += (s, e) => UpdateReview();
            arow("Model", model);
            arow("", new Wrap { ForeColor = QTheme.Muted, Text = "Blank = your Claude Code default. For example sonnet, opus, or a full model name." });
            missed = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, AccessibleName = "If the time is missed" };
            missed.Items.Add("Run it once Claude Status is running again (after a 1-minute chance to skip)"); missed.Items.Add("Skip it");
            missed.SelectedIndex = task.IfMissed == "skip" ? 1 : 0;
            arow("If the time is missed", missed);
            adv.Controls.Add(at2);
            body.Controls.Add(adv);

            // 5. review
            step("5", "Review");
            review = new Wrap { ForeColor = QTheme.Text, BackColor = QTheme.Panel, Padding = new Padding(S(10), S(8), S(10), S(8)) };
            add(review);

            // buttons + inline validation
            var bottom = new Panel { Dock = DockStyle.Bottom, AutoSize = true, Padding = new Padding(S(16), S(4), S(16), S(10)), BackColor = QTheme.Bg };
            error = new Wrap { ForeColor = QTheme.Red, Dock = DockStyle.Top };
            var buttons = new FlowLayoutPanel { Dock = DockStyle.Top, FlowDirection = FlowDirection.RightToLeft, AutoSize = true, BackColor = QTheme.Bg };
            var cancel = QTheme.Btn(running ? "Close" : "Cancel", (s, e) => Close());
            buttons.Controls.Add(cancel);
            Button primary = null;
            if (!running)
            {
                bool draft = isNew || task.State == QState.Draft;
                primary = QTheme.Primary(QTheme.Btn(draft ? "Schedule" : "Save", (s, e) => SaveAs(false)));
                buttons.Controls.Add(primary);
                buttons.Controls.Add(QTheme.Btn(draft ? "Save as draft" : "Save as draft (won't run)", (s, e) => SaveAs(true)));
                buttons.Controls.Add(new Label { Text = "Ctrl+Enter schedules", AutoSize = true, ForeColor = QTheme.Muted, Margin = new Padding(0, S(8), S(12), 0) });
            }
            else buttons.Controls.Add(new Label { AutoSize = true, ForeColor = QTheme.Yellow, Margin = new Padding(0, S(6), S(12), 0), Text = "Running: the submitted prompt can't change. Duplicate the task to queue a revised version." });
            AcceptButton = null;   // Enter belongs to the prompt; nothing submits by accident
            CancelButton = cancel;
            bottom.Controls.Add(buttons); bottom.Controls.Add(error);

            Controls.Add(body);
            Controls.Add(bottom);
            QTheme.Style(this);
            if (primary != null) QTheme.Primary(primary);
            foreach (Control c in Controls) c.BackColor = c.BackColor == SystemColors.Control ? QTheme.Bg : c.BackColor;
            if (running) foreach (Control c in new Control[] { title, project, prompt, cNew, cResume, cPred, sessions, sessionId, whenKind, whenAt, whenAfter, whenUsage, adv }) c.Enabled = false;
            LoadSessions();
            UpdateVisibility();
            UpdateReview();
            initial = Snapshot();
            Shown += (s, e) => { if (project.Text.Trim() == "") project.Focus(); else { prompt.Focus(); prompt.Select(prompt.TextLength, 0); } };
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (keyData == (Keys.Control | Keys.Enter) && orig.State != QState.Running) { SaveAs(false); return true; }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        // Unsaved input isn't lost by accident: closing asks whether to keep it as a draft.
        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            base.OnFormClosing(e);
            if (e.Cancel || saved || orig.State == QState.Running || e.CloseReason != CloseReason.UserClosing || Snapshot() == initial) return;
            var r = MessageBox.Show(this, "Keep your changes as a draft?\r\n\r\nYes: save as a draft (it won't run).  No: discard them.", "Claude Status", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
            if (r == DialogResult.Cancel) { e.Cancel = true; return; }
            if (r == DialogResult.Yes && !SaveAs(true)) e.Cancel = true;
        }

        string Snapshot() { return QueueStore.Ser().Serialize(Collect(true)) + "|" + atDate.Value + atTime.Value + reset.Value; }

        Control Stack(params Control[] items)
        {
            var p = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false };
            foreach (var c in items) { c.Margin = new Padding(0, S(2), 0, S(2)); p.Controls.Add(c); }
            return p;
        }

        Panel Pair(params Control[] items)
        {
            var p = new FlowLayoutPanel { FlowDirection = FlowDirection.LeftToRight, AutoSize = true, WrapContents = false, Margin = new Padding(0) };
            foreach (var c in items) p.Controls.Add(c);
            return p;
        }

        Panel Indent(Control c) { var p = Pair(c); p.Padding = new Padding(S(18), 0, 0, 0); return p; }

        string WhenKey { get { return new[] { "asap", "at", "after", "usage" }[Math.Max(0, whenKind.SelectedIndex)]; } }

        // Only the fields for the chosen options are shown.
        void UpdateVisibility()
        {
            string w = WhenKey;
            whenAt.Visible = w == "at"; whenAfter.Visible = w == "after"; whenUsage.Visible = w == "usage";
            foreach (var c in sessionRows) c.Visible = cResume.Checked;
            cPred.Visible = w == "after" || cPred.Checked;
            if (w != "after" && cPred.Checked) cNew.Checked = true;
            whenNote.Text = w == "after" ? (afterChoices.Count == 0 ? "There's no other task to run after yet." : "It runs only after that task completes successfully (or you mark it completed). If that task fails, needs attention or is canceled, this one waits as Blocked.") :
                w == "usage" ? "It runs when no usage limit is known to be in effect. Claude Status only knows reset times that Claude Code reports (or that you enter here); it can't see your remaining usage." :
                w == "at" ? "Your local time. If Claude Status isn't running then (or the computer is asleep), it starts when it runs again, after a 1-minute chance to skip it." : "";
        }

        void LoadSessions()
        {
            sessions.Items.Clear();
            found = Directory.Exists(project.Text) ? Sessions.List(eng.ProjectsDir, project.Text, 40) : new List<SessionMeta>();
            foreach (var m in found) sessions.Items.Add(m.Display);
            for (int i = 0; i < found.Count; i++) if (found[i].Id == sessionId.Text.Trim()) sessions.SelectedIndex = i;
            sessionNote.Text = (found.Count == 0 && Directory.Exists(project.Text) ? "No saved conversations found for this folder. " : "") +
                "From Claude Code's saved conversations for this folder (titles are best effort). If that conversation is open in Claude Code when the task starts, the task waits instead of opening it twice.";
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
            t.Schedule = WhenKey;
            t.After = t.Schedule == "after" && after.SelectedIndex >= 0 ? afterChoices[after.SelectedIndex].Id : "";
            if (t.Schedule == "at")
            {
                DateTime d = atDate.Value.Date, tm = atTime.Value;
                DateTime wall = new DateTime(d.Year, d.Month, d.Day, tm.Hour, tm.Minute, 0);
                t.RunAtLocal = wall.ToString("yyyy-MM-dd HH:mm"); t.TimeZone = TimeZoneInfo.Local.Id;
                t.RunAtUtc = Sched.LocalToUtc(wall, TimeZoneInfo.Local);
            }
            else { t.RunAtUtc = DateTime.MinValue; t.RunAtLocal = ""; }
            t.UsageResetUtc = t.Schedule == "usage" && resetKnown.Checked ? Sched.LocalToUtc(reset.Value, TimeZoneInfo.Local) : DateTime.MinValue;
            t.Runner = runner.SelectedIndex == 1 ? "headless" : "vscode";
            t.PermissionMode = Modes[Math.Max(0, mode.SelectedIndex)];
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

        // A plain-language summary of what will happen, kept current as you edit.
        void UpdateReview()
        {
            if (review == null || runner == null) return;
            var t = Collect(true);
            DateTime now = DateTime.UtcNow;
            int lines = t.Prompt == "" ? 0 : t.Prompt.Split('\n').Length;
            promptInfo.Text = t.Prompt == "" ? "Exactly what you'd type to Claude. Line breaks and formatting are kept." : t.Prompt.Length + " characters · " + lines + " line" + (lines == 1 ? "" : "s") + " · sent exactly as written" +
                (t.Runner != "headless" && t.Prompt.Length > QueueEngine.MaxArgChars - 2000 ? " · too long to start in VS Code: shorten it or run it in the background (Advanced)" : "");
            string when;
            switch (t.Schedule)
            {
                case "at": when = "on " + QLook.When(t.RunAtUtc, now) + (t.RunAtUtc < now ? ": that's in the past, so it starts right away" : ""); break;
                case "after": when = t.After != "" ? "after \"" + afterChoices[after.SelectedIndex].Title + "\" completes successfully" : "after another task (choose which)"; break;
                case "usage": when = "when your usage is available again" + (t.UsageResetUtc != DateTime.MinValue ? " (from " + QLook.When(t.UsageResetUtc, now) + ")" : ""); break;
                default: when = "as soon as possible, after the tasks before it"; break;
            }
            string conv = t.Conversation == "resume" ? (t.ResumeSession != "" ? "continues conversation " + (t.ResumeSession.Length > 8 ? t.ResumeSession.Substring(0, 8) : t.ResumeSession) : "continues a conversation (choose which)") :
                t.Conversation == "predecessor" ? "continues the conversation of the task before it" : "starts a new conversation";
            string where = t.Runner == "headless" ? "in the background (no window)" : "in a terminal in " + (t.Project != "" ? Path.GetFileName(Sched.NormPath(t.Project)) : "the project") + "'s VS Code window, where you can watch and answer it";
            review.Text = "\"" + (t.Title != "" ? t.Title : "Untitled") + "\" runs " + when + ", " + where + ", and " + conv + ".\r\n\r\n" + Sched.PermissionSummary(t) +
                (t.Conversation != "new" && t.PermissionMode == "plan" ? " (Plan mode: Claude only reads and proposes.)" : "");
            error.Text = "";
        }

        bool SaveAs(bool draft)
        {
            var t = Collect(draft);
            string err = draft ? (t.Project.Trim() == "" && t.Prompt.Trim() == "" ? "Write at least the project or the prompt before saving a draft." : null) : eng.Validate(t, eng.Data.Tasks);
            if (!draft && err == null && t.Runner != "headless" && t.Prompt.Length > QueueEngine.MaxArgChars - 2000) err = "The prompt is too long to start in a VS Code terminal. Shorten it, or choose Background under Advanced.";
            if (err != null) { error.Text = err; FocusField(err); return false; }
            if (!draft && (t.AllowPush || t.AllowDelete) && !ConfirmRisk(t)) return false;
            if (t.Title == "") t.Title = "Untitled draft";
            err = eng.Save(t, revision);
            if (err != null) { error.Text = err; return false; }
            SavedId = t.Id; saved = true;
            Close();
            return true;
        }

        void FocusField(string err)
        {
            Control c = err.Contains("project") ? (Control)project : err.Contains("prompt") ? (Control)prompt : err.Contains("conversation") ? (Control)sessions : err.Contains("date") ? (Control)atDate : err.Contains("after") ? (Control)after : null;
            if (c != null && c.Visible && c.Enabled) c.Focus();
        }

        // Pushing and deleting without asking are the risky authorizations: confirmed once more, explicitly.
        bool ConfirmRisk(QueueTask t)
        {
            string what = (t.AllowPush ? "git push" : "") + (t.AllowPush && t.AllowDelete ? " and " : "") + (t.AllowDelete ? "deleting files" : "");
            return MessageBox.Show(this, "\"" + t.Title + "\" may use " + what + " without asking you, in\r\n" + t.Project + "\r\n\r\n" + Sched.PermissionSummary(t) +
                "\r\n\r\nThis is enforced by Claude Code's own permission rules. It limits which tools and commands Claude may use; it is not a sandbox.",
                "Schedule task", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning) == DialogResult.OK;
        }
    }
}
