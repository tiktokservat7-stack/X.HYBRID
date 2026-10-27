using System;
using System.IO;

namespace FNHybrid
{
    public class AppConfig
    {
        public string FortniteExe { get; set; }
        public string ServerDir { get; set; }
        public string NodeExe { get; set; }
        public string ServerIp { get; set; }

        // Domaine du service de jeu que le privé remplace : redirigé via hosts vers ServerIp (port 80)
        public string RedirectHost { get; set; }

        // URL protocole com.epicgames.launcher://... du raccourci Fortnite (lecture auto du Bureau)
        public string EgsLaunchUrl { get; set; }

        // Activer la redirection hosts automatiquement au lancement du jeu
        public bool LaunchRedirect { get; set; } = true;

        public static string GetDir()
        {
            // XHYBRID_HOME permet de deplacer tout le dossier de donnees (profil
            // separe) : utilise par les tests d'installation sur un profil vierge.
            var custom = Environment.GetEnvironmentVariable("XHYBRID_HOME");
            if (!string.IsNullOrEmpty(custom)) return custom;

            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "FNHybrid");
        }

        public static string GetPath()
        {
            return Path.Combine(GetDir(), "config.json");
        }

        public static AppConfig Load()
        {
            try
            {
                var path = GetPath();
                if (File.Exists(path))
                {
                    var c = JsonH.Parse<AppConfig>(File.ReadAllText(path));
                    if (c != null) return c;
                }
            }
            catch (Exception)
            {
                // config corrompue → défauts
            }
            return new AppConfig();
        }

        public void Save()
        {
            try
            {
                Directory.CreateDirectory(GetDir());
                File.WriteAllText(GetPath(), JsonH.Dump(this));
            }
            catch (Exception)
            {
                // pas critique
            }
        }
    }
}
