using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;

namespace FNHybrid
{
    // Palette + primitives graphiques de X.HYBRID (noir / bleu electrique, aucun violet).
    internal static class Theme
    {
        public static readonly Color Window    = Color.FromArgb(4, 6, 10);
        public static readonly Color Bg        = Color.FromArgb(5, 7, 11);
        public static readonly Color Bg2       = Color.FromArgb(8, 13, 22);
        public static readonly Color Panel     = Color.FromArgb(10, 17, 29);
        public static readonly Color PanelHi   = Color.FromArgb(12, 23, 40);
        public static readonly Color Field     = Color.FromArgb(7, 13, 23);
        public static readonly Color Electric  = Color.FromArgb(8, 124, 255);
        public static readonly Color Bright    = Color.FromArgb(0, 168, 255);
        public static readonly Color DeepBlue  = Color.FromArgb(0, 71, 216);
        public static readonly Color Ink       = Color.FromArgb(245, 248, 255);
        public static readonly Color Dim       = Color.FromArgb(145, 167, 196);
        public static readonly Color Dim2      = Color.FromArgb(104, 126, 154);
        public static readonly Color Border    = Color.FromArgb(16, 42, 73);
        public static readonly Color BorderHi  = Color.FromArgb(27, 76, 134);
        public static readonly Color Green     = Color.FromArgb(43, 217, 124);
        public static readonly Color Red       = Color.FromArgb(255, 96, 114);
        public static readonly Color Amber     = Color.FromArgb(255, 199, 84);

        static string _brand;

        public static string BrandFamily
        {
            get
            {
                if (_brand != null) return _brand;
                try
                {
                    using (var ff = new FontFamily("Segoe UI Black")) { }
                    _brand = "Segoe UI Black";
                }
                catch (Exception) { _brand = "Segoe UI"; }
                return _brand;
            }
        }

        public static Font UI(float pt) { return new Font("Segoe UI", pt, FontStyle.Regular, GraphicsUnit.Point); }
        public static Font UI(float pt, FontStyle style) { return new Font("Segoe UI", pt, style, GraphicsUnit.Point); }
        public static Font Mono(float pt) { return new Font("Consolas", pt, FontStyle.Regular, GraphicsUnit.Point); }
        public static Font Mono(float pt, FontStyle style) { return new Font("Consolas", pt, style, GraphicsUnit.Point); }
        public static Font Brand(float pt) { return new Font(BrandFamily, pt, FontStyle.Bold | FontStyle.Italic, GraphicsUnit.Point); }

        public static GraphicsPath Round(RectangleF r, float radius)
        {
            var p = new GraphicsPath();
            float d = Math.Max(0.1f, radius * 2f);
            if (d > r.Width) d = r.Width;
            if (d > r.Height) d = r.Height;
            if (d < 1f) d = 1f;
            p.AddArc(r.X, r.Y, d, d, 180f, 90f);
            p.AddArc(r.Right - d, r.Y, d, d, 270f, 90f);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0f, 90f);
            p.AddArc(r.X, r.Bottom - d, d, d, 90f, 90f);
            p.CloseFigure();
            return p;
        }

        public static GraphicsPath Round(Rectangle r, int radius)
        {
            return Round(new RectangleF(r.X, r.Y, r.Width, r.Height), radius);
        }

        // panneau "gaming" : coins coupes
        public static GraphicsPath Chamfer(RectangleF r, float tl, float tr, float br, float bl)
        {
            float max = Math.Min(r.Width, r.Height) / 2.4f;
            tl = Math.Min(tl, max); tr = Math.Min(tr, max);
            br = Math.Min(br, max); bl = Math.Min(bl, max);

            var p = new GraphicsPath();
            p.AddPolygon(new PointF[]
            {
                new PointF(r.X + tl, r.Y),
                new PointF(r.Right - tr, r.Y),
                new PointF(r.Right, r.Y + tr),
                new PointF(r.Right, r.Bottom - br),
                new PointF(r.Right - br, r.Bottom),
                new PointF(r.X + bl, r.Bottom),
                new PointF(r.X, r.Bottom - bl),
                new PointF(r.X, r.Y + tl)
            });
            p.CloseFigure();
            return p;
        }

        public static void FillVertical(Graphics g, GraphicsPath p, Color top, Color bottom)
        {
            var b = p.GetBounds();
            if (b.Width <= 0f || b.Height <= 0f) return;
            using (var br = new LinearGradientBrush(new RectangleF(b.X, b.Y - 1f, b.Width, b.Height + 2f), top, bottom, 90f))
                g.FillPath(br, p);
        }

        public static void Text(Graphics g, string s, Font f, Color c, RectangleF r,
            StringAlignment h, StringAlignment v, bool wrap)
        {
            if (string.IsNullOrEmpty(s)) return;
            var sf = new StringFormat();
            sf.Alignment = h;
            sf.LineAlignment = v;
            sf.Trimming = StringTrimming.EllipsisCharacter;
            sf.FormatFlags = wrap ? (StringFormatFlags)0 : StringFormatFlags.NoWrap;
            using (var br = new SolidBrush(c))
                g.DrawString(s, f, br, r, sf);
            sf.Dispose();
        }

        public static void Text(Graphics g, string s, Font f, Color c, RectangleF r)
        {
            Text(g, s, f, c, r, StringAlignment.Near, StringAlignment.Center, false);
        }

        // icone filaire 24x24, mise a l'echelle dans la zone demandee
        public static void Icon(Graphics g, string id, RectangleF box, Color color, float stroke)
        {
            if (box.Width <= 0f || box.Height <= 0f) return;
            var state = g.Save();
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TranslateTransform(box.X, box.Y);
            g.ScaleTransform(box.Width / 24f, box.Height / 24f);

            float sw = stroke * 24f / Math.Max(1f, box.Width);
            using (var pen = new Pen(color, sw))
            using (var brush = new SolidBrush(color))
            using (var path = new GraphicsPath())
            {
                pen.StartCap = LineCap.Round;
                pen.EndCap = LineCap.Round;
                pen.LineJoin = LineJoin.Round;

                switch (id)
                {
                    case "user":
                        g.DrawEllipse(pen, 8.4f, 3.8f, 7.2f, 7.2f);
                        g.DrawArc(pen, 4.2f, 13.2f, 15.6f, 14f, 180f, 180f);
                        break;

                    case "user-fill":
                        g.FillEllipse(brush, 8.4f, 3.8f, 7.2f, 7.2f);
                        using (var body = new GraphicsPath())
                        {
                            body.AddArc(4.2f, 13.2f, 15.6f, 14f, 180f, 180f);
                            body.AddLine(19.8f, 20.5f, 4.2f, 20.5f);
                            body.CloseFigure();
                            g.FillPath(brush, body);
                        }
                        break;

                    case "chevron-down":
                        g.DrawLines(pen, new PointF[] { new PointF(7.5f, 10f), new PointF(12f, 14.5f), new PointF(16.5f, 10f) });
                        break;

                    case "chevron-right":
                        g.DrawLines(pen, new PointF[] { new PointF(10f, 6.5f), new PointF(15.5f, 12f), new PointF(10f, 17.5f) });
                        break;

                    case "arrow-left":
                        g.DrawLines(pen, new PointF[] { new PointF(14f, 6.5f), new PointF(8.5f, 12f), new PointF(14f, 17.5f) });
                        break;

                    case "min":
                        g.DrawLine(pen, 5f, 12f, 19f, 12f);
                        break;

                    case "close":
                        g.DrawLine(pen, 7.2f, 7.2f, 16.8f, 16.8f);
                        g.DrawLine(pen, 16.8f, 7.2f, 7.2f, 16.8f);
                        break;

                    case "pencil":
                        path.AddPolygon(new PointF[]
                        {
                            new PointF(4.2f, 19.8f), new PointF(5.4f, 15.1f), new PointF(16.3f, 4.2f),
                            new PointF(19.8f, 7.7f), new PointF(8.9f, 18.6f)
                        });
                        path.CloseFigure();
                        g.DrawPath(pen, path);
                        g.DrawLine(pen, 15f, 5.5f, 18.5f, 9f);
                        g.DrawLine(pen, 5.4f, 15.1f, 8.9f, 18.6f);
                        break;

                    case "gamepad":
                        using (var pad = new GraphicsPath())
                        {
                            pad.AddLine(8f, 7.4f, 16f, 7.4f);
                            pad.AddBezier(16f, 7.4f, 19.6f, 7.4f, 21.4f, 10.4f, 21.4f, 13.6f);
                            pad.AddBezier(21.4f, 13.6f, 21.4f, 16.4f, 19.6f, 17.8f, 17.8f, 17.8f);
                            pad.AddBezier(17.8f, 17.8f, 16.4f, 17.8f, 15.4f, 16.6f, 14.6f, 15.4f);
                            pad.AddLine(14.6f, 15.4f, 9.4f, 15.4f);
                            pad.AddBezier(9.4f, 15.4f, 8.6f, 16.6f, 7.6f, 17.8f, 6.2f, 17.8f);
                            pad.AddBezier(6.2f, 17.8f, 4.4f, 17.8f, 2.6f, 16.4f, 2.6f, 13.6f);
                            pad.AddBezier(2.6f, 13.6f, 2.6f, 10.4f, 4.4f, 7.4f, 8f, 7.4f);
                            pad.CloseFigure();
                            g.DrawPath(pen, pad);
                        }
                        g.DrawLine(pen, 7f, 9.8f, 7f, 14f);
                        g.DrawLine(pen, 4.9f, 11.9f, 9.1f, 11.9f);
                        g.DrawEllipse(pen, 14.6f, 9.6f, 2.2f, 2.2f);
                        g.DrawEllipse(pen, 17.2f, 9.6f, 2.2f, 2.2f);
                        g.DrawEllipse(pen, 14.6f, 12.2f, 2.2f, 2.2f);
                        break;

                    case "key":
                        g.DrawEllipse(pen, 3.2f, 3.2f, 9.6f, 9.6f);
                        g.DrawEllipse(pen, 6.3f, 6.3f, 3.4f, 3.4f);
                        g.DrawLine(pen, 11.6f, 11.6f, 19.4f, 19.4f);
                        g.DrawLine(pen, 14.6f, 14.6f, 14.6f, 18.6f);
                        g.DrawLine(pen, 17.2f, 17.2f, 17.2f, 21.2f);
                        break;

                    case "gear":
                        g.DrawEllipse(pen, 8.7f, 8.7f, 6.6f, 6.6f);
                        for (int i = 0; i < 8; i++)
                        {
                            double a = i * Math.PI / 4.0;
                            float x1 = 12f + (float)Math.Cos(a) * 7.1f;
                            float y1 = 12f + (float)Math.Sin(a) * 7.1f;
                            float x2 = 12f + (float)Math.Cos(a) * 9.4f;
                            float y2 = 12f + (float)Math.Sin(a) * 9.4f;
                            g.DrawLine(pen, x1, y1, x2, y2);
                        }
                        g.DrawEllipse(pen, 3.1f, 3.1f, 17.8f, 17.8f);
                        break;

                    case "palette":
                        g.DrawEllipse(pen, 3.2f, 4.2f, 17.6f, 15.6f);
                        g.FillEllipse(brush, 8.2f, 7.4f, 1.9f, 1.9f);
                        g.FillEllipse(brush, 14.2f, 7.6f, 1.9f, 1.9f);
                        g.FillEllipse(brush, 6.9f, 12.4f, 1.9f, 1.9f);
                        g.FillEllipse(brush, 15.1f, 12.6f, 1.9f, 1.9f);
                        break;

                    case "shield":
                        path.AddPolygon(new PointF[]
                        {
                            new PointF(12f, 3.1f), new PointF(19.2f, 6.1f), new PointF(19.2f, 11.6f),
                            new PointF(17.6f, 16.4f), new PointF(12f, 20.6f), new PointF(6.4f, 16.4f),
                            new PointF(4.8f, 11.6f), new PointF(4.8f, 6.1f)
                        });
                        path.CloseFigure();
                        g.DrawPath(pen, path);
                        g.DrawLines(pen, new PointF[] { new PointF(8.9f, 11.9f), new PointF(11.2f, 14.2f), new PointF(15.4f, 9.4f) });
                        break;

                    case "bell":
                        path.AddArc(6.4f, 4.1f, 11.2f, 11.2f, 180f, 180f);
                        path.AddLine(17.6f, 9.7f, 18.8f, 17.1f);
                        path.AddLine(18.8f, 17.1f, 5.2f, 17.1f);
                        path.AddLine(5.2f, 17.1f, 6.4f, 9.7f);
                        g.DrawPath(pen, path);
                        g.DrawArc(pen, 10.1f, 17.4f, 3.8f, 3.2f, 0f, 180f);
                        break;

                    case "globe":
                        g.DrawEllipse(pen, 3.2f, 3.2f, 17.6f, 17.6f);
                        g.DrawEllipse(pen, 8.5f, 3.2f, 7f, 17.6f);
                        g.DrawLine(pen, 3.2f, 12f, 20.8f, 12f);
                        break;

                    case "info":
                        g.DrawEllipse(pen, 3.2f, 3.2f, 17.6f, 17.6f);
                        g.FillEllipse(brush, 11.15f, 7.1f, 1.9f, 1.9f);
                        g.DrawLine(pen, 12.1f, 11.2f, 12.1f, 16.6f);
                        break;

                    case "logout":
                        g.DrawPath(pen, ArrowDoorPath());
                        g.DrawLine(pen, 14.2f, 12f, 8.6f, 12f);
                        g.DrawLines(pen, new PointF[] { new PointF(11.2f, 8.9f), new PointF(14.4f, 12f), new PointF(11.2f, 15.1f) });
                        break;

                    case "plus-circle":
                        g.DrawEllipse(pen, 3.4f, 3.4f, 17.2f, 17.2f);
                        g.DrawLine(pen, 12f, 8f, 12f, 16f);
                        g.DrawLine(pen, 8f, 12f, 16f, 12f);
                        break;

                    case "copy":
                        using (var a = Round(new RectangleF(8.4f, 8.4f, 11.6f, 11.6f), 2.4f)) g.DrawPath(pen, a);
                        g.DrawLines(pen, new PointF[] { new PointF(15f, 5.4f), new PointF(15f, 4.6f), new PointF(4f, 4.6f), new PointF(4f, 15.6f), new PointF(4.8f, 15.6f) });
                        break;

                    case "trash":
                        g.DrawLine(pen, 4.6f, 6.8f, 19.4f, 6.8f);
                        g.DrawLines(pen, new PointF[] { new PointF(9.2f, 6.8f), new PointF(9.6f, 3.8f), new PointF(14.4f, 3.8f), new PointF(14.8f, 6.8f) });
                        g.DrawLines(pen, new PointF[] { new PointF(6.4f, 6.8f), new PointF(7.4f, 20.2f), new PointF(16.6f, 20.2f), new PointF(17.6f, 6.8f) });
                        g.DrawLine(pen, 10.4f, 10.2f, 10.7f, 17f);
                        g.DrawLine(pen, 13.6f, 10.2f, 13.3f, 17f);
                        break;

                    case "refresh":
                        g.DrawArc(pen, 4.2f, 4.2f, 15.6f, 15.6f, 300f, 300f);
                        g.DrawLines(pen, new PointF[] { new PointF(17.6f, 3.4f), new PointF(19.3f, 8.7f), new PointF(14f, 8.2f) });
                        break;

                    case "play":
                        path.AddPolygon(new PointF[] { new PointF(8f, 5.2f), new PointF(19f, 12f), new PointF(8f, 18.8f) });
                        path.CloseFigure();
                        g.DrawPath(pen, path);
                        break;

                    case "terminal":
                        using (var t = Round(new RectangleF(2.8f, 4.2f, 18.4f, 15.6f), 2.6f)) g.DrawPath(pen, t);
                        g.DrawLines(pen, new PointF[] { new PointF(6.6f, 9.4f), new PointF(9.4f, 12f), new PointF(6.6f, 14.6f) });
                        g.DrawLine(pen, 12.4f, 14.8f, 16.6f, 14.8f);
                        break;

                    case "folder":
                        path.AddPolygon(new PointF[]
                        {
                            new PointF(3f, 6.4f), new PointF(9.4f, 6.4f), new PointF(11f, 8.6f),
                            new PointF(21f, 8.6f), new PointF(21f, 19f), new PointF(3f, 19f)
                        });
                        path.CloseFigure();
                        g.DrawPath(pen, path);
                        break;

                    case "external":
                        using (var boxp = Round(new RectangleF(4f, 4f, 16f, 16f), 2.4f)) g.DrawPath(pen, boxp);
                        g.DrawLine(pen, 11f, 13f, 20f, 4f);
                        g.DrawLines(pen, new PointF[] { new PointF(14.6f, 4f), new PointF(20f, 4f), new PointF(20f, 9.4f) });
                        break;

                    case "check":
                        g.DrawLines(pen, new PointF[] { new PointF(4.8f, 12.4f), new PointF(9.8f, 17.4f), new PointF(19.2f, 7f) });
                        break;

                    case "power":
                        g.DrawArc(pen, 4.2f, 4.6f, 15.6f, 15.6f, 300f, 300f);
                        g.DrawLine(pen, 12f, 3.2f, 12f, 11.4f);
                        break;

                    case "server":
                        using (var s1 = Round(new RectangleF(3.2f, 4f, 17.6f, 7f), 2f)) g.DrawPath(pen, s1);
                        using (var s2 = Round(new RectangleF(3.2f, 13f, 17.6f, 7f), 2f)) g.DrawPath(pen, s2);
                        g.FillEllipse(brush, 6.6f, 6.9f, 1.7f, 1.7f);
                        g.FillEllipse(brush, 6.6f, 15.9f, 1.7f, 1.7f);
                        break;

                    case "dot":
                        g.FillEllipse(brush, 7f, 7f, 10f, 10f);
                        break;

                    case "lock":
                        using (var l = Round(new RectangleF(4.6f, 10.2f, 14.8f, 10f), 2.2f)) g.DrawPath(pen, l);
                        g.DrawArc(pen, 7.4f, 3.6f, 9.2f, 11.6f, 180f, 180f);
                        g.FillEllipse(brush, 11.1f, 13.9f, 1.8f, 1.8f);
                        g.DrawLine(pen, 12f, 15.7f, 12f, 17.6f);
                        break;

                    case "image":
                        using (var ir = Round(new RectangleF(3.2f, 4.2f, 17.6f, 15.6f), 2.4f)) g.DrawPath(pen, ir);
                        g.FillEllipse(brush, 8f, 8.4f, 2.2f, 2.2f);
                        g.DrawLines(pen, new PointF[] { new PointF(4.4f, 17.2f), new PointF(10.2f, 11.6f), new PointF(14.4f, 15.6f), new PointF(17.2f, 13f), new PointF(20f, 15.8f) });
                        break;

                    case "pickaxe":
                        g.DrawArc(pen, 3.2f, 3.2f, 17.6f, 17.6f, 300f, 190f);
                        g.DrawLine(pen, 4.9f, 9.4f, 19.6f, 4f);
                        break;
                }
            }
            g.Restore(state);
        }

        static GraphicsPath ArrowDoorPath()
        {
            var p = new GraphicsPath();
            p.AddLine(13.8f, 3.4f, 5.2f, 3.4f);
            p.AddLine(5.2f, 3.4f, 5.2f, 20.6f);
            p.AddLine(5.2f, 20.6f, 13.8f, 20.6f);
            return p;
        }

        // fine ligne lumineuse horizontale (transparent -> bleu -> transparent)
        public static void GlowLine(Graphics g, RectangleF r, Color core, int alpha)
        {
            if (r.Width <= 2f) return;
            var rect = new RectangleF(r.X, r.Y, r.Width, Math.Max(2f, r.Height));
            using (var lg = new LinearGradientBrush(rect, Color.Transparent, Color.Transparent, 0f))
            {
                var blend = new ColorBlend(3);
                blend.Colors = new Color[] { Color.FromArgb(0, core), Color.FromArgb(alpha, core), Color.FromArgb(0, core) };
                blend.Positions = new float[] { 0f, 0.5f, 1f };
                lg.InterpolationColors = blend;
                using (var p = Round(rect, rect.Height / 2f))
                    g.FillPath(lg, p);
            }
        }

        // halo elliptique doux
        public static void Glow(Graphics g, RectangleF area, Color color, int alpha)
        {
            if (area.Width <= 1f || area.Height <= 1f) return;
            using (var p = new GraphicsPath())
            {
                p.AddEllipse(area);
                using (var pg = new PathGradientBrush(p))
                {
                    pg.CenterColor = Color.FromArgb(alpha, color);
                    pg.SurroundColors = new Color[] { Color.FromArgb(0, color) };
                    g.FillPath(pg, p);
                }
            }
        }

        public static Color Alpha(Color c, int a)
        {
            return Color.FromArgb(Math.Max(0, Math.Min(255, a)), c.R, c.G, c.B);
        }

        public static Color Mix(Color a, Color b, float t)
        {
            t = Math.Max(0f, Math.Min(1f, t));
            return Color.FromArgb(
                (int)(a.R + (b.R - a.R) * t),
                (int)(a.G + (b.G - a.G) * t),
                (int)(a.B + (b.B - a.B) * t));
        }
    }
}
