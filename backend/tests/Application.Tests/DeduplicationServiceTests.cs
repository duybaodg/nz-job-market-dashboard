using Application.Deduplication;
using FluentAssertions;

namespace Application.Tests;

public sealed class DeduplicationServiceTests
{
    private readonly DeduplicationService service = new();

    [Fact]
    public void GenerateContentHash_uses_case_insensitive_job_identity()
    {
        var first = new RawJobInput("Seed", null, "Developer", "Acme", "Wellington", null, null, "https://example.com/1", null, null);
        var second = new RawJobInput("seed", null, "developer", "acme", "wellington", null, null, "https://example.com/1", null, null);

        service.GenerateContentHash(first).Should().Be(service.GenerateContentHash(second));
    }

    [Fact]
    public void GenerateContentHash_changes_when_url_changes()
    {
        var first = new RawJobInput("Seed", null, "Developer", "Acme", "Wellington", null, null, "https://example.com/1", null, null);
        var second = first with { Url = "https://example.com/2" };

        service.GenerateContentHash(first).Should().NotBe(service.GenerateContentHash(second));
    }
}
