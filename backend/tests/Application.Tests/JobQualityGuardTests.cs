using Application.Deduplication;
using Application.Jobs;
using Application.Normalisation;
using Domain.Entities;
using FluentAssertions;
using Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Application.Tests;

public sealed class JobQualityGuardTests
{
    [Theory]
    [InlineData("Careers - BNZ", "BNZ")]
    [InlineData("3 jobs found for engineering", "Randstad NZ")]
    [InlineData("Home | New Cops | New Zealand Police", "NZ Police")]
    [InlineData("Working here at One NZ - Careers", "One NZ")]
    [InlineData("Jobspace - Launch Your New Career", "MyJobSpace")]
    [InlineData("Register as a job seeker", "Student Job Search")]
    [InlineData("745 Software Developer jobs in New Zealand", "SEEK NZ")]
    [InlineData("Your Software Engineering Recruitment Specialists | Potentia", "Potentia")]
    [InlineData("Fonterra", "Fonterra")]
    [InlineData("Technology and engineering careers", "ANZ NZ")]
    [InlineData("Developer Opportunities", "Talent Vault")]
    [InlineData("Graduate Jobs and Graduate Programmes (74 open now!)", "GradConnection NZ")]
    [InlineData("", "Any")]
    public void IsLowQualityTitle_flags_hub_and_landing_pages(string title, string source)
    {
        JobQualityGuard.IsLowQualityTitle(title, source).Should().BeTrue();
    }

    [Theory]
    [InlineData("Senior Software Engineer", "Acme")]
    [InlineData("Workday Reporting Specialist", "Salt NZ")]
    [InlineData("IT Help Desk Technician", "SEEK NZ")]
    [InlineData("Fonterra", "Some Other Source")]
    public void IsLowQualityTitle_keeps_real_jobs(string title, string source)
    {
        JobQualityGuard.IsLowQualityTitle(title, source).Should().BeFalse();
    }
}

public sealed class JobQualityGuardRunTests
{
    [Fact]
    public async Task RunAsync_removes_junk_and_normalises_regions()
    {
        await using var dbContext = CreateDbContext();
        dbContext.JobSources.Add(new JobSource
        {
            Id = Guid.NewGuid(),
            Name = "Acme",
            BaseUrl = "https://acme.example/careers/",
            Method = "Scrapling",
            Parser = "generic"
        });
        dbContext.Jobs.AddRange(
            CreateJob("404 Error: Page Not Found", "https://acme.example/jobs/missing"),
            CreateJob(
                "Recruitment and Executive Search | Tribe Group Find a Job",
                "https://acme.example/jobs/hub"),
            CreateJob(
                "Service Desk Support Analyst",
                "https://acme.example/jobs/1",
                description: "Service Desk Support Analyst Job in Christchurch Central, Canterbury - SEEK"));
        await dbContext.SaveChangesAsync();

        var guard = CreateGuard(dbContext);
        var result = await guard.RunAsync(CancellationToken.None);

        result.JunkRemoved.Should().Be(2);
        result.RegionsNormalised.Should().BeGreaterThan(0);
        dbContext.Jobs.Should().ContainSingle();
        dbContext.Jobs.Single().Region.Should().Be("Canterbury");
    }

    [Fact]
    public async Task RunAsync_is_idempotent()
    {
        await using var dbContext = CreateDbContext();
        dbContext.Jobs.AddRange(
            CreateJob("404 Error: Page Not Found", "https://acme.example/jobs/missing"),
            CreateJob("Senior Data Engineer", "https://acme.example/jobs/2", location: "Hamilton"));
        await dbContext.SaveChangesAsync();

        var guard = CreateGuard(dbContext);
        var first = await guard.RunAsync(CancellationToken.None);
        var second = await guard.RunAsync(CancellationToken.None);

        first.JunkRemoved.Should().Be(1);
        first.RegionsNormalised.Should().Be(1);
        second.JunkRemoved.Should().Be(0);
        second.RegionsNormalised.Should().Be(0);
        dbContext.Jobs.Should().ContainSingle();
        dbContext.Jobs.Single().Region.Should().Be("Waikato");
    }

    [Fact]
    public async Task RunAsync_defaults_missing_location_to_new_zealand()
    {
        await using var dbContext = CreateDbContext();
        dbContext.Jobs.Add(CreateJob("Senior Data Engineer", "https://acme.example/jobs/3"));
        await dbContext.SaveChangesAsync();

        await CreateGuard(dbContext).RunAsync(CancellationToken.None);

        dbContext.Jobs.Single().Region.Should().Be("New Zealand");
    }

    [Fact]
    public async Task RunAsync_repairs_contaminated_locations_and_preserves_the_remainder()
    {
        await using var dbContext = CreateDbContext();
        dbContext.Jobs.Add(CreateJob(
            "Platform Engineer",
            "https://acme.example/jobs/4",
            location: "Wellington Job type Permanent Specialisation Team Leaders Reference BH-2844 Job description Are you passionate"));
        await dbContext.SaveChangesAsync();

        var guard = CreateGuard(dbContext);
        var first = await guard.RunAsync(CancellationToken.None);
        var second = await guard.RunAsync(CancellationToken.None);

        first.LocationsRepaired.Should().Be(1);
        second.LocationsRepaired.Should().Be(0);
        var job = dbContext.Jobs.Single();
        job.Location.Should().Be("Wellington");
        job.Region.Should().Be("Wellington");
        job.DescriptionSummary.Should().Contain("Job type Permanent");
    }

    private static JobQualityGuard CreateGuard(ApplicationDbContext dbContext) => new(
        dbContext,
        new JobContentValidator(),
        new NormalisationService(),
        NullLogger<JobQualityGuard>.Instance);

    private static Job CreateJob(
        string title,
        string url,
        string? location = null,
        string? description = null) => new()
    {
        Id = Guid.NewGuid(),
        Source = "Acme",
        Title = title,
        Url = url,
        ContentHash = Guid.NewGuid().ToString(),
        Location = location,
        DescriptionSummary = description,
        Region = "Unknown",
        IsActive = true,
        FirstSeenAt = DateTime.UtcNow,
        LastSeenAt = DateTime.UtcNow,
        CreatedAt = DateTime.UtcNow,
        UpdatedAt = DateTime.UtcNow
    };

    private static ApplicationDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        var dbContext = new ApplicationDbContext(options);
        dbContext.Database.EnsureCreated();
        return dbContext;
    }
}
