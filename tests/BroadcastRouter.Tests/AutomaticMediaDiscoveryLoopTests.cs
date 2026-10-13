using System.Collections.Concurrent;
using System.Reflection;
using BroadcastRouter.Application;
using BroadcastRouter.Domain;
using BroadcastRouter.Infrastructure;
using Xunit;

namespace BroadcastRouter.Tests;

public sealed class AutomaticMediaDiscoveryLoopTests
{
    [Theory]
    [InlineData(true, 3, 2)]
    [InlineData(false, 3, 2)]
    [InlineData(true, 12, 2)]
    [InlineData(true, 3, 10)]
    [InlineData(false, 3, 10)]
    public async Task DiscoveryAndReconciliationAdmitHealthySourceWithoutAwaitingDetection(bool savedAssignment, int sourceCount, int discoveryInterval)
    {
        await using var lab = await OfflineFallbackTests.RecoveryLab.Create(new AbsentManagement());
        await lab.InstallFakeMediaTools();
        await lab.Supervisor.StopAsync(lab.Source, CancellationToken.None);
        lab.Routes.Clear();
        lab.Sources.Clear();
        lab.Get<PortReservationManager>("_reservations").Release("TEST-OUTPUT", lab.Source, force: true);
        lab.Settings.WowzaServers.Clear();
        if (discoveryInterval != 2) lab.Settings.WowzaServers.Add(new() { ServerId = "unused", Enabled = true,
            ManagementUrl = "http://example.invalid/", Applications = "fixture", ApplicationInstances = "_default_",
            PollingIntervalSeconds = discoveryInterval });
        lab.Settings.Routing.AutomaticRoutingEnabled = !savedAssignment;
        var target = new SourceIdentity("MANUAL", "manual", "_definst_", "test-a");
        foreach (var id in Enumerable.Range(0, sourceCount).Select(i => "test-" + (char)('a' + i)))
        {
            lab.Settings.ManualSources.Add(new() { StableId = id, FriendlyName = id,
                RtspUrl = "rtsp://example.invalid/" + id, Enabled = true });
            var identity = new SourceIdentity("MANUAL", "manual", "_definst_", id);
            lab.Sources[identity.Value] = new(identity, id, new Uri("rtsp://example.invalid/" + id),
                SourceState.PublisherActive, id == "test-a" ? 10 : 0, LastObservedAt: lab.Clock.Now);
        }
        if (savedAssignment)
            await lab.Invoke("SaveDesiredRouteAsync", target.Value, "TEST-OUTPUT", lab.Settings.Presets[0].Id,
                AssignmentMode.Manual, true, false, "test-admin", CancellationToken.None);
        using var cancel = new CancellationTokenSource();
        Task? discovery = null;
        try
        {
            // Exercise the hosted loop order, its one-second cadence and the
            // default two-second discovery interval, not private admission alone.
            for (var second = 0; second <= 12; second++)
            {
                discovery = lab.Invoke("DiscoverAndProbeAsync", cancel.Token, false);
                Assert.True(await Task.WhenAny(discovery, Task.Delay(1500)) == discovery,
                    "Discovery must not await the five-second media window or block other sources/reconciliation.");
                await discovery;
                Assert.True(lab.Get<ConcurrentDictionary<string, AutomaticMediaDetection>>("_mediaDetectors").Values.Count(d =>
                    ((SemaphoreSlim)typeof(AutomaticMediaDetection).GetField("_gate", BindingFlags.Instance | BindingFlags.NonPublic)!
                        .GetValue(d)!).CurrentCount == 0) <= 8,
                    "Nonblocking discovery must retain the eight-source concurrent detection bound.");
                await lab.Invoke("ReconcileRoutesAsync", cancel.Token);
                if (lab.Supervisor.IsRunning(target, RouteProcessPurpose.Live)) break;
                foreach (var detector in lab.Get<ConcurrentDictionary<string, AutomaticMediaDetection>>("_mediaDetectors").Values)
                    if (typeof(AutomaticMediaDetection).GetField("_pending", BindingFlags.Instance | BindingFlags.NonPublic)!
                        .GetValue(detector) is Task pending) await pending.WaitAsync(TimeSpan.FromSeconds(5));
                await Task.Delay(10);
                lab.Time.Advance(TimeSpan.FromSeconds(1));
                await Task.Delay(10);
            }
            Assert.True(lab.Supervisor.IsRunning(target, RouteProcessPurpose.Live),
                "Healthy initial admission must consume discovery's fresh proof, not starve behind repeated detection.");
            Assert.Equal(sourceCount, lab.Sources.Count);
            Assert.Single(lab.Supervisor.Snapshot(), p => p.Purpose == RouteProcessPurpose.Live && p.Running);
            Assert.Single(lab.Get<PortReservationManager>("_reservations").Snapshot());
        }
        finally
        {
            cancel.Cancel();
            if (discovery is not null) try { await discovery; } catch (OperationCanceledException) { }
        }
    }

    private sealed class AbsentManagement : HttpMessageHandler, IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(this, disposeHandler: false);
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.ServiceUnavailable));
    }
}
