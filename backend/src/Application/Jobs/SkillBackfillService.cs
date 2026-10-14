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
/// all stored text: the title, the stored summary and every distinct retained
/// raw-page markdown whose URL matches the job. The extraction is then unioned
/// with the existing skill set — canonical types are upgraded in place and
/// retired vocabulary entries are dropped — rather than replacing the set
/// outright, because the stored text is a weaker signal than the crawl-time
/// description. Replacing would silently lose real skills (observed: C#/ .NET
/// mentions that only ever existed in the crawl-time detail text), so the
/// backfill enriches instead of destroying data. Ingestion keeps the stricter
/// replace semantics when a fresh full description is available.
///
/// The pass is idempotent: a second run derives the same union and reports
/// zero changes.
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
    /// <summary>Keep at most this much distinct retained markdown per job.</summary>
    private const int MaxMarkdownsPerJob = 3;

    /// <summary>Cap on each retained markdown fragment fed to the extractor.</summary>
    private const int MaxMarkdownChars = 24_000;

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

        var markdownByUrl = pages
            .Where(page => !string.IsNullOrWhiteSpace(page.Markdown))
            .GroupBy(page => page.Url, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group
                    .OrderByDescending(page => page.CrawledAt)
                    .Select(page => page.Markdown!)
                    .Distinct(StringComparer.Ordinal)
                    .Take(MaxMarkdownsPerJob)
                    .ToList(),
                StringComparer.OrdinalIgnoreCase);

        var jobsUpdated = 0;
        var skillsAdded = 0;
        var skillsRemoved = 0;

        foreach (var job in jobs)
        {
            markdownByUrl.TryGetValue(job.Url, out var markdowns);
            var text = BuildText(job, markdowns);
            var desired = skillExtractor.Extract(text)
                .ToDictionary(skill => skill.Name, skill => skill.Type, StringComparer.OrdinalIgnoreCase);
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

                // Not re-derived from the stored text: keep it when it is still
                // a valid canonical skill (the stored text may simply be a
                // weaker signal), but drop retired vocabulary.
                if (!skillExtractor.Taxonomy.ByName.ContainsKey(existing.SkillName))
                {
                    job.Skills.Remove(existing);
                    dbContext.JobSkills.Remove(existing);
                    skillsRemoved++;
                    changed = true;
                }
            }

            foreach (var (name, skillType) in desired)
            {
                if (job.Skills.Count >= skillExtractor.Taxonomy.MaxSkillsPerJob)
                {
                    break;
                }

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

    private static string BuildText(Job job, IReadOnlyList<string>? markdowns)
    {
        var parts = new List<string> { job.Title };
        if (!string.IsNullOrWhiteSpace(job.DescriptionSummary))
        {
            parts.Add(job.DescriptionSummary);
        }

        if (markdowns is not null)
        {
            foreach (var markdown in markdowns)
            {
                parts.Add(markdown.Length > MaxMarkdownChars ? markdown[..MaxMarkdownChars] : markdown);
            }
        }

        return string.Join('\n', parts);
    }
}
