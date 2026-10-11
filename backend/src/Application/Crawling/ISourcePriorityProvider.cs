namespace Application.Crawling;

/// <summary>
/// Orders due sources for crawling. Sources marked <c>"last"</c> in the registry
/// (SEEK, Trade Me) are deliberately crawled after every other source.
/// </summary>
public interface ISourcePriorityProvider
{
    /// <summary>Lower ranks are crawled first; 0 is normal, 1 is "last".</summary>
    int RankFor(string sourceName);
}
