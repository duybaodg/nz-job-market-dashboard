namespace Application.Metrics;

/// <summary>
/// A reporting interval with an explicit inclusive start and exclusive end, in
/// UTC. The local start/end dates record the Pacific/Auckland calendar days the
/// interval was derived from so labels and tests can assert the boundary.
/// </summary>
public sealed record ReportingInterval(
    DateTime StartUtc,
    DateTime EndUtc,
    DateOnly StartLocalDate,
    DateOnly EndLocalDateExclusive,
    string Label)
{
    public TimeSpan Duration => EndUtc - StartUtc;

    public bool Contains(DateTime valueUtc) => valueUtc >= StartUtc && valueUtc < EndUtc;
}

/// <summary>
/// Pacific/Auckland reporting boundaries. All dashboard periods use NZ local
/// calendar days so a daylight-saving transition can never silently shorten or
/// lengthen a window: the interval is defined by local midnights and converted
/// to UTC individually.
/// </summary>
public static class NzReportingCalendar
{
    /// <summary>NZ Standard Time as a Windows fallback for hosts with no tzdata.</summary>
    public const string TimeZoneId = "Pacific/Auckland";

    public static readonly TimeZoneInfo TimeZone = ResolveTimeZone();

    /// <summary>
    /// The last <paramref name="days"/> <b>complete</b> local days, ending at the
    /// most recent local midnight. Excluding the in-progress day keeps every
    /// current/previous comparison equal-duration and free of a partial period.
    /// </summary>
    public static ReportingInterval LastCompleteDays(int days, DateTime nowUtc)
    {
        if (days < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(days), days, "Window length must be at least one day.");
        }

        var today = LocalDate(nowUtc);
        var startLocal = today.AddDays(-days);
        var endLocal = today;
        return Create(startLocal, endLocal, $"Last {days} completed days to {endLocal:yyyy-MM-dd} (NZ)");
    }

    /// <summary>The current in-progress local day, from midnight to <paramref name="nowUtc"/>.</summary>
    public static ReportingInterval CurrentDay(DateTime nowUtc)
    {
        var today = LocalDate(nowUtc);
        return Create(today, today.AddDays(1), $"Today to date ({today:yyyy-MM-dd} NZ)") with { EndUtc = nowUtc };
    }

    /// <summary>The immediately preceding interval of exactly the same duration.</summary>
    public static ReportingInterval PreviousPeriod(ReportingInterval interval)
    {
        var startUtc = interval.StartUtc - interval.Duration;
        var endUtc = interval.StartUtc;
        var startLocal = LocalDate(startUtc);
        var endLocal = LocalDate(endUtc);
        return new ReportingInterval(
            startUtc,
            endUtc,
            startLocal,
            endLocal,
            $"Previous {interval.Duration.TotalDays:0.#} days ({startLocal:yyyy-MM-dd} to {endLocal:yyyy-MM-dd} NZ)");
    }

    public static DateOnly LocalDate(DateTime utc)
    {
        var local = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), TimeZone);
        return DateOnly.FromDateTime(local);
    }

    public static DateTime StartOfLocalDayUtc(DateOnly localDate) => ToUtc(localDate.ToDateTime(TimeOnly.MinValue));

    private static ReportingInterval Create(DateOnly startLocal, DateOnly endLocal, string label) =>
        new(StartOfLocalDayUtc(startLocal), StartOfLocalDayUtc(endLocal), startLocal, endLocal, label);

    private static DateTime ToUtc(DateTime localWallTime)
    {
        // A DST spring-forward can make local midnight invalid on the rare
        // transition that occurs at midnight; nudge forward until it exists.
        while (TimeZone.IsInvalidTime(localWallTime))
        {
            localWallTime = localWallTime.AddMinutes(30);
        }

        return TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(localWallTime, DateTimeKind.Unspecified), TimeZone);
    }

    private static TimeZoneInfo ResolveTimeZone()
    {
        foreach (var id in new[] { TimeZoneId, "New Zealand Standard Time" })
        {
            try
            {
                return TimeZoneInfo.FindSystemTimeZoneById(id);
            }
            catch (TimeZoneNotFoundException)
            {
                // Try the next id.
            }
            catch (InvalidTimeZoneException)
            {
                // Try the next id.
            }
        }

        // Never crash the dashboard over a missing tz database; UTC is a clearly
        // documented fallback and the interval labels still carry the zone name.
        return TimeZoneInfo.Utc;
    }
}
