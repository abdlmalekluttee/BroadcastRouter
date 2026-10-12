using System.Net;
using System.Text.Json;
using BroadcastRouter.Domain;
using BroadcastRouter.Infrastructure;
using Xunit;

namespace BroadcastRouter.Tests;

public sealed class StrictWowzaTests
{
    [Theory]
    [InlineData("{}")]
    [InlineData("[]")]
    [InlineData("{\"other\":{\"incomingStreams\":[]}}")]
    [InlineData("{\"incomingStreams\":null}")]
    [InlineData("{\"incomingStreams\":{}}")]
    [InlineData("{\"incomingStreams\":[null]}")]
    [InlineData("{\"incomingStreams\":[{}]}")]
    [InlineData("{\"incomingStreams\":[{\"name\":\"source-A\"}]}")]
    [InlineData("{\"incomingStreams\":[{\"name\":\"source-A\",\"isConnected\":\"false\"}]}")]
    [InlineData("{\"incomingStreams\":[{\"name\":\"source-A\",\"state\":\"new-state\"}]}")]
    [InlineData("{\"incomingStreams\":[{\"name\":\"source-A\",\"isConnected\":false,\"isPublished\":true}]}")]
    [InlineData("{\"incomingStreams\":[{\"name\":\"source-A\",\"isConnected\":false,\"isConnected\":true}]}")]
    [InlineData("{\"incomingStreams\":[],\"incomingStreams\":[]}")]
    [InlineData("{\"incomingStreams\":[{\"name\":\"source-A\",\"isConnected\":true},{\"name\":\"source-A\",\"isConnected\":false}]}")]
    [InlineData("{\"incomingStreams\":[{\"name\":\"source-A\",\"streamName\":\"source-B\",\"isConnected\":true}]}")]
    public void MalformedInventoryIsUnknownNotAnEmptyOrPartialSuccess(string json)
    {
        using var document = JsonDocument.Parse(json);
        var error = Record.Exception(() => WowzaIncomingStreamParser.Parse(document.RootElement));
        Assert.True(error is JsonException, "R07: malformed or ambiguous inventory must reject the entire observation as Unknown");
    }

    [Theory]
    [InlineData("{\"incomingStreams\":[]}", 0)]
    [InlineData("{\"incomingStreams\":[{\"name\":\"source-A\",\"isConnected\":false}]}", 1)]
    [InlineData("{\"incomingStreams\":[{\"streamName\":\"source-A\",\"isPublished\":true}]}", 1)]
    [InlineData("{\"incomingStreams\":[{\"name\":\"source-A\",\"state\":\"published\"}]}", 1)]
    public void ExplicitInventoryRemainsSupported(string json, int count)
    {
        using var document = JsonDocument.Parse(json);
        Assert.Equal(count, WowzaIncomingStreamParser.Parse(document.RootElement).Count);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("truncated")]
    [InlineData("unknown-state")]
    [InlineData("403")]
    [InlineData("500")]
    [InlineData("timeout")]
    [InlineData("partial")]
    [InlineData("empty-scope")]
    public async Task DiscoveryFaultsNeverStopTheExistingLiveOwner(string fault)
    {
        using var http = new InventoryHttp { Mode = fault };
        await using var lab = await LiveLab(http);
        if (fault == "partial") lab.Settings.WowzaServers.Single().Applications = "fixture;second";
        if (fault == "empty-scope") lab.Settings.WowzaServers.Single().Applications = "";
        var owner = lab.Owner;
        var stops = lab.Events.Count(e => e.State == RouteProcessLifecycleState.StopRequested);
        for (var sample = 0; sample < 3; sample++)
        {
            await lab.Invoke("SuperviseWowzaPublisherPresenceAsync", lab.Supervisor, CancellationToken.None);
            await lab.Invoke("DiscoverAndProbeAsync", CancellationToken.None, true);
            await lab.Invoke("ReconcileRoutesAsync", CancellationToken.None);
            Assert.Equal(owner.SessionId, lab.Owner.SessionId);
            Assert.True(lab.Supervisor.IsRunning(lab.Source, RouteProcessPurpose.Live), "R07: management fault stopped a healthy live owner");
            Assert.Equal(stops, lab.Events.Count(e => e.State == RouteProcessLifecycleState.StopRequested));
            Assert.Equal("TEST-OUTPUT", lab.Route.PortId);
            Assert.Equal(SourceState.Ready, lab.Sources[lab.Source.Value].State);
        }
    }

    [Fact]
    public async Task ErrorBodyCannotLeakIntoDiscoveryDiagnostics()
    {
        using var http = new InventoryHttp { Mode = "secret-error" };
        using var client = http.CreateClient("");
        var configuration = new WowzaServerConfiguration("Synthetic", "TEST", new Uri("http://example.invalid/"), "", true,
            "example.invalid", 554, ["fixture"], ["_default_"], "rtsp://{wowza-host}/{application}/{stream-name}",
            TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(1));
        var provider = new WowzaDiscoveryProvider(client, configuration, new StaticCredentialResolver(new("", "")));
        var error = await Assert.ThrowsAsync<HttpRequestException>(() => provider.DiscoverAsync(CancellationToken.None));
        Assert.DoesNotContain("SYNTHETIC_PRIVATE_VALUE", error.Message);
        Assert.Contains("403", error.Message);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OneCompleteAbsenceCannotStopAnAudioLedOwnerDuringManualDiscovery(bool omitted)
    {
        using var http = new InventoryHttp { Mode = omitted ? "empty" : "offline" };
        await using var lab = await LiveLab(http);
        lab.Sources[lab.Source.Value] = lab.Sources[lab.Source.Value] with
        { Media = new(null, "aac", null, null, null, null, 48000, 2, false) };
        var session = lab.Owner.SessionId;
        await lab.Invoke("DiscoverAndProbeAsync", CancellationToken.None, true);
        await lab.Invoke("ReconcileRoutesAsync", CancellationToken.None);
        Assert.Equal(session, lab.Owner.SessionId);
        Assert.True(lab.Supervisor.IsRunning(lab.Source, RouteProcessPurpose.Live),
            "R07: one complete management absence must not tear down healthy audio-led output");
    }

    [Fact]
    public async Task UnknownBreaksPendingAbsenceConfirmation()
    {
        using var http = new InventoryHttp { Mode = "offline" };
        await using var lab = await LiveLab(http);
        var session = lab.Owner.SessionId;
        await lab.Invoke("SuperviseWowzaPublisherPresenceAsync", lab.Supervisor, CancellationToken.None);
        http.Mode = "403";
        await lab.Invoke("SuperviseWowzaPublisherPresenceAsync", lab.Supervisor, CancellationToken.None);
        http.Mode = "offline";
        await lab.Invoke("SuperviseWowzaPublisherPresenceAsync", lab.Supervisor, CancellationToken.None);
        Assert.Equal(session, lab.Owner.SessionId);
        Assert.True(lab.Supervisor.IsRunning(lab.Source, RouteProcessPurpose.Live),
            "R07: failed observation must not bridge two isolated missing samples");
    }

    [Fact]
    public async Task TwoCompleteAbsencesAuthorizeOneOwnedHandoff()
    {
        using var http = new InventoryHttp { Mode = "offline" };
        await using var lab = await LiveLab(http);
        var session = lab.Owner.SessionId;
        var stops = lab.Events.Count(e => e.State == RouteProcessLifecycleState.StopRequested);
        await lab.Invoke("SuperviseWowzaPublisherPresenceAsync", lab.Supervisor, CancellationToken.None);
        Assert.Equal(session, lab.Owner.SessionId);
        await lab.Invoke("SuperviseWowzaPublisherPresenceAsync", lab.Supervisor, CancellationToken.None);
        Assert.Equal(stops + 1, lab.Events.Count(e => e.State == RouteProcessLifecycleState.StopRequested));
        Assert.NotEqual(session, lab.Owner.SessionId);
        Assert.True(lab.Supervisor.IsRunning(lab.Source, RouteProcessPurpose.Fallback));
        await lab.Invoke("SuperviseWowzaPublisherPresenceAsync", lab.Supervisor, CancellationToken.None);
        Assert.Equal(stops + 1, lab.Events.Count(e => e.State == RouteProcessLifecycleState.StopRequested));
    }

    private static async Task<OfflineFallbackTests.RecoveryLab> LiveLab(InventoryHttp http)
    {
        var lab = await OfflineFallbackTests.RecoveryLab.Create(http);
        try
        {
            await lab.InstallFakeMediaTools();
            for (var i = 0; i < 100 && !lab.Supervisor.IsRunning(lab.Source, RouteProcessPurpose.Live); i++)
            { await lab.RestartDue(); await Task.Delay(20); }
            await FaultHarnessTests.Eventually(() => lab.Owner.Progress?.Frame > 0);
            Assert.True(lab.Supervisor.IsRunning(lab.Source, RouteProcessPurpose.Live));
            lab.Routes[lab.Source.Value] = lab.Route with { State = RouteState.Running, RetryAt = null };
            lab.Sources[lab.Source.Value] = lab.Sources[lab.Source.Value] with
            { State = SourceState.Ready, Media = RecoveryReadinessTests.Av().Media };
            lab.Set("_nextDeckLinkReferenceStatusCheck", DateTimeOffset.MaxValue);
            lab.Settings.WowzaServers.Add(new()
            {
                ServerId = lab.Source.ServerId, Applications = "fixture", ApplicationInstances = "_default_",
                ManagementUrl = "http://example.invalid/", ConnectionTimeoutSeconds = 1
            });
            return lab;
        }
        catch { await lab.DisposeAsync(); throw; }
    }

    private sealed class InventoryHttp : HttpMessageHandler, IHttpClientFactory
    {
        public string Mode { get; set; } = "offline";
        public HttpClient CreateClient(string name) => new(this, disposeHandler: false);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var mode = Mode == "partial" ? (request.RequestUri!.AbsolutePath.Contains("/second/") ? "missing" : "empty") : Mode;
            if (mode == "timeout") await Task.Delay(Timeout.Infinite, cancellationToken);
            var code = mode is "403" or "secret-error" ? HttpStatusCode.Forbidden : mode == "500" ? HttpStatusCode.InternalServerError : HttpStatusCode.OK;
            return new(code) { Content = new StringContent(mode switch
            {
                "missing" => "{\"unexpectedEnvelope\":true}",
                "secret-error" => "SYNTHETIC_PRIVATE_VALUE",
                "truncated" => "{\"incomingStreams\":[",
                "unknown-state" => "{\"incomingStreams\":[{\"name\":\"source-A\",\"state\":\"new-state\"}]}",
                "empty" => "{\"incomingStreams\":[]}",
                _ => "{\"incomingStreams\":[{\"name\":\"source-A\",\"isConnected\":false}]}"
            }) };
        }
    }
}
