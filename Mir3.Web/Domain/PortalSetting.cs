namespace Mir3.Web.Domain;

public sealed class PortalSetting
{
    public const int SingletonId = 1;
    public const string SingletonKey = "portal";

    public int Id { get; set; } = SingletonId;

    public string Key { get; set; } = SingletonKey;

    public bool AutoActivateAfterEmailVerification { get; set; }

    public DateTime UpdatedUtc { get; set; }
}
