namespace Domain.Entities;

/// <summary>
/// Whether the canonical employer is the organisation hiring for the role
/// (a direct employer, including government bodies) or a recruitment agency /
/// staffing firm that advertises on behalf of an unnamed client.
/// </summary>
public enum EmployerType
{
    DirectEmployer,
    Agency
}

/// <summary>
/// An admin-reviewed canonical employer. Canonicals are data, not code: they
/// are created and merged only through the documented alias workflow, never by
/// a fuzzy automatic match. <see cref="Job.EmployerCanonicalId"/> points here
/// while the raw source company text on the job stays untouched.
/// </summary>
public sealed class Employer
{
    public Guid Id { get; set; }
    public string CanonicalName { get; set; } = string.Empty;
    public string NormalizedName { get; set; } = string.Empty;
    public EmployerType Type { get; set; } = EmployerType.DirectEmployer;
    public DateTime CreatedAt { get; set; }
    public string? Notes { get; set; }
    public ICollection<EmployerAlias> Aliases { get; set; } = new List<EmployerAlias>();
}
