using Application.Metrics;
using FluentAssertions;

namespace Application.Tests;

public sealed class ListingMetricsTests
{
    private static readonly DateTime Now = new(2026, 10, 15, 0, 30, 0, DateTimeKind.Utc);

    [Fact]
    public void Posted_and_discovered_bases_are_counted_separately()
    {
        var listings = new[]
        {
            // Posted long ago but first seen inside the window.
            new ListingDateObservation(new DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc), Now.AddDays(-1)),
            // Posted inside the window and first seen inside the window.
            new ListingDateObservation(Now.AddDays(-2), Now.AddDays(-2)),
            // No posted date, discovered inside the window.
            new ListingDateObservation(null, Now.AddDays(-3))
        };
        var interval = NzReportingCalendar.LastCompleteDays(7, Now);

        var posted = ListingMetrics.Count(listings, interval, DateBasis.Posted);
        var discovered = ListingMetrics.Count(listings, interval, DateBasis.Discovered);

        posted.Count.Should().Be(1);
        discovered.Count.Should().Be(3);
        posted.WithPostedDate.Should().Be(2);
        posted.MissingPostedDate.Should().Be(1);
        posted.PostedDateCoveragePercent.Should().Be(66.7m);
    }

    [Fact]
    public void Posted_basis_never_falls_back_to_first_seen()
    {
        var listings = new[]
        {
            new ListingDateObservation(null, Now.AddDays(-1))
        };
        var interval = NzReportingCalendar.LastCompleteDays(7, Now);

        ListingMetrics.Count(listings, interval, DateBasis.Posted).Count.Should().Be(0);
        ListingMetrics.Count(listings, interval, DateBasis.Discovered).Count.Should().Be(1);
    }

    [Fact]
    public void Interval_boundaries_are_inclusive_start_exclusive_end()
    {
        var interval = NzReportingCalendar.LastCompleteDays(7, Now);
        var listings = new[]
        {
            new ListingDateObservation(interval.StartUtc, interval.StartUtc),
            new ListingDateObservation(interval.EndUtc, interval.EndUtc),
            new ListingDateObservation(interval.EndUtc.AddTicks(-1), interval.EndUtc.AddTicks(-1))
        };

        ListingMetrics.Count(listings, interval, DateBasis.Posted).Count.Should().Be(2);
        ListingMetrics.Count(listings, interval, DateBasis.Discovered).Count.Should().Be(2);
    }
}
