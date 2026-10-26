using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading.Tasks;

namespace FNHybrid
{
    public class EpicLoginResult
    {
        public string AccessToken;
        public string RefreshToken;
        public string AccountId;
        public string DisplayName;
        public string ExchangeCode;
        // expiration du jeton d'accès (UTC), 0 si inconnue
        public DateTime ExpiresAt;
    }

    public class DeviceAuthInfo
    {
        public string UserCode;
        public string DeviceCode;
        public string VerificationUriComplete;
    }

    // Connexion à Epic Games avec la méthode "device code" :
    // le launcher demande un code (ex: HNLFZBSW), l'utilisateur le valide sur
    // https://www.epicgames.com/id/activate?userCode=..., puis on récupère le token.
    // Client public utilisé : fortniteSwitchGameClient (device_code + refresh_token).
    // Aucun mot de passe n'est jamais saisi ni stocké par le launcher.
    public class EpicAuth
    {
        const string CLIENT_ID = "98f7e42c2e3a4f86a74eb43fbb41ed39";
        const string CLIENT_SECRET = "0a2449a2-001a-451e-afec-3e812901c4d7";
        const string BASE = "https://account-public-service-prod.ol.epicgames.com/account/api/";

        readonly HttpClient _http;
        Dictionary<string, object> _lastToken;
        volatile bool _cancel;

        public EpicAuth()
        {
            _http = new HttpClient(new HttpClientHandler
            {
                // la connexion ne depend pas du proxy système (évite les blocages si le
                // proxy du mode privé reste actif d'une session précédente)
                UseProxy = false
            });
            _http.Timeout = TimeSpan.FromSeconds(30);
            _http.DefaultRequestHeaders.UserAgent.ParseAdd("XHybrid/2.0");
        }

        public void CancelWait()
        {
            _cancel = true;
        }

        static AuthenticationHeaderValue Basic()
        {
            var raw = Convert.ToBase64String(Encoding.UTF8.GetBytes(CLIENT_ID + ":" + CLIENT_SECRET));
            return new AuthenticationHeaderValue("Basic", raw);
        }

        static AuthenticationHeaderValue Bearer(string token)
        {
            return new AuthenticationHeaderValue("Bearer", token);
        }

        async Task<string> TokenType(string grant, Dictionary<string, string> extra)
        {
            var form = new List<KeyValuePair<string, string>>();
            form.Add(new KeyValuePair<string, string>("grant_type", grant));
            if (extra != null) form.AddRange(extra);

            var req = new HttpRequestMessage(HttpMethod.Post, BASE + "oauth/token");
            req.Headers.Authorization = Basic();
            req.Content = new FormUrlEncodedContent(form);

            var resp = await _http.SendAsync(req);
            var body = await resp.Content.ReadAsStringAsync();
            if (!resp.IsSuccessStatusCode)
                throw new Exception("OAuth " + grant + " (" + (int)resp.StatusCode + ") : " + body);

            var j = JsonH.Parse<Dictionary<string, object>>(body);
            _lastToken = j;
            return Convert.ToString(j["access_token"]);
        }

        public async Task<DeviceAuthInfo> StartDeviceAuth()
        {
            _cancel = false;
            var clientToken = await TokenType("client_credentials", null);

            var req = new HttpRequestMessage(HttpMethod.Post, BASE + "oauth/deviceAuthorization");
            req.Headers.Authorization = Bearer(clientToken);
            var resp = await _http.SendAsync(req);
            var body = await resp.Content.ReadAsStringAsync();
            if (!resp.IsSuccessStatusCode)
                throw new Exception("deviceAuthorization (" + (int)resp.StatusCode + ") : " + body);

            var j = JsonH.Parse<Dictionary<string, object>>(body);
            var info = new DeviceAuthInfo
            {
                UserCode = Convert.ToString(j["user_code"]),
                DeviceCode = Convert.ToString(j["device_code"]),
            };
            info.VerificationUriComplete = j.ContainsKey("verification_uri_complete")
                ? Convert.ToString(j["verification_uri_complete"])
                : Convert.ToString(j["verification_uri"]);
            return info;
        }

        public async Task<EpicLoginResult> WaitDeviceLogin(DeviceAuthInfo info)
        {
            var interval = 5;
            while (true)
            {
                if (_cancel) throw new OperationCanceledException("Connexion annulée.");
                await Task.Delay(interval * 1000);
                if (_cancel) throw new OperationCanceledException("Connexion annulée.");
                var extra = new Dictionary<string, string>();
                extra.Add("device_code", info.DeviceCode);
                try
                {
                    await TokenType("device_code", extra);
                    break;
                }
                catch (Exception ex)
                {
                    var msg = ex.Message;
                    var pending = msg.IndexOf("authorization_pending", StringComparison.OrdinalIgnoreCase) >= 0;
                    var slow = msg.IndexOf("slow_down", StringComparison.OrdinalIgnoreCase) >= 0
                            || msg.IndexOf("rate_limit", StringComparison.OrdinalIgnoreCase) >= 0;
                    if (pending) continue;
                    if (slow) { interval = Math.Min(interval + 3, 20); continue; }
                    throw;
                }
            }

            return await BuildResult(_lastToken);
        }

        // Restaure une session à partir du refresh token stocké (chiffré DPAPI).
        public async Task<EpicLoginResult> Refresh(string refreshToken)
        {
            if (string.IsNullOrEmpty(refreshToken))
                throw new Exception("Aucun jeton de session enregistré.");

            var extra = new Dictionary<string, string>();
            extra.Add("refresh_token", refreshToken);
            await TokenType("refresh_token", extra);
            var result = await BuildResult(_lastToken);
            if (string.IsNullOrEmpty(result.RefreshToken)) result.RefreshToken = refreshToken;
            return result;
        }

        async Task<EpicLoginResult> BuildResult(Dictionary<string, object> t)
        {
            if (t == null) throw new Exception("Réponse d'authentification vide.");

            var result = new EpicLoginResult
            {
                AccessToken = Convert.ToString(t["access_token"]),
                AccountId = t.ContainsKey("account_id") ? Convert.ToString(t["account_id"]) : null,
                RefreshToken = t.ContainsKey("refresh_token") ? Convert.ToString(t["refresh_token"]) : null,
            };
            result.DisplayName = t.ContainsKey("displayName") ? Convert.ToString(t["displayName"]) : null;

            double seconds = 0;
            if (t.ContainsKey("expires_in"))
                double.TryParse(Convert.ToString(t["expires_in"]), out seconds);
            if (seconds > 0)
                result.ExpiresAt = DateTime.UtcNow.AddSeconds(seconds);

            if (string.IsNullOrEmpty(result.DisplayName) && !string.IsNullOrEmpty(result.AccountId))
                result.DisplayName = await FetchDisplayName(result.AccessToken, result.AccountId);

            if (string.IsNullOrEmpty(result.DisplayName))
                result.DisplayName = result.AccountId;

            return result;
        }

        // pseudo réel du compte connecté (jamais inventé)
        async Task<string> FetchDisplayName(string accessToken, string accountId)
        {
            try
            {
                var req = new HttpRequestMessage(HttpMethod.Get, BASE + "public/account/" + accountId);
                req.Headers.Authorization = Bearer(accessToken);
                var resp = await _http.SendAsync(req);
                if (!resp.IsSuccessStatusCode) return null;
                var body = await resp.Content.ReadAsStringAsync();
                var j = JsonH.Parse<Dictionary<string, object>>(body);
                return j.ContainsKey("displayName") ? Convert.ToString(j["displayName"]) : null;
            }
            catch (Exception)
            {
                return null;
            }
        }

    }
}
