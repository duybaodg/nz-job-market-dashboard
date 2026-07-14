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
        foreach (Match match in LinkRegex.Matches(markdown))
        {
            var title = Clean(match.Groups["title"].Value);
            var url = match.Groups["url"].Value.Trim();

            if (!LooksLikeJobTitle(title))
            {
                continue;
            }

            yield return new RawJobInput(
                source,
                null,
                title,
                null,
                ExtractLocation(markdown),
                ExtractSalary(markdown),
                ExtractNearbyDescription(markdown, title),
                url,
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
