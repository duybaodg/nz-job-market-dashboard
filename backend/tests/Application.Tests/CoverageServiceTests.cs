using Application.Health;
using Domain.Entities;
using FluentAssertions;
using Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Application.Tests;

public sealed class CoverageServiceTests
{
    [Fact]
    public async Task Reports_per_field_coverage_for_active_listings_only()
    {
        await using var dbContext = CreateDbContext();

        var complete = CreateJob();
        complete.WorkMode = "Remote";
        complete.Seniority = "Senior";
        complete.EmploymentType = "Full-time";
        complete.SalaryMin = 120_000m;
        complete.SalaryMax = 150_000m;
        complete.SalaryPeriod = "Year";
        complete.PostedDate = DateTime.UtcNow;
        complete.EmployerCanonicalId = Guid.NewGuid();
        complete.Skills.Add(new JobSkill { Id = Guid.NewGuid(), JobId = complete.Id, SkillName = "C#", SkillType = "ProgrammingLanguage" });

        var sparse = CreateJob();
        var expired = CreateJob();
        expired.IsActive = false;

        dbContext.Jobs.AddRange(complete, sparse, expired);
        await dbContext.SaveChangesAsync();

        var report = await new CoverageService(dbContext).GetCoverageAsync(CancellationToken.None);

        report.TotalJobs.Should().Be(2);
        report.Fields.Single(field => field.Field == "salary").Known.Should().Be(1);
        report.Fields.Single(field => field.Field == "salary").Percentage.Should().Be(50);
        report.Fields.Single(field => field.Field == "workMode").Known.Should().Be(1);
        report.Fields.Single(field => field.Field == "skills").Known.Should().Be(1);
        report.Salary.Annual.Should().Be(1);
        report.Salary.OpenEnded.Should().Be(0);
        report.WorkModeBreakdown.Should().Contain(item => item.Value == "Remote" && item.Count == 1);
    }

    [Fact]
    public async Task Counts_hourly_conversion_and_open_ranges()
    {
        await using var dbContext = CreateDbContext();
        var hourly = CreateJob();
        hourly.SalaryMin = 60m;
        hourly.SalaryMax = 80m;
        hourly.SalaryPeriod = "Hour";

        var open = CreateJob();
        open.SalaryMax = 150_000m;
        open.SalaryPeriod = "Year";

        dbContext.Jobs.AddRange(hourly, open);
        await dbContext.SaveChangesAsync();

        var report = await new CoverageService(dbContext).GetCoverageAsync(CancellationToken.None);

        report.Salary.HourlyOrDaily.Should().Be(1);
        report.Salary.Converted.Should().Be(1);
        report.Salary.OpenEnded.Should().Be(1);
    }

    private static Job CreateJob() => new()
    {
        Id = Guid.NewGuid(),
        Source = "Test",
        Title = "Engineer",
        Url = $"https://example.com/jobs/{Guid.NewGuid()}",
        ContentHash = Guid.NewGuid().ToString(),
        WorkMode = "Unknown",
        Seniority = "Intermediate",
        EmploymentType = "Unknown",
        SalaryPeriod = "Unknown",
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
