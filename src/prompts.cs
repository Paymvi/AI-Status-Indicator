// Claude Status - Prompts: reusable prompts you save once and copy whenever you need them. The Queue window's Prompts
// tab manages them; the phone shows them read-only (phone.cs, api/prompts). Kept in state\prompts.json. Nothing here
// runs, schedules or sends a prompt anywhere: it only stores text and puts it on the clipboard. C# 5 (Add-Type).
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Text;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace ClaudeStatus
{
    public class SavedPrompt
    {
        public string Id = "", Title = "", Text = "";
        public string Status = "";   // your own marker, set by hand: "", "green", "yellow" or "red"
        public DateTime UpdatedUtc;

        public static readonly string[] Statuses = { "", "green", "yellow", "red" };
    }

    // The saved prompts. Every change writes the whole file atomically before memory changes, so a failed write
    // changes nothing; a change to one prompt rewrites only that entry. Thread-safe: the phone server reads it too.
    public class PromptStore
    {
        readonly string path;
        readonly object gate = new object();
        List<SavedPrompt> items = new List<SavedPrompt>();
        bool locked;                     // the file was unreadable and couldn't be set aside: never overwrite it
        public string Notice = "";       // shown in the tab when the file couldn't be read
        public Func<DateTime> Now = () => DateTime.UtcNow;

        static JavaScriptSerializer Ser() { return new JavaScriptSerializer { MaxJsonLength = int.MaxValue }; }

        public PromptStore(string path)
        {
            this.path = path;
            if (!File.Exists(path)) return;
            try
            {
                var l = Ser().Deserialize<List<SavedPrompt>>(File.ReadAllText(path, Encoding.UTF8));
                if (l != null)
                    foreach (var p in l)
                    {
                        if (p == null) continue;
                        if (string.IsNullOrEmpty(p.Id)) p.Id = Guid.NewGuid().ToString("N");
                        p.Title = p.Title ?? ""; p.Text = p.Text ?? "";
                        if (Array.IndexOf(SavedPrompt.Statuses, p.Status) < 0) p.Status = "";   // older files have none
                        items.Add(p);
                    }
            }
            catch (Exception e)
            {
                // Keep the unreadable file as it is (a copy beside it), and start empty rather than lose it.
                string keep = path + ".unreadable-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".json";
                try { File.Copy(path, keep, true); Notice = "Your saved prompts file couldn't be read (" + e.Message + "). It was kept as " + keep + "."; }
                catch { locked = true; Notice = "Your saved prompts file (" + path + ") couldn't be read: " + e.Message + " Prompts can't be saved until it's fixed or removed."; }
            }
        }

        // Most recently updated first; equal times by title.
        public List<SavedPrompt> All()
        {
            var l = new List<SavedPrompt>();
            lock (gate) foreach (var p in items) l.Add(new SavedPrompt { Id = p.Id, Title = p.Title, Text = p.Text, Status = p.Status, UpdatedUtc = p.UpdatedUtc });
            l.Sort((a, b) => { int c = b.UpdatedUtc.CompareTo(a.UpdatedUtc); return c != 0 ? c : string.Compare(a.Title, b.Title, StringComparison.CurrentCultureIgnoreCase); });
            return l;
        }

        public static string Problem(string title, string text)
        {
            if (string.IsNullOrWhiteSpace(title)) return "Give the prompt a title.";
            if (string.IsNullOrWhiteSpace(text)) return "Write the prompt text.";
            return null;
        }

        public static bool Matches(SavedPrompt p, string query)
        {
            query = (query ?? "").Trim();
            return query == "" || p.Title.IndexOf(query, StringComparison.CurrentCultureIgnoreCase) >= 0 || p.Text.IndexOf(query, StringComparison.CurrentCultureIgnoreCase) >= 0;
        }

        // New prompt (id null) or a change to one; returns its id. The text is kept exactly as written.
        public string Save(string id, string title, string text)
        {
            string err = Problem(title, text);
            if (err != null) throw new ArgumentException(err);
            if (id == "") id = null;
            lock (gate)
            {
                if (locked) throw new IOException(Notice);
                var next = new List<SavedPrompt>();
                bool found = false;
                foreach (var p in items)
                    if (p.Id == id) { found = true; next.Add(new SavedPrompt { Id = id, Title = title.Trim(), Text = text, Status = p.Status, UpdatedUtc = Now() }); }
                    else next.Add(p);
                if (id != null && !found) throw new InvalidOperationException("This prompt was deleted.");
                if (id == null) { id = Guid.NewGuid().ToString("N"); next.Add(new SavedPrompt { Id = id, Title = title.Trim(), Text = text, UpdatedUtc = Now() }); }
                Write(next);
                items = next;
            }
            return id;
        }

        // Your status marker. Not an edit: the prompt keeps its place in the list (its update time doesn't change).
        public void SetStatus(string id, string status)
        {
            if (Array.IndexOf(SavedPrompt.Statuses, status) < 0) throw new ArgumentException("Unknown status: " + status);
            lock (gate)
            {
                if (locked) throw new IOException(Notice);
                var next = new List<SavedPrompt>();
                bool found = false;
                foreach (var p in items)
                    if (p.Id == id) { found = true; next.Add(new SavedPrompt { Id = id, Title = p.Title, Text = p.Text, Status = status, UpdatedUtc = p.UpdatedUtc }); }
                    else next.Add(p);
                if (!found) throw new InvalidOperationException("This prompt was deleted.");
                Write(next);
                items = next;
            }
        }

        public bool Delete(string id)
        {
            lock (gate)
            {
                if (locked) throw new IOException(Notice);
                var next = items.FindAll(p => p.Id != id);
                if (next.Count == items.Count) return false;
                Write(next);
                items = next;
            }
            return true;
        }

        void Write(List<SavedPrompt> l)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            Json.WriteAtomic(path, Json.Pretty(Ser().Serialize(l)));
        }
    }

    // The Prompts tab: a search box and a list of collapsible cards (title; click to show the full text), each with
    // Copy, Edit and Delete. Several can be open at once; which ones stays across searches and edits.
    public class PromptsView : UserControl
    {
        readonly PromptStore store;
        readonly float scale;
        readonly TextBox search;
        readonly Panel list;
        readonly Wrap emptyText, notice;
        readonly HashSet<string> open = new HashSet<string>();
        readonly Font titleFont;
        readonly ToolTip tips = new ToolTip();

        int S(float v) { return (int)Math.Round(v * scale); }

        public PromptsView(PromptStore store, float scale)
        {
            this.store = store;
            this.scale = scale;
            Font = SystemFonts.MessageBoxFont;
            BackColor = QTheme.Bg; ForeColor = QTheme.Text;
            titleFont = new Font(Font, FontStyle.Bold);

            var top = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 2, Padding = new Padding(S(10), S(4), S(10), S(4)) };
            top.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize)); top.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            var add = QTheme.Btn("+  New prompt", (s, e) => Edit(null));
            search = new TextBox { Dock = DockStyle.Fill, Margin = new Padding(S(4), S(2), 0, S(6)), AccessibleName = "Search prompts (titles and text)" };
            QTheme.Cue(search, "Search titles and text  (Ctrl+F)");
            search.TextChanged += (s, e) => Rebuild(null);
            search.KeyDown += (s, e) => { if (e.KeyCode == Keys.Escape && search.Text != "") { search.Text = ""; e.Handled = e.SuppressKeyPress = true; } };
            top.Controls.Add(add, 0, 0); top.Controls.Add(search, 1, 0);
            notice = new Wrap { ForeColor = QTheme.Yellow, Padding = new Padding(S(12), 0, S(12), S(6)) };
            list = new Panel { Dock = DockStyle.Fill, AutoScroll = true, Padding = new Padding(S(10), S(2), S(10), S(10)) };
            list.AccessibleName = "Saved prompts";
            emptyText = new Wrap { ForeColor = QTheme.Muted, Padding = new Padding(S(4), S(12), S(4), 0) };
            Controls.Add(list); Controls.Add(notice); Controls.Add(top);
            QTheme.Style(top); QTheme.Primary(add); QTheme.FocusTint(top);
            Disposed += (s, e) => tips.Dispose();
            Rebuild(null);
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (keyData == (Keys.Control | Keys.F)) { search.Focus(); search.SelectAll(); return true; }
            if (keyData == (Keys.Control | Keys.N)) { Edit(null); return true; }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        public void RefreshView() { Rebuild(null); }

        // For tests and screen readers' sake, what the list shows right now.
        public string SearchText { get { return search.Text; } set { search.Text = value; } }
        public string EmptyText { get { return emptyText.Parent != null ? emptyText.Text : ""; } }
        public bool IsOpen(string id) { return open.Contains(id); }

        // Rebuilt from the store; focus goes to the given prompt's header (after an edit) when it's shown.
        void Rebuild(string focusId)
        {
            notice.Text = store.Notice;
            var all = store.All();
            var shown = all.FindAll(p => PromptStore.Matches(p, search.Text));
            Button focus = null;
            list.SuspendLayout();
            var old = new List<Control>();
            foreach (Control c in list.Controls) if (c != emptyText) old.Add(c);
            list.Controls.Clear();
            foreach (var c in old) c.Dispose();
            if (shown.Count == 0)
            {
                emptyText.Text = all.Count == 0 ? "No saved prompts yet.\n\nClick + New prompt to save a prompt you use often. Then copy it from here whenever you need it."
                    : "No prompts match “" + search.Text.Trim() + "”. Try other words, or press Esc to clear the search.";
                list.Controls.Add(emptyText);
            }
            // Docked controls stack from the last added: add from the bottom up, so the newest is on top.
            for (int i = shown.Count - 1; i >= 0; i--)
            {
                list.Controls.Add(new Panel { Dock = DockStyle.Top, Height = S(6) });
                Button header;
                list.Controls.Add(Card(shown[i], out header));
                if (shown[i].Id == focusId) focus = header;
            }
            list.ResumeLayout();
            if (focus != null) { focus.Focus(); list.ScrollControlIntoView(focus.Parent.Parent); }
        }

        Control Card(SavedPrompt p, out Button header)
        {
            var card = new Panel { Dock = DockStyle.Top, Padding = new Padding(1), BackColor = QTheme.Panel };
            card.Paint += (s, e) => { using (var pen = new Pen(QTheme.Line)) e.Graphics.DrawRectangle(pen, 0, 0, card.Width - 1, card.Height - 1); };
            var head = new Panel { Dock = DockStyle.Top, BackColor = QTheme.Panel, Padding = new Padding(S(4), S(5), 0, S(5)) };
            var buttons = new FlowLayoutPanel { Dock = DockStyle.Right, AutoSize = true, WrapContents = false, BackColor = QTheme.Panel, Margin = new Padding(0) };
            var body = new Wrap { Text = p.Text, UseMnemonic = false, ForeColor = QTheme.Text, BackColor = QTheme.Panel, Padding = new Padding(S(26), S(2), S(12), S(10)), Visible = open.Contains(p.Id) };
            var toggle = QTheme.Btn("", null);
            var copy = QTheme.Btn("Copy", null);
            var edit = QTheme.Btn("Edit", (s, e) => Edit(p));
            var del = QTheme.Btn("Delete", (s, e) => Delete(p));
            copy.Click += (s, e) => Copy(copy, p);
            copy.AccessibleName = "Copy " + p.Title; edit.AccessibleName = "Edit " + p.Title; del.AccessibleName = "Delete " + p.Title;
            copy.MinimumSize = new Size(TextRenderer.MeasureText("Copied!", Font).Width + S(18), 0);   // "Copied!" doesn't shift the row
            toggle.AutoSize = false; toggle.Dock = DockStyle.Fill; toggle.TextAlign = ContentAlignment.MiddleLeft; toggle.AutoEllipsis = true; toggle.UseMnemonic = false;
            toggle.Font = titleFont;
            // The title fills what the buttons leave (a long one is cut with …); the buttons never move off the card.
            head.Height = head.Padding.Vertical + copy.PreferredSize.Height;
            foreach (var b in new[] { copy, edit, del }) { b.Margin = new Padding(S(4), 0, 0, 0); buttons.Controls.Add(b); }
            del.Margin = new Padding(S(4), 0, S(4), 0);
            // Your status dot, left of the title: click (or Enter / Space) to go none → green → yellow → red → none.
            var dot = QTheme.Btn("", null);
            dot.AutoSize = false; dot.Dock = DockStyle.Left; dot.Width = copy.PreferredSize.Height; dot.Margin = Padding.Empty;
            dot.Paint += (s, e) =>
            {
                int d = S(12), x = (dot.Width - d) / 2, y = (dot.Height - d) / 2;
                Color c = StatusColor(p.Status);
                e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                if (c == Color.Empty) using (var pen = new Pen(QTheme.Muted, Math.Max(1f, S(1.5f)))) e.Graphics.DrawEllipse(pen, x, y, d, d);
                else using (var br = new SolidBrush(c)) e.Graphics.FillEllipse(br, x, y, d, d);
            };
            Action showDot = () =>
            {
                string n = StatusName(p.Status);
                dot.AccessibleName = "Status of " + p.Title + ": " + n;
                dot.AccessibleDescription = "Press to change the status";
                tips.SetToolTip(dot, "Status: " + n + " (click to change)");
                dot.Invalidate();
            };
            dot.Click += (s, e) =>
            {
                string next = SavedPrompt.Statuses[(Array.IndexOf(SavedPrompt.Statuses, p.Status) + 1) % SavedPrompt.Statuses.Length];
                try { store.SetStatus(p.Id, next); }
                catch (Exception ex) { MessageBox.Show(FindForm(), "Couldn't change the status: " + ex.Message, "Claude Status", MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
                p.Status = next;
                showDot();
            };
            showDot();
            body.Padding = new Padding(body.Padding.Left + dot.Width, body.Padding.Top, body.Padding.Right, body.Padding.Bottom);   // the text lines up with the title
            head.Controls.Add(toggle); head.Controls.Add(buttons); head.Controls.Add(dot);   // Fill first, then the docked sides
            dot.TabIndex = 0; toggle.TabIndex = 1; buttons.TabIndex = 2;   // status, title, Copy, Edit, Delete
            card.Controls.Add(body); card.Controls.Add(head);
            QTheme.Style(card);
            toggle.BackColor = QTheme.Panel; toggle.FlatAppearance.BorderColor = QTheme.Panel;
            toggle.LostFocus += (s, e) => toggle.FlatAppearance.BorderColor = QTheme.Panel;   // (Btn restores Line)
            dot.BackColor = QTheme.Panel; dot.FlatAppearance.BorderColor = QTheme.Panel;
            dot.LostFocus += (s, e) => dot.FlatAppearance.BorderColor = QTheme.Panel;

            Action fit = () => card.Height = card.Padding.Vertical + head.Height + (body.Visible ? body.Height : 0);
            Action show = () =>
            {
                bool on = open.Contains(p.Id);
                toggle.Text = (on ? "▾   " : "▸   ") + p.Title;
                toggle.AccessibleName = p.Title + (on ? " (expanded)" : " (collapsed)");
                toggle.AccessibleDescription = on ? "Press to hide the prompt text" : "Press to show the prompt text";
                body.Visible = on;
                fit();
            };
            toggle.Click += (s, e) => { if (!open.Remove(p.Id)) open.Add(p.Id); show(); };
            head.SizeChanged += (s, e) => fit(); body.SizeChanged += (s, e) => fit();
            // When the list's scroll bar appears the card narrows while it is still laying itself out, and WinForms skips
            // that nested layout: size the docked children here so the buttons never end up under the scroll bar.
            card.Resize += (s, e) => { head.Width = body.Width = card.ClientSize.Width - card.Padding.Horizontal; };
            show();
            header = toggle;
            return card;
        }

        static string StatusName(string s) { return s == "" ? "none" : char.ToUpperInvariant(s[0]) + s.Substring(1); }
        static Color StatusColor(string s) { return s == "green" ? QTheme.Green : s == "yellow" ? QTheme.Yellow : s == "red" ? QTheme.Red : Color.Empty; }

        void Copy(Button b, SavedPrompt p)
        {
            try { Clipboard.SetText(p.Text, TextDataFormat.UnicodeText); }
            catch (Exception e)
            {
                MessageBox.Show(FindForm(), "Couldn't copy “" + p.Title + "” to the clipboard: " + e.Message + "\n\nAnother app may be using the clipboard. Try again in a moment.", "Claude Status", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            b.Text = "Copied!";
            var t = new Timer { Interval = 1500 };
            t.Tick += (s, e) => { t.Dispose(); if (!b.IsDisposed) b.Text = "Copy"; };
            t.Start();
        }

        void Edit(SavedPrompt p)
        {
            string id = p != null ? p.Id : null;
            using (var f = new PromptEditor(p, scale, (title, text) =>
            {
                try { id = store.Save(id, title, text); return null; }
                catch (Exception e) { return e.Message; }
            }))
            {
                if (f.ShowDialog(FindForm()) != DialogResult.OK) return;
            }
            Rebuild(id);
        }

        void Delete(SavedPrompt p)
        {
            if (MessageBox.Show(FindForm(), "Delete the prompt “" + p.Title + "”?\n\nThis can't be undone.", "Delete prompt", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
            try { store.Delete(p.Id); }
            catch (Exception e) { MessageBox.Show(FindForm(), "Couldn't delete the prompt: " + e.Message, "Claude Status", MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
            open.Remove(p.Id);
            Rebuild(null);
            search.Focus();
        }
    }

    // New / edit prompt: a title and a large multiline editor. Save keeps the dialog open with the reason if it fails.
    public class PromptEditor : Form
    {
        readonly TextBox title, prompt;
        readonly Label problem;
        readonly string startTitle, startText;

        public PromptEditor(SavedPrompt p, float scale, Func<string, string, string> save)
        {
            Func<float, int> S = v => (int)Math.Round(v * scale);
            Text = p == null ? "New prompt" : "Edit prompt";
            Font = QTheme.UiFont(this, scale);
            StartPosition = FormStartPosition.CenterParent;
            ShowInTaskbar = false; MinimizeBox = false; ShowIcon = false;
            ClientSize = new Size(S(680), S(540)); MinimumSize = new Size(S(420), S(340));
            startTitle = p != null ? p.Title : ""; startText = p != null ? p.Text : "";

            var grid = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, Padding = new Padding(S(14), S(10), S(14), S(10)) };
            for (int i = 0; i < 6; i++) grid.RowStyles.Add(new RowStyle(i == 3 ? SizeType.Percent : SizeType.AutoSize, 100));
            var tl = new Label { Text = "&Title", AutoSize = true, Margin = new Padding(0, 0, 0, S(3)) };
            title = new TextBox { Dock = DockStyle.Fill, Text = startTitle, MaxLength = 200, AccessibleName = "Title", Margin = new Padding(0, 0, 0, S(10)) };
            var pl = new Label { Text = "&Prompt  (Ctrl+Enter saves)", AutoSize = true, Margin = new Padding(0, 0, 0, S(3)) };
            prompt = new TextBox { Dock = DockStyle.Fill, Multiline = true, AcceptsReturn = true, ScrollBars = ScrollBars.Vertical, WordWrap = true, MaxLength = 0, Text = startText, AccessibleName = "Prompt text" };
            prompt.Font = QTheme.Mono(Font);
            problem = new Label { AutoSize = true, ForeColor = QTheme.Red, Margin = new Padding(0, S(6), 0, 0) };
            var buttons = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, Margin = new Padding(0, S(8), 0, 0) };
            var ok = QTheme.Btn("Save", null);
            var cancel = QTheme.Btn("Cancel", (s, e) => Close());
            buttons.Controls.Add(ok); buttons.Controls.Add(cancel);
            grid.Controls.Add(tl, 0, 0); grid.Controls.Add(title, 0, 1); grid.Controls.Add(pl, 0, 2); grid.Controls.Add(prompt, 0, 3); grid.Controls.Add(problem, 0, 4); grid.Controls.Add(buttons, 0, 5);
            Controls.Add(grid);
            BackColor = QTheme.Bg; ForeColor = QTheme.Text;
            QTheme.Style(this); QTheme.Primary(ok); QTheme.FocusTint(this);
            AcceptButton = ok; CancelButton = cancel;   // Enter in the title saves; Enter in the prompt is a new line

            ok.Click += (s, e) =>
            {
                string err = PromptStore.Problem(title.Text, prompt.Text) ?? save(title.Text, prompt.Text);
                if (err != null) { problem.Text = err; (string.IsNullOrWhiteSpace(title.Text) ? title : prompt).Focus(); return; }
                DialogResult = DialogResult.OK;
            };
            FormClosing += (s, e) =>
            {
                if (DialogResult == DialogResult.OK || (title.Text == startTitle && prompt.Text == startText)) return;
                if (MessageBox.Show(this, "Discard your changes to this prompt?", Text, MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) != DialogResult.Yes) e.Cancel = true;
            };
            Shown += (s, e) => { if (p == null) title.Focus(); else { prompt.Focus(); prompt.Select(0, 0); } };
            KeyPreview = true;
            KeyDown += (s, e) => { if (e.KeyCode == Keys.Enter && e.Control) { ok.PerformClick(); e.Handled = e.SuppressKeyPress = true; } };
        }
    }
}
