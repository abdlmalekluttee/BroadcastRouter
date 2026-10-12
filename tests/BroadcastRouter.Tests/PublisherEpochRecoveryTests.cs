using System.Net;
using System.Text.Json;
using BroadcastRouter.Application;
using BroadcastRouter.Domain;
using BroadcastRouter.Infrastructure;
using Xunit;

namespace BroadcastRouter.Tests;

public sealed class PublisherEpochRecoveryTests
{
    [Fact]
    public async Task ContinuousConnectedHourNeverOverwritesRetryDeadline()
    {
        using var http = new PublisherHttp();
        await using var lab = await OfflineFallbackTests.RecoveryLab.Create(http);
        Configure(lab);
        var deadline = lab.Clock.Now.AddHours(2);
        lab.Routes[lab.Source.Value] = lab.Route with { RetryAt = deadline };
        for (var second = 0; second < 3600; second++)
        {
            await lab.Invoke("SuperviseWowzaPublisherPresenceAsync", lab.Supervisor, CancellationToken.None);
            Assert.Equal(deadline, lab.Route.RetryAt);
            Assert.Equal(3, lab.Route.RestartCount);
            lab.Time.Advance(TimeSpan.FromSeconds(1));
        }
        Assert.Single(lab.Events, e => e.State == RouteProcessLifecycleState.Started);
        Assert.Empty(await lab.Store.ReadLogsAsync("PublisherPresence"));
    }

    [Fact]
    public async Task Connected404HourUsesBoundedRealFakeProbesAndOneReconnectAcceleration()
    {
        using var http = new PublisherHttp();
        await using var lab = await OfflineFallbackTests.RecoveryLab.Create(http);
        Configure(lab);
        await lab.InstallFakeMediaTools();
        await File.WriteAllTextAsync(Path.Combine(lab.Root, "probe.json"), JsonSerializer.Serialize(new
        {
            events = new[] { new { atMilliseconds = 0, channel = "stderr", text = "RTSP input returned 404 Not Found" } },
            exitAfterMilliseconds = 20, exitCode = -875574520
        }));
        async Task ProbeDue()
        {
            await lab.RestartDue();
            var readiness = lab.Get<System.Collections.Concurrent.ConcurrentDictionary<string, RecoveryReadinessGate>>("_recoveryReadiness")[lab.Source.Value];
            var pending = (Task?)typeof(RecoveryReadinessGate).GetField("_pending", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(readiness);
            if (pending is not null) await pending.WaitAsync(TimeSpan.FromSeconds(10));
            await lab.RestartDue();
        }
        for (var second = 0; second < 3600; second++)
        {
            await lab.Invoke("SuperviseWowzaPublisherPresenceAsync", lab.Supervisor, CancellationToken.None);
            await ProbeDue();
            lab.Time.Advance(TimeSpan.FromSeconds(1));
        }
        var marker = Path.Combine(lab.Root, "probe-starts.log");
        Assert.Equal(124, File.ReadAllLines(marker).Length);
        http.Connected = false;
        await lab.Invoke("SuperviseWowzaPublisherPresenceAsync", lab.Supervisor, CancellationToken.None);
        await lab.Invoke("SuperviseWowzaPublisherPresenceAsync", lab.Supervisor, CancellationToken.None);
        http.Connected = true;
        await lab.Invoke("SuperviseWowzaPublisherPresenceAsync", lab.Supervisor, CancellationToken.None);
        await ProbeDue();
        Assert.Equal(125, File.ReadAllLines(marker).Length);
        for (var second = 0; second < 29; second++)
        {
            lab.Time.Advance(TimeSpan.FromSeconds(1));
            await lab.Invoke("SuperviseWowzaPublisherPresenceAsync", lab.Supervisor, CancellationToken.None);
            await ProbeDue();
        }
        Assert.Equal(125, File.ReadAllLines(marker).Length);
        Assert.Single(lab.Events, e => e.State == RouteProcessLifecycleState.Started);
        Assert.DoesNotContain(lab.Events, e => e.State == RouteProcessLifecycleState.StopRequested);
        Assert.Equal(3, lab.Route.RestartCount);
    }

    [Fact]
    public async Task ConfirmedReconnectAcceleratesOnceEvenIfRouteGateWasBusy()
    {
        using var http = new PublisherHttp { Connected = false };
        await using var lab = await OfflineFallbackTests.RecoveryLab.Create(http);
        Configure(lab);
        var deadline = lab.Clock.Now.AddMinutes(1);
        lab.Routes[lab.Source.Value] = lab.Route with { RetryAt = deadline };
        await lab.Invoke("SuperviseWowzaPublisherPresenceAsync", lab.Supervisor, CancellationToken.None);
        await lab.Invoke("SuperviseWowzaPublisherPresenceAsync", lab.Supervisor, CancellationToken.None);
        var gate = lab.Get<System.Collections.Concurrent.ConcurrentDictionary<string, SemaphoreSlim>>("_routeGates")[lab.Source.Value];
        await gate.WaitAsync();
        try
        {
            http.Connected = true;
            await lab.Invoke("SuperviseWowzaPublisherPresenceAsync", lab.Supervisor, CancellationToken.None);
            Assert.Equal(deadline, lab.Route.RetryAt);
        }
        finally { gate.Release(); }
        await lab.Invoke("SuperviseWowzaPublisherPresenceAsync", lab.Supervisor, CancellationToken.None);
        Assert.Equal(lab.Clock.Now, lab.Route.RetryAt);
        lab.Routes[lab.Source.Value] = lab.Route with { RetryAt = deadline };
        lab.Time.Advance(TimeSpan.FromSeconds(2));
        await lab.Invoke("SuperviseWowzaPublisherPresenceAsync", lab.Supervisor, CancellationToken.None);
        Assert.Equal(deadline, lab.Route.RetryAt);
        Assert.Equal(3, lab.Route.RestartCount);
    }

    [Fact]
    public async Task ReconnectedWaitingRouteDoesNotTreatCachedMetadataAsMediaProof()
    {
        using var http = new PublisherHttp { Connected = false };
        await using var lab = await OfflineFallbackTests.RecoveryLab.Create(http);
        Configure(lab);
        await lab.InstallFakeMediaTools();
        lab.Routes[lab.Source.Value] = lab.Route with { State = RouteState.WaitingForStream };
        lab.Sources[lab.Source.Value] = lab.Sources[lab.Source.Value] with { Media = RecoveryReadinessTests.Av().Media };
        await lab.Invoke("SuperviseWowzaPublisherPresenceAsync", lab.Supervisor, CancellationToken.None);
        await lab.Invoke("SuperviseWowzaPublisherPresenceAsync", lab.Supervisor, CancellationToken.None);
        http.Connected = true;
        await lab.Invoke("SuperviseWowzaPublisherPresenceAsync", lab.Supervisor, CancellationToken.None);
        Assert.True(lab.Supervisor.IsRunning(lab.Source, RouteProcessPurpose.Fallback));
        Assert.DoesNotContain(lab.Events, e => e.State == RouteProcessLifecycleState.StopRequested);
    }

    [Fact]
    public async Task FirstLiveProgressDoesNotResetRetryDebt()
    {
        await using var lab = await OfflineFallbackTests.RecoveryLab.Create();
        await lab.InstallFakeMediaTools();
        for (var i = 0; i < 100 && !lab.Supervisor.IsRunning(lab.Source, RouteProcessPurpose.Live); i++)
        { await lab.RestartDue(); await Task.Delay(20); }
        await FaultHarnessTests.Eventually(() => lab.Owner.Progress?.Frame > 0);
        lab.Set("_nextDeckLinkReferenceStatusCheck", DateTimeOffset.MaxValue);
        await lab.Invoke("MonitorProcessesAsync", CancellationToken.None);
        Assert.Equal(RouteState.Running, lab.Route.State);
        Assert.Equal(3, lab.Route.RestartCount);
    }

    private static void Configure(OfflineFallbackTests.RecoveryLab lab) => lab.Settings.WowzaServers.Add(new()
    {
        ServerId = lab.Source.ServerId, Applications = lab.Source.Application,
        ApplicationInstances = lab.Source.ApplicationInstance, ManagementUrl = "http://example.invalid/"
    });

    private sealed class PublisherHttp : HttpMessageHandler, IHttpClientFactory
    {
        public bool Connected { get; set; } = true;
        public HttpClient CreateClient(string name) => new(this, disposeHandler: false);
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(new { incomingStreams = new[] { new { name = "source-A", isConnected = Connected } } }))
            });
    }
}
