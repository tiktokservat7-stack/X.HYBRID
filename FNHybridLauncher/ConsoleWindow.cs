using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace FNHybrid
{
    // Console flottante X.HYBRID, facon CMD : fond noir bleute, texte bleu
    // electrique, 75 % d'opacite, coins arrondis. Elle N'AFFICHE PAS les details
    // techniques de l'app : seulement les etapes importantes (prete, lancement de
    // Fortnite, jeu ouvert/ferme), poussees via ConsoleWindow.Say.
    //
    // Fermeture liee : fermer la console ferme l'application, et fermer
    // l'application ferme la console.
    public class ConsoleWindow : Form
    {
        public enum Kind { Info, Go, Ok, Warn }

        static ConsoleWindow _instance;
        static bool _appClosing;   // l'app se ferme : la console ne relance pas Exit

        readonly RichTextBox _out;
        readonly Panel _header;
        Label _status;

        [DllImport("user32.dll")] static extern bool ReleaseCapture();
        [DllImport("user32.dll")] static extern int SendMessage(IntPtr hWnd, int msg, int wParam, int lParam);

        // ---- API statique ----

        public static void ShowConsole()
        {
            try
            {
                if (_instance == null || _instance.IsDisposed) _instance = new ConsoleWindow();
                if (!_instance.Visible) _instance.Show();
                _instance.BringToFront();
            }
            catch (Exception) { }
        }

        // Pousse une ligne dans la console (rien d'autre n'y ecrit).
        public static void Say(string text, Kind kind)
        {
            var inst = _instance;
            if (inst == null || inst.IsDisposed) return;
            try
            {
                if (inst.InvokeRequired) inst.BeginInvoke((Action)(() => inst.Append(text, kind)));
                else inst.Append(text, kind);
            }
            catch (Exception) { }
        }

        public static void Say(string text) { Say(text, Kind.Info); }

        // Appelee par l'app qui se ferme : ferme la console sans relancer Exit.
        public static void CloseFromApp()
        {
            _appClosing = true;
            var inst = _instance;
            if (inst == null || inst.IsDisposed) return;
            try
            {
                if (inst.InvokeRequired) inst.BeginInvoke((Action)inst.Close);
                else inst.Close();
            }
            catch (Exception) { }
        }

        // ---- instance ----

        ConsoleWindow()
        {
            Text = "X.HYBRID — Console";
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            ShowInTaskbar = true;
            BackColor = Color.FromArgb(3, 6, 12);
            Opacity = 0.75;
            Size = new Size(760, 400);
            MinimumSize = new Size(440, 240);
            DoubleBuffered = true;

            var scr = Screen.PrimaryScreen.WorkingArea;
            Location = new Point(scr.Right - Width - 24, scr.Bottom - Height - 24);

            // ----- barre de titre (deplacable) -----
            _header = new Panel { Dock = DockStyle.Top, Height = 38, BackColor = Color.FromArgb(6, 12, 24) };
            _header.Paint += HeaderPaint;
            _header.MouseDown += DragStart;
            Controls.Add(_header);

            var close = new Label
            {
                Text = "✕", Dock = DockStyle.Right, Width = 46, TextAlign = ContentAlignment.MiddleCenter,
                ForeColor = Color.FromArgb(120, 150, 190), Font = new Font("Segoe UI", 11f, FontStyle.Bold),
                Cursor = Cursors.Hand
            };
            close.Click += delegate { Close(); };
            close.MouseEnter += delegate { close.ForeColor = Color.FromArgb(255, 96, 114); };
            close.MouseLeave += delegate { close.ForeColor = Color.FromArgb(120, 150, 190); };
            _header.Controls.Add(close);
            _header.Controls.SetChildIndex(close, 0);

            // ----- bandeau de statut -----
            var statusBar = new Panel { Dock = DockStyle.Top, Height = 24, BackColor = Color.FromArgb(4, 8, 16) };
            _status = new Label
            {
                Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(14, 0, 8, 0),
                ForeColor = Color.FromArgb(104, 126, 154), Font = new Font("Consolas", 8.5f, FontStyle.Regular),
                Text = "en veille"
            };
            statusBar.Controls.Add(_status);
            Controls.Add(statusBar);

            // ----- corps -----
            _out = new RichTextBox
            {
                Dock = DockStyle.Fill,
                BackColor = Color.FromArgb(3, 6, 12),
                ForeColor = Color.FromArgb(150, 180, 220),
                BorderStyle = BorderStyle.None,
                Font = new Font("Consolas", 10f, FontStyle.Regular),
                ReadOnly = true,
                WordWrap = true,
                ScrollBars = RichTextBoxScrollBars.Vertical,
                DetectUrls = false
            };
            _out.Padding = new Padding(12, 8, 12, 10);
            Controls.Add(_out);
            Controls.SetChildIndex(_out, 0);
            Controls.SetChildIndex(statusBar, 1);
            Controls.SetChildIndex(_header, 2);

            Append("X.HYBRID " + AppInfo.Badge + " prêt.", Kind.Ok);

            Resize += delegate { ApplyRegion(); };
            HandleCreated += delegate { ApplyRegion(); };
        }

        // Fermeture par l'utilisateur (croix ou Alt+F4) : on ferme toute l'app.
        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            base.OnFormClosing(e);
            if (_instance == this) _instance = null;
            if (!_appClosing)
            {
                _appClosing = true;
                try { Application.Exit(); } catch (Exception) { }
            }
        }

        void ApplyRegion()
        {
            try
            {
                using (var p = Round(new Rectangle(0, 0, Width, Height), 12))
                    Region = new Region(p);
            }
            catch (Exception) { }
        }

        static GraphicsPath Round(Rectangle r, int radius)
        {
            int d = radius * 2;
            var p = new GraphicsPath();
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        void DragStart(object sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left) return;
            ReleaseCapture();
            SendMessage(Handle, 0xA1, 0x2, 0); // glisser la fenetre
        }

        void HeaderPaint(object sender, PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;

            // point vert "en ligne"
            using (var br = new SolidBrush(Color.FromArgb(43, 217, 124)))
                g.FillEllipse(br, 14, 15, 9, 9);
            using (var halo = new SolidBrush(Color.FromArgb(70, 43, 217, 124)))
                g.FillEllipse(halo, 11, 12, 15, 15);

            using (var f = new Font("Consolas", 10.5f, FontStyle.Bold))
            using (var br = new SolidBrush(Color.FromArgb(0, 168, 255)))
                g.DrawString("X.HYBRID  CONSOLE", f, br, 32, 9);

            // ligne lumineuse bas du header
            using (var br = new SolidBrush(Color.FromArgb(0, 168, 255)))
                g.FillRectangle(br, 0, _header.Height - 2, _header.Width, 2);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            base.OnPaint(e);
            g.SmoothingMode = SmoothingMode.AntiAlias;

            // contour bleu fonce epais + liseret interieur plus clair (effet releve)
            using (var outer = new Pen(Color.FromArgb(10, 36, 86), 3f))
            using (var p = Round(new Rectangle(1, 1, Width - 3, Height - 3), 12))
                g.DrawPath(outer, p);

            using (var inner = new Pen(Color.FromArgb(0, 90, 190), 1f))
            using (var p = Round(new Rectangle(3, 3, Width - 7, Height - 7), 10))
                g.DrawPath(inner, p);
        }

        void Append(string text, Kind kind)
        {
            Color color;
            string tag;
            switch (kind)
            {
                case Kind.Go:   color = Color.FromArgb(0, 200, 255);  tag = "▸"; break;
                case Kind.Ok:   color = Color.FromArgb(43, 217, 124); tag = "✓"; break;
                case Kind.Warn: color = Color.FromArgb(255, 96, 114); tag = "!"; break;
                default:        color = Color.FromArgb(150, 180, 220); tag = "·"; break;
            }

            if (_out.TextLength > 100000)
                _out.Text = _out.Text.Substring(_out.TextLength - 40000);

            string stamp = DateTime.Now.ToString("HH:mm:ss");

            _out.SelectionStart = _out.TextLength; _out.SelectionLength = 0;
            _out.SelectionColor = Color.FromArgb(84, 104, 132);
            _out.AppendText(stamp + "  ");
            _out.SelectionColor = color;
            _out.AppendText(tag + " " + text + Environment.NewLine);
            _out.SelectionStart = _out.TextLength;
            _out.ScrollToCaret();

            if (_status != null) _status.Text = text;
        }
    }
}
