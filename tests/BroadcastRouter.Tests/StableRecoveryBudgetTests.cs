using BroadcastRouter.Application;
using Xunit;

namespace BroadcastRouter.Tests;

public sealed class StableRecoveryBudgetTests
{
    [Fact]
    public void RepeatedStaleSampleNeverQualifiesRecovery()
    {
        var policy = new StableRecoveryBudget();
        var now = DateTimeOffset.Parse("2025-01-01T00:00:00Z");
        var session = Guid.NewGuid();
        for (var i = 0; i < 120; i++)
            Assert.False(policy.Observe(session, 1, TimeSpan.FromSeconds(1), now, now.AddSeconds(i), true));
        Assert.False(policy.Observe(session, 2, TimeSpan.FromSeconds(2), now.AddSeconds(120), now.AddSeconds(120), true));
    }

    private static dynamic Policy()
    {
        var type = typeof(SupervisionClock).Assembly.GetType("BroadcastRouter.Application.StableRecoveryBudget");
        Assert.NotNull(type);
        return Activator.CreateInstance(type)!;
    }

    [Fact]
    public void DebtResetsOnlyAfterSixtySecondsOfContinuousAdvancement()
    {
        dynamic policy = Policy();
        var start = DateTimeOffset.Parse("2025-01-01T00:00:00Z");
        var session = Guid.NewGuid();
        for (var second = 0; second <= 60; second++)
            Assert.Equal(second == 60, (bool)policy.Observe(session, second + 1L, TimeSpan.FromSeconds(second + 1),
                start.AddSeconds(second), start.AddSeconds(second), true));
    }

    [Theory]
    [InlineData("stale-progress")]
    [InlineData("no-advance")]
    [InlineData("media-regression")]
    [InlineData("anomaly")]
    [InlineData("session-change")]
    [InlineData("observation-gap")]
    public void InterruptedEvidenceStartsNewStabilityWindow(string fault)
    {
        dynamic policy = Policy();
        var start = DateTimeOffset.Parse("2025-01-01T00:00:00Z");
        var session = Guid.NewGuid();
        for (var second = 0; second < 59; second++)
            Assert.False((bool)policy.Observe(session, second + 1L, TimeSpan.FromSeconds(second + 1),
                start.AddSeconds(second), start.AddSeconds(second), true));
        var now = start.AddSeconds(fault == "observation-gap" ? 62 : 59);
        Assert.False((bool)policy.Observe(fault == "session-change" ? Guid.NewGuid() : session,
            fault == "no-advance" ? 59L : 60L, TimeSpan.FromSeconds(fault == "media-regression" ? 1 : 60),
            fault == "stale-progress" ? start : now, now, fault != "anomaly"));
        Assert.False((bool)policy.Observe(session, 61L, TimeSpan.FromSeconds(61), now.AddSeconds(1), now.AddSeconds(1), true));
    }
}
