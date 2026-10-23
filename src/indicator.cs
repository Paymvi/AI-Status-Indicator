// Claude Status Indicator - tiny always-on-top window. Compiled at launch by indicator.ps1 via Add-Type
// (uses only .NET Framework, which ships with Windows). Watches state\sessions\*.json written by hook.ps1.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.IO;
using System.Media;
using System.Runtime.InteropServices;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace ClaudeStatus
{
    public class SessionInfo
    {
        public string Id = "", State = "", Detail = "", Project = "";
        public DateTime Updated;
    }

    public class IndicatorForm : Form
    {
        [DllImport("user32.dll")] static extern bool ReleaseCapture();
        [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr h, int msg, IntPtr w, IntPtr l);
        [DllImport("user32.dll")] static extern bool SetProcessDPIAware();
        [DllImport("user32.dll")] static extern bool DestroyIcon(IntPtr h);
        const int WS_EX_TOPMOST = 0x8, WS_EX_TOOLWINDOW = 0x80, WS_EX_NOACTIVATE = 0x08000000;
        const int WM_NCLBUTTONDOWN = 0xA1, HTCAPTION = 2;

        static readonly Color Bg = Color.FromArgb(30, 30, 30);
        static readonly Color Muted = Color.FromArgb(160, 160, 160);
        static readonly Color Yellow = Color.FromArgb(255, 200, 61);
        static readonly Color Green = Color.FromArgb(63, 185, 80);
        static readonly Color Red = Color.FromArgb(248, 81, 73);
        static readonly Color Grey = Color.FromArgb(128, 128, 128);

        readonly string root, sessionsDir, posPath;
        readonly Dictionary<string, object> cfg;
        readonly FileSystemWatcher watcher;
        readonly System.Windows.Forms.Timer debounce, slow;
        readonly NotifyIcon tray;
        readonly ContextMenuStrip menu;
        readonly Font smallFont = new Font("Segoe UI", 7.5f);
        readonly Font bigFont = new Font("Segoe UI", 9f, FontStyle.Bold);
        readonly float scale;

        Dictionary<string, SessionInfo> sessions = new Dictionary<string, SessionInfo>();
        SessionInfo current;
        bool initialized, hoverClose;
        string trayState;

        protected override bool ShowWithoutActivation { get { return true; } }
        protected override CreateParams CreateParams
        {
            get { var cp = base.CreateParams; cp.ExStyle |= WS_EX_TOPMOST | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE; return cp; }
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

        public IndicatorForm(string root)
        {
            this.root = root;
            sessionsDir = Path.Combine(root, "state\\sessions");
            posPath = Path.Combine(root, "position.txt");
            Directory.CreateDirectory(sessionsDir);
            cfg = LoadConfig(Path.Combine(root, "config.json"));

            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            StartPosition = FormStartPosition.Manual;
            BackColor = Bg;
            DoubleBuffered = true;
            Text = "Claude Status";
            using (var g = CreateGraphics()) scale = g.DpiX / 96f;
            Size = new Size(S(124), S(38));
            Location = LoadPosition();

            menu = new ContextMenuStrip();
            menu.Opening += (s, e) => BuildMenu();
            tray = new NotifyIcon { Visible = true, ContextMenuStrip = menu, Text = "Claude: IDLE" };
            tray.MouseClick += (s, e) => { if (e.Button == MouseButtons.Left) { Show(); BringToFrontNoActivate(); } };
            SetTrayIcon(Grey);

            watcher = new FileSystemWatcher(sessionsDir, "*.*") { SynchronizingObject = this, IncludeSubdirectories = false,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.CreationTime };
            FileSystemEventHandler h = (s, e) => { debounce.Stop(); debounce.Start(); };
            watcher.Changed += h; watcher.Created += h; watcher.Deleted += h;
            watcher.Renamed += (s, e) => { debounce.Stop(); debounce.Start(); };
            debounce = new System.Windows.Forms.Timer { Interval = 60 };
            debounce.Tick += (s, e) => { debounce.Stop(); Reload(); };
            slow = new System.Windows.Forms.Timer { Interval = 5000 }; // safety net + stale pruning
            slow.Tick += (s, e) => Reload();

            Reload();
            watcher.EnableRaisingEvents = true;
            slow.Start();
        }

        int S(float v) { return (int)Math.Round(v * scale); }

        // ---------- state ----------
        void Reload()
        {
            var next = new Dictionary<string, SessionInfo>();
            double staleHours = CfgNum("staleHours", 24);
            string[] files;
            try { files = Directory.GetFiles(sessionsDir, "*.json"); } catch { files = new string[0]; }
            var ser = new JavaScriptSerializer();
            foreach (var f in files)
            {
                string id = Path.GetFileNameWithoutExtension(f);
                try
                {
                    var d = ser.Deserialize<Dictionary<string, object>>(ReadShared(f));
                    var si = new SessionInfo { Id = id, State = Str(d, "state"), Detail = Str(d, "detail"), Project = Str(d, "project") };
                    DateTime u;
                    si.Updated = DateTime.TryParse(Str(d, "updated"), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out u)
                        ? u.ToUniversalTime() : File.GetLastWriteTimeUtc(f);
                    if ((DateTime.UtcNow - si.Updated).TotalHours > staleHours) { try { File.Delete(f); } catch { } continue; }
                    next[id] = si;
                }
                catch
                {
                    SessionInfo prev; // file mid-replace or unreadable: keep what we had
                    if (sessions.TryGetValue(id, out prev)) next[id] = prev;
                }
            }

            if (initialized)
                foreach (var si in next.Values)
                {
                    SessionInfo prev;
                    if (!sessions.TryGetValue(si.Id, out prev) || prev.State != si.State) Announce(si);
                }

            sessions = next;
            initialized = true;
            current = null;
            foreach (var si in sessions.Values) if (current == null || si.Updated > current.Updated) current = si;

            string st = current == null ? "IDLE" : current.State;
            if (st != trayState) { trayState = st; SetTrayIcon(ColorFor(st)); }
            string tip = "Claude: " + Label(st) + (current != null && current.Project != "" ? " (" + current.Project + ")" : "");
            tray.Text = tip.Length > 63 ? tip.Substring(0, 63) : tip;
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
            string where = si.Project != "" ? si.Project : "Claude Code";
            switch (si.State)
            {
                case "DONE":
                    if (CfgBool("soundOnDone", true)) SystemSounds.Asterisk.Play();
                    if (CfgBool("toastOnDone", false)) Toast("Claude is done", where, ToolTipIcon.Info);
                    break;
                case "NEEDS_YOU":
                    if (CfgBool("soundOnNeedsYou", true)) SystemSounds.Exclamation.Play();
                    if (CfgBool("toastOnNeedsYou", true)) Toast("Claude needs you", where + " is waiting for your input or approval", ToolTipIcon.Warning);
                    break;
                case "ERROR":
                    if (CfgBool("soundOnError", true)) SystemSounds.Hand.Play();
                    if (CfgBool("toastOnError", true)) Toast("Claude stopped with an error", where + (si.Detail != "" ? ": " + si.Detail : ""), ToolTipIcon.Error);
                    break;
            }
        }

        void Toast(string title, string text, ToolTipIcon icon)
        {
            try { tray.ShowBalloonTip(5000, title, text, icon); } catch { }
        }

        static string Label(string st)
        {
            switch (st) { case "WORKING": return "WORKING"; case "DONE": return "DONE"; case "NEEDS_YOU": return "NEEDS YOU"; case "ERROR": return "ERROR"; default: return "IDLE"; }
        }

        static Color ColorFor(string st)
        {
            switch (st) { case "WORKING": return Yellow; case "DONE": return Green; case "NEEDS_YOU": case "ERROR": return Red; default: return Grey; }
        }

        // ---------- drawing ----------
        Rectangle CloseRect { get { return new Rectangle(ClientSize.Width - S(17), S(1), S(16), S(16)); } }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            string st = current == null ? "IDLE" : current.State;
            Color c = ColorFor(st);
            using (var pen = new Pen(Color.FromArgb(150, c), Math.Max(1, S(1))))
                g.DrawRectangle(pen, 0, 0, ClientSize.Width - 1, ClientSize.Height - 1);

            string head = "Claude";
            if (sessions.Count > 1) head += "  (" + sessions.Count + " sessions)";
            else if (current != null && current.Project != "") head += " \u00B7 " + current.Project;
            TextRenderer.DrawText(g, head, smallFont, new Rectangle(S(7), S(2), ClientSize.Width - S(26), S(15)), Muted,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
            TextRenderer.DrawText(g, "\u00D7", smallFont, CloseRect, hoverClose ? Color.White : Grey,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);

            g.SmoothingMode = SmoothingMode.AntiAlias;
            int dot = S(9);
            using (var b = new SolidBrush(c)) g.FillEllipse(b, S(8), S(24) - dot / 2, dot, dot);
            TextRenderer.DrawText(g, Label(st), bigFont, new Rectangle(S(22), S(16), ClientSize.Width - S(24), S(18)), c,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
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
        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left)
            {
                if (CloseRect.Contains(e.Location)) { Close(); return; }
                ReleaseCapture();
                SendMessage(Handle, WM_NCLBUTTONDOWN, (IntPtr)HTCAPTION, IntPtr.Zero); // native drag; returns when released
                SavePosition();
            }
            else if (e.Button == MouseButtons.Right) menu.Show(this, e.Location);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            bool h = CloseRect.Contains(e.Location);
            if (h != hoverClose) { hoverClose = h; Invalidate(CloseRect); }
            Cursor = h ? Cursors.Hand : Cursors.SizeAll;
        }

        protected override void OnMouseLeave(EventArgs e) { if (hoverClose) { hoverClose = false; Invalidate(CloseRect); } }

        void BuildMenu()
        {
            menu.Items.Clear();
            if (sessions.Count == 0) menu.Items.Add(new ToolStripMenuItem("No active sessions") { Enabled = false });
            foreach (var si in sessions.Values)
            {
                string name = (si.Project != "" ? si.Project : "session") + " \u2014 " + Label(si.State) + "  (" + si.Updated.ToLocalTime().ToString("HH:mm") + ")";
                menu.Items.Add(new ToolStripMenuItem(name) { Enabled = false });
            }
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Show window", null, (s, e) => { Show(); BringToFrontNoActivate(); });
            menu.Items.Add("Reset position", null, (s, e) => { Location = DefaultPosition(); SavePosition(); Show(); });
            menu.Items.Add("Exit", null, (s, e) => Close());
        }

        void BringToFrontNoActivate() { TopMost = false; TopMost = true; }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            SavePosition();
            watcher.EnableRaisingEvents = false;
            tray.Visible = false;
            tray.Dispose();
            base.OnFormClosed(e);
        }

        // ---------- persistence (window position + config only; nothing sensitive) ----------
        Point DefaultPosition()
        {
            var wa = Screen.PrimaryScreen.WorkingArea;
            // Bottom-right, but left of where Windows toasts appear so they don't cover it.
            return new Point(wa.Right - Width - S(400), wa.Bottom - Height - S(12));
        }

        Point LoadPosition()
        {
            try
            {
                var p = File.ReadAllText(posPath).Trim().Split(',');
                var pt = new Point(int.Parse(p[0]), int.Parse(p[1]));
                foreach (var scr in Screen.AllScreens)
                    if (scr.WorkingArea.IntersectsWith(new Rectangle(pt, Size))) return pt;
            }
            catch { }
            return DefaultPosition();
        }

        void SavePosition()
        {
            try { File.WriteAllText(posPath, Location.X + "," + Location.Y); } catch { }
        }

        static Dictionary<string, object> LoadConfig(string path)
        {
            try { return new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(File.ReadAllText(path)) ?? new Dictionary<string, object>(); }
            catch { return new Dictionary<string, object>(); }
        }

        bool CfgBool(string k, bool def) { object v; return cfg.TryGetValue(k, out v) && v is bool ? (bool)v : def; }
        double CfgNum(string k, double def) { object v; try { return cfg.TryGetValue(k, out v) ? Convert.ToDouble(v, CultureInfo.InvariantCulture) : def; } catch { return def; } }
        static string Str(Dictionary<string, object> d, string k) { object v; return d.TryGetValue(k, out v) && v != null ? v.ToString() : ""; }
    }
}
