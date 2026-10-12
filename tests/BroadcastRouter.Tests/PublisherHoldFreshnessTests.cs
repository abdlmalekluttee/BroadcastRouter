using BroadcastRouter.Application;
using BroadcastRouter.Domain;
using BroadcastRouter.Web.Services;
using Microsoft.Extensions.Logging.Abstractions;
using System.Reflection;
using Xunit;

namespace BroadcastRouter.Tests;

public sealed class PublisherHoldFreshnessTests
{
    [Fact]
    public void CoordinatorUsesItsMonotonicClockForHoldExpiry()
    {
        var time = new ManualTimeProvider();
        var clock = new SupervisionClock(time);
        using var coordinator = new RouterCoordinator(null!, null!, null!, NullLogger<RouterCoordinator>.Instance, clock);
        var policy = (PublisherLiveHoldPolicy)typeof(RouterCoordinator)
            .GetField("_publisherLiveHold", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(coordinator)!;
        var route = HeldRoute(clock.Now);
        policy.Observe(route.SourceId, true);
        Assert.True(policy.ShouldHold(route, true, true));
        time.Advance(TimeSpan.FromSeconds(11));
        time.StepUtc(TimeSpan.FromHours(-12));
        Assert.False(policy.ShouldHold(route, true, true));
    }

    [Fact]
    public void PublisherEvidenceExpiresWithoutPretendingPublisherIsOffline()
    {
        var time = new ManualTimeProvider();
        var policy = new PublisherLiveHoldPolicy(new SupervisionClock(time));
        var route = HeldRoute(time.GetUtcNow());
        policy.Observe(route.SourceId, true);
        time.Advance(TimeSpan.FromSeconds(9));
        Assert.True(policy.ShouldHold(route, true, true));
        time.Advance(TimeSpan.FromSeconds(1));
        Assert.False(policy.ShouldHold(route, true, true));
    }

    [Fact]
    public void WallClockStepCannotRenewStalePublisherEvidence()
    {
        var time = new ManualTimeProvider();
        var policy = new PublisherLiveHoldPolicy(new SupervisionClock(time));
        var route = HeldRoute(time.GetUtcNow());
        policy.Observe(route.SourceId, true);
        time.Advance(TimeSpan.FromSeconds(11));
        time.StepUtc(TimeSpan.FromHours(-12));
        Assert.False(policy.ShouldHold(route, true, true));
    }

    [Fact]
    public void SeedCannotRefreshOldEvidenceButNewAuthoritativeObservationCan()
    {
        var time = new ManualTimeProvider();
        var policy = new PublisherLiveHoldPolicy(new SupervisionClock(time));
        var route = HeldRoute(time.GetUtcNow());
        policy.SeedConnected(route.SourceId);
        time.Advance(TimeSpan.FromSeconds(11));
        policy.SeedConnected(route.SourceId);
        Assert.False(policy.ShouldHold(route, true, true));
        policy.Observe(route.SourceId, true);
        Assert.True(policy.ShouldHold(route, true, true));
    }

    private static RuntimeRoute HeldRoute(DateTimeOffset now) => new("QA/live/_definst_/synthetic", "Synthetic", "port", "Port",
        "1080i50", RouteState.Running, AssignmentMode.Manual, false, 0, 0, 1, 25, 1, 0, 0, now, now, null, null,
        HoldOutputWhilePublisherLive: true);
}
