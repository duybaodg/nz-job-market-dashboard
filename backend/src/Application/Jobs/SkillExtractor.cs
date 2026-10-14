using System.Text.RegularExpressions;
using Application.Deduplication;

namespace Application.Jobs;

public sealed record ExtractedSkill(string Name, string Type);

/// <summary>
/// Applies the shared <see cref="SkillsTaxonomy"/> to free text: word-boundary
/// alias matching, per-skill false-positive guards (<c>deny</c>/<c>contextAny</c>)
/// and a per-job cap so a long description does not become keyword soup.
///
/// The Python worker runs the equivalent logic in
/// <c>crawler/skills_taxonomy.py</c>; both read the same JSON file.
/// </summary>
public sealed class SkillExtractor
{
    private static readonly Lazy<SkillExtractor> LazyDefault = new(() => new SkillExtractor(SkillsTaxonomy.Default));

    public SkillExtractor(SkillsTaxonomy taxonomy) => Taxonomy = taxonomy;

    public SkillsTaxonomy Taxonomy { get; }

    public static SkillExtractor Default => LazyDefault.Value;

    public IReadOnlyList<ExtractedSkill> Extract(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return [];
        }

        var scored = new List<(int Count, int Order, string Name, string Type)>();
        foreach (var rule in Taxonomy.Rules)
        {
            var count = Count(rule, text);
            if (count > 0)
            {
                scored.Add((count, rule.Order, rule.Name, rule.Type));
            }
        }

        return scored
            .OrderByDescending(item => item.Count)
            .ThenBy(item => item.Order)
            .Take(Taxonomy.MaxSkillsPerJob)
            .Select(item => new ExtractedSkill(item.Name, item.Type))
            .ToList();
    }

    public IReadOnlyList<ExtractedSkill> ExtractForJob(string? title, string? description) =>
        Extract(string.Join('\n', new[] { title, description }.Where(value => !string.IsNullOrWhiteSpace(value))));

    /// <summary>
    /// Canonicalises a caller-provided skill list (for example the <c>skills</c>
    /// array emitted by the Python worker) against the shared taxonomy.
    /// </summary>
    public IReadOnlyList<ExtractedSkill> NormaliseProvided(IEnumerable<RawJobSkill> skills)
    {
        var result = new List<ExtractedSkill>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var skill in skills)
        {
            var name = skill.Name?.Trim();
            if (string.IsNullOrEmpty(name) || !Taxonomy.ByName.TryGetValue(name, out var canonical))
            {
                continue;
            }

            if (seen.Add(canonical.Name))
            {
                result.Add(new ExtractedSkill(canonical.Name, canonical.Type));
            }

            if (result.Count >= Taxonomy.MaxSkillsPerJob)
            {
                break;
            }
        }

        return result;
    }

    private static int Count(SkillRule rule, string text)
    {
        var count = 0;
        foreach (var alias in rule.Aliases)
        {
            foreach (Match match in alias.Pattern.Matches(text))
            {
                if (rule.Deny.Count > 0 && IsDenied(text, match.Index + match.Length, rule.Deny))
                {
                    continue;
                }

                if (rule.ContextAny.Count > 0 && !InContext(text, match.Index, match.Index + match.Length, rule))
                {
                    continue;
                }

                count++;
            }
        }

        return count;
    }

    private static bool IsDenied(string text, int end, IReadOnlyList<Regex> deny)
    {
        var remainder = text[end..];
        foreach (var pattern in deny)
        {
            var match = pattern.Match(remainder);
            if (match.Success && match.Index == 0)
            {
                return true;
            }
        }

        return false;
    }

    private static bool InContext(string text, int start, int end, SkillRule rule)
    {
        var window = rule.ContextWindow > 0
            ? text[Math.Max(0, start - rule.ContextWindow)..Math.Min(text.Length, end + rule.ContextWindow)]
            : text;
        return rule.ContextAny.Any(pattern => pattern.IsMatch(window));
    }
}
