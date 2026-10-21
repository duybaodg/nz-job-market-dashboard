using Application.Crawling;
using Microsoft.EntityFrameworkCore;

namespace Infrastructure.Persistence;

/// <summary>
/// Serialises the API and worker startup work (migrations + registry sync) with
/// a PostgreSQL session advisory lock. On a fresh deploy both processes start at
/// once and previously could both insert the same registry rows or race the
/// migration. The lock is released by the same session in <c>finally</c> and by
/// PostgreSQL automatically if the process dies.
/// </summary>
public static class DatabaseStartup
{
    /// <summary>Arbitrary stable key shared by the API and worker.</summary>
    public const long AdvisoryLockKey = 7_314_159_265L;

    public static async Task MigrateAndSyncAsync(
        ApplicationDbContext dbContext,
        ISourceRegistrySynchronizer synchronizer,
        CancellationToken cancellationToken)
    {
        if (!dbContext.Database.IsNpgsql())
        {
            // Unit-test / non-Postgres provider: nothing to serialise.
            await dbContext.Database.MigrateAsync(cancellationToken);
            await synchronizer.SyncAsync(cancellationToken);
            return;
        }

        await dbContext.Database.OpenConnectionAsync(cancellationToken);
        try
        {
            await dbContext.Database.ExecuteSqlRawAsync(
                "SELECT pg_advisory_lock({0})",
                new object[] { AdvisoryLockKey },
                cancellationToken);

            await dbContext.Database.MigrateAsync(cancellationToken);
            await synchronizer.SyncAsync(cancellationToken);
        }
        finally
        {
            try
            {
                await dbContext.Database.ExecuteSqlRawAsync(
                    "SELECT pg_advisory_unlock({0})",
                    new object[] { AdvisoryLockKey },
                    CancellationToken.None);
            }
            catch
            {
                // The connection close below releases the lock even if unlock fails.
            }

            await dbContext.Database.CloseConnectionAsync();
        }
    }
}
