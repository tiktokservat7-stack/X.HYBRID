using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;

namespace FNHybrid
{
    // Recherche et lancement de FortniteClient-Win64-Shipping.exe (le JEU, pas le
    // bootstrapper qui ouvre Epic Games Launcher) avec les paramètres d'authentification.
    public static class GameLauncher
    {
        public const string ExeName = "FortniteClient-Win64-Shipping.exe";

        static string ExeIn(string installRoot)
        {
            return Path.Combine(installRoot, "FortniteGame", "Binaries", "Win64", ExeName);
        }

        public static string FindFortniteExe(AppConfig cfg, List<string> tested)
        {
            // 1. chemin choisi dans la config
            if (!string.IsNullOrEmpty(cfg.FortniteExe))
            {
                if (File.Exists(cfg.FortniteExe)) return cfg.FortniteExe;
                if (tested != null) tested.Add(cfg.FortniteExe + " (fichier inexistant)");
            }

            // 2. emplacements standards
            string[] roots =
            {
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                @"C:\Program Files",
                @"C:\Program Files (x86)",
                @"D:\Program Files",
                @"D:\Epic Games",
            };
            string[] subDirs = { @"Epic Games\Fortnite", @"Fortnite\Epic Games\Fortnite", @"Fortnite" };

            foreach (var root in roots)
            {
                if (string.IsNullOrEmpty(root)) continue;
                foreach (var sub in subDirs)
                {
                    var exe = ExeIn(Path.Combine(root, sub));
                    if (tested != null) tested.Add(exe);
                    if (File.Exists(exe)) return exe;
                }
            }

            // 3. raccourcis .url du Bureau : ils contiennent souvent "IconFile=" avec
            //    le chemin du jeu (ex: ...Fortnite\FortniteGame\Binaries\Win64\...)
            string[] desks =
            {
                Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
                @"C:\Users\Public\Desktop",
            };
            foreach (var desk in desks)
            {
                if (string.IsNullOrEmpty(desk) || !Directory.Exists(desk)) continue;
                foreach (var url in Directory.GetFiles(desk, "*.url"))
                {
                    var icon = ReadIconFileFromUrl(url);
                    if (string.IsNullOrEmpty(icon)) continue;
                    var sep = icon.IndexOf(@"\FortniteGame\Binaries\Win64\", StringComparison.OrdinalIgnoreCase);
                    if (sep < 0) continue;
                    var install = icon.Substring(0, sep);
                    var exe = ExeIn(install);
                    if (tested != null) tested.Add(exe + " icon=" + icon);
                    if (File.Exists(exe)) return exe;
                }
            }

            // 4. manifests de l'Epic Games Launcher
            try
            {
                var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                    "Epic", "EpicGamesLauncher", "Data", "Manifests");
                if (Directory.Exists(dir))
                {
                    foreach (var file in Directory.GetFiles(dir, "*.item"))
                    {
                        try
                        {
                            var j = JsonH.Parse<Dictionary<string, object>>(File.ReadAllText(file));
                            var appName = j.ContainsKey("AppName") ? Convert.ToString(j["AppName"]) : "";
                            var mainGame = j.ContainsKey("MainGameAppName") ? Convert.ToString(j["MainGameAppName"]) : "";
                            var install = j.ContainsKey("InstallLocation") ? Convert.ToString(j["InstallLocation"]) : "";
                            if (appName != "FortniteGame" && mainGame != "FortniteGame") continue;
                            if (string.IsNullOrEmpty(install)) continue;
                            var exe = ExeIn(install);
                            if (tested != null) tested.Add(exe);
                            if (File.Exists(exe)) return exe;
                        }
                        catch (Exception) { }
                    }
                }
            }
            catch (Exception) { }

            return null;
        }

        static string ReadIconFileFromUrl(string urlFile)
        {
            try
            {
                foreach (var line in File.ReadAllLines(urlFile))
                {
                    var t = line.Trim();
                    if (t.StartsWith("IconFile=", StringComparison.OrdinalIgnoreCase))
                    {
                        var f = t.Substring("IconFile=".Length).Trim();
                        if (f.Length > 0 && File.Exists(f)) return f;
                    }
                }
            }
            catch (Exception) { }
            return null;
        }

        // ---------- Lancement via Epic Games Launcher ----------

        // Les versions recentes de Fortnite refusent de demarrer autrement que via le
        // "handshake" du Epic Games Launcher (pipe nomme + jeton EAC). On demande donc a
        // EGS de lancer le jeu lui-meme via le protocole com.epicgames.launcher://,
        // pendant que le proxy SSL route le trafic du jeu vers le serveur prive.
        public static string FindEgsLaunchUrl(AppConfig cfg)
        {
            if (!string.IsNullOrEmpty(cfg.EgsLaunchUrl))
                return cfg.EgsLaunchUrl;

            // lit les raccourcis .url du Bureau (Public + utilisateur)
            string[] desks =
            {
                Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
                @"C:\Users\Public\Desktop",
            };
            foreach (var desk in desks)
            {
                if (string.IsNullOrEmpty(desk) || !Directory.Exists(desk)) continue;
                foreach (var url in Directory.GetFiles(desk, "*.url"))
                {
                    try
                    {
                        foreach (var line in File.ReadAllLines(url))
                        {
                            var t = line.Trim();
                            if (t.StartsWith("URL=", StringComparison.OrdinalIgnoreCase)
                                && t.Contains("com.epicgames.launcher://")
                                && t.IndexOf("fortnite", StringComparison.OrdinalIgnoreCase) >= 0)
                                return t.Substring(4);
                        }
                    }
                    catch { }
                }
            }

            // URL protocole standard de Fortnite (manifest M-4fe75bbc...)
            return "com.epicgames.launcher://apps/fn%3A4fe75bbc5a674f4f9b356b5c90567da5%3AFortnite?action=launch&silent=true";
        }

        public static void LaunchViaEgs(AppConfig cfg, bool privateMode)
        {
            if (Process.GetProcessesByName("FortniteClient-Win64-Shipping").Length > 0) return;

            var url = FindEgsLaunchUrl(cfg);

            // On ne tue plus Epic Games Launcher ni EpicWebHelper : les arrêter
            // déconnectait le compte Epic et cassait son cache/session.
            // Le proxy local est activé au niveau système le temps de la partie
            // (WatchGameSession le restaure dès que Fortnite se ferme).
            // En mode en ligne on ne touche a aucun reglage reseau : le jeu doit
            // joindre les vrais serveurs Epic pour qu'une partie puisse demarrer.
            if (privateMode) ProxyManager.EnableSystemProxy();

            // UseShellExecute = true : l'instance d'Epic Games Launcher déjà ouverte
            // reçoit l'URI et lance Fortnite, sans redémarrage ni perte de session.
            var psi = new ProcessStartInfo(url);
            psi.UseShellExecute = true;
            try
            {
                Process.Start(psi);
            }
            catch (Exception ex)
            {
                throw new Exception(
                    "Impossible de demander le lancement à Epic Games Launcher. " +
                    "Vérifie que le lanceur Epic est installé et connecté. (" + ex.Message + ")");
            }
        }

        public static void Launch(string exe, string exchangeCode)
        {
            var args = "-AUTH_LOGIN=unused"
                + " -AUTH_PASSWORD=" + (exchangeCode ?? "")
                + " -AUTH_TYPE=exchangecode"
                + " -epicapp=Fortnite"
                + " -epicenv=Prod"
                + " -epicovs=1^-1"
                // journal du jeu dans %LOCALAPPDATA%\FortniteGame\Saved\Logs
                + " -log";

            var psi = new ProcessStartInfo(exe, args);
            psi.WorkingDirectory = Path.GetDirectoryName(exe);
            psi.UseShellExecute = false;
            // utilise explicitement notre proxy local pour le trafic HTTPS du jeu
            try
            {
                psi.EnvironmentVariables["https_proxy"] = "http://127.0.0.1:" + ProxyManager.ProxyPort;
                psi.EnvironmentVariables["http_proxy"] = "http://127.0.0.1:" + ProxyManager.ProxyPort;
                psi.EnvironmentVariables["HTTPS_PROXY"] = "http://127.0.0.1:" + ProxyManager.ProxyPort;
            }
            catch (Exception) { }
            Process.Start(psi);
        }
    }
}
