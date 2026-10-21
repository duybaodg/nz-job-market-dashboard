using Api.Health;
using Application.Health;

namespace Api.Security;

/// <summary>
/// Round 15: every <c>/api/admin/*</c> route is protected by the same
/// shared-secret gate as the crawler health view. The health endpoint keeps its
/// own mapping (it returns the same 401/429 contract and is excluded here to
/// avoid double-counting failed attempts). The gate is header-only, constant
/// time, rate-limited and fails closed when <c>HEALTH_ACCESS_PASSWORD</c> is
/// unset.
/// </summary>
public static class AdminAccessMiddleware
{
    public static IApplicationBuilder UseAdminAccess(this IApplicationBuilder app)
    {
        return app.UseWhen(
            context => context.Request.Path.StartsWithSegments("/api/admin")
                && !context.Request.Path.Equals("/api/admin/health", StringComparison.OrdinalIgnoreCase),
            branch => branch.Use(async (context, next) =>
            {
                var guard = context.RequestServices.GetRequiredService<HealthAccessGuard>();
                var decision = guard.Evaluate(
                    HealthEndpoints.ReadPassword(context.Request),
                    HealthEndpoints.ClientKey(context));

                if (decision == HealthAccessDecision.Authorized)
                {
                    await next();
                    return;
                }

                if (decision == HealthAccessDecision.RateLimited)
                {
                    context.Response.StatusCode = StatusCodes.Status429TooManyRequests;
                    await context.Response.WriteAsJsonAsync(new { error = "too many failed attempts" });
                    return;
                }

                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                await context.Response.WriteAsJsonAsync(new { error = "unauthorized" });
            }));
    }
}
