using FluentAssertions;
using Infrastructure.Firecrawl;
using Microsoft.Extensions.Options;

namespace Application.Tests;

public sealed class FirecrawlClientTests
{
    [Fact]
    public async Task ScrapeAsync_returns_clear_error_when_api_key_is_missing()
    {
        var client = new FirecrawlClient(new HttpClient(), Options.Create(new FirecrawlOptions()));

        var act = () => client.ScrapeAsync("https://example.com/jobs/1", CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("Firecrawl is disabled*");
    }
}
