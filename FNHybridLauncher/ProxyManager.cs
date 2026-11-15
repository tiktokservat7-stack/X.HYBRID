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

        // le proxy système pointe-t-il vers X.HYBRID ?
        public static bool IsOurs()
        {
            try
            {
                using (var k = SettingsKey())
                {
                    var en = k.GetValue("ProxyEnable");
                    var sv = k.GetValue("ProxyServer") as string;
                    return en is int && (int)en == 1 && sv != null
                        && (sv == "127.0.0.1:" + ProxyPort || sv == "127.0.0.1:8899");
                }
            }
            catch (Exception) { return false; }
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
                    // réglage resté d'une session précédente : l'état d'origine
                    // est « sans proxy », pas notre propre proxy
                    if (_savedServer == "127.0.0.1:" + ProxyPort || _savedServer == "127.0.0.1:8899")
                    {
                        _savedEnable = 0;
                        _savedServer = "";
                    }

                    k.SetValue("ProxyEnable", 1, RegistryValueKind.DWord);
                    k.SetValue("ProxyServer", "127.0.0.1:" + ProxyPort, RegistryValueKind.String);
                    k.SetValue("ProxyOverride", "<local>", RegistryValueKind.String);
                }
                // met aussi à jour la copie que lisent WinHTTP et Fortnite
                ApplyPerConnection(true, "127.0.0.1:" + ProxyPort, "<local>");
                RefreshWindows();
                _active = true;
                ArmExitGuards();
            }
            catch (Exception) { }
        }

        // Le proxy vit dans ce processus : s'il s'arrête, le réglage système doit
        // disparaître avec lui, sinon tout le PC perd internet.
        public static void ReleaseForExit()
        {
            try { if (_active) DisableSystemProxy(); } catch (Exception) { }
            _active = false;
            CleanResidue();
        }

        static bool _guardsArmed;

        static void ArmExitGuards()
        {
            if (_guardsArmed) return;
            _guardsArmed = true;
            try
            {
                AppDomain.CurrentDomain.ProcessExit += delegate { ReleaseForExit(); };
                AppDomain.CurrentDomain.UnhandledException += delegate { ReleaseForExit(); };
            }
            catch (Exception) { }

            // filet de sécurité si l'app est tuée ou plante : un petit script caché
            // attend la fin du processus puis coupe le proxy s'il pointe encore sur nous.
            try
            {
                int pid = Process.GetCurrentProcess().Id;
                string script =
                    "Wait-Process -Id " + pid + " -ErrorAction SilentlyContinue; " +
                    "$k='HKCU:\\" + KeyPath + "'; $s=(Get-ItemProperty $k -ErrorAction SilentlyContinue).ProxyServer; " +
                    "if ($s -eq '127.0.0.1:" + ProxyPort + "' -or $s -eq '127.0.0.1:8899') { " +
                    "Set-ItemProperty $k ProxyEnable 0; Remove-ItemProperty $k ProxyServer -ErrorAction SilentlyContinue }";
                var psi = new ProcessStartInfo("powershell.exe",
                    "-NoProfile -NonInteractive -WindowStyle Hidden -Command \"" + script + "\"")
                {
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    WindowStyle = ProcessWindowStyle.Hidden
                };
                Process.Start(psi);
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
                ApplyPerConnection(_savedEnable == 1 && _savedServer.Length > 0, _savedServer, _savedOverride);
                RefreshWindows();
            }
            catch (Exception) { }
            _active = false;
        }

        // ---------- réglage proxy via l'API Windows (pas seulement le registre) ----------
        // Fortnite lit le proxy avec WinHTTP au démarrage. Écrire seulement les
        // valeurs du registre ne met pas toujours à jour ce que WinHTTP renvoie :
        // le jeu partait alors sans proxy, donc sans le serveur privé.

        const int INTERNET_OPTION_PER_CONNECTION_OPTION = 75;
        const int PER_CONN_FLAGS = 1, PER_CONN_PROXY_SERVER = 2, PER_CONN_PROXY_BYPASS = 3;
        const int PROXY_TYPE_DIRECT = 1, PROXY_TYPE_PROXY = 2;

        [DllImport("wininet.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "InternetSetOptionW")]
        static extern bool InternetSetOptionList(IntPtr h, int option, IntPtr list, int size);
        [DllImport("wininet.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "InternetQueryOptionW")]
        static extern bool InternetQueryOptionList(IntPtr h, int option, IntPtr list, ref int size);

        static int OptionStride { get { return IntPtr.Size == 8 ? 16 : 12; } }
        static int ValueOffset { get { return IntPtr.Size == 8 ? 8 : 4; } }
        static int ListSize { get { return IntPtr.Size == 8 ? 32 : 20; } }

        static IntPtr BuildList(IntPtr options, int count)
        {
            var list = Marshal.AllocHGlobal(ListSize);
            for (int i = 0; i < ListSize; i++) Marshal.WriteByte(list, i, 0);
            Marshal.WriteInt32(list, 0, ListSize);                        // dwSize
            Marshal.WriteIntPtr(list, IntPtr.Size, IntPtr.Zero);          // connexion par défaut
            Marshal.WriteInt32(list, IntPtr.Size * 2, count);              // dwOptionCount
            Marshal.WriteIntPtr(list, ListSize - IntPtr.Size, options);    // pOptions
            return list;
        }

        static int QueryFlags()
        {
            var options = Marshal.AllocHGlobal(OptionStride);
            var list = IntPtr.Zero;
            try
            {
                for (int i = 0; i < OptionStride; i++) Marshal.WriteByte(options, i, 0);
                Marshal.WriteInt32(options, 0, PER_CONN_FLAGS);
                list = BuildList(options, 1);
                int size = ListSize;
                if (!InternetQueryOptionList(IntPtr.Zero, INTERNET_OPTION_PER_CONNECTION_OPTION, list, ref size)) return -1;
                return Marshal.ReadInt32(options, ValueOffset);
            }
            catch (Exception) { return -1; }
            finally
            {
                if (list != IntPtr.Zero) Marshal.FreeHGlobal(list);
                Marshal.FreeHGlobal(options);
            }
        }

        static void ApplyPerConnection(bool enable, string server, string bypass)
        {
            var options = IntPtr.Zero;
            var list = IntPtr.Zero;
            var strServer = IntPtr.Zero;
            var strBypass = IntPtr.Zero;
            try
            {
                // garde la détection auto / script PAC éventuels de l'utilisateur
                int current = QueryFlags();
                int keep = current < 0 ? 0 : (current & ~(PROXY_TYPE_DIRECT | PROXY_TYPE_PROXY));
                int flags = keep | PROXY_TYPE_DIRECT | (enable ? PROXY_TYPE_PROXY : 0);

                options = Marshal.AllocHGlobal(OptionStride * 3);
                for (int i = 0; i < OptionStride * 3; i++) Marshal.WriteByte(options, i, 0);
                strServer = Marshal.StringToHGlobalUni(server ?? "");
                strBypass = Marshal.StringToHGlobalUni(bypass ?? "");

                Marshal.WriteInt32(options, 0, PER_CONN_FLAGS);
                Marshal.WriteInt32(options, ValueOffset, flags);
                Marshal.WriteInt32(options, OptionStride, PER_CONN_PROXY_SERVER);
                Marshal.WriteIntPtr(options, OptionStride + ValueOffset, strServer);
                Marshal.WriteInt32(options, OptionStride * 2, PER_CONN_PROXY_BYPASS);
                Marshal.WriteIntPtr(options, OptionStride * 2 + ValueOffset, strBypass);

                list = BuildList(options, 3);
                if (!InternetSetOptionList(IntPtr.Zero, INTERNET_OPTION_PER_CONNECTION_OPTION, list, ListSize))
                    FileLog.Write("PROXY : réglage Windows refusé (erreur " + Marshal.GetLastWin32Error() + ")");
            }
            catch (Exception ex) { FileLog.Write("PROXY : réglage Windows impossible (" + ex.Message + ")"); }
            finally
            {
                if (list != IntPtr.Zero) Marshal.FreeHGlobal(list);
                if (options != IntPtr.Zero) Marshal.FreeHGlobal(options);
                if (strServer != IntPtr.Zero) Marshal.FreeHGlobal(strServer);
                if (strBypass != IntPtr.Zero) Marshal.FreeHGlobal(strBypass);
            }
        }

        // ---------- vérification : ce que le jeu va réellement voir ----------

        [StructLayout(LayoutKind.Sequential)]
        struct IEProxyConfig { public bool AutoDetect; public IntPtr AutoConfigUrl; public IntPtr Proxy; public IntPtr ProxyBypass; }

        [DllImport("winhttp.dll", SetLastError = true)]
        static extern bool WinHttpGetIEProxyConfigForCurrentUser(ref IEProxyConfig config);
        [DllImport("kernel32.dll")]
        static extern IntPtr GlobalFree(IntPtr mem);

        // proxy tel que WinHTTP (donc Fortnite) le lit
        public static string WinHttpProxy()
        {
            var c = new IEProxyConfig();
            if (!WinHttpGetIEProxyConfigForCurrentUser(ref c)) return null;
            string proxy = c.Proxy == IntPtr.Zero ? null : Marshal.PtrToStringUni(c.Proxy);
            if (c.AutoConfigUrl != IntPtr.Zero) GlobalFree(c.AutoConfigUrl);
            if (c.Proxy != IntPtr.Zero) GlobalFree(c.Proxy);
            if (c.ProxyBypass != IntPtr.Zero) GlobalFree(c.ProxyBypass);
            return proxy;
        }

        // attend que Fortnite puisse voir le proxy avant de le lancer
        public static bool WaitVisibleToGame(int timeoutMs)
        {
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < timeoutMs)
            {
                var p = WinHttpProxy();
                if (p != null && p.IndexOf("127.0.0.1:" + ProxyPort, StringComparison.OrdinalIgnoreCase) >= 0) return true;
                ApplyPerConnection(true, "127.0.0.1:" + ProxyPort, "<local>");
                RefreshWindows();
                System.Threading.Thread.Sleep(200);
            }
            return false;
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
