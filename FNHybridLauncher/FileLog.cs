using System;
using System.IO;

namespace FNHybrid
{
    public static class FileLog
    {
        static readonly object _lock = new object();

        public static string LogPath
        {
            get { return Path.Combine(AppConfig.GetDir(), "launcher.log"); }
        }

        public static void Write(string s)
        {
            lock (_lock)
            {
                try
                {
                    Directory.CreateDirectory(AppConfig.GetDir());
                    File.AppendAllText(LogPath, "[" + DateTime.Now.ToString("HH:mm:ss") + "] " + s + Environment.NewLine);
                }
                catch (Exception) { }
            }
        }
    }
}
