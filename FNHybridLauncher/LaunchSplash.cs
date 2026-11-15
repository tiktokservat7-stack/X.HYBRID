using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

namespace FNHybrid
{
    // Écran de lancement X.HYBRID. Pendant le démarrage du jeu, il se place
    // par-dessus la fenêtre de démarrage de Fortnite (« INITIALISATION… ») pour
    // la remplacer visuellement, sans toucher aux fichiers du jeu.
    internal sealed class LaunchSplash : Form
    {
        readonly Bitmap _artwork;
        readonly Timer _animation;
        readonly Stopwatch _elapsed = Stopwatch.StartNew();
        bool _waitingForGame;
        bool _coveringGame;

        public LaunchSplash(bool animate)
        {
            using (var stream = typeof(LaunchSplash).Assembly.GetManifestResourceStream("LobbyHero.jpg"))
            using (var image = Image.FromStream(stream))
                _artwork = new Bitmap(image);

            Text = "X.HYBRID";
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.CenterScreen;
            ShowInTaskbar = false;
            DoubleBuffered = true;
            AutoScaleMode = AutoScaleMode.None;
            BackColor = Color.FromArgb(3, 10, 22);
            ClientSize = new Size(640, 360);
            UpdateRegion();

            _animation = new Timer { Interval = 30 };
            _animation.Tick += (sender, args) => Invalidate();
            if (animate) _animation.Start();
        }

        // pas d'activation : la fenêtre ne vole pas le focus au jeu
        protected override bool ShowWithoutActivation { get { return true; } }

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ExStyle |= 0x08000000 | 0x80; // WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW
                return cp;
            }
        }

        public void WaitingForGame()
        {
            _waitingForGame = true;
            Invalidate();
        }

        // Recouvre exactement la fenêtre de démarrage de Fortnite.
        public void CoverGame(Rectangle screenBounds)
        {
            if (IsDisposed) return;
            _waitingForGame = true;
            if (!_coveringGame)
            {
                _coveringGame = true;
                TopMost = true;
            }
            if (Bounds != screenBounds)
            {
                Bounds = screenBounds;
                UpdateRegion();
            }
            Invalidate();
        }

        void UpdateRegion()
        {
            var old = Region;
            if (_coveringGame) Region = null;
            else using (var outline = Theme.Round(ClientRectangle, 16)) Region = new Region(outline);
            if (old != null) old.Dispose();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            int w = ClientSize.Width, h = ClientSize.Height;
            float k = Math.Min(w / 640f, h / 360f);

            // image en mode « cover » (pas de déformation quand on recouvre Fortnite)
            float scale = Math.Max(w / (float)_artwork.Width, h / (float)_artwork.Height);
            float iw = _artwork.Width * scale, ih = _artwork.Height * scale;
            g.DrawImage(_artwork, (w - iw) / 2f, (h - ih) / 2f, iw, ih);

            var footer = new Rectangle(0, (int)(h * 0.55f), w, h - (int)(h * 0.55f) + 1);
            using (var shade = new LinearGradientBrush(footer, Color.Transparent, Color.FromArgb(235, 3, 10, 22), 90f))
                g.FillRectangle(shade, footer);

            float left = 28 * k;
            using (var title = Theme.UI(21 * k, FontStyle.Bold))
            using (var status = Theme.UI(10 * k))
            {
                Theme.Text(g, "X.HYBRID", title, Theme.Ink, new RectangleF(left, h - 124 * k, w - 2 * left, 40 * k));
                string message = _coveringGame
                    ? MainForm.T("Initialisation de Fortnite…", "Initializing Fortnite…")
                    : _waitingForGame
                        ? MainForm.T("En attente de Fortnite…", "Waiting for Fortnite…")
                        : MainForm.T("Préparation du lancement…", "Preparing launch…");
                int dots = (int)(_elapsed.Elapsed.TotalSeconds * 2) % 4;
                if (_animation.Enabled) message = message.TrimEnd('…') + new string('.', dots);
                Theme.Text(g, message, status, Theme.Ink, new RectangleF(left + 2, h - 77 * k, w - 2 * left, 26 * k));
            }

            PaintShieldBreak(g, new PointF(w * 0.76f, h * 0.36f), h * 0.40f,
                _animation.Enabled ? (float)_elapsed.Elapsed.TotalSeconds : 99f);

            var track = new RectangleF(left + 1, h - 38 * k, w - 2 * left - 2, Math.Max(3f, 4 * k));
            using (var background = new SolidBrush(Color.FromArgb(54, 87, 125)))
                g.FillRectangle(background, track);
            float segment = track.Width * 0.28f;
            float offset = _animation.Enabled
                ? (float)(_elapsed.Elapsed.TotalSeconds % 1.4 / 1.4) * (track.Width + segment) - segment
                : 0;
            var saved = g.Save();
            g.SetClip(track);
            using (var highlight = new SolidBrush(Theme.Bright))
                g.FillRectangle(highlight, new RectangleF(track.X + offset, track.Y, segment, track.Height));
            g.Restore(saved);
        }

        // ================= animation : bouclier anti-cheat qui se casse =================
        // 0-0.5 s apparition · 0.5-1.2 s rond rouge barré · 1.2-1.7 s fissures
        // 1.7-2.8 s le bouclier éclate en morceaux · ensuite le rond rouge s'efface

        static GraphicsPath Shield(PointF c, float s)
        {
            float w = s * 0.78f, top = c.Y - s * 0.5f, bottom = c.Y + s * 0.5f;
            var p = new GraphicsPath();
            p.AddBezier(c.X, top, c.X + w * 0.25f, top + s * 0.08f, c.X + w * 0.42f, top + s * 0.06f, c.X + w * 0.5f, top + s * 0.04f);
            p.AddBezier(c.X + w * 0.5f, top + s * 0.04f, c.X + w * 0.54f, c.Y + s * 0.15f, c.X + w * 0.3f, c.Y + s * 0.36f, c.X, bottom);
            p.AddBezier(c.X, bottom, c.X - w * 0.3f, c.Y + s * 0.36f, c.X - w * 0.54f, c.Y + s * 0.15f, c.X - w * 0.5f, top + s * 0.04f);
            p.AddBezier(c.X - w * 0.5f, top + s * 0.04f, c.X - w * 0.42f, top + s * 0.06f, c.X - w * 0.25f, top + s * 0.08f, c.X, top);
            p.CloseFigure();
            return p;
        }

        static void DrawShieldBody(Graphics g, PointF c, float s)
        {
            using (var path = Shield(c, s))
            {
                using (var fill = new LinearGradientBrush(new RectangleF(c.X - s / 2, c.Y - s / 2, s, s),
                    Color.FromArgb(70, 84, 104), Color.FromArgb(22, 30, 44), 90f))
                    g.FillPath(fill, path);
                using (var rim = new Pen(Color.FromArgb(230, 236, 244), Math.Max(2f, s * 0.035f)) { LineJoin = LineJoin.Round })
                    g.DrawPath(rim, path);
            }
            using (var f = Theme.UI(Math.Max(6f, s * 0.17f), FontStyle.Bold))
                Theme.Text(g, "AC", f, Color.FromArgb(235, 240, 248),
                    new RectangleF(c.X - s / 2, c.Y - s * 0.24f, s, s * 0.4f), StringAlignment.Center, StringAlignment.Center, false);
        }

        static readonly float[] ShardAngles = { 0f, 47f, 101f, 148f, 203f, 251f, 304f };

        void PaintShieldBreak(Graphics g, PointF c, float s, float t)
        {
            const float appear = 0.5f, ringEnd = 1.2f, crackEnd = 1.7f, burstEnd = 2.8f;
            var saved = g.Save();
            g.SmoothingMode = SmoothingMode.AntiAlias;

            if (t < crackEnd)
            {
                // apparition
                float a = Math.Min(1f, t / appear);
                float sc = 0.7f + 0.3f * a;
                var st = g.Save();
                g.TranslateTransform(c.X, c.Y);
                g.ScaleTransform(sc, sc);
                g.TranslateTransform(-c.X, -c.Y);
                // tremblement pendant les fissures
                if (t > ringEnd)
                {
                    float shake = s * 0.015f * (float)Math.Sin(t * 90);
                    g.TranslateTransform(shake, -shake * 0.5f);
                }
                DrawShieldBody(g, c, s);

                // fissures
                if (t > ringEnd)
                {
                    float q = Math.Min(1f, (t - ringEnd) / (crackEnd - ringEnd));
                    using (var crack = new Pen(Color.FromArgb(240, 255, 255, 255), Math.Max(1.5f, s * 0.018f)))
                    using (var clip = Shield(c, s))
                    {
                        g.SetClip(clip);
                        foreach (var ang in ShardAngles)
                        {
                            double r = (ang + 9) * Math.PI / 180;
                            float len = s * 0.62f * q;
                            var mid = new PointF(c.X + (float)Math.Cos(r) * len * 0.5f + s * 0.04f, c.Y + (float)Math.Sin(r) * len * 0.5f);
                            var end = new PointF(c.X + (float)Math.Cos(r) * len, c.Y + (float)Math.Sin(r) * len);
                            g.DrawLines(crack, new[] { c, mid, end });
                        }
                        g.ResetClip();
                    }
                }
                g.Restore(st);
            }
            else if (t < burstEnd)
            {
                // éclatement : chaque morceau part vers l'extérieur en tournant
                float q = (t - crackEnd) / (burstEnd - crackEnd);
                float ease = 1f - (1f - q) * (1f - q);
                int alpha = (int)(255 * (1f - q));
                for (int i = 0; i < ShardAngles.Length; i++)
                {
                    float a0 = ShardAngles[i], a1 = ShardAngles[(i + 1) % ShardAngles.Length];
                    if (a1 <= a0) a1 += 360f;
                    double mid = (a0 + a1) / 2 * Math.PI / 180;
                    using (var wedge = new GraphicsPath())
                    {
                        wedge.AddPie(c.X - s * 1.2f, c.Y - s * 1.2f, s * 2.4f, s * 2.4f, a0, a1 - a0);
                        var st = g.Save();
                        g.TranslateTransform((float)Math.Cos(mid) * s * 0.9f * ease, (float)Math.Sin(mid) * s * 0.9f * ease + s * 0.4f * q * q);
                        g.TranslateTransform(c.X, c.Y);
                        g.RotateTransform((i % 2 == 0 ? 1 : -1) * 70f * ease);
                        g.TranslateTransform(-c.X, -c.Y);
                        using (var region = new Region(wedge))
                        using (var shield = Shield(c, s))
                        {
                            region.Intersect(shield);
                            g.SetClip(region, CombineMode.Replace);
                            using (var fill = new SolidBrush(Color.FromArgb(alpha, 52, 64, 82)))
                                g.FillPath(fill, shield);
                            using (var rim = new Pen(Color.FromArgb(alpha, 230, 236, 244), Math.Max(2f, s * 0.035f)))
                                g.DrawPath(rim, shield);
                            using (var edge = new Pen(Color.FromArgb(alpha, 255, 255, 255), Math.Max(1.5f, s * 0.02f)))
                                g.DrawPath(edge, wedge);
                            g.ResetClip();
                        }
                        g.Restore(st);
                    }
                }
                // éclair blanc au moment de la casse
                if (q < 0.25f)
                    using (var flash = new SolidBrush(Color.FromArgb((int)(150 * (1 - q / 0.25f)), 255, 255, 255)))
                        g.FillEllipse(flash, c.X - s * 0.7f, c.Y - s * 0.7f, s * 1.4f, s * 1.4f);
            }

            // rond rouge barré
            if (t > appear)
            {
                float ringQ = Math.Min(1f, (t - appear) / (ringEnd - appear));
                float fade = t < burstEnd ? 1f : Math.Max(0f, 1f - (t - burstEnd) / 0.8f);
                if (fade > 0f)
                {
                    float pulse = t > crackEnd && t < burstEnd ? 1f + 0.08f * (float)Math.Sin((t - crackEnd) * 18) : 1f;
                    float r = s * 0.68f * pulse;
                    int al = (int)(255 * fade);
                    using (var red = new Pen(Color.FromArgb(al, 232, 36, 46), Math.Max(3f, s * 0.08f)) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                    {
                        g.DrawArc(red, c.X - r, c.Y - r, 2 * r, 2 * r, -135f, 360f * ringQ);
                        float slash = Math.Max(0f, (ringQ - 0.6f) / 0.4f);
                        if (slash > 0f)
                        {
                            double d = Math.PI / 4;
                            var p0 = new PointF(c.X - (float)Math.Cos(d) * r, c.Y - (float)Math.Sin(d) * r);
                            var p1 = new PointF(c.X + (float)Math.Cos(d) * r, c.Y + (float)Math.Sin(d) * r);
                            g.DrawLine(red, p0, new PointF(p0.X + (p1.X - p0.X) * slash, p0.Y + (p1.Y - p0.Y) * slash));
                        }
                    }
                }
            }
            g.Restore(saved);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _animation.Dispose();
                _artwork.Dispose();
            }
            base.Dispose(disposing);
        }

        // ================= détection des fenêtres de Fortnite =================

        internal struct GameWindows
        {
            public bool Running;
            public Rectangle? Splash;   // fenêtre de démarrage (SplashScreenClass)
            public bool MainVisible;    // fenêtre du jeu (UnrealWindow)
        }

        static readonly string[] GameProcesses =
        {
            "FortniteClient-Win64-Shipping", "FortniteClient-Win64-Shipping_EAC_EOS",
            "FortniteClient-Win64-Shipping_EAC", "FortniteClient-Win64-Shipping_BE",
            "GameClient-Win64-Shipping"
        };

        internal static GameWindows FindGameWindows()
        {
            var result = new GameWindows();
            var pids = new HashSet<uint>();
            foreach (var name in GameProcesses)
            {
                try
                {
                    foreach (var p in Process.GetProcessesByName(name))
                    {
                        pids.Add((uint)p.Id);
                        p.Dispose();
                    }
                }
                catch (Exception) { }
            }
            if (pids.Count == 0) return result;
            result.Running = true;

            var cls = new StringBuilder(64);
            EnumWindows((hwnd, lp) =>
            {
                uint pid;
                GetWindowThreadProcessId(hwnd, out pid);
                if (!pids.Contains(pid) || !IsWindowVisible(hwnd)) return true;
                RECT r;
                if (!GetWindowRect(hwnd, out r)) return true;
                var rect = Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom);
                if (rect.Width < 160 || rect.Height < 90) return true;
                cls.Length = 0;
                GetClassName(hwnd, cls, cls.Capacity);
                string c = cls.ToString();
                if (c == "SplashScreenClass") result.Splash = rect;
                else if (c == "UnrealWindow") result.MainVisible = true;
                return true;
            }, IntPtr.Zero);
            return result;
        }

        delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

        [StructLayout(LayoutKind.Sequential)]
        struct RECT { public int Left, Top, Right, Bottom; }

        [DllImport("user32.dll")] static extern bool EnumWindows(EnumWindowsProc cb, IntPtr lParam);
        [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
        [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr hwnd);
        [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr hwnd, out RECT r);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr hwnd, StringBuilder sb, int max);
    }
}
