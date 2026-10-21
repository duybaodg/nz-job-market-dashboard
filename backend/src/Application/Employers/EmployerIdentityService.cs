using Application.Common;
using Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Application.Employers;

public interface IEmployerIdentityService
{
    /// <summary>Bootstraps canonicals from the current distinct company values (once).</summary>
    Task<EmployerSeedResult> SeedFromCompaniesAsync(string actor, CancellationToken cancellationToken);

    /// <summary>Repair pass: recomputes every job's canonical employer by exact/alias match.</summary>
    Task<EmployerMatchResult> MatchJobsAsync(string actor, CancellationToken cancellationToken);

    /// <summary>Ingestion hook: resolves one job's canonical by exact/alias match (never creates).</summary>
    Task<Guid?> ResolveCanonicalEmployerAsync(string source, string? company, CancellationToken cancellationToken);

    Task<EmployersReportDto> GetReportAsync(CancellationToken cancellationToken);
    Task<EmployerDto> CreateEmployerAsync(CreateEmployerRequest request, CancellationToken cancellationToken);
    Task<EmployerDto?> AddAliasAsync(Guid employerId, AddEmployerAliasRequest request, CancellationToken cancellationToken);
    Task<bool> RemoveAliasAsync(Guid employerId, string alias, string actor, CancellationToken cancellationToken);
}

/// <summary>
/// The only place canonical employer identity is decided. Matching is exact
/// after case/whitespace normalisation or through an admin-curated alias;
/// unmatched names create nothing and the job stays in the unknown bucket.
/// There is deliberately no fuzzy path.
/// </summary>
public sealed class EmployerIdentityService(
    IApplicationDbContext dbContext,
    ILogger<EmployerIdentityService>? logger = null) : IEmployerIdentityService
{
    private const string SeedAction = "EmployerSeedFromCompanies";
    private const string MatchAction = "EmployerMatchPass";
    private EmployerLookup? cachedLookup;

    public async Task<EmployerSeedResult> SeedFromCompaniesAsync(string actor, CancellationToken cancellationToken)
    {
        var alreadySeeded = await dbContext.EmployerAuditEvents
            .AsNoTracking()
            .AnyAsync(audit => audit.Action == SeedAction, cancellationToken);
        if (alreadySeeded)
        {
            return new EmployerSeedResult(false, 0, 0);
        }

        var now = DateTime.UtcNow;

        // Round 15 (H-9): a fresh database has no companies yet. Do not seed the
        // reviewed agencies or write the one-shot audit marker in that state, or
        // the employer list would never be bootstrapped after the first crawl.
        var companies = await dbContext.Jobs
            .AsNoTracking()
            .Where(job => job.Company != null && job.Company != "")
            .Select(job => job.Company!)
            .Distinct()
            .ToListAsync(cancellationToken);
        if (companies.Count == 0)
        {
            return new EmployerSeedResult(false, 0, 0);
        }

        var employers = await dbContext.Employers.Include(employer => employer.Aliases).ToListAsync(cancellationToken);
        var byNormalisedName = employers
            .GroupBy(employer => employer.NormalizedName, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        var known = new HashSet<string>(StringComparer.Ordinal);
        foreach (var employer in employers)
        {
            known.Add(employer.NormalizedName);
            foreach (var alias in employer.Aliases)
            {
                known.Add(alias.NormalizedAlias);
            }
        }

        var canonicalsCreated = 0;
        var aliasesCreated = 0;

        // 1. Reviewed agencies and their board-facing aliases.
        foreach (var (alias, canonicalName) in ReviewedEmployerSeed.AgencySeeds)
        {
            var canonicalNormalised = EmployerNameNormaliser.Normalise(canonicalName);
            if (!byNormalisedName.TryGetValue(canonicalNormalised, out var canonical))
            {
                canonical = new Employer
                {
                    Id = Guid.NewGuid(),
                    CanonicalName = canonicalName,
                    NormalizedName = canonicalNormalised,
                    Type = EmployerType.Agency,
                    CreatedAt = now,
                    Notes = "Seeded reviewed agency"
                };
                dbContext.Employers.Add(canonical);
                byNormalisedName[canonicalNormalised] = canonical;
                known.Add(canonicalNormalised);
                canonicalsCreated++;
            }

            var aliasNormalised = EmployerNameNormaliser.Normalise(alias);
            if (aliasNormalised.Length == 0 || aliasNormalised == canonicalNormalised || known.Contains(aliasNormalised))
            {
                continue;
            }

            dbContext.EmployerAliases.Add(new EmployerAlias
            {
                Id = Guid.NewGuid(),
                EmployerId = canonical.Id,
                Alias = alias.Trim(),
                NormalizedAlias = aliasNormalised,
                CreatedAt = now,
                CreatedBy = "system:seed"
            });
            known.Add(aliasNormalised);
            aliasesCreated++;
        }

        // 2. Everything else currently advertised becomes a direct employer.
        foreach (var company in companies)
        {
            var normalised = EmployerNameNormaliser.Normalise(company);
            if (normalised.Length == 0 || known.Contains(normalised))
            {
                continue;
            }

            dbContext.Employers.Add(new Employer
            {
                Id = Guid.NewGuid(),
                CanonicalName = company.Trim(),
                NormalizedName = normalised,
                Type = EmployerType.DirectEmployer,
                CreatedAt = now,
                Notes = "Seeded from source company value"
            });
            known.Add(normalised);
            canonicalsCreated++;
        }

        dbContext.EmployerAuditEvents.Add(new EmployerAuditEvent
        {
            Id = Guid.NewGuid(),
            EmployerId = null,
            Action = SeedAction,
            Detail = $"canonicals={canonicalsCreated} aliases={aliasesCreated} distinct_companies={companies.Count}",
            Actor = string.IsNullOrWhiteSpace(actor) ? "system" : actor,
            CreatedAt = now
        });

        await dbContext.SaveChangesAsync(cancellationToken);
        cachedLookup = null;
        logger?.LogInformation("Employer seed created {Canonicals} canonicals and {Aliases} aliases.", canonicalsCreated, aliasesCreated);
        return new EmployerSeedResult(true, canonicalsCreated, aliasesCreated);
    }

    public async Task<EmployerMatchResult> MatchJobsAsync(string actor, CancellationToken cancellationToken)
    {
        var lookup = await BuildLookupAsync(cancellationToken);
        cachedLookup = lookup;

        var jobs = await dbContext.Jobs.ToListAsync(cancellationToken);
        var matched = 0;
        var unmatched = 0;
        var changed = 0;

        foreach (var job in jobs)
        {
            var (canonicalId, disclosed) = Resolve(lookup, job.Source, job.Company);
            if (disclosed)
            {
                if (canonicalId is null)
                {
                    unmatched++;
                }
                else
                {
                    matched++;
                }
            }

            if (job.EmployerCanonicalId != canonicalId)
            {
                job.EmployerCanonicalId = canonicalId;
                job.UpdatedAt = DateTime.UtcNow;
                changed++;
            }
        }

        if (changed > 0)
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }

        dbContext.EmployerAuditEvents.Add(new EmployerAuditEvent
        {
            Id = Guid.NewGuid(),
            EmployerId = null,
            Action = MatchAction,
            Detail = $"evaluated={jobs.Count} matched={matched} unmatched={unmatched} changed={changed}",
            Actor = string.IsNullOrWhiteSpace(actor) ? "system" : actor,
            CreatedAt = DateTime.UtcNow
        });
        await dbContext.SaveChangesAsync(cancellationToken);

        logger?.LogInformation(
            "Employer match pass: evaluated {Evaluated}, matched {Matched}, unmatched {Unmatched}, changed {Changed}.",
            jobs.Count,
            matched,
            unmatched,
            changed);
        return new EmployerMatchResult(jobs.Count, matched, unmatched, changed);
    }

    public async Task<Guid?> ResolveCanonicalEmployerAsync(string source, string? company, CancellationToken cancellationToken)
    {
        var lookup = cachedLookup ??= await BuildLookupAsync(cancellationToken);
        var (canonicalId, _) = Resolve(lookup, source, company);
        return canonicalId;
    }

    public async Task<EmployersReportDto> GetReportAsync(CancellationToken cancellationToken)
    {
        var employers = await dbContext.Employers
            .AsNoTracking()
            .Include(employer => employer.Aliases)
            .OrderBy(employer => employer.CanonicalName)
            .ToListAsync(cancellationToken);

        var activeJobs = await dbContext.Jobs
            .AsNoTracking()
            .Where(job => job.IsActive)
            .Select(job => new { job.Source, job.Company, job.EmployerCanonicalId })
            .ToListAsync(cancellationToken);

        var lookup = await BuildLookupAsync(cancellationToken);
        var countsByEmployer = activeJobs
            .Where(job => job.EmployerCanonicalId != null)
            .GroupBy(job => job.EmployerCanonicalId!.Value)
            .ToDictionary(group => group.Key, group => group.Count());

        var employerDtos = employers
            .Select(employer => new EmployerDto(
                employer.Id,
                employer.CanonicalName,
                employer.Type.ToString(),
                employer.Aliases
                    .OrderBy(alias => alias.Alias, StringComparer.OrdinalIgnoreCase)
                    .Select(alias => alias.Alias)
                    .ToList(),
                countsByEmployer.GetValueOrDefault(employer.Id),
                employer.CreatedAt,
                employer.Notes))
            .ToList();

        var unmatched = new Dictionary<string, (string Name, int Count)>(StringComparer.Ordinal);
        var listingsWithCanonical = 0;
        foreach (var job in activeJobs)
        {
            if (job.EmployerCanonicalId != null)
            {
                listingsWithCanonical++;
                continue;
            }

            var (_, disclosed) = Resolve(lookup, job.Source, job.Company);
            if (!disclosed)
            {
                continue;
            }

            var classification = EmployerClassifier.Classify(
                job.Source,
                CategoryFor(lookup, job.Source),
                job.Company);
            var name = classification.HiringEmployer?.Trim();
            if (string.IsNullOrEmpty(name))
            {
                continue;
            }

            var key = EmployerNameNormaliser.Normalise(name);
            if (unmatched.TryGetValue(key, out var current))
            {
                unmatched[key] = (current.Name, current.Count + 1);
            }
            else
            {
                unmatched[key] = (name, 1);
            }
        }

        var unmatchedDtos = unmatched.Values
            .OrderByDescending(item => item.Count)
            .ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .Select(item => new UnmatchedEmployerNameDto(item.Name, item.Count))
            .ToList();

        return new EmployersReportDto(
            DateTime.UtcNow,
            employerDtos.Count,
            employerDtos.Count(employer => employer.Type == EmployerType.Agency.ToString()),
            employerDtos.Count(employer => employer.Type == EmployerType.DirectEmployer.ToString()),
            listingsWithCanonical,
            activeJobs.Count - listingsWithCanonical,
            employerDtos,
            unmatchedDtos);
    }

    public async Task<EmployerDto> CreateEmployerAsync(CreateEmployerRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.CanonicalName))
        {
            throw new ArgumentException("A canonical name is required.", nameof(request));
        }

        var normalised = EmployerNameNormaliser.Normalise(request.CanonicalName);
        var actor = string.IsNullOrWhiteSpace(request.Actor) ? "admin" : request.Actor.Trim();
        var existing = await dbContext.Employers
            .Include(employer => employer.Aliases)
            .FirstOrDefaultAsync(employer => employer.NormalizedName == normalised, cancellationToken);
        if (existing is not null)
        {
            return ToDto(existing, 0);
        }

        var employer = new Employer
        {
            Id = Guid.NewGuid(),
            CanonicalName = request.CanonicalName.Trim(),
            NormalizedName = normalised,
            Type = ParseType(request.Type),
            CreatedAt = DateTime.UtcNow,
            Notes = string.IsNullOrWhiteSpace(request.Notes) ? null : request.Notes.Trim()
        };
        dbContext.Employers.Add(employer);
        dbContext.EmployerAuditEvents.Add(new EmployerAuditEvent
        {
            Id = Guid.NewGuid(),
            EmployerId = employer.Id,
            Action = "EmployerCreated",
            Detail = $"canonical={employer.CanonicalName} type={employer.Type}",
            Actor = actor,
            CreatedAt = DateTime.UtcNow
        });
        await dbContext.SaveChangesAsync(cancellationToken);
        cachedLookup = null;
        await MatchJobsAsync(actor, cancellationToken);
        return ToDto(employer, 0);
    }

    public async Task<EmployerDto?> AddAliasAsync(Guid employerId, AddEmployerAliasRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Alias))
        {
            return null;
        }

        var employer = await dbContext.Employers
            .Include(entity => entity.Aliases)
            .FirstOrDefaultAsync(entity => entity.Id == employerId, cancellationToken);
        if (employer is null)
        {
            return null;
        }

        var normalised = EmployerNameNormaliser.Normalise(request.Alias);
        var actor = string.IsNullOrWhiteSpace(request.Actor) ? "admin" : request.Actor.Trim();
        var clash = await dbContext.EmployerAliases
            .FirstOrDefaultAsync(alias => alias.NormalizedAlias == normalised, cancellationToken);
        if (clash is not null)
        {
            return clash.EmployerId == employerId ? ToDto(employer, 0) : null;
        }

        var canonicalClash = await dbContext.Employers
            .FirstOrDefaultAsync(entity => entity.NormalizedName == normalised && entity.Id != employerId, cancellationToken);
        if (canonicalClash is not null)
        {
            return null;
        }

        dbContext.EmployerAliases.Add(new EmployerAlias
        {
            Id = Guid.NewGuid(),
            EmployerId = employerId,
            Alias = request.Alias.Trim(),
            NormalizedAlias = normalised,
            CreatedAt = DateTime.UtcNow,
            CreatedBy = actor
        });
        dbContext.EmployerAuditEvents.Add(new EmployerAuditEvent
        {
            Id = Guid.NewGuid(),
            EmployerId = employerId,
            Action = "AliasAdded",
            Detail = $"alias={request.Alias.Trim()}",
            Actor = actor,
            CreatedAt = DateTime.UtcNow
        });
        await dbContext.SaveChangesAsync(cancellationToken);
        cachedLookup = null;
        await MatchJobsAsync(actor, cancellationToken);
        return await GetEmployerDtoAsync(employerId, cancellationToken);
    }

    public async Task<bool> RemoveAliasAsync(Guid employerId, string alias, string actor, CancellationToken cancellationToken)
    {
        var normalised = EmployerNameNormaliser.Normalise(alias);
        var entity = await dbContext.EmployerAliases
            .FirstOrDefaultAsync(item => item.EmployerId == employerId && item.NormalizedAlias == normalised, cancellationToken);
        if (entity is null)
        {
            return false;
        }

        dbContext.EmployerAliases.Remove(entity);
        dbContext.EmployerAuditEvents.Add(new EmployerAuditEvent
        {
            Id = Guid.NewGuid(),
            EmployerId = employerId,
            Action = "AliasRemoved",
            Detail = $"alias={entity.Alias}",
            Actor = string.IsNullOrWhiteSpace(actor) ? "admin" : actor,
            CreatedAt = DateTime.UtcNow
        });
        await dbContext.SaveChangesAsync(cancellationToken);
        cachedLookup = null;
        // Removing an alias immediately un-matches any job that only resolved
        // through it, returning those jobs to the unknown bucket.
        await MatchJobsAsync(string.IsNullOrWhiteSpace(actor) ? "admin" : actor, cancellationToken);
        return true;
    }

    private async Task<EmployerDto> GetEmployerDtoAsync(Guid employerId, CancellationToken cancellationToken)
    {
        var employer = await dbContext.Employers
            .AsNoTracking()
            .Include(entity => entity.Aliases)
            .FirstAsync(entity => entity.Id == employerId, cancellationToken);
        var jobCount = await dbContext.Jobs.CountAsync(job => job.IsActive && job.EmployerCanonicalId == employerId, cancellationToken);
        return ToDto(employer, jobCount);
    }

    private static EmployerDto ToDto(Employer employer, int jobCount) =>
        new(
            employer.Id,
            employer.CanonicalName,
            employer.Type.ToString(),
            employer.Aliases
                .OrderBy(alias => alias.Alias, StringComparer.OrdinalIgnoreCase)
                .Select(alias => alias.Alias)
                .ToList(),
            jobCount,
            employer.CreatedAt,
            employer.Notes);

    private static EmployerType ParseType(string? value) =>
        Enum.TryParse<EmployerType>(value, ignoreCase: true, out var type) ? type : EmployerType.DirectEmployer;

    private static (Guid? CanonicalId, bool Disclosed) Resolve(EmployerLookup lookup, string source, string? company)
    {
        var classification = EmployerClassifier.Classify(source, CategoryFor(lookup, source), company);
        if (classification.Disclosure != EmployerDisclosure.Disclosed || string.IsNullOrWhiteSpace(classification.HiringEmployer))
        {
            return (null, false);
        }

        var id = lookup.ByNormalisedName.TryGetValue(
            EmployerNameNormaliser.Normalise(classification.HiringEmployer),
            out var employer)
            ? employer.Id
            : (Guid?)null;
        return (id, true);
    }

    private static string? CategoryFor(EmployerLookup lookup, string source) =>
        lookup.SourceCategories.TryGetValue(source, out var category) ? category : null;

    private async Task<EmployerLookup> BuildLookupAsync(CancellationToken cancellationToken)
    {
        var employers = await dbContext.Employers
            .AsNoTracking()
            .Include(employer => employer.Aliases)
            .ToListAsync(cancellationToken);
        var byNormalisedName = new Dictionary<string, Employer>(StringComparer.Ordinal);
        foreach (var employer in employers)
        {
            byNormalisedName.TryAdd(employer.NormalizedName, employer);
            foreach (var alias in employer.Aliases)
            {
                if (!string.IsNullOrEmpty(alias.NormalizedAlias))
                {
                    byNormalisedName.TryAdd(alias.NormalizedAlias, employer);
                }
            }
        }

        var categories = await dbContext.JobSources
            .AsNoTracking()
            .Select(source => new { source.Name, source.Category })
            .ToListAsync(cancellationToken);
        var categoryMap = categories
            .GroupBy(source => source.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => string.IsNullOrWhiteSpace(group.First().Category) ? "unknown" : group.First().Category,
                StringComparer.OrdinalIgnoreCase);

        return new EmployerLookup(byNormalisedName, categoryMap);
    }

    private sealed record EmployerLookup(
        IReadOnlyDictionary<string, Employer> ByNormalisedName,
        IReadOnlyDictionary<string, string> SourceCategories);
}
