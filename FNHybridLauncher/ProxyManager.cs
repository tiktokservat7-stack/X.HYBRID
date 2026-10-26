using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace FNHybrid
{
    // Le proxy local de HybridProxy doit etre declare comme proxy systeme pour que
    // le client Fortnite l'utilise. On sauvegarde l'ancien reglage pour le restaurer.
    public static class ProxyManager
    {
        public const int ProxyPort = 8898;

        const string KeyPath = @"Software\Microsoft\Windows\CurrentVersion\Internet Settings";

        static int _savedEnable = -1;
        static string _savedServer = "";
        static string _savedOverride = "";
        static bool _active;

        [DllImport("wininet.dll", SetLastError = true, CharSet = CharSet.Auto)]
        static extern bool InternetSetOption(IntPtr hInternet, int dwOption, IntPtr lpBuffer, int dwBufferLength);

        const int INTERNET_OPTION_SETTINGS_CHANGED = 39;
        const int INTERNET_OPTION_REFRESH = 37;

        static RegistryKey SettingsKey()
        {
            return Registry.CurrentUser.OpenSubKey(KeyPath, true);
        }

        public static void Start()
        {
            // neutralise un residu laisse par une session fermee brutalement : si le
            // reglage systeme pointe vers notre port alors qu'on n'est pas a l'origine
            // de cette activation, on le desactive (sinon toutes les apps du PC
            // tentent un proxy potentiellement mort pour rien).
            CleanResidue();

            string error;
            if (!EnsureStarted(out error))
                FileLog.Write("PROXY : demarrage impossible (" + error + ")");
        }

        // le proxy accepte-t-il vraiment des connexions ?
        public static bool IsListening()
        {
            try
            {
                using (var c = new System.Net.Sockets.TcpClient())
                {
                    var r = c.BeginConnect(System.Net.IPAddress.Loopback, ProxyPort, null, null);
                    if (!r.AsyncWaitHandle.WaitOne(300)) return false;
                    c.EndConnect(r);
                    return true;
                }
            }
            catch (Exception) { return false; }
        }

        // demarre le proxy et verifie qu'il ecoute reellement (port deja pris =
        // ancienne instance ou autre programme : sans ce controle le jeu echouait
        // au demarrage avec "Failed to download distribution root").
        public static bool EnsureStarted(out string error)
        {
            error = null;
            if (IsListening()) return true;

            // le port peut être encore tenu quelques secondes par une instance qui
            // vient de se fermer : on réessaie plus longtemps avant d'abandonner.
            for (int attempt = 0; attempt < 5; attempt++)
            {
                try { HybridProxy.Start(); }
                catch (Exception ex) { error = ex.Message; }

                for (int i = 0; i < 15; i++)
                {
                    if (IsListening()) return true;
                    System.Threading.Thread.Sleep(200);
                }
            }

            if (string.IsNullOrEmpty(error))
                error = "le port " + ProxyPort + " est deja utilise";
            return false;
        }

        // desactive le proxy systeme uniquement s'il pointe vers nos ports
        // (8898 actuel, ou 8899 des versions precedentes)
        public static void CleanResidue()
        {
            // ne jamais couper un proxy que cette instance vient d'activer
            if (_active) return;
            try
            {
                using (var k = SettingsKey())
                {
                    var en = k.GetValue("ProxyEnable");
                    var sv = k.GetValue("ProxyServer") as string;
                    bool enabled = en is int && (int)en == 1;
                    var ours = !string.IsNullOrEmpty(sv)
                        && (sv.Equals("127.0.0.1:" + ProxyPort, StringComparison.OrdinalIgnoreCase)
                            || sv.Equals("127.0.0.1:8899", StringComparison.OrdinalIgnoreCase)
                            || sv.Equals("127.0.0.1:8898", StringComparison.OrdinalIgnoreCase));
                    if (!enabled || !ours) return;

                    _savedEnable = -1;
                    _savedServer = "";
                    _savedOverride = "";
                    k.SetValue("ProxyEnable", 0, RegistryValueKind.DWord);
                    k.DeleteValue("ProxyServer", false);
                    RefreshWindows();
                    FileLog.Write("Proxy systeme residuel " + sv + " neutralise au demarrage.");
                }
            }
            catch (Exception) { }
        }

        // le proxy global (registre) est-il actif ?
        public static bool IsGlobal()
        {
            try
            {
                using (var k = SettingsKey())
                {
                    var en = k.GetValue("ProxyEnable");
                    return en is int && (int)en == 1;
                }
            }
            catch (Exception) { return false; }
        }

        public static void Stop()
        {
            if (_active) DisableSystemProxy();
            try { HybridProxy.Stop(); } catch (Exception) { }
        }

        public static void EnableSystemProxy()
        {
            try
            {
                using (var k = SettingsKey())
                {
                    var en = k.GetValue("ProxyEnable");
                    if (en == null || !(en is int)) _savedEnable = -1;
                    else _savedEnable = (int)en;
                    var sv = k.GetValue("ProxyServer") as string;
                    _savedServer = sv ?? "";
                    var ov = k.GetValue("ProxyOverride") as string;
                    _savedOverride = ov ?? "";

                    k.SetValue("ProxyEnable", 1, RegistryValueKind.DWord);
                    k.SetValue("ProxyServer", "127.0.0.1:" + ProxyPort, RegistryValueKind.String);
                    k.SetValue("ProxyOverride", "<local>", RegistryValueKind.String);
                }
                RefreshWindows();
                _active = true;
            }
            catch (Exception) { }
        }


        public static void DisableSystemProxy()
        {
            try
            {
                using (var k = SettingsKey())
                {
                    if (_savedEnable == -1) k.DeleteValue("ProxyEnable", false);
                    else k.SetValue("ProxyEnable", _savedEnable, RegistryValueKind.DWord);
                    if (_savedServer.Length > 0) k.SetValue("ProxyServer", _savedServer, RegistryValueKind.String);
                    else k.DeleteValue("ProxyServer", false);
                    if (_savedOverride.Length > 0) k.SetValue("ProxyOverride", _savedOverride, RegistryValueKind.String);
                    else k.DeleteValue("ProxyOverride", false);
                }
                RefreshWindows();
            }
            catch (Exception) { }
            _active = false;
        }

        static void RefreshWindows()
        {
            try
            {
                InternetSetOption(IntPtr.Zero, INTERNET_OPTION_SETTINGS_CHANGED, IntPtr.Zero, 0);
                InternetSetOption(IntPtr.Zero, INTERNET_OPTION_REFRESH, IntPtr.Zero, 0);
            }
            catch (Exception) { }
        }
    }
}
