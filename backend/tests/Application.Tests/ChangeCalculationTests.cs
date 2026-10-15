using Application.Metrics;
using FluentAssertions;

namespace Application.Tests;

public sealed class ChangeCalculationTests
{
    [Fact]
    public void Computes_absolute_and_percentage_change_when_previous_is_positive()
    {
        var change = ChangeCalculation.Compute(current: 120, previous: 100);

        change.AbsoluteChange.Should().Be(20);
        change.PercentChange.Should().Be(20m);
        change.ComparisonAvailable.Should().BeTrue();
        change.NoPriorBaseline.Should().BeFalse();
        change.Note.Should().BeNull();
    }

    [Fact]
    public void Rounds_percentage_to_one_decimal()
    {
        var change = ChangeCalculation.Compute(current: 7, previous: 3);

        change.PercentChange.Should().Be(133.3m);
    }

    [Fact]
    public void Zero_baseline_shows_absolute_change_and_no_percentage()
    {
        var change = ChangeCalculation.Compute(current: 12, previous: 0);

        change.AbsoluteChange.Should().Be(12);
        change.PercentChange.Should().BeNull();
        change.ComparisonAvailable.Should().BeTrue();
        change.NoPriorBaseline.Should().BeTrue();
        change.Note.Should().Be("No prior baseline.");
    }

    [Fact]
    public void Absent_history_is_not_a_fabricated_trend()
    {
        var change = ChangeCalculation.Compute(current: 12, previous: null);

        change.Previous.Should().BeNull();
        change.AbsoluteChange.Should().BeNull();
        change.PercentChange.Should().BeNull();
        change.ComparisonAvailable.Should().BeFalse();
        change.Note.Should().Be("Comparison unavailable.");
    }

    [Fact]
    public void Negative_change_is_reported_with_a_negative_sign()
    {
        var change = ChangeCalculation.Compute(current: 40, previous: 50);

        change.AbsoluteChange.Should().Be(-10);
        change.PercentChange.Should().Be(-20m);
    }
}
