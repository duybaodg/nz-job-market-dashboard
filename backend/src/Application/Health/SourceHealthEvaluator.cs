namespace Application.Health;

/// <summary>The crawler-health states surfaced by GET /api/admin/health.</summary>
public static class SourceHealthState
{
    public const string Healthy = "healthy";
    public const string Empty = "empty";
    public const string Degraded = "degraded";
    public const string Stale = "stale";
    public const string Disabled = "disabled";
    public const string NeverRun = "never-run";
    /// <summary>Removed from the registry; kept only for historical provenance.</summary>
    public const string Retired = "retired";
}

/// <summary>A terminal crawl run reduced to the fields the state machine needs.</summary>
public sealed record CrawlRunSnapshot(
    string Status,
    int TotalJobsFound,
    DateTime StartedAt,
    string? ErrorMessage);

/// <summary>
/// Everything the state machine needs about one source. Kept free of EF/IO so
/// the boundaries can be pinned by fast unit tests.
/// </summary>
public sealed record SourceHealthInput(
    bool Enabled,
    DateTime? LastCrawledAt,
    int CrawlFrequencyMinutes,
    int JobsYielded,
    IReadOnlyList<CrawlRunSnapshot> RecentRuns);

public sealed record SourceHealthEvaluation(
    string State,
    int ConsecutiveFailures,
    int ConsecutiveZeroYield,
    string? LastStatus,
    string? LastError,
    string? DegradeReason);

/// <summary>
/// Pure state machine for crawler health. Computed from existing crawl_runs and
/// job_sources rows only — no schema changes, no crawls, no writes.
/// </summary>
public static class SourceHealthEvaluator
{
    public const int DegradedFailureThreshold = 3;
    public const int DegradedZeroYieldThreshold = 3;
    public const double StaleFrequencyMultiplier = 3d;
    public const int MaxErrorLength = 200;

    public static SourceHealthEvaluation Evaluate(SourceHealthInput input, DateTime now)
    {
        var recent = input.RecentRuns;
        var consecutiveFailures = ConsecutiveFailures(recent);
        var consecutiveZeroYield = ConsecutiveZeroYield(recent);
        var lastRun = recent.Count > 0 ? recent[0] : null;
        var lastStatus = lastRun?.Status;
        var lastError = Truncate(recent.FirstOrDefault(run => !string.IsNullOrWhiteSpace(run.ErrorMessage))?.ErrorMessage);
        var lastCrawledAt = input.LastCrawledAt ?? lastRun?.StartedAt;
        var frequencyMinutes = Math.Max(input.CrawlFrequencyMinutes, 1);
        var staleBefore = now.AddMinutes(-StaleFrequencyMultiplier * frequencyMinutes);
        var hasEverYielded = input.JobsYielded > 0 || recent.Any(run => run.TotalJobsFound > 0);

        // A disabled source is disabled whatever its history says; that is the
        // deliberate state in the registry, not a failure to investigate.
        if (!input.Enabled)
        {
            return new SourceHealthEvaluation(
                SourceHealthState.Disabled,
                consecutiveFailures,
                consecutiveZeroYield,
                lastStatus,
                lastError,
                "Disabled in the source registry.");
        }

        // Enabled but no crawl evidence at all: nothing has ever run.
        if (lastCrawledAt is null && lastStatus is null)
        {
            return new SourceHealthEvaluation(
                SourceHealthState.NeverRun,
                consecutiveFailures,
                consecutiveZeroYield,
                null,
                null,
                "Enabled but has never been crawled.");
        }

        if (consecutiveFailures >= DegradedFailureThreshold)
        {
            return new SourceHealthEvaluation(
                SourceHealthState.Degraded,
                consecutiveFailures,
                consecutiveZeroYield,
                lastStatus,
                lastError,
                $"Last {consecutiveFailures} crawls failed.{DescribeError(lastError)}");
        }

        // A source that used to yield roles and now silently returns 0 for three
        // completed crawls in a row is the slow-degradation signal this page exists
        // to surface. A source that never yielded is healthy-but-empty instead.
        if (lastStatus == "Completed"
            && consecutiveZeroYield >= DegradedZeroYieldThreshold
            && hasEverYielded)
        {
            return new SourceHealthEvaluation(
                SourceHealthState.Degraded,
                consecutiveFailures,
                consecutiveZeroYield,
                lastStatus,
                lastError,
                $"Last {consecutiveZeroYield} completed crawls returned 0 roles after previously yielding.");
        }

        if (lastCrawledAt < staleBefore)
        {
            return new SourceHealthEvaluation(
                SourceHealthState.Stale,
                consecutiveFailures,
                consecutiveZeroYield,
                lastStatus,
                lastError,
                $"Last crawled {DescribeAge(now - lastCrawledAt!.Value)} ago; expected every {DescribeFrequency(frequencyMinutes)}.");
        }

        if (lastStatus == "Completed" && input.JobsYielded == 0)
        {
            return new SourceHealthEvaluation(
                SourceHealthState.Empty,
                consecutiveFailures,
                consecutiveZeroYield,
                lastStatus,
                lastError,
                "Board is reachable but currently holds 0 IT roles.");
        }

        return new SourceHealthEvaluation(
            SourceHealthState.Healthy,
            consecutiveFailures,
            consecutiveZeroYield,
            lastStatus,
            lastError,
            null);
    }

    /// <summary>
    /// Counts the leading run of Failed terminal runs. A currently-Running run is
    /// skipped (it must not reset or extend the failure streak) and the first
    /// Completed run ends the streak.
    /// </summary>
    public static int ConsecutiveFailures(IReadOnlyList<CrawlRunSnapshot> runsNewestFirst)
    {
        var count = 0;

        foreach (var run in runsNewestFirst)
        {
            if (run.Status == "Running")
            {
                continue;
            }

            if (run.Status == "Failed")
            {
                count++;
                continue;
            }

            break;
        }

        return count;
    }

    /// <summary>
    /// Counts the leading run of Completed runs that saved/found zero roles. A
    /// Failed run or a Completed run with results ends the streak; a Running run
    /// is skipped.
    /// </summary>
    public static int ConsecutiveZeroYield(IReadOnlyList<CrawlRunSnapshot> runsNewestFirst)
    {
        var count = 0;

        foreach (var run in runsNewestFirst)
        {
            if (run.Status == "Running")
            {
                continue;
            }

            if (run.Status != "Completed" || run.TotalJobsFound > 0)
            {
                break;
            }

            count++;
        }

        return count;
    }

    private static string? Truncate(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();
        return trimmed.Length <= MaxErrorLength ? trimmed : $"{trimmed[..(MaxErrorLength - 1)]}…";
    }

    private static string DescribeError(string? lastError) =>
        lastError is null ? string.Empty : $" Last error: {lastError}";

    private static string DescribeAge(TimeSpan age)
    {
        if (age.TotalHours < 1)
        {
            return $"{Math.Max(1, (int)age.TotalMinutes)}m";
        }

        if (age.TotalDays < 1)
        {
            return $"{(int)age.TotalHours}h";
        }

        return $"{(int)age.TotalDays}d";
    }

    private static string DescribeFrequency(int frequencyMinutes)
    {
        if (frequencyMinutes % 1440 == 0)
        {
            return $"{frequencyMinutes / 1440}d";
        }

        if (frequencyMinutes % 60 == 0)
        {
            return $"{frequencyMinutes / 60}h";
        }

        return $"{frequencyMinutes}m";
    }
}
