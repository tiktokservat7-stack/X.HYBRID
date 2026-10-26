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

        public static string FindNode(AppConfig cfg)
        {
            if (!string.IsNullOrEmpty(cfg.NodeExe) && File.Exists(cfg.NodeExe)) return cfg.NodeExe;
            var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            var pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
            var p1 = Path.Combine(pf, "nodejs", "node.exe");
            var p2 = Path.Combine(pf86, "nodejs", "node.exe");
            if (File.Exists(p1)) return p1;
            if (File.Exists(p2)) return p2;
            return "node"; // si présent dans le PATH
        }

        public static void Start(AppConfig cfg, Action<string> log, out Process proc)
        {
            var dir = cfg.ServerDir;
            var node = FindNode(cfg);

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
