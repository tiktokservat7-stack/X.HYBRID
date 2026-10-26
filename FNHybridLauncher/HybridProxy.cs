using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using System.Net;
using Org.BouncyCastle.Asn1;
using Org.BouncyCastle.Asn1.X509;
using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.Crypto.Generators;
using Org.BouncyCastle.Crypto.Operators;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Math;
using Org.BouncyCastle.Pkcs;
using Org.BouncyCastle.Security;
using Org.BouncyCastle.X509;

namespace FNHybrid
{
    // Proxy local HTTP(S) :
    //  - les domaines servant au JEU (mcp-gc...fngw, fortnite-public-service, xmpp, matchmaker)
    //    sont interceptes : on joue le serveur TLS avec un certificat signe par notre CA locale,
    //    puis on relaie en clair vers LawinServer (port 80, ou 8081 pour XMPP/Matchmaker).
    //  - tout le reste (connexion de compte Epic Games, launcher, ecom, connect EOS...)
    //    reste un tunnel chiffre intact vers les vrais serveurs Epic : l'authentification
    //    reelle continue de fonctionner.
    public static class HybridProxy
    {
        const int ListenPort = 8898;
        static TcpListener _listener;
        static volatile bool _running;
        static readonly ConcurrentDictionary<string, X509Certificate2> _certCache =
            new ConcurrentDictionary<string, X509Certificate2>(StringComparer.OrdinalIgnoreCase);

        static string CaPfxPath { get { return Path.Combine(AppConfig.GetDir(), "FNHybridCA.pfx"); } }
        static string CaCerPath { get { return Path.Combine(AppConfig.GetDir(), "FNHybridCA.cer"); } }
        const string CaPassword = "FNHybridCA2026";

        static AsymmetricKeyParameter _caPriv;
        static Org.BouncyCastle.X509.X509Certificate _caCert;

        public static void Start()
        {
            if (_running && _listener != null) return;

            EnsureCa();
            InstallCaIfMissing();

            var listener = new TcpListener(IPAddress.Loopback, ListenPort);
            try
            {
                listener.Start();
            }
            catch (Exception)
            {
                try { listener.Stop(); } catch (Exception) { }
                _listener = null;
                _running = false;
                throw;
            }

            _listener = listener;
            _running = true;
            ClearInherit(listener.Server.Handle);
            Task.Run(AcceptLoop);
        }

        // Le serveur privé (node) est lancé en processus enfant par le launcher.
        // Sans ça il hérite du socket d'écoute et garde le port 8898 occupé après
        // la fermeture de l'app : le launcher suivant croit alors que le proxy
        // tourne et le jeu se connecte à un proxy mort.
        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool SetHandleInformation(IntPtr hObject, uint dwMask, uint dwFlags);

        const uint HandleFlagInherit = 0x1;

        static void ClearInherit(IntPtr handle)
        {
            try
            {
                if (handle == IntPtr.Zero || handle == new IntPtr(-1)) return;
                SetHandleInformation(handle, HandleFlagInherit, 0);
            }
            catch (Exception) { }
        }

        public static void Stop()
        {
            _running = false;
            try { if (_listener != null) _listener.Stop(); } catch { }
            _listener = null;
            _certCache.Clear();
        }

        static async Task AcceptLoop()
        {
            while (_running)
            {
                TcpClient client;
                try { client = await _listener.AcceptTcpClientAsync(); }
                catch { break; }
                var c = client;
                Task.Run(() => HandleClient(c));
            }
        }

        // les seuls domaines detournes vers le serveur prive
        static bool IsGameHostException(string host)
        {
            if (string.IsNullOrEmpty(host)) return false;
            var h = host.ToLowerInvariant();
            // Le matchmaking local (matchmaker, mm.*, ws.mm) reste detourne : c'est
            // lui qui renvoie la file d'attente simulee du serveur prive. Le relais
            // vers Epic (pass_through_matchmaking.js) ne s'active que si le jeu
            // presente un vrai jeton Epic, pas le jeton local.
            return h.Contains("fngw")
                || h.Contains("fortnitecontent")
                || h.Contains("fortnite-public-service")
                || h.Contains("xmpp-service")
                || h.Contains("matchmaker")
                || h.StartsWith("mm.") || h.StartsWith("mm-")
                || h.StartsWith("ws.mm")
                || h.Contains("lawinserver");
        }

        static int BackendPort(string host)
        {
            var h = (host ?? "").ToLowerInvariant();
            if (h.Contains("xmpp") || h.Contains("matchmaker")) return 8081;
            return 80;
        }

        static async Task HandleClient(TcpClient client)
        {
            var ns = client.GetStream();
            try
            {
                var buf = new byte[16384];
                int n = await ns.ReadAsync(buf, 0, buf.Length);
                if (n <= 0) return;
                var text = Encoding.ASCII.GetString(buf, 0, n);
                int le = text.IndexOf("\r\n");
                if (le < 0) le = (n > 0) ? n : 0;
                var first = text.Substring(0, le);

                if (first.StartsWith("CONNECT", StringComparison.OrdinalIgnoreCase))
                    await HandleConnect(ns, text, firstLine: first);
                else
                    await HandlePlainHttp(ns, buf, n, text, first);
            }
            catch (Exception e) { FileLog.Write("PROXY ERREUR : " + e.Message); }
            finally { try { client.Close(); } catch { } }
        }

        static async Task HandleConnect(NetworkStream ns, string text, string firstLine)
        {
            // terminer la lecture des en-tetes CONNECT
            int guard = 0;
            while (text.IndexOf("\r\n\r\n") < 0 && guard < 10)
            {
                var b = new byte[4096];
                int r = await ns.ReadAsync(b, 0, b.Length);
                if (r <= 0) return;
                text += Encoding.ASCII.GetString(b, 0, r);
                guard++;
            }

            // "CONNECTION host:port HTTP/1.1" -> on coupe VRAIMENT sur les espaces :
            // l'ancien code prenait "host:port HTTP/1.1" d'un coup et le port tombait a 0
            var parts = firstLine.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2) return;
            var target = parts[1].Trim();
            int port = 443;
            if (!target.StartsWith("["))
            {
                var colon = target.LastIndexOf(':');
                if (colon > 0)
                {
                    int p;
                    if (int.TryParse(target.Substring(colon + 1).Trim(), out p) && p > 0)
                        port = p;
                    target = target.Substring(0, colon).Trim();
                }
            }

            byte[] ok = Encoding.ASCII.GetBytes("HTTP/1.1 200 Connection Established\r\n\r\n");
            await ns.WriteAsync(ok, 0, ok.Length);

            FileLog.Write("PROXY CONNECT : " + target + ":" + port + " -> "
                + (IsGameHostException(target) ? "SERVEUR PRIVE" : "tunnel real"));

            if (IsGameHostException(target))
            {
                var cert = CertFor(target);
                using (var ssl = new SslStream(ns, true))
                {
                    await ssl.AuthenticateAsServerAsync(cert, false, SslProtocols.Tls12, false);
                    using (var backend = new TcpClient())
                    {
                        await backend.ConnectAsync(IPAddress.Loopback, BackendPort(target));
                        var be = backend.GetStream();
                        await Pump2(ssl, be);
                    }
                }
            }
            else
            {
                // tunnel intact vers le vrai serveur
                var backend = ConnectSmart(target, port);
                if (backend == null)
                {
                    // on prelevt le client proprement au lieu d'un reset sec
                    byte[] err = Encoding.ASCII.GetBytes("HTTP/1.1 502 Bad Gateway\r\n\r\n");
                    await ns.WriteAsync(err, 0, err.Length);
                    return;
                }
                using (backend)
                {
                    var be = backend.GetStream();
                    await Pump2(ns, be);
                }
            }
        }

        // connection backend fiable : essaie chaque IP renvoyee par le DNS (IPv4 en
        // priorite : une adresse IPv6 morte fait echouer TcpClient.ConnectAsync d'un coup)
        static TcpClient ConnectSmart(string host, int port)
        {
            IPAddress[] list;
            try { list = Dns.GetHostAddresses(host); }
            catch (Exception e)
            {
                FileLog.Write("PROXY : DNS impossible pour " + host + " : " + e.Message);
                return null;
            }
            // trie : IPv4 d'abord
            Array.Sort(list, (x, y) =>
            {
                if (x.AddressFamily == AddressFamily.InterNetwork && y.AddressFamily == AddressFamily.InterNetwork) return 0;
                if (x.AddressFamily == AddressFamily.InterNetwork) return -1;
                if (y.AddressFamily == AddressFamily.InterNetwork) return 1;
                return 0;
            });
            foreach (var ip in list)
            {
                var c = new TcpClient(ip.AddressFamily);
                try
                {
                    var t = c.ConnectAsync(ip, port);
                    if (t.Wait(5000) && c.Connected)
                    {
                        FileLog.Write("PROXY : backend " + host + " -> " + ip + ":" + port + " OK");
                        return c;
                    }
                    FileLog.Write("PROXY : connect " + host + " " + ip + ":" + port + " timeout");
                }
                catch (Exception e)
                {
                    FileLog.Write("PROXY : connect " + host + " " + ip + ":" + port + " echec : " + e.Message);
                }
                try { c.Dispose(); } catch { }
            }
            FileLog.Write("PROXY : TOUTES les IP de " + host + " ont echoue !");
            return null;
        }

        // requete HTTP en clair envoyee au proxy (absolute-form)
        static async Task HandlePlainHttp(NetworkStream ns, byte[] buf, int n, string text, string firstLine)
        {
            var seg = firstLine.Split(new[] { ' ' }, 3);
            if (seg.Length < 2) return;
            int le = text.IndexOf("\r\n");
            if (le < 0) le = n;

            Uri uri;
            try { uri = new Uri(seg[1]); }
            catch { return; }

            bool game = IsGameHostException(uri.Host);
            string host = game ? "127.0.0.1" : uri.Host;
            int port = game ? BackendPort(uri.Host) : (uri.IsDefaultPort ? 80 : uri.Port);

            try
            {
                using (var backend = new TcpClient())
                {
                    await backend.ConnectAsync(host, port);
                    var be = backend.GetStream();

                    if (game)
                    {
                        // on resume : METHODE + chemin + en-tetes d'origine
                        var rebuilt = seg[0] + " " + uri.PathAndQuery + " HTTP/1.1\r\n";
                        byte[] rb = Encoding.ASCII.GetBytes(rebuilt);
                        await be.WriteAsync(rb, 0, rb.Length);
                        if (le + 2 < n) await be.WriteAsync(buf, le + 2, n - (le + 2));
                        await Pump2(ns, be);
                    }
                    else
                    {
                        await be.WriteAsync(buf, 0, n);
                        await Pump2(ns, be);
                    }
                }
            }
            catch { }
        }

        static async Task Pump2(Stream a, Stream b)
        {
            var t1 = Pip(a, b);
            var t2 = Pip(b, a);
            await Task.WhenAny(t1, t2);
            try { a.Dispose(); } catch { }
            try { b.Dispose(); } catch { }
        }

        static async Task Pip(Stream from, Stream to)
        {
            var buf = new byte[16384];
            while (true)
            {
                int n = await from.ReadAsync(buf, 0, buf.Length);
                if (n <= 0) break;
                try { await to.WriteAsync(buf, 0, n); }
                catch { break; }
            }
        }

        // ---------- Certificats ----------

        static void EnsureCa()
        {
            if (_caCert != null) return;
            if (File.Exists(CaPfxPath))
            {
                try
                {
                    var pfx = new Pkcs12StoreBuilder().Build();
                    using (var fs = File.OpenRead(CaPfxPath))
                    {
                        pfx.Load(fs, CaPassword.ToCharArray());
                        foreach (string alias in pfx.Aliases)
                        {
                            if (pfx.IsKeyEntry(alias))
                            {
                                _caPriv = pfx.GetKey(alias).Key;
                                _caCert = pfx.GetCertificate(alias).Certificate;
                                return;
                            }
                        }
                    }
                }
                catch { }
            }

            var gen = new RsaKeyPairGenerator();
            gen.Init(new KeyGenerationParameters(new SecureRandom(), 2048));
            var kp = gen.GenerateKeyPair();

            var cg = new X509V3CertificateGenerator();
            var name = new X509Name("CN=FNHybrid Local Root CA");
            cg.SetSerialNumber(BigInteger.ValueOf(DateTime.UtcNow.Ticks));
            cg.SetIssuerDN(name);
            cg.SetSubjectDN(name);
            cg.SetNotBefore(DateTime.UtcNow.AddDays(-1));
            cg.SetNotAfter(DateTime.UtcNow.AddYears(10));
            cg.SetPublicKey(kp.Public);
            cg.AddExtension(X509Extensions.BasicConstraints, true, new BasicConstraints(true));
            cg.AddExtension(X509Extensions.KeyUsage, true, new KeyUsage(KeyUsage.KeyCertSign | KeyUsage.CrlSign));
            _caCert = cg.Generate(new Asn1SignatureFactory("SHA256WithRSA", kp.Private));
            _caPriv = kp.Private;

            SaveCa();
        }

        static void SaveCa()
        {
            try
            {
                var certDn = DotNetUtilities.ToX509Certificate(_caCert);
                File.WriteAllBytes(CaCerPath, certDn.Export(X509ContentType.Cert));

                var caRsa = DotNetUtilities.ToRSA((RsaPrivateCrtKeyParameters)_caPriv);
                var withKey = new X509Certificate2(certDn.Export(X509ContentType.Cert));
                withKey.PrivateKey = caRsa;
                File.WriteAllBytes(CaPfxPath, withKey.Export(X509ContentType.Pkcs12, CaPassword));
            }
            catch { }
        }

        static void InstallCaIfMissing()
        {
            try
            {
                if (!File.Exists(CaCerPath)) return;
                var psi = new System.Diagnostics.ProcessStartInfo(
                    @"C:\Windows\System32\certutil.exe",
                    "-addstore -user -f ROOT \"" + CaCerPath + "\"");
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                System.Diagnostics.Process.Start(psi).WaitForExit(8000);
            }
            catch { }
        }

        static X509Certificate2 CertFor(string host)
        {
            X509Certificate2 cached;
            if (_certCache.TryGetValue(host, out cached)) return cached;

            EnsureCa();

            var gen = new RsaKeyPairGenerator();
            gen.Init(new KeyGenerationParameters(new SecureRandom(), 2048));
            var lk = gen.GenerateKeyPair();

            var cg = new X509V3CertificateGenerator();
            cg.SetSerialNumber(BigInteger.ValueOf(DateTime.UtcNow.Ticks));
            cg.SetIssuerDN(_caCert.SubjectDN);
            cg.SetSubjectDN(new X509Name("CN=" + host));
            cg.SetNotBefore(DateTime.UtcNow.AddDays(-1));
            cg.SetNotAfter(DateTime.UtcNow.AddYears(5));
            cg.SetPublicKey(lk.Public);
            cg.AddExtension(X509Extensions.SubjectAlternativeName, false,
                new DerSequence(new GeneralName(GeneralName.DnsName, host)));
            cg.AddExtension(X509Extensions.BasicConstraints, true, new BasicConstraints(false));
            cg.AddExtension(X509Extensions.KeyUsage, true,
                new KeyUsage(KeyUsage.DigitalSignature | KeyUsage.KeyEncipherment));
            cg.AddExtension(X509Extensions.ExtendedKeyUsage, true,
                new ExtendedKeyUsage(new[] { KeyPurposeID.IdKPServerAuth }));

            var bcert = cg.Generate(new Asn1SignatureFactory("SHA256WithRSA", _caPriv));

            var cert = new X509Certificate2(DotNetUtilities.ToX509Certificate(bcert));
            cert.PrivateKey = DotNetUtilities.ToRSA((RsaPrivateCrtKeyParameters)lk.Private);

            _certCache[host] = cert;
            return cert;
        }
    }
}
