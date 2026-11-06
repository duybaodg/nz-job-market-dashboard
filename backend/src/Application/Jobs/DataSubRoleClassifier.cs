using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Application.Jobs;

/// <summary>
/// One versioned sub-role rule. Patterns are regexes applied to the retained
/// original title only; the classifier never reads the description and never
/// rewrites the title or the role family.
/// </summary>
public sealed record DataSubRoleRule(
    string Key,
    string Label,
    string Definition,
    IReadOnlyList<Regex> Patterns);

/// <summary>
/// The result of applying the versioned Data &amp; Analytics sub-role taxonomy.
/// A null <see cref="Key"/> with <see cref="Uncertain"/> true is an explicit
/// "not forced" state, not a failure to classify silently.
/// </summary>
public sealed record DataSubRoleMatch(
    string? Key,
    string? Label,
    bool Uncertain,
    IReadOnlyList<string> CandidateKeys,
    IReadOnlyList<string> CandidateLabels,
    string? UncertainReason,
    string TaxonomyVersion)
{
    public bool Applicable => Key is not null || Uncertain;
}

/// <summary>
/// Loads <c>Jobs/Data/data-subrole-taxonomy.json</c>, which is embedded in the
/// Application assembly. The data file is the versioned authority: the version
/// string travels with every API result and every sub-role-bearing job row.
/// </summary>
public sealed class DataSubRoleTaxonomy
{
    private static readonly Lazy<DataSubRoleTaxonomy> LazyDefault = new(LoadDefault);

    public DataSubRoleTaxonomy(
        string version,
        string effectiveDate,
        string family,
        string description,
        string scope,
        string uncertainLabel,
        string uncertainReasonMultiple,
        string uncertainReasonNone,
        IReadOnlyList<DataSubRoleRule> roles)
    {
        Version = version;
        EffectiveDate = effectiveDate;
        Family = family;
        Description = description;
        Scope = scope;
        UncertainLabel = uncertainLabel;
        UncertainReasonMultiple = uncertainReasonMultiple;
        UncertainReasonNone = uncertainReasonNone;
        Roles = roles;
    }

    public string Version { get; }

    public string EffectiveDate { get; }

    public string Family { get; }

    public string Description { get; }

    public string Scope { get; }

    public string UncertainLabel { get; }

    public string UncertainReasonMultiple { get; }

    public string UncertainReasonNone { get; }

    public IReadOnlyList<DataSubRoleRule> Roles { get; }

    public static DataSubRoleTaxonomy Default => LazyDefault.Value;

    public static DataSubRoleTaxonomy LoadDefault()
    {
        var assembly = typeof(DataSubRoleTaxonomy).Assembly;
        const string suffix = "Jobs.Data.data-subrole-taxonomy.json";
        var resourceName = assembly.GetManifestResourceNames()
            .SingleOrDefault(name => name.EndsWith(suffix, StringComparison.Ordinal))
            ?? throw new InvalidOperationException(
                "The embedded Data & Analytics sub-role taxonomy was not found in the Application assembly.");
        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Embedded taxonomy '{resourceName}' could not be opened.");
        using var reader = new StreamReader(stream);
        return Load(reader.ReadToEnd());
    }

    public static DataSubRoleTaxonomy Load(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var roles = new List<DataSubRoleRule>();

        foreach (var role in root.GetProperty("roles").EnumerateArray())
        {
            var key = RequireString(role, "key");
            var label = RequireString(role, "label");
            var definition = RequireString(role, "definition");
            var patterns = role.GetProperty("patterns").EnumerateArray()
                .Select(pattern => new Regex(
                    pattern.GetString() ?? throw new InvalidDataException($"A pattern for '{key}' is missing."),
                    RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant))
                .ToList();
            if (patterns.Count == 0)
            {
                throw new InvalidDataException($"Sub-role '{key}' has no patterns.");
            }

            roles.Add(new DataSubRoleRule(key, label, definition, patterns));
        }

        if (roles.Count == 0 || roles.Select(role => role.Key).Distinct(StringComparer.Ordinal).Count() != roles.Count)
        {
            throw new InvalidDataException("The sub-role taxonomy must contain at least one uniquely keyed role.");
        }

        return new DataSubRoleTaxonomy(
            RequireString(root, "version"),
            RequireString(root, "effectiveDate"),
            RequireString(root, "family"),
            RequireString(root, "description"),
            RequireString(root, "scope"),
            RequireString(root, "uncertainLabel"),
            RequireString(root, "uncertainReasonMultiple"),
            RequireString(root, "uncertainReasonNone"),
            roles);
    }

    private static string RequireString(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(value.GetString())
            ? value.GetString()!
            : throw new InvalidDataException($"Taxonomy property '{property}' is required.");
}

/// <summary>
/// Deterministic sub-role classification for the existing Data &amp; Analytics
/// family. Every match is evaluated; one match is labelled, two or more stay
/// uncertain with their candidate labels, and no match stays explicitly
/// unspecified. Nothing is guessed from the description.
/// </summary>
public static class DataSubRoleClassifier
{
    /// <summary>Special URL/filter token for the explicit uncertain bucket.</summary>
    public const string UncertainKey = "uncertain";

    public static DataSubRoleMatch Classify(
        string? title,
        string roleFamily,
        DataSubRoleTaxonomy? taxonomy = null)
    {
        var rules = taxonomy ?? DataSubRoleTaxonomy.Default;
        var value = (title ?? string.Empty).Trim();

        if (!string.Equals(roleFamily, rules.Family, StringComparison.OrdinalIgnoreCase))
        {
            return new DataSubRoleMatch(null, null, false, [], [], null, rules.Version);
        }

        if (value.Length == 0)
        {
            return new DataSubRoleMatch(
                null,
                null,
                true,
                [],
                [],
                rules.UncertainReasonNone,
                rules.Version);
        }

        var matches = rules.Roles
            .Where(role => role.Patterns.Any(pattern => pattern.IsMatch(value)))
            .ToList();

        if (matches.Count == 1)
        {
            return new DataSubRoleMatch(
                matches[0].Key,
                matches[0].Label,
                false,
                [],
                [],
                null,
                rules.Version);
        }

        if (matches.Count > 1)
        {
            return new DataSubRoleMatch(
                null,
                null,
                true,
                matches.Select(role => role.Key).ToList(),
                matches.Select(role => role.Label).ToList(),
                rules.UncertainReasonMultiple,
                rules.Version);
        }

        return new DataSubRoleMatch(
            null,
            null,
            true,
            [],
            [],
            rules.UncertainReasonNone,
            rules.Version);
    }

    public static DataSubRoleRule? RuleFor(string? key, DataSubRoleTaxonomy? taxonomy = null) =>
        key is null
            ? null
            : (taxonomy ?? DataSubRoleTaxonomy.Default).Roles
                .FirstOrDefault(rule => string.Equals(rule.Key, key, StringComparison.Ordinal));
}
