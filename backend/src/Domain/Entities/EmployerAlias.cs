namespace Domain.Entities;

/// <summary>
/// A reviewed alternative spelling that resolves to a canonical employer. The
/// normalised alias is matched exactly (case/whitespace-insensitive) so similar
/// names are never merged automatically.
/// </summary>
public sealed class EmployerAlias
{
    public Guid Id { get; set; }
    public Guid EmployerId { get; set; }
    public Employer? Employer { get; set; }
    public string Alias { get; set; } = string.Empty;
    public string NormalizedAlias { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
    public string CreatedBy { get; set; } = "system";
}
