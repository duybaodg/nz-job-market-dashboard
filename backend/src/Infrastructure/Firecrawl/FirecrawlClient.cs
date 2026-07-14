using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace Infrastructure.Firecrawl;

public interface IFirecrawlClient
{
    Task<FirecrawlScrapeResult> ScrapeAsync(string url, CancellationToken cancellationToken);
}

public sealed record FirecrawlScrapeResult(
    string Url,
    string? Markdown,
    string? Html,
    string? RawHtml,
    string RawJson);

public sealed class FirecrawlClient(HttpClient httpClient, IOptions<FirecrawlOptions> options)
    : IFirecrawlClient
{
    private readonly FirecrawlOptions options = options.Value;

    public async Task<FirecrawlScrapeResult> ScrapeAsync(string url, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(options.ApiKey))
        {
            throw new InvalidOperationException("Firecrawl is disabled. Set FIRECRAWL_API_KEY to enable crawling.");
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, "scrape");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", options.ApiKey);
        request.Content = JsonContent.Create(new
        {
            url,
            formats = new[] { "markdown", "html", "rawHtml" },
            onlyMainContent = true,
            blockAds = true,
            timeout = options.TimeoutSeconds * 1000
        });

        using var response = await httpClient.SendAsync(request, cancellationToken);
        var rawJson = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Firecrawl scrape failed with HTTP {(int)response.StatusCode}: {rawJson}");
        }

        using var document = JsonDocument.Parse(rawJson);
        var root = document.RootElement;

        if (root.TryGetProperty("success", out var success) && success.ValueKind == JsonValueKind.False)
        {
            throw new InvalidOperationException($"Firecrawl scrape failed: {rawJson}");
        }

        var data = root.GetProperty("data");

        return new FirecrawlScrapeResult(
            url,
            ReadString(data, "markdown"),
            ReadString(data, "html"),
            ReadString(data, "rawHtml"),
            rawJson);
    }

    private static string? ReadString(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }
}
