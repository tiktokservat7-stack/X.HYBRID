using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;

namespace FNHybrid
{
    // Synchronisation des cosmétiques : récupère la liste complète des objets
    // Battle Royale (fortnite-api.com) et l'ajoute au profil athéna du serveur
    // privé via sync_cosmetics.js. Les skins, emotes, sprays... sortis depuis la
    // dernière synchro apparaissent donc tout seuls dans le casier.
    //
    // Le serveur garde ses profils en mémoire (require) : il doit être arrêté
    // avant l'écriture puis redémarré (MainForm.SyncCosmetics s'en charge).
    public static class CosmeticsSync
    {
        public const int IntervalHours = 6;

        static readonly object _lock = new object();

        static string StatePath(string serverDir)
        {
            return Path.Combine(serverDir, "Config", "cosmetics_sync.json");
        }

        // vrai si la dernière synchro date de plus de IntervalHours (ou inconnue)
        public static bool IsDue(string serverDir)
        {
            try
            {
                var path = StatePath(serverDir);
                if (!File.Exists(path)) return true;

                var text = File.ReadAllText(path);
                var m = System.Text.RegularExpressions.Regex.Match(text, "\"lastSync\"\\s*:\\s*\"([^\"]+)\"");
                if (!m.Success) return true;

                DateTime when;
                if (!DateTime.TryParse(m.Groups[1].Value, CultureInfo.InvariantCulture,
                        DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out when))
                    return true;

                return DateTime.UtcNow - when > TimeSpan.FromHours(IntervalHours);
            }
            catch (Exception) { return true; }
        }

        public static bool Run(string nodeExe, string serverDir, Action<string> log, out string error)
        {
            error = null;

            lock (_lock)
            {
                // un autre appel (démarrage + lancement simultanés) a pu la faire
                if (!IsDue(serverDir)) return true;

                var script = Path.Combine(serverDir, "sync_cosmetics.js");
                if (!File.Exists(script))
                {
                    error = T("sync_cosmetics.js introuvable.", "sync_cosmetics.js not found.");
                    return false;
                }

                try
                {
                    var psi = new ProcessStartInfo();
                    psi.FileName = nodeExe;
                    psi.Arguments = "\"sync_cosmetics.js\"";
                    psi.WorkingDirectory = serverDir;
                    psi.UseShellExecute = false;
                    psi.CreateNoWindow = true;
                    psi.RedirectStandardOutput = true;
                    psi.RedirectStandardError = true;
                    psi.StandardOutputEncoding = Encoding.UTF8;
                    psi.StandardErrorEncoding = Encoding.UTF8;

                    using (var p = Process.Start(psi))
                    {
                        var output = new StringBuilder();
                        var err = new StringBuilder();
                        p.OutputDataReceived += (s, e) => { if (e.Data != null) output.AppendLine(e.Data); };
                        p.ErrorDataReceived += (s, e) => { if (e.Data != null) err.AppendLine(e.Data); };
                        p.BeginOutputReadLine();
                        p.BeginErrorReadLine();

                        if (!p.WaitForExit(240000))
                        {
                            try { p.Kill(); } catch (Exception) { }
                            error = T("délai dépassé pendant la récupération.", "timed out while fetching.");
                            return false;
                        }
                        p.WaitForExit(); // laisse sortir les dernières lignes

                        foreach (var line in output.ToString().Split('\n'))
                        {
                            var t = line.Trim();
                            if (t.Length > 0 && log != null) log("[cosmétiques] " + t);
                        }

                        if (p.ExitCode != 0)
                        {
                            var msg = (err.ToString() ?? "").Trim();
                            error = msg.Length > 0
                                ? msg.Split('\n')[0].Trim()
                                : T("échec de la récupération.", "fetch failed.");
                            return false;
                        }
                    }
                    return true;
                }
                catch (Exception ex)
                {
                    error = ex.Message;
                    return false;
                }
            }
        }

        static string T(string fr, string en)
        {
            return LauncherPrefs.Language_ == "en" ? en : fr;
        }
    }
}
