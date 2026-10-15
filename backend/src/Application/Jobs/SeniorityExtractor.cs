using System.Text.RegularExpressions;

namespace Application.Jobs;

/// <summary>
/// A deterministic seniority reading. Values stay within the existing
/// vocabulary (<c>Junior</c>, <c>Intermediate</c>, <c>Senior</c>, <c>Lead</c>)
/// so dashboard filters keep working; NZ "Graduate/Intern/Cadet" programmes map
/// to <c>Junior</c>.
/// </summary>
public sealed record SeniorityExtraction(string Value, string? Source, decimal? Confidence)
{
    public static readonly SeniorityExtraction Unknown = new("Unknown", null, null);

    public bool HasValue => Value != "Unknown";
}

/// <summary>
/// Deterministic seniority extraction. An explicit title marker always wins;
/// otherwise a description signal (a graduate/entry programme, or a years-of-
/// experience band) is used. Bands follow the plan: 0-2 Junior, 2-5
/// Intermediate, 5+ Senior, 8+ Lead.
///
/// The title is scanned, never the company name, so "Senior Developer at Salt"
/// is Senior because of the title and an agency called "Senior" can never leak
/// in. Title markers are recorded as <c>title</c> provenance; description
/// signals as <c>description</c>, so the weaker default can be upgraded later.
/// </summary>
public static class SeniorityExtractor
{
    private static readonly Regex TitleJunior = new(
        @"(?ix)\b(?:junior|jnr|entry[\s-]?level|graduate|intern|internship|cadet)\b",
        RegexOptions.Compiled);

    private static readonly Regex TitleLead = new(
        @"(?ix)\b(?:lead|principal|head\s+of|chief)\b|\bstaff\s+(?:engineer|software|developer|sre|platform|data|architect|designer|product|security)\b",
        RegexOptions.Compiled);

    private static readonly Regex TitleSenior = new(
        @"(?ix)\b(?:senior|snr|sr\.?)\b",
        RegexOptions.Compiled);

    private static readonly Regex TitleIntermediate = new(
        @"(?ix)\bintermediate\b",
        RegexOptions.Compiled);

    private static readonly Regex DescriptionJunior = new(
        @"(?ix)
        \b(?:recent|new)\s+graduate
        | \bgraduate\s+(?:programme|program|role|position|opportunity|intake|scheme|development|pathway|career)
        | \b(?:summer\s+)?internship\s+(?:programme|program|role|position|opportunity)
        | \bintern\s+(?:programme|program|role|position|opportunity)
        | \bentry[\s-]?level\b
        | \byour\s+first\s+(?:role|job|position|professional)
        | \bno\s+(?:prior\s+|previous\s+)?(?:professional\s+)?experience\s+(?:is\s+)?(?:required|necessary|needed)
        | \bcadetship\b
        ",
        RegexOptions.Compiled | RegexOptions.IgnorePatternWhitespace);

    // "5+ years", "at least 8 years", "3-5 years", "minimum of 7 years"
    private static readonly Regex YearsExperience = new(
        @"(?ix)
        (?:(?:at\s+least|minimum(?:\s+of)?|over|more\s+than)\s+)?
        (?<lo>\d{1,2})(?:\s*\+)?(?:\s*(?:-|to|–|—)\s*(?<hi>\d{1,2}))?
        \s+years?(?:'|’)?\s+(?:of\s+)?(?:relevant\s+|professional\s+|commercial\s+|industry\s+|hands[\s-]?on\s+)?experience
        ",
        RegexOptions.Compiled | RegexOptions.IgnorePatternWhitespace);

    private static readonly Regex DescriptionSenior = new(
        @"(?ix)\b(?:senior|principal|lead|staff|head\s+of)\s+(?:role|position|level|capacity)\b",
        RegexOptions.Compiled);

    public static SeniorityExtraction Extract(string? title, string? summary, string? description)
    {
        var titleText = title ?? string.Empty;

        // Title first: an explicit word is the strongest, cheapest signal.
        if (TitleJunior.IsMatch(titleText)) return new SeniorityExtraction("Junior", "title", 0.95m);
        if (TitleLead.IsMatch(titleText)) return new SeniorityExtraction("Lead", "title", 0.95m);
        if (TitleSenior.IsMatch(titleText)) return new SeniorityExtraction("Senior", "title", 0.95m);
        if (TitleIntermediate.IsMatch(titleText)) return new SeniorityExtraction("Intermediate", "title", 0.95m);

        var text = FieldTextSanitizer.Sanitize(
            string.Join('\n', new[] { summary, description }.Where(part => !string.IsNullOrWhiteSpace(part))));

        if (string.IsNullOrWhiteSpace(text))
        {
            return SeniorityExtraction.Unknown;
        }

        if (DescriptionJunior.IsMatch(text))
        {
            return new SeniorityExtraction("Junior", "description", 0.7m);
        }

        if (DescriptionSenior.IsMatch(text))
        {
            return new SeniorityExtraction("Senior", "description", 0.5m);
        }

        var years = MaxYearsExperience(text);
        if (years is { } value)
        {
            return value switch
            {
                >= 8 => new SeniorityExtraction("Lead", "description", 0.55m),
                >= 5 => new SeniorityExtraction("Senior", "description", 0.55m),
                > 2 => new SeniorityExtraction("Intermediate", "description", 0.45m),
                _ => new SeniorityExtraction("Junior", "description", 0.45m)
            };
        }

        return SeniorityExtraction.Unknown;
    }

    /// <summary>
    /// True when the title carries an explicit seniority marker. Used by the
    /// backfill to distinguish a real, source-derived value from the
    /// uninformative <c>Intermediate</c> default that may be refined.
    /// </summary>
    public static bool HasExplicitTitleMarker(string? title)
    {
        var value = title ?? string.Empty;
        return TitleJunior.IsMatch(value)
            || TitleLead.IsMatch(value)
            || TitleSenior.IsMatch(value)
            || TitleIntermediate.IsMatch(value);
    }

    /// <summary>
    /// Beyond this many years the figure is more likely to describe the
    /// company ("over 28 years' experience servicing Nelson") than a candidate
    /// requirement, so it is not used as a seniority band.
    /// </summary>
    private const int MaxPlausibleCandidateYears = 20;

    private static int? MaxYearsExperience(string text)
    {
        var best = -1;
        foreach (Match match in YearsExperience.Matches(text))
        {
            // The band's upper edge is the more conservative "you will have"
            // requirement when a range is advertised.
            var candidate = int.Parse(match.Groups["hi"].Success
                ? match.Groups["hi"].Value
                : match.Groups["lo"].Value);
            if (candidate > MaxPlausibleCandidateYears)
            {
                continue;
            }

            if (candidate > best)
            {
                best = candidate;
            }
        }

        return best < 0 ? null : best;
    }
}
