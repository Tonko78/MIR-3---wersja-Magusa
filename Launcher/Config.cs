using Library;

namespace Launcher
{
    [ConfigPath(@".\Launcher.ini")]
    public static class Config
    {
        [ConfigSection("Patcher")]
        public static string Host { get; set; } = string.Empty;
    }
}
