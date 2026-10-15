namespace Application.Metrics;

/// <summary>
/// Change against a comparable prior period. A missing baseline and a zero
/// baseline are different facts and must not produce a fabricated percentage.
/// </summary>
public sealed record ChangeResult(
    int Current,
    int? Previous,
    int? AbsoluteChange,
    decimal? PercentChange,
    bool ComparisonAvailable,
    bool NoPriorBaseline,
    string? Note)
{
    public static ChangeResult NoHistory(int current) =>
        new(current, null, null, null, false, false, "Comparison unavailable.");

    public static ChangeResult ZeroBaseline(int current, int previous) =>
        new(current, previous, current - previous, null, true, true, "No prior baseline.");
}

public static class ChangeCalculation
{
    /// <summary>
    /// Absolute change = current − previous. Percentage change is only shown when
    /// a positive previous value exists; a zero previous value yields the
    /// absolute change plus "No prior baseline", and absent history yields
    /// "Comparison unavailable".
    /// </summary>
    public static ChangeResult Compute(int current, int? previous)
    {
        if (previous is null)
        {
            return ChangeResult.NoHistory(current);
        }

        var absolute = current - previous.Value;
        if (previous.Value == 0)
        {
            return ChangeResult.ZeroBaseline(current, previous.Value);
        }

        var percent = Math.Round((decimal)absolute / previous.Value * 100m, 1, MidpointRounding.AwayFromZero);
        return new ChangeResult(current, previous, absolute, percent, true, false, null);
    }
}
