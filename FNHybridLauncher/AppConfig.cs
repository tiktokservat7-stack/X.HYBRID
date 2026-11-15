using System;
using System.IO;

namespace FNHybrid
{
    public class AppConfig
    {
        // Valeurs par defaut du mode prive. SANS elles, un nouveau joueur avait
        // RedirectHost = null : la redirection hosts ecrite etait vide et le mode
        // prive ne marchait jamais. On les applique a chaque chargement (ApplyDefaults),
        // ce qui repare aussi les installations qui ont deja "RedirectHost": null.
        public const string DefaultServerIp = "127.0.0.1";
        public const string DefaultRedirectHost = "fortnite-public-service-prod11.ol.epicgames.com";

        public string FortniteExe { get; set; }
        public string ServerDir { get; set; }
        public string NodeExe { get; set; }
        public string ServerIp { get; set; } = DefaultServerIp;

        // Domaine du service de jeu que le privé remplace : redirigé via hosts vers ServerIp (port 80)
        public string RedirectHost { get; set; } = DefaultRedirectHost;

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
            AppConfig c = null;
            try
            {
                var path = GetPath();
                if (File.Exists(path))
                    c = JsonH.Parse<AppConfig>(File.ReadAllText(path));
            }
            catch (Exception)
            {
                // config corrompue → défauts
            }
            if (c == null) c = new AppConfig();
            c.ApplyDefaults();
            return c;
        }

        // Remplit les champs vides (anciennes configs avec "RedirectHost": null) :
        // c'est ce qui permet au mode privé de marcher partout, pas seulement sur le
        // PC d'origine.
        public void ApplyDefaults()
        {
            if (string.IsNullOrWhiteSpace(ServerIp)) ServerIp = DefaultServerIp;
            if (string.IsNullOrWhiteSpace(RedirectHost)) RedirectHost = DefaultRedirectHost;
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
