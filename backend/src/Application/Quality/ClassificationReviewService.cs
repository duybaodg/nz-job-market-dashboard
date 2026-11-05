using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Application.Common;
using Application.Jobs;
using Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace Application.Quality;

public interface IClassificationReviewService
{
    Task<ClassificationSampleDto> GetSampleAsync(int size, CancellationToken cancellationToken);
    Task<ClassificationReviewsReportDto> GetReviewsAsync(CancellationToken cancellationToken);
    Task<ClassificationLabelsResultDto> SaveLabelsAsync(
        RecordClassificationLabelsRequest request,
        CancellationToken cancellationToken);
}

/// <summary>
/// Release B2: deterministic stratified sampling plus an auditable review
/// workflow. Sampling reads active listings and groups them by source and
/// predicted role family; it never changes the classifier or the job rows.
/// </summary>
public sealed partial class ClassificationReviewService(
    IApplicationDbContext dbContext,
    TimeProvider? timeProvider = null) : IClassificationReviewService
{
    public const int DefaultSampleSize = 120;
    public const int MaxSampleSize = 250;
    private const int SpanTarget = 15;
    private const int SpanExtraCap = 20;

    private static readonly HashSet<string> Labels = new(StringComparer.Ordinal)
    {
        "correct", "false-positive", "false-negative", "ambiguous", "unknown"
    };

    private static readonly HashSet<string> SeniorityFlags = new(StringComparer.Ordinal)
    {
        "correct", "span", "ambiguous", "unknown"
    };

    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;

    public async Task<ClassificationSampleDto> GetSampleAsync(int size, CancellationToken cancellationToken)
    {
        var requested = Math.Clamp(size <= 0 ? DefaultSampleSize : size, 1, MaxSampleSize);
        var now = clock.GetUtcNow().UtcDateTime;
        var jobs = await dbContext.Jobs
            .AsNoTracking()
            .Where(job => job.IsActive)
            .Select(job => new
            {
                job.Id,
                job.Source,
                job.Title,
                job.Company,
                job.Seniority,
                job.DescriptionSummary,
                job.DescriptionText
            })
            .ToListAsync(cancellationToken);

        var rows = jobs.Select(job =>
        {
            var levels = SeniorityExtractor.DetectTitleLevels(job.Title);
            return new SampleJob(
                job.Id,
                job.Source,
                job.Title,
                job.Company,
                Excerpt(string.IsNullOrWhiteSpace(job.DescriptionText) ? job.DescriptionSummary : job.DescriptionText),
                RoleFamilyClassifier.Classify(job.Title).Family,
                string.IsNullOrWhiteSpace(job.Seniority) ? "Unknown" : job.Seniority,
                levels,
                levels.Count > 1,
                StableKey(job.Id));
        }).ToList();

        var selected = Select(rows, requested);
        var selectedIds = selected.Select(job => job.Id).ToList();
        var existingLabels = await dbContext.ClassificationReviews
            .AsNoTracking()
            .Where(review => selectedIds.Contains(review.JobId)
                && review.RuleVersion == RoleFamilyClassifier.RuleVersion)
            .Select(review => new { review.JobId, review.Label })
            .ToDictionaryAsync(review => review.JobId, review => review.Label, cancellationToken);

        var items = selected.Select(job => new ClassificationSampleItemDto(
            job.Id,
            job.Source,
            job.Title,
            job.Company,
            job.Evidence,
            job.PredictedRoleFamily,
            job.PredictedSeniority,
            job.TitleLevels,
            job.SpansSeniority,
            RoleFamilyClassifier.RuleVersion,
            SeniorityExtractor.RuleVersion,
            existingLabels.GetValueOrDefault(job.Id))).ToList();

        return new ClassificationSampleDto(
            now,
            requested,
            items.Count,
            jobs.Count,
            rows.Select(row => row.Source).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
            rows.Select(row => row.PredictedRoleFamily).Distinct(StringComparer.Ordinal).Count(),
            "Deterministic MD5/SHA-256 ordering within each source × predicted-role-family stratum; one guaranteed listing per source, then round-robin across strata. Seniority-span titles are added (up to 15) beyond the requested size when available.",
            items);
    }

    public async Task<ClassificationReviewsReportDto> GetReviewsAsync(CancellationToken cancellationToken)
    {
        var activeListings = await dbContext.Jobs.CountAsync(job => job.IsActive, cancellationToken);
        var reviews = await dbContext.ClassificationReviews
            .AsNoTracking()
            .OrderByDescending(review => review.ReviewedAt)
            .ToListAsync(cancellationToken);
        var counts = Labels.ToDictionary(label => label, _ => 0, StringComparer.Ordinal);
        foreach (var group in reviews.GroupBy(review => review.Label))
        {
            counts[group.Key] = group.Count();
        }

        return new ClassificationReviewsReportDto(
            clock.GetUtcNow().UtcDateTime,
            activeListings,
            reviews.Count,
            counts,
            reviews.Count(review => review.SeniorityFlag == "span"),
            reviews.Take(500).Select(ToDto).ToList());
    }

    public async Task<ClassificationLabelsResultDto> SaveLabelsAsync(
        RecordClassificationLabelsRequest request,
        CancellationToken cancellationToken)
    {
        if (request.Labels is null || request.Labels.Count == 0)
        {
            throw new ArgumentException("At least one label is required.", nameof(request));
        }

        var ids = request.Labels.Select(label => label.JobId).Distinct().ToList();
        var jobs = await dbContext.Jobs
            .AsNoTracking()
            .Where(job => ids.Contains(job.Id))
            .Select(job => new { job.Id, job.Source, job.Title, job.Seniority })
            .ToDictionaryAsync(job => job.Id, cancellationToken);
        var missing = ids.Where(id => !jobs.ContainsKey(id)).ToList();
        if (missing.Count > 0)
        {
            throw new ArgumentException(
                $"The following job ids are not present in the retained listings: {string.Join(", ", missing)}.",
                nameof(request));
        }

        var now = clock.GetUtcNow().UtcDateTime;
        var reviewer = string.IsNullOrWhiteSpace(request.Reviewer) ? "admin" : request.Reviewer.Trim();
        if (reviewer.Length > 80)
        {
            reviewer = reviewer[..80];
        }

        var existing = await dbContext.ClassificationReviews
            .Where(review => ids.Contains(review.JobId)
                && review.RuleVersion == RoleFamilyClassifier.RuleVersion)
            .ToDictionaryAsync(review => review.JobId, cancellationToken);

        var created = 0;
        var updated = 0;
        foreach (var input in request.Labels)
        {
            var label = Normalise(input.Label);
            if (!Labels.Contains(label))
            {
                throw new ArgumentException(
                    $"Label '{input.Label}' is not one of: {string.Join(", ", Labels)}.",
                    nameof(request));
            }

            var job = jobs[input.JobId];
            var predictedFamily = RoleFamilyClassifier.Classify(job.Title).Family;
            var levels = SeniorityExtractor.DetectTitleLevels(job.Title);
            var reviewedFamily = string.IsNullOrWhiteSpace(input.ReviewedRoleFamily)
                ? label == "correct" ? predictedFamily : null
                : input.ReviewedRoleFamily.Trim();
            if (reviewedFamily is null && label is "false-positive" or "false-negative")
            {
                throw new ArgumentException(
                    $"A reviewed role family is required for label '{label}' (job {input.JobId}).",
                    nameof(request));
            }

            var seniorityFlag = string.IsNullOrWhiteSpace(input.SeniorityFlag)
                ? levels.Count > 1 ? "span" : "correct"
                : Normalise(input.SeniorityFlag);
            if (!SeniorityFlags.Contains(seniorityFlag))
            {
                throw new ArgumentException(
                    $"Seniority flag '{input.SeniorityFlag}' is not one of: {string.Join(", ", SeniorityFlags)}.",
                    nameof(request));
            }

            if (existing.TryGetValue(input.JobId, out var review))
            {
                updated++;
            }
            else
            {
                review = new ClassificationReview
                {
                    Id = Guid.NewGuid(),
                    JobId = input.JobId,
                    RuleVersion = RoleFamilyClassifier.RuleVersion,
                    CreatedAt = now
                };
                dbContext.ClassificationReviews.Add(review);
                created++;
            }

            review.Source = job.Source;
            review.Title = job.Title;
            review.PredictedRoleFamily = predictedFamily;
            review.Label = label;
            review.ReviewedRoleFamily = reviewedFamily;
            review.PredictedSeniority = string.IsNullOrWhiteSpace(job.Seniority) ? "Unknown" : job.Seniority;
            review.ReviewedSeniority = string.IsNullOrWhiteSpace(input.ReviewedSeniority)
                ? levels.Count > 1 ? string.Join(" / ", levels) : null
                : input.ReviewedSeniority.Trim();
            review.SeniorityFlag = seniorityFlag;
            review.SeniorityNote = Clean(input.SeniorityNote, 500);
            review.Notes = Clean(input.Notes, 1000);
            review.Reviewer = reviewer;
            review.ReviewedAt = now;
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        return new ClassificationLabelsResultDto(
            request.Labels.Count,
            created,
            updated,
            await GetReviewsAsync(cancellationToken));
    }

    private static List<SampleJob> Select(IReadOnlyList<SampleJob> rows, int size)
    {
        var selected = new List<SampleJob>();
        var chosen = new HashSet<Guid>();
        var strata = rows
            .GroupBy(row => (row.Source, row.PredictedRoleFamily))
            .Select(group => new Stratum(
                group.Key.Source,
                group.Key.PredictedRoleFamily,
                group.OrderBy(row => row.StableKey, StringComparer.Ordinal).ToList()))
            .OrderByDescending(stratum => stratum.Items.Count)
            .ThenBy(stratum => stratum.Source, StringComparer.OrdinalIgnoreCase)
            .ThenBy(stratum => stratum.RoleFamily, StringComparer.Ordinal)
            .ToList();

        // Guarantee one listing per source, chosen from its largest stratum.
        foreach (var sourceGroup in strata.GroupBy(stratum => stratum.Source, StringComparer.OrdinalIgnoreCase)
                     .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase))
        {
            var largest = sourceGroup
                .OrderByDescending(stratum => stratum.Items.Count)
                .ThenBy(stratum => stratum.RoleFamily, StringComparer.Ordinal)
                .First();
            if (largest.TryTake(chosen, out var item))
            {
                selected.Add(item);
            }
        }

        while (selected.Count < size)
        {
            var progressed = false;
            foreach (var stratum in strata)
            {
                if (selected.Count >= size)
                {
                    break;
                }

                if (stratum.TryTake(chosen, out var item))
                {
                    selected.Add(item);
                    progressed = true;
                }
            }

            if (!progressed)
            {
                break;
            }
        }

        // Seniority spans are rare, so add them explicitly after the main fill.
        var spanTarget = Math.Min(SpanTarget, rows.Count(row => row.SpansSeniority));
        if (selected.Count(row => row.SpansSeniority) < spanTarget)
        {
            foreach (var span in rows.Where(row => row.SpansSeniority)
                         .OrderBy(row => row.StableKey, StringComparer.Ordinal))
            {
                if (chosen.Add(span.Id))
                {
                    selected.Add(span);
                }

                if (selected.Count(row => row.SpansSeniority) >= spanTarget
                    || selected.Count >= size + SpanExtraCap)
                {
                    break;
                }
            }
        }

        return selected;
    }

    private static ClassificationReviewDto ToDto(ClassificationReview review) =>
        new(
            review.JobId,
            review.Source,
            review.Title,
            review.PredictedRoleFamily,
            review.ReviewedRoleFamily ?? string.Empty,
            review.Label,
            review.PredictedSeniority,
            review.ReviewedSeniority ?? string.Empty,
            review.SeniorityFlag,
            review.SeniorityNote,
            review.Notes,
            review.Reviewer,
            review.RuleVersion,
            review.ReviewedAt);

    private static string Normalise(string? value) => (value ?? string.Empty).Trim().ToLowerInvariant();

    private static string? Clean(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = Whitespace().Replace(value.Trim(), " ");
        return trimmed.Length > maxLength ? trimmed[..maxLength] : trimmed;
    }

    private static string Excerpt(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var text = Whitespace().Replace(value.Trim(), " ");
        return text.Length > 900 ? text[..900] : text;
    }

    private static string StableKey(Guid id) =>
        Convert.ToHexString(SHA256.HashData(id.ToByteArray()));

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
    private static partial Regex Whitespace();

    private sealed record SampleJob(
        Guid Id,
        string Source,
        string Title,
        string? Company,
        string Evidence,
        string PredictedRoleFamily,
        string PredictedSeniority,
        IReadOnlyList<string> TitleLevels,
        bool SpansSeniority,
        string StableKey);

    private sealed class Stratum(string source, string roleFamily, List<SampleJob> items)
    {
        private int pointer;

        public string Source { get; } = source;
        public string RoleFamily { get; } = roleFamily;
        public List<SampleJob> Items { get; } = items;

        public bool TryTake(HashSet<Guid> chosen, out SampleJob item)
        {
            while (pointer < Items.Count)
            {
                var candidate = Items[pointer++];
                if (!chosen.Add(candidate.Id))
                {
                    continue;
                }

                item = candidate;
                return true;
            }

            item = null!;
            return false;
        }
    }
}
