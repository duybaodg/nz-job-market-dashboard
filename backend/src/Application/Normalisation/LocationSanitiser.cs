using System.Text.RegularExpressions;

namespace Application.Normalisation;

/// <summary>
/// Result of sanitising a raw location string.
/// <see cref="Location"/> is the canonical place (or <c>null</c> when nothing
/// usable was found), <see cref="RemovedRemainder"/> is content that was cut
/// off so the caller can preserve it in the description, and
/// <see cref="RegionHint"/> carries an explicit region for a location that was
/// recognised but intentionally not stored (currently
/// <see cref="NzPlaceDirectory.InternationalRegion"/> for non-NZ places).
/// </summary>
public sealed record LocationSanitisation(
    string? Location,
    string? RemovedRemainder,
    string? RegionHint = null);

/// <summary>
/// Shared location normaliser applied at ingestion time and by the startup
/// repair pass. Round 5 turns the previous "cut the description tail" pass into
/// a canonical NZ place model:
/// <list type="bullet">
/// <item>a value becomes at most <c>"City, Region"</c> (suburbs fold into
/// their city, e.g. <c>"Manukau City, Auckland, NZ"</c> → <c>"Auckland"</c>,
/// <c>"Hamilton, New Zealand, NZ"</c> → <c>"Hamilton, Waikato"</c>);</item>
/// <item>noise tokens (country, <c>NZ</c>, "Region"/"Greater", duplicated
/// tokens, employment markers, rate/start text) are stripped wherever they
/// appear;</item>
/// <item>"NZ: Remote"/"Remote"/"Work from home" → <c>"Remote (NZ)"</c>;</item>
/// <item>a non-NZ place list → <c>location = null</c> with
/// <c>region = "International"</c>;</item>
/// <item>a sentence fragment with no known place name → <c>null</c>.</item>
/// </list>
/// Every rule is data-driven through <see cref="NzPlaceDirectory"/> so a new
/// suburb alias is a one-line table addition, not a new branch.
/// </summary>
public static class LocationSanitiser
{
    public const int MaxLength = 60;

    public const string RemoteLocation = "Remote (NZ)";

    public const string NewZealandLocation = "New Zealand";

    private static readonly Regex Whitespace = new(@"\s+", RegexOptions.Compiled);

    //: Starts a new field/paragraph after a location. The leading \s* means the
    //: cut happens before the separating space.
    private static readonly Regex StopMarker = new(
        """
        (?ix)
        \s*
        (?:
            job\s+type
          | job\s+description
          | job\s+category
          | speciali[sz]ation
          | reference
          | discipline
          | salary
          | category
          | posted
          | employment\s+type
          | work\s+type
          | contract\s+type
          | industry
          | contact\s+name
          | contact\s+details
          | contact
          | career\s+growth
          | closing\s+date
          | department
          | we\s+(?:also|are|have|offer|provide|believe|welcome)
          | the\s+role
          | about\s+(?:the|us|you|this)
          | key\s+responsibilit\w*
          | what\s+you
          | apply\s+now
          | please\s+(?:note|apply|contact)
          | join\s+(?:our|us|the)
          | rate\s*:
          | start\s*:
          | asap
          | urgent
          | (?:temporary|permanent|contract|casual)\s+job
          | (?:full|part)[_\s-]?time\s+job
          | fixed[_\s-]?term\s+job
        )\b
        """,
        RegexOptions.Compiled | RegexOptions.IgnorePatternWhitespace | RegexOptions.IgnoreCase);

    //: A sentence boundary (". Next sentence") that is not a common abbreviation.
    private static readonly Regex SentenceBoundary = new(
        @"(?<=\w)[.!?]\s+(?=[A-Z])",
        RegexOptions.Compiled);

    //: Comma/semicolon/pipe and spaced dash/ampersand separate the place
    //: segments; parentheses are treated as separators too so
    //: "Auckland (Albany HQ) (NZ)" exposes all three tokens.
    private static readonly Regex SegmentSplit = new(
        @"\s*[,;|/]\s*|\s+[-–—]\s+|\s*[()\[\]{}]\s*|\s+&\s+",
        RegexOptions.Compiled);

    private static readonly Regex RemotePattern = new(
        @"(?<![\p{L}\d])(?:remote|work\s+from\s+home|wfh|work\s+anywhere|telecommut\w*)(?![\p{L}\d])",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex NzWidePattern = new(
        @"(?<![\p{L}\d])(?:new\s+zealand|aotearoa|nationwide|countrywide|national|nz)(?![\p{L}\d])",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly HashSet<string> Abbreviations = new(StringComparer.OrdinalIgnoreCase)
    {
        "st", "mt", "dr", "mr", "mrs", "ms", "no", "ave", "rd", "po", "sq", "co", "inc", "ltd"
    };

    private enum LocationKind
    {
        Place,
        International,
        Junk
    }

    private readonly record struct LocationAnalysis(string? Location, LocationKind Kind);

    public static LocationSanitisation Sanitise(string? location)
    {
        if (string.IsNullOrWhiteSpace(location))
        {
            return new LocationSanitisation(null, null);
        }

        var original = Whitespace.Replace(location, " ").Trim();

        var value = original;
        var marker = StopMarker.Match(value);
        if (marker.Success)
        {
            if (marker.Index == 0)
            {
                // The whole value is a description marker, not a location. Keep
                // the text, but flag overseas content so the region is right.
                var hint = NzPlaceDirectory.ContainsForeignMarker(original)
                    ? NzPlaceDirectory.InternationalRegion
                    : null;
                return new LocationSanitisation(null, original, hint);
            }

            value = value[..marker.Index];
        }
        else if (value.Length > MaxLength)
        {
            var boundary = FindSentenceBoundary(value);
            if (boundary > 0)
            {
                value = value[..boundary];
            }
        }

        var remainder = value.Length < original.Length
            ? CleanRemainder(original[value.Length..])
            : null;

        var analysis = Analyse(value);

        if (analysis.Location is not null)
        {
            var clean = HardCap(analysis.Location.Trim().Trim('.', ',', ';', '|', '-', ' ', '\t'));
            return new LocationSanitisation(string.IsNullOrEmpty(clean) ? null : clean, remainder);
        }

        if (analysis.Kind == LocationKind.International)
        {
            // Preserve the raw value in the description so the region stays
            // "International" on the next idempotent repair pass. A foreign
            // place is a location, so it is never treated as place noise.
            return new LocationSanitisation(
                null,
                original,
                NzPlaceDirectory.InternationalRegion);
        }

        // Junk: return the whole raw value so the caller can move it into the
        // description rather than silently lose it.
        return new LocationSanitisation(null, original);
    }

    /// <summary>
    /// Prepends a removed location remainder to a description without creating
    /// an obvious duplicate. Used by ingestion and the repair pass.
    /// </summary>
    public static string? PrependRemainder(string? remainder, string? description)
    {
        if (string.IsNullOrWhiteSpace(remainder))
        {
            return description;
        }

        var trimmed = remainder.Trim();
        var text = description?.Trim() ?? string.Empty;
        if (text.Contains(trimmed, StringComparison.OrdinalIgnoreCase))
        {
            return text;
        }

        return string.IsNullOrEmpty(text) ? trimmed : $"{trimmed} {text}";
    }

    private static LocationAnalysis Analyse(string value)
    {
        var hasForeign = NzPlaceDirectory.ContainsForeignMarker(value);
        var hasRemote = RemotePattern.IsMatch(value);
        var hasNzWide = NzWidePattern.IsMatch(value);

        string? aliasCity = null;
        string? aliasRegion = null;
        string? cityCity = null;
        string? cityRegion = null;
        var cityExact = false;
        string? regionOnly = null;

        foreach (var segment in SegmentSplit.Split(value))
        {
            var normalised = NzPlaceDirectory.NormaliseSegment(segment);
            if (normalised.Length == 0)
            {
                continue;
            }

            if (aliasCity is null
                && NzPlaceDirectory.TryResolveAlias(normalised, out var alias, out var aliasReg, out _))
            {
                aliasCity = alias;
                aliasRegion = aliasReg;
            }

            if (cityCity is null
                && NzPlaceDirectory.TryResolveCity(normalised, out var city, out var cityReg, out var exact))
            {
                cityCity = city;
                cityRegion = cityReg;
                cityExact = exact;
            }

            if (regionOnly is null
                && NzPlaceDirectory.TryResolveRegionOnly(normalised, out var region))
            {
                regionOnly = region;
            }
        }

        // A recognisable non-NZ list ("Adelaide, Brisbane, Canberra",
        // "Allen, TX; …; Remote") is not a NZ location. A suburb alias is not
        // enough to override that: only an exact NZ city name is.
        if (hasForeign && !cityExact)
        {
            return new LocationAnalysis(null, LocationKind.International);
        }

        // The most specific match wins: a suburb alias (Trentham) beats a broad
        // city (Wellington), a city beats a bare region (Canterbury).
        if (aliasCity is not null)
        {
            return new LocationAnalysis(Build(aliasCity, aliasRegion), LocationKind.Place);
        }

        if (cityCity is not null)
        {
            return new LocationAnalysis(Build(cityCity, cityRegion), LocationKind.Place);
        }

        if (regionOnly is not null)
        {
            return new LocationAnalysis(regionOnly, LocationKind.Place);
        }

        if (hasRemote)
        {
            return new LocationAnalysis(RemoteLocation, LocationKind.Place);
        }

        if (hasNzWide && IsEssentiallyNzWide(value))
        {
            return new LocationAnalysis(NewZealandLocation, LocationKind.Place);
        }

        return new LocationAnalysis(null, LocationKind.Junk);
    }

    /// <summary>"Auckland" for a city that is also its region, else "City, Region".</summary>
    private static string Build(string city, string? region)
    {
        if (string.IsNullOrWhiteSpace(region)
            || string.Equals(city, region, StringComparison.OrdinalIgnoreCase))
        {
            return city;
        }

        return $"{city}, {region}";
    }

    /// <summary>
    /// True when the value is nothing but NZ-wide wording ("New Zealand",
    /// "All New Zealand"). "New Zealand's legal and political heart" is not.
    /// </summary>
    private static bool IsEssentiallyNzWide(string value)
    {
        var tokens = Regex
            .Replace(value, @"[^\p{L}\p{N}\s]", " ")
            .Split(' ', StringSplitOptions.RemoveEmptyEntries);

        return tokens.Length == 0 || tokens.All(NzPlaceDirectory.IsNoiseToken);
    }

    private static string? CleanRemainder(string value)
    {
        var trimmed = value.Trim().Trim(',', ';', '|', '-', ' ', '\t');
        return string.IsNullOrWhiteSpace(trimmed) ? null : trimmed;
    }

    private static int FindSentenceBoundary(string value)
    {
        foreach (Match match in SentenceBoundary.Matches(value))
        {
            var tokenStart = match.Index - 1;
            while (tokenStart > 0 && char.IsLetter(value[tokenStart - 1]))
            {
                tokenStart--;
            }

            var token = value[tokenStart..match.Index];
            if (!Abbreviations.Contains(token))
            {
                return match.Index + 1;
            }
        }

        return -1;
    }

    private static string HardCap(string value)
    {
        if (value.Length <= MaxLength)
        {
            return value;
        }

        var window = value[..MaxLength];
        var separator = window.LastIndexOfAny([',', ';']);
        if (separator > 0)
        {
            return window[..separator];
        }

        var space = window.LastIndexOf(' ');
        return space > 0 ? window[..space] : window;
    }
}
