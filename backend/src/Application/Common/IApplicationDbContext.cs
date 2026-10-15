using Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace Application.Common;

public interface IApplicationDbContext
{
    DbSet<Job> Jobs { get; }
    DbSet<JobSkill> JobSkills { get; }
    DbSet<RawJobPage> RawJobPages { get; }
    DbSet<CrawlRun> CrawlRuns { get; }
    DbSet<JobSource> JobSources { get; }
    DbSet<ListingSnapshot> ListingSnapshots { get; }
    DbSet<ListingSnapshotRun> ListingSnapshotRuns { get; }
    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
}
