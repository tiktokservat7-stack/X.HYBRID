using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace FNHybrid
{
    // Fenêtre de mise à jour : proposition, téléchargement avec progression,
    // puis installation et redémarrage automatique dans la nouvelle version.
    public class UpdateWindow : Form
    {
        enum Phase { Ask, Working, Done, Error }

        struct Spot
        {
            public RectangleF R;
            public string Id;
            public Action Act;
        }

        readonly UpdateInfo _info;
        readonly float _u;
        readonly Dictionary<string, Font> _fonts = new Dictionary<string, Font>();
        readonly List<Spot> _spots = new List<Spot>();
        Phase _phase = Phase.Ask;
        string _hot;
        int _pct;
        string _error;
        CancellationTokenSource _cts;
        System.Windows.Forms.Timer _closeTimer;

        public bool Applied;

        public UpdateWindow(UpdateInfo info)
        {
            _info = info;

            using (var g = Graphics.FromHwnd(IntPtr.Zero)) _u = g.DpiX / 96f;
            if (_u < 1f) _u = 1f;
            if (_u > 1.75f) _u = 1.75f;

            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.CenterParent;
            ShowInTaskbar = false;
            BackColor = Theme.Window;
            DoubleBuffered = true;
            KeyPreview = true;
            Text = AppInfo.Name;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            ClientSize = new Size(S(560), S(404));
            Region = new Region(Theme.Round(new Rectangle(0, 0, Width, Height), S(12)));
        }

        int S(int v) { return (int)Math.Round(v * _u); }
        float S(float v) { return v * _u; }

        Font F(float pt, FontStyle style)
        {
            string key = pt.ToString("0.##") + "|" + (int)style;
            Font f;
            if (_fonts.TryGetValue(key, out f)) return f;
            f = new Font("Segoe UI", pt * _u, style, GraphicsUnit.Point);
            _fonts[key] = f;
            return f;
        }

        void Hit(RectangleF r, string id, Action act)
        {
            var s = new Spot();
            s.R = r;
            s.Id = id;
            s.Act = act;
            _spots.Add(s);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(Theme.Window);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;

            _spots.Clear();

            int W = ClientSize.Width;
            int H = ClientSize.Height;
            int pad = S(26);

            var frame = new RectangleF(1, 1, W - 2, H - 2);
            using (var p = Theme.Round(frame, S(12)))
            {
                using (var br = new LinearGradientBrush(frame, Color.FromArgb(10, 20, 34), Color.FromArgb(5, 10, 18), 90f))
                    g.FillPath(br, p);
                using (var glow = new Pen(Color.FromArgb(40, Theme.Bright), S(6))) g.DrawPath(glow, p);
                using (var pen = new Pen(Color.FromArgb(220, 24, 86, 152), Math.Max(1f, S(1.2f)))) g.DrawPath(pen, p);
            }

            Theme.Text(g, "MISE À JOUR", F(7.8f, FontStyle.Bold), Theme.Bright,
                new RectangleF(pad, S(22), S(240), S(16)));

            var close = new RectangleF(W - pad - S(28), S(18), S(28), S(28));
            if (_phase == Phase.Ask || _phase == Phase.Error)
            {
                if (_hot == "up:close")
                    using (var p = Theme.Round(close, S(6)))
                    using (var br = new SolidBrush(Color.FromArgb(24, 34, 52)))
                        g.FillPath(br, p);
                Theme.Icon(g, "close", new RectangleF(close.X + S(6), close.Y + S(6), S(16), S(16)),
                    _hot == "up:close" ? Theme.Ink : Color.FromArgb(180, 206, 236), 1.8f);
                Hit(close, "up:close", Later);
            }

            // ---- titre : X.HYBRID + nouvelle version
            var title = new RectangleF(pad, S(44), W - pad * 2, S(40));
            Theme.Text(g, "X.HYBRID", F(19f, FontStyle.Bold | FontStyle.Italic), Theme.Ink, title,
                StringAlignment.Near, StringAlignment.Center, false);
            float brandW = g.MeasureString("X.HYBRID", F(19f, FontStyle.Bold | FontStyle.Italic)).Width;
            var ver = new RectangleF(title.X + brandW + S(8), title.Y + S(4), S(110), S(30));
            Theme.Text(g, "V" + _info.Version, F(19f, FontStyle.Bold | FontStyle.Italic), Theme.Bright, ver,
                StringAlignment.Near, StringAlignment.Center, false);

            Theme.Text(g, "Version installée " + AppInfo.Badge + "   →   nouvelle version V" + _info.Version,
                F(9f, FontStyle.Regular), Theme.Dim,
                new RectangleF(pad, S(88), W - pad * 2, S(20)));

            if (_phase == Phase.Ask || _phase == Phase.Error) PaintAsk(g, W, H, pad);
            else PaintProgress(g, W, H, pad);

            base.OnPaint(e);
        }

        void PaintAsk(Graphics g, int W, int H, int pad)
        {
            var box = new RectangleF(pad, S(118), W - pad * 2, S(132));
            using (var p = Theme.Round(box, S(8)))
            {
                using (var br = new SolidBrush(Color.FromArgb(7, 14, 25))) g.FillPath(br, p);
                using (var pen = new Pen(Color.FromArgb(150, 26, 66, 112))) g.DrawPath(pen, p);
            }

            if (_phase == Phase.Error)
            {
                Theme.Text(g, "Installation impossible", F(10.5f, FontStyle.Bold), Theme.Red,
                    new RectangleF(box.X + S(16), box.Y + S(12), box.Width - S(32), S(20)));
                DrawWrapped(g, _error ?? "", F(8.6f, FontStyle.Regular), Color.FromArgb(214, 226, 242),
                    new RectangleF(box.X + S(16), box.Y + S(38), box.Width - S(32), box.Height - S(50)));

                var retry = new RectangleF(box.X + S(16), box.Bottom - S(54), box.Width - S(32), S(42));
                PaintButton(g, retry, "RÉESSAYER", true, "up:retry", StartInstall);
                return;
            }

            string notes = string.IsNullOrEmpty(_info.Notes)
                ? "Cette nouvelle version corrige des bugs et améliore le launcher. Ta session Epic reste connectée."
                : _info.Notes;
            DrawWrapped(g, notes, F(8.7f, FontStyle.Regular), Color.FromArgb(210, 224, 240),
                new RectangleF(box.X + S(16), box.Y + S(14), box.Width - S(32), box.Height - S(28)));

            float by = H - S(84);
            float bw = (W - pad * 2 - S(12)) / 2f;

            var later = new RectangleF(pad, by, bw, S(48));
            PaintButton(g, later, "PLUS TARD", false, "up:later", Later);

            var install = new RectangleF(pad + bw + S(12), by, bw, S(48));
            PaintButton(g, install, "INSTALLER MAINTENANT", true, "up:install", StartInstall);
        }

        void PaintProgress(Graphics g, int W, int H, int pad)
        {
            bool done = _phase == Phase.Done;
            string title = done ? "Mise à jour installée" : "Téléchargement de la mise à jour…";
            string sub = done
                ? "Redémarrage de X.HYBRID V" + _info.Version + "…"
                : "L'application va se fermer, installer la nouvelle version et redémarrer. Ta session est conservée.";

            Theme.Text(g, title, F(11.5f, FontStyle.Bold), done ? Theme.Green : Theme.Ink,
                new RectangleF(pad, S(132), W - pad * 2, S(24)));
            DrawWrapped(g, sub, F(8.8f, FontStyle.Regular), Theme.Dim,
                new RectangleF(pad, S(160), W - pad * 2, S(44)));

            var track = new RectangleF(pad, S(230), W - pad * 2, S(10));
            using (var p = Theme.Round(track, track.Height / 2f))
            using (var br = new SolidBrush(Color.FromArgb(10, 22, 38))) g.FillPath(br, p);

            float ratio = done ? 1f : _pct / 100f;
            float fillW = Math.Max(track.Height, track.Width * ratio);
            var fill = new RectangleF(track.X, track.Y, fillW, track.Height);
            using (var p = Theme.Round(fill, fill.Height / 2f))
            using (var br = new LinearGradientBrush(fill, Theme.Electric, Theme.Bright, 0f)) g.FillPath(br, p);

            Theme.Text(g, done ? "100 %" : _pct + " %", F(9f, FontStyle.Bold), Theme.Bright,
                new RectangleF(track.X, track.Bottom + S(10), S(80), S(18)));
            Theme.Text(g, done ? "Installation terminée" : "Ne ferme pas cette fenêtre",
                F(8.4f, FontStyle.Regular), Theme.Dim2,
                new RectangleF(track.Right - S(240), track.Bottom + S(10), S(240), S(18)),
                StringAlignment.Far, StringAlignment.Center, false);
        }

        void DrawWrapped(Graphics g, string text, Font f, Color color, RectangleF r)
        {
            var sf = new StringFormat();
            sf.Alignment = StringAlignment.Near;
            sf.LineAlignment = StringAlignment.Near;
            sf.Trimming = StringTrimming.EllipsisWord;
            sf.FormatFlags = StringFormatFlags.LineLimit;
            using (var br = new SolidBrush(color))
                g.DrawString(text ?? "", f, br, r, sf);
            sf.Dispose();
        }

        void PaintButton(Graphics g, RectangleF r, string label, bool primary, string id, Action act)
        {
            bool hot = _hot == id;
            using (var p = Theme.Round(r, S(7)))
            {
                if (primary)
                {
                    using (var br = new LinearGradientBrush(r,
                        hot ? Color.FromArgb(0, 150, 255) : Color.FromArgb(3, 108, 232),
                        hot ? Color.FromArgb(10, 196, 255) : Color.FromArgb(0, 154, 240), 0f))
                        g.FillPath(br, p);
                    using (var pen = new Pen(Color.FromArgb(190, 128, 212, 255))) g.DrawPath(pen, p);
                }
                else
                {
                    using (var br = new SolidBrush(hot ? Color.FromArgb(16, 30, 50) : Color.FromArgb(8, 16, 28)))
                        g.FillPath(br, p);
                    using (var pen = new Pen(Color.FromArgb(150, 34, 78, 128))) g.DrawPath(pen, p);
                }
            }
            Theme.Text(g, label, F(8.8f, FontStyle.Bold), primary ? Color.White : Color.FromArgb(210, 226, 244),
                r, StringAlignment.Center, StringAlignment.Center, false);
            if (act != null) Hit(r, id, act);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            string id = null;
            for (int i = _spots.Count - 1; i >= 0; i--)
                if (_spots[i].R.Contains(e.Location)) { id = _spots[i].Id; break; }
            if (id != _hot)
            {
                _hot = id;
                Cursor = id != null ? Cursors.Hand : Cursors.Default;
                Invalidate();
            }
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left) return;
            for (int i = _spots.Count - 1; i >= 0; i--)
            {
                if (!_spots[i].R.Contains(e.Location)) continue;
                if (_spots[i].Act != null) _spots[i].Act();
                return;
            }
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (keyData == Keys.Escape && _phase != Phase.Working && _phase != Phase.Done)
            {
                Later();
                return true;
            }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        void Later()
        {
            if (_phase == Phase.Working || _phase == Phase.Done) return;
            Close();
        }

        void StartInstall()
        {
            if (_phase == Phase.Working || _phase == Phase.Done) return;
            _phase = Phase.Working;
            _pct = 0;
            _error = null;
            _cts = new CancellationTokenSource();
            Invalidate();

            var token = _cts.Token;
            var progress = new Progress<int>(delegate (int pct)
            {
                _pct = pct;
                Invalidate();
            });

            Task.Run(delegate
            {
                string tmp = Path.Combine(Path.GetTempPath(),
                    "X.HYBRID-update-" + Guid.NewGuid().ToString("N") + ".exe");
                try
                {
                    UpdateService.Download(_info, tmp, progress, token);
                    token.ThrowIfCancellationRequested();
                    UpdateService.LaunchUpdater(_info, tmp);
                    try { File.Delete(tmp); } catch (Exception) { }

                    if (IsDisposed) return;
                    BeginInvoke(new Action(delegate
                    {
                        Applied = true;
                        _phase = Phase.Done;
                        _pct = 100;
                        Invalidate();
                        _closeTimer = new System.Windows.Forms.Timer();
                        _closeTimer.Interval = 1200;
                        _closeTimer.Tick += delegate
                        {
                            _closeTimer.Stop();
                            Close();
                        };
                        _closeTimer.Start();
                    }));
                }
                catch (OperationCanceledException)
                {
                    try { File.Delete(tmp); } catch (Exception) { }
                    if (IsDisposed) return;
                    BeginInvoke(new Action(delegate
                    {
                        _phase = Phase.Ask;
                        Invalidate();
                    }));
                }
                catch (Exception ex)
                {
                    try { File.Delete(tmp); } catch (Exception) { }
                    FileLog.Write("MAJ installation : " + ex);
                    if (IsDisposed) return;
                    BeginInvoke(new Action(delegate
                    {
                        _error = ex.Message;
                        _phase = Phase.Error;
                        Invalidate();
                    }));
                }
            });
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (_phase == Phase.Working)
            {
                e.Cancel = true;
                return;
            }
            try
            {
                if (_cts != null) _cts.Cancel();
                if (_closeTimer != null) { _closeTimer.Stop(); _closeTimer.Dispose(); _closeTimer = null; }
            }
            catch (Exception) { }
            base.OnFormClosing(e);
        }

        internal Bitmap RenderPreview()
        {
            var bmp = new Bitmap(Math.Max(1, Width), Math.Max(1, Height));
            using (var g = Graphics.FromImage(bmp))
            {
                g.Clear(Theme.Window);
                using (var pe = new PaintEventArgs(g, new Rectangle(0, 0, bmp.Width, bmp.Height)))
                    OnPaint(pe);
            }
            return bmp;
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            foreach (var f in _fonts.Values) { try { f.Dispose(); } catch (Exception) { } }
            _fonts.Clear();
            base.OnFormClosed(e);
        }
    }
}
