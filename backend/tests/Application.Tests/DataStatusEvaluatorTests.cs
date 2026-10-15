using Application.Metrics;
using FluentAssertions;

namespace Application.Tests;

public sealed class DataStatusEvaluatorTests
{
    private static readonly DateTime Now = new(2026, 10, 15, 12, 0, 0, DateTimeKind.Utc);
    private static readonly TimeSpan StaleAfter = TimeSpan.FromHours(48);

    [Fact]
    public void No_successful_crawl_is_unavailable_not_zero_demand()
    {
        var status = DataStatusEvaluator.Evaluate(
            Now,
            lastSuccessfulUpdateUtc: null,
            newestListingSeenUtc: null,
            [new SourceRunObservation("Acme", true, null, null)],
            StaleAfter);

        status.State.Should().Be(DataFreshnessState.Unavailable);
        status.Message.Should().Contain("not a zero-demand signal");
    }

    [Fact]
    public void A_recent_success_is_fresh_when_every_enabled_source_refreshed()
    {
        var status = DataStatusEvaluator.Evaluate(
            Now,
            lastSuccessfulUpdateUtc: Now.AddHours(-2),
            newestListingSeenUtc: Now.AddHours(-2),
            [
                new SourceRunObservation("Acme", true, Now.AddHours(-2), "Completed"),
                new SourceRunObservation("Beta", false, null, null)
            ],
            StaleAfter);

        status.State.Should().Be(DataFreshnessState.Fresh);
        status.EnabledSources.Should().Be(1);
        status.EnabledSourcesWithRecentSuccess.Should().Be(1);
    }

    [Fact]
    public void An_old_success_is_stale()
    {
        var status = DataStatusEvaluator.Evaluate(
            Now,
            lastSuccessfulUpdateUtc: Now.AddDays(-5),
            newestListingSeenUtc: Now.AddDays(-5),
            [new SourceRunObservation("Acme", true, Now.AddDays(-5), "Completed")],
            StaleAfter);

        status.State.Should().Be(DataFreshnessState.Stale);
        status.Message.Should().Contain("may be out of date");
    }

    [Fact]
    public void A_fresh_dataset_with_a_source_that_did_not_refresh_is_partial()
    {
        var status = DataStatusEvaluator.Evaluate(
            Now,
            lastSuccessfulUpdateUtc: Now.AddHours(-2),
            newestListingSeenUtc: Now.AddHours(-2),
            [
                new SourceRunObservation("Acme", true, Now.AddHours(-2), "Completed"),
                new SourceRunObservation("Beta", true, Now.AddDays(-4), "Failed")
            ],
            StaleAfter);

        status.State.Should().Be(DataFreshnessState.Partial);
        status.EnabledSourcesWithRecentSuccess.Should().Be(1);
        status.Message.Should().Contain("last successful dataset is shown");
    }
}
