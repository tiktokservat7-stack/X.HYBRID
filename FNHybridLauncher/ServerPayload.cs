using System;
using System.IO;
using System.IO.Compression;
using System.Reflection;

namespace FNHybrid
{
    // Le serveur privé (LawinServer adapté pour X.HYBRID) et le moteur Node.js sont
    // embarqués dans l'exe (ressource "payload.zip", construite par tools/make-payload.ps1).
    //
    // Au premier besoin, ils sont installés dans %APPDATA%\FNHybrid : l'utilisateur
    // qui télécharge seulement l'exe n'a donc rien d'autre à installer, ni dossier
    // LawinServer, ni Node.js déjà présent sur la machine.
    public static class ServerPayload
    {
        // À incrémenter chaque fois que le contenu embarqué change : les
        // installations existantes récupèrent alors la nouvelle version, sans
        // jamais toucher aux profils ni à la configuration du joueur.
        public const string Version = "1";

        public const string ResourceName = "payload.zip";

        // dossiers contenant les données du joueur : jamais écrasés
        static readonly string[] PlayerData = new string[]
        {
            "server/profiles/", "server/Config/", "server/CloudStorage/"
        };

        public static string Root { get { return AppConfig.GetDir(); } }
        public static string ServerDir { get { return Path.Combine(Root, "server"); } }
        public static string NodeExe { get { return Path.Combine(Root, "node", "node.exe"); } }
        static string Marker { get { return Path.Combine(Root, "payload.version"); } }

        // vrai si l'exe contient bien le paquet (les compilations de test non)
        public static bool HasPayload
        {
            get
            {
                try
                {
                    using (var s = Assembly.GetExecutingAssembly().GetManifestResourceStream(ResourceName))
                        return s != null;
                }
                catch (Exception) { return false; }
            }
        }

        public static bool ServerPresent { get { return File.Exists(Path.Combine(ServerDir, "index.js")); } }
        public static bool NodePresent { get { return File.Exists(NodeExe); } }

        public static string InstalledVersion
        {
            get
            {
                try { return File.Exists(Marker) ? File.ReadAllText(Marker).Trim() : null; }
                catch (Exception) { return null; }
            }
        }

        public static bool IsInstalled
        {
            get { return ServerPresent && NodePresent && InstalledVersion == Version; }
        }

        // Vrai si le paquet embarque contient bien l'essentiel : le serveur, le
        // moteur Node.js, le script de cosmétiques et le profil de départ.
        public static bool Verify()
        {
            try
            {
                using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(ResourceName))
                {
                    if (stream == null)
                    {
                        FileLog.Write("payload : ressource absente de l'exécutable.");
                        return false;
                    }
                    using (var zip = new ZipArchive(stream, ZipArchiveMode.Read))
                    {
                        bool server = false, node = false, sync = false, profile = false;
                        foreach (var e in zip.Entries)
                        {
                            var name = Slash(e.FullName);
                            if (name == "server/index.js") server = true;
                            else if (name == "node/node.exe") node = true;
                            else if (name == "server/sync_cosmetics.js") sync = true;
                            else if (name == "server/profiles/athena.json") profile = true;
                        }
                        if (!server) FileLog.Write("payload : server/index.js absent.");
                        if (!node) FileLog.Write("payload : node/node.exe absent.");
                        if (!sync) FileLog.Write("payload : server/sync_cosmetics.js absent.");
                        if (!profile) FileLog.Write("payload : server/profiles/athena.json absent.");
                        return server && node && sync && profile;
                    }
                }
            }
            catch (Exception ex)
            {
                FileLog.Write("payload : " + ex.Message);
                return false;
            }
        }

        // Installe (ou met à jour) le contenu embarqué dans %APPDATA%\FNHybrid.
        // Retourne le dossier du serveur. progress reçoit un pourcentage.
        public static string Install(Action<string> log, Action<int> progress)
        {
            var self = Assembly.GetExecutingAssembly();
            using (var stream = self.GetManifestResourceStream(ResourceName))
            {
                if (stream == null)
                    throw new Exception("Le serveur embarqué est absent de cet exécutable (payload.zip manquant).");

                bool upgrade = ServerPresent || NodePresent;
                Directory.CreateDirectory(Root);

                int kept = 0, written = 0, failed = 0;
                using (var zip = new ZipArchive(stream, ZipArchiveMode.Read))
                {
                    int total = 0;
                    foreach (var e in zip.Entries) if (e.Name.Length > 0) total++;
                    if (total <= 0) throw new Exception("Le paquet embarqué est vide.");

                    int done = 0, lastPct = -1;
                    foreach (var entry in zip.Entries)
                    {
                        if (entry.Name.Length == 0) continue;   // entrée de dossier

                        var rel = Slash(entry.FullName).Replace('/', Path.DirectorySeparatorChar);
                        var dest = Path.Combine(Root, rel);
                        done++;

                        if (upgrade && IsPlayerData(rel) && File.Exists(dest))
                        {
                            kept++;
                            continue;
                        }

                        try
                        {
                            var parent = Path.GetDirectoryName(dest);
                            if (!string.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);
                            using (var src = entry.Open())
                            using (var dst = new FileStream(dest, FileMode.Create, FileAccess.Write, FileShare.None))
                            {
                                var buf = new byte[81920];
                                int n;
                                while ((n = src.Read(buf, 0, buf.Length)) > 0) dst.Write(buf, 0, n);
                            }
                            written++;
                        }
                        catch (IOException)
                        {
                            // fichier utilisé par le serveur en cours d'exécution : on
                            // le retentera au prochain lancement (marqueur non écrit)
                            failed++;
                        }
                        catch (UnauthorizedAccessException)
                        {
                            failed++;
                        }

                        if (progress != null)
                        {
                            int pct = (int)Math.Min(99, done * 100 / total);
                            if (pct != lastPct) { lastPct = pct; progress(pct); }
                        }
                    }
                }

                if (log != null)
                    log("Contenu embarqué : " + written + " fichier(s) installé(s)"
                        + (kept > 0 ? ", " + kept + " donnée(s) du joueur conservée(s)" : "")
                        + (failed > 0 ? ", " + failed + " fichier(s) verrouillé(s)" : "") + " dans " + Root);

                // marqueur écrit dès que le serveur et le moteur sont en place. Un
                // fichier verrouillé (serveur en cours d'exécution) est signalé mais
                // ne relance pas une extraction de 150 Mo à chaque démarrage.
                if (ServerPresent && NodePresent)
                    File.WriteAllText(Marker, Version);

                if (progress != null) progress(100);
                return ServerDir;
            }
        }

        // les entrées du zip peuvent utiliser '\' ou '/' : on normalise
        static string Slash(string name)
        {
            return (name ?? "").Replace('\\', '/');
        }

        static bool IsPlayerData(string rel)
        {
            var p = Slash(rel);
            foreach (var d in PlayerData)
                if (p.StartsWith(d, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }
    }
}
