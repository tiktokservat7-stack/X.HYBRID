using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace FNHybrid
{
    // Une release GitHub qui contient un .exe plus recent que la version installee.
    public sealed class UpdateInfo
    {
        public string Tag { get; set; }
        public string Version { get; set; }
        public string Notes { get; set; }
        public string AssetName { get; set; }
        public string AssetUrl { get; set; }
        public long AssetSize { get; set; }
        public string PageUrl { get; set; }
    }

    // Verification et installation des mises a jour depuis les releases GitHub.
    //
    // Depôt utilise : la constante DefaultRepo, ou le premier ligne du fichier
    // "updates.txt" place a cote de l'exe (owner/repo ou URL GitHub), ou la
    // variable d'environnement XHYBRID_UPDATE_REPO. Cela permet de changer de
    // depot sans recompiler.
    //
    // La release doit contenir un fichier .exe (de preference "X.HYBRID V1.exe").
    // Le tag doit etre superieur a AppInfo.VersionTag (ex: v1.1.0).
    public static class UpdateService
    {
        public const string DefaultRepo = "tiktokservat7-stack/X.HYBRID";
        const string UserAgent = "X.HYBRID-Updater";
        const long MinExeSize = 64 * 1024;

        public static string AppDir
        {
            get { return AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\'); }
        }

        public static string Repo
        {
            get
            {
                try
                {
                    var env = NormalizeRepo(Environment.GetEnvironmentVariable("XHYBRID_UPDATE_REPO"));
                    if (env != null) return env;

                    var file = Path.Combine(AppDir, "updates.txt");
                    if (File.Exists(file))
                    {
                        foreach (var line in File.ReadAllLines(file))
                        {
                            var t = line.Trim();
                            if (t.Length == 0 || t.StartsWith("#", StringComparison.Ordinal)) continue;
                            var r = NormalizeRepo(t);
                            if (r != null) return r;
                        }
                    }
                }
                catch (Exception) { }

                return DefaultRepo;
            }
        }

        // "https://github.com/owner/repo.git" -> "owner/repo"
        public static string NormalizeRepo(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return null;
            var s = raw.Trim();
            int i = s.IndexOf("github.com/", StringComparison.OrdinalIgnoreCase);
            if (i >= 0) s = s.Substring(i + "github.com/".Length);
            if (s.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
                s = s.Substring(0, s.Length - 4);
            s = s.Trim('/');
            var parts = s.Split('/');
            if (parts.Length != 2) return null;
            if (parts[0].Length == 0 || parts[1].Length == 0) return null;
            if (parts[0].IndexOf(' ') >= 0 || parts[1].IndexOf(' ') >= 0) return null;
            return parts[0] + "/" + parts[1];
        }

        public static int[] ParseVersion(string value)
        {
            var v = new int[4];
            if (string.IsNullOrEmpty(value)) return v;
            var s = value.Trim();
            if (s.StartsWith("v", StringComparison.OrdinalIgnoreCase)) s = s.Substring(1);
            var sb = new StringBuilder();
            foreach (var c in s)
            {
                if (char.IsDigit(c) || c == '.') sb.Append(c);
                else break;
            }
            var parts = sb.ToString().Split(new char[] { '.' }, StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < 4 && i < parts.Length; i++)
                int.TryParse(parts[i], out v[i]);
            return v;
        }

        // > 0 si a est plus recent que b
        public static int CompareVersions(string a, string b)
        {
            var pa = ParseVersion(a);
            var pb = ParseVersion(b);
            for (int i = 0; i < 4; i++)
            {
                int c = pa[i].CompareTo(pb[i]);
                if (c != 0) return c;
            }
            return 0;
        }

        static HttpClient NewClient(int seconds)
        {
            var http = new HttpClient();
            http.Timeout = TimeSpan.FromSeconds(seconds);
            http.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
            http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
            return http;
        }

        static IEnumerable<object> AsList(object value)
        {
            if (value == null || value is string) yield break;
            var seq = value as System.Collections.IEnumerable;
            if (seq == null) yield break;
            foreach (var item in seq) yield return item;
        }

        static long ToLong(object value)
        {
            long n;
            if (value == null) return 0;
            if (value is long) return (long)value;
            if (value is int) return (int)value;
            long.TryParse(Convert.ToString(value), out n);
            return n;
        }

        // Analyse une reponse JSON de l'API GitHub "releases/latest".
        // Retourne null si la version n'est pas plus recente ou sans exe.
        public static UpdateInfo ParseRelease(string json)
        {
            if (string.IsNullOrEmpty(json)) return null;
            var rel = JsonH.Parse<Dictionary<string, object>>(json);
            if (rel == null) return null;

            string tag = rel.ContainsKey("tag_name") ? Convert.ToString(rel["tag_name"]) : "";
            if (CompareVersions(tag, AppInfo.VersionTag) <= 0) return null;

            string notes = rel.ContainsKey("body") ? Convert.ToString(rel["body"]) : "";
            string page = rel.ContainsKey("html_url") ? Convert.ToString(rel["html_url"]) : "";
            string assetUrl = null, assetName = null;
            long size = 0;

            object assetsObj;
            if (rel.TryGetValue("assets", out assetsObj))
            {
                foreach (var item in AsList(assetsObj))
                {
                    var a = item as Dictionary<string, object>;
                    if (a == null) continue;
                    string an = a.ContainsKey("name") ? Convert.ToString(a["name"]) : "";
                    string au = a.ContainsKey("browser_download_url") ? Convert.ToString(a["browser_download_url"]) : "";
                    if (string.IsNullOrEmpty(au)) continue;
                    if (!an.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) continue;

                    bool preferred = an.IndexOf("x.hybrid", StringComparison.OrdinalIgnoreCase) >= 0;
                    if (assetUrl == null || preferred)
                    {
                        assetUrl = au;
                        assetName = an;
                        size = a.ContainsKey("size") ? ToLong(a["size"]) : 0;
                    }
                }
            }
            if (assetUrl == null) return null;

            var info = new UpdateInfo();
            info.Tag = tag;
            info.Version = tag.TrimStart(new char[] { 'v', 'V' });
            info.Notes = notes;
            info.AssetName = assetName;
            info.AssetUrl = assetUrl;
            info.AssetSize = size;
            info.PageUrl = page;
            return info;
        }

        // null en cas de panne reseau, de depot absent ou de version a jour.
        public static UpdateInfo Check()
        {
            try
            {
                ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
                string repo = Repo;
                if (string.IsNullOrEmpty(repo)) return null;

                using (var http = NewClient(10))
                {
                    var url = "https://api.github.com/repos/" + repo + "/releases/latest";
                    var resp = http.GetAsync(url).GetAwaiter().GetResult();
                    if (!resp.IsSuccessStatusCode)
                    {
                        FileLog.Write("MAJ: " + repo + " -> HTTP " + (int)resp.StatusCode);
                        return null;
                    }
                    var json = resp.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                    return ParseRelease(json);
                }
            }
            catch (Exception ex)
            {
                FileLog.Write("MAJ: " + ex.Message);
                return null;
            }
        }

        public static void Download(UpdateInfo info, string dest, IProgress<int> progress, CancellationToken token)
        {
            if (info == null) throw new ArgumentNullException("info");
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;

            using (var http = NewClient(600))
            using (var resp = http.GetAsync(info.AssetUrl, HttpCompletionOption.ResponseHeadersRead, token)
                                   .GetAwaiter().GetResult())
            {
                resp.EnsureSuccessStatusCode();
                long total = resp.Content.Headers.ContentLength ?? info.AssetSize;
                using (var src = resp.Content.ReadAsStreamAsync().GetAwaiter().GetResult())
                using (var dst = new FileStream(dest, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    var buf = new byte[81920];
                    long done = 0;
                    int last = -1;
                    int n;
                    while ((n = src.Read(buf, 0, buf.Length)) > 0)
                    {
                        token.ThrowIfCancellationRequested();
                        dst.Write(buf, 0, n);
                        done += n;
                        if (progress != null && total > 0)
                        {
                            int pct = (int)Math.Min(100, done * 100 / total);
                            if (pct != last) { last = pct; progress.Report(pct); }
                        }
                    }
                }
            }

            var fi = new FileInfo(dest);
            if (!fi.Exists || fi.Length < MinExeSize) throw new Exception("Fichier téléchargé invalide (taille).");
            using (var fs = File.OpenRead(dest))
            {
                int a = fs.ReadByte(), b = fs.ReadByte();
                if (a != 'M' || b != 'Z') throw new Exception("Le fichier téléchargé n'est pas un exécutable Windows.");
            }
        }

        // Genere le script qui attend la fermeture de l'app, remplace l'exe par
        // la nouvelle version, relance l'app puis supprime l'ancienne version.
        public static string BuildScript(string exeName)
        {
            var sb = new StringBuilder();
            sb.AppendLine("@echo off");
            sb.AppendLine("setlocal EnableExtensions EnableDelayedExpansion");
            sb.AppendLine("set \"APP=%~dp0" + exeName + "\"");
            sb.AppendLine("set \"NEW=%~dp0" + exeName + ".new\"");
            sb.AppendLine("set \"OLD=%~dp0" + exeName + ".old\"");
            sb.AppendLine("set \"SYS=%SystemRoot%\\System32\"");
            sb.AppendLine();
            sb.AppendLine("rem attendre la fermeture de l'ancienne version (60 s max)");
            sb.AppendLine("set /a n=0");
            sb.AppendLine(":wait");
            sb.AppendLine("\"%SYS%\\tasklist.exe\" /FI \"IMAGENAME eq " + exeName + "\" 2>nul | \"%SYS%\\find.exe\" /I \"" + exeName + "\" >nul");
            sb.AppendLine("if errorlevel 1 goto swap");
            sb.AppendLine("set /a n+=1");
            sb.AppendLine("if !n! GEQ 60 goto abort");
            sb.AppendLine("\"%SYS%\\ping.exe\" -n 2 -w 1000 127.0.0.1 >nul");
            sb.AppendLine("goto wait");
            sb.AppendLine();
            sb.AppendLine(":swap");
            sb.AppendLine("if not exist \"%NEW%\" goto abort");
            sb.AppendLine("if exist \"%OLD%\" del /f /q \"%OLD%\" >nul 2>&1");
            sb.AppendLine("move /y \"%APP%\" \"%OLD%\" >nul 2>&1");
            sb.AppendLine("if errorlevel 1 goto abort");
            sb.AppendLine("move /y \"%NEW%\" \"%APP%\" >nul 2>&1");
            sb.AppendLine("if errorlevel 1 goto restore");
            sb.AppendLine();
            sb.AppendLine("rem demarrer la nouvelle version");
            sb.AppendLine("start \"\" \"%APP%\"");
            sb.AppendLine("\"%SYS%\\ping.exe\" -n 5 -w 1000 127.0.0.1 >nul");
            sb.AppendLine("\"%SYS%\\tasklist.exe\" /FI \"IMAGENAME eq " + exeName + "\" 2>nul | \"%SYS%\\find.exe\" /I \"" + exeName + "\" >nul");
            sb.AppendLine("if errorlevel 1 goto restore");
            sb.AppendLine();
            sb.AppendLine("rem desinstaller l'ancienne version");
            sb.AppendLine("set /a n=0");
            sb.AppendLine(":delold");
            sb.AppendLine("if not exist \"%OLD%\" goto done");
            sb.AppendLine("set /a n+=1");
            sb.AppendLine("if !n! GEQ 15 goto done");
            sb.AppendLine("del /f /q \"%OLD%\" >nul 2>&1");
            sb.AppendLine("\"%SYS%\\ping.exe\" -n 2 -w 1000 127.0.0.1 >nul");
            sb.AppendLine("goto delold");
            sb.AppendLine();
            sb.AppendLine(":restore");
            sb.AppendLine("if exist \"%APP%\" del /f /q \"%APP%\" >nul 2>&1");
            sb.AppendLine("if exist \"%OLD%\" move /y \"%OLD%\" \"%APP%\" >nul 2>&1");
            sb.AppendLine("start \"\" \"%APP%\"");
            sb.AppendLine("goto done");
            sb.AppendLine();
            sb.AppendLine(":abort");
            sb.AppendLine("del /f /q \"%NEW%\" >nul 2>&1");
            sb.AppendLine("if not exist \"%APP%\" if exist \"%OLD%\" move /y \"%OLD%\" \"%APP%\" >nul 2>&1");
            sb.AppendLine();
            sb.AppendLine(":done");
            sb.AppendLine("del /f /q \"%~f0\" >nul 2>&1");
            sb.AppendLine("exit /b");
            return sb.ToString();
        }

        // Copie l'exe telecharge a cote de l'app et lance le script de remplacement.
        // L'appelant doit fermer l'application juste apres.
        public static void LaunchUpdater(UpdateInfo info, string downloaded)
        {
            string app = Process.GetCurrentProcess().MainModule.FileName;
            string dir = Path.GetDirectoryName(app);
            string exeName = Path.GetFileName(app);
            string newPath = app + ".new";
            string script = Path.Combine(dir, "X.HYBRID-update.cmd");

            File.Copy(downloaded, newPath, true);
            File.WriteAllText(script, BuildScript(exeName), Encoding.Default);

            var psi = new ProcessStartInfo();
            psi.FileName = "cmd.exe";
            psi.Arguments = "/c \"\"" + script + "\"\"";
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            psi.WindowStyle = ProcessWindowStyle.Hidden;
            Process.Start(psi);
        }

        // Nettoie les restes d'une mise a jour precedente (l'ancienne version
        // devrait deja etre supprimee par le script).
        public static void CleanupLeftovers()
        {
            try
            {
                string app = Process.GetCurrentProcess().MainModule.FileName;
                foreach (var path in new string[] { app + ".old", app + ".new" })
                {
                    try { if (File.Exists(path)) File.Delete(path); }
                    catch (Exception) { }
                }
            }
            catch (Exception) { }
        }
    }
}
