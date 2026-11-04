namespace Application.Deduplication;

/// <summary>
/// The published dedup state. <see cref="Active"/> is only true once at least
/// one full pass has completed and passed its integrity validation; until then
/// no unique-vacancy count is published.
///
/// The three counting terms are deliberately separate:
/// <see cref="Groups"/> is the number of vacancy groups,
/// <see cref="ListingsInGroups"/> is every active source row that participates
/// in one of those groups, and <see cref="ListingsMerged"/> is the duplicate
/// rows removed from the listing count (rows-in-groups − groups). They are
/// computed from the current active population so the identity
/// vacancies = activeListings − listingsMerged always holds.
///
/// <see cref="Status"/> is "validated", "not-run", "failed" or "invalid"; a
/// missing pass is never presented as a successful zero-merge run.
/// </summary>
public sealed record DeduplicationSummaryDto(
    bool Active,
    DateTime? LastValidatedAtUtc,
    string MatchVersion,
    int ActiveListings,
    int? UniqueVacancies,
    int Groups,
    int ListingsMerged,
    int ReviewCandidates,
    int GroupCap,
    string? LastRunStatus,
    int ListingsInGroups,
    string Status,
    string? UnavailableReason);

public sealed record DuplicateGroupMemberDto(
    Guid JobId,
    string Source,
    string Title,
    string? Company,
    string? HiringEmployer,
    string Url,
    bool IsActive,
    bool IsCanonical);

public sealed record DuplicateGroupDto(
    Guid Id,
    string MatchMethod,
    string MatchVersion,
    string Confidence,
    Guid CanonicalListingId,
    int MemberCount,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    IReadOnlyList<DuplicateGroupMemberDto> Members);

public sealed record DuplicateReviewCandidateDto(
    Guid JobId,
    Guid? CandidateJobId,
    string Reason,
    string? Title,
    string? Employer,
    DateTime CreatedAt);

public sealed record DuplicateGroupsReportDto(
    DateTime GeneratedAtUtc,
    int GroupCount,
    int ListingsMerged,
    int? UniqueVacancies,
    int ActiveListings,
    bool Deduplicated,
    IReadOnlyList<DuplicateGroupDto> Groups,
    IReadOnlyList<DuplicateReviewCandidateDto> ReviewCandidates,
    int Page,
    int PageSize);
