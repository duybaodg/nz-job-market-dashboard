using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Configuration;

namespace Infrastructure.Crawling;

/// <summary>
/// Reads <c>crawler/sources/sites.json</c> — the shared crawler registry — so the
/// synchroniser and the crawl scheduler agree on the same source definitions.
/// </summary>
internal static class SourceRegistryReader
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    internal sealed record RegistrySite(
        [property: JsonPropertyName("name")] string? Name,
        [property: JsonPropertyName("homepage")] string? Homepage,
        [property: JsonPropertyName("jobs_url")] string? JobsUrl,
        [property: JsonPropertyName("parser")] string? Parser,
        [property: JsonPropertyName("strategy")] string? Strategy,
        [property: JsonPropertyName("platform")] string? Platform,
        [property: JsonPropertyName("robotsAllows")] bool? RobotsAllows,
        [property: JsonPropertyName("enabled")] bool? Enabled,
        [property: JsonPropertyName("frequency_minutes")] int? FrequencyMinutes,
        [property: JsonPropertyName("priority")] string? Priority);

    public static string? ResolvePath(IConfiguration configuration, string? overridePath = null)
    {
        if (!string.IsNullOrWhiteSpace(overridePath) && File.Exists(overridePath))
        {
            return overridePath;
        }

        var configured = configuration["CRAWLER_SOURCE_REGISTRY"];
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured))
        {
            return configured;
        }

        var currentDirectory = Directory.GetCurrentDirectory();
        var candidates = new[]
        {
            Path.Combine(currentDirectory, "crawler", "sources", "sites.json"),
            Path.Combine(currentDirectory, "..", "..", "..", "crawler", "sources", "sites.json"),
            Path.Combine(AppContext.BaseDirectory, "crawler", "sources", "sites.json")
        };

        return candidates.Select(Path.GetFullPath).FirstOrDefault(File.Exists);
    }

    public static IReadOnlyList<RegistrySite> Load(string path)
    {
        using var stream = File.OpenRead(path);
        using var document = JsonDocument.Parse(stream);
        if (!document.RootElement.TryGetProperty("sites", out var sitesElement))
        {
            return [];
        }

        return JsonSerializer.Deserialize<List<RegistrySite>>(sitesElement.GetRawText(), JsonOptions) ?? [];
    }
}
