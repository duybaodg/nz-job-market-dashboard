using Application.Employers;
using Domain.Entities;
using FluentAssertions;
using Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Application.Tests;

public sealed class EmployerReviewServiceTests
{
    private static readonly DateTime Now = new(2026, 11, 5, 10, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task Queue_groups_similar_names_without_creating_canonicals_or_aliases()
    {
        await using var dbContext = CreateDbContext();
        var datacom = AddEmployer(dbContext, "Datacom", "datacom");
        AddJob(dbContext, "Datacom Systems Ltd", null);
        AddJob(dbContext, "Datacom NZ", null);
        AddJob(dbContext, "Xero", AddEmployer(dbContext, "Xero", "xero").Id);
        await dbContext.SaveChangesAsync();

        var service = CreateService(dbContext);
        var queue = await service.GetQueueAsync(CancellationToken.None);

        queue.UnmatchedNameCount.Should().Be(2);
        queue.TotalDecisionCount.Should().Be(0);
        queue.PendingNameCount.Should().Be(2);
        queue.Groups.Should().ContainSingle();
        queue.Groups[0].GroupKey.Should().Be("datacom");
        queue.Groups[0].Names.Select(name => name.AdvertisedName)
            .Should().BeEquivalentTo("Datacom Systems Ltd", "Datacom NZ");
        (await dbContext.EmployerAliases.CountAsync()).Should().Be(0);
        (await dbContext.EmployerAliasReviews.CountAsync()).Should().Be(0);
        (await dbContext.Employers.CountAsync()).Should().Be(2, "review must not create canonicals");
        datacom.NormalizedName.Should().Be("datacom");
    }

    [Fact]
    public async Task An_explicit_match_creates_a_curated_alias_and_preserves_the_advertised_name()
    {
        await using var dbContext = CreateDbContext();
        var datacom = AddEmployer(dbContext, "Datacom", "datacom");
        var jobId = AddJob(dbContext, "Datacom Systems Ltd", null);
        AddJob(dbContext, "Datacom NZ", null);
        await dbContext.SaveChangesAsync();

        var service = CreateService(dbContext);
        var result = await service.SaveDecisionsAsync(
            new RecordEmployerReviewDecisionsRequest(
                [new EmployerReviewDecisionInput("Datacom Systems Ltd", "matched", datacom.Id, "Exact reviewed legal-name variant.")],
                "tester"),
            CancellationToken.None);

        result.Created.Should().Be(1);
        result.AliasesCreated.Should().Be(1);
        result.ListingsRematched.Should().Be(1);
        result.Queue.TotalDecisionCount.Should().Be(1);

        var job = await dbContext.Jobs.SingleAsync(entity => entity.Id == jobId);
        job.EmployerCanonicalId.Should().Be(datacom.Id);
        var alias = await dbContext.EmployerAliases.SingleAsync();
        alias.Alias.Should().Be("Datacom Systems Ltd");
        alias.CreatedBy.Should().Be("tester");
        var decision = await dbContext.EmployerAliasReviews.SingleAsync();
        decision.AdvertisedName.Should().Be("Datacom Systems Ltd");
        decision.Decision.Should().Be("matched");
        decision.CanonicalEmployerId.Should().Be(datacom.Id);
        // The still-unmatched similar name is not auto-merged.
        (await dbContext.Jobs.CountAsync(entity => entity.EmployerCanonicalId == null)).Should().Be(1);
    }

    [Fact]
    public async Task Kept_separate_and_agency_client_decisions_never_create_aliases()
    {
        await using var dbContext = CreateDbContext();
        var client = AddEmployer(dbContext, "Kernel", "kernel");
        AddJob(dbContext, "Recruit Co", null);
        AddJob(dbContext, "Agency Brand", null);
        await dbContext.SaveChangesAsync();

        var service = CreateService(dbContext);
        var result = await service.SaveDecisionsAsync(
            new RecordEmployerReviewDecisionsRequest(
                [
                    new EmployerReviewDecisionInput("Recruit Co", "kept-separate", null, "Distinct employer, no canonical yet."),
                    new EmployerReviewDecisionInput("Agency Brand", "agency-client", client.Id, "Agency advertises for Kernel; keep the distinction.")
                ],
                "tester"),
            CancellationToken.None);

        result.Created.Should().Be(2);
        result.AliasesCreated.Should().Be(0);
        result.ListingsRematched.Should().Be(0);
        (await dbContext.EmployerAliases.CountAsync()).Should().Be(0);
        var decisions = await dbContext.EmployerAliasReviews.OrderBy(review => review.AdvertisedName).ToListAsync();
        decisions.Select(review => review.Decision).Should().Equal("agency-client", "kept-separate");
        decisions.Single(review => review.Decision == "agency-client").CanonicalEmployerId.Should().Be(client.Id);
        (await dbContext.Jobs.CountAsync(job => job.EmployerCanonicalId != null)).Should().Be(0);
    }

    [Fact]
    public async Task Saving_the_same_decision_twice_updates_without_duplicating()
    {
        await using var dbContext = CreateDbContext();
        var datacom = AddEmployer(dbContext, "Datacom", "datacom");
        AddJob(dbContext, "Datacom Systems Ltd", null);
        await dbContext.SaveChangesAsync();

        var service = CreateService(dbContext);
        var request = new RecordEmployerReviewDecisionsRequest(
            [new EmployerReviewDecisionInput("Datacom Systems Ltd", "matched", datacom.Id, null)],
            "tester");
        await service.SaveDecisionsAsync(request, CancellationToken.None);
        var second = await service.SaveDecisionsAsync(request, CancellationToken.None);

        second.Created.Should().Be(0);
        second.Updated.Should().Be(1);
        second.AliasesCreated.Should().Be(0);
        (await dbContext.EmployerAliasReviews.CountAsync()).Should().Be(1);
        (await dbContext.EmployerAliases.CountAsync()).Should().Be(1);
    }

    private static EmployerReviewService CreateService(ApplicationDbContext dbContext) =>
        new(dbContext, new EmployerIdentityService(dbContext), new TestClock(Now));

    private static Employer AddEmployer(ApplicationDbContext dbContext, string name, string normalized)
    {
        var employer = new Employer
        {
            Id = Guid.NewGuid(),
            CanonicalName = name,
            NormalizedName = normalized,
            Type = EmployerType.DirectEmployer,
            CreatedAt = Now
        };
        dbContext.Employers.Add(employer);
        return employer;
    }

    private static Guid AddJob(ApplicationDbContext dbContext, string company, Guid? canonicalId)
    {
        var id = Guid.NewGuid();
        dbContext.JobSources.Add(new JobSource
        {
            Id = Guid.NewGuid(),
            Name = "Datacom",
            BaseUrl = "https://datacom.example",
            Method = "Scraping",
            Parser = "generic",
            Category = "company",
            Enabled = true,
            CreatedAt = Now,
            UpdatedAt = Now
        });
        dbContext.Jobs.Add(new Job
        {
            Id = id,
            Source = "Datacom",
            Title = "Software Engineer",
            Company = company,
            Url = $"https://example.com/{id}",
            ContentHash = id.ToString(),
            IsActive = true,
            EmployerCanonicalId = canonicalId,
            FirstSeenAt = Now.AddDays(-1),
            LastSeenAt = Now,
            CreatedAt = Now.AddDays(-1),
            UpdatedAt = Now
        });
        return id;
    }

    private static ApplicationDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new ApplicationDbContext(options);
    }
}
