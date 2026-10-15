using System.Text.RegularExpressions;

namespace Application.Jobs;

/// <summary>
/// A deterministic work-mode reading. <see cref="Source"/> records how it was
/// derived so a later, better signal can upgrade it: <c>statement</c> (an
/// explicit sentence), <c>title</c> (a cue in the title) or <c>location</c>
/// (a low-confidence inference from the NZ location/region). <c>Unknown</c>
/// carries no source.
/// </summary>
public sealed record WorkModeExtraction(string Mode, string? Source, decimal? Confidence)
{
    public static readonly WorkModeExtraction Unknown = new("Unknown", null, null);

    public bool HasValue => Mode != "Unknown";
}

/// <summary>
/// Deterministic work-mode extraction from the full available text (title,
/// location/region, summary and sanitised description).
///
/// Rules, strongest first:
/// <list type="number">
/// <item>An explicit labelled field (<c>Work mode: Hybrid</c>) wins.</item>
/// <item>Office-day counts: "5 days in the office" is on-site, "2-3 days in
/// our office" is hybrid, and any "N days remote/WFH" is hybrid.</item>
/// <item>Any hybrid wording beats a co-occurring remote mention.</item>
/// <item>Explicit remote wording ("fully remote", "work from home", "WFH",
/// "work from anywhere").</item>
/// <item>Explicit on-site wording ("on-site", "office-based", "based in our
/// Auckland office").</item>
/// <item>A title cue (lower confidence).</item>
/// <item>An NZ location with no remote/hybrid mention implies on-site
/// (lowest confidence, tagged <c>location</c> so it can be upgraded).</item>
/// </list>
///
/// NZ nuance: "flexible working hours" is about hours, not location, and must
/// not trigger Hybrid. Only "flexible working arrangements/options/..." does.
/// </summary>
public static class WorkModeExtractor
{
    private static readonly Regex Labelled = new(
        @"(?ix)\b(?:work\s*mode|workplace\s*type|work\s*style|working\s*(?:style|arrangement))\s*[:\-]\s*(?<mode>remote|hybrid|on[\s-]?site|onsite)\b",
        RegexOptions.Compiled);

    // "2-3 days a week in our Auckland office", "3 days per week in the office"
    private static readonly Regex OfficeDaysRange = new(
        @"(?ix)\b(?<lo>\d{1,2})(?:\s*(?:-|to|–|—)\s*(?<hi>\d{1,2}))?\s*days?\s+(?:a|per)\s+week\s+(?:in|at|from|on|working\s+in)\b[^.\n]{0,30}?\b(?:office|site|campus)\b",
        RegexOptions.Compiled);

    // "3 days in the office", "5 days in our Auckland office"
    private static readonly Regex OfficeDays = new(
        @"(?ix)\b(?<lo>\d{1,2})(?:\s*(?:-|to|–|—)\s*(?<hi>\d{1,2}))?\s*days?\s+(?:in|at|on)\s+(?:the\s+|our\s+|an?\s+)?(?:[\w'’]+\s+){0,2}?(?:office|site|campus)\b",
        RegexOptions.Compiled);

    // "2 days remote", "2 days WFH", "1-day WFH", "3 days from home"
    private static readonly Regex RemoteDays = new(
        @"(?ix)
        \b\d{1,2}\s*days?\s+(?:remote|from\s+home|wfh|working\s+from\s+home)\b
        | \b(?:remote|wfh|work(?:ing)?\s+from\s+home)\s+\d{1,2}\s*days?\b
        | \b\d{1,2}[\s-]*day\b[^.\n]{0,12}?\b(?:wfh|remote|from\s+home)\b
        | \b(?:wfh|remote|from\s+home)\b[^.\n]{0,12}?\b\d{1,2}[\s-]*days?\b
        ",
        RegexOptions.Compiled | RegexOptions.IgnorePatternWhitespace);

    private static readonly Regex Hybrid = new(
        @"(?ix)
        \bhybrid\b(?!\s*(?:cloud|multi[\s-]?cloud|infrastructure|storage|network|premises|architecture|solution|it\b))
        | \bmix(?:ing|ed)?\s+of\s+(?:office|home|remote|onsite|on[\s-]?site)
        | \b(?:split|balance)\s+(?:between|across)\s+(?:the\s+)?(?:office|home|remote)
        | \bflexible\s+working\s+(?:arrangements|options|environment|policy|model|practices|opportunities)
        | \bflexible\s+work\s+(?:arrangements|options|policy|practices)
        | \b(?:office|home|remote)\s+and\s+(?:office|home|remote)\b
        | \bwork\s+from\s+home\s+(?:a\s+)?(?:couple|few|\d)\s+days?
        | \bcouple\s+of\s+days\s+(?:from\s+home|wfh|remote)
        ",
        RegexOptions.Compiled | RegexOptions.IgnorePatternWhitespace);

    private static readonly Regex RemoteStrong = new(
        @"(?ix)
        \bfully\s+remote\b
        | \b100\s*%\s*remote\b
        | \bremote[\s-]?first\b
        | \bwork(?:ing)?\s+from\s+home\b
        | \bwfh\b
        | \bwork(?:ing)?\s+(?:fully\s+)?remotely\b
        | \bwork(?:ing)?\s+from\s+anywhere\b
        | \banywhere\s+in\s+(?:nz|new\s+zealand)\b
        | \bremote\s+(?:role|position|opportunity|working|work|job|based|option|arrangement|team|first|only)\b
        | \bhome[\s-]based\b
        | \btelecommut\w*
        ",
        RegexOptions.Compiled | RegexOptions.IgnorePatternWhitespace);

    // A bare "remote" is accepted only when it is not the "remote support /
    // access / desktop" task sense that has nothing to do with location.
    private static readonly Regex RemoteWeak = new(
        @"(?ix)\bremote\b(?!\s+(?:support|access|desktop|monitoring|troubleshoot\w*|diagnos\w*|management|control|instrument\w*|sensing|hands?|sites?|servers?|devices?|connectivity|backup|assistance|assist))",
        RegexOptions.Compiled);

    private static readonly Regex LocationRemoteMarker = new(
        @"(?ix)\b(?:remote|wfh|work\s+from\s+home)\b",
        RegexOptions.Compiled);

    private static readonly Regex OnSite = new(
        @"(?ix)
        \bon[\s-]?site\b
        | \bonsite\b
        | \boffice[\s-]based\b
        | \bbased\s+(?:in|at)\s+(?:our|the)\s+[\w'’\s]{0,30}?\boffice\b
        | \bwork(?:ing)?\s+(?:from|in|at)\s+(?:the|our)\s+[\w'’\s]{0,30}?\boffice\b
        | \bin\s+(?:the|our)\s+[\w'’\s]{0,30}?\boffice\b
        | \battendance\s+(?:at|in)\s+(?:the|our)\s+office\b
        | \bfull[\s-]?time\s+in\s+(?:the\s+)?office\b
        | \ball\s+office\b
        ",
        RegexOptions.Compiled | RegexOptions.IgnorePatternWhitespace);

    private static readonly Regex TitleRemote = new(
        @"(?ix)\b(?:remote|wfh|work\s+from\s+home)\b(?!\s+sensing)",
        RegexOptions.Compiled);

    private static readonly Regex TitleOnSite = new(
        @"(?ix)\b(?:on[\s-]?site|onsite|office[\s-]based)\b",
        RegexOptions.Compiled);

    /// <summary>Canonical NZ regions, used for the on-site location inference.</summary>
    private static readonly HashSet<string> NzRegions = new(StringComparer.OrdinalIgnoreCase)
    {
        "Auckland", "Wellington", "Canterbury", "Waikato", "Otago", "Bay of Plenty",
        "Manawatū-Whanganui", "Hawke's Bay", "Taranaki", "Northland", "Gisborne",
        "Nelson", "Marlborough", "Tasman", "West Coast", "Southland"
    };

    public static WorkModeExtraction Extract(
        string? title,
        string? location,
        string? region,
        string? summary,
        string? description)
    {
        var titleText = title ?? string.Empty;
        var clean = FieldTextSanitizer.Sanitize(Join(summary, description));

        // 1. Explicit labelled field.
        var labelled = Labelled.Match(clean);
        if (!labelled.Success)
        {
            labelled = Labelled.Match(titleText);
        }

        if (labelled.Success)
        {
            var mode = Normalise(labelled.Groups["mode"].Value);
            if (mode != "Unknown")
            {
                return new WorkModeExtraction(mode, "statement", 1.0m);
            }
        }

        // 2. Office-day counts.
        var officeDays = MatchOfficeDays(clean);
        if (officeDays is not null)
        {
            return officeDays.Value.Mode == "On-site"
                ? new WorkModeExtraction("On-site", "statement", 0.9m)
                : new WorkModeExtraction("Hybrid", "statement", 0.85m);
        }

        if (RemoteDays.IsMatch(clean))
        {
            return new WorkModeExtraction("Hybrid", "statement", 0.8m);
        }

        // 3. Hybrid wording beats a co-occurring remote mention.
        if (Hybrid.IsMatch(clean))
        {
            return new WorkModeExtraction("Hybrid", "statement", 0.8m);
        }

        // 4. Remote wording.
        if (RemoteStrong.IsMatch(clean))
        {
            return new WorkModeExtraction("Remote", "statement", 0.9m);
        }

        if (RemoteWeak.IsMatch(clean))
        {
            return new WorkModeExtraction("Remote", "statement", 0.6m);
        }

        // 5. On-site wording.
        if (OnSite.IsMatch(clean))
        {
            return new WorkModeExtraction("On-site", "statement", 0.85m);
        }

        // 6. Title cue.
        if (TitleRemote.IsMatch(titleText))
        {
            return new WorkModeExtraction("Remote", "title", 0.7m);
        }

        if (Hybrid.IsMatch(titleText))
        {
            return new WorkModeExtraction("Hybrid", "title", 0.7m);
        }

        if (TitleOnSite.IsMatch(titleText))
        {
            return new WorkModeExtraction("On-site", "title", 0.7m);
        }

        // 7. NZ location inference (low confidence, tagged so it can be upgraded).
        if (!string.IsNullOrWhiteSpace(location)
            && !LocationRemoteMarker.IsMatch(location)
            && NzRegions.Contains(region ?? string.Empty))
        {
            return new WorkModeExtraction("On-site", "location", 0.35m);
        }

        return WorkModeExtraction.Unknown;
    }

    private static (string Mode, int Lo)? MatchOfficeDays(string text)
    {
        // Range form: "2-3 days a week in our office" -> hybrid unless the
        // minimum is a full week.
        var range = OfficeDaysRange.Match(text);
        if (range.Success)
        {
            var lo = int.Parse(range.Groups["lo"].Value);
            return (lo >= 5 ? "On-site" : "Hybrid", lo);
        }

        var single = OfficeDays.Match(text);
        if (single.Success)
        {
            var lo = int.Parse(single.Groups["lo"].Value);
            return (lo >= 5 ? "On-site" : "Hybrid", lo);
        }

        return null;
    }

    private static string Normalise(string value)
    {
        var lower = value.ToLowerInvariant().Replace(" ", "-");
        return lower switch
        {
            "remote" => "Remote",
            "hybrid" => "Hybrid",
            "on-site" or "onsite" => "On-site",
            _ => "Unknown"
        };
    }

    private static string Join(string? summary, string? description)
    {
        if (string.IsNullOrWhiteSpace(summary)) return description ?? string.Empty;
        if (string.IsNullOrWhiteSpace(description)) return summary;
        return summary + "\n" + description;
    }
}
