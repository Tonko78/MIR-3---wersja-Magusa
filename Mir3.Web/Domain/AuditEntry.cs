namespace Mir3.Web.Domain;

public sealed class AuditEntry
{
    public long Id { get; set; }

    public string Actor { get; set; } = string.Empty;

    public string Action { get; set; } = string.Empty;

    public string Target { get; set; } = string.Empty;

    public string? DetailsJson { get; set; }

    public DateTime CreatedUtc { get; set; }
}
