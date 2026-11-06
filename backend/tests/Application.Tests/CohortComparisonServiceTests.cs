using Application.Analytics;
using Application.History;
using Domain.Entities;
using FluentAssertions;
using Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Application.Tests;

public sealed class CohortComparisonServiceTests
{
    // 2026-11-06T00:30Z is 2026-11-06 13:30 NZDT. The 7 complete-day interval
    // is [2026-10-30, 2026-11-06) NZ; the previous period is [2026-10-23, 2026-10-30).
    private static readonly DateTime Now = new(2026, 11, 6, 0, 30, 0, DateTimeKind.Utc);

    [Fact]
    public async Task Current_comparison_uses_one_capture_and_percentage_point_skill_differences()
    {
        await using var db = CreateDbContext();
        for (var index = 0; index < 5; index++)
        {
            AddJob(db, $"Senior Data Engineer {index}", "Auckland", 120000 + index * 1000, 140000 + index * 1000,
                skills: index < 3 ? ["SQL", "Python"] : ["Python"]);
        }
        for (var index = 0; index < 3; index++)
        {
            AddJob(db, $"Data Analyst {index}", "Auckland", 90000 + index * 1000, 100000 + index * 1000,
                skills: index == 0 ? ["SQL", "Excel"] : ["Excel"]);
        }
        await db.SaveChangesAsync();

        var history = new HistoryService(db, new TestClock(Now));
        var service = new CohortComparisonService(db, history, new TestClock(Now));
        var result = await service.CompareAsync(
            new CohortComparisonQuery(
                7,
                new ComparisonCohortQuery(DataSubRole: "data-engineer"),
                new ComparisonCohortQuery(DataSubRole: "data-analyst")),
            CancellationToken.None);

        result.A.ActiveListings.Should().Be(5);
        result.B.ActiveListings.Should().Be(3);
        result.ActiveCountDifference.AbsoluteDifference.Should().Be(-2);
        result.ActiveCountDifference.RelativeDifferencePercent.Should().Be(-40m);
        result.ActiveCountDifference.BaselineAvailable.Should().BeTrue();

        result.A.Salary.EligibleCount.Should().Be(5);
        result.A.Salary.Suppressed.Should().BeFalse();
        result.A.Salary.SmallSample.Should().BeTrue();
        result.B.Salary.EligibleCount.Should().Be(3);
        result.B.Salary.Suppressed.Should().BeTrue("salary groups below five are suppressed");

        var sql = result.SkillDifferences.Single(skill => skill.SkillName == "SQL");
        sql.ACount.Should().Be(3);
        sql.ASharePercent.Should().Be(60m);
        sql.BCount.Should().Be(1);
        sql.BSharePercent.Should().Be(33.3m);
        sql.DifferencePercentagePoints.Should().Be(-26.7m);
        sql.AListingDenominator.Should().Be(5);
        sql.BListingDenominator.Should().Be(3);

        result.SkillHistory.Available.Should().BeFalse();
        result.SkillHistory.Reason.Should().Contain("not the versioned");
        result.Notes.Should().Contain(note => note.Contains("Sub-role cohorts"));
        result.DateRule.Should().Contain("identical date rules");
    }

    [Fact]
    public async Task Zero_baseline_shows_absolute_count_difference_only()
    {
        await using var db = CreateDbContext();
        AddJob(db, "Data Engineer", "Auckland", 120000, 140000, ["SQL"]);
        AddJob(db, "Data Analyst", "Wellington", 90000, 100000, ["Excel"]);
        await db.SaveChangesAsync();

        var service = new CohortComparisonService(db, new HistoryService(db, new TestClock(Now)), new TestClock(Now));
        var result = await service.CompareAsync(
            new CohortComparisonQuery(
                7,
                new ComparisonCohortQuery(Region: "No Such Region"),
                new ComparisonCohortQuery(Region: "Auckland")),
            CancellationToken.None);

        result.A.ActiveListings.Should().Be(0);
        result.B.ActiveListings.Should().Be(1);
        result.ActiveCountDifference.AbsoluteDifference.Should().Be(1);
        result.ActiveCountDifference.RelativeDifferencePercent.Should().BeNull();
        result.ActiveCountDifference.BaselineAvailable.Should().BeFalse();
        result.ActiveCountDifference.Note.Should().Contain("no active listings");
        result.ActiveCountDifference.Note.Should().Contain("not infinity");
    }

    [Fact]
    public async Task Period_counts_use_complete_windows_and_a_zero_prior_count_keeps_the_absolute_change()
    {
        await using var db = CreateDbContext();
        SeedFortnight(db, skillsCaptured: false);
        await db.SaveChangesAsync();

        var service = new CohortComparisonService(db, new HistoryService(db, new TestClock(Now)), new TestClock(Now));
        var result = await service.CompareAsync(
            new CohortComparisonQuery(
                7,
                new ComparisonCohortQuery(Region: "Auckland"),
                new ComparisonCohortQuery(Region: "Wellington")),
            CancellationToken.None);

        result.A.NewListings.Available.Should().BeTrue();
        result.A.NewListings.CurrentNewListings.Should().Be(1, "A2 was first seen in the current period");
        result.A.NewListings.PreviousNewListings.Should().Be(1, "A1 was first seen in the previous period");
        result.A.NewListings.Change!.ComparisonAvailable.Should().BeTrue();

        result.B.NewListings.Available.Should().BeTrue();
        result.B.NewListings.CurrentNewListings.Should().Be(1, "W1 was first seen in the current period");
        result.B.NewListings.PreviousNewListings.Should().Be(0);
        result.B.NewListings.Change!.NoPriorBaseline.Should().BeTrue();
        result.B.NewListings.Change.AbsoluteChange.Should().Be(1);
        result.B.NewListings.Change.PercentChange.Should().BeNull();
        result.B.NewListings.Change.Note.Should().Contain("No prior baseline");
    }

    [Fact]
    public async Task Skill_history_is_gated_on_skill_captured_snapshot_depth_with_a_specific_reason()
    {
        await using var db = CreateDbContext();
        SeedFortnight(db, skillsCaptured: true, onlyLastDayCaptured: true);
        await db.SaveChangesAsync();

        var service = new CohortComparisonService(db, new HistoryService(db, new TestClock(Now)), new TestClock(Now));
        var result = await service.CompareAsync(
            new CohortComparisonQuery(
                7,
                new ComparisonCohortQuery(Region: "Auckland"),
                new ComparisonCohortQuery(Region: "Wellington")),
            CancellationToken.None);

        result.SkillHistory.Available.Should().BeFalse();
        result.SkillHistory.SkillsCapturedSnapshotDays.Should().Be(1);
        result.SkillHistory.RequiredCompleteDays.Should().Be(14);
        result.SkillHistory.FirstSkillsCapturedDate.Should().Be("2026-11-05");
        result.SkillHistory.Reason.Should().Contain("only 1 snapshot day");
        result.SkillHistory.Reason.Should().Contain("14 complete skill-captured days");
        result.SkillHistory.Skills.Should().BeEmpty();
    }

    [Fact]
    public async Task Skill_history_is_computed_when_both_complete_windows_captured_skills()
    {
        await using var db = CreateDbContext();
        SeedFortnight(db, skillsCaptured: true, varyAucklandSkills: true);
        await db.SaveChangesAsync();

        var service = new CohortComparisonService(db, new HistoryService(db, new TestClock(Now)), new TestClock(Now));
        var result = await service.CompareAsync(
            new CohortComparisonQuery(
                7,
                new ComparisonCohortQuery(Region: "Auckland"),
                new ComparisonCohortQuery(Region: "Wellington")),
            CancellationToken.None);

        result.SkillHistory.Available.Should().BeTrue(result.SkillHistory.Reason);
        result.SkillHistory.SkillsCapturedSnapshotDays.Should().Be(14);
        result.SkillHistory.ACurrentListings.Should().Be(2);
        result.SkillHistory.APreviousListings.Should().Be(1);
        result.SkillHistory.BCurrentListings.Should().Be(2);
        result.SkillHistory.BPreviousListings.Should().Be(1);

        var python = result.SkillHistory.Skills.Single(skill => skill.SkillName == "Python");
        python.ACurrentCount.Should().Be(1);
        python.ACurrentSharePercent.Should().Be(50m);
        python.APreviousCount.Should().Be(0);
        python.APreviousSharePercent.Should().Be(0m);
        python.AChangePoints.Should().Be(50m);
        python.BCurrentCount.Should().Be(1);
        python.BPreviousCount.Should().Be(0);
        python.BChangePoints.Should().Be(50m);

        var sql = result.SkillHistory.Skills.Single(skill => skill.SkillName == "SQL");
        sql.ACurrentCount.Should().Be(1);
        sql.APreviousCount.Should().Be(1);
        sql.AChangePoints.Should().Be(-50m);
        sql.BCurrentCount.Should().Be(1);
        sql.BPreviousCount.Should().Be(1);
        sql.BChangePoints.Should().Be(-50m);
    }

    [Fact]
    public async Task Identical_cohorts_are_labelled_as_zero_by_construction()
    {
        await using var db = CreateDbContext();
        AddJob(db, "Data Engineer", "Auckland", 120000, 140000, ["SQL"]);
        await db.SaveChangesAsync();

        var service = new CohortComparisonService(db, new HistoryService(db, new TestClock(Now)), new TestClock(Now));
        var result = await service.CompareAsync(
            new CohortComparisonQuery(
                7,
                new ComparisonCohortQuery(DataSubRole: "data-engineer"),
                new ComparisonCohortQuery(DataSubRole: "data-engineer")),
            CancellationToken.None);

        result.Notes.Should().Contain(note => note.Contains("same filter definition"));
        result.ActiveCountDifference.AbsoluteDifference.Should().Be(0);
    }

    private static void SeedFortnight(
        ApplicationDbContext db,
        bool skillsCaptured,
        bool onlyLastDayCaptured = false,
        bool varyAucklandSkills = false)
    {
        var aucklandPrevious = new DateTime(2026, 10, 25, 0, 0, 0, DateTimeKind.Utc);
        var aucklandCurrent = new DateTime(2026, 11, 2, 0, 0, 0, DateTimeKind.Utc);
        var wellingtonPrevious = new DateTime(2026, 10, 20, 0, 0, 0, DateTimeKind.Utc);
        var wellingtonCurrent = new DateTime(2026, 11, 3, 0, 0, 0, DateTimeKind.Utc);
        var a1 = AddJob(db, "Data Analyst A1", "Auckland", 100000, 120000, ["SQL"], firstSeen: aucklandPrevious);
        var a2 = AddJob(db, "Data Engineer A2", "Auckland", 120000, 140000, ["Python"], firstSeen: aucklandCurrent);
        var w0 = AddJob(db, "Data Analyst W0", "Wellington", 110000, 130000, ["SQL"], firstSeen: wellingtonPrevious);
        var w1 = AddJob(db, "Data Engineer W1", "Wellington", 130000, 150000, ["Python"], firstSeen: wellingtonCurrent);

        var firstDay = new DateOnly(2026, 10, 23);
        var lastDay = new DateOnly(2026, 11, 5);
        for (var day = firstDay; day <= lastDay; day = day.AddDays(1))
        {
            db.ListingSnapshotRuns.Add(new ListingSnapshotRun
            {
                Id = Guid.NewGuid(),
                SnapshotDate = day,
                CapturedAtUtc = day.ToDateTime(TimeOnly.FromTimeSpan(TimeSpan.Zero), DateTimeKind.Utc),
                ListingCount = 0,
                ActiveCount = 0,
                SourceCount = 0,
                IdentifiableEmployerCount = 0,
                SourceCoverageJson = "{}"
            });

            var currentPeriod = day >= new DateOnly(2026, 10, 30);
            var captured = skillsCaptured && (!onlyLastDayCaptured || day == lastDay);
            AddSnapshotIfActive(db, day, a1, aucklandPrevious, "A1", "Auckland", "Data Analyst", captured, ["SQL"]);
            AddSnapshotIfActive(db, day, a2, aucklandCurrent, "A2", "Auckland", "Data Engineer", captured, ["Python"]);
            AddSnapshotIfActive(db, day, w0, wellingtonPrevious, "W0", "Wellington", "Data Analyst", captured, ["SQL"]);
            AddSnapshotIfActive(db, day, w1, wellingtonCurrent, "W1", "Wellington", "Data Engineer", captured, ["Python"]);
            _ = currentPeriod;
            _ = varyAucklandSkills;
        }
    }

    private static void AddSnapshotIfActive(
        ApplicationDbContext db,
        DateOnly day,
        Guid jobId,
        DateTime firstSeen,
        string hash,
        string region,
        string title,
        bool skillsCaptured,
        IReadOnlyList<string> skills)
    {
        if (day < DateOnly.FromDateTime(firstSeen))
        {
            return;
        }

        AddSnapshot(db, day, jobId, firstSeen, hash, region, title, skillsCaptured, skills);
    }

    private static void AddSnapshot(
        ApplicationDbContext db,
        DateOnly day,
        Guid jobId,
        DateTime firstSeen,
        string hash,
        string region,
        string title,
        bool skillsCaptured,
        IReadOnlyList<string> skills)
    {
        db.ListingSnapshots.Add(new ListingSnapshot
        {
            Id = Guid.NewGuid(),
            SnapshotDate = day,
            JobId = jobId,
            Source = "Xero",
            SourceCategory = "company",
            ContentHash = hash,
            Title = title,
            Region = region,
            Location = region,
            WorkMode = "Hybrid",
            Seniority = "Senior",
            EmploymentType = "Full-time",
            RoleFamily = "Data & Analytics",
            SkillsCaptured = skillsCaptured,
            SkillsJson = System.Text.Json.JsonSerializer.Serialize(skills),
            FirstSeenAt = firstSeen,
            LastSeenAt = Now,
            IsActive = true,
            CapturedAtUtc = Now
        });
    }

    private static Guid AddJob(
        ApplicationDbContext db,
        string title,
        string region,
        decimal? salaryMin,
        decimal? salaryMax,
        IReadOnlyList<string> skills,
        DateTime? firstSeen = null)
    {
        var id = Guid.NewGuid();
        var seen = firstSeen ?? new DateTime(2026, 10, 20, 0, 0, 0, DateTimeKind.Utc);
        db.Jobs.Add(new Job
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
            Seniority = "Senior",
            WorkMode = "Hybrid",
            SalaryMin = salaryMin,
            SalaryMax = salaryMax,
            SalaryText = salaryMin is null ? null : $"${salaryMin:N0} - ${salaryMax:N0}",
            SalaryCurrency = "NZD",
            SalaryPeriod = salaryMin is null ? "Unknown" : "Year",
            FirstSeenAt = seen,
            LastSeenAt = Now,
            CreatedAt = seen,
            UpdatedAt = Now
        });

        foreach (var skill in skills)
        {
            db.JobSkills.Add(new JobSkill
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
