using Application.Metrics;

namespace Application.Analytics;

/// <summary>Single mapping from a reporting interval to its wire DTO.</summary>
public static class MetricIntervalMapper
{
    public static MetricIntervalDto ToDto(ReportingInterval interval, bool isPartial) =>
        new(
            interval.StartUtc,
            interval.EndUtc,
            interval.StartLocalDate.ToString("yyyy-MM-dd"),
            interval.EndLocalDateExclusive.ToString("yyyy-MM-dd"),
            (int)Math.Round(interval.Duration.TotalDays),
            interval.Label,
            isPartial);
}
