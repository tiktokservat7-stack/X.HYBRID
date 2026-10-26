using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace FNHybrid
{
    // Preferences d'interface : fichier separe de la session pour pouvoir les
    // reinitialiser sans toucher aux comptes (et inversement).
    public class LauncherPrefs
    {
        // langue courante, lue par les helpers de traduction de l'interface
        public static string Language_ { get; set; }

        static LauncherPrefs()
        {
            Language_ = "fr";
        }

        public bool Animations { get; set; }
        public bool GlowBorders { get; set; }
        public bool OpenConsoleOnStart { get; set; }
        public bool AutoStartServer { get; set; }
        public bool PersistSession { get; set; }
        public bool NotifySystem { get; set; }
        public bool ConfirmCloseInGame { get; set; }
        public string Language { get; set; }

        public LauncherPrefs()
        {
            Animations = true;
            GlowBorders = true;
            OpenConsoleOnStart = false;
            AutoStartServer = true;
            PersistSession = true;
            NotifySystem = true;
            ConfirmCloseInGame = true;
            Language = "fr";
        }

        public static string Path_
        {
            get { return Path.Combine(AppConfig.GetDir(), "prefs.json"); }
        }

        public static LauncherPrefs Load()
        {
            try
            {
                if (File.Exists(Path_))
                {
                    var p = JsonH.Parse<LauncherPrefs>(File.ReadAllText(Path_));
                    if (p != null)
                    {
                        if (string.IsNullOrEmpty(p.Language)) p.Language = "fr";
                        return p;
                    }
                }
            }
            catch (Exception) { }
            return new LauncherPrefs();
        }

        public void Save()
        {
            try
            {
                Directory.CreateDirectory(AppConfig.GetDir());
                File.WriteAllText(Path_, JsonH.Dump(this));
            }
            catch (Exception) { }
        }
    }

    public class SavedAccount
    {
        public string AccountId { get; set; }
        public string DisplayName { get; set; }
        public string Token { get; set; }        // refresh token chiffre DPAPI (jamais en clair)
        public string AccessToken { get; set; }  // jeton d'acces chiffre DPAPI (reprise rapide)
        public string ExpiresAt { get; set; }    // expiration du jeton d'acces (ISO UTC)
        public string SavedAt { get; set; }
    }

    // Un seul compte Epic a la fois : seul le refresh token est conserve, protege
    // par DPAPI (utilisateur Windows courant). Aucun mot de passe n'est stocke.
    public class AccountBook
    {
        public List<SavedAccount> Accounts { get; set; }
        public string ActiveId { get; set; }

        public AccountBook()
        {
            Accounts = new List<SavedAccount>();
        }

        static readonly object Sync = new object();

        public static string Path_
        {
            get { return Path.Combine(AppConfig.GetDir(), "accounts.json"); }
        }

        public static AccountBook Load()
        {
            try
            {
                if (File.Exists(Path_))
                {
                    var b = JsonH.Parse<AccountBook>(File.ReadAllText(Path_));
                    if (b != null)
                    {
                        if (b.Accounts == null) b.Accounts = new List<SavedAccount>();
                        b.Accounts.RemoveAll(a => a == null || string.IsNullOrEmpty(a.AccountId));
                        return b;
                    }
                }
            }
            catch (Exception) { }
            return new AccountBook();
        }

        public void Save()
        {
            lock (Sync) SaveLocked();
        }

        void SaveLocked()
        {
            try
            {
                Directory.CreateDirectory(AppConfig.GetDir());
                File.WriteAllText(Path_, JsonH.Dump(this));
            }
            catch (Exception) { }
        }

        // Enregistre la session des que les jetons sont obtenus, sans passer par le
        // thread d'interface : si l'application se ferme juste apres, le refresh token
        // (usage unique chez Epic) ne doit pas etre perdu.
        public void SaveTokens(string accountId, string displayName,
            string refreshToken, string accessToken, DateTime expiresAt)
        {
            if (string.IsNullOrEmpty(accountId)) return;
            lock (Sync)
            {
                var acc = Find(accountId);
                if (acc == null)
                {
                    acc = new SavedAccount();
                    acc.AccountId = accountId;
                    Accounts.Add(acc);
                }

                if (!string.IsNullOrEmpty(displayName)) acc.DisplayName = displayName;
                if (!string.IsNullOrEmpty(refreshToken)) acc.Token = SecureStore.Protect(refreshToken);
                if (!string.IsNullOrEmpty(accessToken)) acc.AccessToken = SecureStore.Protect(accessToken);
                if (expiresAt != DateTime.MinValue)
                    acc.ExpiresAt = expiresAt.ToUniversalTime().ToString("o");
                acc.SavedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm");
                ActiveId = acc.AccountId;

                for (int i = Accounts.Count - 1; i >= 0; i--)
                    if (Accounts[i] != acc) Accounts.RemoveAt(i);

                SaveLocked();
            }
        }

        public SavedAccount Find(string id)
        {
            if (string.IsNullOrEmpty(id)) return null;
            foreach (var a in Accounts)
                if (a.AccountId == id) return a;
            return null;
        }

        public void Upsert(EpicLoginResult login, bool keepToken)
        {
            if (login == null || string.IsNullOrEmpty(login.AccountId)) return;
            lock (Sync)
            {
                var acc = Find(login.AccountId);
                if (acc == null)
                {
                    acc = new SavedAccount();
                    acc.AccountId = login.AccountId;
                    Accounts.Add(acc);
                }
                acc.DisplayName = login.DisplayName;
                acc.SavedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm");
                if (keepToken && !string.IsNullOrEmpty(login.RefreshToken))
                    acc.Token = SecureStore.Protect(login.RefreshToken);
                if (keepToken && !string.IsNullOrEmpty(login.AccessToken))
                    acc.AccessToken = SecureStore.Protect(login.AccessToken);
                if (keepToken && login.ExpiresAt != DateTime.MinValue)
                    acc.ExpiresAt = login.ExpiresAt.ToUniversalTime().ToString("o");
                ActiveId = acc.AccountId;

                // un seul compte à la fois : les anciennes sessions sont oubliées
                for (int i = Accounts.Count - 1; i >= 0; i--)
                    if (Accounts[i] != acc) Accounts.RemoveAt(i);

                SaveLocked();
            }
        }

        public void Remove(string id)
        {
            lock (Sync)
            {
                Accounts.RemoveAll(a => a.AccountId == id);
                if (ActiveId == id) ActiveId = null;
                SaveLocked();
            }
        }
    }

    public static class SecureStore
    {
        static readonly byte[] Entropy = Encoding.UTF8.GetBytes("X.HYBRID.session.v1");

        public static string Protect(string value)
        {
            if (string.IsNullOrEmpty(value)) return null;
            try
            {
                var raw = Encoding.UTF8.GetBytes(value);
                var enc = ProtectedData.Protect(raw, Entropy, DataProtectionScope.CurrentUser);
                return Convert.ToBase64String(enc);
            }
            catch (Exception)
            {
                return null;
            }
        }

        public static string Unprotect(string value)
        {
            if (string.IsNullOrEmpty(value)) return null;
            try
            {
                var raw = Convert.FromBase64String(value);
                var dec = ProtectedData.Unprotect(raw, Entropy, DataProtectionScope.CurrentUser);
                return Encoding.UTF8.GetString(dec);
            }
            catch (Exception)
            {
                return null;
            }
        }
    }
}
