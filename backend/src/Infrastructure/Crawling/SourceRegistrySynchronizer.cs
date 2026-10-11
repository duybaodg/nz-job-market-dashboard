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
            return new SourceSyncResult(0, 0, 0, 0);
        }

        var sites = SourceRegistryReader.Load(path);
        if (sites.Count == 0)
        {
            logger.LogWarning("Registry {Path} has no 'sites' entries; skipping sync.", path);
            return new SourceSyncResult(0, 0, 0, 0);
        }

        var existing = await dbContext.JobSources.ToListAsync(cancellationToken);
        var byName = existing.ToDictionary(source => source.Name, StringComparer.OrdinalIgnoreCase);
        var registryNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var now = DateTime.UtcNow;
        var inserted = 0;
        var updated = 0;

        foreach (var site in sites)
        {
            if (string.IsNullOrWhiteSpace(site.Name))
            {
                continue;
            }

            registryNames.Add(site.Name);
            var parser = string.IsNullOrWhiteSpace(site.Parser) ? "generic" : site.Parser!;
            var baseUrl = site.JobsUrl ?? site.Homepage;
            var enabled = site.Enabled ?? true;
            var frequency = site.FrequencyMinutes is > 0 ? site.FrequencyMinutes.Value : 1440;
            // robots.txt disallows crawling for some sites, so leave those unapproved.
            var termsCheckedAt = site.RobotsAllows == false ? (DateTime?)null : now;

            if (byName.TryGetValue(site.Name, out var entity))
            {
                var changed =
                    entity.BaseUrl != baseUrl ||
                    entity.Parser != parser ||
                    entity.Enabled != enabled ||
                    entity.CrawlFrequencyMinutes != frequency ||
                    !string.Equals(entity.Method, "Scrapling", StringComparison.Ordinal);

                entity.BaseUrl = baseUrl;
                entity.Parser = parser;
                entity.Enabled = enabled;
                entity.Method = "Scrapling";
                entity.CrawlFrequencyMinutes = frequency;
                entity.TermsCheckedAt ??= termsCheckedAt;
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
                    Enabled = enabled,
                    CrawlFrequencyMinutes = frequency,
                    TermsCheckedAt = termsCheckedAt,
                    CreatedAt = now,
                    UpdatedAt = now
                });
                inserted++;
            }
        }

        // Anything enabled in the database but absent from the registry is retired.
        var disabled = 0;
        foreach (var entity in existing.Where(source => source.Enabled && !registryNames.Contains(source.Name)))
        {
            entity.Enabled = false;
            entity.UpdatedAt = now;
            disabled++;
        }

        if (inserted + updated + disabled > 0)
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }

        logger.LogInformation(
            "Source registry sync: {Inserted} inserted, {Updated} updated, {Disabled} retired, {Total} registry entries",
            inserted,
            updated,
            disabled,
            sites.Count);
        return new SourceSyncResult(inserted, updated, disabled, sites.Count);
    }
}
