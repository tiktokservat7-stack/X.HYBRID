namespace FNHybrid
{
    // Nom et version du launcher. La version est comparee aux tags GitHub
    // (v1.0.0, v1.1.0, ...) par UpdateService : incrementer Version et
    // VersionTag ensemble avant de publier une release.
    public static class AppInfo
    {
        public const string Name = "X.HYBRID V1";
        public const string ExeName = "X.HYBRID V1.exe";
        public const string Version = "1.3";
        public const string VersionTag = "v1.3.0";

        public static string Badge { get { return "V" + Version; } }
    }
}
