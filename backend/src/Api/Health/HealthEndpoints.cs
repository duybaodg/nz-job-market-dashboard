using Application.Health;

namespace Api.Health;

/// <summary>
/// Maps the password-protected crawler health endpoint. Kept as an extension so
/// the production host and the HTTP integration tests exercise the same wiring.
/// </summary>
public static class HealthEndpoints
{
    public static IEndpointRouteBuilder MapHealthEndpoint(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/admin/health", async (
                HttpContext context,
                IHealthService healthService,
                HealthAccessGuard guard,
                CancellationToken cancellationToken) =>
            {
                var decision = guard.Evaluate(ReadPassword(context.Request), ClientKey(context));
                return decision switch
                {
                    HealthAccessDecision.Authorized => Results.Ok(
                        await healthService.GetHealthAsync(cancellationToken)),
                    HealthAccessDecision.RateLimited => Results.Json(
                        new { error = "too many failed attempts" },
                        statusCode: StatusCodes.Status429TooManyRequests),
                    _ => Results.Json(
                        new { error = "unauthorized" },
                        statusCode: StatusCodes.Status401Unauthorized),
                };
            })
            .WithName("GetHealth");

        return endpoints;
    }

    /// <summary>
    /// Reads only the Authorization: Bearer and X-Health-Password headers. The
    /// query string is deliberately never consulted so the password cannot leak
    /// into logs, browser history or referrers.
    /// </summary>
    internal static string? ReadPassword(HttpRequest request)
    {
        var authorization = request.Headers.Authorization.ToString();
        if (authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            return Normalize(authorization["Bearer ".Length..]);
        }

        return Normalize(request.Headers["X-Health-Password"].ToString());
    }

    internal static string ClientKey(HttpContext context)
        => context.Connection.RemoteIpAddress?.ToString() ?? "unknown";

    private static string? Normalize(string value)
    {
        var trimmed = value.Trim();
        return trimmed.Length == 0 ? null : trimmed;
    }
}
