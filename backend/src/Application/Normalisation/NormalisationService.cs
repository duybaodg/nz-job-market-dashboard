using System.Text.RegularExpressions;

namespace Application.Normalisation;

public interface INormalisationService
{
    /// <summary>
    /// Maps a location string to a canonical NZ region. A missing location
    /// resolves to the country-level default <c>New Zealand</c>; a location
    /// that is present and recognisably non-NZ resolves to
    /// <c>International</c>; any other present but unrecognisable value stays
    /// <c>Other</c> so it remains distinguishable from a data hole.
    /// </summary>
    string StandardiseRegion(string? location);

    /// <summary>
    /// Region resolution used by ingestion and cleanup. When
    /// <paramref name="location"/> is missing the surrounding
    /// <paramref name="context"/> text is scanned for a recognisable NZ place
    /// before falling back to <c>New Zealand</c>.
    /// </summary>
    string ResolveRegion(string? location, string? context = null);

    string StandardiseSeniority(string? title);
}

public sealed class NormalisationService : INormalisationService
{
    // City/suburb -> region. Region names are checked after cities so a string
    // such as "Auckland, New Zealand" resolves to Auckland, not New Zealand.
    // Both macron and non-macron spellings are accepted.
    private static readonly (Regex Pattern, string Region)[] RegionRules = BuildRegionRules();

    private static readonly Regex RemotePattern = Word(@"remote|work\s+from\s+home|wfh|anywhere");
    private static readonly Regex NzWidePattern = Word(@"new\s+zealand|nz\-wide|nationwide|countrywide|national");
    private static readonly Regex InternationalPattern = Word(
        @"india|australia|united\s+states|usa|u\.s\.|united\s+kingdom|uk|canada|singapore|philippines|ireland|germany|japan|china|malaysia|vietnam|indonesia|south\s+africa|brazil|mexico|poland|spain|france|netherlands");

    public string StandardiseRegion(string? location)
    {
        if (string.IsNullOrWhiteSpace(location))
        {
            return "New Zealand";
        }

        return MatchRegion(location) ?? "Other";
    }

    public string ResolveRegion(string? location, string? context = null)
    {
        if (!string.IsNullOrWhiteSpace(location))
        {
            return StandardiseRegion(location);
        }

        return MatchRegion(context) ?? "New Zealand";
    }

    /// <summary>
    /// True when the value names a recognisable place or work location (an NZ
    /// region/city, a recognised country, remote or NZ-wide).
    /// </summary>
    public static bool ContainsKnownPlace(string? value) => MatchRegion(value) is not null;

    public string StandardiseSeniority(string? title)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return "Unknown";
        }

        var value = title.ToLowerInvariant();

        if (value.Contains("graduate")) return "Graduate";
        if (value.Contains("junior") || value.Contains("entry")) return "Junior";
        if (value.Contains("senior")) return "Senior";
        if (value.Contains("lead") || value.Contains("principal")) return "Lead";

        return "Intermediate";
    }

    private static string? MatchRegion(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        foreach (var (pattern, region) in RegionRules)
        {
            if (pattern.IsMatch(value))
            {
                return region;
            }
        }

        // A foreign marker beats a bare remote token ("Remote - Sydney" is an
        // overseas posting), but an explicit NZ city above already won.
        var foreign = NzPlaceDirectory.ContainsForeignMarker(value)
            || InternationalPattern.IsMatch(value);

        if (!foreign && RemotePattern.IsMatch(value)) return "Remote";
        if (NzWidePattern.IsMatch(value)) return "New Zealand";
        if (foreign) return NzPlaceDirectory.InternationalRegion;

        return null;
    }

    private static (Regex Pattern, string Region)[] BuildRegionRules()
    {
        static (Regex, string) Rule(string region, params string[] keywords) =>
            (Word(string.Join("|", keywords.Select(Regex.Escape))), region);

        return
        [
            Rule("Auckland", "auckland", "manukau", "north shore", "waitakere", "papakura", "pukekohe", "waiheke"),
            Rule("Wellington", "wellington", "porirua", "lower hutt", "upper hutt", "kapiti", "kāpiti", "masterton", "paraparaumu"),
            Rule("Canterbury", "christchurch", "canterbury", "timaru", "ashburton", "rangiora", "rolleston"),
            Rule("Waikato", "hamilton", "waikato", "taupo", "taupō", "cambridge", "te awamutu", "tokoroa", "thames"),
            Rule("Otago", "dunedin", "otago", "queenstown", "wanaka", "oamaru", "alexandra", "cromwell"),
            Rule("Bay of Plenty", "tauranga", "bay of plenty", "rotorua", "whakatane", "whakatāne", "mount maunganui"),
            Rule("Manawatū-Whanganui", "palmerston north", "manawatu", "manawatū", "whanganui", "wanganui", "levin", "feilding", "ohakune"),
            Rule("Hawke's Bay", "hawke's bay", "hawkes bay", "hawke’s bay", "napier", "hastings", "havelock north"),
            Rule("Taranaki", "new plymouth", "taranaki", "stratford", "hawera", "hāwera"),
            Rule("Northland", "whangarei", "whangārei", "northland", "kerikeri", "kaitaia", "dargaville"),
            Rule("Gisborne", "gisborne"),
            Rule("Nelson", "nelson"),
            Rule("Marlborough", "blenheim", "picton", "marlborough"),
            Rule("Tasman", "tasman", "motueka"),
            Rule("West Coast", "greymouth", "hokitika", "westport", "west coast"),
            Rule("Southland", "invercargill", "southland", "gore"),
        ];
    }

    private static Regex Word(string pattern) => new(
        $@"(?<![\p{{L}}\d])(?:{pattern})(?![\p{{L}}\d])",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
}
