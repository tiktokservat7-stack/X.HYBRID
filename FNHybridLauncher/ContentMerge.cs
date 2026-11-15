using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Web.Script.Serialization;

namespace FNHybrid
{
    // Page de contenus du jeu (/content/api/pages/fortnite-game) :
    //  - tout vient du serveur prive (salon, messages, fonds X.HYBRID...),
    //  - sauf la partie boutique, prise sur la vraie page d'Epic pour que la
    //    boutique du jour (servie par Epic) ait sa mise en page.
    public static class ContentMerge
    {
        // cles de la page qui decrivent la boutique
        static readonly string[] ShopKeys = { "mpItemShop", "shopSections", "shopCarousel", "shopoffervisuals" };

        static readonly object _lock = new object();
        static readonly Dictionary<string, KeyValuePair<DateTime, Dictionary<string, object>>> _cache =
            new Dictionary<string, KeyValuePair<DateTime, Dictionary<string, object>>>();
        static readonly TimeSpan CacheFor = TimeSpan.FromMinutes(10);

        static ContentMerge()
        {
            try { ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12; } catch (Exception) { }
        }

        static JavaScriptSerializer Ser()
        {
            return new JavaScriptSerializer { MaxJsonLength = int.MaxValue, RecursionLimit = 1000 };
        }

        public static byte[] Build(string host, string target, string language, int localPort)
        {
            byte[] local = Fetch("http://127.0.0.1:" + localPort + target, language);
            Dictionary<string, object> shop = RealShop(host, target, language);

            if (local == null)
            {
                FileLog.Write("CONTENU : serveur prive injoignable pour la page du salon");
                return null;
            }
            if (shop == null || shop.Count == 0) return local;   // Epic indisponible : page du serveur telle quelle

            try
            {
                var ser = Ser();
                var page = ser.Deserialize<Dictionary<string, object>>(Encoding.UTF8.GetString(local));
                foreach (var kv in shop) page[kv.Key] = kv.Value;
                return Encoding.UTF8.GetBytes(ser.Serialize(page));
            }
            catch (Exception e)
            {
                FileLog.Write("CONTENU : fusion boutique impossible : " + e.Message);
                return local;
            }
        }

        static Dictionary<string, object> RealShop(string host, string target, string language)
        {
            string key = (language ?? "").ToLowerInvariant();
            lock (_lock)
            {
                KeyValuePair<DateTime, Dictionary<string, object>> hit;
                if (_cache.TryGetValue(key, out hit) && DateTime.UtcNow - hit.Key < CacheFor) return hit.Value;
            }

            byte[] real = Fetch("https://" + host + target, language);
            if (real == null) return null;
            try
            {
                var page = Ser().Deserialize<Dictionary<string, object>>(Encoding.UTF8.GetString(real));
                var shop = new Dictionary<string, object>();
                foreach (var k in ShopKeys)
                {
                    object v;
                    if (page.TryGetValue(k, out v) && v != null) shop[k] = v;
                }
                lock (_lock) _cache[key] = new KeyValuePair<DateTime, Dictionary<string, object>>(DateTime.UtcNow, shop);
                FileLog.Write("CONTENU : boutique du jour recuperee chez Epic (" + shop.Count + " blocs)");
                return shop;
            }
            catch (Exception e)
            {
                FileLog.Write("CONTENU : page Epic illisible : " + e.Message);
                return null;
            }
        }

        static byte[] Fetch(string url, string language)
        {
            try
            {
                var req = (HttpWebRequest)WebRequest.Create(url);
                req.Proxy = null;   // jamais via le proxy systeme (qui pointe sur nous)
                req.Timeout = 10000;
                req.ReadWriteTimeout = 10000;
                req.AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate;
                req.UserAgent = "Fortnite/X.HYBRID";
                if (!string.IsNullOrEmpty(language)) req.Headers["Accept-Language"] = language;
                using (var res = (HttpWebResponse)req.GetResponse())
                using (var s = res.GetResponseStream())
                using (var ms = new MemoryStream())
                {
                    s.CopyTo(ms);
                    return ms.ToArray();
                }
            }
            catch (Exception e)
            {
                FileLog.Write("CONTENU : " + url + " : " + e.Message);
                return null;
            }
        }
    }
}
