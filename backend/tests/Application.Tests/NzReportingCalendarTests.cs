using Application.Metrics;
using FluentAssertions;

namespace Application.Tests;

public sealed class NzReportingCalendarTests
{
    [Fact]
    public void LastCompleteDays_uses_local_midnight_boundaries_inclusive_start_exclusive_end()
    {
        // 2026-10-15T00:30Z is 2026-10-15 13:30 NZDT.
        var now = new DateTime(2026, 10, 15, 0, 30, 0, DateTimeKind.Utc);

        var interval = NzReportingCalendar.LastCompleteDays(7, now);

        interval.StartLocalDate.Should().Be(new DateOnly(2026, 10, 8));
        interval.EndLocalDateExclusive.Should().Be(new DateOnly(2026, 10, 15));
        interval.StartUtc.Should().Be(new DateTime(2026, 10, 7, 11, 0, 0, DateTimeKind.Utc));
        interval.EndUtc.Should().Be(new DateTime(2026, 10, 14, 11, 0, 0, DateTimeKind.Utc));
        interval.Duration.Should().Be(TimeSpan.FromDays(7));

        // Inclusive start, exclusive end.
        interval.Contains(interval.StartUtc).Should().BeTrue();
        interval.Contains(interval.EndUtc).Should().BeFalse();
        interval.Contains(interval.EndUtc.AddTicks(-1)).Should().BeTrue();
    }

    [Fact]
    public void LastCompleteDays_excludes_the_in_progress_local_day()
    {
        var now = new DateTime(2026, 10, 15, 0, 30, 0, DateTimeKind.Utc);

        var interval = NzReportingCalendar.LastCompleteDays(2, now);

        // Today (15 Oct NZ) is partial and must not be inside a complete-day window.
        interval.Contains(now).Should().BeFalse();
        interval.EndLocalDateExclusive.Should().Be(new DateOnly(2026, 10, 15));
    }

    [Fact]
    public void PreviousPeriod_is_the_immediately_preceding_equal_duration()
    {
        var now = new DateTime(2026, 10, 15, 0, 30, 0, DateTimeKind.Utc);
        var interval = NzReportingCalendar.LastCompleteDays(7, now);

        var previous = NzReportingCalendar.PreviousPeriod(interval);

        previous.EndUtc.Should().Be(interval.StartUtc);
        previous.Duration.Should().Be(interval.Duration);
        previous.StartUtc.Should().Be(interval.StartUtc.AddDays(-7));
    }

    [Fact]
    public void Dst_start_day_is_twenty_three_hours_long_across_the_spring_forward()
    {
        // NZDT begins 2026-09-27 02:00 NZST -> 03:00 NZDT. The local day
        // 2026-09-27 therefore runs 23 hours.
        var now = new DateTime(2026, 9, 28, 0, 0, 0, DateTimeKind.Utc);

        var interval = NzReportingCalendar.LastCompleteDays(1, now);

        interval.StartLocalDate.Should().Be(new DateOnly(2026, 9, 27));
        interval.EndLocalDateExclusive.Should().Be(new DateOnly(2026, 9, 28));
        interval.Duration.Should().Be(TimeSpan.FromHours(23));
    }

    [Fact]
    public void Dst_end_day_is_twenty_five_hours_long_across_the_fall_back()
    {
        // NZDT ends 2026-04-05 03:00 NZDT -> 02:00 NZST. The local day
        // 2026-04-05 therefore runs 25 hours.
        var now = new DateTime(2026, 4, 6, 0, 0, 0, DateTimeKind.Utc);

        var interval = NzReportingCalendar.LastCompleteDays(1, now);

        interval.StartLocalDate.Should().Be(new DateOnly(2026, 4, 5));
        interval.EndLocalDateExclusive.Should().Be(new DateOnly(2026, 4, 6));
        interval.Duration.Should().Be(TimeSpan.FromHours(25));
    }

    [Fact]
    public void Seven_day_window_spanning_a_dst_transition_is_not_a_fixed_number_of_utc_hours()
    {
        // Seven complete local days including the 23-hour 27 September must be
        // 6 * 24 + 23 = 167 hours, not 168.
        var now = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc);

        var interval = NzReportingCalendar.LastCompleteDays(7, now);

        interval.Duration.Should().Be(TimeSpan.FromHours(167));
    }
}
