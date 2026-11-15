using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace FNHybrid
{
    // Animation jouée par-dessus l'écran de démarrage de Fortnite (fenêtre
    // EasyAntiCheat) : un rond rouge barré entoure le logo en bas à gauche, le
    // logo tremble, se fissure puis explose en éclats. Purement visuel : une
    // fenêtre transparente et non cliquable qui suit l'écran du jeu ; rien n'est
    // modifié dans le jeu ni dans l'anti-cheat.
    internal sealed class EacBreakOverlay : Form
    {
        readonly IntPtr _splashHwnd;
        readonly Point _offset;          // position de l'overlay dans l'écran du jeu
        readonly Rectangle _logo;        // zone du logo (coordonnées de l'overlay)
        readonly Bitmap _logoShot;       // capture du logo réel
        readonly Bitmap _patch;          // image X.HYBRID de tout l'écran de démarrage
        readonly Timer _timer;
        readonly Stopwatch _clock = Stopwatch.StartNew();
        readonly List<Shard> _shards = new List<Shard>();
        readonly List<Spark> _sparks = new List<Spark>();

        // chronologie (secondes)
        const float RingEnd = 0.75f, SlashEnd = 1.0f, CrackEnd = 1.75f, BurstEnd = 3.2f, FadeEnd = 3.9f;

        sealed class Shard { public PointF[] Poly; public PointF Center; public float Vx, Vy, Spin; }
        sealed class Spark { public float X, Y, Vx, Vy, Life; public Color Color; }

        // ---------- lancement depuis MainForm ----------

        // Attend l'écran de démarrage de Fortnite puis joue l'animation (thread de fond).
        public static void WatchAndPlay(Action<Action> ui, int timeoutSeconds)
        {
            var wait = Stopwatch.StartNew();
            while (wait.Elapsed.TotalSeconds < timeoutSeconds)
            {
                IntPtr hwnd;
                if (FindSplash(out hwnd).HasValue)
                {
                    System.Threading.Thread.Sleep(250);
                    var found = FindSplash(out hwnd);
                    if (!found.HasValue) return;
                    var s = found.Value;
                    var logo = LogoRect(s);

                    // image X.HYBRID plein écran (garantie non vide : fond de secours
                    // si le rendu échoue, sinon on voyait juste un fond gris foncé)
                    Bitmap patch = SafePatch(s.Width, s.Height);

                    var h = hwnd;
                    ui(delegate
                    {
                        try { new EacBreakOverlay(h, s, logo, null, patch).Show(); }
                        catch (Exception) { }
                    });
                    return;
                }
                System.Threading.Thread.Sleep(100);
            }
        }

        internal static Rectangle LogoRect(Rectangle s)
        {
            // logo « easy ANTI-CHEAT » en bas à gauche de l'écran (800x450 à 100 %)
            return new Rectangle(
                s.X + (int)(s.Width * 0.015f), s.Bottom - (int)(s.Height * 0.205f),
                (int)(s.Width * 0.27f), (int)(s.Height * 0.185f));
        }

        // image X.HYBRID plein écran, jamais null
        static Bitmap SafePatch(int w, int h)
        {
            w = Math.Max(1, w); h = Math.Max(1, h);
            try { var b = GameSplash.Render(w, h); if (b != null) return b; }
            catch (Exception ex) { FileLog.Write("OVERLAY : rendu X.HYBRID échoué (" + ex.Message + ")"); }

            var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                using (var grad = new LinearGradientBrush(new Rectangle(0, 0, w, h),
                    Color.FromArgb(13, 28, 52), Color.FromArgb(3, 8, 18), 90f))
                    g.FillRectangle(grad, 0, 0, w, h);
                using (var f = Theme.Brand(h * 0.14f))
                using (var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
                {
                    using (var blue = new SolidBrush(Theme.Electric))
                        g.DrawString("X.", f, blue, new RectangleF(0, 0, w * 0.5f, h), sf);
                    using (var white = new SolidBrush(Color.White))
                        g.DrawString("HYBRID", f, white, new RectangleF(w * 0.5f, 0, w * 0.5f, h), sf);
                }
            }
            return bmp;
        }

        // vrai logo « easy ANTI-CHEAT » (image embarquée) posé sur une carte blanche
        // arrondie, comme sur l'écran de démarrage d'origine ; c'est lui qui se casse
        static Bitmap BuildBadge(int w, int h)
        {
            try
            {
                using (var stream = typeof(EacBreakOverlay).Assembly.GetManifestResourceStream("EacLogo.png"))
                    if (stream != null)
                        using (var src = new Bitmap(stream))
                            return BuildLogoCard(src, w, h);
            }
            catch (Exception ex) { FileLog.Write("OVERLAY : logo anti-cheat illisible (" + ex.Message + ")"); }
            return BuildDrawnBadge(w, h);
        }

        static Bitmap BuildLogoCard(Bitmap src, int w, int h)
        {
            // on enlève les marges blanches de l'image pour ne garder que le logo
            Rectangle content = ContentBounds(src);

            var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;

                // logo seul, fond blanc retiré (ratio conservé), centré dans la zone
                float pad = h * 0.04f;
                float scale = Math.Min((w - pad * 2) / content.Width, (h - pad * 2) / content.Height);
                float lw = content.Width * scale, lh = content.Height * scale;
                var card = new RectangleF((w - lw) / 2f - pad, (h - lh) / 2f - pad, lw + pad * 2, lh + pad * 2);

                int pandaEnd = content.X + (int)(content.Width * PandaWidth);
                using (var clear = RemoveWhite(src, pandaEnd))
                    g.DrawImage(clear, new RectangleF(card.X + pad, card.Y + pad, lw, lh),
                        content, GraphicsUnit.Pixel);

                // centre de la tête du panda (le tiers gauche du logo) : c'est là
                // que le rond rouge se dessine et que l'impact part
                _lastImpact = new PointF(card.X + pad + lw * PandaCenterX, card.Y + pad + lh * 0.5f);
            }
            return bmp;
        }

        // position horizontale du centre du panda dans le logo (mesurée sur l'image)
        const float PandaCenterX = 0.17f;
        // le panda occupe le tiers gauche du logo (colonne vide à 34 % de la largeur)
        const float PandaWidth = 0.34f;

        // Rend le fond blanc transparent :
        //  - à droite du panda (texte « easy ANTI-CHEAT ») : tout le blanc, trous des lettres compris ;
        //  - sur le panda : seulement le blanc relié au bord, pour garder son museau et ses yeux blancs.
        // Les pixels presque blancs des bords deviennent semi-transparents (contours lisses).
        static Bitmap RemoveWhite(Bitmap src, int pandaEnd)
        {
            int w = src.Width, h = src.Height;
            var outBmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(outBmp)) g.DrawImage(src, 0, 0, w, h);

            var rect = new Rectangle(0, 0, w, h);
            var data = outBmp.LockBits(rect, ImageLockMode.ReadWrite, PixelFormat.Format32bppArgb);
            var px = new byte[data.Stride * h];
            Marshal.Copy(data.Scan0, px, 0, px.Length);
            int stride = data.Stride;

            const int Hard = 238, Soft = 175;   // >= Hard : fond ; entre Soft et Hard : bord adouci
            Func<int, int> light = i => Math.Min(px[i], Math.Min(px[i + 1], px[i + 2]));

            // blanc relié au bord de l'image (fond autour du panda)
            var outside = new bool[w * h];
            var stack = new Stack<int>();
            for (int x = 0; x < w; x++) { stack.Push(x); stack.Push((h - 1) * w + x); }
            for (int y = 0; y < h; y++) { stack.Push(y * w); stack.Push(y * w + w - 1); }
            while (stack.Count > 0)
            {
                int p = stack.Pop();
                if (outside[p]) continue;
                int x = p % w, y = p / w;
                if (light(y * stride + x * 4) < Soft) continue;
                outside[p] = true;
                if (x > 0) stack.Push(p - 1);
                if (x < w - 1) stack.Push(p + 1);
                if (y > 0) stack.Push(p - w);
                if (y < h - 1) stack.Push(p + w);
            }

            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    int i = y * stride + x * 4;
                    int l = light(i);
                    if (l < Soft) continue;
                    if (x < pandaEnd && !outside[y * w + x]) continue;   // blanc du panda : on garde
                    if (l >= Hard) { px[i + 3] = 0; continue; }
                    // bord : opacité selon la distance au blanc, couleur « dé-blanchie »
                    float a = (Hard - l) / (float)(Hard - Soft);
                    for (int k = 0; k < 3; k++)
                    {
                        float c = (px[i + k] - 255f * (1f - a)) / Math.Max(0.05f, a);
                        px[i + k] = (byte)Math.Max(0, Math.Min(255, c));
                    }
                    px[i + 3] = (byte)(255 * a);
                }

            Marshal.Copy(px, 0, data.Scan0, px.Length);
            outBmp.UnlockBits(data);
            return outBmp;
        }
        [ThreadStatic] static PointF? _lastImpact;

        // zone de l'image qui n'est pas du blanc de fond
        static Rectangle ContentBounds(Bitmap src)
        {
            int minX = src.Width, minY = src.Height, maxX = -1, maxY = -1;
            for (int y = 0; y < src.Height; y += 2)
                for (int x = 0; x < src.Width; x += 2)
                {
                    var c = src.GetPixel(x, y);
                    if (c.A > 30 && (c.R < 225 || c.G < 225 || c.B < 225))
                    {
                        if (x < minX) minX = x; if (x > maxX) maxX = x;
                        if (y < minY) minY = y; if (y > maxY) maxY = y;
                    }
                }
            if (maxX < 0) return new Rectangle(0, 0, src.Width, src.Height);
            minX = Math.Max(0, minX - 2); minY = Math.Max(0, minY - 2);
            maxX = Math.Min(src.Width - 1, maxX + 2); maxY = Math.Min(src.Height - 1, maxY + 2);
            return Rectangle.FromLTRB(minX, minY, maxX + 1, maxY + 1);
        }

        static GraphicsPath RoundRect(RectangleF r, float radius)
        {
            float d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
            var p = new GraphicsPath();
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        // ancien badge dessiné, gardé si l'image embarquée manque
        static Bitmap BuildDrawnBadge(int w, int h)
        {
            var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
                float hex = h * 0.82f;
                float cx = hex * 0.62f, cy = h * 0.5f, r = hex * 0.5f;
                var pts = new PointF[6];
                for (int i = 0; i < 6; i++)
                {
                    double a = Math.PI / 180 * (60 * i - 30);
                    pts[i] = new PointF(cx + (float)Math.Cos(a) * r, cy + (float)Math.Sin(a) * r);
                }
                using (var fill = new LinearGradientBrush(new RectangleF(cx - r, cy - r, r * 2, r * 2),
                    Color.FromArgb(40, 120, 200), Color.FromArgb(18, 40, 70), 90f))
                    g.FillPolygon(fill, pts);
                using (var pen = new Pen(Color.FromArgb(235, 245, 255), Math.Max(1.5f, h * 0.045f)) { LineJoin = LineJoin.Round })
                    g.DrawPolygon(pen, pts);
                // petit bouclier blanc au centre
                using (var sh = new GraphicsPath())
                {
                    float sw = r * 0.8f, sh2 = r * 0.95f;
                    sh.AddBezier(cx, cy - sh2 * 0.5f, cx + sw * 0.5f, cy - sh2 * 0.5f, cx + sw * 0.5f, cy - sh2 * 0.2f, cx + sw * 0.5f, cy);
                    sh.AddBezier(cx + sw * 0.5f, cy, cx + sw * 0.5f, cy + sh2 * 0.35f, cx + sw * 0.25f, cy + sh2 * 0.5f, cx, cy + sh2 * 0.5f);
                    sh.AddBezier(cx, cy + sh2 * 0.5f, cx - sw * 0.25f, cy + sh2 * 0.5f, cx - sw * 0.5f, cy + sh2 * 0.35f, cx - sw * 0.5f, cy);
                    sh.AddBezier(cx - sw * 0.5f, cy, cx - sw * 0.5f, cy - sh2 * 0.2f, cx - sw * 0.5f, cy - sh2 * 0.5f, cx, cy - sh2 * 0.5f);
                    using (var wb = new SolidBrush(Color.FromArgb(240, 248, 255))) g.FillPath(wb, sh);
                }
                // texte « ANTI-CHEAT »
                float tx = cx + r + h * 0.08f;
                using (var f1 = Theme.UI(h * 0.3f, FontStyle.Bold))
                using (var f2 = Theme.UI(h * 0.17f, FontStyle.Bold))
                {
                    Theme.Text(g, "ANTI", f1, Color.White, new RectangleF(tx, h * 0.12f, w - tx, h * 0.42f),
                        StringAlignment.Near, StringAlignment.Center, false);
                    Theme.Text(g, "CHEAT", f2, Color.FromArgb(150, 200, 255), new RectangleF(tx, h * 0.52f, w - tx, h * 0.36f),
                        StringAlignment.Near, StringAlignment.Center, false);
                }
            }
            return bmp;
        }

        internal EacBreakOverlay(IntPtr splashHwnd, Rectangle splash, Rectangle logoScreen, Bitmap shot, Bitmap patch)
        {
            _splashHwnd = splashHwnd;
            // badge « ANTI-CHEAT » dessiné par l'app (plus de capture d'écran du
            // vrai logo, qui revenait souvent vide) ; c'est lui qui se casse
            _lastImpact = null;
            _logoShot = shot ?? BuildBadge(Math.Max(8, logoScreen.Width), Math.Max(8, logoScreen.Height));
            _patch = patch;

            // overlay = tout l'écran du jeu : les éclats peuvent voler partout dessus
            _offset = Point.Empty;
            _logo = new Rectangle(logoScreen.X - splash.X, logoScreen.Y - splash.Y, logoScreen.Width, logoScreen.Height);
            _impact = _lastImpact.HasValue
                ? new PointF(_logo.X + _lastImpact.Value.X, _logo.Y + _lastImpact.Value.Y)
                : new PointF(_logo.X + _logo.Height * 0.5f, _logo.Y + _logo.Height * 0.5f);

            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            Bounds = splash;
            TopMost = true;

            BuildShards();

            _timer = new Timer { Interval = 15 };
            _timer.Tick += delegate { Tick(); };
        }

        readonly PointF _impact;
        PointF Impact { get { return _impact; } }

        // découpe le logo en triangles irréguliers
        void BuildShards()
        {
            var rnd = new Random(7);
            int cols = 7, rows = 3;
            var pts = new PointF[cols + 1, rows + 1];
            for (int x = 0; x <= cols; x++)
                for (int y = 0; y <= rows; y++)
                {
                    float jx = (x == 0 || x == cols) ? 0 : (float)(rnd.NextDouble() - 0.5) * _logo.Width / cols * 0.7f;
                    float jy = (y == 0 || y == rows) ? 0 : (float)(rnd.NextDouble() - 0.5) * _logo.Height / rows * 0.7f;
                    pts[x, y] = new PointF(_logo.X + _logo.Width * x / (float)cols + jx, _logo.Y + _logo.Height * y / (float)rows + jy);
                }
            var c = Impact;
            for (int x = 0; x < cols; x++)
                for (int y = 0; y < rows; y++)
                {
                    bool flip = rnd.Next(2) == 0;
                    var a = pts[x, y]; var b = pts[x + 1, y]; var d = pts[x, y + 1]; var e = pts[x + 1, y + 1];
                    var tris = flip
                        ? new[] { new[] { a, b, e }, new[] { a, e, d } }
                        : new[] { new[] { a, b, d }, new[] { b, e, d } };
                    foreach (var tri in tris)
                    {
                        var center = new PointF((tri[0].X + tri[1].X + tri[2].X) / 3, (tri[0].Y + tri[1].Y + tri[2].Y) / 3);
                        float dx = center.X - c.X, dy = center.Y - c.Y;
                        float len = Math.Max(1f, (float)Math.Sqrt(dx * dx + dy * dy));
                        float power = _logo.Height * (2.6f + (float)rnd.NextDouble() * 2.2f);
                        _shards.Add(new Shard
                        {
                            Poly = tri,
                            Center = center,
                            Vx = dx / len * power + _logo.Height * 0.8f,
                            Vy = dy / len * power * 0.6f - _logo.Height * (2.4f + (float)rnd.NextDouble() * 1.6f),
                            Spin = (float)(rnd.NextDouble() - 0.5) * 900f
                        });
                    }
                }

            for (int i = 0; i < 46; i++)
            {
                double ang = rnd.NextDouble() * Math.PI * 2;
                float sp = _logo.Height * (3f + (float)rnd.NextDouble() * 6f);
                _sparks.Add(new Spark
                {
                    X = c.X, Y = c.Y,
                    Vx = (float)Math.Cos(ang) * sp, Vy = (float)Math.Sin(ang) * sp - _logo.Height * 1.5f,
                    Life = 0.4f + (float)rnd.NextDouble() * 0.7f,
                    Color = rnd.Next(3) == 0 ? Color.FromArgb(255, 255, 240, 200) : Color.FromArgb(255, 255, 70 + rnd.Next(120), 40)
                });
            }
        }

        protected override bool ShowWithoutActivation { get { return true; } }

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                // layered + click-through + pas dans Alt-Tab + sans activation + topmost
                cp.ExStyle |= 0x80000 | 0x20 | 0x80 | 0x08000000 | 0x8;
                return cp;
            }
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            // Windows nous prévient à chaque déplacement de l'écran de Fortnite :
            // l'overlay se recale aussitôt, sans attendre la prochaine image
            try
            {
                uint pid;
                GetWindowThreadProcessId(_splashHwnd, out pid);
                _hookProc = OnWinEvent;
                _hook = SetWinEventHook(EVENT_OBJECT_LOCATIONCHANGE, EVENT_OBJECT_LOCATIONCHANGE,
                    IntPtr.Zero, _hookProc, pid, 0, 0 /* WINEVENT_OUTOFCONTEXT */);
            }
            catch (Exception) { }
            Tick();
            _timer.Start();
        }

        const uint EVENT_OBJECT_LOCATIONCHANGE = 0x800B;
        WinEventProc _hookProc;
        IntPtr _hook;
        bool _settled;

        void OnWinEvent(IntPtr hook, uint evt, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
        {
            if (hwnd != _splashHwnd || idObject != 0 || IsDisposed) return;
            Follow();
        }

        // place l'overlay pile sur l'écran du jeu, juste au-dessus de lui
        bool Follow()
        {
            RECT r;
            if (!IsWindow(_splashHwnd) || !IsWindowVisible(_splashHwnd) || !GetWindowRect(_splashHwnd, out r)) return false;
            if (r.Left + _offset.X != Left || r.Top + _offset.Y != Top)
                SetWindowPos(Handle, (IntPtr)(-1), r.Left + _offset.X, r.Top + _offset.Y, 0, 0, 0x0001 | 0x0010);
            else
                SetWindowPos(Handle, (IntPtr)(-1), 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010);
            return true;
        }

        void Tick()
        {
            if (IsDisposed) return;
            float t = (float)_clock.Elapsed.TotalSeconds;

            // suit l'écran de Fortnite (déplacé, fermé…)
            if (!Follow() || t > 180)
            {
                Close();
                return;
            }
            // animation finie : l'image ne change plus, on ne fait que suivre
            if (_settled) return;
            var pos = new Point(Left, Top);

            using (var frame = new Bitmap(Width, Height, PixelFormat.Format32bppPArgb))
            {
                using (var g = Graphics.FromImage(frame))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.InterpolationMode = InterpolationMode.HighQualityBilinear;
                    g.PixelOffsetMode = PixelOffsetMode.Half;
                    // fond opaque : jamais de zone transparente, sinon l'écran clair
                    // de Fortnite apparaît derrière (le « truc blanc »)
                    g.Clear(Color.FromArgb(255, 3, 10, 22));
                    DrawFrame(g, t);
                }
                Push(frame, pos);
            }
            if (t > FadeEnd + 0.1f)
            {
                _settled = true;
                // on continue de coller à la fenêtre de Fortnite de façon réactive
                _timer.Interval = 30;
            }
        }

        internal void DrawFrame(Graphics g, float t)
        {
            var L = _logo;
            var c = Impact;
            float ringR = L.Height * 0.6f;

            // ---- image X.HYBRID sur tout l'écran de Fortnite ----
            if (_patch != null) g.DrawImage(_patch, 0, 0, Width, Height);
            else if (t >= SlashEnd)
                using (var b = new SolidBrush(Color.FromArgb(3, 10, 22))) g.FillRectangle(b, L);

            // le logo anti-cheat (capturé) reste visible jusqu'à ce qu'il soit barré
            if (t < SlashEnd) g.DrawImage(_logoShot, L);

            if (t >= SlashEnd && t < CrackEnd)
            {
                // tremblement qui s'amplifie + fissures qui grandissent depuis l'impact
                float q = (t - SlashEnd) / (CrackEnd - SlashEnd);
                float amp = L.Height * (0.01f + 0.06f * q * q);
                float sx = amp * (float)Math.Sin(t * 87), sy = amp * (float)Math.Cos(t * 113);
                g.DrawImage(_logoShot, L.X + sx, L.Y + sy, L.Width, L.Height);

                var st = g.Save();
                g.SetClip(L);
                g.TranslateTransform(sx, sy);
                using (var glow = new Pen(Color.FromArgb((int)(70 * q), 255, 60, 60), Math.Max(2f, L.Height * 0.045f)))
                using (var crack = new Pen(Color.FromArgb(220, 255, 255, 255), Math.Max(1f, L.Height * 0.012f)))
                {
                    foreach (var sh in _shards)
                    {
                        // les arêtes des éclats apparaissent de l'impact vers l'extérieur
                        float dist = Distance(sh.Center, c) / Math.Max(1f, L.Width);
                        if (dist > q * 1.3f) continue;
                        g.DrawPolygon(glow, sh.Poly);
                        g.DrawPolygon(crack, sh.Poly);
                    }
                }
                g.Restore(st);

                // halo rouge qui monte avant l'explosion
                if (q > 0.6f)
                    using (var halo = new SolidBrush(Color.FromArgb((int)(110 * (q - 0.6f) / 0.4f), 255, 40, 40)))
                        g.FillEllipse(halo, c.X - ringR, c.Y - ringR, ringR * 2, ringR * 2);
            }
            else if (t >= CrackEnd && t < BurstEnd)
            {
                float dt = t - CrackEnd;
                float q = dt / (BurstEnd - CrackEnd);
                float gravity = L.Height * 9f;

                // onde de choc
                float wave = L.Height * (0.4f + 6f * (float)Math.Sqrt(Math.Min(1f, q * 2.2f)));
                int wa = (int)(220 * Math.Max(0f, 1f - q * 2.2f));
                if (wa > 0)
                    using (var shock = new Pen(Color.FromArgb(wa, 255, 230, 230), Math.Max(2f, L.Height * 0.08f * (1f - q))))
                        g.DrawEllipse(shock, c.X - wave, c.Y - wave, wave * 2, wave * 2);

                // flash
                if (q < 0.12f)
                    using (var flash = new SolidBrush(Color.FromArgb((int)(230 * (1 - q / 0.12f)), 255, 255, 255)))
                        g.FillEllipse(flash, c.X - ringR * 1.6f, c.Y - ringR * 1.6f, ringR * 3.2f, ringR * 3.2f);

                // éclats
                float alpha = q < 0.6f ? 1f : Math.Max(0f, 1f - (q - 0.6f) / 0.4f);
                using (var attrs = new ImageAttributes())
                using (var edge = new Pen(Color.FromArgb((int)(200 * alpha), 255, 255, 255), Math.Max(1f, L.Height * 0.015f)))
                {
                    attrs.SetColorMatrix(new ColorMatrix { Matrix33 = alpha });
                    foreach (var sh in _shards)
                    {
                        float ox = sh.Vx * dt, oy = sh.Vy * dt + 0.5f * gravity * dt * dt;
                        var st = g.Save();
                        g.TranslateTransform(sh.Center.X + ox, sh.Center.Y + oy);
                        g.RotateTransform(sh.Spin * dt);
                        g.TranslateTransform(-sh.Center.X, -sh.Center.Y);
                        using (var path = new GraphicsPath())
                        {
                            path.AddPolygon(sh.Poly);
                            g.SetClip(path);
                            g.DrawImage(_logoShot, L, 0, 0, _logoShot.Width, _logoShot.Height, GraphicsUnit.Pixel, attrs);
                            g.ResetClip();
                            g.DrawPath(edge, path);
                        }
                        g.Restore(st);
                    }
                }

                // étincelles
                foreach (var sp in _sparks)
                {
                    if (dt > sp.Life) continue;
                    float life = 1f - dt / sp.Life;
                    float x = sp.X + sp.Vx * dt, y = sp.Y + sp.Vy * dt + 0.5f * gravity * 0.6f * dt * dt;
                    float tx = x - sp.Vx * 0.03f, ty = y - (sp.Vy + gravity * 0.6f * dt) * 0.03f;
                    using (var pen = new Pen(Color.FromArgb((int)(255 * life), sp.Color), Math.Max(1.5f, L.Height * 0.03f * life))
                           { StartCap = LineCap.Round, EndCap = LineCap.Round })
                        g.DrawLine(pen, tx, ty, x, y);
                }
            }

            // ---- rond rouge barré ----
            float ringQ = Ease(Math.Min(1f, t / RingEnd));
            float slashQ = Ease(Clamp((t - RingEnd) / (SlashEnd - RingEnd)));
            float fade = t < BurstEnd ? 1f : Math.Max(0f, 1f - (t - BurstEnd) / (FadeEnd - BurstEnd));
            if (fade <= 0f) return;

            float pop = 1f;
            if (t >= SlashEnd && t < SlashEnd + 0.25f) pop = 1f + 0.18f * (float)Math.Sin((t - SlashEnd) / 0.25f * Math.PI);
            if (t >= CrackEnd && t < CrackEnd + 0.35f) pop = 1f + 0.35f * (float)Math.Sin((t - CrackEnd) / 0.35f * Math.PI);
            float rr = ringR * pop;
            float width = Math.Max(3f, L.Height * 0.1f);

            if (ringQ >= 1f)
                for (int i = 3; i >= 1; i--)
                    using (var glow = new Pen(Color.FromArgb((int)(35 * fade), 255, 30, 30), width * (1 + i * 0.9f)))
                        g.DrawEllipse(glow, c.X - rr, c.Y - rr, rr * 2, rr * 2);

            using (var red = new Pen(Color.FromArgb((int)(255 * fade), 235, 28, 36), width) { StartCap = LineCap.Round, EndCap = LineCap.Round })
            {
                if (ringQ > 0.001f) g.DrawArc(red, c.X - rr, c.Y - rr, 2 * rr, 2 * rr, -135f, 360f * ringQ);
                if (slashQ > 0f)
                {
                    double d = Math.PI / 4;
                    var p0 = new PointF(c.X - (float)Math.Cos(d) * rr, c.Y - (float)Math.Sin(d) * rr);
                    var p1 = new PointF(c.X + (float)Math.Cos(d) * rr, c.Y + (float)Math.Sin(d) * rr);
                    g.DrawLine(red, p0, new PointF(p0.X + (p1.X - p0.X) * slashQ, p0.Y + (p1.Y - p0.Y) * slashQ));
                }
            }
            using (var shine = new Pen(Color.FromArgb((int)(120 * fade), 255, 190, 190), Math.Max(1f, width * 0.25f)))
                if (ringQ > 0.001f) g.DrawArc(shine, c.X - rr, c.Y - rr, 2 * rr, 2 * rr, -135f, 360f * ringQ);
        }

        static float Clamp(float v) { return v < 0 ? 0 : (v > 1 ? 1 : v); }
        static float Ease(float v) { return 1f - (1f - v) * (1f - v) * (1f - v); }
        static float Distance(PointF a, PointF b) { float dx = a.X - b.X, dy = a.Y - b.Y; return (float)Math.Sqrt(dx * dx + dy * dy); }

        // ---------- fenêtre transparente (alpha par pixel) ----------

        void Push(Bitmap bmp, Point pos)
        {
            IntPtr screen = GetDC(IntPtr.Zero);
            IntPtr mem = CreateCompatibleDC(screen);
            IntPtr hbmp = IntPtr.Zero, old = IntPtr.Zero;
            try
            {
                hbmp = bmp.GetHbitmap(Color.FromArgb(0));
                old = SelectObject(mem, hbmp);
                var size = new SIZE { cx = bmp.Width, cy = bmp.Height };
                var src = new POINT();
                var p = new POINT { x = pos.X, y = pos.Y };
                var blend = new BLENDFUNCTION { BlendOp = 0, BlendFlags = 0, SourceConstantAlpha = 255, AlphaFormat = 1 };
                UpdateLayeredWindow(Handle, screen, ref p, ref size, mem, ref src, 0, ref blend, 2);
            }
            finally
            {
                if (old != IntPtr.Zero) SelectObject(mem, old);
                if (hbmp != IntPtr.Zero) DeleteObject(hbmp);
                DeleteDC(mem);
                ReleaseDC(IntPtr.Zero, screen);
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _timer.Dispose();
                if (_hook != IntPtr.Zero) { UnhookWinEvent(_hook); _hook = IntPtr.Zero; }
                _logoShot.Dispose();
                if (_patch != null) _patch.Dispose();
            }
            base.Dispose(disposing);
        }

        // ---------- recherche de l'écran de démarrage ----------

        static readonly string[] SplashProcesses =
        {
            "FortniteClient-Win64-Shipping_EAC_EOS", "FortniteClient-Win64-Shipping_EAC",
            "FortniteLauncher", "FortniteBootstrapper"
        };

        internal static Rectangle? FindSplash(out IntPtr hwndFound)
        {
            hwndFound = IntPtr.Zero;
            var pids = new HashSet<uint>();
            foreach (var name in SplashProcesses)
            {
                try { foreach (var p in Process.GetProcessesByName(name)) { pids.Add((uint)p.Id); p.Dispose(); } }
                catch (Exception) { }
            }
            if (pids.Count == 0) return null;

            Rectangle? found = null;
            IntPtr hit = IntPtr.Zero;
            EnumWindows((hwnd, lp) =>
            {
                uint pid;
                GetWindowThreadProcessId(hwnd, out pid);
                if (!pids.Contains(pid) || !IsWindowVisible(hwnd)) return true;
                RECT r;
                if (!GetWindowRect(hwnd, out r)) return true;
                int w = r.Right - r.Left, h = r.Bottom - r.Top;
                if (w < 300 || h < 150) return true;
                float ratio = w / (float)h;
                if (ratio < 1.5f || ratio > 2.0f) return true;
                found = Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom);
                hit = hwnd;
                return false;
            }, IntPtr.Zero);
            hwndFound = hit;
            return found;
        }

        delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);
        delegate void WinEventProc(IntPtr hook, uint evt, IntPtr hwnd, int idObject, int idChild, uint thread, uint time);

        [DllImport("user32.dll")] static extern IntPtr SetWinEventHook(uint min, uint max, IntPtr module, WinEventProc proc, uint pid, uint thread, uint flags);
        [DllImport("user32.dll")] static extern bool UnhookWinEvent(IntPtr hook);

        [StructLayout(LayoutKind.Sequential)] struct RECT { public int Left, Top, Right, Bottom; }
        [StructLayout(LayoutKind.Sequential)] struct POINT { public int x, y; }
        [StructLayout(LayoutKind.Sequential)] struct SIZE { public int cx, cy; }
        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        struct BLENDFUNCTION { public byte BlendOp, BlendFlags, SourceConstantAlpha, AlphaFormat; }

        [DllImport("user32.dll")] static extern bool EnumWindows(EnumWindowsProc cb, IntPtr lParam);
        [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
        [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr hwnd);
        [DllImport("user32.dll")] static extern bool IsWindow(IntPtr hwnd);
        [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr hwnd, out RECT r);
        [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
        [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr hwnd);
        [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);
        [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleDC(IntPtr dc);
        [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr dc);
        [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
        [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr obj);
        [DllImport("user32.dll")] static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr dst, ref POINT pos, ref SIZE size,
            IntPtr src, ref POINT srcPos, int key, ref BLENDFUNCTION blend, int flags);
    }
}
