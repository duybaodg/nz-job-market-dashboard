namespace Application.Employers;

public sealed record EmployerReviewQueueItemDto(
    string NormalizedName,
    string AdvertisedName,
    int JobCount,
    string? Decision,
    Guid? CanonicalEmployerId,
    string? CanonicalName,
    string? Reason,
    string? Reviewer,
    DateTime? ReviewedAt);

public sealed record EmployerReviewGroupDto(
    string GroupKey,
    int TotalListings,
    IReadOnlyList<EmployerReviewQueueItemDto> Names);

public sealed record EmployerReviewDecisionDto(
    string NormalizedName,
    string AdvertisedName,
    string Decision,
    Guid? CanonicalEmployerId,
    string? CanonicalName,
    string GroupKey,
    string? Reason,
    string Reviewer,
    DateTime ReviewedAt);

public sealed record EmployerReviewQueueDto(
    DateTime GeneratedAtUtc,
    int UnmatchedNameCount,
    int TotalDecisionCount,
    int DecidedNameCount,
    int PendingNameCount,
    int TotalListingsUnmatched,
    string GroupingRule,
    IReadOnlyList<EmployerReviewGroupDto> Groups,
    IReadOnlyList<EmployerReviewDecisionDto> RecentDecisions);

public sealed record EmployerReviewDecisionInput(
    string AdvertisedName,
    string Decision,
    Guid? CanonicalEmployerId,
    string? Reason);

public sealed record RecordEmployerReviewDecisionsRequest(
    IReadOnlyList<EmployerReviewDecisionInput> Decisions,
    string? Reviewer);

public sealed record EmployerReviewResultDto(
    int Saved,
    int Created,
    int Updated,
    int AliasesCreated,
    int ListingsRematched,
    EmployerReviewQueueDto Queue);
