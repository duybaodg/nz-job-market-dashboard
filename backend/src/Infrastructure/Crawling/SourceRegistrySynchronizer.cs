using Application.Common;
using Application.Crawling;
using Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Infrastructure.Crawling;

/// <summary>
/// Keeps <c>job_sources</c> in step with <c>crawler/sources/sites.json</c>.
/// Runs once at API/worker startup so adding or retuning a site is a registry
/// edit plus a restart, not a bespoke migration.
/// </summary>
public sealed class SourceRegistrySynchronizer(
    IApplicationDbContext dbContext,
    IConfiguration configuration,
    ILogger<SourceRegistrySynchronizer> logger) : ISourceRegistrySynchronizer
{
    public async Task<SourceSyncResult> SyncAsync(CancellationToken cancellationToken)
    {
        var path = SourceRegistryReader.ResolvePath(configuration);
        if (path is null)
        {
            logger.LogWarning("Crawler source registry was not found; skipping job_sources sync.");
            return new SourceSyncResult(0, 0, 0, 0, 0);
        }

        var sites = SourceRegistryReader.Load(path);
        if (sites.Count == 0)
        {
            logger.LogWarning("Registry {Path} has no 'sites' entries; skipping sync.", path);
            return new SourceSyncResult(0, 0, 0, 0, 0);
        }

        var existing = await dbContext.JobSources.ToListAsync(cancellationToken);
        var byName = existing.ToDictionary(source => source.Name, StringComparer.OrdinalIgnoreCase);
        var registryNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var now = DateTime.UtcNow;
        var inserted = 0;
        var updated = 0;
        var revived = 0;

        foreach (var site in sites)
        {
            if (string.IsNullOrWhiteSpace(site.Name))
            {
                continue;
            }

            registryNames.Add(site.Name);
            var parser = string.IsNullOrWhiteSpace(site.Parser) ? "generic" : site.Parser!;
            var category = string.IsNullOrWhiteSpace(site.Category) ? "unknown" : site.Category!.Trim().ToLowerInvariant();
            var baseUrl = site.JobsUrl ?? site.Homepage;
            var enabled = site.Enabled ?? true;
            var frequency = site.FrequencyMinutes is > 0 ? site.FrequencyMinutes.Value : 1440;
            // Round 15 (M-8): only an explicit robotsAllows=true grants approval.
            // false revokes it, and null is unapproved-pending review.
            var robotsAllows = site.RobotsAllows == true;
            var termsCheckedAt = robotsAllows ? (DateTime?)now : null;

            if (byName.TryGetValue(site.Name, out var entity))
            {
                // A row that is back in the registry is no longer retired, even
                // if the registry keeps it disabled for review.
                var wasRetired = entity.RetiredAt.HasValue;
                var changed =
                    entity.BaseUrl != baseUrl ||
                    entity.Parser != parser ||
                    entity.Category != category ||
                    entity.Enabled != enabled ||
                    entity.CrawlFrequencyMinutes != frequency ||
                    wasRetired ||
                    !string.Equals(entity.Method, "Scrapling", StringComparison.Ordinal);
                var nextTermsCheckedAt = robotsAllows ? (entity.TermsCheckedAt ?? now) : (DateTime?)null;
                changed = changed || entity.TermsCheckedAt != nextTermsCheckedAt;

                entity.BaseUrl = baseUrl;
                entity.Parser = parser;
                entity.Category = category;
                entity.Enabled = enabled;
                entity.Method = "Scrapling";
                entity.CrawlFrequencyMinutes = frequency;
                entity.TermsCheckedAt = nextTermsCheckedAt;
                if (wasRetired)
                {
                    entity.RetiredAt = null;
                    revived++;
                }

                if (changed)
                {
                    entity.UpdatedAt = now;
                    updated++;
                }
            }
            else
            {
                dbContext.JobSources.Add(new JobSource
                {
                    Id = Guid.NewGuid(),
                    Name = site.Name,
                    BaseUrl = baseUrl,
                    Method = "Scrapling",
                    Parser = parser,
                    Category = category,
                    Enabled = enabled,
                    CrawlFrequencyMinutes = frequency,
                    TermsCheckedAt = termsCheckedAt,
                    CreatedAt = now,
                    UpdatedAt = now
                });
                inserted++;
            }
        }

        // Anything absent from the registry is retired: disabled AND stamped
        // with retired_at. retired_at is what distinguishes it from a source the
        // registry deliberately keeps disabled, so the health report can show
        // only the current registry while the historical rows stay in place.
        var retired = 0;
        foreach (var entity in existing.Where(source => !registryNames.Contains(source.Name) && !source.RetiredAt.HasValue))
        {
            entity.Enabled = false;
            entity.RetiredAt = now;
            entity.UpdatedAt = now;
            retired++;
        }

        if (inserted + updated + retired + revived > 0)
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }

        logger.LogInformation(
            "Source registry sync: {Inserted} inserted, {Updated} updated, {Retired} retired, {Revived} revived, {Total} registry entries",
            inserted,
            updated,
            retired,
            revived,
            sites.Count);
        return new SourceSyncResult(inserted, updated, retired, revived, sites.Count);
    }
}
