using System.Text.Json;
using System.Text.RegularExpressions;
using Application.Crawling;
using FluentAssertions;

namespace Application.Tests;

/// <summary>
/// Cross-language contract: the .NET adapter may only pass parser/fetcher
/// values that the Python CLI accepts, for every enabled source in
/// <c>crawler/sources/sites.json</c>. Reads both the Python choices and the
/// registry from the repository so the two definitions cannot drift again.
/// </summary>
public sealed class ScraplingCliContractTests
{
    [Fact]
    public void Dotnet_parser_choices_are_accepted_by_the_python_cli()
    {
        var pythonParsers = PythonChoices("PARSER_CHOICES");

        ScraplingCliContract.ParserChoices.Should().BeSubsetOf(pythonParsers);
    }

    [Fact]
    public void Dotnet_fetcher_choices_are_accepted_by_the_python_cli()
    {
        var pythonFetchers = PythonChoices("FETCHER_CHOICES");

        ScraplingCliContract.FetcherChoices.Should().BeSubsetOf(pythonFetchers);
    }

    [Fact]
    public void Every_enabled_registry_source_maps_to_a_valid_argv()
    {
        var pythonParsers = PythonChoices("PARSER_CHOICES");
        var pythonFetchers = PythonChoices("FETCHER_CHOICES");
        var sites = LoadRegistry().Where(site => site.Enabled).ToList();

        sites.Should().NotBeEmpty();

        foreach (var site in sites)
        {
            var parser = ScraplingCliContract.NormaliseParser(site.Parser);
            var fetcher = ScraplingCliContract.NormaliseFetcher(site.Fetcher);

            pythonParsers.Should().Contain(parser, $"'{site.Name}' parser '{site.Parser}' must map to a CLI choice");
            pythonFetchers.Should().Contain(fetcher, $"'{site.Name}' fetcher '{site.Fetcher}' must map to a CLI choice");
        }
    }

    [Theory]
    [InlineData("ashby", "ashby")]
    [InlineData("trade-me", "trade-me")]
    [InlineData("jsonld", "jsonld")]
    [InlineData("custom", "generic")]
    [InlineData("successfactors", "rss")]
    [InlineData("json-ld", "jsonld")]
    [InlineData("something-unknown", "generic")]
    [InlineData(null, "generic")]
    public void NormaliseParser_maps_registry_values_to_cli_choices(string? input, string expected)
    {
        ScraplingCliContract.NormaliseParser(input).Should().Be(expected);
    }

    [Theory]
    [InlineData("http", "http")]
    [InlineData("dynamic", "dynamic")]
    [InlineData("browser", "browser")]
    [InlineData("firecrawl", "firecrawl")]
    [InlineData("firecrawl-selfhosted", "firecrawl")]
    [InlineData("playwright", "browser")]
    [InlineData("static", "http")]
    [InlineData("something-unknown", "fetcher")]
    [InlineData(null, "fetcher")]
    public void NormaliseFetcher_maps_registry_values_to_cli_choices(string? input, string expected)
    {
        ScraplingCliContract.NormaliseFetcher(input).Should().Be(expected);
    }

    private static IReadOnlySet<string> PythonChoices(string name)
    {
        var source = File.ReadAllText(Path.Combine(RepoRoot(), "crawler", "adapters", "scrapling", "scrapling_worker.py"));
        var block = Regex.Match(source, $@"{name}\s*=\s*\((?<body>[^)]*)\)", RegexOptions.Singleline);
        block.Success.Should().BeTrue($"{name} should be defined in scrapling_worker.py");

        return Regex.Matches(block.Groups["body"].Value, "\"(?<value>[^\"]+)\"")
            .Select(match => match.Groups["value"].Value)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private static IReadOnlyList<RegistrySite> LoadRegistry()
    {
        var path = Path.Combine(RepoRoot(), "crawler", "sources", "sites.json");
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        return document.RootElement.GetProperty("sites").EnumerateArray()
            .Select(site => new RegistrySite(
                site.GetProperty("name").GetString() ?? string.Empty,
                site.TryGetProperty("parser", out var parser) ? parser.GetString() : null,
                site.TryGetProperty("fetcher", out var fetcher) ? fetcher.GetString() : null,
                !site.TryGetProperty("enabled", out var enabled) || enabled.GetBoolean()))
            .ToList();
    }

    private static string RepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null
               && !File.Exists(Path.Combine(directory.FullName, "crawler", "sources", "sites.json")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
            ?? throw new InvalidOperationException("Could not locate the repository root from the test output directory.");
    }

    private sealed record RegistrySite(string Name, string? Parser, string? Fetcher, bool Enabled);
}
