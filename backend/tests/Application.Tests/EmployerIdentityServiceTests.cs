using Application.Employers;
using Domain.Entities;
using FluentAssertions;
using Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Application.Tests;

public sealed class EmployerIdentityServiceTests
{
    private static readonly DateTime Now = new(2026, 10, 15, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task Exact_case_and_whitespace_normalised_name_maps_to_the_canonical()
    {
        await using var dbContext = CreateDbContext();
        var xero = AddEmployer(dbContext, "Xero", EmployerType.DirectEmployer);
        var job = AddJob(dbContext, "Xero", "company", "  xero  ");
        await dbContext.SaveChangesAsync();

        var result = await new EmployerIdentityService(dbContext).MatchJobsAsync("test", CancellationToken.None);

        result.Matched.Should().Be(1);
        result.Unmatched.Should().Be(0);
        job.EmployerCanonicalId.Should().Be(xero.Id);
    }

    [Fact]
    public async Task Alias_addition_immediately_rematches_affected_jobs()
    {
        await using var dbContext = CreateDbContext();
        var employer = AddEmployer(dbContext, "Fisher & Paykel", EmployerType.DirectEmployer);
        var job = AddJob(dbContext, "Fisher & Paykel", "company", "F&P");
        await dbContext.SaveChangesAsync();
        var service = new EmployerIdentityService(dbContext);

        await service.MatchJobsAsync("test", CancellationToken.None);
        job.EmployerCanonicalId.Should().BeNull("F&P is not the canonical spelling yet");

        var updated = await service.AddAliasAsync(
            employer.Id,
            new AddEmployerAliasRequest("F&P", "tester"),
            CancellationToken.None);

        updated.Should().NotBeNull();
        updated!.Aliases.Should().Contain("F&P");
        job.EmployerCanonicalId.Should().Be(employer.Id, "adding an alias rematches immediately");
        (await dbContext.EmployerAuditEvents.CountAsync(audit => audit.Action == "AliasAdded")).Should().Be(1);
    }

    [Fact]
    public async Task Seed_types_reviewed_agencies_as_agencies_and_the_rest_as_direct_employers()
    {
        await using var dbContext = CreateDbContext();
        AddJob(dbContext, "Beyond Recruitment", "recruiter", "Beyond Recruitment");
        AddJob(dbContext, "Xero", "company", "Xero");
        await dbContext.SaveChangesAsync();
        var service = new EmployerIdentityService(dbContext);

        var result = await service.SeedFromCompaniesAsync("tester", CancellationToken.None);

        result.Seeded.Should().BeTrue();
        var beyond = await dbContext.Employers.SingleAsync(employer => employer.NormalizedName == "beyond recruitment");
        beyond.Type.Should().Be(EmployerType.Agency);
        var xero = await dbContext.Employers.SingleAsync(employer => employer.NormalizedName == "xero");
        xero.Type.Should().Be(EmployerType.DirectEmployer);

        var second = await service.SeedFromCompaniesAsync("tester", CancellationToken.None);
        second.Seeded.Should().BeFalse("seeding is a one-off bootstrapping step");
    }

    [Fact]
    public async Task Seed_waits_for_the_first_company_then_creates_it_on_a_later_call()
    {
        await using var dbContext = CreateDbContext();
        var service = new EmployerIdentityService(dbContext);

        var first = await service.SeedFromCompaniesAsync("tester", CancellationToken.None);

        first.Seeded.Should().BeFalse("an empty database has no company rows to seed from");
        (await dbContext.Employers.CountAsync()).Should().Be(0, "agencies are not seeded before the first company");
        (await dbContext.EmployerAuditEvents.CountAsync(audit => audit.Action == "EmployerSeedFromCompanies"))
            .Should().Be(0, "no one-shot marker is written when nothing was produced");

        AddJob(dbContext, "Xero", "company", "Xero");
        await dbContext.SaveChangesAsync();

        var second = await service.SeedFromCompaniesAsync("tester", CancellationToken.None);

        second.Seeded.Should().BeTrue();
        (await dbContext.Employers.CountAsync(employer => employer.NormalizedName == "xero")).Should().Be(1);
        (await dbContext.EmployerAuditEvents.CountAsync(audit => audit.Action == "EmployerSeedFromCompanies"))
            .Should().Be(1);
    }

    [Fact]
    public async Task Unmatched_disclosed_names_stay_null_and_are_reported()
    {
        await using var dbContext = CreateDbContext();
        AddEmployer(dbContext, "Xero", EmployerType.DirectEmployer);
        var job = AddJob(dbContext, "Trade Me Jobs", "job-board", "Weta Digital");
        await dbContext.SaveChangesAsync();
        var service = new EmployerIdentityService(dbContext);

        var result = await service.MatchJobsAsync("test", CancellationToken.None);
        result.Unmatched.Should().Be(1);
        job.EmployerCanonicalId.Should().BeNull("an unmatched name creates nothing");

        var report = await service.GetReportAsync(CancellationToken.None);
        report.UnmatchedNames.Should().ContainSingle(name => name.Name == "Weta Digital" && name.JobCount == 1);
        (await dbContext.Employers.CountAsync()).Should().Be(1, "no canonical is auto-created");
    }

    [Fact]
    public async Task Similar_but_different_names_are_never_fuzzy_merged()
    {
        await using var dbContext = CreateDbContext();
        AddEmployer(dbContext, "Fisher & Paykel", EmployerType.DirectEmployer);
        var noAmpersand = AddJob(dbContext, "Fisher & Paykel", "company", "Fisher Paykel");
        var misspelled = AddJob(dbContext, "Fisher & Paykel", "company", "Fischer & Paykel");
        await dbContext.SaveChangesAsync();
        var service = new EmployerIdentityService(dbContext);

        var result = await service.MatchJobsAsync("test", CancellationToken.None);

        result.Matched.Should().Be(0);
        result.Unmatched.Should().Be(2);
        noAmpersand.EmployerCanonicalId.Should().BeNull();
        misspelled.EmployerCanonicalId.Should().BeNull();
        (await dbContext.EmployerAliases.CountAsync()).Should().Be(0, "no alias is invented by a match pass");
    }

    [Fact]
    public async Task Agency_only_listings_never_receive_a_canonical_employer()
    {
        await using var dbContext = CreateDbContext();
        AddEmployer(dbContext, "Beyond Recruitment", EmployerType.Agency);
        var job = AddJob(dbContext, "Beyond Recruitment", "recruiter", "Beyond Recruitment");
        await dbContext.SaveChangesAsync();

        var result = await new EmployerIdentityService(dbContext).MatchJobsAsync("test", CancellationToken.None);

        result.Unmatched.Should().Be(0, "agency-only listings are not unmatched employer names, they are intentionally anonymous");
        job.EmployerCanonicalId.Should().BeNull();
    }

    [Fact]
    public async Task Removing_an_alias_returns_jobs_to_the_unknown_bucket()
    {
        await using var dbContext = CreateDbContext();
        var employer = AddEmployer(dbContext, "Fisher & Paykel", EmployerType.DirectEmployer);
        var job = AddJob(dbContext, "Fisher & Paykel", "company", "F&P");
        dbContext.EmployerAliases.Add(new EmployerAlias
        {
            Id = Guid.NewGuid(),
            EmployerId = employer.Id,
            Alias = "F&P",
            NormalizedAlias = "f&p",
            CreatedAt = Now,
            CreatedBy = "tester"
        });
        await dbContext.SaveChangesAsync();
        var service = new EmployerIdentityService(dbContext);
        await service.MatchJobsAsync("test", CancellationToken.None);
        job.EmployerCanonicalId.Should().Be(employer.Id);

        var removed = await service.RemoveAliasAsync(employer.Id, "F&P", "tester", CancellationToken.None);

        removed.Should().BeTrue();
        job.EmployerCanonicalId.Should().BeNull("removing the alias revises the decision");
    }

    private static Employer AddEmployer(ApplicationDbContext dbContext, string name, EmployerType type)
    {
        var employer = new Employer
        {
            Id = Guid.NewGuid(),
            CanonicalName = name,
            NormalizedName = EmployerNameNormaliser.Normalise(name),
            Type = type,
            CreatedAt = Now
        };
        dbContext.Employers.Add(employer);
        return employer;
    }

    private static Job AddJob(ApplicationDbContext dbContext, string source, string category, string? company)
    {
        dbContext.JobSources.Add(new JobSource
        {
            Id = Guid.NewGuid(),
            Name = source,
            Method = "Scraping",
            Parser = "generic",
            Category = category,
            BaseUrl = $"https://{source.Replace(" ", "").ToLowerInvariant()}.example",
            CreatedAt = Now,
            UpdatedAt = Now
        });
        var job = new Job
        {
            Id = Guid.NewGuid(),
            Source = source,
            Title = "Software Engineer",
            Company = company,
            Url = $"https://example.com/{Guid.NewGuid()}",
            ContentHash = Guid.NewGuid().ToString(),
            IsActive = true,
            FirstSeenAt = Now.AddDays(-2),
            LastSeenAt = Now,
            CreatedAt = Now.AddDays(-2),
            UpdatedAt = Now
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
