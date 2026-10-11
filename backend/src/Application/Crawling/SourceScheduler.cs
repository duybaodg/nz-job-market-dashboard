using Domain.Entities;

namespace Application.Crawling;

/// <summary>
/// Decides the order in which due sources are crawled. Kept separate from the
/// worker so the "SEEK and Trade Me last" rule is unit-testable.
/// </summary>
public static class SourceScheduler
{
    public static IReadOnlyList<JobSource> OrderForCrawl(
        IEnumerable<JobSource> sources,
        ISourcePriorityProvider priority)
    {
        return sources
            .OrderBy(source => priority.RankFor(source.Name))
            .ThenBy(source => source.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}
