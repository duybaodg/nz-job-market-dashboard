using Application.Health;
using Api.Health;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Api.Tests;

/// <summary>
/// Boots a real in-process Kestrel host that maps the production health endpoint
/// (<see cref="HealthEndpoints.MapHealthEndpoint"/>) over a stub health service.
/// Avoids the database startup path while still exercising real HTTP, headers,
/// routing, status codes and the rate limiter.
/// </summary>
internal sealed class HealthApiHost : IAsyncDisposable
{
    private readonly WebApplication app;

    private HealthApiHost(WebApplication app, HttpClient client)
    {
        this.app = app;
        Client = client;
    }

    public HttpClient Client { get; }

    public static async Task<HealthApiHost> StartAsync(string? password, TimeProvider? timeProvider = null)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = "Production",
        });
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton<IHealthService, StubHealthService>();
        builder.Services.AddSingleton(new HealthAccessGuard(password, timeProvider));

        var app = builder.Build();
        app.MapHealthEndpoint();
        await app.StartAsync();

        var addresses = app.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()!;
        var client = new HttpClient { BaseAddress = new Uri(addresses.Addresses.First()) };
        return new HealthApiHost(app, client);
    }

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await app.StopAsync();
        await app.DisposeAsync();
    }
}
