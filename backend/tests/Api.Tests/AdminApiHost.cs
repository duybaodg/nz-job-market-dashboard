using Api.Security;
using Application.Health;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Api.Tests;

/// <summary>
/// Boots the production admin-access middleware over a catch-all admin route so
/// every documented admin path is exercised through the same gate as the real
/// host, without needing a database-backed service graph.
/// </summary>
internal sealed class AdminApiHost : IAsyncDisposable
{
    private readonly WebApplication app;

    private AdminApiHost(WebApplication app, HttpClient client)
    {
        this.app = app;
        Client = client;
    }

    public HttpClient Client { get; }

    public static async Task<AdminApiHost> StartAsync(string? password)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = "Production",
        });
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton(new HealthAccessGuard(password));

        var app = builder.Build();
        app.UseAdminAccess();
        app.MapMethods(
            "/api/admin/{**path}",
            ["GET", "POST", "DELETE"],
            () => Results.Ok(new { ok = true }));
        await app.StartAsync();

        var addresses = app.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()!;
        var client = new HttpClient { BaseAddress = new Uri(addresses.Addresses.First()) };
        return new AdminApiHost(app, client);
    }

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await app.StopAsync();
        await app.DisposeAsync();
    }
}
