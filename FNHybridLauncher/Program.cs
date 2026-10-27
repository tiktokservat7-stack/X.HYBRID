using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;

namespace FNHybrid
{
    static class Program
    {
        // une seule instance : deux launchers se disputaient le proxy (port 8898)
        // et le serveur (port 80), ce qui cassait le lancement du jeu.
        static Mutex _instance;

        [STAThread]
        static void Main(string[] args)
        {
            // BouncyCastle est embarque dans l'exe : l'application fonctionne sans
            // la DLL a cote du fichier (un seul .exe a heberger).
            AppDomain.CurrentDomain.AssemblyResolve += ResolveEmbedded;

            // controle technique utilise par les tests : charge le chiffrement
            // embarque, verifie le paquet serveur (serveur + Node.js) et sort
            // avec le code 0 si tout va bien (5 si le paquet est incomplet).
            if (args != null && args.Length > 0 && args[0] == "--self-test")
            {
                int code = 0;
                try
                {
                    var type = Type.GetType("Org.BouncyCastle.X509.X509V3CertificateGenerator, BouncyCastle.Cryptography", true);
                    if (type == null) code = 3;
                }
                catch (Exception ex)
                {
                    FileLog.Write("self-test: " + ex);
                    code = 4;
                }
                if (code == 0 && !ServerPayload.Verify()) code = 5;
                Environment.Exit(code);
                return;
            }

            // installation du serveur embarqué sans ouvrir la fenêtre (diagnostic) :
            // le compte rendu est écrit dans %TEMP%\xhybrid-payload.txt
            if (args != null && args.Length > 0 && args[0] == "--install-payload")
            {
                string report = Path.Combine(Path.GetTempPath(), "xhybrid-payload.txt");
                int code = 0;
                try
                {
                    var lines = new List<string>();
                    ServerPayload.Install(delegate (string m) { lines.Add(m); }, null);
                    lines.Add("racine=" + ServerPayload.Root);
                    lines.Add("serveur=" + ServerPayload.ServerDir);
                    lines.Add("node=" + ServerPayload.NodeExe);
                    lines.Add("version installee=" + ServerPayload.InstalledVersion);
                    File.WriteAllLines(report, lines.ToArray());
                }
                catch (Exception ex)
                {
                    code = 6;
                    FileLog.Write("install-payload: " + ex);
                    try { File.WriteAllText(report, "ECHEC : " + ex); } catch (Exception) { }
                }
                Environment.Exit(code);
                return;
            }

            bool created;
            _instance = new Mutex(true, "X.HYBRID.Launcher.SingleInstance", out created);
            if (!created)
            {
                MessageBox.Show(
                    "X.HYBRID V1 est déjà lancé.\n\nFerme la fenêtre déjà ouverte (ou vérifie la zone de notification) avant de relancer.",
                    "X.HYBRID V1", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            // restes d'une mise a jour precedente (ancienne version, fichier .new)
            UpdateService.CleanupLeftovers();

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new MainForm());

            GC.KeepAlive(_instance);
        }

        static Assembly ResolveEmbedded(object sender, ResolveEventArgs e)
        {
            try
            {
                var requested = new AssemblyName(e.Name).Name;
                if (!string.Equals(requested, "BouncyCastle.Cryptography", StringComparison.OrdinalIgnoreCase))
                    return null;

                var self = Assembly.GetExecutingAssembly();
                using (var stream = self.GetManifestResourceStream("BouncyCastle.Cryptography.dll"))
                {
                    if (stream == null) return null;
                    var buffer = new byte[stream.Length];
                    int read = 0;
                    while (read < buffer.Length)
                    {
                        int n = stream.Read(buffer, read, buffer.Length - read);
                        if (n <= 0) break;
                        read += n;
                    }
                    return Assembly.Load(buffer);
                }
            }
            catch (Exception ex)
            {
                FileLog.Write("AssemblyResolve: " + ex.Message);
                return null;
            }
        }
    }
}
