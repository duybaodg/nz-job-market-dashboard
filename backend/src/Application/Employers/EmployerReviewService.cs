using Application.Common;
using Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace Application.Employers;

public interface IEmployerReviewService
{
    Task<EmployerReviewQueueDto> GetQueueAsync(CancellationToken cancellationToken);
    Task<EmployerReviewResultDto> SaveDecisionsAsync(
        RecordEmployerReviewDecisionsRequest request,
        CancellationToken cancellationToken);
}

/// <summary>
/// Release B3: groups unmatched advertised names for human review and records
/// explicit decisions. Grouping is a display aid only; no code path creates a
/// canonical employer, alias or merge from a similar-looking name. A match
/// happens only when an admin supplies the canonical employer id, and even then
/// the advertised string is preserved in the decision and the alias row.
/// </summary>
public sealed class EmployerReviewService(
    IApplicationDbContext dbContext,
    IEmployerIdentityService identityService,
    TimeProvider? timeProvider = null) : IEmployerReviewService
{
    private static readonly HashSet<string> Decisions = new(StringComparer.Ordinal)
    {
        "matched", "kept-separate", "agency-client"
    };

    private static readonly string[] LegalSuffixes =
    [
        "limited", "ltd", "group", "nz", "new zealand", "inc", "plc", "corp",
        "corporation", "holdings", "services", "solutions", "technologies",
        "technology", "systems", "recruitment", "consulting", "consultancy", "partners"
    ];

    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;

    public async Task<EmployerReviewQueueDto> GetQueueAsync(CancellationToken cancellationToken)
    {
        var report = await identityService.GetReportAsync(cancellationToken);
        var decisions = await dbContext.EmployerAliasReviews
            .AsNoTracking()
            .OrderByDescending(review => review.ReviewedAt)
            .ToListAsync(cancellationToken);
        var decisionByName = decisions
            .GroupBy(review => review.NormalizedName, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        var employerNames = report.Employers.ToDictionary(
            employer => employer.Id,
            employer => employer.CanonicalName);

        var rows = report.UnmatchedNames
            .Select(unmatched =>
            {
                var normalized = EmployerNameNormaliser.Normalise(unmatched.Name);
                decisionByName.TryGetValue(normalized, out var decision);
                return new QueueRow(
                    normalized,
                    unmatched.Name,
                    unmatched.JobCount,
                    GroupKey(normalized),
                    decision,
                    decision?.CanonicalEmployerId is { } id ? employerNames.GetValueOrDefault(id) : null);
            })
            .ToList();

        var groups = rows
            .GroupBy(row => row.GroupKey, StringComparer.Ordinal)
            .Select(group => new EmployerReviewGroupDto(
                group.Key,
                group.Sum(row => row.JobCount),
                group
                    .OrderByDescending(row => row.JobCount)
                    .ThenBy(row => row.AdvertisedName, StringComparer.OrdinalIgnoreCase)
                    .Select(row => new EmployerReviewQueueItemDto(
                        row.NormalizedName,
                        row.AdvertisedName,
                        row.JobCount,
                        row.Decision?.Decision,
                        row.Decision?.CanonicalEmployerId,
                        row.CanonicalName,
                        row.Decision?.Reason,
                        row.Decision?.Reviewer,
                        row.Decision?.ReviewedAt))
                    .ToList()))
            .OrderByDescending(group => group.TotalListings)
            .ThenBy(group => group.GroupKey, StringComparer.Ordinal)
            .ToList();

        var unmatchedNormals = rows.Select(row => row.NormalizedName).ToHashSet(StringComparer.Ordinal);
        var decided = decisions.Count(review => unmatchedNormals.Contains(review.NormalizedName));
        var recent = decisions.Take(200).Select(review => new EmployerReviewDecisionDto(
            review.NormalizedName,
            review.AdvertisedName,
            review.Decision,
            review.CanonicalEmployerId,
            review.CanonicalEmployerId is { } id ? employerNames.GetValueOrDefault(id) : null,
            review.GroupKey,
            review.Reason,
            review.Reviewer,
            review.ReviewedAt)).ToList();

        return new EmployerReviewQueueDto(
            clock.GetUtcNow().UtcDateTime,
            rows.Count,
            decisions.Count,
            decided,
            rows.Count - decided,
            rows.Sum(row => row.JobCount),
            "Similar names are grouped only for review by stripping trailing legal suffixes and punctuation. Grouping never changes identity; each advertised name is decided separately.",
            groups,
            recent);
    }

    public async Task<EmployerReviewResultDto> SaveDecisionsAsync(
        RecordEmployerReviewDecisionsRequest request,
        CancellationToken cancellationToken)
    {
        if (request.Decisions is null || request.Decisions.Count == 0)
        {
            throw new ArgumentException("At least one decision is required.", nameof(request));
        }

        var now = clock.GetUtcNow().UtcDateTime;
        var reviewer = string.IsNullOrWhiteSpace(request.Reviewer) ? "admin" : request.Reviewer.Trim();
        if (reviewer.Length > 80)
        {
            reviewer = reviewer[..80];
        }

        var employers = await dbContext.Employers
            .AsNoTracking()
            .Select(employer => new { employer.Id, employer.CanonicalName, employer.NormalizedName })
            .ToListAsync(cancellationToken);
        var employersById = employers.ToDictionary(employer => employer.Id);
        var aliases = await dbContext.EmployerAliases
            .AsNoTracking()
            .Select(alias => new { alias.EmployerId, alias.NormalizedAlias })
            .ToListAsync(cancellationToken);
        var aliasOwner = aliases
            .GroupBy(alias => alias.NormalizedAlias, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First().EmployerId, StringComparer.Ordinal);

        var existing = await dbContext.EmployerAliasReviews
            .ToDictionaryAsync(review => review.NormalizedName, StringComparer.Ordinal, cancellationToken);
        var saved = 0;
        var created = 0;
        var updated = 0;
        var aliasesCreated = 0;

        foreach (var input in request.Decisions)
        {
            var advertisedName = (input.AdvertisedName ?? string.Empty).Trim();
            var normalized = EmployerNameNormaliser.Normalise(advertisedName);
            if (normalized.Length == 0)
            {
                throw new ArgumentException("An advertised name is required for every decision.", nameof(request));
            }

            var decision = (input.Decision ?? string.Empty).Trim().ToLowerInvariant();
            if (!Decisions.Contains(decision))
            {
                throw new ArgumentException(
                    $"Decision '{input.Decision}' is not one of: {string.Join(", ", Decisions)}.",
                    nameof(request));
            }

            Guid? canonicalId = null;
            if (decision == "matched")
            {
                if (input.CanonicalEmployerId is not { } id || !employersById.ContainsKey(id))
                {
                    throw new ArgumentException(
                        $"A decision of 'matched' requires an existing canonical employer id (name '{advertisedName}').",
                        nameof(request));
                }

                canonicalId = id;
                var canonical = employersById[id];
                if (aliasOwner.TryGetValue(normalized, out var owner) && owner != id)
                {
                    throw new ArgumentException(
                        $"The advertised name '{advertisedName}' is already an alias of another canonical employer.",
                        nameof(request));
                }

                var canonicalClash = employers.FirstOrDefault(employer =>
                    employer.NormalizedName == normalized && employer.Id != id);
                if (canonicalClash is not null)
                {
                    throw new ArgumentException(
                        $"The advertised name '{advertisedName}' is already a canonical name of another employer.",
                        nameof(request));
                }

                if (canonical.NormalizedName != normalized && !aliasOwner.ContainsKey(normalized))
                {
                    dbContext.EmployerAliases.Add(new EmployerAlias
                    {
                        Id = Guid.NewGuid(),
                        EmployerId = id,
                        Alias = advertisedName,
                        NormalizedAlias = normalized,
                        CreatedAt = now,
                        CreatedBy = reviewer
                    });
                    aliasOwner[normalized] = id;
                    aliasesCreated++;
                }
            }
            else if (decision == "agency-client" && input.CanonicalEmployerId is { } clientId)
            {
                if (!employersById.ContainsKey(clientId))
                {
                    throw new ArgumentException(
                        $"The agency-client decision for '{advertisedName}' references an unknown canonical employer.",
                        nameof(request));
                }

                // Deliberately does NOT create an alias: the agency name must not
                // be attached to the client, or the distinction would be lost.
                canonicalId = clientId;
            }

            if (existing.TryGetValue(normalized, out var review))
            {
                updated++;
            }
            else
            {
                review = new EmployerAliasReview
                {
                    Id = Guid.NewGuid(),
                    NormalizedName = normalized,
                    CreatedAt = now
                };
                dbContext.EmployerAliasReviews.Add(review);
                existing[normalized] = review;
                created++;
            }

            review.AdvertisedName = advertisedName;
            review.Decision = decision;
            review.CanonicalEmployerId = canonicalId;
            review.GroupKey = GroupKey(normalized);
            review.Reason = Clean(input.Reason, 500);
            review.Reviewer = reviewer;
            review.ReviewedAt = now;
            saved++;
        }

        await dbContext.SaveChangesAsync(cancellationToken);

        var rematched = 0;
        if (aliasesCreated > 0)
        {
            rematched = (await identityService.MatchJobsAsync(reviewer, cancellationToken)).Changed;
        }

        return new EmployerReviewResultDto(
            saved,
            created,
            updated,
            aliasesCreated,
            rematched,
            await GetQueueAsync(cancellationToken));
    }

    /// <summary>
    /// Display grouping only. Strips trailing legal suffixes and punctuation so
    /// "Datacom" and "Datacom Systems Ltd" can be reviewed together; it never
    /// writes an identity match.
    /// </summary>
    internal static string GroupKey(string normalizedName)
    {
        var value = normalizedName.Trim();
        var changed = true;
        while (changed && value.Length > 0)
        {
            changed = false;
            foreach (var suffix in LegalSuffixes)
            {
                if (value.Equals(suffix, StringComparison.Ordinal))
                {
                    value = string.Empty;
                    changed = true;
                    break;
                }

                var withSpace = " " + suffix;
                if (value.EndsWith(withSpace, StringComparison.Ordinal))
                {
                    value = value[..^withSpace.Length].TrimEnd();
                    changed = true;
                    break;
                }
            }
        }

        var cleaned = new string(value.Where(character => char.IsLetterOrDigit(character) || character == '&' || character == ' ').ToArray())
            .Trim();
        return cleaned.Length == 0 ? normalizedName : cleaned;
    }

    private static string? Clean(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return trimmed.Length > maxLength ? trimmed[..maxLength] : trimmed;
    }

    private sealed record QueueRow(
        string NormalizedName,
        string AdvertisedName,
        int JobCount,
        string GroupKey,
        EmployerAliasReview? Decision,
        string? CanonicalName);
}
