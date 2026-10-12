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
