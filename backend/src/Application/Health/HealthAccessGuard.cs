using System.Security.Cryptography;
using System.Text;

namespace Application.Health;

/// <summary>Outcome of a crawler-health access check.</summary>
public enum HealthAccessDecision
{
    Authorized,
    Unauthorized,
    RateLimited,
}

/// <summary>
/// Shared-secret gate for the crawler health view. The password is supplied once
/// from configuration; when it is missing the guard fails closed and authorises
/// nothing. Comparisons are constant-time (SHA-256 + FixedTimeEquals, so neither
/// value's length leaks) and failed attempts are rate-limited in memory per
/// client key.
/// </summary>
public sealed class HealthAccessGuard
{
    /// <summary>Failed attempts allowed per client inside <see cref="FailureWindow"/>.</summary>
    public const int MaxFailures = 10;

    /// <summary>How long a client's failure count is remembered.</summary>
    public static readonly TimeSpan FailureWindow = TimeSpan.FromMinutes(5);

    // Bound the per-process dictionary so a spray of one-off client keys cannot
    // grow it without limit; only consulted once the map is already large.
    private const int MaxTrackedClients = 4096;

    private readonly string? password;
    private readonly TimeProvider timeProvider;
    private readonly object gate = new();
    private readonly Dictionary<string, FailureState> failures = new(StringComparer.Ordinal);

    public HealthAccessGuard(string? password, TimeProvider? timeProvider = null)
    {
        this.password = string.IsNullOrWhiteSpace(password) ? null : password;
        this.timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>False when HEALTH_ACCESS_PASSWORD is unset: every request is refused.</summary>
    public bool IsConfigured => password is not null;

    public HealthAccessDecision Evaluate(string? presentedPassword, string clientKey)
    {
        var key = string.IsNullOrEmpty(clientKey) ? "unknown" : clientKey;
        var now = timeProvider.GetUtcNow();

        lock (gate)
        {
            if (password is null)
            {
                // Fail closed: without a configured secret nothing is authorised.
                return HealthAccessDecision.Unauthorized;
            }

            PruneIfNeeded(now);

            if (failures.TryGetValue(key, out var state))
            {
                if (now - state.FirstFailure >= FailureWindow)
                {
                    failures.Remove(key);
                }
                else if (state.Count >= MaxFailures)
                {
                    return HealthAccessDecision.RateLimited;
                }
            }

            if (FixedTimeEquals(password, presentedPassword))
            {
                failures.Remove(key);
                return HealthAccessDecision.Authorized;
            }

            if (!failures.TryGetValue(key, out state))
            {
                state = new FailureState { FirstFailure = now };
                failures[key] = state;
            }

            state.Count++;
            return HealthAccessDecision.Unauthorized;
        }
    }

    private void PruneIfNeeded(DateTimeOffset now)
    {
        if (failures.Count <= MaxTrackedClients)
        {
            return;
        }

        var expired = failures
            .Where(pair => now - pair.Value.FirstFailure >= FailureWindow)
            .Select(pair => pair.Key)
            .ToList();
        foreach (var key in expired)
        {
            failures.Remove(key);
        }
    }

    private static bool FixedTimeEquals(string expected, string? presented)
    {
        if (presented is null)
        {
            return false;
        }

        var expectedHash = SHA256.HashData(Encoding.UTF8.GetBytes(expected));
        var presentedHash = SHA256.HashData(Encoding.UTF8.GetBytes(presented));
        return CryptographicOperations.FixedTimeEquals(expectedHash, presentedHash);
    }

    private sealed class FailureState
    {
        public DateTimeOffset FirstFailure { get; init; }

        public int Count { get; set; }
    }
}
