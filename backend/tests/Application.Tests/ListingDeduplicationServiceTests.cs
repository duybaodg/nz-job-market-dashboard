using Application.Deduplication;
using Domain.Entities;
using FluentAssertions;
using Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Application.Tests;

public sealed class ListingDeduplicationServiceTests
{
    private static readonly Guid Employer = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly DateTime Base = new(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task Run_groups_cross_board_duplicates_and_publishes_unique_vacancies()
    {
        await using var dbContext = CreateDbContext();
        var first = AddJob(dbContext, "SEEK NZ", "Senior Developer", salary: "$120,000", posted: Base);
        var second = AddJob(dbContext, "Trade Me Jobs", "Senior Developer", salary: "$120,000", posted: Base.AddDays(2));
        var unrelated = AddJob(dbContext, "Xero", "Platform Engineer", salary: null, posted: Base.AddDays(1));
        await dbContext.SaveChangesAsync();

        var summary = await new ListingDeduplicationService(dbContext).RunAsync("test", CancellationToken.None);

        summary.Active.Should().BeTrue();
        summary.Groups.Should().Be(1);
        summary.ListingsMerged.Should().Be(1);
        summary.UniqueVacancies.Should().Be(2);
        first.DuplicateGroupId.Should().NotBeNull();
        first.DuplicateGroupId.Should().Be(second.DuplicateGroupId);
        unrelated.DuplicateGroupId.Should().BeNull();

        var group = await dbContext.DuplicateGroups.SingleAsync();
        group.CanonicalListingId.Should().Be(first.Id, "the earliest posted listing is canonical");
        group.MemberCount.Should().Be(2);
        group.MatchMethod.Should().Be(DeduplicationMatchMethods.TitleEmployerSalaryWindow);

        (await dbContext.Jobs.CountAsync()).Should().Be(3, "source rows are never deleted");
    }

    [Fact]
    public async Task Listings_from_the_same_source_are_not_grouped()
    {
        await using var dbContext = CreateDbContext();
        AddJob(dbContext, "SEEK NZ", "Senior Developer", salary: "$120,000", posted: Base);
        AddJob(dbContext, "SEEK NZ", "Senior Developer", salary: "$120,000", posted: Base);
        await dbContext.SaveChangesAsync();

        var summary = await new ListingDeduplicationService(dbContext).RunAsync("test", CancellationToken.None);

        summary.Groups.Should().Be(0);
        summary.UniqueVacancies.Should().Be(2);
    }

    [Fact]
    public async Task Legitimate_twin_openings_with_different_descriptions_stay_separate()
    {
        await using var dbContext = CreateDbContext();
        AddJob(
            dbContext,
            "SEEK NZ",
            "Graduate Developer",
            salary: null,
            posted: Base,
            description: "Build fraud detection services for our payments platform using C# and Kafka.");
        AddJob(
            dbContext,
            "Trade Me Jobs",
            "Graduate Developer",
            salary: null,
            posted: Base,
            description: "Model vessel telemetry data for our marine engineering group in Tauranga.");
        await dbContext.SaveChangesAsync();

        var summary = await new ListingDeduplicationService(dbContext).RunAsync("test", CancellationToken.None);

        summary.Groups.Should().Be(0);
        summary.ListingsMerged.Should().Be(0);
        summary.ReviewCandidates.Should().Be(1);
        var candidate = await dbContext.DuplicateReviewCandidates.SingleAsync();
        candidate.Reason.Should().Be("gates-failed");
    }

    [Fact]
    public async Task More_than_four_identical_listings_are_review_candidates_not_a_group()
    {
        await using var dbContext = CreateDbContext();
        for (var index = 0; index < 5; index++)
        {
            AddJob(
                dbContext,
                index % 2 == 0 ? "SEEK NZ" : "Trade Me Jobs",
                "Graduate Developer",
                salary: "$70,000",
                posted: Base.AddDays(index % 2));
        }

        await dbContext.SaveChangesAsync();

        var summary = await new ListingDeduplicationService(dbContext).RunAsync("test", CancellationToken.None);

        summary.Groups.Should().Be(0);
        summary.ReviewCandidates.Should().BeGreaterThan(0);
        (await dbContext.DuplicateReviewCandidates.ToListAsync())
            .Should().OnlyContain(candidate => candidate.Reason == "group-cap-exceeded");
    }

    [Fact]
    public async Task Expiring_the_canonical_repoints_to_the_next_oldest_member()
    {
        await using var dbContext = CreateDbContext();
        var canonical = AddJob(dbContext, "SEEK NZ", "Senior Developer", salary: "$120,000", posted: Base);
        var second = AddJob(dbContext, "Trade Me Jobs", "Senior Developer", salary: "$120,000", posted: Base.AddDays(1));
        var third = AddJob(dbContext, "LinkedIn", "Senior Developer", salary: "$120,000", posted: Base.AddDays(2));
        await dbContext.SaveChangesAsync();
        var service = new ListingDeduplicationService(dbContext);

        var first = await service.RunAsync("test", CancellationToken.None);
        first.Groups.Should().Be(1);
        var group = await dbContext.DuplicateGroups.SingleAsync();
        group.CanonicalListingId.Should().Be(canonical.Id);

        canonical.IsActive = false;
        canonical.ExpiredAt = Base.AddDays(10);
        await dbContext.SaveChangesAsync();

        var secondRun = await service.RunAsync("test", CancellationToken.None);

        secondRun.Groups.Should().Be(1);
        secondRun.UniqueVacancies.Should().Be(1);
        var reloaded = await dbContext.DuplicateGroups.SingleAsync();
        reloaded.CanonicalListingId.Should().Be(second.Id, "the canonical expired and the next-oldest live member takes over");
        reloaded.MemberCount.Should().Be(2);
        canonical.DuplicateGroupId.Should().BeNull();
        second.DuplicateGroupId.Should().Be(reloaded.Id);
        third.DuplicateGroupId.Should().Be(reloaded.Id);
    }

    [Fact]
    public async Task Salary_mismatch_blocks_a_merge_even_with_identical_descriptions()
    {
        await using var dbContext = CreateDbContext();
        var description = string.Join(' ', Enumerable.Range(0, 100).Select(index => $"token{index}"));
        AddJob(dbContext, "SEEK NZ", "Senior Developer", salary: "$120,000", posted: Base, description: description);
        AddJob(dbContext, "Trade Me Jobs", "Senior Developer", salary: "$150,000", posted: Base, description: description);
        await dbContext.SaveChangesAsync();

        var summary = await new ListingDeduplicationService(dbContext).RunAsync("test", CancellationToken.None);

        summary.Groups.Should().Be(0);
        (await dbContext.DuplicateReviewCandidates.SingleAsync()).Reason.Should().Be("salary-conflict");
    }

    [Fact]
    public async Task Group_report_exposes_the_canonical_and_every_merged_listing()
    {
        await using var dbContext = CreateDbContext();
        AddJob(dbContext, "SEEK NZ", "Senior Developer", salary: "$120,000", posted: Base);
        AddJob(dbContext, "Trade Me Jobs", "Senior Developer", salary: "$120,000", posted: Base.AddDays(2));
        await dbContext.SaveChangesAsync();
        var service = new ListingDeduplicationService(dbContext);
        await service.RunAsync("test", CancellationToken.None);

        var report = await service.GetGroupsAsync(CancellationToken.None);

        report.Deduplicated.Should().BeTrue();
        report.UniqueVacancies.Should().Be(1);
        report.Groups.Should().ContainSingle();
        report.Groups[0].Members.Should().HaveCount(2);
        report.Groups[0].Members.Count(member => member.IsCanonical).Should().Be(1);
    }

    private static Job AddJob(
        ApplicationDbContext dbContext,
        string source,
        string title,
        string? salary,
        DateTime posted,
        string? description = null)
    {
        var job = new Job
        {
            Id = Guid.NewGuid(),
            Source = source,
            Title = title,
            Company = $"{title} Employer",
            Region = "Auckland",
            SalaryText = salary,
            EmployerCanonicalId = Employer,
            DescriptionText = description,
            IsActive = true,
            PostedDate = posted,
            ClosingDate = posted.AddDays(30),
            FirstSeenAt = posted,
            LastSeenAt = posted.AddDays(1),
            CreatedAt = posted,
            UpdatedAt = posted,
            Url = $"https://example.com/{Guid.NewGuid()}",
            ContentHash = Guid.NewGuid().ToString()
        };
        dbContext.Jobs.Add(job);
        return job;
    }

    private static ApplicationDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new ApplicationDbContext(options);
    }
}
