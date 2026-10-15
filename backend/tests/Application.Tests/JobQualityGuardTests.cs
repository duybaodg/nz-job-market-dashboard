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

    // Round 2 follow-up: the Phase A sanitizer left these exact live strings in
    // place, so the previous repair pass treated them as already normalised.
    // A pattern change must re-run the sanitizer over those rows.
    [Fact]
    public async Task RunAsync_reruns_sanitizer_on_rows_the_previous_pass_missed()
    {
        await using var dbContext = CreateDbContext();
        dbContext.Jobs.AddRange(
            CreateJob(
                "CloudOps Engineer - Software",
                "https://careers.gallagher.com/jobs/1",
                location: "centives Career growth opportunities as eShepherd continues to scale globally"),
            CreateJob(
                "Network Solutions Architect",
                "https://www.talentvault.co.nz/job-details/1",
                location: "Auckland Industry: Information & Communication Technology Contact name: Steve"));
        await dbContext.SaveChangesAsync();

        var guard = CreateGuard(dbContext);
        var first = await guard.RunAsync(CancellationToken.None);
        var second = await guard.RunAsync(CancellationToken.None);

        first.LocationsRepaired.Should().Be(2);
        second.LocationsRepaired.Should().Be(0);

        var gallagher = dbContext.Jobs.Single(job => job.Source == "Acme" && job.Url.Contains("gallagher"));
        gallagher.Location.Should().BeNull();
        gallagher.DescriptionSummary.Should().Contain("Career growth");

        var talentVault = dbContext.Jobs.Single(job => job.Url.Contains("talentvault"));
        talentVault.Location.Should().Be("Auckland");
        talentVault.Region.Should().Be("Auckland");
        talentVault.DescriptionSummary.Should().Contain("Industry: Information & Communication Technology");
    }

    // Round 5: the repair pass must canonicalise every existing spelling and
    // mark (and keep marked) non-NZ places as International, idempotently.
    [Fact]
    public async Task RunAsync_canonicalises_existing_locations_and_marks_international_places()
    {
        await using var dbContext = CreateDbContext();
        dbContext.Jobs.AddRange(
            CreateJob("Data Engineer", "https://acme.example/jobs/10", location: "Auckland, Auckland, nz"),
            CreateJob("Cloud Engineer", "https://acme.example/jobs/11", location: "Hamilton, New Zealand, NZ"),
            CreateJob("Support Engineer", "https://acme.example/jobs/12", location: "Adelaide, Brisbane, Canberra"),
            CreateJob("Platform Developer", "https://acme.example/jobs/13", location: "None"),
            CreateJob("Network Engineer", "https://acme.example/jobs/14", location: "Manukau City, Auckland, NZ"));
        await dbContext.SaveChangesAsync();

        var guard = CreateGuard(dbContext);
        var first = await guard.RunAsync(CancellationToken.None);
        var second = await guard.RunAsync(CancellationToken.None);

        first.LocationsRepaired.Should().Be(5);
        second.LocationsRepaired.Should().Be(0);
        second.RegionsNormalised.Should().Be(0);

        dbContext.Jobs.Single(job => job.Url.EndsWith("/10")).Location.Should().Be("Auckland");
        dbContext.Jobs.Single(job => job.Url.EndsWith("/11")).Location.Should().Be("Hamilton, Waikato");
        dbContext.Jobs.Single(job => job.Url.EndsWith("/11")).Region.Should().Be("Waikato");
        dbContext.Jobs.Single(job => job.Url.EndsWith("/14")).Location.Should().Be("Auckland");

        var international = dbContext.Jobs.Single(job => job.Url.EndsWith("/12"));
        international.Location.Should().BeNull();
        international.Region.Should().Be("International");
        international.DescriptionSummary.Should().Contain("Adelaide");

        dbContext.Jobs.Single(job => job.Url.EndsWith("/13")).Location.Should().BeNull();
    }

    [Fact]
    public async Task RunAsync_backfills_salary_provenance_and_flags_questionable_parses()
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
        var normal = CreateJob("Senior Software Engineer", "https://acme.example/jobs/s1");
        normal.SalaryMin = 120_000m;
        normal.SalaryMax = 140_000m;
        var support = CreateJob("Tier 2 IT Support Specialist", "https://acme.example/jobs/s2");
        support.SalaryMin = 300_000m;
        dbContext.Jobs.AddRange(normal, support);
        await dbContext.SaveChangesAsync();

        var guard = CreateGuard(dbContext);
        var first = await guard.RunAsync(CancellationToken.None);
        var second = await guard.RunAsync(CancellationToken.None);

        first.SalariesBackfilled.Should().Be(2);
        second.SalariesBackfilled.Should().Be(0, "rows with provenance are skipped on the next run");
        normal.SalaryText.Should().Be("$120000 - $140000");
        normal.SalaryPeriod.Should().Be("Year");
        normal.SalaryQuestionable.Should().BeFalse();
        support.SalaryQuestionable.Should().BeTrue();
        support.SalaryQuestionableReason.Should().Contain("support");
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
