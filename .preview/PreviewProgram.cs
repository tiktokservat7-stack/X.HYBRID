using System;
using System.Collections;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Reflection;
using System.Windows.Forms;

namespace FNHybrid
{
    sealed class ProbeForm : MainForm
    {
        public void Click(Point p)
        {
            var e = new MouseEventArgs(MouseButtons.Left, 1, p.X, p.Y, 0);
            OnMouseDown(e);
        }

        public void Hover(Point p)
        {
            OnMouseMove(new MouseEventArgs(MouseButtons.None, 0, p.X, p.Y, 0));
        }

        public void SendKey(Keys key)
        {
            var m = new Message();
            ProcessCmdKey(ref m, key);
        }

        // repaint offscreen : reconstruit la table des zones cliquables
        public void PaintOffscreen()
        {
            using (var bmp = RenderPreview()) { }
        }

        public RectangleF RectOf(string id)
        {
            var fi = typeof(MainForm).GetField("_hits", BindingFlags.Instance | BindingFlags.NonPublic);
            var list = (IEnumerable)fi.GetValue(this);
            RectangleF found = RectangleF.Empty;
            bool ok = false;
            foreach (var h in list)
            {
                var t = h.GetType();
                var idf = t.GetField("Id");
                if (Convert.ToString(idf.GetValue(h)) != id) continue;
                found = (RectangleF)t.GetField("R").GetValue(h);
                ok = true;
            }
            if (!ok) throw new Exception("zone introuvable : " + id);
            return found;
        }

        public bool HasId(string id)
        {
            PaintOffscreen();
            var fi = typeof(MainForm).GetField("_hits", BindingFlags.Instance | BindingFlags.NonPublic);
            foreach (var h in (IEnumerable)fi.GetValue(this))
                if (Convert.ToString(h.GetType().GetField("Id").GetValue(h)) == id) return true;
            return false;
        }

        public void ClickId(string id)
        {
            PaintOffscreen();
            var r = RectOf(id);
            Click(new Point((int)(r.X + r.Width / 2f), (int)(r.Y + r.Height / 2f)));
        }

        public void CloseMini(string field)
        {
            var fi = typeof(MainForm).GetField(field, BindingFlags.Instance | BindingFlags.NonPublic);
            var w = fi == null ? null : fi.GetValue(this) as Form;
            if (w != null && !w.IsDisposed) w.Close();
            Application.DoEvents();
        }

        public void HoverId(string id)
        {
            PaintOffscreen();
            var r = RectOf(id);
            Hover(new Point((int)(r.X + r.Width / 2f), (int)(r.Y + r.Height / 2f)));
        }
    }

    static class PreviewProgram
    {
        static int _fail;

        static object Field(object o, string name)
        {
            for (var t = o.GetType(); t != null; t = t.BaseType)
            {
                var fi = t.GetField(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.DeclaredOnly);
                if (fi != null) return fi.GetValue(o);
            }
            return null;
        }

        static void Check(string name, bool ok, string detail)
        {
            Console.WriteLine((ok ? "PASS " : "FAIL ") + name + (detail == "" ? "" : " -> " + detail));
            if (!ok) _fail++;
        }

        [STAThread]
        static void Main(string[] args)
        {
            Application.EnableVisualStyles();
            if (args.Length > 1 && args[0] == "--download")
            {
                var info = UpdateService.Check();
                if (info == null) { Console.WriteLine("aucune mise a jour"); return; }
                UpdateService.Download(info, args[1], null, System.Threading.CancellationToken.None);
                Console.WriteLine("telecharge " + new System.IO.FileInfo(args[1]).Length + " octets depuis " + info.AssetUrl);
                return;
            }

            if (args.Length > 0 && args[0] == "--interact")
            {
                RunInteractions(args.Length > 1 ? args[1] : "shots");
                return;
            }

            if (args.Length > 0 && args[0] == "--mini")
            {
                Environment.SetEnvironmentVariable("XHYBRID_PREVIEW", "1");
                var kind = (MiniWindow.Kind)Enum.Parse(typeof(MiniWindow.Kind), args[1], true);
                string st = args.Length > 3 ? args[3] : "";
                using (var f = new ProbeForm())
                {
                    if (!string.IsNullOrEmpty(st)) f.ApplyPreview(st);
                    var w = new MiniWindow(f, kind);
                    if (kind == MiniWindow.Kind.Custom)
                    {
                        foreach (Control c in w.Controls)
                            Console.WriteLine("champ: '" + c.Text + "'");
                    }
                    using (var bmp = w.RenderPreview())
                        bmp.Save(args[2], ImageFormat.Png);
                    w.Close();
                }
                return;
            }

            if (args.Length > 0 && args[0] == "--renderloop")
            {
                Environment.SetEnvironmentVariable("XHYBRID_PREVIEW", "profile");
                using (var f = new MainForm())
                {
                    f.ApplyPreview("profile");
                    for (int i = 0; i < 6; i++)
                    {
                        try
                        {
                            using (var bmp = f.RenderPreview())
                                bmp.Save("shots/loop" + i + ".png", ImageFormat.Png);
                            Console.WriteLine("render " + i + " OK");
                        }
                        catch (Exception ex) { Console.WriteLine("render " + i + " KO: " + ex.GetType().Name + " " + ex.Message); }
                    }
                }
                return;
            }

            string state = args.Length > 0 ? args[0] : "";
            string outPath = args.Length > 1 ? args[1] : "preview.png";

            Environment.SetEnvironmentVariable("XHYBRID_PREVIEW", state);
            using (var f = new MainForm())
            {
                if (args.Length > 2)
                {
                    var parts = args[2].Split('x');
                    f.ClientSize = new Size(int.Parse(parts[0]), int.Parse(parts[1]));
                }
                if (!string.IsNullOrEmpty(state)) f.ApplyPreview(state);
                using (var bmp = f.RenderPreview())
                    bmp.Save(outPath, ImageFormat.Png);
            }
        }

        static void RunInteractions(string shots)
        {
            Environment.SetEnvironmentVariable("XHYBRID_PREVIEW", "1");

            // les tests touchent prefs/comptes : on restaure l'état du joueur après
            string accPath = System.IO.Path.Combine(AppConfig.GetDir(), "accounts.json");
            string prefsPath = System.IO.Path.Combine(AppConfig.GetDir(), "prefs.json");
            string accBackup = System.IO.File.Exists(accPath) ? System.IO.File.ReadAllText(accPath) : null;
            string prefsBackup = System.IO.File.Exists(prefsPath) ? System.IO.File.ReadAllText(prefsPath) : null;

            using (var f = new ProbeForm())
            {
                f.ShowInTaskbar = false;
                f.StartPosition = FormStartPosition.Manual;
                f.Location = new Point(-4000, -3000);
                f.Show();
                Application.DoEvents();

                var prefs = (LauncherPrefs)Field(f, "_prefs");
                Check("préférences chargées", prefs != null, "");

                // ---- aucune session : demande de connexion
                f.ApplyPreview("loginprompt");
                f.PaintOffscreen();
                Check("écran de connexion demandé", Convert.ToString(Field(f, "_view")) == "Login",
                    Convert.ToString(Field(f, "_view")));
                Check("phase de demande de connexion", Convert.ToString(Field(f, "_phase")) == "Idle",
                    Convert.ToString(Field(f, "_phase")));
                Check("bouton Se connecter avec Epic", f.HasId("login:start"), "");
                Check("bouton Plus tard", f.HasId("login:later"), "");
                using (var bmp = f.RenderPreview()) bmp.Save(shots + "/loginprompt.png", ImageFormat.Png);
                f.ClickId("login:later");
                Check("Plus tard revient à l'accueil", Convert.ToString(Field(f, "_view")) == "Home",
                    Convert.ToString(Field(f, "_view")));

                // ---- accueil déconnecté : le bouton principal demande la connexion
                f.PaintOffscreen();
                Check("accueil déconnecté propose la connexion", f.HasId("home:login") && !f.HasId("home:play"), "");

                // ---- plus de panneau de réglages : aucun engrenage
                f.PaintOffscreen();
                Check("aucun bouton engrenage", !f.HasId("title:gear"), "");

                // ---- mise à jour : comparaison et lecture d'une release GitHub
                Check("version 1.1 plus récente que 1.0",
                    UpdateService.CompareVersions("v1.1.0", "v1.0.0") > 0,
                    UpdateService.CompareVersions("v1.1.0", "v1.0.0").ToString());
                Check("même version", UpdateService.CompareVersions("1.0", "v1.0.0") == 0, "");
                Check("version antérieure", UpdateService.CompareVersions("v0.9.2", "v1.0.0") < 0, "");
                Check("URL GitHub normalisée",
                    UpdateService.NormalizeRepo("https://github.com/SERVAT/X.HYBRID.git") == "SERVAT/X.HYBRID",
                    Convert.ToString(UpdateService.NormalizeRepo("https://github.com/SERVAT/X.HYBRID.git")));

                string releaseJson = "{\"tag_name\":\"v1.2.0\",\"body\":\"Corrections et nouveau design.\"," +
                    "\"html_url\":\"https://github.com/\",\"assets\":[{\"name\":\"X.HYBRID V1.exe\"," +
                    "\"browser_download_url\":\"https://github.com/app.exe\",\"size\":500000}]}";
                var upd = UpdateService.ParseRelease(releaseJson);
                Check("release V1.2 détectée", upd != null && upd.Version == "1.2.0" &&
                    upd.AssetUrl.EndsWith(".exe"), upd == null ? "null" : upd.Version);
                Check("release identique ignorée",
                    UpdateService.ParseRelease(releaseJson.Replace("v1.2.0", "v1.1.0")) == null, "");
                Check("release sans exe ignorée",
                    UpdateService.ParseRelease(releaseJson.Replace(".exe", ".zip")) == null, "");

                if (upd != null)
                {
                    using (var uw = new UpdateWindow(upd))
                    using (var bmp = uw.RenderPreview())
                        bmp.Save(shots + "/update.png", ImageFormat.Png);
                }

                // ---- tuiles du bas : chacune ouvre sa petite fenêtre
                f.HoverId("tile:console");
                Check("survol tuile console", (string)Field(f, "_hot") == "tile:console", Convert.ToString(Field(f, "_hot")));
                f.ClickId("tile:console");
                Check("tuile console ouvre sa fenêtre", f.MiniAlive(MiniWindow.Kind.Console), "");
                Check("le grand panneau reste l'accueil", Convert.ToString(Field(f, "_view")) == "Home",
                    Convert.ToString(Field(f, "_view")));
                f.CloseMini("_miniConsole");
                Check("fermeture de la fenêtre console", !f.MiniAlive(MiniWindow.Kind.Console), "");

                f.ClickId("tile:accounts");
                Check("tuile compte ouvre la page Comptes", Convert.ToString(Field(f, "_view")) == "Accounts",
                    Convert.ToString(Field(f, "_view")));
                f.SendKey(Keys.Escape);
                Check("Échap revient à l'accueil", Convert.ToString(Field(f, "_view")) == "Home",
                    Convert.ToString(Field(f, "_view")));

                f.ClickId("tile:play");
                Check("tuile manette ouvre sa fenêtre", f.MiniAlive(MiniWindow.Kind.Custom), "");
                Check("la tuile ne lance pas le jeu directement", !(bool)Field(f, "_launching"), "");
                f.CloseMini("_miniLaunch");

                f.ClickId("tile:login");
                Check("tuile clé ouvre sa fenêtre", f.MiniAlive(MiniWindow.Kind.Private), "");
                f.CloseMini("_miniPrivate");

                Check("Échap / accueil inchangé", Convert.ToString(Field(f, "_view")) == "Home",
                    Convert.ToString(Field(f, "_view")));

                // lecture (sans écriture) du profil du serveur privé
                long vbucks;
                bool vbOk = ProfileTools.TryReadVbucks(f.ToolsServerDir, out vbucks);
                Check("lecture des V-Bucks du profil", vbOk, vbucks.ToString());
                int level;
                bool lvlOk = ProfileTools.TryReadLevel(f.ToolsServerDir, out level);
                Check("lecture du niveau du profil", lvlOk, level.ToString());

                // écriture réelle mais sur une COPIE des profils (données du joueur intactes)
                string src = f.ToolsServerDir;
                if (src != null && System.IO.File.Exists(System.IO.Path.Combine(src, "profiles", "profile0.json")))
                {
                    string tmp = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "xhybrid-profiletest");
                    try
                    {
                        if (System.IO.Directory.Exists(tmp)) System.IO.Directory.Delete(tmp, true);
                        System.IO.Directory.CreateDirectory(System.IO.Path.Combine(tmp, "profiles"));
                        System.IO.Directory.CreateDirectory(System.IO.Path.Combine(tmp, "Config"));
                        System.IO.File.Copy(System.IO.Path.Combine(src, "profiles", "profile0.json"), System.IO.Path.Combine(tmp, "profiles", "profile0.json"));
                        System.IO.File.Copy(System.IO.Path.Combine(src, "profiles", "athena.json"), System.IO.Path.Combine(tmp, "profiles", "athena.json"));
                        System.IO.File.Copy(System.IO.Path.Combine(src, "Config", "config.ini"), System.IO.Path.Combine(tmp, "Config", "config.ini"));

                        string err;
                        bool w1 = ProfileTools.TrySetVbucks(tmp, 12345, out err);
                        bool w2 = ProfileTools.TrySetLevel(tmp, 42, out err);
                        bool w3 = ProfileTools.TrySetPseudo(tmp, "TestName", out err);

                        long gotVb;
                        int gotLvl;
                        ProfileTools.TryReadVbucks(tmp, out gotVb);
                        ProfileTools.TryReadLevel(tmp, out gotLvl);

                        Check("écriture V-Bucks", w1 && gotVb == 12345, gotVb.ToString());
                        Check("écriture niveau", w2 && gotLvl == 42, gotLvl.ToString());
                        Check("écriture pseudo", w3 && ProfileTools.ReadPseudo(tmp) == "TestName", Convert.ToString(ProfileTools.ReadPseudo(tmp)));
                    }
                    catch (Exception ex)
                    {
                        Check("écriture profil (copie)", false, ex.Message);
                    }
                }

                // ---- favori privé : bascule réelle du mode privé
                f.ApplyPreview("connected");
                f.PaintOffscreen();
                bool wasPrivate = (bool)Field(f, "_privateOn");
                Check("état privé lu depuis le système", true, wasPrivate.ToString());

                // ---- menu profil
                f.ApplyPreview("profile");
                f.PaintOffscreen();
                Check("menu profil ouvert", (bool)Field(f, "_profileOpen"), "");
                Check("aucune entrée « ajouter un compte »", !f.HasId("menu:add"), "");
                Check("entrée « Se déconnecter » présente", f.HasId("menu:logout"), "");

                f.ClickId("backdrop");
                Check("clic extérieur ferme le menu", !(bool)Field(f, "_profileOpen"), "");

                // ---- déconnexion depuis le menu profil
                f.ApplyPreview("profile");
                f.ClickId("menu:logout");
                Application.DoEvents();
                Check("déconnexion depuis le menu profil", Field(f, "_login") == null, "");
                Check("le menu se referme", !(bool)Field(f, "_profileOpen"), "");

                // ---- page comptes : plus de bouton d'ajout une fois connecté
                f.ApplyPreview("connected");
                f.PaintOffscreen();
                f.ClickId("tile:accounts");
                Check("page comptes sans bouton d'ajout", !f.HasId("acct:add"), "");
                f.SendKey(Keys.Escape);

                // ---- déconnexion depuis la page Comptes
                f.ApplyPreview("connected");
                f.PaintOffscreen();
                f.ClickId("tile:accounts");
                Check("page comptes connecté", Convert.ToString(Field(f, "_view")) == "Accounts",
                    Convert.ToString(Field(f, "_view")));
                f.ClickId("acct:logout");
                Application.DoEvents();
                Check("déconnexion depuis la page Comptes", Field(f, "_login") == null, "");
                f.PaintOffscreen();
                Check("sans session, le launcher reste sur l'accueil déconnecté", Field(f, "_login") == null, "");

                f.Close();
            }

            try
            {
                if (accBackup != null) System.IO.File.WriteAllText(accPath, accBackup);
                if (prefsBackup != null) System.IO.File.WriteAllText(prefsPath, prefsBackup);
            }
            catch (Exception) { }

            Console.WriteLine(_fail == 0 ? "ALL INTERACTION TESTS PASSED" : (_fail + " FAILURE(S)"));
            Environment.ExitCode = _fail == 0 ? 0 : 1;
        }
    }
}
