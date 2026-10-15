namespace Domain.Entities;

/// <summary>
/// An append-only record of an employer identity decision (canonical created,
/// alias added/removed, or a match pass run). Decisions are data and revisable.
/// </summary>
public sealed class EmployerAuditEvent
{
    public Guid Id { get; set; }
    public Guid? EmployerId { get; set; }
    public string Action { get; set; } = string.Empty;
    public string? Detail { get; set; }
    public string Actor { get; set; } = "system";
    public DateTime CreatedAt { get; set; }
}
