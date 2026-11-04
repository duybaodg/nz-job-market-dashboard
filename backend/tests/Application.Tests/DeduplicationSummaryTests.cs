using Application.Deduplication;
using Domain.Entities;
using FluentAssertions;
using Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Application.Tests;

public sealed class DeduplicationSummaryTests
{
    private static readonly Guid Employer = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly DateTime Now = new(2026, 11, 4, 8, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Vacancy_identity_holds_for_singleton_pair_and_larger_groups()
    {
        // One singleton listing, one pair, and one valid group of four.
        var population = DeduplicationPopulation.FromActiveGroupSizes(7, [1, 2, 4]);

        population.ActiveListings.Should().Be(7);
        population.Groups.Should().Be(2, "a singleton is not a vacancy group");
        population.ListingsInGroups.Should().Be(6, "1 + 2 + 4 listings, excluding the singleton");
        population.ListingsMerged.Should().Be(4, "(2-1) + (4-1)");
        population.UniqueVacancies.Should().Be(3);
        population.UniqueVacancies.Should().Be(
            population.ActiveListings - population.ListingsMerged,
            "vacancies = listings - sum(group_size - 1)");
    }

    [Fact]
    public void Empty_population_has_no_fabricated_merge_or_vacancy()
    {
        var population = DeduplicationPopulation.FromActiveGroupSizes(0, []);

        population.Groups.Should().Be(0);
        population.ListingsInGroups.Should().Be(0);
        population.ListingsMerged.Should().Be(0);
        population.UniqueVacancies.Should().Be(0);
    }

    [Fact]
    public async Task Summary_recomputes_vacancies_from_the_current_population_not_a_stale_run()
    {
        await using var context = CreateDbContext();
        var group = new DuplicateGroup
        {
            Id = Guid.NewGuid(),
            CanonicalListingId = Guid.NewGuid(),
            MatchMethod = "test",
            MatchVersion = "v1",
            Confidence = "high",
            CreatedAt = Now.AddDays(-1),
            UpdatedAt = Now.AddDays(-1),
            MemberCount = 2
        };
        context.DuplicateGroups.Add(group);
        var first = AddJob(context, active: true);
        var second = AddJob(context, active: true);
        first.DuplicateGroupId = group.Id;
        second.DuplicateGroupId = group.Id;
        group.CanonicalListingId = first.Id;
        AddJob(context, active: true);
        // The last validated run saw 1,000 active listings. Only 3 remain active now.
        context.DeduplicationRuns.Add(new DeduplicationRun
        {
            Id = Guid.NewGuid(),
            StartedAt = Now.AddDays(-4),
            FinishedAt = Now.AddDays(-4),
            Status = "Completed",
            IsValidated = true,
            MatchVersion = "v1",
            ListingsEvaluated = 1000,
            GroupsFound = 1,
            ListingsMerged = 1,
            UniqueVacancies = 999
        });
        await context.SaveChangesAsync();

        var summary = await new ListingDeduplicationService(context).GetSummaryAsync(CancellationToken.None);

        summary.Active.Should().BeTrue();
        summary.ActiveListings.Should().Be(3);
        summary.Groups.Should().Be(1);
        summary.ListingsInGroups.Should().Be(2);
        summary.ListingsMerged.Should().Be(1);
        summary.UniqueVacancies.Should().Be(2, "3 active listings minus one merged duplicate row");
        summary.UniqueVacancies.Should().Be(summary.ActiveListings - summary.ListingsMerged);
        summary.LastValidatedAtUtc.Should().Be(Now.AddDays(-4));
        summary.Status.Should().Be("validated");
    }

    [Fact]
    public async Task Summary_reports_not_run_instead_of_a_successful_zero_merge()
    {
        await using var context = CreateDbContext();
        AddJob(context, active: true);
        AddJob(context, active: true);
        await context.SaveChangesAsync();

        var summary = await new ListingDeduplicationService(context).GetSummaryAsync(CancellationToken.None);

        summary.Active.Should().BeFalse();
        summary.UniqueVacancies.Should().BeNull();
        summary.Groups.Should().Be(0);
        summary.ListingsMerged.Should().Be(0);
        summary.Status.Should().Be("not-run");
        summary.UnavailableReason.Should().Contain("has not run");
    }

    private static Job AddJob(ApplicationDbContext context, bool active)
    {
        var job = new Job
        {
            Id = Guid.NewGuid(),
            Source = "SEEK NZ",
            Title = "Senior Developer",
            EmployerCanonicalId = Employer,
            IsActive = active,
            FirstSeenAt = Now.AddDays(-2),
            LastSeenAt = Now,
            CreatedAt = Now.AddDays(-2),
            UpdatedAt = Now,
            Url = $"https://example.com/{Guid.NewGuid()}",
            ContentHash = Guid.NewGuid().ToString()
        };
        context.Jobs.Add(job);
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
