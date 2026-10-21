using System.Collections.Concurrent;
using System.Net;
using Microsoft.Extensions.Logging;

namespace Infrastructure.Firecrawl;

/// <summary>
/// Enforces robots.txt for the .NET Firecrawl path. The self-hosted Firecrawl
/// <c>/scrape</c> endpoint does not consult robots.txt (it is a single-URL
/// fetch, unlike <c>/crawl</c>), so the adapter must gate every URL before it
/// hands it to the instance.
///
/// Round 15: only a genuine 404/410 ("no robots file") means unrestricted.
/// A timeout, TLS error, 401, 429 or 5xx is *unknown* permission, so it is
/// cached as an explicit deny for five minutes and the URL is refused. This
/// mirrors the Python worker's fail-closed behaviour.
/// </summary>
public interface IRobotsTxtPolicy
{
    Task<bool> CanFetchAsync(string url, CancellationToken cancellationToken);
}

public sealed class RobotsTxtPolicy(
    IHttpClientFactory httpClientFactory,
    ILogger<RobotsTxtPolicy> logger,
    TimeProvider? timeProvider = null) : IRobotsTxtPolicy
{
    /// <summary>The same bot identity the Python worker sends, so both crawl paths read the same group.</summary>
    public const string UserAgent = "Mozilla/5.0 (compatible; NZJobMarketDashboardBot/0.2; +https://example.invalid/bot)";

    public static readonly TimeSpan UnavailableTtl = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan RulesTtl = TimeSpan.FromHours(1);

    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;
    private readonly ConcurrentDictionary<string, CacheEntry> cache = new(StringComparer.OrdinalIgnoreCase);

    public async Task<bool> CanFetchAsync(string url, CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return false;
        }

        var origin = $"{uri.Scheme}://{uri.Authority}";
        var rules = await GetRulesAsync(origin, cancellationToken);
        return rules is not null && RobotsTxtMatcher.CanFetch(rules, UserAgent, url);
    }

    private async Task<RobotsTxtMatcher.RobotsRules?> GetRulesAsync(
        string origin,
        CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        if (cache.TryGetValue(origin, out var cached) && cached.ExpiresAt > now)
        {
            return cached.Rules;
        }

        try
        {
            var client = httpClientFactory.CreateClient("robots");
            using var request = new HttpRequestMessage(HttpMethod.Get, $"{origin}/robots.txt");
            request.Headers.UserAgent.ParseAdd(UserAgent);
            using var response = await client.SendAsync(request, cancellationToken);
            if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone)
            {
                // A site with no robots file is unrestricted by definition.
                var empty = RobotsTxtMatcher.RobotsRules.Empty;
                cache[origin] = new CacheEntry(empty, now + RulesTtl);
                return empty;
            }

            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning(
                    "Could not read robots.txt for {Origin} (HTTP {Status}); failing closed for {Ttl}.",
                    origin,
                    (int)response.StatusCode,
                    UnavailableTtl);
                cache[origin] = new CacheEntry(null, now + UnavailableTtl);
                return null;
            }

            var text = await response.Content.ReadAsStringAsync(cancellationToken);
            var rules = RobotsTxtMatcher.Parse(text);
            cache[origin] = new CacheEntry(rules, now + RulesTtl);
            return rules;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            // Timeout, TLS failure, DNS error: unknown permission -> deny.
            logger.LogWarning(
                exception,
                "Could not read robots.txt for {Origin}; failing closed for {Ttl}.",
                origin,
                UnavailableTtl);
            cache[origin] = new CacheEntry(null, now + UnavailableTtl);
            return null;
        }
    }

    private sealed record CacheEntry(RobotsTxtMatcher.RobotsRules? Rules, DateTimeOffset ExpiresAt);
}
