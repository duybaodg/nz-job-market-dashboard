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
    DbSet<Employer> Employers { get; }
    DbSet<EmployerAlias> EmployerAliases { get; }
    DbSet<EmployerAuditEvent> EmployerAuditEvents { get; }
    DbSet<DuplicateGroup> DuplicateGroups { get; }
    DbSet<DuplicateReviewCandidate> DuplicateReviewCandidates { get; }
    DbSet<DeduplicationRun> DeduplicationRuns { get; }
    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
}
