using Application.Health;

namespace Api.Tests;

internal sealed class StubHealthService : IHealthService
{
    public static readonly DateTime GeneratedAt = new(2026, 10, 16, 0, 0, 0, DateTimeKind.Utc);

    public Task<HealthReportDto> GetHealthAsync(CancellationToken cancellationToken)
        => Task.FromResult(new HealthReportDto(
            GeneratedAt,
            new HealthSummaryDto(1, 0, 0, 0, 0, 0, 0, 1),
            new[]
            {
                new SourceHealthDto(
                    "Acme", true, "generic", "http", null, "Completed", null,
                    0, 0, 3, "healthy", null, 60),
            },
            Array.Empty<SourceHealthDto>()));
}
