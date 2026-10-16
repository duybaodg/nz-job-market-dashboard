using Application.Jobs;
using Domain.Entities;
using FluentAssertions;
using Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Application.Tests;

public sealed class SalaryAuditServiceTests
{
    [Fact]
    public async Task RunAsync_ignores_a_salary_taken_from_a_contact_country_code()
    {
        await using var dbContext = CreateDbContext();
        var job = CreateJob(
            "Senior Software Engineer - iOS",
            "https://www.potentia.co.nz/job/61149780",
            description: "Meet the Consultant Kayla Moore +64kayla@potentia.co.nz kayla@potentia.co.nz");
        job.Seniority = "Senior";
        job.EmploymentType = "Contract";
        job.SalaryMin = 64_000m;
        job.SalaryPeriod = "Year";
        job.SalarySource = "source";
        job.SalaryText = "64k";
        dbContext.Jobs.Add(job);
        await dbContext.SaveChangesAsync();

        var result = await CreateService(dbContext).RunAsync(CancellationToken.None);

        result.NonPaySalaryIgnored.Should().Be(1);
        job.SalaryMin.Should().BeNull();
        job.SalaryMax.Should().BeNull();
        job.SalaryPeriod.Should().Be("Unknown");
        job.SalaryQuestionable.Should().BeTrue();
        job.SalaryQuestionableReason.Should().Contain("non-pay token");
    }

    [Fact]
    public async Task RunAsync_ignores_a_synthesized_amount_taken_from_a_contact_country_code()
    {
        // The QualityGuard synthesizes "$64000" from legacy numeric bounds, so
        // the audit must still recognise the "+64k" contact evidence.
        await using var dbContext = CreateDbContext();
        var job = CreateJob(
            "Senior Digital Workspace Engineer",
            "https://www.potentia.co.nz/job/61081623",
            description: "Rate: $100-$120/hr + GST. Meet Kayla +64kayla@potentia.co.nz");
        job.Seniority = "Senior";
        job.SalaryMin = 64_000m;
        job.SalaryPeriod = "Year";
        job.SalarySource = "source";
        job.SalaryText = "$64000";
        dbContext.Jobs.Add(job);
        await dbContext.SaveChangesAsync();

        var result = await CreateService(dbContext).RunAsync(CancellationToken.None);

        result.NonPaySalaryIgnored.Should().Be(1);
        job.SalaryMin.Should().BeNull();
        job.SalaryQuestionable.Should().BeTrue();
    }

    [Fact]
    public async Task RunAsync_ignores_a_file_size_from_the_retained_raw_page()
    {
        await using var dbContext = CreateDbContext();
        var job = CreateJob(
            "Digital Workspace Engineer",
            "https://jobs.govt.nz/jobs/Otago/HNZ-1952408",
            description: "Digital Workspace Engineer - join our team.");
        job.Seniority = "Junior";
        job.SalaryMin = 198_000m;
        job.SalaryPeriod = "Year";
        job.SalarySource = "source";
        job.SalaryText = "198K";
        dbContext.Jobs.Add(job);
        dbContext.RawJobPages.Add(new RawJobPage
        {
            Id = Guid.NewGuid(),
            Source = "NZ Government Jobs",
            Url = job.Url,
            Markdown = "Digital Workspace Engineer.pdf (PDF, 198KB) Job Description File links:",
            CrawledAt = DateTime.UtcNow
        });
        await dbContext.SaveChangesAsync();

        var result = await CreateService(dbContext).RunAsync(CancellationToken.None);

        result.NonPaySalaryIgnored.Should().Be(1);
        job.SalaryMin.Should().BeNull();
        job.SalaryQuestionable.Should().BeTrue();
    }

    [Fact]
    public async Task RunAsync_leaves_a_full_annual_figure_alone_when_a_lookalike_size_exists()
    {
        // "$198,000" is a real salary even if the page also lists an "198KB" file.
        await using var dbContext = CreateDbContext();
        var job = CreateJob(
            "Platform Engineer",
            "https://jobs.example/1",
            description: "Job Description (PDF, 198KB) File links:");
        job.SalaryMin = 198_000m;
        job.SalaryPeriod = "Year";
        job.SalarySource = "source";
        job.SalaryText = "$198,000";
        dbContext.Jobs.Add(job);
        await dbContext.SaveChangesAsync();

        var result = await CreateService(dbContext).RunAsync(CancellationToken.None);

        result.NonPaySalaryIgnored.Should().Be(0);
        job.SalaryMin.Should().Be(198_000m);
    }

    [Fact]
    public async Task RunAsync_flags_low_annual_pay_for_a_senior_title()
    {
        await using var dbContext = CreateDbContext();
        var job = CreateJob("Senior Digital Learning Specialist", "https://zeil.example/1");
        job.Seniority = "Senior";
        job.SalaryMin = 70_000m;
        job.SalaryMax = 80_000m;
        job.SalaryPeriod = "Year";
        job.SalarySource = "source";
        job.SalaryText = "NZD 70000 - NZD 80000 per year";
        dbContext.Jobs.Add(job);
        await dbContext.SaveChangesAsync();

        var result = await CreateService(dbContext).RunAsync(CancellationToken.None);

        result.SeniorLowPayFlagged.Should().Be(1);
        job.SalaryMin.Should().Be(70_000m);
        job.SalaryQuestionable.Should().BeTrue();
        job.SalaryQuestionableReason.Should().Contain("Senior/Lead");
    }

    [Fact]
    public async Task RunAsync_clears_a_below_floor_annual_figure_that_is_really_hourly()
    {
        await using var dbContext = CreateDbContext();
        var job = CreateJob("Retail Performance & Sales Analyst", "https://salt.example/1");
        job.Seniority = "Junior";
        job.SalaryMin = 36m;
        job.SalaryMax = 39m;
        job.SalaryPeriod = "Year";
        job.SalarySource = "description";
        job.SalaryText = "NZD 36-39";
        dbContext.Jobs.Add(job);
        await dbContext.SaveChangesAsync();

        var result = await CreateService(dbContext).RunAsync(CancellationToken.None);

        result.OutOfRangeCleared.Should().Be(1);
        job.SalaryMin.Should().BeNull();
        job.SalaryMax.Should().BeNull();
        job.SalaryQuestionable.Should().BeTrue();
        job.SalaryQuestionableReason.Should().Contain("below the annual floor");
    }

    [Fact]
    public async Task RunAsync_is_idempotent()
    {
        await using var dbContext = CreateDbContext();
        var job = CreateJob(
            "Senior Software Engineer - iOS",
            "https://www.potentia.co.nz/job/61149780",
            description: "+64kayla@potentia.co.nz");
        job.Seniority = "Senior";
        job.SalaryMin = 64_000m;
        job.SalaryPeriod = "Year";
        job.SalarySource = "source";
        job.SalaryText = "64k";
        dbContext.Jobs.Add(job);
        await dbContext.SaveChangesAsync();

        var service = CreateService(dbContext);
        var first = await service.RunAsync(CancellationToken.None);
        var second = await service.RunAsync(CancellationToken.None);

        first.NonPaySalaryIgnored.Should().Be(1);
        second.NonPaySalaryIgnored.Should().Be(0);
        second.SeniorLowPayFlagged.Should().Be(0);
        second.OutOfRangeCleared.Should().Be(0);
    }

    private static SalaryAuditService CreateService(ApplicationDbContext dbContext)
        => new(dbContext, NullLogger<SalaryAuditService>.Instance);

    private static Job CreateJob(string title, string url, string? description = null) => new()
    {
        Id = Guid.NewGuid(),
        Source = "Acme",
        Title = title,
        Url = url,
        ContentHash = Guid.NewGuid().ToString(),
        DescriptionText = description,
        DescriptionSummary = description,
        Region = "Unknown",
        EmploymentType = "Unknown",
        Seniority = "Unknown",
        WorkMode = "Unknown",
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
