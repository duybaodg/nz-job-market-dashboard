using Application.Jobs;
using Domain.Entities;
using FluentAssertions;
using Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Application.Tests;

public sealed class AiFieldEnrichmentServiceTests
{
    [Fact]
    public async Task Disabled_enrichment_never_calls_the_client()
    {
        await using var dbContext = CreateDbContext();
        dbContext.Jobs.Add(CreateJob());
        await dbContext.SaveChangesAsync();
        var client = new FakeClient(_ => Response(workMode: "Remote"));

        var service = CreateService(dbContext, client, enabled: false, apiKey: string.Empty);
        var result = await service.RunBatchAsync(10, CancellationToken.None);

        result.Enabled.Should().BeFalse();
        client.Calls.Should().Be(0);
        dbContext.Jobs.Single().WorkMode.Should().Be("Unknown");
    }

    [Fact]
    public async Task Enabled_without_a_key_is_still_off()
    {
        await using var dbContext = CreateDbContext();
        dbContext.Jobs.Add(CreateJob());
        await dbContext.SaveChangesAsync();
        var client = new FakeClient(_ => Response(workMode: "Remote"));

        var service = CreateService(dbContext, client, enabled: true, apiKey: string.Empty);
        var result = await service.RunBatchAsync(10, CancellationToken.None);

        result.Enabled.Should().BeFalse();
        client.Calls.Should().Be(0);
    }

    [Fact]
    public async Task Fills_only_the_fields_deterministic_extraction_could_not()
    {
        await using var dbContext = CreateDbContext();
        var job = CreateJob();
        dbContext.Jobs.Add(job);
        await dbContext.SaveChangesAsync();
        var client = new FakeClient(_ => Response(
            workMode: "Hybrid", seniority: "Lead", salaryMin: 130_000, salaryMax: 150_000));

        var service = CreateService(dbContext, client, enabled: true, apiKey: "test-key");
        var result = await service.RunBatchAsync(10, CancellationToken.None);

        result.Enabled.Should().BeTrue();
        result.Updated.Should().Be(1);
        result.SalaryFilled.Should().Be(1);
        result.WorkModeFilled.Should().Be(1);
        result.SeniorityFilled.Should().Be(1);

        job.WorkMode.Should().Be("Hybrid");
        job.WorkModeSource.Should().Be("ai-enrichment");
        job.Seniority.Should().Be("Lead");
        job.SenioritySource.Should().Be("ai-enrichment");
        job.SalaryMin.Should().Be(130_000m);
        job.SalarySource.Should().Be("ai-enrichment");
        job.AiEnrichmentModel.Should().Be("test-model");
        job.AiEnrichedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task Never_overwrites_a_deterministic_value()
    {
        await using var dbContext = CreateDbContext();
        var job = CreateJob();
        job.WorkMode = "Remote";
        job.WorkModeSource = "statement";
        job.Seniority = "Senior";
        job.SenioritySource = "title";
        job.SalaryMin = 100_000m;
        job.SalaryMax = 110_000m;
        job.SalarySource = "source";
        dbContext.Jobs.Add(job);
        await dbContext.SaveChangesAsync();
        var client = new FakeClient(_ => Response(
            workMode: "Hybrid", seniority: "Lead", salaryMin: 200_000, salaryMax: 250_000));

        var service = CreateService(dbContext, client, enabled: true, apiKey: "test-key");
        // No field is missing, so it is not even a candidate.
        var result = await service.RunBatchAsync(10, CancellationToken.None);

        result.Attempted.Should().Be(0);
        client.Calls.Should().Be(0);
        job.WorkMode.Should().Be("Remote");
        job.Seniority.Should().Be("Senior");
        job.SalaryMin.Should().Be(100_000m);
    }

    [Fact]
    public async Task A_low_confidence_or_invalid_answer_is_ignored_but_throttled()
    {
        await using var dbContext = CreateDbContext();
        var job = CreateJob();
        dbContext.Jobs.Add(job);
        await dbContext.SaveChangesAsync();
        var client = new FakeClient(_ => Response(workMode: "Remote", seniority: "Lead", confidence: 0.1m));

        var service = CreateService(dbContext, client, enabled: true, apiKey: "test-key");
        var result = await service.RunBatchAsync(10, CancellationToken.None);

        result.Updated.Should().Be(0);
        job.WorkMode.Should().Be("Unknown");
        job.AiEnrichedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task Failed_calls_are_counted_throttled_and_do_not_block_the_batch()
    {
        await using var dbContext = CreateDbContext();
        dbContext.Jobs.AddRange(CreateJob(), CreateJob());
        await dbContext.SaveChangesAsync();
        var calls = 0;
        var client = new FakeClient(_ => ++calls == 1 ? null : Response(workMode: "Remote"));

        var service = CreateService(dbContext, client, enabled: true, apiKey: "test-key");
        var result = await service.RunBatchAsync(10, CancellationToken.None);

        result.Attempted.Should().Be(2);
        result.Failed.Should().Be(1);
        result.Updated.Should().Be(1);
        dbContext.Jobs.Count(job => job.AiEnrichedAt != null).Should().Be(2);
    }

    [Fact]
    public async Task Batch_is_capped_per_call()
    {
        await using var dbContext = CreateDbContext();
        for (var i = 0; i < 15; i++)
        {
            dbContext.Jobs.Add(CreateJob());
        }

        await dbContext.SaveChangesAsync();
        var client = new FakeClient(_ => Response(workMode: "Remote"));

        var service = CreateService(dbContext, client, enabled: true, apiKey: "test-key");
        var result = await service.RunBatchAsync(5, CancellationToken.None);

        result.Attempted.Should().Be(5);
        client.Calls.Should().Be(5);
    }

    [Fact]
    public async Task A_recently_throttled_job_is_skipped()
    {
        await using var dbContext = CreateDbContext();
        var job = CreateJob();
        job.AiEnrichedAt = DateTime.UtcNow.AddMinutes(-5);
        dbContext.Jobs.Add(job);
        await dbContext.SaveChangesAsync();
        var client = new FakeClient(_ => Response(workMode: "Remote"));

        var service = CreateService(dbContext, client, enabled: true, apiKey: "test-key");
        var result = await service.RunBatchAsync(10, CancellationToken.None);

        result.Attempted.Should().Be(0);
        client.Calls.Should().Be(0);
    }

    [Fact]
    public async Task An_implausible_ai_salary_is_rejected_by_the_central_parser()
    {
        await using var dbContext = CreateDbContext();
        var job = CreateJob();
        dbContext.Jobs.Add(job);
        await dbContext.SaveChangesAsync();
        var client = new FakeClient(_ => Response(salaryMin: 5, salaryMax: 10));

        var service = CreateService(dbContext, client, enabled: true, apiKey: "test-key");
        await service.RunBatchAsync(10, CancellationToken.None);

        job.SalaryMin.Should().BeNull();
        job.SalaryMax.Should().BeNull();
        job.SalarySource.Should().BeNull();
    }

    private static AiFieldEnrichmentService CreateService(
        ApplicationDbContext dbContext,
        IAiFieldEnrichmentClient client,
        bool enabled,
        string apiKey)
    {
        var options = Options.Create(new AiFieldEnrichmentOptions
        {
            Enabled = enabled,
            ApiKey = apiKey,
            Model = "test-model"
        });
        return new AiFieldEnrichmentService(dbContext, client, options);
    }

    private static AiFieldEnrichmentResponse Response(
        string? workMode = null,
        string? seniority = null,
        decimal? salaryMin = null,
        decimal? salaryMax = null,
        decimal? confidence = 0.9m) =>
        new(salaryMin, salaryMax, "NZD", salaryMin is null && salaryMax is null ? null : "Year",
            workMode, seniority, confidence, "test-model");

    private static Job CreateJob() => new()
    {
        Id = Guid.NewGuid(),
        Source = "Test",
        Title = "Engineer",
        DescriptionSummary = "A role.",
        Url = $"https://example.com/jobs/{Guid.NewGuid()}",
        ContentHash = Guid.NewGuid().ToString(),
        WorkMode = "Unknown",
        Seniority = "Intermediate",
        SenioritySource = "default",
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

    private sealed class FakeClient(Func<AiFieldEnrichmentRequest, AiFieldEnrichmentResponse?> handler)
        : IAiFieldEnrichmentClient
    {
        public int Calls { get; private set; }

        public Task<AiFieldEnrichmentResponse?> ExtractAsync(
            AiFieldEnrichmentRequest request,
            CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(handler(request));
        }
    }
}
