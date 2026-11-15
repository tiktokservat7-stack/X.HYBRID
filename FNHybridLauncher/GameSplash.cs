using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.IO;

namespace FNHybrid
{
    // Remplace les images de démarrage de Fortnite (écran « INITIALISATION… »
    // d'EasyAntiCheat + Splash.bmp du jeu) par une image X.HYBRID, uniquement
    // pour les lancements faits depuis l'app. Les originaux sont sauvegardés
    // dans le dossier de l'app et remis dès que le jeu est ouvert (ou au
    // prochain démarrage de l'app si elle a été fermée entre-temps).
    public static class GameSplash
    {
        static string BackupDir { get { return Path.Combine(AppConfig.GetDir(), "SplashBackup"); } }

        static IEnumerable<KeyValuePair<string, string>> Targets(string installRoot)
        {
            yield return new KeyValuePair<string, string>("eac.png",
                Path.Combine(installRoot, "FortniteGame", "Binaries", "Win64", "EasyAntiCheat", "SplashScreen.png"));
            yield return new KeyValuePair<string, string>("game.bmp",
                Path.Combine(installRoot, "FortniteGame", "Content", "Splash", "Splash.bmp"));
        }

        static string InstallRootFromExe(string exe)
        {
            // ...\Fortnite\FortniteGame\Binaries\Win64\FortniteClient-Win64-Shipping.exe
            var dir = Path.GetDirectoryName(exe);
            for (int i = 0; i < 3 && dir != null; i++) dir = Path.GetDirectoryName(dir);
            return dir;
        }

        public static void Apply(AppConfig cfg, Action<string> log)
        {
            try
            {
                var exe = GameLauncher.FindFortniteExe(cfg, new List<string>());
                if (string.IsNullOrEmpty(exe)) return;
                var root = InstallRootFromExe(exe);
                if (root == null) return;

                Directory.CreateDirectory(BackupDir);
                File.WriteAllText(Path.Combine(BackupDir, "root.txt"), root);
                int done = 0;
                foreach (var t in Targets(root))
                {
                    var target = t.Value;
                    if (!File.Exists(target)) continue;
                    var backup = Path.Combine(BackupDir, t.Key);
                    // on ne sauvegarde que l'original (pas une image X.HYBRID déjà posée)
                    if (!File.Exists(backup)) File.Copy(target, backup, false);

                    Size size;
                    using (var original = Image.FromFile(backup)) size = original.Size;
                    using (var img = Render(size.Width, size.Height))
                    {
                        if (target.EndsWith(".bmp", StringComparison.OrdinalIgnoreCase))
                        {
                            using (var rgb = new Bitmap(size.Width, size.Height, PixelFormat.Format24bppRgb))
                            {
                                using (var g = Graphics.FromImage(rgb)) g.DrawImage(img, 0, 0, size.Width, size.Height);
                                rgb.Save(target, ImageFormat.Bmp);
                            }
                        }
                        else img.Save(target, ImageFormat.Png);
                    }
                    done++;
                }
                if (done > 0 && log != null)
                    log(MainForm.T("Écran de démarrage X.HYBRID appliqué à Fortnite.", "X.HYBRID splash applied to Fortnite."));
            }
            catch (Exception ex)
            {
                FileLog.Write("SPLASH : application impossible (" + ex.Message + ")");
                Restore();
            }
        }

        // remet les images d'origine de Fortnite
        public static void Restore()
        {
            try
            {
                var rootFile = Path.Combine(BackupDir, "root.txt");
                if (!File.Exists(rootFile)) return;
                var root = File.ReadAllText(rootFile).Trim();
                bool all = true;
                foreach (var t in Targets(root))
                {
                    var backup = Path.Combine(BackupDir, t.Key);
                    if (!File.Exists(backup)) continue;
                    try
                    {
                        File.Copy(backup, t.Value, true);
                        File.Delete(backup);
                    }
                    catch (Exception) { all = false; }
                }
                if (all) File.Delete(rootFile);
            }
            catch (Exception ex) { FileLog.Write("SPLASH : restauration impossible (" + ex.Message + ")"); }
        }

        internal static Bitmap Render(int w, int h)
        {
            var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                g.Clear(Color.FromArgb(3, 10, 22));

                // fond : l'image de l'app, en mode « cover »
                using (var stream = typeof(GameSplash).Assembly.GetManifestResourceStream("LobbyHero.jpg"))
                using (var art = Image.FromStream(stream))
                {
                    float s = Math.Max(w / (float)art.Width, h / (float)art.Height);
                    float iw = art.Width * s, ih = art.Height * s;
                    g.DrawImage(art, (w - iw) / 2f, (h - ih) / 2f, iw, ih);
                }

                // assombrit pour faire ressortir le logo
                using (var veil = new SolidBrush(Color.FromArgb(110, 3, 10, 22)))
                    g.FillRectangle(veil, 0, 0, w, h);
                using (var shade = new LinearGradientBrush(new RectangleF(0, 0, w, h),
                    Color.FromArgb(40, 3, 10, 22), Color.FromArgb(235, 3, 10, 22), 90f))
                    g.FillRectangle(shade, 0, 0, w, h);

                // logo X.HYBRID comme dans l'app : « X. » bleu, « HYBRID » blanc
                const string x = "X.", rest = "HYBRID";
                float target = w * 0.78f;
                float pt = 40f;
                using (var sf = (StringFormat)StringFormat.GenericTypographic.Clone())
                {
                    sf.FormatFlags |= StringFormatFlags.NoWrap;
                    using (var probe = Theme.Brand(pt))
                    {
                        float pw = g.MeasureString(x + rest, probe, new PointF(0, 0), sf).Width;
                        if (pw > 1) pt = pt * target / pw;
                    }
                    using (var f = Theme.Brand(pt))
                    {
                        var szX = g.MeasureString(x, f, new PointF(0, 0), sf);
                        var szR = g.MeasureString(rest, f, new PointF(0, 0), sf);
                        float total = szX.Width + szR.Width;
                        float left = (w - total) / 2f;
                        float top = (h - szX.Height) / 2f - h * 0.04f;

                        Theme.Glow(g, new RectangleF(left, top + szX.Height * 0.15f, total, szX.Height * 0.8f), Theme.Electric, 40);
                        using (var shadow = new SolidBrush(Color.FromArgb(170, 0, 0, 0)))
                        {
                            float d = Math.Max(2f, pt * 0.05f);
                            g.DrawString(x, f, shadow, left + d, top + d, sf);
                            g.DrawString(rest, f, shadow, left + szX.Width + d, top + d, sf);
                        }
                        using (var blue = new LinearGradientBrush(new RectangleF(left, top, Math.Max(1, szX.Width), Math.Max(1, szX.Height)),
                            Color.FromArgb(126, 200, 255), Theme.Electric, 90f))
                            g.DrawString(x, f, blue, left, top, sf);
                        using (var white = new LinearGradientBrush(new RectangleF(left, top, Math.Max(1, total), Math.Max(1, szX.Height)),
                            Color.FromArgb(250, 252, 255), Color.FromArgb(198, 216, 240), 90f))
                            g.DrawString(rest, f, white, left + szX.Width, top, sf);

                        // sous-titre
                        using (var sub = Theme.UI(Math.Max(8f, pt * 0.16f), FontStyle.Bold))
                        using (var ink = new SolidBrush(Color.FromArgb(220, 214, 230, 248)))
                        {
                            const string tag = "LAUNCHER  ·  by SERVAT";
                            var st = g.MeasureString(tag, sub, new PointF(0, 0), sf);
                            g.DrawString(tag, sub, ink, (w - st.Width) / 2f, top + szX.Height + h * 0.02f, sf);
                        }
                    }
                }

                // fine barre bleue en bas, comme dans l'app
                using (var line = new SolidBrush(Theme.Electric))
                    g.FillRectangle(line, 0, h - Math.Max(3, h / 120), w, Math.Max(3, h / 120));
            }
            return bmp;
        }
    }
}
