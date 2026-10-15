using Application.Metrics;

namespace Application.Analytics;

/// <summary>Single mapping from a computed change to its wire DTO.</summary>
public static class ChangeMapper
{
    public static ChangeDto ToDto(ChangeResult change) =>
        new(
            change.Current,
            change.Previous,
            change.AbsoluteChange,
            change.PercentChange,
            change.ComparisonAvailable,
            change.NoPriorBaseline,
            change.Note);
}
