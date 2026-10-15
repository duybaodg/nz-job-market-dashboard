using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace Infrastructure.Firecrawl;

/// <summary>
/// Enforces robots.txt for the .NET Firecrawl path. The self-hosted Firecrawl
/// <c>/scrape</c> endpoint does not consult robots.txt (it is a single-URL
/// fetch, unlike <c>/crawl</c>), so the adapter must gate every URL before it
/// hands it to the instance. Rules are cached per origin for the process
/// lifetime; an unreachable or missing robots.txt means "no restriction", which
/// matches the Python worker's behaviour.
/// </summary>
public interface IRobotsTxtPolicy
{
    Task<bool> CanFetchAsync(string url, CancellationToken cancellationToken);
}

public sealed class RobotsTxtPolicy(
    IHttpClientFactory httpClientFactory,
    ILogger<RobotsTxtPolicy> logger) : IRobotsTxtPolicy
{
    /// <summary>The same bot identity the Python worker sends, so both crawl paths read the same group.</summary>
    public const string UserAgent = "Mozilla/5.0 (compatible; NZJobMarketDashboardBot/0.2; +https://example.invalid/bot)";

    private static readonly ConcurrentDictionary<string, RobotsTxtMatcher.RobotsRules> Cache =
        new(StringComparer.OrdinalIgnoreCase);

    public async Task<bool> CanFetchAsync(string url, CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return false;
        }

        var origin = $"{uri.Scheme}://{uri.Authority}";
        var rules = await GetRulesAsync(origin, cancellationToken);
        return RobotsTxtMatcher.CanFetch(rules, UserAgent, url);
    }

    private async Task<RobotsTxtMatcher.RobotsRules> GetRulesAsync(string origin, CancellationToken cancellationToken)
    {
        if (Cache.TryGetValue(origin, out var cached))
        {
            return cached;
        }

        try
        {
            var client = httpClientFactory.CreateClient("robots");
            using var request = new HttpRequestMessage(HttpMethod.Get, $"{origin}/robots.txt");
            request.Headers.UserAgent.ParseAdd(UserAgent);
            using var response = await client.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                Cache[origin] = RobotsTxtMatcher.RobotsRules.Empty;
                return RobotsTxtMatcher.RobotsRules.Empty;
            }

            var text = await response.Content.ReadAsStringAsync(cancellationToken);
            var rules = RobotsTxtMatcher.Parse(text);
            Cache[origin] = rules;
            return rules;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "Could not read robots.txt for {Origin}; treating it as unrestricted.", origin);
            Cache[origin] = RobotsTxtMatcher.RobotsRules.Empty;
            return RobotsTxtMatcher.RobotsRules.Empty;
        }
    }
}
