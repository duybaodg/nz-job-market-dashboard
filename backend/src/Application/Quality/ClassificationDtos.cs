namespace Application.Quality;

public sealed record ClassificationSampleItemDto(
    Guid JobId,
    string Source,
    string Title,
    string? AdvertisedCompany,
    string? EvidenceExcerpt,
    string PredictedRoleFamily,
    string PredictedSeniority,
    IReadOnlyList<string> TitleLevels,
    bool SpansSeniority,
    string RuleVersion,
    string SeniorityRuleVersion,
    string? ExistingLabel);

public sealed record ClassificationSampleDto(
    DateTime GeneratedAtUtc,
    int RequestedSize,
    int SampleSize,
    int ActiveListings,
    int SourceCount,
    int RoleFamilyCount,
    string Method,
    IReadOnlyList<ClassificationSampleItemDto> Items);

public sealed record ClassificationLabelInput(
    Guid JobId,
    string Label,
    string? ReviewedRoleFamily,
    string? ReviewedSeniority,
    string? SeniorityFlag,
    string? SeniorityNote,
    string? Notes);

public sealed record RecordClassificationLabelsRequest(
    IReadOnlyList<ClassificationLabelInput> Labels,
    string? Reviewer);

public sealed record ClassificationReviewDto(
    Guid JobId,
    string Source,
    string Title,
    string PredictedRoleFamily,
    string ReviewedRoleFamily,
    string Label,
    string PredictedSeniority,
    string ReviewedSeniority,
    string SeniorityFlag,
    string? SeniorityNote,
    string? Notes,
    string Reviewer,
    string RuleVersion,
    DateTime ReviewedAt);

public sealed record ClassificationReviewsReportDto(
    DateTime GeneratedAtUtc,
    int ActiveListings,
    int ReviewedListings,
    IReadOnlyDictionary<string, int> LabelCounts,
    int SenioritySpanCount,
    IReadOnlyList<ClassificationReviewDto> Reviews);

public sealed record ClassificationLabelsResultDto(
    int Saved,
    int Created,
    int Updated,
    ClassificationReviewsReportDto Report);
