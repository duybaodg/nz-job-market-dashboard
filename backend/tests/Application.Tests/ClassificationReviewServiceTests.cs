using Application.Jobs;
using Application.Quality;
using Domain.Entities;
using FluentAssertions;
using Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Application.Tests;

public sealed class ClassificationReviewServiceTests
{
    private static readonly DateTime Now = new(2026, 11, 5, 9, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task Sample_is_stratified_by_source_and_role_family_and_includes_seniority_spans()
    {
        await using var dbContext = CreateDbContext();
        AddJob(dbContext, "Xero", "Senior Data Engineer", 1);
        AddJob(dbContext, "Xero", "Software Engineer", 1);
        AddJob(dbContext, "Xero", "Intermediate/Senior Backend Engineer", 1);
        AddJob(dbContext, "SEEK NZ", "Service Desk Analyst", 2);
        AddJob(dbContext, "SEEK NZ", "Senior Network Engineer", 2);
        AddJob(dbContext, "SEEK NZ", "Business Analyst", 2);
        await dbContext.SaveChangesAsync();

        var service = new ClassificationReviewService(dbContext, new TestClock(Now));
        var sample = await service.GetSampleAsync(4, CancellationToken.None);

        sample.ActiveListings.Should().Be(6);
        sample.SampleSize.Should().BeGreaterThanOrEqualTo(4);
        sample.Items.Select(item => item.Source).Distinct().Should().HaveCount(2);
        sample.Items.Single(item => item.Title == "Intermediate/Senior Backend Engineer")
            .SpansSeniority.Should().BeTrue();
        sample.Items.Single(item => item.Title == "Intermediate/Senior Backend Engineer")
            .TitleLevels.Should().Equal("Intermediate", "Senior");
        sample.Items.Should().OnlyContain(item => item.RuleVersion == "role-family-v1");
    }

    [Fact]
    public async Task Saving_labels_is_idempotent_and_reports_confusion_counts()
    {
        await using var dbContext = CreateDbContext();
        var correctId = AddJob(dbContext, "Xero", "Senior Data Engineer", 1);
        var missedId = AddJob(dbContext, "SEEK NZ", "Solution Architect", 2);
        await dbContext.SaveChangesAsync();

        var service = new ClassificationReviewService(dbContext, new TestClock(Now));
        var first = await service.SaveLabelsAsync(
            new RecordClassificationLabelsRequest(
                [
                    new ClassificationLabelInput(correctId, "correct", null, null, null, null, "Clear data engineering role."),
                    new ClassificationLabelInput(missedId, "false-negative", "Architecture", "Unknown", "correct", null, "Architect token missed.")
                ],
                "tester"),
            CancellationToken.None);

        first.Created.Should().Be(2);
        first.Updated.Should().Be(0);
        first.Report.ReviewedListings.Should().Be(2);
        first.Report.LabelCounts["correct"].Should().Be(1);
        first.Report.LabelCounts["false-negative"].Should().Be(1);
        first.Report.Reviews.Should().OnlyContain(review => review.Reviewer == "tester");

        var second = await service.SaveLabelsAsync(
            new RecordClassificationLabelsRequest(
                [new ClassificationLabelInput(correctId, "ambiguous", "Data & Analytics", "Senior", "ambiguous", "Spans data engineering and analysis.", "updated")],
                "tester"),
            CancellationToken.None);

        second.Created.Should().Be(0);
        second.Updated.Should().Be(1);
        second.Report.ReviewedListings.Should().Be(2);
        second.Report.LabelCounts["correct"].Should().Be(0);
        second.Report.LabelCounts["ambiguous"].Should().Be(1);
        (await dbContext.ClassificationReviews.CountAsync()).Should().Be(2);
    }

    [Fact]
    public async Task Rejects_unknown_job_ids_and_invalid_labels()
    {
        await using var dbContext = CreateDbContext();
        var jobId = AddJob(dbContext, "Xero", "Software Engineer", 1);
        await dbContext.SaveChangesAsync();
        var service = new ClassificationReviewService(dbContext, new TestClock(Now));

        var missing = () => service.SaveLabelsAsync(
            new RecordClassificationLabelsRequest(
                [new ClassificationLabelInput(Guid.NewGuid(), "correct", null, null, null, null, null)],
                null),
            CancellationToken.None);
        await missing.Should().ThrowAsync<ArgumentException>();

        var invalid = () => service.SaveLabelsAsync(
            new RecordClassificationLabelsRequest(
                [new ClassificationLabelInput(jobId, "made-up", null, null, null, null, null)],
                null),
            CancellationToken.None);
        await invalid.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public void Embedded_evaluation_is_backed_by_the_committed_sample()
    {
        var provider = new EmbeddedClassificationEvaluationProvider();
        var evaluation = provider.GetEvaluation();

        evaluation.Available.Should().BeTrue();
        evaluation.SampledListings.Should().BeGreaterThanOrEqualTo(100);
        evaluation.ReviewedListings.Should().Be(evaluation.SampledListings);
        evaluation.AccuracyDenominator.Should().Be(
            evaluation.Correct + evaluation.FalsePositive + evaluation.FalseNegative);
        evaluation.AccuracyPercent.Should().Be(87.5m);
        evaluation.RuleVersion.Should().Be("role-family-v1");
        evaluation.Scope.Should().Contain("not a census");
    }

    [Fact]
    public void Detects_every_explicit_seniority_level_in_a_title()
    {
        SeniorityExtractor.DetectTitleLevels("Junior, Intermediate and Senior Software Engineers")
            .Should().Equal("Junior", "Intermediate", "Senior");
        SeniorityExtractor.DetectTitleLevels("Senior/Lead Test Automation / Principal Test Engineer")
            .Should().Equal("Senior", "Lead");
        SeniorityExtractor.DetectTitleLevels("Senior Software Engineer")
            .Should().Equal("Senior");
    }

    private static Guid AddJob(ApplicationDbContext dbContext, string source, string title, int seed)
    {
        var id = Guid.NewGuid();
        dbContext.Jobs.Add(new Job
        {
            Id = id,
            Source = source,
            Title = title,
            Company = source,
            Url = $"https://example.com/{seed}/{id}",
            ContentHash = id.ToString(),
            IsActive = true,
            Seniority = "Unknown",
            DescriptionSummary = $"Evidence for {title}",
            FirstSeenAt = Now.AddDays(-2),
            LastSeenAt = Now,
            CreatedAt = Now.AddDays(-2),
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
