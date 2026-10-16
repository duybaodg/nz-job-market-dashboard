namespace Application.Health;

public sealed record SourceHealthDto(
    string Source,
    bool Enabled,
    string Parser,
    string Method,
    DateTime? LastCrawledAt,
    string? LastStatus,
    string? LastError,
    int ConsecutiveFailures,
    int ConsecutiveZeroYield,
    int JobsYielded,
    string State,
    string? DegradeReason,
    int CrawlFrequencyMinutes);

public sealed record HealthSummaryDto(
    int Healthy,
    int Degraded,
    int Stale,
    int Empty,
    int Disabled,
    int NeverRun,
    int Retired,
    int TotalSources);

public sealed record HealthReportDto(
    DateTime GeneratedAt,
    HealthSummaryDto Summary,
    IReadOnlyList<SourceHealthDto> Sources,
    IReadOnlyList<SourceHealthDto> RetiredSources);
