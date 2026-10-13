using System.Text.RegularExpressions;

namespace Application.Normalisation;

/// <summary>
/// Data tables for the canonical NZ location model (Round 5).
///
/// Keeping the knowledge in tables (rather than deep if-chains) makes the
/// suburb-to-city folds easy to extend whenever a live string exposes a new
/// alias. Three lookups are exposed:
/// <list type="bullet">
/// <item><see cref="CitiesByKey"/> — canonical town/city → NZ region;</item>
/// <item><see cref="AliasesByKey"/> — suburb/alias → canonical city;</item>
/// <item><see cref="RegionsByKey"/> — region name/spelling → canonical region.</item>
/// </list>
/// <see cref="AliasesByKey"/> wins over <see cref="CitiesByKey"/> so a suburb
/// token can resolve to its parent city even when a broader city name also
/// appears (e.g. "Wellington - Trentham" → Upper Hutt, not Wellington).
/// </summary>
public static class NzPlaceDirectory
{
    /// <summary>
    /// Region value used when a location is recognisably outside New Zealand.
    /// A missing location stays <c>New Zealand</c> and a present but
    /// unrecognisable value stays <c>Other</c>.
    /// </summary>
    public const string InternationalRegion = "International";

    public static readonly IReadOnlyDictionary<string, string> CitiesByKey;

    /// <summary>Canonical city spelling keyed case-insensitively (city → city).</summary>
    public static readonly IReadOnlyDictionary<string, string> CanonicalCitiesByKey;

    public static readonly IReadOnlyDictionary<string, string> AliasesByKey;

    public static readonly IReadOnlyDictionary<string, string> RegionsByKey;

    private static readonly HashSet<string> NoiseTokens;

    private static readonly Regex CityTokenPattern;

    private static readonly Regex AliasTokenPattern;

    private static readonly Regex RegionTokenPattern;

    private static readonly Regex ForeignPattern;

    private static readonly Regex ForeignUpperPattern;

    static NzPlaceDirectory()
    {
        // Canonical region → itself, plus alternate spellings.
        var regions = new (string Canonical, string[] Keys)[]
        {
            ("Northland", ["northland"]),
            ("Auckland", ["auckland"]),
            ("Waikato", ["waikato"]),
            ("Bay of Plenty", ["bay of plenty"]),
            ("Gisborne", ["gisborne"]),
            ("Hawke's Bay", ["hawkes bay", "hawke bay"]),
            ("Taranaki", ["taranaki"]),
            ("Manawatū-Whanganui", ["manawatu-whanganui", "manawatū-whanganui", "manawatu", "whanganui"]),
            ("Wellington", ["wellington"]),
            ("Tasman", ["tasman"]),
            ("Nelson", ["nelson"]),
            ("Marlborough", ["marlborough"]),
            ("West Coast", ["west coast"]),
            ("Canterbury", ["canterbury"]),
            ("Otago", ["otago"]),
            ("Southland", ["southland"])
        };

        var regionMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (canonical, keys) in regions)
        {
            foreach (var key in keys)
            {
                regionMap[key] = canonical;
            }
        }

        RegionsByKey = regionMap;

        // Canonical city/town → region.
        var cities = new (string City, string Region)[]
        {
            // Northland
            ("Whangarei", "Northland"), ("Whangārei", "Northland"), ("Kerikeri", "Northland"),
            ("Kaitaia", "Northland"), ("Dargaville", "Northland"), ("Paihia", "Northland"),
            ("Russell", "Northland"), ("Kaikohe", "Northland"),
            // Auckland
            ("Auckland", "Auckland"), ("Manukau", "Auckland"), ("North Shore", "Auckland"),
            ("Waitakere", "Auckland"), ("Papakura", "Auckland"), ("Pukekohe", "Auckland"),
            ("Waiheke Island", "Auckland"), ("Warkworth", "Auckland"), ("Orewa", "Auckland"),
            ("Helensville", "Auckland"), ("Kumeu", "Auckland"), ("Snells Beach", "Auckland"),
            ("Wellsford", "Auckland"),
            // Waikato
            ("Hamilton", "Waikato"), ("Cambridge", "Waikato"), ("Te Awamutu", "Waikato"),
            ("Tokoroa", "Waikato"), ("Taupo", "Waikato"), ("Taupō", "Waikato"),
            ("Thames", "Waikato"), ("Matamata", "Waikato"), ("Morrinsville", "Waikato"),
            ("Huntly", "Waikato"), ("Ngaruawahia", "Waikato"), ("Ngāruawāhia", "Waikato"),
            ("Raglan", "Waikato"), ("Otorohanga", "Waikato"), ("Te Kuiti", "Waikato"),
            ("Putaruru", "Waikato"), ("Waihi", "Waikato"), ("Paeroa", "Waikato"),
            ("Coromandel", "Waikato"), ("Whitianga", "Waikato"), ("Turangi", "Waikato"),
            ("Tūrangi", "Waikato"),
            // Bay of Plenty
            ("Tauranga", "Bay of Plenty"), ("Rotorua", "Bay of Plenty"),
            ("Whakatane", "Bay of Plenty"), ("Whakatāne", "Bay of Plenty"),
            ("Mount Maunganui", "Bay of Plenty"), ("Te Puke", "Bay of Plenty"),
            ("Katikati", "Bay of Plenty"), ("Kawerau", "Bay of Plenty"),
            ("Opotiki", "Bay of Plenty"), ("Ōpōtiki", "Bay of Plenty"),
            // Gisborne
            ("Gisborne", "Gisborne"),
            // Hawke's Bay
            ("Napier", "Hawke's Bay"), ("Hastings", "Hawke's Bay"),
            ("Havelock North", "Hawke's Bay"), ("Waipukurau", "Hawke's Bay"),
            ("Wairoa", "Hawke's Bay"),
            // Taranaki
            ("New Plymouth", "Taranaki"), ("Stratford", "Taranaki"), ("Hawera", "Taranaki"),
            ("Hāwera", "Taranaki"), ("Waitara", "Taranaki"), ("Inglewood", "Taranaki"),
            ("Opunake", "Taranaki"),
            // Manawatū-Whanganui
            ("Palmerston North", "Manawatū-Whanganui"), ("Whanganui", "Manawatū-Whanganui"),
            ("Wanganui", "Manawatū-Whanganui"), ("Levin", "Manawatū-Whanganui"),
            ("Feilding", "Manawatū-Whanganui"), ("Ohakune", "Manawatū-Whanganui"),
            ("Marton", "Manawatū-Whanganui"), ("Dannevirke", "Manawatū-Whanganui"),
            ("Foxton", "Manawatū-Whanganui"), ("Taihape", "Manawatū-Whanganui"),
            ("Bulls", "Manawatū-Whanganui"),
            // Wellington
            ("Wellington", "Wellington"), ("Porirua", "Wellington"), ("Upper Hutt", "Wellington"),
            ("Kapiti", "Wellington"), ("Paraparaumu", "Wellington"), ("Masterton", "Wellington"),
            ("Carterton", "Wellington"), ("Greytown", "Wellington"), ("Featherston", "Wellington"),
            ("Martinborough", "Wellington"), ("Otaki", "Wellington"), ("Waikanae", "Wellington"),
            ("Raumati", "Wellington"), ("Petone", "Wellington"), ("Johnsonville", "Wellington"),
            // Nelson / Tasman / Marlborough
            ("Nelson", "Nelson"), ("Motueka", "Tasman"), ("Takaka", "Tasman"),
            ("Richmond", "Tasman"), ("Blenheim", "Marlborough"), ("Picton", "Marlborough"),
            ("Kaikoura", "Marlborough"),
            // West Coast
            ("Greymouth", "West Coast"), ("Hokitika", "West Coast"), ("Westport", "West Coast"),
            ("Reefton", "West Coast"),
            // Canterbury
            ("Christchurch", "Canterbury"), ("Timaru", "Canterbury"), ("Ashburton", "Canterbury"),
            ("Rangiora", "Canterbury"), ("Rolleston", "Canterbury"), ("Kaiapoi", "Canterbury"),
            ("Lyttelton", "Canterbury"), ("Geraldine", "Canterbury"), ("Temuka", "Canterbury"),
            ("Waimate", "Canterbury"), ("Methven", "Canterbury"), ("Darfield", "Canterbury"),
            ("Leeston", "Canterbury"), ("Akaroa", "Canterbury"), ("Hanmer Springs", "Canterbury"),
            // Otago
            ("Dunedin", "Otago"), ("Queenstown", "Otago"), ("Wanaka", "Otago"),
            ("Oamaru", "Otago"), ("Alexandra", "Otago"), ("Cromwell", "Otago"),
            ("Balclutha", "Otago"), ("Milton", "Otago"), ("Arrowtown", "Otago"),
            ("Clyde", "Otago"), ("Ranfurly", "Otago"), ("Lawrence", "Otago"),
            ("Mosgiel", "Otago"),
            // Southland
            ("Invercargill", "Southland"), ("Gore", "Southland"), ("Te Anau", "Southland"),
            ("Winton", "Southland"), ("Bluff", "Southland"), ("Riverton", "Southland")
        };

        var cityMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var canonicalCities = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (city, region) in cities)
        {
            cityMap[city] = region;
            canonicalCities[city] = city;
        }

        CitiesByKey = cityMap;
        CanonicalCitiesByKey = canonicalCities;

        // Suburb/alias → canonical city. Only distinctive names belong here;
        // ambiguous global city names (e.g. Richmond) are resolved by the city
        // table instead so a foreign "Richmond, VA" is not mistaken for NZ.
        var aliases = new (string Alias, string City)[]
        {
            // Auckland suburbs
            ("manukau", "Auckland"), ("manukau city", "Auckland"),
            ("east tamaki", "Auckland"), ("takutai", "Auckland"), ("takutai square", "Auckland"),
            ("north shore", "Auckland"), ("albany", "Auckland"), ("penrose", "Auckland"),
            ("mt wellington", "Auckland"), ("mount wellington", "Auckland"),
            ("onehunga", "Auckland"), ("otahuhu", "Auckland"), ("otara", "Auckland"),
            ("ellerslie", "Auckland"), ("greenlane", "Auckland"), ("newmarket", "Auckland"),
            ("parnell", "Auckland"), ("remuera", "Auckland"), ("takapuna", "Auckland"),
            ("devonport", "Auckland"), ("howick", "Auckland"), ("pakuranga", "Auckland"),
            ("botany", "Auckland"), ("mangere", "Auckland"), ("avondale", "Auckland"),
            ("henderson", "Auckland"), ("massey", "Auckland"), ("ranui", "Auckland"),
            ("te atatu", "Auckland"), ("titirangi", "Auckland"), ("waiuku", "Auckland"),
            ("clevedon", "Auckland"), ("beachlands", "Auckland"), ("maraetai", "Auckland"),
            ("whitford", "Auckland"), ("drury", "Auckland"), ("tuakau", "Auckland"),
            ("kohimarama", "Auckland"), ("mission bay", "Auckland"), ("st heliers", "Auckland"),
            ("saint heliers", "Auckland"), ("panmure", "Auckland"), ("glendowie", "Auckland"),
            ("meadowbank", "Auckland"), ("blockhouse bay", "Auckland"), ("hillsborough", "Auckland"),
            ("royal oak", "Auckland"), ("epsom", "Auckland"), ("mount eden", "Auckland"),
            ("mount albert", "Auckland"), ("kingsland", "Auckland"), ("grey lynn", "Auckland"),
            ("ponsonby", "Auckland"), ("herne bay", "Auckland"), ("westmere", "Auckland"),
            ("point chevalier", "Auckland"), ("waterview", "Auckland"), ("sandringham", "Auckland"),
            ("morningside", "Auckland"), ("freemans bay", "Auckland"), ("newton", "Auckland"),
            ("grafton", "Auckland"), ("eden terrace", "Auckland"),
            // Wellington suburbs
            ("lower hutt", "Wellington"), ("trentham", "Upper Hutt"), ("hutt valley", "Wellington"),
            ("karori", "Wellington"), ("miramar", "Wellington"), ("wainuiomata", "Wellington"),
            ("eastbourne", "Wellington"), ("newtown", "Wellington"), ("mount victoria", "Wellington"),
            ("kilbirnie", "Wellington"), ("lyall bay", "Wellington"), ("island bay", "Wellington"),
            ("seatoun", "Wellington"), ("churton park", "Wellington"), ("tawa", "Wellington"),
            // Christchurch suburbs
            ("riccarton", "Christchurch"), ("addington", "Christchurch"), ("papanui", "Christchurch"),
            ("hornby", "Christchurch"), ("halswell", "Christchurch"), ("iredale", "Christchurch"),
            ("merivale", "Christchurch"), ("shirley", "Christchurch"), ("linwood", "Christchurch"),
            ("woolston", "Christchurch"), ("spreydon", "Christchurch"), ("cashmere", "Christchurch"),
            ("burnside", "Christchurch"), ("bishopdale", "Christchurch"), ("harewood", "Christchurch"),
            ("fendalton", "Christchurch"), ("st albans", "Christchurch"),
            ("saint albans", "Christchurch"), ("new brighton", "Christchurch"),
            ("redcliffs", "Christchurch"), ("mount pleasant", "Christchurch"),
            ("southshore", "Christchurch"), ("wigram", "Christchurch"),
            // Hamilton suburbs
            ("te rapa", "Hamilton"), ("chartwell", "Hamilton"), ("dinsdale", "Hamilton"),
            ("frankton", "Hamilton"), ("rototuna", "Hamilton"), ("nawton", "Hamilton"),
            ("melville", "Hamilton"),
            // Tauranga suburbs
            ("papamoa", "Tauranga"), ("bethlehem", "Tauranga"), ("otumoetai", "Tauranga"),
            ("welcome bay", "Tauranga"), ("greerton", "Tauranga"),
            // Dunedin suburbs
            ("green island", "Dunedin"), ("roslyn", "Dunedin"), ("mornington", "Dunedin"),
            ("north east valley", "Dunedin"), ("port chalmers", "Dunedin"),
            // Nelson suburbs
            ("tahunanui", "Nelson"), ("stoke", "Nelson")
        };

        var aliasMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (alias, city) in aliases)
        {
            // Manukau/North Shore are also city-table entries; the alias table is
            // checked first, and both resolve to Auckland, so either is fine.
            aliasMap[alias] = city;
        }

        AliasesByKey = aliasMap;

        NoiseTokens = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "new", "zealand", "nz", "aotearoa",
            "region", "greater", "district", "city", "cbd", "central", "area",
            "location", "locations", "flexible",
            "job", "temporary", "permanent", "full", "part", "time", "contract",
            "casual", "fixed", "term", "asap", "urgent",
            "all", "nationwide", "countrywide", "national", "wide"
        };

        CityTokenPattern = BuildTokenPattern(cityMap.Keys);
        AliasTokenPattern = BuildTokenPattern(aliasMap.Keys);
        RegionTokenPattern = BuildTokenPattern(regionMap.Keys);

        var foreign = new[]
        {
            "india", "australia", "united states", "usa", "u.s.", "u.s.a.",
            "united kingdom", "great britain", "canada", "singapore", "philippines",
            "ireland", "germany", "japan", "china", "malaysia", "vietnam", "indonesia",
            "south africa", "brazil", "mexico", "poland", "spain", "france", "netherlands",
            "sri lanka", "pakistan", "bangladesh", "nepal", "thailand", "south korea",
            "taiwan", "hong kong", "uae", "united arab emirates", "dubai", "qatar",
            "saudi arabia", "bengaluru", "bangalore", "mumbai", "delhi", "hyderabad",
            "chennai", "pune", "sydney", "melbourne", "brisbane", "adelaide", "canberra",
            "perth", "hobart", "darwin", "london", "manchester", "birmingham", "edinburgh",
            "glasgow", "dublin", "toronto", "vancouver", "montreal", "ontario", "quebec",
            "new york", "san francisco", "seattle", "austin", "boston", "chicago", "denver",
            "atlanta", "dallas", "houston", "los angeles", "washington", "california",
            "texas", "colorado", "florida", "virginia", "redmond", "colorado springs",
            "portland", "phoenix", "philadelphia", "san diego", "san jose", "miami",
            "minneapolis", "detroit", "cleveland", "pittsburgh", "salt lake city",
            "las vegas", "nashville", "charlotte", "raleigh", "columbus", "indianapolis",
            "kansas city", "milwaukee", "orlando", "tampa", "baltimore", "arlington",
            "alexandria", "another country", "overseas", "offshore"
        };

        ForeignPattern = new Regex(
            $@"(?<![\p{{L}}\d])(?:{string.Join("|", foreign.Select(Regex.Escape))})(?![\p{{L}}\d])",
            RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        // Two-letter US state abbreviations only when upper-case so the common
        // lower-case English words ("in", "or", "ca") never trigger.
        ForeignUpperPattern = new Regex(
            @"(?<![\p{L}\d])(?:AL|AK|AZ|AR|CA|CO|CT|DE|FL|GA|HI|ID|IL|IN|IA|KS|KY|LA|ME|MD|MA|MI|MN|MS|MO|MT|NE|NV|NH|NJ|NM|NY|NC|ND|OH|OK|OR|PA|RI|SC|SD|TN|TX|UT|VT|VA|WA|WV|WI|WY|DC|AU|UK|USA)(?![\p{L}\d])",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

    }

    /// <summary>
    /// Normalises one comma/semicolon segment: lower-cases, strips apostrophes
    /// and punctuation, and drops noise tokens ("NZ", "Region", "CBD", …).
    /// </summary>
    public static string NormaliseSegment(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var cleaned = value
            .Replace("'", string.Empty)
            .Replace("’", string.Empty)
            .ToLowerInvariant();

        cleaned = Regex.Replace(cleaned, @"[^\p{L}\p{N}\s\-]", " ");
        cleaned = Regex.Replace(cleaned, @"\s+", " ").Trim();

        var tokens = cleaned
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(token => !NoiseTokens.Contains(token));

        return string.Join(' ', tokens).Trim();
    }

    public static bool TryResolveAlias(string segment, out string city, out string region, out bool exact)
    {
        if (AliasesByKey.TryGetValue(segment, out city!))
        {
            region = CitiesByKey[city];
            exact = true;
            return true;
        }

        var match = AliasTokenPattern.Match(segment);
        if (match.Success && AliasesByKey.TryGetValue(match.Value, out city!))
        {
            region = CitiesByKey[city];
            exact = false;
            return true;
        }

        city = string.Empty;
        region = string.Empty;
        exact = false;
        return false;
    }

    public static bool TryResolveCity(string segment, out string city, out string region, out bool exact)
    {
        if (CanonicalCitiesByKey.TryGetValue(segment, out city!)
            && CitiesByKey.TryGetValue(segment, out region!))
        {
            exact = true;
            return true;
        }

        var match = CityTokenPattern.Match(segment);
        if (match.Success)
        {
            // The match value is the dictionary key (case-insensitive lookup).
            var key = CitiesByKey.Keys.FirstOrDefault(candidate =>
                string.Equals(candidate, match.Value, StringComparison.OrdinalIgnoreCase));
            if (key is not null)
            {
                city = key;
                region = CitiesByKey[key];
                exact = false;
                return true;
            }
        }

        city = string.Empty;
        region = string.Empty;
        exact = false;
        return false;
    }

    public static bool TryResolveRegionOnly(string segment, out string region)
    {
        if (RegionsByKey.TryGetValue(segment, out region!))
        {
            return true;
        }

        var match = RegionTokenPattern.Match(segment);
        if (match.Success && RegionsByKey.TryGetValue(match.Value, out region!))
        {
            return true;
        }

        region = string.Empty;
        return false;
    }

    public static bool ContainsForeignMarker(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        return ForeignPattern.IsMatch(value) || ForeignUpperPattern.IsMatch(value);
    }

    /// <summary>True when a token is location noise ("NZ", "Region", "CBD").</summary>
    public static bool IsNoiseToken(string token) => NoiseTokens.Contains(token);

    private static Regex BuildTokenPattern(IEnumerable<string> keys)
    {
        var ordered = keys
            .OrderByDescending(key => key.Length)
            .Select(Regex.Escape);

        return new Regex(
            $@"(?<![\p{{L}}\d])(?:{string.Join("|", ordered)})(?![\p{{L}}\d])",
            RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }
}
