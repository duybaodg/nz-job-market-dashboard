using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using FluentAssertions;

namespace Api.Tests;

public sealed class HealthEndpointTests
{
    private const string Password = "s3cret-health-password";

    [Fact]
    public async Task Missing_credentials_return_401_with_a_generic_body()
    {
        await using var host = await HealthApiHost.StartAsync(Password);

        var response = await GetAsync(host.Client);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("unauthorized");
        body.Should().NotContain("Acme");
    }

    [Fact]
    public async Task Wrong_password_returns_401()
    {
        await using var host = await HealthApiHost.StartAsync(Password);

        var response = await GetAsync(host.Client, bearer: "not-the-password");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await response.Content.ReadAsStringAsync()).Should().NotContain("Acme");
    }

    [Fact]
    public async Task Correct_bearer_password_returns_health()
    {
        await using var host = await HealthApiHost.StartAsync(Password);

        var response = await GetAsync(host.Client, bearer: Password);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("Acme");
        body.Should().Contain("healthy");
    }

    [Fact]
    public async Task Correct_X_Health_Password_header_returns_health()
    {
        await using var host = await HealthApiHost.StartAsync(Password);

        var response = await GetAsync(host.Client, xHealth: Password);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync()).Should().Contain("Acme");
    }

    [Fact]
    public async Task Bearer_scheme_is_case_insensitive()
    {
        await using var host = await HealthApiHost.StartAsync(Password);

        var request = new HttpRequestMessage(HttpMethod.Get, "/api/admin/health");
        request.Headers.TryAddWithoutValidation("Authorization", $"bearer {Password}");

        var response = await host.Client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Password_query_parameter_is_ignored()
    {
        await using var host = await HealthApiHost.StartAsync(Password);

        // A correct secret in the query string must NOT authenticate: query
        // strings leak into access logs, history and referrers.
        var response = await GetAsync(host.Client, query: $"?password={Password}");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Fails_closed_when_password_is_unset()
    {
        await using var host = await HealthApiHost.StartAsync(password: null);

        var noHeader = await GetAsync(host.Client);
        var withBearer = await GetAsync(host.Client, bearer: Password);
        var withXHeader = await GetAsync(host.Client, xHealth: Password);
        var withQuery = await GetAsync(host.Client, query: $"?password={Password}");

        noHeader.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        withBearer.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        withXHeader.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        withQuery.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await withBearer.Content.ReadAsStringAsync()).Should().NotContain("Acme");
    }

    [Fact]
    public async Task Rate_limits_after_ten_failures_even_with_the_correct_password()
    {
        var clock = new MutableClock(new DateTimeOffset(2026, 10, 16, 0, 0, 0, TimeSpan.Zero));
        await using var host = await HealthApiHost.StartAsync(Password, clock);

        for (var i = 0; i < 10; i++)
        {
            var failed = await GetAsync(host.Client, bearer: "wrong");
            failed.StatusCode.Should().Be(HttpStatusCode.Unauthorized, "failure {0}", i + 1);
        }

        var blockedWrong = await GetAsync(host.Client, bearer: "wrong");
        var blockedRight = await GetAsync(host.Client, bearer: Password);

        blockedWrong.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
        blockedRight.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);

        var body = await blockedWrong.Content.ReadAsStringAsync();
        body.Should().Contain("too many failed attempts");
        using var json = JsonDocument.Parse(body);
        json.RootElement.GetProperty("error").GetString().Should().Be("too many failed attempts");
    }

    [Fact]
    public async Task Rate_limit_expires_after_the_window()
    {
        var clock = new MutableClock(new DateTimeOffset(2026, 10, 16, 0, 0, 0, TimeSpan.Zero));
        await using var host = await HealthApiHost.StartAsync(Password, clock);

        for (var i = 0; i < 10; i++)
        {
            await GetAsync(host.Client, bearer: "wrong");
        }

        (await GetAsync(host.Client, bearer: Password)).StatusCode.Should().Be(HttpStatusCode.TooManyRequests);

        clock.Advance(TimeSpan.FromMinutes(5) + TimeSpan.FromSeconds(1));

        (await GetAsync(host.Client, bearer: Password)).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    private static Task<HttpResponseMessage> GetAsync(
        HttpClient client,
        string? bearer = null,
        string? xHealth = null,
        string? query = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, $"/api/admin/health{query}");
        if (bearer is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        }

        if (xHealth is not null)
        {
            request.Headers.TryAddWithoutValidation("X-Health-Password", xHealth);
        }

        return client.SendAsync(request);
    }

    private sealed class MutableClock(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset current = now;

        public override DateTimeOffset GetUtcNow() => current;

        public void Advance(TimeSpan delta) => current += delta;
    }
}
