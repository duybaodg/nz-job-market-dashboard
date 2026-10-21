using System.Text.RegularExpressions;
using Application.Deduplication;

namespace Infrastructure.Firecrawl;

public interface IFirecrawlMarkdownParser
{
    IReadOnlyList<RawJobInput> Parse(string source, string sourceUrl, string markdown);
}

public sealed class FirecrawlMarkdownParser : IFirecrawlMarkdownParser
{
    private static readonly Regex LinkRegex = new(@"\[(?<title>[^\]]+)\]\((?<url>https?://[^)]+)\)", RegexOptions.Compiled);
    private static readonly Regex SalaryRegex = new(@"(?<salary>\$?\s*\d{2,3}(?:,\d{3}|k)?\s*(?:-|to|–)\s*\$?\s*\d{2,3}(?:,\d{3}|k)?)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public IReadOnlyList<RawJobInput> Parse(string source, string sourceUrl, string markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown))
        {
            return [];
        }

        var linkJobs = ParseLinkedJobs(source, sourceUrl, markdown).ToList();
        if (linkJobs.Count > 0)
        {
            return linkJobs;
        }

        return [ParseSinglePageJob(source, sourceUrl, markdown)];
    }

    private static IEnumerable<RawJobInput> ParseLinkedJobs(string source, string sourceUrl, string markdown)
    {
        // Round 15 (M-10): page-level labels belong to one card only when the
        // page is a single-job page. On a multi-card listing each card is parsed
        // from its own block, so the first card's salary/location cannot leak to
        // every later card.
        var links = LinkRegex.Matches(markdown)
            .Cast<Match>()
            .Select(match => new
            {
                Match = match,
                Title = Clean(match.Groups["title"].Value),
                Url = match.Groups["url"].Value.Trim()
            })
            .Where(link => LooksLikeJobTitle(link.Title))
            .ToList();

        for (var index = 0; index < links.Count; index++)
        {
            var link = links[index];
            var blockEnd = index + 1 < links.Count ? links[index + 1].Match.Index : markdown.Length;
            var block = markdown.Substring(link.Match.Index, Math.Max(0, blockEnd - link.Match.Index));
            var singleJobPage = links.Count == 1;

            yield return new RawJobInput(
                source,
                null,
                link.Title,
                ExtractCompany(block) ?? (singleJobPage ? ExtractCompany(markdown) : null),
                ExtractLocation(block) ?? (singleJobPage ? ExtractLocation(markdown) : null),
                ExtractSalary(block) ?? (singleJobPage ? ExtractSalary(markdown) : null),
                ExtractNearbyDescription(block, link.Title) ?? (singleJobPage ? ExtractNearbyDescription(markdown, link.Title) : null),
                link.Url,
                null,
                null);
        }
    }

    private static RawJobInput ParseSinglePageJob(string source, string sourceUrl, string markdown)
    {
        var title = markdown
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Trim().TrimStart('#').Trim())
            .FirstOrDefault(line => line.Length > 0) ?? "Untitled job";

        return new RawJobInput(
            source,
            null,
            Clean(title),
            ExtractCompany(markdown),
            ExtractLocation(markdown),
            ExtractSalary(markdown),
            markdown,
            sourceUrl,
            null,
            null);
    }

    private static bool LooksLikeJobTitle(string title)
    {
        var value = title.ToLowerInvariant();
        var keywords = new[] { "developer", "engineer", "analyst", "architect", "consultant", "administrator", "manager", "designer", "specialist", "graduate", "intern" };
        return keywords.Any(value.Contains);
    }

    private static string? ExtractNearbyDescription(string markdown, string title)
    {
        var index = markdown.IndexOf(title, StringComparison.OrdinalIgnoreCase);
        if (index < 0)
        {
            return null;
        }

        var length = Math.Min(600, markdown.Length - index);
        return Clean(markdown.Substring(index, length));
    }

    private static string? ExtractCompany(string markdown)
    {
        return ExtractLabel(markdown, "Company") ?? ExtractLabel(markdown, "Employer");
    }

    private static string? ExtractLocation(string markdown)
    {
        return ExtractLabel(markdown, "Location") ?? ExtractLabel(markdown, "Region");
    }

    private static string? ExtractSalary(string markdown)
    {
        var labelled = ExtractLabel(markdown, "Salary");
        if (!string.IsNullOrWhiteSpace(labelled))
        {
            return labelled;
        }

        var match = SalaryRegex.Match(markdown);
        return match.Success ? Clean(match.Groups["salary"].Value) : null;
    }

    private static string? ExtractLabel(string markdown, string label)
    {
        var match = Regex.Match(markdown, $@"(?:\*\*)?{label}(?:\*\*)?\s*:\s*(?<value>[^\n\r]+)", RegexOptions.IgnoreCase);
        return match.Success ? Clean(match.Groups["value"].Value) : null;
    }

    private static string Clean(string value)
    {
        return Regex.Replace(value.Trim(), @"\s+", " ").Trim(' ', '-', '*', '#');
    }
}
