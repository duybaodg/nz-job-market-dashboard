namespace Application.Crawling;

/// <summary>
/// The single, auditable robots.txt exemption in the .NET side of the crawler.
///
/// Operator decision, 2026-10-23 (Bao Duy): the LinkedIn guest-endpoint crawl
/// becomes a scheduled source and hard rule #1 is waived FOR LINKEDIN GUEST
/// CRAWLING ONLY. linkedin.com robots.txt is <c>User-agent: *</c> ->
/// <c>Disallow: /</c>; this is a knowing, operator-authorised exception
/// (personal use, no login, honest UA, low volume). It applies to NOTHING else.
///
/// This is an ALLOWLIST OF ONE mirroring <c>ROBOTS_EXEMPT_SOURCES</c> in
/// <c>crawler/adapters/scrapling/scrapling_worker.py</c>. Never add a second
/// name, and never turn it into a configuration flag. The synchroniser uses it
/// only to grant scheduling approval (<c>terms_checked_at</c>); the registry
/// keeps the truthful <c>robotsAllows: false</c>.
/// </summary>
public static class RobotsExemptionPolicy
{
    public static readonly IReadOnlySet<string> ExemptSources =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "LinkedIn Jobs" };

    public static bool IsExempt(string? sourceName) =>
        !string.IsNullOrWhiteSpace(sourceName) && ExemptSources.Contains(sourceName.Trim());
}
