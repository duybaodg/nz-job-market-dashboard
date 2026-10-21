using System.Net;
using FluentAssertions;
using Infrastructure.Firecrawl;
using Microsoft.Extensions.Logging.Abstractions;

namespace Application.Tests;

public sealed class RobotsTxtPolicyTests
{
    [Fact]
    public async Task NotFound_means_no_robots_file_and_is_allowed()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound));
        var policy = CreatePolicy(handler);

        var allowed = await policy.CanFetchAsync("https://example.com/jobs/1", CancellationToken.None);

        allowed.Should().BeTrue();
    }

    [Fact]
    public async Task Gone_means_no_robots_file_and_is_allowed()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.Gone));
        var policy = CreatePolicy(handler);

        (await policy.CanFetchAsync("https://example.com/jobs/1", CancellationToken.None)).Should().BeTrue();
    }

    [Fact]
    public async Task ServerError_fails_closed_and_is_cached_as_a_short_deny()
    {
        var clock = new MutableClock(new DateTimeOffset(2026, 10, 21, 0, 0, 0, TimeSpan.Zero));
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError));
        var policy = CreatePolicy(handler, clock);

        (await policy.CanFetchAsync("https://example.com/jobs/1", CancellationToken.None)).Should().BeFalse();
        (await policy.CanFetchAsync("https://example.com/jobs/2", CancellationToken.None)).Should().BeFalse();
        handler.Calls.Should().Be(1, "the deny is cached for the short TTL");

        clock.Advance(RobotsTxtPolicy.UnavailableTtl + TimeSpan.FromSeconds(1));
        (await policy.CanFetchAsync("https://example.com/jobs/3", CancellationToken.None)).Should().BeFalse();
        handler.Calls.Should().Be(2, "the deny expires and the policy retries");
    }

    [Fact]
    public async Task Timeout_fails_closed()
    {
        var handler = new StubHandler(_ => throw new TaskCanceledException("timed out"));
        var policy = CreatePolicy(handler);

        (await policy.CanFetchAsync("https://example.com/jobs/1", CancellationToken.None)).Should().BeFalse();
    }

    [Fact]
    public async Task Returned_rules_are_enforced()
    {
        const string robots = "User-agent: *\nDisallow: /private/\n";
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(robots)
        });
        var policy = CreatePolicy(handler);

        (await policy.CanFetchAsync("https://example.com/private/job", CancellationToken.None)).Should().BeFalse();
        (await policy.CanFetchAsync("https://example.com/jobs/1", CancellationToken.None)).Should().BeTrue();
    }

    [Fact]
    public async Task Unauthorized_robots_endpoint_fails_closed()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized));
        var policy = CreatePolicy(handler);

        (await policy.CanFetchAsync("https://api.ashbyhq.com/posting-api/job-board/halter", CancellationToken.None))
            .Should().BeFalse();
    }

    private static RobotsTxtPolicy CreatePolicy(StubHandler handler, TimeProvider? clock = null) =>
        new(new FakeHttpClientFactory(handler), NullLogger<RobotsTxtPolicy>.Instance, clock);

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(respond(request));
        }
    }

    private sealed class FakeHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class MutableClock(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset current = now;

        public override DateTimeOffset GetUtcNow() => current;

        public void Advance(TimeSpan delta) => current += delta;
    }
}
