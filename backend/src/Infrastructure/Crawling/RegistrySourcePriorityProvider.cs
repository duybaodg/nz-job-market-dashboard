using Application.Crawling;
using Microsoft.Extensions.Configuration;

namespace Infrastructure.Crawling;

/// <summary>
/// Ranks sources for the crawl scheduler using the registry's <c>priority</c>
/// field. SEEK and Trade Me are marked <c>"last"</c>: they stay enabled but are
/// crawled after every other due source, exactly as the project brief requires.
/// </summary>
public sealed class RegistrySourcePriorityProvider : ISourcePriorityProvider
{
    public const int NormalRank = 0;
    public const int LastRank = 1;

    private readonly Dictionary<string, int> ranks;

    public RegistrySourcePriorityProvider(IConfiguration configuration)
        : this(SourceRegistryReader.ResolvePath(configuration))
    {
    }

    internal RegistrySourcePriorityProvider(string? registryPath)
    {
        ranks = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        if (registryPath is null)
        {
            return;
        }

        foreach (var site in SourceRegistryReader.Load(registryPath))
        {
            if (!string.IsNullOrWhiteSpace(site.Name))
            {
                ranks[site.Name] = string.Equals(site.Priority, "last", StringComparison.OrdinalIgnoreCase)
                    ? LastRank
                    : NormalRank;
            }
        }
    }

    public int RankFor(string sourceName) =>
        ranks.TryGetValue(sourceName, out var rank) ? rank : NormalRank;
}
