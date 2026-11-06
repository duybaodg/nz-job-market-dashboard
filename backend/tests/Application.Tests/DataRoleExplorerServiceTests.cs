using Application.Analytics;
using Application.Jobs;
using Domain.Entities;
using FluentAssertions;
using Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Application.Tests;

public sealed class DataRoleExplorerServiceTests
{
    private static readonly DateTime Now = new(2026, 11, 6, 0, 30, 0, DateTimeKind.Utc);

    [Fact]
    public async Task Breakdown_publishes_counts_salary_samples_and_taxonomy_version()
    {
        await using var dbContext = CreateDbContext();
        SeedRoles(dbContext);
        await dbContext.SaveChangesAsync();

        var explorer = new DataRoleExplorerService(dbContext, new TestClock(Now));
        var result = await explorer.GetDataRolesAsync(new DataRoleExplorerQuery(), CancellationToken.None);

        result.TaxonomyVersion.Should().Be("data-subrole-v1");
        result.Family.Should().Be("Data & Analytics");
        result.CohortCount.Should().Be(21);
        result.ClassifiedCount.Should().Be(20);
        result.UncertainCount.Should().Be(1);
        result.Roles.Should().OnlyContain(role => role.Salary.CohortCount > 0 || role.ListingCount == 0);
        result.Roles.Should().OnlyContain(role => role.Salary.Currency == "NZD" && role.Salary.PayPeriod == "Year");
        result.Roles.Should().Contain(role => role.Key == "leadership" && role.Label == "Data & Analytics Leadership");

        var analysts = result.Roles.Single(role => role.Key == "data-analyst");
        analysts.ListingCount.Should().Be(5);
        analysts.Salary.EligibleCount.Should().Be(4);
        analysts.Salary.CohortCount.Should().Be(5);
        analysts.Salary.Suppressed.Should().BeTrue("the product suppresses groups below five");
        analysts.Salary.MedianAnnualNzd.Should().BeNull();

        var engineers = result.Roles.Single(role => role.Key == "data-engineer");
        engineers.ListingCount.Should().Be(5);
        engineers.Salary.EligibleCount.Should().Be(5);
        engineers.Salary.Suppressed.Should().BeFalse();
        engineers.Salary.SmallSample.Should().BeTrue("a sample of 5–9 is flagged");
        engineers.Salary.CoveragePercent.Should().Be(100m);
        engineers.Salary.MedianAnnualNzd.Should().Be(130000m);
        engineers.ListingsWithSkills.Should().Be(4);
        engineers.TopSkills.Should().Contain(skill => skill.SkillName == "SQL");

        var uncertain = result.Roles.Single(role => role.Uncertain);
        uncertain.Key.Should().Be("uncertain");
        uncertain.Label.Should().Be(DataSubRoleTaxonomy.Default.UncertainLabel);
        uncertain.ListingCount.Should().Be(1);
        uncertain.CandidateLabels.Should().BeEquivalentTo(["Data & Analytics Leadership", "Data Analyst"]);
    }

    [Fact]
    public async Task Explorer_filters_narrow_the_same_cohort_and_exclude_non_family_listings()
    {
        await using var dbContext = CreateDbContext();
        SeedRoles(dbContext);
        await dbContext.SaveChangesAsync();

        var explorer = new DataRoleExplorerService(dbContext, new TestClock(Now));
        var auckland = await explorer.GetDataRolesAsync(
            new DataRoleExplorerQuery(Region: "Auckland", Seniority: "Senior", WorkMode: "Hybrid", Skill: "SQL"),
            CancellationToken.None);

        auckland.Filters.HasFilters.Should().BeTrue();
        auckland.CohortCount.Should().BeGreaterThan(0);
        auckland.Roles.Should().OnlyContain(role => role.ListingCount > 0);
        auckland.CohortCount.Should().Be(auckland.Roles.Sum(role => role.ListingCount));
        auckland.Roles.SelectMany(role => role.TopSkills).Should().Contain(skill => skill.SkillName == "SQL");
    }

    [Fact]
    public async Task Empty_cohort_is_a_successful_empty_result_not_a_zero_demand_claim()
    {
        await using var dbContext = CreateDbContext();
        SeedRoles(dbContext);
        await dbContext.SaveChangesAsync();

        var explorer = new DataRoleExplorerService(dbContext, new TestClock(Now));
        var result = await explorer.GetDataRolesAsync(
            new DataRoleExplorerQuery(Region: "No Such Region"),
            CancellationToken.None);

        result.CohortCount.Should().Be(0);
        result.Roles.Should().BeEmpty();
        result.ClassifiedSharePercent.Should().Be(0m);
        result.ScopeNote.Should().Contain("explicit uncertain");
    }

    private static void SeedRoles(ApplicationDbContext dbContext)
    {
        AddJob(dbContext, "Data Analyst", "Auckland", "Intermediate", "Hybrid", 90000, 110000, ["SQL", "Excel"]);
        AddJob(dbContext, "Data Analyst", "Auckland", "Senior", "Hybrid", 100000, 120000, ["SQL"]);
        AddJob(dbContext, "Senior Data Analyst", "Wellington", "Senior", "Remote", 110000, 130000, ["SQL"]);
        AddJob(dbContext, "Workforce Data Analyst", "Auckland", "Senior", "Hybrid", null, null, ["SQL"]);
        AddJob(dbContext, "Principal Data Analyst", "Auckland", "Lead", "Hybrid", 120000, 140000, []);

        AddJob(dbContext, "Data Engineer", "Auckland", "Senior", "Hybrid", 120000, 140000, ["SQL", "Python"]);
        AddJob(dbContext, "Senior Data Engineer", "Auckland", "Senior", "Hybrid", 125000, 145000, ["SQL"]);
        AddJob(dbContext, "Data Engineer", "Wellington", "Senior", "Hybrid", 130000, 150000, ["Python"]);
        AddJob(dbContext, "Data Engineer (SQL, SSIS)", "Auckland", "Senior", "Hybrid", 115000, 135000, ["SQL"]);
        AddJob(dbContext, "Data Engineer - Analytics", "Auckland", "Senior", "Hybrid", 115000, 135000, []);
        AddJob(dbContext, "Lead Machine Learning Engineer", "Auckland", "Lead", "Hybrid", 140000, 160000, []);

        AddJob(dbContext, "Business Intelligence Analyst", "Auckland", "Intermediate", "Hybrid", 90000, 100000, ["Power BI"]);
        AddJob(dbContext, "Senior Business Intelligence Developer", "Auckland", "Senior", "Hybrid", 110000, 130000, ["Power BI"]);
        AddJob(dbContext, "Data Warehouse & BI Specialist", "Wellington", "Senior", "Hybrid", null, null, []);

        AddJob(dbContext, "Data & Analytics Technical Lead", "Auckland", "Lead", "Hybrid", 150000, 170000, []);
        AddJob(dbContext, "Line 1 Financial Crime Risk Analytics Senior Manager", "Auckland", "Senior", "Hybrid", 160000, 180000, []);

        AddJob(dbContext, "Senior Machine Learning Engineer", "Auckland", "Senior", "Hybrid", 150000, 180000, ["Python"]);
        AddJob(dbContext, "Senior Machine Learning Engineer", "Wellington", "Senior", "Hybrid", 190000, 220000, []);
        AddJob(dbContext, "Data Scientist", "Auckland", "Senior", "Hybrid", 150000, 170000, ["Python"]);

        AddJob(dbContext, "Senior Analytics Engineer", "Auckland", "Senior", "Hybrid", 160000, null, ["SQL"]);

        AddJob(dbContext, "Lead Data Analyst", "Auckland", "Lead", "Hybrid", 130000, 150000, ["SQL"]);

        // Non-family row: a data pattern alone must not pull it into the explorer.
        AddJob(dbContext, "Head of Data, Digital & Information Services", "Nelson", "Lead", "Hybrid", 170000, 190000, [], familyOverride: "Management & Leadership");
    }

    private static Guid AddJob(
        ApplicationDbContext dbContext,
        string title,
        string region,
        string seniority,
        string workMode,
        decimal? salaryMin,
        decimal? salaryMax,
        IReadOnlyList<string> skills,
        string? familyOverride = null)
    {
        // The family is computed on read; familyOverride is documentation only.
        _ = familyOverride;
        var id = Guid.NewGuid();
        dbContext.Jobs.Add(new Job
        {
            Id = id,
            Source = "Xero",
            Title = title,
            Company = "Xero",
            Location = region,
            Region = region,
            Url = $"https://example.com/{id}",
            ContentHash = id.ToString(),
            IsActive = true,
            EmploymentType = "Permanent",
            Seniority = seniority,
            WorkMode = workMode,
            SalaryMin = salaryMin,
            SalaryMax = salaryMax,
            SalaryText = salaryMin is null ? null : $"${salaryMin:N0} - ${salaryMax:N0}",
            SalaryCurrency = "NZD",
            SalaryPeriod = salaryMin is null ? "Unknown" : "Year",
            FirstSeenAt = Now.AddDays(-2),
            LastSeenAt = Now,
            CreatedAt = Now.AddDays(-2),
            UpdatedAt = Now
        });

        foreach (var skill in skills)
        {
            dbContext.JobSkills.Add(new JobSkill
            {
                Id = Guid.NewGuid(),
                JobId = id,
                SkillName = skill,
                SkillType = "ProgrammingLanguage"
            });
        }

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
