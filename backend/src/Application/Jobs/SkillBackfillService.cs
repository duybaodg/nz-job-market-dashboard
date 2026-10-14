using Application.Common;
using Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Application.Jobs;

public sealed record SkillBackfillResult(int JobsScanned, int JobsUpdated, int SkillsAdded, int SkillsRemoved);

/// <summary>
/// One-off, idempotent repair pass for rows written before the Round 7
/// taxonomy existed.
///
/// Every job is re-scanned with the shared <see cref="SkillExtractor"/> over
/// its full stored text: the title, the stored summary and — where the crawler
/// still retains it — the newest raw page markdown for the job URL. The job's
/// skill set is then replaced with the extracted set (deduplicated by canonical
/// name). The pass is idempotent: a second run derives the same set and reports
/// zero changes.
///
/// When no raw markdown is retained (for example a Workday listing-only source)
/// the pass unions the summary-derived skills with whatever is already stored
/// instead of deleting them, so a later run can never regress a richer set.
/// </summary>
public interface ISkillBackfillService
{
    Task<SkillBackfillResult> RunAsync(CancellationToken cancellationToken);
}

public sealed class SkillBackfillService(
    IApplicationDbContext dbContext,
    SkillExtractor skillExtractor,
    ILogger<SkillBackfillService>? logger = null) : ISkillBackfillService
{
    public async Task<SkillBackfillResult> RunAsync(CancellationToken cancellationToken)
    {
        var jobs = await dbContext.Jobs
            .Include(job => job.Skills)
            .ToListAsync(cancellationToken);

        var pages = await dbContext.RawJobPages
            .AsNoTracking()
            .Where(page => page.Markdown != null)
            .Select(page => new { page.Url, page.Markdown, page.CrawledAt })
            .ToListAsync(cancellationToken);

        var latestMarkdown = pages
            .Where(page => !string.IsNullOrWhiteSpace(page.Markdown))
            .GroupBy(page => page.Url, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group.OrderByDescending(page => page.CrawledAt).First().Markdown!,
                StringComparer.OrdinalIgnoreCase);

        var jobsUpdated = 0;
        var skillsAdded = 0;
        var skillsRemoved = 0;

        foreach (var job in jobs)
        {
            var hasFullText = latestMarkdown.TryGetValue(job.Url, out var markdown);
            var extracted = skillExtractor.ExtractForJob(
                job.Title,
                string.Join('\n', new[] { job.DescriptionSummary, hasFullText ? markdown : null }
                    .Where(value => !string.IsNullOrWhiteSpace(value))));

            var desired = extracted.ToDictionary(skill => skill.Name, skill => skill.Type, StringComparer.OrdinalIgnoreCase);
            var changed = false;

            foreach (var existing in job.Skills.ToList())
            {
                if (desired.TryGetValue(existing.SkillName, out var skillType))
                {
                    if (!string.Equals(existing.SkillType, skillType, StringComparison.Ordinal))
                    {
                        existing.SkillType = skillType;
                        changed = true;
                    }

                    desired.Remove(existing.SkillName);
                    continue;
                }

                // Without the full description we cannot prove a stored skill is
                // wrong, so keep it; with the full text, replace the set.
                if (hasFullText)
                {
                    job.Skills.Remove(existing);
                    dbContext.JobSkills.Remove(existing);
                    skillsRemoved++;
                    changed = true;
                }
            }

            foreach (var (name, skillType) in desired)
            {
                var jobSkill = new JobSkill
                {
                    Id = Guid.NewGuid(),
                    JobId = job.Id,
                    SkillName = name,
                    SkillType = skillType,
                    Confidence = 0.7m
                };
                job.Skills.Add(jobSkill);
                dbContext.JobSkills.Add(jobSkill);
                skillsAdded++;
                changed = true;
            }

            if (changed)
            {
                job.UpdatedAt = DateTime.UtcNow;
                jobsUpdated++;
            }
        }

        if (jobsUpdated > 0)
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }

        logger?.LogInformation(
            "Skill backfill: scanned {JobsScanned} jobs, updated {JobsUpdated}, added {SkillsAdded} skills, removed {SkillsRemoved}.",
            jobs.Count,
            jobsUpdated,
            skillsAdded,
            skillsRemoved);

        return new SkillBackfillResult(jobs.Count, jobsUpdated, skillsAdded, skillsRemoved);
    }
}
