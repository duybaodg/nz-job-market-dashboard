namespace Application.Employers;

/// <summary>An admin-reviewed canonical employer and its live job count.</summary>
public sealed record EmployerDto(
    Guid Id,
    string CanonicalName,
    string Type,
    IReadOnlyList<string> Aliases,
    int JobCount,
    DateTime CreatedAt,
    string? Notes);

/// <summary>
/// A resolved hiring-employer string that has no canonical yet. It is reported,
/// never auto-created, and stays in the unknown bucket until reviewed.
/// </summary>
public sealed record UnmatchedEmployerNameDto(string Name, int JobCount);

public sealed record EmployersReportDto(
    DateTime GeneratedAtUtc,
    int CanonicalCount,
    int AgencyCount,
    int DirectEmployerCount,
    int ListingsWithCanonical,
    int ListingsUnmatched,
    IReadOnlyList<EmployerDto> Employers,
    IReadOnlyList<UnmatchedEmployerNameDto> UnmatchedNames);

public sealed record CreateEmployerRequest(string CanonicalName, string? Type, string? Notes, string? Actor);

public sealed record AddEmployerAliasRequest(string Alias, string? Actor);

public sealed record EmployerSeedResult(bool Seeded, int CanonicalsCreated, int AliasesCreated);

public sealed record EmployerMatchResult(int Evaluated, int Matched, int Unmatched, int Changed);
