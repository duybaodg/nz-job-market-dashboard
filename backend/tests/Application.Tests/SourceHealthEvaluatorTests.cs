using Application.Health;
using FluentAssertions;

namespace Application.Tests;

public sealed class SourceHealthEvaluatorTests
{
    private static readonly DateTime Now = new(2026, 10, 13, 12, 0, 0, DateTimeKind.Utc);

    // ---- state boundaries -------------------------------------------------

    [Fact]
    public void Disabled_wins_over_a_healthy_history()
    {
        var input = Input(enabled: false, lastCrawledAt: Now.AddHours(-1), jobs: 12, runs: [Run("Completed", found: 4)]);

        SourceHealthEvaluator.Evaluate(input, Now).State.Should().Be(SourceHealthState.Disabled);
    }

    [Fact]
    public void Enabled_source_with_no_crawl_evidence_is_never_run()
    {
        var input = Input(lastCrawledAt: null, jobs: 0);

        var result = SourceHealthEvaluator.Evaluate(input, Now);

        result.State.Should().Be(SourceHealthState.NeverRun);
        result.LastStatus.Should().BeNull();
    }

    [Fact]
    public void Recent_completed_crawl_with_jobs_is_healthy()
    {
        var input = Input(lastCrawledAt: Now.AddHours(-2), jobs: 7, runs: [Run("Completed", found: 7)]);

        SourceHealthEvaluator.Evaluate(input, Now).State.Should().Be(SourceHealthState.Healthy);
    }

    [Fact]
    public void Completed_crawl_with_zero_roles_is_empty()
    {
        var input = Input(lastCrawledAt: Now.AddHours(-2), jobs: 0, runs: [Run("Completed", found: 0)]);

        var result = SourceHealthEvaluator.Evaluate(input, Now);

        result.State.Should().Be(SourceHealthState.Empty);
        result.DegradeReason.Should().Contain("0 IT roles");
    }

    [Fact]
    public void Two_consecutive_failures_are_below_the_degraded_threshold()
    {
        var input = Input(lastCrawledAt: Now.AddHours(-2), jobs: 5, runs: [Run("Failed"), Run("Failed")]);

        var result = SourceHealthEvaluator.Evaluate(input, Now);

        result.ConsecutiveFailures.Should().Be(2);
        result.State.Should().NotBe(SourceHealthState.Degraded);
    }

    [Fact]
    public void Three_consecutive_failures_are_degraded()
    {
        var input = Input(lastCrawledAt: Now.AddHours(-2), jobs: 5, runs: [Run("Failed", error: "HTTP 403"), Run("Failed"), Run("Failed")]);

        var result = SourceHealthEvaluator.Evaluate(input, Now);

        result.State.Should().Be(SourceHealthState.Degraded);
        result.ConsecutiveFailures.Should().Be(3);
        result.LastError.Should().Be("HTTP 403");
        result.DegradeReason.Should().Contain("Last 3 crawls failed");
    }

    [Fact]
    public void Two_zero_yields_are_below_the_degraded_threshold_for_a_previously_yielding_source()
    {
        var input = Input(lastCrawledAt: Now.AddHours(-2), jobs: 0, runs: [Run("Completed", found: 0), Run("Completed", found: 0)]);

        var result = SourceHealthEvaluator.Evaluate(input, Now);

        result.ConsecutiveZeroYield.Should().Be(2);
        result.State.Should().NotBe(SourceHealthState.Degraded);
    }

    [Fact]
    public void Three_zero_yields_degrade_a_previously_yielding_source()
    {
        var input = Input(
            lastCrawledAt: Now.AddHours(-2),
            jobs: 0,
            runs:
            [
                Run("Completed", found: 0),
                Run("Completed", found: 0),
                Run("Completed", found: 0),
                Run("Completed", found: 9, minutesAgo: 90)
            ]);

        var result = SourceHealthEvaluator.Evaluate(input, Now);

        result.State.Should().Be(SourceHealthState.Degraded);
        result.ConsecutiveZeroYield.Should().Be(3);
        result.DegradeReason.Should().Contain("0 roles after previously yielding");
    }

    [Fact]
    public void Three_zero_yields_for_a_never_yielding_source_stay_empty()
    {
        var input = Input(lastCrawledAt: Now.AddHours(-2), jobs: 0, runs: [Run("Completed", found: 0), Run("Completed", found: 0), Run("Completed", found: 0)]);

        SourceHealthEvaluator.Evaluate(input, Now).State.Should().Be(SourceHealthState.Empty);
    }

    // ---- stale boundary ---------------------------------------------------

    [Fact]
    public void Exactly_three_frequencies_old_is_not_yet_stale()
    {
        var input = Input(lastCrawledAt: Now.AddDays(-3), frequencyMinutes: 1440, jobs: 5, runs: [Run("Completed", found: 5, minutesAgo: 4320)]);

        SourceHealthEvaluator.Evaluate(input, Now).State.Should().Be(SourceHealthState.Healthy);
    }

    [Fact]
    public void Just_over_three_frequencies_old_is_stale()
    {
        var input = Input(lastCrawledAt: Now.AddDays(-3).AddSeconds(-1), frequencyMinutes: 1440, jobs: 5, runs: [Run("Completed", found: 5, minutesAgo: 4321)]);

        var result = SourceHealthEvaluator.Evaluate(input, Now);

        result.State.Should().Be(SourceHealthState.Stale);
        result.DegradeReason.Should().Contain("expected every 1d");
    }

    [Fact]
    public void Stale_takes_precedence_over_empty()
    {
        var input = Input(lastCrawledAt: Now.AddDays(-5), frequencyMinutes: 1440, jobs: 0, runs: [Run("Completed", found: 0, minutesAgo: 7200)]);

        SourceHealthEvaluator.Evaluate(input, Now).State.Should().Be(SourceHealthState.Stale);
    }

    [Fact]
    public void Stale_threshold_scales_with_the_source_frequency()
    {
        // 6 hours is exactly 3x a 2-hour frequency: not stale.
        var atBoundary = Input(lastCrawledAt: Now.AddHours(-6), frequencyMinutes: 120, jobs: 1, runs: [Run("Completed", found: 1, minutesAgo: 361)]);
        SourceHealthEvaluator.Evaluate(atBoundary, Now).State.Should().Be(SourceHealthState.Healthy);

        // One second past 3x is stale.
        var pastBoundary = Input(lastCrawledAt: Now.AddHours(-6).AddSeconds(-1), frequencyMinutes: 120, jobs: 1, runs: [Run("Completed", found: 1, minutesAgo: 361)]);
        SourceHealthEvaluator.Evaluate(pastBoundary, Now).State.Should().Be(SourceHealthState.Stale);
    }

    // ---- streak mechanics -------------------------------------------------

    [Fact]
    public void A_running_run_is_skipped_when_counting_failures()
    {
        var runs = new[] { Run("Running"), Run("Failed"), Run("Failed"), Run("Failed") };

        SourceHealthEvaluator.ConsecutiveFailures(runs).Should().Be(3);
        SourceHealthEvaluator.Evaluate(Input(lastCrawledAt: Now.AddHours(-1), jobs: 0, runs: runs), Now).State
            .Should().Be(SourceHealthState.Degraded);
    }

    [Fact]
    public void A_completed_run_breaks_the_failure_streak()
    {
        var runs = new[] { Run("Failed"), Run("Completed", found: 5) };

        SourceHealthEvaluator.ConsecutiveFailures(runs).Should().Be(1);
    }

    [Fact]
    public void A_failed_run_breaks_the_zero_yield_streak()
    {
        var runs = new[] { Run("Failed"), Run("Completed", found: 0), Run("Completed", found: 0), Run("Completed", found: 0) };

        SourceHealthEvaluator.ConsecutiveZeroYield(runs).Should().Be(0);
    }

    [Fact]
    public void A_yielding_run_ends_the_zero_yield_count()
    {
        var runs = new[] { Run("Completed", found: 0), Run("Completed", found: 4), Run("Completed", found: 0) };

        SourceHealthEvaluator.ConsecutiveZeroYield(runs).Should().Be(1);
    }

    // ---- derived fields ---------------------------------------------------

    [Fact]
    public void Last_crawled_at_falls_back_to_the_most_recent_run()
    {
        var runs = new[] { Run("Completed", found: 3, minutesAgo: 30) };

        SourceHealthEvaluator.Evaluate(Input(lastCrawledAt: null, jobs: 3, runs: runs), Now).State.Should().Be(SourceHealthState.Healthy);
    }

    [Fact]
    public void Last_error_is_truncated_to_200_characters()
    {
        var longError = new string('x', 350);
        var runs = new[] { Run("Failed", error: longError), Run("Failed", error: "older"), Run("Failed", error: "oldest") };

        var result = SourceHealthEvaluator.Evaluate(Input(lastCrawledAt: Now.AddHours(-1), jobs: 0, runs: runs), Now);

        result.LastError.Should().HaveLength(SourceHealthEvaluator.MaxErrorLength);
        result.LastError.Should().EndWith("…");
    }

    [Fact]
    public void Last_error_prefers_the_most_recent_run_that_has_one()
    {
        var runs = new[] { Run("Failed", error: null), Run("Failed", error: "connection reset"), Run("Failed") };

        SourceHealthEvaluator.Evaluate(Input(lastCrawledAt: Now.AddHours(-1), jobs: 0, runs: runs), Now).LastError
            .Should().Be("connection reset");
    }

    // ---- helpers ----------------------------------------------------------

    private static CrawlRunSnapshot Run(string status, int found = 0, string? error = null, int minutesAgo = 0) =>
        new(status, found, Now.AddMinutes(-minutesAgo), error);

    private static SourceHealthInput Input(
        bool enabled = true,
        DateTime? lastCrawledAt = null,
        int frequencyMinutes = 1440,
        int jobs = 0,
        params CrawlRunSnapshot[] runs) =>
        new(enabled, lastCrawledAt, frequencyMinutes, jobs, runs);
}
