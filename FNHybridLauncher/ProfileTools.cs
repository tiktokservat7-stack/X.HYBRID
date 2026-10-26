using System;
using System.Globalization;
using System.IO;
using System.Text;

namespace FNHybrid
{
    // Édition du profil du serveur privé local (LawinServer) : V-Bucks, niveau
    // de compte et pseudo. Le serveur charge ses profils via require(), donc il
    // est redémarré après écriture (voir MainForm.ApplyProfileChanges).
    //
    // Les champs modifiés sont ceux que le CLIENT lit réellement :
    //  - V-Bucks  : common_core.json (solde du lobby / boutique BR) ET
    //               profile0.json (Sauver le monde), pour qu'ils restent alignés
    //  - Niveau   : athena.json -> stats.level, stats.accountLevel, stats.book_level
    //               + l'item de saison (AthenaSeason:...) -> attributes.level
    //  - Pseudo   : Config/config.ini (bUseConfigDisplayName + displayName)
    public static class ProfileTools
    {
        const string MtxId = "\"Currency:MtxPurchased\"";
        const string QuantityId = "\"quantity\"";
        const string LevelId = "\"level\"";
        const string AccountLevelId = "\"accountLevel\"";
        const string BookLevelId = "\"book_level\"";
        const string SeasonItemId = "\"AthenaSeason:athenaseason";

        public static string ProfilesDir(string serverDir) { return Path.Combine(serverDir, "profiles"); }
        static string Profile0(string serverDir) { return Path.Combine(ProfilesDir(serverDir), "profile0.json"); }
        static string CommonCore(string serverDir) { return Path.Combine(ProfilesDir(serverDir), "common_core.json"); }
        static string Athena(string serverDir) { return Path.Combine(ProfilesDir(serverDir), "athena.json"); }
        static string ConfigIni(string serverDir) { return Path.Combine(serverDir, "Config", "config.ini"); }

        static string ReadAll(string path) { return File.ReadAllText(path, Encoding.UTF8); }

        static void WriteAll(string path, string text)
        {
            try
            {
                var backup = path + ".xhybrid.bak";
                if (!File.Exists(backup)) File.Copy(path, backup, true);
            }
            catch (Exception) { }
            File.WriteAllText(path, text, new UTF8Encoding(false));
        }

        static bool ParseNumberAt(string raw, int keyIndex, out long value)
        {
            value = 0;
            int colon = raw.IndexOf(':', keyIndex);
            if (colon < 0) return false;
            int start = colon + 1;
            while (start < raw.Length && !char.IsDigit(raw[start]) && raw[start] != '-') start++;
            int end = start;
            while (end < raw.Length && (char.IsDigit(raw[end]) || raw[end] == '-')) end++;
            return long.TryParse(raw.Substring(start, end - start), NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
        }

        static bool ReplaceNumber(ref string raw, int quotedKeyIndex, long value)
        {
            if (quotedKeyIndex < 0) return false;
            int colon = raw.IndexOf(':', quotedKeyIndex);
            if (colon < 0) return false;
            int start = colon + 1;
            while (start < raw.Length && !char.IsDigit(raw[start]) && raw[start] != '-') start++;
            int end = start;
            while (end < raw.Length && (char.IsDigit(raw[end]) || raw[end] == '-')) end++;
            if (end <= start) return false;
            raw = raw.Substring(0, start) + value.ToString(CultureInfo.InvariantCulture) + raw.Substring(end);
            return true;
        }

        // début du bloc "stats" du profil athéna (les items contiennent aussi
        // des clés "level" : on ne touche qu'aux vraies stats du compte).
        static int StatsRegion(string raw)
        {
            int s = raw.IndexOf("\"stats\"", StringComparison.Ordinal);
            if (s < 0) return -1;
            int a = raw.IndexOf("\"attributes\"", s, StringComparison.Ordinal);
            return a >= 0 ? a : s;
        }

        // ---------- V-Bucks ----------

        public static bool TryReadVbucks(string serverDir, out long value)
        {
            value = 0;
            foreach (var path in new[] { CommonCore(serverDir), Profile0(serverDir) })
            {
                try
                {
                    var raw = ReadAll(path);
                    int i = raw.IndexOf(MtxId, StringComparison.Ordinal);
                    if (i < 0) continue;
                    int q = raw.IndexOf(QuantityId, i, StringComparison.Ordinal);
                    if (q < 0) continue;
                    if (ParseNumberAt(raw, q, out value)) return true;
                }
                catch (Exception) { }
            }
            return false;
        }

        public static bool TrySetVbucks(string serverDir, long value, out string error)
        {
            error = null;
            if (value < 0 || value > 2000000000L)
            {
                error = T("Montant de V-Bucks invalide (0 à 2 000 000 000).", "Invalid V-Bucks amount (0 to 2,000,000,000).");
                return false;
            }

            bool written = false;
            foreach (var path in new[] { CommonCore(serverDir), Profile0(serverDir) })
            {
                try
                {
                    if (!File.Exists(path)) continue;
                    var raw = ReadAll(path);
                    int i = raw.IndexOf(MtxId, StringComparison.Ordinal);
                    if (i < 0) continue;
                    int q = raw.IndexOf(QuantityId, i, StringComparison.Ordinal);
                    if (q < 0 || !ReplaceNumber(ref raw, q, value)) continue;
                    WriteAll(path, raw);
                    written = true;
                }
                catch (Exception ex)
                {
                    if (error == null) error = ex.Message;
                }
            }

            if (!written)
            {
                error = T("Devise V-Bucks absente des profils du serveur.", "V-Bucks currency missing from the server profiles.");
                return false;
            }
            return true;
        }

        // ---------- niveau de compte ----------

        public static bool TryReadLevel(string serverDir, out int value)
        {
            value = 0;
            try
            {
                var raw = ReadAll(Athena(serverDir));
                int region = StatsRegion(raw);
                if (region < 0) return false;
                int i = raw.IndexOf(LevelId, region, StringComparison.Ordinal);
                if (i < 0) return false;
                long lv;
                if (!ParseNumberAt(raw, i, out lv)) return false;
                value = (int)lv;
                return true;
            }
            catch (Exception) { return false; }
        }

        public static bool TrySetLevel(string serverDir, int level, out string error)
        {
            error = null;
            if (level < 1 || level > 1000)
            {
                error = T("Niveau invalide (1 à 1000).", "Invalid level (1 to 1000).");
                return false;
            }
            try
            {
                var path = Athena(serverDir);
                if (!File.Exists(path)) { error = T("athena.json introuvable.", "athena.json not found."); return false; }
                var raw = ReadAll(path);

                bool ok = false;

                // niveau de compte affiché à côté du pseudo
                int region = StatsRegion(raw);
                if (region >= 0)
                {
                    int i = raw.IndexOf(LevelId, region, StringComparison.Ordinal);
                    if (i >= 0 && ReplaceNumber(ref raw, i, level)) ok = true;
                }

                // ancienne clé de niveau, encore lue par certains clients
                int a = raw.IndexOf(AccountLevelId, StringComparison.Ordinal);
                if (a >= 0 && ReplaceNumber(ref raw, a, level)) ok = true;

                // niveau du pass de la saison en cours
                region = StatsRegion(raw);
                if (region >= 0)
                {
                    int b = raw.IndexOf(BookLevelId, region, StringComparison.Ordinal);
                    if (b >= 0 && ReplaceNumber(ref raw, b, level)) ok = true;
                }

                // item de saison : même niveau que le pass (best effort)
                int s = raw.IndexOf(SeasonItemId, StringComparison.Ordinal);
                if (s >= 0)
                {
                    int sl = raw.IndexOf(LevelId, s, StringComparison.Ordinal);
                    if (sl >= 0) ReplaceNumber(ref raw, sl, level);
                }

                if (!ok)
                {
                    error = T("Niveau de compte introuvable dans le profil.", "Account level not found in the profile.");
                    return false;
                }

                WriteAll(path, raw);
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        // ---------- pseudo ----------

        public static string ReadPseudo(string serverDir)
        {
            try
            {
                var path = ConfigIni(serverDir);
                if (!File.Exists(path)) return null;
                bool use = false;
                string name = null;
                foreach (var line in File.ReadAllLines(path))
                {
                    var t = line.Trim();
                    if (t.StartsWith("bUseConfigDisplayName=", StringComparison.OrdinalIgnoreCase))
                        use = t.Substring(t.IndexOf('=') + 1).Trim().Equals("true", StringComparison.OrdinalIgnoreCase);
                    else if (t.StartsWith("displayName=", StringComparison.OrdinalIgnoreCase))
                        name = t.Substring(t.IndexOf('=') + 1).Trim();
                }
                return use ? name : null;
            }
            catch (Exception) { return null; }
        }

        public static bool TrySetPseudo(string serverDir, string pseudo, out string error)
        {
            error = null;
            if (!IsValidPseudo(pseudo))
            {
                error = T("Pseudo invalide (3 à 16 caractères : lettres, chiffres, _ . -).",
                          "Invalid name (3 to 16 characters: letters, digits, _ . -).");
                return false;
            }
            try
            {
                var path = ConfigIni(serverDir);
                if (!File.Exists(path)) { error = T("config.ini introuvable.", "config.ini not found."); return false; }

                var text = ReadAll(path);
                text = SetIniValue(text, "bUseConfigDisplayName", "true");
                text = SetIniValue(text, "displayName", pseudo);
                WriteAll(path, text);
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        public static bool IsValidPseudo(string s)
        {
            if (string.IsNullOrEmpty(s)) return false;
            s = s.Trim();
            if (s.Length < 3 || s.Length > 16) return false;
            foreach (var c in s)
            {
                if (char.IsLetterOrDigit(c) || c == '_' || c == '.' || c == '-') continue;
                return false;
            }
            return true;
        }

        static string SetIniValue(string text, string key, string value)
        {
            var newline = text.Contains("\r\n") ? "\r\n" : "\n";
            var parts = text.Replace("\r\n", "\n").Split('\n');
            bool done = false;
            for (int i = 0; i < parts.Length; i++)
            {
                var t = parts[i].Trim();
                if (!t.StartsWith(key + "=", StringComparison.OrdinalIgnoreCase)) continue;
                parts[i] = key + "=" + value;
                done = true;
                break;
            }
            var result = string.Join(newline, parts);
            if (!done) result = key + "=" + value + newline + result;
            return result;
        }

        static string T(string fr, string en)
        {
            return LauncherPrefs.Language_ == "en" ? en : fr;
        }
    }
}
