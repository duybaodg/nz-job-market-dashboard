using System.Text.RegularExpressions;
using Application.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Application.Jobs;

/// <summary>
/// Removes navigation/landing pages that generic extraction can mistake for jobs
/// ("Careers - BNZ", "3 jobs found for engineering"). The crawler filters these
/// at ingestion; this sweep repairs records created by older worker versions.
/// Mirrors the Python <c>HUB_TITLE_RE</c> in the crawler worker.
/// </summary>
public interface IJobQualityGuard
{
    Task<int> DeactivateLowQualityJobsAsync(CancellationToken cancellationToken);
}

public sealed class JobQualityGuard(
    IApplicationDbContext dbContext,
    ILogger<JobQualityGuard> logger) : IJobQualityGuard
{
    private static readonly Regex HubTitle = new(
        """
        (?ix)
        ^\s*(?:home|careers?|jobs?|job\s+search|search\s+results?|working\s+here|people\s+and\s+careers
            |register(?:\s+as)?|login|sign\s+in|find\s+a\s+job|current\s+vacancies|job\s+vacancies
            |job\s+listings?|vacancies)\b
        | \bcareers?\s+at\b
        | \bjob\s+search\b
        | \bjobs?\s+in\s+(?:new\s+zealand|nz)\b
        | \brecruitment\s+specialists?\b
        | \blaunch\s+your\b
        | \bjob\s?seek(?:er)?s?\b
        | \bregister\s+as\b
        | \bprogram\s+your\s+path\b
        | \b\d+\s+jobs?\s+found\b
        | \bjob\s+alerts?\b
        | \bjob\s+board\b
        | \bcurrent\s+opportunities\b
        | \bjob\s+opportunit\w*
        | \bgraduate\s+jobs?\b
        | \bopen\s+now\b
        | \bcareers?\s+home\b
        | \bjoin\s+(?:our|the)\s+team\b
        | \bcareers\b
        | \bopportunit\w*\s*$
        """,
        RegexOptions.Compiled | RegexOptions.IgnorePatternWhitespace | RegexOptions.IgnoreCase);

    public static bool IsLowQualityTitle(string? title, string? source)
    {
        var value = (title ?? string.Empty).Trim();
        if (value.Length < 4)
        {
            return true;
        }

        if (HubTitle.IsMatch(value))
        {
            return true;
        }

        return source is not null && string.Equals(value, source.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    public async Task<int> DeactivateLowQualityJobsAsync(CancellationToken cancellationToken)
    {
        var candidates = await dbContext.Jobs
            .AsNoTracking()
            .Where(job => job.IsActive)
            .Select(job => new { job.Id, job.Title, job.Source })
            .ToListAsync(cancellationToken);

        var junkIds = candidates
            .Where(job => IsLowQualityTitle(job.Title, job.Source))
            .Select(job => job.Id)
            .ToHashSet();

        if (junkIds.Count == 0)
        {
            return 0;
        }

        var entities = await dbContext.Jobs
            .Where(job => junkIds.Contains(job.Id))
            .ToListAsync(cancellationToken);

        var now = DateTime.UtcNow;
        foreach (var job in entities)
        {
            job.IsActive = false;
            job.ExpiredAt = now;
            job.UpdatedAt = now;
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        logger.LogInformation("Deactivated {Count} low-quality (hub/landing page) job records.", entities.Count);
        return entities.Count;
    }
}
