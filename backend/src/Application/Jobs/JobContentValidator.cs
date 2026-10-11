using System.Text.RegularExpressions;
using Application.Deduplication;

namespace Application.Jobs;

/// <summary>
/// Extra context that the orchestration layer can supply when a candidate is
/// validated. <see cref="CrawlUrl"/> is the listing/root URL that produced the
/// candidate (hub detection) and <see cref="PageTitle"/> is the fetched page's
/// HTML &lt;title&gt; when available (error-page detection).
/// </summary>
public sealed record JobValidationContext(
    string? CrawlUrl = null,
    string? PageTitle = null,
    string? PageHtml = null);

public sealed record JobValidationResult(bool IsAccepted, string? RejectionReason)
{
    public static readonly JobValidationResult Accepted = new(true, null);

    public static JobValidationResult Reject(string reason) => new(false, reason);
}

public interface IJobContentValidator
{
    JobValidationResult Validate(RawJobInput job, JobValidationContext? context = null);
}

/// <summary>
/// Shared ingestion gate that rejects error/404 pages, articles/advice content
/// and hub/landing pages before a candidate job is persisted. Only strong
/// signals are used: a real job that merely lacks a field still saves.
/// Mirrored by the Python validator in
/// <c>crawler/adapters/scrapling/scrapling_worker.py</c>.
/// </summary>
public sealed class JobContentValidator : IJobContentValidator
{
    private static readonly Regex ErrorPagePattern = new(
        """
        (?ix)
        \b404\b
        | \berror\s*\(\s*404\s*\)
        | \bpage\s+not\s+found\b
        | \bpage\s+cannot\s+be\s+found\b
        | \bpage\s+not\s+available\b
        | \bpage\s+unavailable\b
        | \bpage\s+does\s+not\s+exist\b
        | \bnot\s+found\b
        | \bsomething\s+went\s+wrong\b
        | \binternal\s+server\s+error\b
        | \bthe\s+page\s+you\s+are\s+looking\s+for\b
        """,
        RegexOptions.Compiled | RegexOptions.IgnorePatternWhitespace | RegexOptions.IgnoreCase);

    private static readonly Regex ArticlePathPattern = new(
        @"(?i)/(?:advice|article|articles|blog|blogs|guides?)(?:/|$)",
        RegexOptions.Compiled);

    private static readonly Regex ArticleTitlePattern = new(
        """
        (?ix)
        ^\s*(?:how\s+to\s+|why\s+\w|what\s+\w|when\s+\w
              |top\s+\d+\b
              |\d+\s+(?:good|best|great|amazing|reasons?|tips?|ways?|things?)\b)
        """,
        RegexOptions.Compiled | RegexOptions.IgnorePatternWhitespace | RegexOptions.IgnoreCase);

    private static readonly Regex JobSignalPattern = new(
        @"(?i)\b(?:full[-\s]?time|part[-\s]?time|permanent|fixed[-\s]?term|contract|casual|internship|apply\s+now|salary|per\s+annum)\b",
        RegexOptions.Compiled);

    // Unambiguous navigation/listing titles. Rejected even when a salary or
    // date was misparsed from the page.
    private static readonly Regex HubStrongPattern = new(
        """
        (?ix)
        \bpeople\s+and\s+careers\b
        | \brecruitment\s+and\s+executive\s+search\b
        | \bempowering\s+new\s+zealand\s+to\s+hire\b
        | \blaunch\s+your\b
        | \bprogram\s+your\s+path\b
        | \bjob\s+board\b
        | \bcareers?\s+home\b
        | \bjoin\s+(?:our|the)\s+team\b
        | \b\d+\s+jobs?\s+found\b
        | ^\s*(?:home|careers?|jobs?|job\s+search|working\s+here|find\s+a\s+job
              |search\s+results?|current\s+vacancies|job\s+vacancies|job\s+listings?
              |vacancies)\s*$
        | ^\s*(?:home|careers?|jobs?|job\s+search)\s*[-|–—:]\s*
        """,
        RegexOptions.Compiled | RegexOptions.IgnorePatternWhitespace | RegexOptions.IgnoreCase);

    // Weaker navigation phrasing that could also appear in a real title
    // ("Executive Search Consultant"), so only trusted with no date/salary.
    private static readonly Regex HubWeakPattern = new(
        """
        (?ix)
        \bjob\s+search\b
        | \bsearch\s+results?\b
        | \bexecutive\s+search\b
        | \brecruitment\s+specialists?\b
        """,
        RegexOptions.Compiled | RegexOptions.IgnorePatternWhitespace | RegexOptions.IgnoreCase);

    // Employer-profile boilerplate ("10 opportunities New Zealand").
    private static readonly Regex HubDescriptionPattern = new(
        @"(?ix)\b\d+\s+opportunit(?:y|ies)\b",
        RegexOptions.Compiled | RegexOptions.IgnorePatternWhitespace | RegexOptions.IgnoreCase);

    public JobValidationResult Validate(RawJobInput job, JobValidationContext? context = null)
    {
        ArgumentNullException.ThrowIfNull(job);
        context ??= new JobValidationContext();

        var title = (job.Title ?? string.Empty).Trim();
        var description = job.DescriptionText ?? string.Empty;

        if (title.Length < 3)
        {
            return JobValidationResult.Reject("empty or truncated title");
        }

        // 1) Error / 404 pages: a title or fetched-page <title> match is a strong
        //    signal; a description match is only trusted when the description is a
        //    short error blurb rather than a long job advert.
        if (ErrorPagePattern.IsMatch(title))
        {
            return JobValidationResult.Reject("error/404 page title");
        }

        if (!string.IsNullOrWhiteSpace(context.PageTitle) && ErrorPagePattern.IsMatch(context.PageTitle))
        {
            return JobValidationResult.Reject("error/404 page <title>");
        }

        if (description.Length is > 0 and <= 400 && ErrorPagePattern.IsMatch(description))
        {
            return JobValidationResult.Reject("error/404 page description");
        }

        // 2) Articles / advice content rather than a posting.
        if (ArticlePathPattern.IsMatch(GetPath(job.Url)))
        {
            return JobValidationResult.Reject("article/advice url");
        }

        if (ArticleTitlePattern.IsMatch(title) && !HasJobSignals(job))
        {
            return JobValidationResult.Reject("article title without job signals");
        }

        // 3) Hub / landing pages. Strong navigation/listing titles are rejected
        //    even when a salary or date was misparsed from the page; weaker
        //    signals require no posted date and no salary so a real posting that
        //    merely lacks one field still saves.
        if (HubStrongPattern.IsMatch(title))
        {
            return JobValidationResult.Reject("hub/landing page title");
        }

        if (job.PostedDate is null)
        {
            if (HubWeakPattern.IsMatch(title))
            {
                return JobValidationResult.Reject("hub/landing page title");
            }

            if (string.Equals(title, (job.Source ?? string.Empty).Trim(), StringComparison.OrdinalIgnoreCase))
            {
                return JobValidationResult.Reject("title equals source name");
            }

            if (string.IsNullOrWhiteSpace(job.SalaryText) &&
                SamePage(job.Url, context.CrawlUrl) &&
                string.IsNullOrWhiteSpace(job.Location))
            {
                return JobValidationResult.Reject("hub/landing page url");
            }

            if (string.IsNullOrWhiteSpace(job.SalaryText) &&
                string.IsNullOrWhiteSpace(job.Location) &&
                HubDescriptionPattern.IsMatch(description))
            {
                return JobValidationResult.Reject("hub/landing page description");
            }
        }

        return JobValidationResult.Accepted;
    }

    private static bool HasJobSignals(RawJobInput job)
    {
        if (!string.IsNullOrWhiteSpace(job.SalaryText)) return true;
        if (!string.IsNullOrWhiteSpace(job.Location)) return true;
        if (job.PostedDate is not null) return true;
        if (!string.IsNullOrWhiteSpace(job.SourceJobId)) return true;
        return JobSignalPattern.IsMatch(job.DescriptionText ?? string.Empty);
    }

    private static string GetPath(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return string.Empty;
        }

        return Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.AbsolutePath : url;
    }

    private static bool SamePage(string? left, string? right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
        {
            return false;
        }

        if (!Uri.TryCreate(left, UriKind.Absolute, out var leftUri) ||
            !Uri.TryCreate(right, UriKind.Absolute, out var rightUri))
        {
            return string.Equals(left.TrimEnd('/'), right.TrimEnd('/'), StringComparison.OrdinalIgnoreCase);
        }

        return string.Equals(leftUri.Host, rightUri.Host, StringComparison.OrdinalIgnoreCase) &&
               string.Equals(NormalisePath(leftUri.AbsolutePath), NormalisePath(rightUri.AbsolutePath), StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalisePath(string path)
    {
        var trimmed = (path ?? string.Empty).TrimEnd('/');
        return trimmed.Length == 0 ? "/" : trimmed;
    }
}
