using System;
using System.Diagnostics;
using System.IO;
using System.Net.Sockets;
using System.Text;

namespace FNHybrid
{
    // Gestion du serveur privé local (LawinServer, Node.js) écoutant sur le port 80,
    // pour que la redirection hosts (HTTP :80) tombe directement dessus.
    public static class ServerManager
    {
        const int Port = 80;
        static Process _proc;

        public static bool IsRunning()
        {
            try
            {
                using (var c = new TcpClient())
                {
                    var r = c.BeginConnect("127.0.0.1", Port, null, null);
                    if (!r.AsyncWaitHandle.WaitOne(800)) return false;
                    // WaitOne seul ne suffit pas : une connexion refusée se termine
                    // aussi. EndConnect lève une exception si rien n'écoute.
                    c.EndConnect(r);
                    return c.Connected;
                }
            }
            catch { return false; }
        }

        // Moteur Node.js à utiliser : celui installé par X.HYBRID (embarqué dans
        // l'exe, %APPDATA%\FNHybrid\node) en premier, sinon celui du système.
        // null si aucun n'est disponible : l'appelant affiche alors un message clair
        // au lieu de « Le fichier spécifié est introuvable ».
        public static string FindNode(AppConfig cfg)
        {
            try
            {
                if (!string.IsNullOrEmpty(cfg.NodeExe) && File.Exists(cfg.NodeExe)) return cfg.NodeExe;
                if (File.Exists(ServerPayload.NodeExe)) return ServerPayload.NodeExe;

                var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
                var pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
                var p1 = Path.Combine(pf, "nodejs", "node.exe");
                var p2 = Path.Combine(pf86, "nodejs", "node.exe");
                if (File.Exists(p1)) return p1;
                if (File.Exists(p2)) return p2;

                return FindInPath("node.exe");
            }
            catch (Exception) { return null; }
        }

        public static bool NodeAvailable(AppConfig cfg)
        {
            return FindNode(cfg) != null;
        }

        // Un dossier est utilisable seulement s'il contient vraiment le serveur :
        // index.js, structure/ et node_modules/. Un dossier vide (choisi un jour par
        // erreur) ou une installation interrompue ne compte donc pas comme un serveur.
        public static bool IsUsableServerDir(string dir)
        {
            try
            {
                if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return false;
                return File.Exists(Path.Combine(dir, "index.js"))
                    && Directory.Exists(Path.Combine(dir, "structure"))
                    && Directory.Exists(Path.Combine(dir, "node_modules"));
            }
            catch (Exception) { return false; }
        }

        static string FindInPath(string fileName)
        {
            try
            {
                var path = Environment.GetEnvironmentVariable("PATH");
                if (string.IsNullOrEmpty(path)) return null;
                foreach (var raw in path.Split(new char[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    try
                    {
                        var dir = raw.Trim().Trim('"');
                        if (dir.Length == 0) continue;
                        var full = Path.Combine(dir, fileName);
                        if (File.Exists(full)) return full;
                    }
                    catch (Exception) { }
                }
            }
            catch (Exception) { }
            return null;
        }

        static string T(string fr, string en)
        {
            return LauncherPrefs.Language_ == "en" ? en : fr;
        }

        public static void Start(AppConfig cfg, Action<string> log, out Process proc)
        {
            var dir = cfg.ServerDir;
            if (!IsUsableServerDir(dir))
                throw new Exception(T("Le dossier du serveur privé est incomplet : ",
                                      "The private server folder is incomplete: ") + (dir ?? "aucun"));

            var node = FindNode(cfg);
            if (node == null)
                throw new Exception(T("Le moteur du serveur (Node.js) est introuvable. Relance X.HYBRID V1 : il est inclus dans l'exe.",
                                      "The server engine (Node.js) could not be found. Restart X.HYBRID V1: it is bundled in the exe."));

            var psi = new ProcessStartInfo();
            psi.FileName = node;
            psi.Arguments = "index.js";
            psi.WorkingDirectory = dir;
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            psi.EnvironmentVariables["PORT"] = Port.ToString();
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            psi.StandardOutputEncoding = Encoding.UTF8;
            psi.StandardErrorEncoding = Encoding.UTF8;

            var p = Process.Start(psi);
            _proc = p;
            proc = p;

            p.OutputDataReceived += (s, e) => { if (e.Data != null) log("[serveur] " + e.Data); };
            p.ErrorDataReceived += (s, e) => { if (e.Data != null) log("[serveur] " + e.Data); };
            p.EnableRaisingEvents = true;
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();
        }

        public static void Stop()
        {
            if (_proc != null && !_proc.HasExited)
            {
                try { _proc.Kill(); } catch { }
            }
            _proc = null;

            // après un redémarrage du launcher, _proc est perdu : on libère le port
            // 80 pour que le serveur puisse repartir (sinon "Port 80 already in use").
            KillPortOwner();
            for (int i = 0; i < 20; i++)
            {
                if (!IsRunning()) return;
                System.Threading.Thread.Sleep(150);
            }
        }

        // tue uniquement un node qui sert LawinServer (vérifié par sa réponse HTTP)
        static void KillPortOwner()
        {
            try
            {
                var psi = new ProcessStartInfo("netstat", "-ano");
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.RedirectStandardOutput = true;
                var p = Process.Start(psi);
                var output = p.StandardOutput.ReadToEnd();
                p.WaitForExit(4000);

                bool ours = LooksLikeLawinServer();

                foreach (var line in output.Split('\n'))
                {
                    var t = line.Trim();
                    if (!t.StartsWith("TCP", StringComparison.OrdinalIgnoreCase)) continue;
                    if (t.IndexOf(":" + Port + " ", StringComparison.Ordinal) < 0) continue;
                    if (t.IndexOf("LISTENING", StringComparison.OrdinalIgnoreCase) < 0) continue;

                    var parts = t.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length < 5) continue;
                    int pid;
                    if (!int.TryParse(parts[parts.Length - 1], out pid)) continue;

                    try
                    {
                        var node = Process.GetProcessById(pid);
                        if (!node.ProcessName.Equals("node", StringComparison.OrdinalIgnoreCase)) continue;
                        if (!ours) continue;
                        node.Kill();
                        FileLog.Write("Serveur : ancien node (PID " + pid + ") arrêté pour libérer le port 80.");
                    }
                    catch (Exception) { }
                }
            }
            catch (Exception) { }
        }

        static bool LooksLikeLawinServer()
        {
            try
            {
                using (var c = new System.Net.Http.HttpClient())
                {
                    c.Timeout = TimeSpan.FromSeconds(2);
                    var resp = c.GetAsync("http://127.0.0.1:" + Port + "/").Result;
                    var body = resp.Content.ReadAsStringAsync().Result;
                    return (body ?? "").IndexOf("lawinserver", StringComparison.OrdinalIgnoreCase) >= 0;
                }
            }
            catch (Exception) { return false; }
        }

        public static void WaitReady(int seconds)
        {
            for (int i = 0; i < seconds * 4; i++)
            {
                if (IsRunning()) return;
                System.Threading.Thread.Sleep(250);
            }
        }
    }
}
