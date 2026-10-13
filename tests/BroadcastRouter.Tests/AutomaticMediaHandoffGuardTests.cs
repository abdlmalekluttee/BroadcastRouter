using System.Collections;
using System.Collections.Concurrent;
using System.Reflection;
using BroadcastRouter.Application;
using BroadcastRouter.Domain;
using BroadcastRouter.Infrastructure;
using BroadcastRouter.Web.Services;
using Xunit;

namespace BroadcastRouter.Tests;

public sealed class AutomaticMediaHandoffGuardTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    [Theory]
    [InlineData("epoch")]
    [InlineData("settings")]
    [InlineData("override")]
    [InlineData("emergency")]
    [InlineData("expired")]
    [InlineData("management-unknown")]
    [InlineData("valid")]
    public async Task InvalidGrowthEvidenceNeverStopsOwner(string invalidation)
    {
        await using var lab = await Ready();
        var original = lab.Owner.SessionId;
        if (invalidation == "management-unknown")
            typeof(RouterCoordinator).GetMethod("StampPublisherObservation", Private)!.Invoke(lab.Coordinator,
                [lab.Source.Value, "TEST", null, 1L]);
        var policy = Seed(lab);
        if (invalidation == "epoch") policy.NewPublisherEpoch();
        if (invalidation == "settings") lab.Settings.ConfigurationRevision++;
        if (invalidation == "override") lab.Settings.SourceMediaProfiles[0].Override = SourceMediaOverride.ForceAudioOnly;
        if (invalidation == "emergency") lab.Set("_emergencyStopped", true);
        if (invalidation == "expired") lab.Time.Advance(TimeSpan.FromSeconds(3));
        var stops = lab.Events.Count(e => e.State == RouteProcessLifecycleState.StopRequested);
        await lab.Invoke("RecordMediaModeChangesAsync", CancellationToken.None);
        if (invalidation == "valid")
        {
            Assert.NotEqual(original, lab.Owner.SessionId);
            Assert.Equal(stops + 1, lab.Events.Count(e => e.State == RouteProcessLifecycleState.StopRequested));
            return;
        }
        Assert.Equal(original, lab.Owner.SessionId);
        Assert.Equal(stops, lab.Events.Count(e => e.State == RouteProcessLifecycleState.StopRequested));
    }

    [Fact]
    public async Task OwnerReplacementWhileWaitingForPortGateCannotStopTheNewOwner()
    {
        await using var lab = await Ready();
        Seed(lab);
        var port = lab.Get<Dictionary<string, DeckLinkPort>>("_ports")["TEST-OUTPUT"];
        var portGate = lab.Get<ConcurrentDictionary<string, SemaphoreSlim>>("_portGates").GetOrAdd(port.StableId, _ => new(1, 1));
        await portGate.WaitAsync();
        var attempt = lab.Invoke("RecordMediaModeChangesAsync", CancellationToken.None);
        try
        {
            Assert.False(attempt.IsCompleted);
            await lab.Supervisor.StopAsync(lab.Source, CancellationToken.None);
            await lab.Supervisor.StartAsync(new(lab.Source, port.StableId, lab.Route.PresetId, RouteState.Starting,
                AssignmentMode.Manual, true), lab.Sources[lab.Source.Value], port, lab.Settings.Presets[0].ToDomain(), CancellationToken.None);
            var replacement = lab.Owner.SessionId;
            var stops = lab.Events.Count(e => e.State == RouteProcessLifecycleState.StopRequested);
            portGate.Release();
            await attempt;
            Assert.Equal(replacement, lab.Owner.SessionId);
            Assert.Equal(stops, lab.Events.Count(e => e.State == RouteProcessLifecycleState.StopRequested));
        }
        finally { if (portGate.CurrentCount == 0) portGate.Release(); await attempt; }
    }

    private static async Task<OfflineFallbackTests.RecoveryLab> Ready()
    {
        var lab = await OfflineFallbackTests.RecoveryLab.Create();
        try
        {
            await lab.InstallFakeMediaTools();
            await DeclaredMediaCoordinatorTests.WriteProbe(lab, AutomaticMediaCoordinatorTests.Json(false, true));
            await AutomaticMediaCoordinatorTests.Poll(lab);
            lab.Routes[lab.Source.Value] = lab.Route with { State = RouteState.Running };
            return lab;
        }
        catch { await lab.DisposeAsync(); throw; }
    }

    private static MediaSessionProfilePolicy Seed(OfflineFallbackTests.RecoveryLab lab)
    {
        var policy = lab.Get<ConcurrentDictionary<string, MediaSessionProfilePolicy>>("_mediaProfilePolicies").GetOrAdd(lab.Source.Value, _ => new());
        var session = policy.Attach(lab.Owner.SessionId, lab.Owner.SelectedMedia!);
        var signature = typeof(RouterCoordinator).GetMethod("RecoveryConfigurationSignature", Private)!.Invoke(lab.Coordinator,
            [lab.Sources[lab.Source.Value], lab.Settings.Presets[0]])!;
        var pendingType = typeof(RouterCoordinator).GetNestedType("PendingMediaGrowth", BindingFlags.NonPublic)!;
        var evidence = new MediaGrowthObservation(MediaProfileUpgradeTests.Evidence(), TimeSpan.FromSeconds(10), lab.Clock.Now);
        var pending = Activator.CreateInstance(pendingType, session, signature, Task.FromResult(evidence));
        lab.Get<IDictionary>("_mediaGrowth")[lab.Source.Value] = pending;
        return policy;
    }
}
