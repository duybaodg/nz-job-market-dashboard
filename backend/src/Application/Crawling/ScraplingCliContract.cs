namespace Application.Crawling;

/// <summary>
/// The single source of truth for the values the .NET crawler may pass to
/// <c>scrapling_worker.py</c>.
///
/// The Xero / Trade Me Jobs / Gallagher failures were caused by the
/// CLI's <c>--parser</c> choices falling behind the source registry: the
/// adapter forwarded a registry value such as <c>ashby</c> and argparse exited
/// with status 2 (<c>invalid choice</c>). Both crawl paths — the worker's
/// scheduled loop and the API admin trigger — funnel through this mapping, so a
/// registry edit can never hand an invalid value to the Python boundary.
///
/// <see cref="ParserChoices"/> and <see cref="FetcherChoices"/> are kept in
/// lock-step with <c>PARSER_CHOICES</c>/<c>FETCHER_CHOICES</c> in
/// <c>crawler/adapters/scrapling/scrapling_worker.py</c> by
/// <c>ScraplingCliContractTests</c>, which reads the Python file and
/// <c>crawler/sources/sites.json</c> at test time.
/// </summary>
public static class ScraplingCliContract
{
    public static readonly IReadOnlySet<string> ParserChoices = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "generic",
        "jsonld",
        "govt-nz",
        "absolute-it",
        "talent-army",
        "halter-ashby",
        "beyond-recruitment",
        "consult-recruitment",
        "comspek",
        "salt",
        "digital-garage",
        "jobseek",
        "airnz",
        "linkedin",
        "seek",
        "trade-me",
        "pageup",
        "rss",
        "hays",
        "ashby",
        "greenhouse",
        "lever",
        "workable",
        "smartrecruiters",
        "bamboohr",
        "workday",
        "tribe",
        "oracle",
        "salesforce",
    };

    public static readonly IReadOnlySet<string> FetcherChoices = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "fetcher",
        "dynamic",
        "stealth",
        "http",
        "browser",
        "firecrawl",
    };

    //: Registry/legacy values that are not themselves CLI choices.
    private static readonly Dictionary<string, string> ParserAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        [""] = "generic",
        ["custom"] = "generic",
        ["dynamic"] = "generic",
        ["api"] = "generic",
        ["html"] = "generic",
        ["sitemap"] = "generic",
        ["sitemap-html"] = "generic",
        ["json-ld"] = "jsonld",
        ["json+ld"] = "jsonld",
        ["schema"] = "jsonld",
        ["successfactors"] = "rss",
        ["success-factors"] = "rss",
        ["eightfold"] = "generic",
        ["jobadder"] = "generic",
        ["volcanic"] = "generic",
        ["icims"] = "generic",
        ["taleo"] = "generic",
        ["linkedin-mco"] = "linkedin",
        ["linkedin-jobs"] = "linkedin",
    };

    private static readonly Dictionary<string, string> FetcherAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        [""] = "fetcher",
        ["scrapling"] = "fetcher",
        ["js"] = "dynamic",
        ["javascript"] = "dynamic",
        ["playwright"] = "browser",
        ["patchright"] = "browser",
        ["chrome"] = "browser",
        ["chromium"] = "browser",
        ["headless"] = "browser",
        ["static"] = "http",
        ["requests"] = "http",
        ["urllib"] = "http",
        ["firecrawl-selfhosted"] = "firecrawl",
        ["firecrawl-self-hosted"] = "firecrawl",
    };

    /// <summary>
    /// Maps any registry/alias parser value to a <c>--parser</c> choice. Unknown
    /// values fall back to <c>generic</c> rather than failing the run.
    /// </summary>
    public static string NormaliseParser(string? parser)
    {
        var value = (parser ?? string.Empty).Trim();
        if (ParserChoices.Contains(value))
        {
            return value.ToLowerInvariant();
        }

        if (ParserAliases.TryGetValue(value, out var mapped))
        {
            return mapped;
        }

        return "generic";
    }

    /// <summary>
    /// Maps any configured fetcher value to a <c>--fetcher-type</c> choice.
    /// Unknown values fall back to <c>fetcher</c>.
    /// </summary>
    public static string NormaliseFetcher(string? fetcher)
    {
        var value = (fetcher ?? string.Empty).Trim();
        if (FetcherChoices.Contains(value))
        {
            return value.ToLowerInvariant();
        }

        if (FetcherAliases.TryGetValue(value, out var mapped))
        {
            return mapped;
        }

        return "fetcher";
    }
}
