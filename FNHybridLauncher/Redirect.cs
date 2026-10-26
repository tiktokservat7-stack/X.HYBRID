using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace FNHybrid
{
    // Redirige le domaine du service de jeu Fortnite vers le serveur privé local
    // en ajoutant une entrée dans C:\Windows\System32\drivers\etc\hosts.
    // Le client Fortnite contacte ce service en HTTP (port 80) : le serveur local
    // (LawinServer lancé sur le port 80) répond directement. HTTPS (login Epic)
    // n'est pas touché : la connexion à epicgames.com reste réelle.
    public static class Redirect
    {
        public static string HostsPath
        {
            get { return @"C:\Windows\System32\drivers\etc\hosts"; }
        }

        const string Marker = "# FNHybrid";

        static string EntryLine(AppConfig cfg)
        {
            return (cfg.ServerIp ?? "127.0.0.1") + " " + cfg.RedirectHost;
        }

        // lit le hosts actuel
        static List<string> ReadHosts()
        {
            var text = File.ReadAllText(HostsPath);
            var lines = new List<string>();
            foreach (var raw in text.Split(new[] { '\n' }, StringSplitOptions.None))
            {
                lines.Add(raw.TrimEnd('\r'));
            }
            return lines;
        }

        static void WriteHosts(List<string> lines)
        {
            var sb = new StringBuilder();
            foreach (var l in lines) sb.Append(l).Append('\n');
            File.WriteAllText(HostsPath, sb.ToString(), new UTF8Encoding(false));
        }

        static void FlushDns()
        {
            try
            {
                var psi = new ProcessStartInfo("ipconfig", "/flushdns");
                psi.CreateNoWindow = true;
                psi.UseShellExecute = false;
                psi.WindowStyle = ProcessWindowStyle.Hidden;
                Process.Start(psi).WaitForExit(5000);
            }
            catch { }
        }

        public static bool IsApplied(AppConfig cfg)
        {
            try
            {
                var wanted = EntryLine(cfg).ToLowerInvariant();
                foreach (var l in ReadHosts())
                {
                    if (l.Trim().ToLowerInvariant() == wanted) return true;
                }
            }
            catch { }
            return false;
        }

        // ajoute (ou remplace) l'entrée FNHybrid
        public static void Apply(AppConfig cfg)
        {
            var wanted = EntryLine(cfg).ToLowerInvariant();
            var host = (cfg.RedirectHost ?? "").ToLowerInvariant();
            var lines = ReadHosts();
            lines.RemoveAll(l =>
            {
                var t = l.Trim();
                return t.StartsWith(Marker, StringComparison.OrdinalIgnoreCase)
                    || (host.Length > 0 && t.ToLowerInvariant().Contains(host));
            });
            lines.Add("");
            lines.Add(Marker + " - redirection vers le serveur privé FNHybrid");
            lines.Add(EntryLine(cfg));
            WriteHosts(lines);
            FlushDns();
        }

        // Certains resolveurs (Cloudflare WARP installe un DNS local sur 127.0.2.2)
        // gardent en cache la reponse 127.0.0.1 lue dans le fichier hosts, meme
        // apres sa suppression : le jeu reste alors bloque sur le serveur local.
        // On force une resolution propre avant de rendre la main.
        public static void RepairDnsCache(AppConfig cfg)
        {
            try
            {
                var host = cfg.RedirectHost;
                if (string.IsNullOrEmpty(host)) return;

                bool poisoned = false;
                foreach (var ip in System.Net.Dns.GetHostAddresses(host))
                    if (System.Net.IPAddress.IsLoopback(ip)) poisoned = true;
                if (!poisoned) return;

                FileLog.Write("DNS : " + host + " pointe encore vers la boucle locale, reparation du cache");

                var psi = new ProcessStartInfo("powershell",
                    "-NoProfile -Command \"Restart-Service CloudflareWARP -Force -ErrorAction SilentlyContinue; " +
                    "Stop-Service -Name cbdhsvc* -ErrorAction SilentlyContinue; ipconfig /flushdns | Out-Null\"");
                psi.CreateNoWindow = true;
                psi.UseShellExecute = false;
                psi.WindowStyle = ProcessWindowStyle.Hidden;
                Process.Start(psi).WaitForExit(25000);
                FlushDns();
            }
            catch (Exception) { }
        }

        // enlève l'entrée FNHybrid (retour à l'usage normal d'Epic)
        public static void Remove(AppConfig cfg)
        {
            var host = (cfg.RedirectHost ?? "").ToLowerInvariant();
            var lines = ReadHosts();
            lines.RemoveAll(l =>
            {
                var t = l.Trim();
                return t.StartsWith(Marker, StringComparison.OrdinalIgnoreCase)
                    || (host.Length > 0 && t.ToLowerInvariant().Contains(host));
            });
            WriteHosts(lines);
            FlushDns();
            RepairDnsCache(cfg);
        }
    }
}
