using Library;

namespace PatchManager
{
    [ConfigPath(@".\PatchManager.ini")]
    public static class Config
    {
        [ConfigSection("Patcher")]
        public static string CleanClient { get; set; } = @".\Clean Client\";

        // Local staging directory that ops/mir3-web/publish-patch.ps1 uploads
        // to the public HTTPS patch origin. No credentials are stored here;
        // server transfer happens over the operator's SSH session.
        public static string PublishDirectory { get; set; } = @".\Publish\";
    }
}
