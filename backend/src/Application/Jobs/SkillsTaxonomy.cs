using System.Text.Json;
using System.Text.RegularExpressions;

namespace Application.Jobs;

/// <summary>
/// The shared technical-skill taxonomy, loaded from
/// <c>crawler/skills_taxonomy.json</c>.
///
/// This is the .NET half of the single-source-of-truth contract described in
/// <c>crawler/skills_taxonomy.py</c>: the JSON data file is read by both the
/// Python scrapling worker and this loader, so the two sides can never drift
/// into different skill vocabularies. The file is copied into the API/worker
/// images by <c>COPY crawler ./crawler</c>.
/// </summary>
public sealed class SkillsTaxonomy
{
    private static readonly Lazy<SkillsTaxonomy> LazyDefault = new(LoadDefault);

    public SkillsTaxonomy(
        int maxSkillsPerJob,
        IReadOnlyList<SkillRule> rules,
        IReadOnlyDictionary<string, (string Name, string Type)> byName)
    {
        MaxSkillsPerJob = maxSkillsPerJob;
        Rules = rules;
        ByName = byName;
    }

    public int MaxSkillsPerJob { get; }

    public IReadOnlyList<SkillRule> Rules { get; }

    /// <summary>Canonical name/type lookup keyed by case-folded skill name.</summary>
    public IReadOnlyDictionary<string, (string Name, string Type)> ByName { get; }

    public static SkillsTaxonomy Default => LazyDefault.Value;

    public static string ResolveDefaultPath()
    {
        foreach (var start in new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory() })
        {
            var directory = new DirectoryInfo(start);
            while (directory is not null)
            {
                var candidate = Path.Combine(directory.FullName, "crawler", "skills_taxonomy.json");
                if (File.Exists(candidate))
                {
                    return candidate;
                }

                directory = directory.Parent;
            }
        }

        throw new FileNotFoundException(
            "Could not locate crawler/skills_taxonomy.json. It is the shared skill vocabulary read by "
            + "both the Python crawler and the .NET ingestion pipeline.");
    }

    public static SkillsTaxonomy LoadDefault() => Load(ResolveDefaultPath());

    public static SkillsTaxonomy Load(string path)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var root = document.RootElement;
        var maxSkills = root.TryGetProperty("maxSkillsPerJob", out var maxElement) ? maxElement.GetInt32() : 15;

        var rules = new List<SkillRule>();
        var byName = new Dictionary<string, (string Name, string Type)>(StringComparer.OrdinalIgnoreCase);
        var order = 0;

        foreach (var category in root.GetProperty("categories").EnumerateArray())
        {
            var skillType = category.GetProperty("type").GetString() ?? "Keyword";
            foreach (var skill in category.GetProperty("skills").EnumerateArray())
            {
                var name = skill.GetProperty("name").GetString()
                    ?? throw new InvalidDataException("A skill entry is missing its name.");
                var caseSensitiveDefault = skill.TryGetProperty("caseSensitive", out var cs) && cs.GetBoolean();

                var aliases = new List<SkillAliasRule>();
                if (skill.TryGetProperty("aliases", out var aliasElement) && aliasElement.ValueKind == JsonValueKind.Array)
                {
                    foreach (var alias in aliasElement.EnumerateArray())
                    {
                        if (alias.ValueKind == JsonValueKind.String)
                        {
                            aliases.Add(BuildAlias(alias.GetString()!, caseSensitiveDefault));
                        }
                        else if (alias.ValueKind == JsonValueKind.Object)
                        {
                            var text = alias.GetProperty("text").GetString()!;
                            var caseSensitive = alias.TryGetProperty("caseSensitive", out var aliasCs)
                                ? aliasCs.GetBoolean()
                                : caseSensitiveDefault;
                            aliases.Add(BuildAlias(text, caseSensitive));
                        }
                    }
                }

                if (aliases.Count == 0)
                {
                    aliases.Add(BuildAlias(name, caseSensitiveDefault));
                }

                rules.Add(new SkillRule(
                    Name: name,
                    Type: skillType,
                    Order: order,
                    Aliases: aliases,
                    Deny: BuildPatterns(skill, "deny"),
                    ContextAny: BuildPatterns(skill, "contextAny"),
                    ContextWindow: skill.TryGetProperty("contextWindow", out var window) ? window.GetInt32() : 0));
                byName[name] = (name, skillType);
                order++;
            }
        }

        return new SkillsTaxonomy(Math.Max(maxSkills, 1), rules, byName);
    }

    private static SkillAliasRule BuildAlias(string text, bool caseSensitive) => new(
        new Regex(
            $"(?<![A-Za-z0-9]){Regex.Escape(text)}(?![A-Za-z0-9])",
            caseSensitive ? RegexOptions.None : RegexOptions.IgnoreCase),
        caseSensitive);

    private static IReadOnlyList<Regex> BuildPatterns(JsonElement skill, string property)
    {
        if (!skill.TryGetProperty(property, out var element) || element.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return element.EnumerateArray()
            .Where(item => item.ValueKind == JsonValueKind.String)
            .Select(item => new Regex(item.GetString()!, RegexOptions.IgnoreCase))
            .ToList();
    }
}

public sealed record SkillAliasRule(Regex Pattern, bool CaseSensitive);

public sealed record SkillRule(
    string Name,
    string Type,
    int Order,
    IReadOnlyList<SkillAliasRule> Aliases,
    IReadOnlyList<Regex> Deny,
    IReadOnlyList<Regex> ContextAny,
    int ContextWindow);
