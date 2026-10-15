using Application.Jobs;
using Domain.Entities;
using FluentAssertions;
using Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Application.Tests;

public sealed class JobServiceTests
{
    [Fact]
    public async Task Job_list_exposes_advertiser_and_role_family_from_the_registry_category()
    {
        await using var dbContext = CreateDbContext();
        dbContext.JobSources.Add(new JobSource
        {
            Id = Guid.NewGuid(),
            Name = "Beyond Recruitment",
            Method = "Scraping",
            Parser = "generic",
            Category = "recruiter",
            BaseUrl = "https://beyond.example",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        });
        dbContext.Jobs.Add(new Job
        {
            Id = Guid.NewGuid(),
            Source = "Beyond Recruitment",
            Title = "Senior Data Engineer",
            Company = "Beyond Recruitment",
            Url = "https://beyond.example/jobs/1",
            ContentHash = "hash-1",
            IsActive = true,
            FirstSeenAt = DateTime.UtcNow,
            LastSeenAt = DateTime.UtcNow,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        });
        await dbContext.SaveChangesAsync();

        var jobs = await new JobService(dbContext).GetJobsAsync(new JobListQuery(null, null, null, "all"), CancellationToken.None);

        var job = jobs.Should().ContainSingle().Subject;
        job.SourceCategory.Should().Be("recruiter");
        job.AdvertiserType.Should().Be("Agency");
        job.EmployerDisclosure.Should().Be("AgencyOnly");
        job.HiringEmployer.Should().BeNull();
        job.RoleFamily.Should().Be("Data & Analytics");
        job.RoleFamilyInferred.Should().BeTrue();
    }

    private static ApplicationDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        return new ApplicationDbContext(options);
    }
}
