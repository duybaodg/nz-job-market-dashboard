using Application.Common;
using Application.Employers;
using Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Application.Deduplication;

public interface IListingDeduplicationService
{
    /// <summary>Runs the incremental, idempotent grouping pass and returns the validated summary.</summary>
    Task<DeduplicationSummaryDto> RunAsync(string trigger, CancellationToken cancellationToken);

    Task<DeduplicationSummaryDto> GetSummaryAsync(CancellationToken cancellationToken);

    Task<DuplicateGroupsReportDto> GetGroupsAsync(int page, int pageSize, CancellationToken cancellationToken);
}

/// <summary>
/// Conservative, auditable listing deduplication v1. Groups are soft: source
/// rows are never deleted, existing groups are only adjusted (shrunk on expiry,
/// canonical re-pointed) and new/unmatched jobs are grouped incrementally. A
/// unique-vacancy count is only published from a pass that passed integrity
/// validation.
/// </summary>
public sealed class ListingDeduplicationService(
    IApplicationDbContext dbContext,
    ILogger<ListingDeduplicationService>? logger = null) : IListingDeduplicationService
{
    public async Task<DeduplicationSummaryDto> RunAsync(string trigger, CancellationToken cancellationToken)
    {
        var startedAt = DateTime.UtcNow;
        var run = new DeduplicationRun
        {
            Id = Guid.NewGuid(),
            StartedAt = startedAt,
            Status = "Running",
            MatchVersion = DeduplicationMatchMethods.MatchVersion,
            Trigger = string.IsNullOrWhiteSpace(trigger) ? "manual" : trigger
        };
        dbContext.DeduplicationRuns.Add(run);
        await dbContext.SaveChangesAsync(cancellationToken);

        try
        {
            return await ExecutePassAsync(run, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger?.LogError(exception, "Listing deduplication pass failed");
            run.Status = "Failed";
            run.FinishedAt = DateTime.UtcNow;
            run.IsValidated = false;
            run.Notes = exception.Message.Length <= 1000 ? exception.Message : exception.Message[..1000];
            await dbContext.SaveChangesAsync(CancellationToken.None);
            return await GetSummaryAsync(cancellationToken);
        }
    }

    private async Task<DeduplicationSummaryDto> ExecutePassAsync(DeduplicationRun run, CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        await BackfillDescriptionsAsync(now, cancellationToken);

        // Round 15 (H-8): expired ungrouped history is irrelevant to grouping,
        // so it is not materialised. Active rows and retained group members are.
        var allJobs = await dbContext.Jobs
            .Where(job => job.IsActive || job.DuplicateGroupId != null)
            .ToListAsync(cancellationToken);
        var activeJobs = allJobs.Where(job => job.IsActive).ToList();
        var listings = activeJobs
            .Where(job => job.EmployerCanonicalId != null)
            .ToDictionary(job => job.Id, job => ToDedupListing(job));
        var jobById = allJobs.ToDictionary(job => job.Id);

        var groups = await dbContext.DuplicateGroups.ToListAsync(cancellationToken);
        var reviewPlans = new List<(Guid JobId, Guid? CandidateJobId, string Reason)>();

        // 1. Reconcile existing groups: drop expired/unidentified members, dissolve
        //    groups that no longer have two live, still-identical members, and
        //    re-point the canonical when it expired.
        foreach (var group in groups.ToList())
        {
            var members = allJobs.Where(job => job.DuplicateGroupId == group.Id).ToList();
            DetachMembers(members.Where(job => !job.IsActive || job.EmployerCanonicalId is null).ToList(), now);
            var live = members.Where(job => job.IsActive && job.EmployerCanonicalId != null).ToList();

            var inconsistent = live.Count > 0 && !live.All(job =>
                ListingsMatchIdentity(listings[job.Id], listings[live[0].Id]));
            if (inconsistent)
            {
                foreach (var job in live)
                {
                    job.DuplicateGroupId = null;
                    job.UpdatedAt = now;
                }
            }

            live = live.Where(job => job.DuplicateGroupId == group.Id).ToList();
            if (live.Count < 2)
            {
                DetachMembers(live, now);
                dbContext.DuplicateGroups.Remove(group);
                groups.Remove(group);
                continue;
            }

            if (live.Count > DeduplicationEngine.MaxGroupSize)
            {
                // Defensive: an over-cap group is handed to review, never merged.
                var anchor = PickCanonical(live);
                foreach (var member in live.Where(member => member.Id != anchor.Id))
                {
                    reviewPlans.Add((member.Id, anchor.Id, "group-cap-exceeded"));
                }

                DetachMembers(live, now);
                dbContext.DuplicateGroups.Remove(group);
                groups.Remove(group);
                continue;
            }

            if (live.All(member => member.Id != group.CanonicalListingId))
            {
                group.CanonicalListingId = PickCanonical(live).Id;
            }

            group.MemberCount = live.Count;
            group.UpdatedAt = now;
        }

        // 2. Incrementally attach new/unmatched jobs to existing identical groups.
        var groupMembers = groups.ToDictionary(
            group => group.Id,
            group => allJobs.Where(job => job.DuplicateGroupId == group.Id && job.IsActive).ToList());
        var groupBuckets = new Dictionary<(string Title, Guid Employer), List<DuplicateGroup>>();
        foreach (var group in groups)
        {
            var members = groupMembers[group.Id];
            if (members.Count == 0 || members[0].EmployerCanonicalId is null)
            {
                continue;
            }

            var key = (ListingMatchKeys.NormalizeTitle(members[0].Title), members[0].EmployerCanonicalId!.Value);
            if (!groupBuckets.TryGetValue(key, out var bucket))
            {
                bucket = [];
                groupBuckets[key] = bucket;
            }

            bucket.Add(group);
        }

        var stillUngrouped = new List<Job>();
        var ungrouped = activeJobs
            .Where(job => job.DuplicateGroupId == null && job.EmployerCanonicalId != null)
            .OrderBy(job => job.FirstSeenAt)
            .ThenBy(job => job.Id)
            .ToList();

        foreach (var job in ungrouped)
        {
            var listing = listings[job.Id];
            var key = (listing.NormalizedTitle, listing.EmployerCanonicalId);
            if (!groupBuckets.TryGetValue(key, out var candidates))
            {
                stillUngrouped.Add(job);
                continue;
            }

            DuplicateGroup? join = null;
            (Guid JobId, Guid? CandidateJobId, string Reason)? pendingReview = null;
            foreach (var group in candidates)
            {
                var members = groupMembers[group.Id];
                var memberListings = members.Select(member => listings[member.Id]).ToList();
                var evaluations = memberListings.Select(member => DeduplicationEngine.Evaluate(listing, member)).ToList();

                if (evaluations.Any(evaluation => evaluation.Relation == DedupPairRelation.SalaryConflict))
                {
                    pendingReview ??= (job.Id, members[0].Id, "salary-conflict");
                    continue;
                }

                if (members.Count >= DeduplicationEngine.MaxGroupSize)
                {
                    if (evaluations.Any(evaluation => evaluation.IsMatch))
                    {
                        pendingReview ??= (job.Id, members[0].Id, "group-cap-exceeded");
                    }

                    continue;
                }

                if (evaluations.Any(evaluation => evaluation.IsMatch))
                {
                    join = group;
                    break;
                }

                pendingReview ??= (job.Id, members[0].Id, "gates-failed");
            }

            if (join is null)
            {
                if (pendingReview is not null)
                {
                    reviewPlans.Add(pendingReview.Value);
                }

                stillUngrouped.Add(job);
                continue;
            }

            job.DuplicateGroupId = join.Id;
            job.UpdatedAt = now;
            groupMembers[join.Id].Add(job);
            if (join.CanonicalListingId != job.Id)
            {
                var currentCanonical = jobById.GetValueOrDefault(join.CanonicalListingId);
                if (currentCanonical is null || PreferCanonical(job, currentCanonical))
                {
                    join.CanonicalListingId = job.Id;
                }
            }
        }

        // 3. Form brand-new groups from the remaining ungrouped jobs.
        var plan = DeduplicationEngine.Plan(
            stillUngrouped.Select(job => listings[job.Id]).ToList(),
            DeduplicationEngine.MaxGroupSize);

        foreach (var planGroup in plan.Groups)
        {
            var members = planGroup.MemberJobIds.Select(id => jobById[id]).ToList();
            var group = new DuplicateGroup
            {
                Id = Guid.NewGuid(),
                CanonicalListingId = PickCanonical(members).Id,
                MatchMethod = planGroup.MatchMethod,
                MatchVersion = DeduplicationMatchMethods.MatchVersion,
                Confidence = planGroup.Confidence,
                CreatedAt = now,
                UpdatedAt = now,
                MemberCount = members.Count
            };
            dbContext.DuplicateGroups.Add(group);
            groups.Add(group);
            groupMembers[group.Id] = members;
            foreach (var member in members)
            {
                member.DuplicateGroupId = group.Id;
                member.UpdatedAt = now;
            }
        }

        foreach (var pair in plan.ReviewCandidates)
        {
            reviewPlans.Add((pair.JobId, pair.CandidateJobId, pair.Reason));
        }

        // 4. Final consolidation: drop any group that ended below two members.
        foreach (var group in groups.ToList())
        {
            var members = allJobs
                .Where(job => job.DuplicateGroupId == group.Id && job.IsActive && job.EmployerCanonicalId != null)
                .ToList();
            if (members.Count < 2)
            {
                DetachMembers(members, now);
                dbContext.DuplicateGroups.Remove(group);
                groups.Remove(group);
                continue;
            }

            group.MemberCount = members.Count;
            group.UpdatedAt = now;
            if (members.All(member => member.Id != group.CanonicalListingId))
            {
                group.CanonicalListingId = PickCanonical(members).Id;
            }
        }

        // 5. Replace the open review-candidate set with this pass's findings.
        var previousCandidates = await dbContext.DuplicateReviewCandidates
            .Where(candidate => candidate.ResolvedAt == null)
            .ToListAsync(cancellationToken);
        dbContext.DuplicateReviewCandidates.RemoveRange(previousCandidates);

        var seenCandidates = new HashSet<(Guid, Guid, string)>();
        var newCandidates = new List<DuplicateReviewCandidate>();
        foreach (var (jobId, candidateJobId, reason) in reviewPlans)
        {
            if (!jobById.ContainsKey(jobId))
            {
                continue;
            }

            var candidateKey = candidateJobId ?? Guid.Empty;
            if (!seenCandidates.Add((jobId, candidateKey, reason)))
            {
                continue;
            }

            newCandidates.Add(new DuplicateReviewCandidate
            {
                Id = Guid.NewGuid(),
                JobId = jobId,
                CandidateJobId = candidateJobId,
                Reason = reason,
                Title = jobById[jobId].Title,
                Employer = jobById[jobId].Company,
                CreatedAt = now
            });
        }

        dbContext.DuplicateReviewCandidates.AddRange(newCandidates);

        var merged = groups.Sum(group => Math.Max(0, group.MemberCount - 1));
        var uniqueVacancies = Math.Max(0, activeJobs.Count - merged);
        var valid = Validate(groups, allJobs);

        run.FinishedAt = DateTime.UtcNow;
        run.Status = "Completed";
        run.IsValidated = valid;
        run.ListingsEvaluated = activeJobs.Count;
        run.GroupsFound = groups.Count;
        run.ListingsMerged = merged;
        run.UniqueVacancies = uniqueVacancies;
        run.ReviewCandidateCount = newCandidates.Count;
        run.Notes = valid
            ? "integrity checks passed"
            : "integrity checks failed; unique vacancies withheld";

        await dbContext.SaveChangesAsync(cancellationToken);
        logger?.LogInformation(
            "Dedup pass {Trigger}: {Listings} listings, {Groups} groups, {Merged} merged, {Unique} unique, {Reviews} review candidates, validated={Validated}.",
            run.Trigger,
            activeJobs.Count,
            groups.Count,
            merged,
            uniqueVacancies,
            newCandidates.Count,
            valid);

        return ToSummary(run, groups.Count, merged, activeJobs.Count, newCandidates.Count, active: valid);
    }

    public async Task<DeduplicationSummaryDto> GetSummaryAsync(CancellationToken cancellationToken)
    {
        var activeListings = await dbContext.Jobs.CountAsync(job => job.IsActive, cancellationToken);
        var groups = await dbContext.DuplicateGroups.AsNoTracking().ToListAsync(cancellationToken);
        var merged = groups.Sum(group => Math.Max(0, group.MemberCount - 1));
        var reviews = await dbContext.DuplicateReviewCandidates
            .AsNoTracking()
            .CountAsync(candidate => candidate.ResolvedAt == null, cancellationToken);
        var latestRun = await dbContext.DeduplicationRuns
            .AsNoTracking()
            .OrderByDescending(item => item.StartedAt)
            .FirstOrDefaultAsync(cancellationToken);
        var lastValidated = await dbContext.DeduplicationRuns
            .AsNoTracking()
            .Where(item => item.IsValidated)
            .OrderByDescending(item => item.FinishedAt)
            .FirstOrDefaultAsync(cancellationToken);

        var active = lastValidated is not null;
        return new DeduplicationSummaryDto(
            active,
            lastValidated?.FinishedAt,
            DeduplicationMatchMethods.MatchVersion,
            activeListings,
            active ? lastValidated!.UniqueVacancies : null,
            groups.Count,
            merged,
            reviews,
            DeduplicationEngine.MaxGroupSize,
            latestRun?.Status);
    }

    public async Task<DuplicateGroupsReportDto> GetGroupsAsync(
        int page,
        int pageSize,
        CancellationToken cancellationToken)
    {
        page = Math.Max(page, 1);
        pageSize = Math.Clamp(pageSize, 1, 200);
        var totalGroups = await dbContext.DuplicateGroups.CountAsync(cancellationToken);
        var groups = await dbContext.DuplicateGroups
            .AsNoTracking()
            .OrderByDescending(group => group.MemberCount)
            .ThenBy(group => group.CreatedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(cancellationToken);
        var groupIds = groups.Select(group => group.Id).ToList();
        var memberships = await dbContext.Jobs
            .AsNoTracking()
            .Where(job => job.DuplicateGroupId != null && groupIds.Contains(job.DuplicateGroupId.Value))
            .Select(job => new DuplicateMemberRow(
                job.DuplicateGroupId!.Value,
                job.Id,
                job.Source,
                job.Title,
                job.Company,
                job.Url,
                job.IsActive))
            .ToListAsync(cancellationToken);
        var categories = await LoadSourceCategoriesAsync(cancellationToken);
        var summary = await GetSummaryAsync(cancellationToken);

        var groupDtos = groups
            .Select(group =>
            {
                var members = memberships
                    .Where(membership => membership.GroupId == group.Id)
                    .Select(membership => ToMemberDto(membership, group, categories))
                    .OrderByDescending(member => member.IsCanonical)
                    .ThenBy(member => member.Source, StringComparer.OrdinalIgnoreCase)
                    .ToList();
                return new DuplicateGroupDto(
                    group.Id,
                    group.MatchMethod,
                    group.MatchVersion,
                    group.Confidence,
                    group.CanonicalListingId,
                    group.MemberCount,
                    group.CreatedAt,
                    group.UpdatedAt,
                    members);
            })
            .ToList();

        var reviewCandidates = await dbContext.DuplicateReviewCandidates
            .AsNoTracking()
            .Where(candidate => candidate.ResolvedAt == null)
            .OrderByDescending(candidate => candidate.CreatedAt)
            .Select(candidate => new DuplicateReviewCandidateDto(
                candidate.JobId,
                candidate.CandidateJobId,
                candidate.Reason,
                candidate.Title,
                candidate.Employer,
                candidate.CreatedAt))
            .ToListAsync(cancellationToken);

        return new DuplicateGroupsReportDto(
            DateTime.UtcNow,
            totalGroups,
            summary.ListingsMerged,
            summary.UniqueVacancies,
            summary.ActiveListings,
            summary.Active,
            groupDtos,
            reviewCandidates,
            page,
            pageSize);
    }

    private static DuplicateGroupMemberDto ToMemberDto(
        DuplicateMemberRow job,
        DuplicateGroup group,
        IReadOnlyDictionary<string, string> categories)
    {
        categories.TryGetValue(job.Source, out var category);
        var employer = EmployerClassifier.Classify(job.Source, category, job.Company);
        return new DuplicateGroupMemberDto(
            job.Id,
            job.Source,
            job.Title,
            job.Company,
            employer.HiringEmployer,
            job.Url,
            job.IsActive,
            job.Id == group.CanonicalListingId);
    }

    private sealed record DuplicateMemberRow(
        Guid GroupId,
        Guid Id,
        string Source,
        string Title,
        string? Company,
        string Url,
        bool IsActive);

    private async Task BackfillDescriptionsAsync(DateTime now, CancellationToken cancellationToken)
    {
        var updated = 0;
        var offset = 0;

        // Round 15 (H-8): page instead of loading every description-less row and
        // every matching markdown page at once.
        while (true)
        {
            var missing = await dbContext.Jobs
                .Where(job => job.DescriptionText == null || job.DescriptionText == "")
                .OrderBy(job => job.Id)
                .Skip(offset)
                .Take(500)
                .ToListAsync(cancellationToken);
            if (missing.Count == 0)
            {
                break;
            }

            offset += missing.Count;
            var urls = missing
                .Select(job => job.Url)
                .Where(url => !string.IsNullOrWhiteSpace(url))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (urls.Count == 0)
            {
                continue;
            }

            var pages = await dbContext.RawJobPages
                .AsNoTracking()
                .Where(page => urls.Contains(page.Url) && page.Markdown != null && page.Markdown != "")
                .Select(page => new { page.Url, page.Markdown, page.CrawledAt })
                .ToListAsync(cancellationToken);
            var byUrl = pages
                .GroupBy(page => page.Url, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(
                    group => group.Key,
                    group => group.OrderByDescending(page => page.CrawledAt).First().Markdown!,
                    StringComparer.OrdinalIgnoreCase);

            var batchUpdated = 0;
            foreach (var job in missing)
            {
                if (!byUrl.TryGetValue(job.Url, out var markdown) || string.IsNullOrWhiteSpace(markdown))
                {
                    continue;
                }

                job.DescriptionText = markdown.Length <= 20000 ? markdown : markdown[..20000];
                job.UpdatedAt = now;
                batchUpdated++;
            }

            if (batchUpdated > 0)
            {
                updated += batchUpdated;
                await dbContext.SaveChangesAsync(cancellationToken);
            }
        }

        if (updated > 0)
        {
            logger?.LogInformation("Backfilled full description text for {Count} listings.", updated);
        }
    }

    private static bool Validate(IReadOnlyList<DuplicateGroup> groups, IReadOnlyList<Job> allJobs)
    {
        if (groups.Any(group =>
                group.MemberCount < 2
                || group.MemberCount > DeduplicationEngine.MaxGroupSize
                || group.CanonicalListingId == Guid.Empty))
        {
            return false;
        }

        var validGroupIds = groups.Select(group => group.Id).ToHashSet();
        foreach (var job in allJobs.Where(job => job.DuplicateGroupId != null))
        {
            if (!validGroupIds.Contains(job.DuplicateGroupId!.Value))
            {
                return false;
            }
        }

        foreach (var group in groups)
        {
            var members = allJobs
                .Where(job => job.DuplicateGroupId == group.Id && job.IsActive && job.EmployerCanonicalId != null)
                .ToList();
            if (members.Count != group.MemberCount
                || members.Count < 2
                || members.Count > DeduplicationEngine.MaxGroupSize
                || members.All(member => member.Id != group.CanonicalListingId))
            {
                return false;
            }
        }

        return true;
    }

    private static void DetachMembers(IEnumerable<Job> members, DateTime now)
    {
        foreach (var member in members)
        {
            member.DuplicateGroupId = null;
            member.UpdatedAt = now;
        }
    }

    private static bool ListingsMatchIdentity(DedupListing left, DedupListing right) =>
        string.Equals(left.NormalizedTitle, right.NormalizedTitle, StringComparison.Ordinal)
        && left.EmployerCanonicalId == right.EmployerCanonicalId;

    private static Job PickCanonical(IReadOnlyList<Job> members) =>
        members
            .OrderByDescending(member => member.PostedDate != null)
            .ThenBy(member => member.FirstSeenAt)
            .ThenBy(member => member.Id)
            .First();

    private static bool PreferCanonical(Job candidate, Job current)
    {
        if ((candidate.PostedDate != null) != (current.PostedDate != null))
        {
            return candidate.PostedDate != null;
        }

        return candidate.FirstSeenAt < current.FirstSeenAt;
    }

    private static DedupListing ToDedupListing(Job job)
    {
        var windowStart = job.PostedDate ?? job.FirstSeenAt;
        var windowEnd = job.ClosingDate ?? job.LastSeenAt;
        if (windowEnd < windowStart)
        {
            windowEnd = windowStart;
        }

        var salary = ListingMatchKeys.NormalizeSalary(job.SalaryText);
        return new DedupListing(
            job.Id,
            job.Source,
            ListingMatchKeys.NormalizeTitle(job.Title),
            job.EmployerCanonicalId!.Value,
            HasSalary: salary.Length > 0,
            SalaryKey: salary,
            LocationKey: ListingMatchKeys.NormalizeLocation(job.Region),
            WindowStart: windowStart,
            WindowEnd: windowEnd,
            RepresentativeDate: job.PostedDate ?? job.FirstSeenAt,
            DescriptionShingles: DescriptionSimilarity.Shingles(job.DescriptionText ?? job.DescriptionSummary));
    }

    private static DeduplicationSummaryDto ToSummary(
        DeduplicationRun run,
        int groups,
        int merged,
        int activeListings,
        int reviewCandidates,
        bool active) =>
        new(
            active,
            active ? run.FinishedAt : null,
            run.MatchVersion,
            activeListings,
            active ? run.UniqueVacancies : null,
            groups,
            merged,
            reviewCandidates,
            DeduplicationEngine.MaxGroupSize,
            run.Status);

    private async Task<IReadOnlyDictionary<string, string>> LoadSourceCategoriesAsync(CancellationToken cancellationToken)
    {
        var categories = await dbContext.JobSources
            .AsNoTracking()
            .Select(source => new { source.Name, source.Category })
            .ToListAsync(cancellationToken);
        return categories
            .GroupBy(source => source.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => string.IsNullOrWhiteSpace(group.First().Category) ? "unknown" : group.First().Category,
                StringComparer.OrdinalIgnoreCase);
    }
}
