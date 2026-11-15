using System;
using System.IO;
using System.Net.Security;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;

namespace FNHybrid
{
    // Aiguillage requete par requete des domaines "fngw" (deja dechiffres par le proxy).
    //
    // Le meme domaine (mcp-gc.live.fngw...) sert a la fois le profil / casier du joueur
    // (serveur prive) et la boutique. Pour afficher la VRAIE boutique Fortnite du jour,
    // les requetes de boutique repartent chez Epic, tout le reste reste sur le serveur
    // prive. La connexion du jeu reste ouverte (keep-alive) : chaque reponse est
    // delimitee (Content-Length / chunked) avant de passer a la requete suivante.
    public static class ShopRouter
    {
        public static bool HandlesHost(string host)
        {
            if (string.IsNullOrEmpty(host)) return false;
            var h = host.ToLowerInvariant();
            return h.Contains("fngw") || h.Contains("fortnitecontent");
        }

        static bool IsContentHost(string host)
        {
            return (host ?? "").ToLowerInvariant().Contains("fortnitecontent");
        }

        // page principale des contenus : /content/api/pages/fortnite-game (avec ou sans / et ?...)
        static bool IsMainContentPage(HttpHead req)
        {
            if (!string.Equals(req.Method, "GET", StringComparison.OrdinalIgnoreCase)) return false;
            var p = StripQuery(req.Target).ToLowerInvariant().TrimEnd('/');
            return p == "/content/api/pages/fortnite-game";
        }

        // requetes servies par Epic : catalogue + cles de la boutique, service "boutique"
        static bool ToEpic(string path)
        {
            var p = (path ?? "").ToLowerInvariant();
            return p.StartsWith("/fortnite/api/storefront/")
                || p.StartsWith("/api/boutique/");
        }

        public static async Task Serve(Stream client, string host, int localPort,
                                       Func<string, int, TcpClient> connectReal)
        {
            var cin = new BufReader(client);
            Backend local = null, real = null;
            try
            {
                while (true)
                {
                    var head = await cin.ReadHeadAsync();
                    if (head == null) return;                       // le jeu a ferme
                    var req = HttpHead.Parse(head);
                    if (req == null) return;

                    if (IsContentHost(host) && IsMainContentPage(req))
                    {
                        // salon X.HYBRID (serveur prive) + boutique du jour (Epic)
                        var discard = new MemoryStream();
                        await CopyBody(cin, discard, req.Chunked, req.ContentLength);
                        var page = await Task.Run(() => ContentMerge.Build(host, req.Target, req.AcceptLanguage, localPort));
                        if (page == null)
                        {
                            await Write(client, "HTTP/1.1 502 Bad Gateway\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
                            return;
                        }
                        await Write(client, "HTTP/1.1 200 OK\r\nContent-Type: application/json; charset=utf-8\r\n"
                            + "Content-Length: " + page.Length + "\r\nConnection: keep-alive\r\n\r\n");
                        await client.WriteAsync(page, 0, page.Length);
                        await client.FlushAsync();
                        if (req.Close) return;
                        continue;
                    }

                    bool epic = ToEpic(req.Target);
                    if (epic) FileLog.Write("BOUTIQUE -> Epic : " + req.Method + " " + StripQuery(req.Target));

                    // requete complete en memoire (petites requetes) : on peut la renvoyer
                    // si la connexion gardee ouverte a ete fermee entre-temps par le serveur
                    byte[] body = null;
                    if (!req.IsUpgrade)
                    {
                        var ms = new MemoryStream();
                        await CopyBody(cin, ms, req.Chunked, req.ContentLength);
                        body = ms.ToArray();
                    }

                    Backend be = null;
                    byte[] resHead = null;
                    for (int attempt = 0; attempt < 2; attempt++)
                    {
                        bool reused;
                        if (epic)
                        {
                            reused = real != null && real.Alive;
                            if (!reused)
                            {
                                if (real != null) real.Dispose();
                                real = await Backend.OpenReal(host, connectReal);
                            }
                            be = real;
                        }
                        else
                        {
                            reused = local != null && local.Alive;
                            if (!reused)
                            {
                                if (local != null) local.Dispose();
                                local = await Backend.OpenLocal(localPort);
                            }
                            be = local;
                        }
                        if (be == null) break;

                        try
                        {
                            await be.Stream.WriteAsync(head, 0, head.Length);
                            if (req.IsUpgrade)
                            {
                                // websocket ou autre protocole : on relaie en brut jusqu'a la fin
                                await cin.DrainTo(be.Stream);
                                await RawPump(cin, client, be.Reader, be.Stream);
                                return;
                            }
                            if (body.Length > 0) await be.Stream.WriteAsync(body, 0, body.Length);
                            await be.Stream.FlushAsync();
                            resHead = await be.Reader.ReadHeadAsync();
                        }
                        catch (IOException) { resHead = null; }
                        catch (ObjectDisposedException) { resHead = null; }

                        if (resHead != null) break;
                        be.Dispose();                         // connexion morte
                        if (!reused) break;                   // connexion neuve qui echoue : on abandonne
                    }

                    if (be == null || resHead == null)
                    {
                        await Write(client, "HTTP/1.1 502 Bad Gateway\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
                        return;
                    }

                    // reponse -> jeu
                    bool keepGoing = await RelayResponse(be, client, req.Method, resHead);
                    await client.FlushAsync();
                    if (!keepGoing) return;
                    if (req.Close) return;
                }
            }
            catch (Exception e)
            {
                FileLog.Write("PROXY ROUTEUR : " + host + " : " + e.Message);
            }
            finally
            {
                if (local != null) local.Dispose();
                if (real != null) real.Dispose();
            }
        }

        // renvoie false si la connexion du jeu doit etre fermee apres cette reponse
        static async Task<bool> RelayResponse(Backend be, Stream client, string method, byte[] first)
        {
            while (true)
            {
                var head = first ?? await be.Reader.ReadHeadAsync();
                first = null;
                if (head == null)
                {
                    be.Alive = false;
                    await Write(client, "HTTP/1.1 502 Bad Gateway\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
                    return false;
                }
                var res = HttpHead.Parse(head);
                if (res == null) { be.Alive = false; return false; }

                await client.WriteAsync(head, 0, head.Length);

                int status = res.Status;
                if (status >= 100 && status < 200 && status != 101) continue;   // 100-continue...

                bool noBody = status == 204 || status == 304
                    || string.Equals(method, "HEAD", StringComparison.OrdinalIgnoreCase);
                if (noBody) { if (res.Close) be.Alive = false; return !res.Close; }

                if (res.Chunked || res.ContentLength >= 0)
                {
                    await CopyBody(be.Reader, client, res.Chunked, res.ContentLength);
                    if (res.Close) be.Alive = false;
                    return !res.Close;
                }

                // ni longueur ni chunked : le corps se termine a la fermeture du serveur
                await be.Reader.CopyToEnd(client);
                be.Alive = false;
                return false;
            }
        }

        static async Task CopyBody(BufReader from, Stream to, bool chunked, long length)
        {
            if (chunked)
            {
                while (true)
                {
                    var line = await from.ReadLineAsync();
                    if (line == null) throw new IOException("corps chunked tronque");
                    await to.WriteAsync(line, 0, line.Length);
                    var text = Encoding.ASCII.GetString(line).Trim();
                    int semi = text.IndexOf(';');
                    if (semi >= 0) text = text.Substring(0, semi);
                    long size = Convert.ToInt64(text.Trim(), 16);
                    if (size == 0)
                    {
                        // en-tetes de fin eventuels, jusqu'a la ligne vide
                        while (true)
                        {
                            var t = await from.ReadLineAsync();
                            if (t == null) return;
                            await to.WriteAsync(t, 0, t.Length);
                            if (t.Length <= 2) return;
                        }
                    }
                    await from.CopyExact(to, size + 2);   // donnees + CRLF
                }
            }
            if (length > 0) await from.CopyExact(to, length);
        }

        static async Task RawPump(BufReader clientReader, Stream client, BufReader beReader, Stream be)
        {
            await beReader.DrainTo(client);
            var t1 = Pipe(client, be);
            var t2 = Pipe(be, client);
            await Task.WhenAny(t1, t2);
        }

        static async Task Pipe(Stream from, Stream to)
        {
            var buf = new byte[16384];
            try
            {
                while (true)
                {
                    int n = await from.ReadAsync(buf, 0, buf.Length);
                    if (n <= 0) break;
                    await to.WriteAsync(buf, 0, n);
                }
            }
            catch (Exception) { }
        }

        static Task Write(Stream s, string text)
        {
            var b = Encoding.ASCII.GetBytes(text);
            return s.WriteAsync(b, 0, b.Length);
        }

        static string StripQuery(string target)
        {
            if (target == null) return "";
            int q = target.IndexOf('?');
            return q >= 0 ? target.Substring(0, q) : target;
        }

        // ---------- connexions vers les serveurs ----------

        sealed class Backend : IDisposable
        {
            public TcpClient Tcp;
            public Stream Stream;
            public BufReader Reader;
            public bool Alive = true;

            public static async Task<Backend> OpenLocal(int port)
            {
                var tcp = new TcpClient();
                try
                {
                    await tcp.ConnectAsync(System.Net.IPAddress.Loopback, port);
                    var s = tcp.GetStream();
                    return new Backend { Tcp = tcp, Stream = s, Reader = new BufReader(s) };
                }
                catch (Exception e)
                {
                    FileLog.Write("PROXY ROUTEUR : serveur prive injoignable : " + e.Message);
                    tcp.Dispose();
                    return null;
                }
            }

            public static async Task<Backend> OpenReal(string host, Func<string, int, TcpClient> connect)
            {
                var tcp = connect(host, 443);
                if (tcp == null) return null;
                try
                {
                    var ssl = new SslStream(tcp.GetStream(), false);
                    await ssl.AuthenticateAsClientAsync(host);
                    return new Backend { Tcp = tcp, Stream = ssl, Reader = new BufReader(ssl) };
                }
                catch (Exception e)
                {
                    FileLog.Write("PROXY ROUTEUR : TLS Epic impossible (" + host + ") : " + e.Message);
                    tcp.Dispose();
                    return null;
                }
            }

            public void Dispose()
            {
                Alive = false;
                try { Stream.Dispose(); } catch (Exception) { }
                try { Tcp.Dispose(); } catch (Exception) { }
            }
        }

        // ---------- lecture HTTP bufferisee ----------

        sealed class BufReader
        {
            readonly Stream _s;
            readonly byte[] _buf = new byte[32768];
            int _pos, _len;

            public BufReader(Stream s) { _s = s; }

            async Task<bool> Fill()
            {
                if (_pos > 0 && _pos == _len) { _pos = 0; _len = 0; }
                if (_len == _buf.Length)
                {
                    if (_pos == 0) return false;                  // tampon plein
                    Buffer.BlockCopy(_buf, _pos, _buf, 0, _len - _pos);
                    _len -= _pos; _pos = 0;
                }
                int n = await _s.ReadAsync(_buf, _len, _buf.Length - _len);
                if (n <= 0) return false;
                _len += n;
                return true;
            }

            // en-tetes complets (jusqu'a la ligne vide incluse), null si fin de flux
            public async Task<byte[]> ReadHeadAsync()
            {
                var acc = new MemoryStream();
                int matched = 0;                                   // progression dans \r\n\r\n
                while (true)
                {
                    if (_pos == _len && !await Fill())
                        return null;
                    while (_pos < _len)
                    {
                        byte b = _buf[_pos++];
                        acc.WriteByte(b);
                        if ((matched % 2 == 0 && b == (byte)'\r') || (matched % 2 == 1 && b == (byte)'\n'))
                            matched++;
                        else
                            matched = (b == (byte)'\r') ? 1 : 0;
                        if (matched == 4) return acc.ToArray();
                        if (acc.Length > 65536) throw new IOException("en-tetes trop longs");
                    }
                }
            }

            public async Task<byte[]> ReadLineAsync()
            {
                var acc = new MemoryStream();
                while (true)
                {
                    if (_pos == _len && !await Fill())
                        return acc.Length > 0 ? acc.ToArray() : null;
                    while (_pos < _len)
                    {
                        byte b = _buf[_pos++];
                        acc.WriteByte(b);
                        if (b == (byte)'\n') return acc.ToArray();
                        if (acc.Length > 65536) throw new IOException("ligne trop longue");
                    }
                }
            }

            public async Task CopyExact(Stream to, long count)
            {
                while (count > 0)
                {
                    if (_pos == _len && !await Fill())
                        throw new IOException("corps tronque");
                    int n = (int)Math.Min(count, _len - _pos);
                    await to.WriteAsync(_buf, _pos, n);
                    _pos += n;
                    count -= n;
                }
            }

            public async Task CopyToEnd(Stream to)
            {
                while (true)
                {
                    if (_pos < _len)
                    {
                        await to.WriteAsync(_buf, _pos, _len - _pos);
                        _pos = _len;
                    }
                    if (!await Fill()) return;
                }
            }

            // donne ce qui est deja en tampon (avant de passer en relais brut)
            public async Task DrainTo(Stream to)
            {
                if (_pos < _len)
                {
                    await to.WriteAsync(_buf, _pos, _len - _pos);
                    _pos = _len;
                }
            }
        }

        sealed class HttpHead
        {
            public string Method, Target, AcceptLanguage;
            public int Status;
            public long ContentLength = -1;
            public bool Chunked, Close, IsUpgrade;

            public static HttpHead Parse(byte[] raw)
            {
                var text = Encoding.ASCII.GetString(raw);
                var lines = text.Split(new[] { "\r\n" }, StringSplitOptions.None);
                if (lines.Length == 0) return null;
                var first = lines[0].Split(new[] { ' ' }, 3);
                if (first.Length < 2) return null;

                var h = new HttpHead();
                if (first[0].StartsWith("HTTP/", StringComparison.OrdinalIgnoreCase))
                {
                    int st;
                    int.TryParse(first[1], out st);
                    h.Status = st;
                    h.Close = first[0].EndsWith("/1.0");
                }
                else
                {
                    h.Method = first[0];
                    h.Target = first[1];
                    h.Close = first.Length > 2 && first[2].Trim().EndsWith("/1.0");
                }

                for (int i = 1; i < lines.Length; i++)
                {
                    var l = lines[i];
                    int c = l.IndexOf(':');
                    if (c <= 0) continue;
                    var name = l.Substring(0, c).Trim().ToLowerInvariant();
                    var val = l.Substring(c + 1).Trim();
                    if (name == "content-length")
                    {
                        long len;
                        if (long.TryParse(val, out len)) h.ContentLength = len;
                    }
                    else if (name == "transfer-encoding")
                        h.Chunked = val.ToLowerInvariant().Contains("chunked");
                    else if (name == "connection")
                    {
                        var v = val.ToLowerInvariant();
                        if (v.Contains("close")) h.Close = true;
                        if (v.Contains("upgrade")) h.IsUpgrade = true;
                    }
                    else if (name == "upgrade")
                        h.IsUpgrade = true;
                    else if (name == "accept-language")
                        h.AcceptLanguage = val;
                }
                if (h.Status == 101) h.IsUpgrade = true;
                return h;
            }
        }
    }
}
