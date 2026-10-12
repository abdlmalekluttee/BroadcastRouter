using BroadcastRouter.Application;
using Xunit;

namespace BroadcastRouter.Tests;

public sealed class StandbyRetryBudgetTests
{
    private static dynamic Create()
    {
        var type = typeof(SupervisionClock).Assembly.GetType("BroadcastRouter.Application.StandbyRetryBudget");
        Assert.NotNull(type);
        return Activator.CreateInstance(type)!;
    }

    [Fact]
    public void FullDayOfEarlyFailuresHasFewerThanThreeHundredStarts()
    {
        dynamic budget = Create();
        var start = DateTimeOffset.Parse("2025-01-01T00:00:00Z");
        var starts = 0;
        var circuitAt = (DateTimeOffset?)null;
        DateTimeOffset? lastCircuitAttempt = null;
        for (var tick = 0; tick < 864000; tick++)
        {
            var now = start.AddMilliseconds(100 * tick);
            if (!(bool)budget.CanAttempt(now)) continue;
            starts++;
            if ((bool)budget.CircuitOpen)
            {
                if (lastCircuitAttempt is not null) Assert.True(now - lastCircuitAttempt >= TimeSpan.FromMinutes(5));
                lastCircuitAttempt = now;
            }
            Assert.True((bool)budget.RecordFailure(Guid.NewGuid(), now.AddMilliseconds(100), "synthetic failure", 0d));
            if ((bool)budget.CircuitOpen) circuitAt ??= now;
        }
        Assert.InRange(starts, 288, 299);
        Assert.NotNull(circuitAt);
        Assert.True(circuitAt - start < TimeSpan.FromMinutes(2));
    }

    [Fact]
    public void SameFailedSessionCannotSpendBudgetRepeatedly()
    {
        dynamic budget = Create();
        var now = DateTimeOffset.Parse("2025-01-01T00:00:00Z");
        var session = Guid.NewGuid();
        Assert.True((bool)budget.RecordFailure(session, now, "failure", 0d));
        Assert.False((bool)budget.RecordFailure(session, now.AddSeconds(1), "failure", 0d));
        Assert.Equal(now.AddSeconds(1), (DateTimeOffset)budget.NextAttempt);
    }

    [Fact]
    public void JitterDoesNotShortenCircuitOpenDeadline()
    {
        dynamic budget = Create();
        var now = DateTimeOffset.Parse("2025-01-01T00:00:00Z");
        for (var i = 0; i < 3; i++) budget.RecordFailure(Guid.NewGuid(), now.AddSeconds(i * 10), "failure", 0.5d);
        Assert.True((bool)budget.CircuitOpen);
        Assert.False((bool)budget.CanAttempt(now.AddSeconds(319)));
        Assert.True((DateTimeOffset)budget.NextAttempt >= now.AddSeconds(320));
    }

    [Fact]
    public void FirstFrameDoesNotCloseCircuitButStableMinuteDoes()
    {
        dynamic budget = Create();
        var now = DateTimeOffset.Parse("2025-01-01T00:00:00Z");
        for (var i = 0; i < 3; i++) budget.RecordFailure(Guid.NewGuid(), now.AddSeconds(i * 10), "failure", 0d);
        var session = Guid.NewGuid();
        for (var i = 0; i <= 60; i++)
        {
            var at = now.AddSeconds(400 + i);
            budget.ObserveProgress(session, i + 1L, TimeSpan.FromSeconds(i + 1), at, at);
            Assert.Equal(i < 60, (bool)budget.CircuitOpen);
        }
        Assert.Null((string?)budget.LastError);
    }
}
