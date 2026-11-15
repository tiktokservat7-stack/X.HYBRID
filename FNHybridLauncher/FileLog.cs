using System;
using System.IO;

namespace FNHybrid
{
    public static class FileLog
    {
        static readonly object _lock = new object();

        // Chaque ligne ecrite est aussi diffusee en direct : la console
        // flottante (ConsoleWindow) s'y abonne pour tout afficher en temps reel.
        public static event Action<string> OnLine;

        public static string LogPath
        {
            get { return Path.Combine(AppConfig.GetDir(), "launcher.log"); }
        }

        public static void Write(string s)
        {
            string stamp = DateTime.Now.ToString("HH:mm:ss");
            lock (_lock)
            {
                try
                {
                    Directory.CreateDirectory(AppConfig.GetDir());
                    File.AppendAllText(LogPath, "[" + stamp + "] " + s + Environment.NewLine);
                }
                catch (Exception) { }
            }

            var handler = OnLine;
            if (handler != null)
            {
                try { handler("[" + stamp + "] " + s); }
                catch (Exception) { }
            }
        }
    }
}
