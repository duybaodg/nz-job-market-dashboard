namespace Application.Tests;

internal sealed class TestClock(DateTime nowUtc) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => new(DateTime.SpecifyKind(nowUtc, DateTimeKind.Utc));
}
