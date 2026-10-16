using Application.Health;
using FluentAssertions;

namespace Application.Tests;

public sealed class HealthAccessGuardTests
{
    private const string Password = "correct horse battery staple";

    [Fact]
    public void Evaluate_fails_closed_when_password_is_unset()
    {
        var guard = new HealthAccessGuard(null);

        guard.IsConfigured.Should().BeFalse();
        guard.Evaluate(null, "1.2.3.4").Should().Be(HealthAccessDecision.Unauthorized);
        guard.Evaluate("anything", "1.2.3.4").Should().Be(HealthAccessDecision.Unauthorized);
        guard.Evaluate(Password, "1.2.3.4").Should().Be(HealthAccessDecision.Unauthorized);
    }

    [Fact]
    public void Evaluate_fails_closed_when_password_is_whitespace()
    {
        var guard = new HealthAccessGuard("   ");

        guard.IsConfigured.Should().BeFalse();
        guard.Evaluate("   ", "1.2.3.4").Should().Be(HealthAccessDecision.Unauthorized);
    }

    [Fact]
    public void Evaluate_rejects_missing_and_wrong_passwords()
    {
        var guard = new HealthAccessGuard(Password);

        guard.Evaluate(null, "1.2.3.4").Should().Be(HealthAccessDecision.Unauthorized);
        guard.Evaluate("", "1.2.3.4").Should().Be(HealthAccessDecision.Unauthorized);
        guard.Evaluate("wrong", "1.2.3.4").Should().Be(HealthAccessDecision.Unauthorized);
        // Case matters: the comparison hashes the exact bytes.
        guard.Evaluate(Password.ToUpperInvariant(), "1.2.3.4").Should().Be(HealthAccessDecision.Unauthorized);
    }

    [Fact]
    public void Evaluate_accepts_the_configured_password()
    {
        var guard = new HealthAccessGuard(Password);

        guard.IsConfigured.Should().BeTrue();
        guard.Evaluate(Password, "1.2.3.4").Should().Be(HealthAccessDecision.Authorized);
    }

    [Fact]
    public void Evaluate_resets_a_clients_failures_after_a_success()
    {
        var guard = new HealthAccessGuard(Password);

        for (var i = 0; i < HealthAccessGuard.MaxFailures - 1; i++)
        {
            guard.Evaluate("wrong", "1.2.3.4").Should().Be(HealthAccessDecision.Unauthorized);
        }

        guard.Evaluate(Password, "1.2.3.4").Should().Be(HealthAccessDecision.Authorized);
        // The success cleared the bucket, so the next wrong guess is a plain 401.
        guard.Evaluate("wrong", "1.2.3.4").Should().Be(HealthAccessDecision.Unauthorized);
    }

    [Fact]
    public void Evaluate_rate_limits_after_ten_failures_and_recovers_after_the_window()
    {
        var clock = new MutableClock(new DateTimeOffset(2026, 10, 16, 0, 0, 0, TimeSpan.Zero));
        var guard = new HealthAccessGuard(Password, clock);

        for (var i = 0; i < HealthAccessGuard.MaxFailures; i++)
        {
            guard.Evaluate("wrong", "1.2.3.4").Should().Be(HealthAccessDecision.Unauthorized);
        }

        // Blocked even with the right password: brute force is throttled, and a
        // blocked caller cannot sidestep the limiter.
        guard.Evaluate("wrong", "1.2.3.4").Should().Be(HealthAccessDecision.RateLimited);
        guard.Evaluate(Password, "1.2.3.4").Should().Be(HealthAccessDecision.RateLimited);

        // A different client keeps its own bucket.
        guard.Evaluate("wrong", "5.6.7.8").Should().Be(HealthAccessDecision.Unauthorized);

        clock.Advance(HealthAccessGuard.FailureWindow + TimeSpan.FromSeconds(1));
        guard.Evaluate(Password, "1.2.3.4").Should().Be(HealthAccessDecision.Authorized);
    }

    [Fact]
    public void Evaluate_does_not_rate_limit_when_no_password_is_configured()
    {
        var guard = new HealthAccessGuard(null);

        for (var i = 0; i < HealthAccessGuard.MaxFailures + 5; i++)
        {
            // Fail-closed deployments stay 401 rather than turning into 429s.
            guard.Evaluate("anything", "1.2.3.4").Should().Be(HealthAccessDecision.Unauthorized);
        }
    }

    private sealed class MutableClock(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset current = now;

        public override DateTimeOffset GetUtcNow() => current;

        public void Advance(TimeSpan delta) => current += delta;
    }
}
