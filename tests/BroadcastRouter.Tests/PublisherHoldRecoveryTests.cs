using System.Reflection;
using BroadcastRouter.Application;
using BroadcastRouter.Domain;
using BroadcastRouter.Infrastructure;
using BroadcastRouter.Web.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BroadcastRouter.Tests;

public sealed class PublisherHoldRecoveryTests
{
    [Fact]
    public void HoldCannotSuppressCorroboratedPcmLossDespiteFreshLivePublisher()
    {
        var time = new ManualTimeProvider();
        var clock = new SupervisionClock(time);
        var policy = new PublisherLiveHoldPolicy(clock);
        var route = Route(clock.Now);
        var evidence = QualifiedAudio(clock.Now);
        time.Advance(TimeSpan.FromSeconds(25));
        policy.Observe(route.SourceId, true);
        evidence.ObserveHeartbeat(clock.Now);
        var failed = evidence.Snapshot(clock.Now, true);
        Assert.True(failed.ConfirmedMediaFailure);
        Assert.False(policy.ShouldHold(route, true, true, failed));
    }

    [Fact]
    public void HoldStillSuppressesSpeculationAndSinglePointFailure()
    {
        var time = new ManualTimeProvider();
        var clock = new SupervisionClock(time);
        var policy = new PublisherLiveHoldPolicy(clock);
        var route = Route(clock.Now);
        var evidence = QualifiedAudio(clock.Now);
        time.Advance(TimeSpan.FromSeconds(25));
        evidence.Observe(MediaSignalKind.OutputAudio, 100, clock.Now);
        policy.Observe(route.SourceId, true);
        Assert.True(policy.ShouldHold(route, true, true, evidence.Snapshot(clock.Now,true)));
        Assert.True(policy.ShouldHold(route, true, true, null));
        Assert.False(policy.ShouldHold(route, true, false, null));
    }

    [Theory]
    [InlineData(true, MediaSignalState.Healthy)]
    [InlineData(false, MediaSignalState.Failed)]
    public void ExpiredPresenceIsExplicitlyUnknownNotPublisherAbsent(bool live, MediaSignalState fresh)
    {
        var time = new ManualTimeProvider();
        var clock = new SupervisionClock(time);
        var policy = new PublisherLiveHoldPolicy(clock);
        Assert.Equal(MediaSignalState.Unknown,policy.Presence("TEST"));
        policy.Observe("TEST",live);
        Assert.Equal(fresh,policy.Presence("TEST"));
        time.Advance(TimeSpan.FromSeconds(10));
        time.StepUtc(TimeSpan.FromHours(-12));
        Assert.Equal(MediaSignalState.Unknown,policy.Presence("TEST"));
    }

    [Fact]
    public void CoordinatorPassesTheOwnedSessionsEvidenceToHoldPolicy()
    {
        var time = new ManualTimeProvider();
        var clock = new SupervisionClock(time);
        using var coordinator = new RouterCoordinator(null!,null!,null!,NullLogger<RouterCoordinator>.Instance,clock);
        var route = Route(clock.Now);
        var source = new SourceIdentity("TEST","fixture","_default_","synthetic");
        Get<Dictionary<string,DiscoveredSource>>(coordinator,"_sources")[source.Value] =
            new(source,"Synthetic",new Uri("rtsp://example.invalid/fixture"),SourceState.Ready,0);
        Get<OperatorSettings>(coordinator,"_settings").WowzaServers.Add(new() { ServerId="TEST",Enabled=true });
        var tracker = QualifiedAudio(clock.Now);
        time.Advance(TimeSpan.FromSeconds(25));
        Get<PublisherLiveHoldPolicy>(coordinator,"_publisherLiveHold").Observe(source.Value,true);
        var process = new RouteProcessSnapshot(source,RouteProcessPurpose.Live,123,clock.Now.AddSeconds(-25),true,
            null,[],null,null,null!,IndependentMedia:tracker.Snapshot(clock.Now,true));
        var method = typeof(RouterCoordinator).GetMethod("ShouldHoldLiveOutput",BindingFlags.Instance|BindingFlags.NonPublic,
            null,[typeof(RuntimeRoute),typeof(RouteProcessSnapshot)],null)!;
        Assert.False((bool)method.Invoke(coordinator,[route,process])!);
    }

    private static MediaEvidenceTracker QualifiedAudio(DateTimeOffset start)
    {
        var tracker = new MediaEvidenceTracker([MediaSignalKind.InputAudio,MediaSignalKind.OutputAudio],start);
        for (var i=0;i<=4;i++)
            foreach (var kind in new[] { MediaSignalKind.InputAudio,MediaSignalKind.OutputAudio })
                tracker.Observe(kind,(i+1)*1024,start.AddMilliseconds(i*500));
        return tracker;
    }
    private static RuntimeRoute Route(DateTimeOffset now) => new("TEST/fixture/_default_/synthetic","Synthetic","port","Port",
        "1080i50",RouteState.Running,AssignmentMode.Manual,false,0,0,1,25,1,0,0,now,now,null,null,HoldOutputWhilePublisherLive:true);
    private static T Get<T>(object instance,string field) => (T)typeof(RouterCoordinator)
        .GetField(field,BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(instance)!;
}
