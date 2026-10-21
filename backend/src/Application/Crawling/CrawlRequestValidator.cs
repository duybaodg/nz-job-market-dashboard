using System.Net;
using System.Net.Sockets;
using Application.Common;
using Microsoft.EntityFrameworkCore;

namespace Application.Crawling;

/// <summary>Result of validating an admin crawl trigger before any fetch happens.</summary>
public sealed record CrawlRequestValidation(bool IsAllowed, string? Reason)
{
    public static CrawlRequestValidation Allow() => new(true, null);

    public static CrawlRequestValidation Reject(string reason) => new(false, reason);
}

/// <summary>
/// Resolves a hostname to the addresses the process would connect to. Abstracted
/// so the SSRF guard is deterministic in tests (the production implementation
/// uses the system resolver).
/// </summary>
public interface ICrawlHostResolver
{
    Task<IPAddress[]> ResolveAsync(string host, CancellationToken cancellationToken);
}

public sealed class DnsCrawlHostResolver : ICrawlHostResolver
{
    public async Task<IPAddress[]> ResolveAsync(string host, CancellationToken cancellationToken)
    {
        if (IPAddress.TryParse(host, out var literal))
        {
            return [literal];
        }

        return await Dns.GetHostAddressesAsync(host, cancellationToken);
    }
}

/// <summary>
/// Round 15 SSRF guard for <c>POST /api/admin/crawl-runs</c>. The trigger may
/// only name a registered source and a URL on that source's registered origin;
/// the resolved address must be a public unicast address. This is applied before
/// the adapter is invoked and is intentionally independent of the crawler's
/// per-page robots checks.
/// </summary>
public interface ICrawlRequestValidator
{
    Task<CrawlRequestValidation> ValidateAsync(StartCrawlRunRequest request, CancellationToken cancellationToken);
}

public sealed class CrawlRequestValidator(
    IApplicationDbContext dbContext,
    ICrawlHostResolver hostResolver) : ICrawlRequestValidator
{
    public async Task<CrawlRequestValidation> ValidateAsync(
        StartCrawlRunRequest request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Source))
        {
            return CrawlRequestValidation.Reject("A registered source name is required.");
        }

        if (!Uri.TryCreate(request.Url, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return CrawlRequestValidation.Reject("A valid http(s) URL is required.");
        }

        if (!string.IsNullOrEmpty(uri.UserInfo))
        {
            return CrawlRequestValidation.Reject("Credentials in the crawl URL are not allowed.");
        }

        var registered = await dbContext.JobSources
            .AsNoTracking()
            .Where(source => source.RetiredAt == null && source.BaseUrl != null)
            .Select(source => new { source.Name, source.BaseUrl })
            .ToListAsync(cancellationToken);

        var source = registered.FirstOrDefault(item =>
            string.Equals(item.Name, request.Source.Trim(), StringComparison.OrdinalIgnoreCase));
        if (source is null)
        {
            return CrawlRequestValidation.Reject("The source is not registered.");
        }

        if (!Uri.TryCreate(source.BaseUrl, UriKind.Absolute, out var registeredUri))
        {
            return CrawlRequestValidation.Reject("The registered source has no valid base URL.");
        }

        if (!SameOrigin(uri, registeredUri))
        {
            return CrawlRequestValidation.Reject(
                "The crawl URL origin does not match the registered source origin.");
        }

        IPAddress[] addresses;
        try
        {
            addresses = await hostResolver.ResolveAsync(uri.Host, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return CrawlRequestValidation.Reject("The crawl host could not be resolved safely.");
        }

        if (addresses.Length == 0)
        {
            return CrawlRequestValidation.Reject("The crawl host did not resolve to an address.");
        }

        foreach (var address in addresses)
        {
            if (!IsPublicUnicast(address))
            {
                return CrawlRequestValidation.Reject(
                    "The crawl URL resolves to a loopback, private, link-local or multicast address.");
            }
        }

        return CrawlRequestValidation.Allow();
    }

    internal static bool SameOrigin(Uri left, Uri right) =>
        string.Equals(left.Scheme, right.Scheme, StringComparison.OrdinalIgnoreCase)
        && string.Equals(left.Host, right.Host, StringComparison.OrdinalIgnoreCase)
        && EffectivePort(left) == EffectivePort(right);

    private static int EffectivePort(Uri uri) =>
        uri.IsDefaultPort
            ? (string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ? 443 : 80)
            : uri.Port;

    internal static bool IsPublicUnicast(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        if (IPAddress.IsLoopback(address))
        {
            return false;
        }

        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var bytes = address.GetAddressBytes();
            // 0.0.0.0/8 "this network", 10/8, 100.64/10 CGNAT, 127/8 loopback,
            // 169.254/16 link-local, 172.16/12, 192.0.0/24, 192.168/16,
            // 198.18/15 benchmarking, 224/4 multicast, 240/4 reserved/broadcast.
            return bytes[0] switch
            {
                0 or 10 or 127 => false,
                100 => bytes[1] < 64 || bytes[1] > 127,
                169 => bytes[1] != 254,
                172 => !(bytes[1] >= 16 && bytes[1] <= 31),
                192 when bytes[1] == 0 => bytes[2] != 0,
                192 when bytes[1] == 168 => false,
                198 => !(bytes[1] == 18 || bytes[1] == 19),
                >= 224 => false,
                _ => true,
            };
        }

        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            if (address.Equals(IPAddress.IPv6Any)
                || address.Equals(IPAddress.IPv6None)
                || address.IsIPv6LinkLocal
                || address.IsIPv6SiteLocal
                || address.IsIPv6Multicast)
            {
                return false;
            }

            var bytes = address.GetAddressBytes();
            // fc00::/7 unique-local.
            return (bytes[0] & 0xFE) != 0xFC;
        }

        return false;
    }
}
