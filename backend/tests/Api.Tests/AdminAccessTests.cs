using System.Net;
using System.Net.Http.Headers;
using FluentAssertions;

namespace Api.Tests;

public sealed class AdminAccessTests
{
    private const string Password = "s3cret-admin-password";

    // Every /api/admin route documented in Program.cs. POST /crawl-runs is the
    // SSRF trigger; the rest mutate or expose operational data.
    public static TheoryData<string, string> AdminRoutes => new()
    {
        { "POST", "/api/admin/crawl-runs" },
        { "GET", "/api/admin/crawl-runs" },
        { "GET", "/api/admin/sources" },
        { "GET", "/api/admin/coverage" },
        { "GET", $"/api/admin/crawl-runs/{Guid.NewGuid()}" },
        { "GET", "/api/admin/employers" },
        { "POST", "/api/admin/employers" },
        { "POST", $"/api/admin/employers/{Guid.NewGuid()}/aliases" },
        { "DELETE", $"/api/admin/employers/{Guid.NewGuid()}/aliases/Acme" },
        { "DELETE", $"/api/admin/employers/{Guid.NewGuid()}/aliases?alias=Acme" },
        { "POST", "/api/admin/employers/seed" },
        { "POST", "/api/admin/employers/match" },
        { "GET", "/api/admin/duplicates" },
        { "POST", "/api/admin/dedup/run" },
        { "POST", "/api/admin/salaries/audit" },
    };

    [Theory]
    [MemberData(nameof(AdminRoutes))]
    public async Task Every_admin_route_returns_401_without_the_secret(string method, string path)
    {
        await using var host = await AdminApiHost.StartAsync(Password);

        var response = await host.Client.SendAsync(new HttpRequestMessage(new HttpMethod(method), path));

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await response.Content.ReadAsStringAsync()).Should().Contain("unauthorized");
    }

    [Theory]
    [MemberData(nameof(AdminRoutes))]
    public async Task Every_admin_route_accepts_the_bearer_secret(string method, string path)
    {
        await using var host = await AdminApiHost.StartAsync(Password);
        var request = new HttpRequestMessage(new HttpMethod(method), path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Password);

        var response = await host.Client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Admin_routes_fail_closed_when_the_password_is_unset()
    {
        await using var host = await AdminApiHost.StartAsync(password: null);
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/admin/sources");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Password);

        var response = await host.Client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task X_Health_Password_header_also_authorises_admin_routes()
    {
        await using var host = await AdminApiHost.StartAsync(Password);
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/admin/sources");
        request.Headers.TryAddWithoutValidation("X-Health-Password", Password);

        var response = await host.Client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Password_query_parameter_is_not_accepted_for_admin_routes()
    {
        await using var host = await AdminApiHost.StartAsync(Password);

        var response = await host.Client.GetAsync($"/api/admin/sources?password={Password}");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }
}
