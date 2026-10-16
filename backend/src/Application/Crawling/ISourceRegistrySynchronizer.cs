using Application.Crawling;

namespace Application.Crawling;

/// <summary>
/// Reconciles the database <c>job_sources</c> table with the crawler registry
/// (<c>crawler/sources/sites.json</c>). The registry is the source of truth for
/// which sites we crawl and how, so edits there take effect on restart without
/// hand-writing a migration.
/// </summary>
public interface ISourceRegistrySynchronizer
{
    Task<SourceSyncResult> SyncAsync(CancellationToken cancellationToken);
}

public sealed record SourceSyncResult(
    int Inserted,
    int Updated,
    int Retired,
    int Revived,
    int RegistryEntries);
