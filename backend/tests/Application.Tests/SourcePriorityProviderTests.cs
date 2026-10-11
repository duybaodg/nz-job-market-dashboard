using Application.Crawling;
using Domain.Entities;
using FluentAssertions;
using Infrastructure.Crawling;
using Microsoft.Extensions.Configuration;

namespace Application.Tests;

public sealed class SourcePriorityProviderTests
{
    [Fact]
    public void RankFor_crawls_last_priority_sources_after_everything_else()
    {
        var path = Path.Combine(Path.GetTempPath(), $"registry-{Guid.NewGuid():N}.json");
        File.WriteAllText(
            path,
            """
            {
              "sites": [
                { "name": "SEEK NZ", "priority": "last" },
                { "name": "Trade Me Jobs", "priority": "last" },
                { "name": "Xero" },
                { "name": "ANZ NZ", "priority": "normal" }
              ]
            }
            """);
        try
        {
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> { ["CRAWLER_SOURCE_REGISTRY"] = path })
                .Build();
            var provider = new RegistrySourcePriorityProvider(configuration);

            provider.RankFor("SEEK NZ").Should().Be(RegistrySourcePriorityProvider.LastRank);
            provider.RankFor("Trade Me Jobs").Should().Be(RegistrySourcePriorityProvider.LastRank);
            provider.RankFor("Xero").Should().Be(RegistrySourcePriorityProvider.NormalRank);
            provider.RankFor("ANZ NZ").Should().Be(RegistrySourcePriorityProvider.NormalRank);
            provider.RankFor("Unknown Source").Should().Be(RegistrySourcePriorityProvider.NormalRank);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void OrderForCrawl_places_last_priority_sources_at_the_end()
    {
        var sources = new List<JobSource>
        {
            new() { Name = "SEEK NZ" },
            new() { Name = "Absolute IT" },
            new() { Name = "Trade Me Jobs" },
            new() { Name = "Xero" },
        };
        var priority = new StubPriority(new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["SEEK NZ"] = RegistrySourcePriorityProvider.LastRank,
            ["Trade Me Jobs"] = RegistrySourcePriorityProvider.LastRank,
        });

        var ordered = SourceScheduler.OrderForCrawl(sources, priority).Select(source => source.Name).ToList();

        ordered.Should().Equal("Absolute IT", "Xero", "SEEK NZ", "Trade Me Jobs");
    }

    private sealed class StubPriority(Dictionary<string, int> ranks) : ISourcePriorityProvider
    {
        public int RankFor(string sourceName) => ranks.TryGetValue(sourceName, out var rank) ? rank : 0;
    }
}
