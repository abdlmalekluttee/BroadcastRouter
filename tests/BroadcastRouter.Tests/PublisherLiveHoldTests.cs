using System.Diagnostics;
using System.Reflection;
using System.Security.Claims;
using System.Text.Json;
using BroadcastRouter.Application;
using BroadcastRouter.Domain;
using BroadcastRouter.Infrastructure;
using BroadcastRouter.Web.Services;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.Logging.Abstractions;

internal static class PublisherLiveHoldTests
{
    private static readonly SourceIdentity Source = new("QA", "live", "_definst_", "hold-test");
    private static RuntimeRoute Route() => new(Source.Value, "Test feed", null, null, "1080i50",
        RouteState.Running, AssignmentMode.Manual, true, 0, 0, 1, 25, 1, 0, 0,
        DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, null, null);
    private static void Check(bool value) { if (!value) throw new Exception("Recovery hold assertion failed."); }

    public static void Policy()
    {
        var policy = new PublisherLiveHoldPolicy();
        var route = Route() with { HoldOutputWhilePublisherLive = true };
        Check(!policy.ShouldHold(route, true, true));
        policy.SeedConnected(route.SourceId);
        Check(policy.ShouldHold(route, true, true));
        Check(!policy.ShouldHold(route with { HoldOutputWhilePublisherLive = false }, true, true));
        Check(!policy.ShouldHold(route, false, true));
        Check(!policy.ShouldHold(route, true, false));
        Check(!policy.ShouldHold(route with { SourceId = "QA/live/_definst_/other" }, true, true));
        // No observation during a management outage: retain the last authoritative state.
        Check(policy.ShouldHold(route, true, true));
        policy.Observe(route.SourceId, false);
        policy.SeedConnected(route.SourceId); // stale normal discovery must not undo confirmed loss
        Check(!policy.ShouldHold(route, true, true));
        policy.Observe(route.SourceId, true);
        Check(policy.ShouldHold(route, true, true));
    }

    public static void Persistence()
    {
        using var lab = new Lab();
        var old = Route();
        var json = JsonSerializer.Serialize(old).Replace(",\"HoldOutputWhilePublisherLive\":false", "", StringComparison.Ordinal);
        Check(!JsonSerializer.Deserialize<RuntimeRoute>(json)!.HoldOutputWhilePublisherLive);
        var held = old with { HoldOutputWhilePublisherLive = true, DesiredPortId = "QA-PORT" };
        Check(RouteTelemetryPersistencePolicy.RequiresPersistence(old, held));
        Check(DesiredRoutePolicy.ResetTransientStateForStartup(held, DateTimeOffset.UtcNow).HoldOutputWhilePublisherLive);
        lab.Store.SaveRouteAsync(held, held.State, audit: Audit(held, "qa-admin")).GetAwaiter().GetResult();
        Check(new SqliteDataStore(lab.Store.DatabasePath).LoadRoutesAsync().Result.Single().HoldOutputWhilePublisherLive);
        Check(lab.Store.ReadConfigurationAuditAsync().Result.Single().Actor == "qa-admin");
        // A NOT NULL audit failure must roll back the route write too.
        try
        {
            lab.Store.SaveRouteAsync(held with { HoldOutputWhilePublisherLive = false }, held.State,
                audit: Audit(held, "qa-admin") with { EventType = null! }).GetAwaiter().GetResult();
            throw new Exception("Expected atomic audit failure");
        }
        catch (InvalidOperationException) { }
        Check(lab.Store.LoadRoutesAsync().Result.Single().HoldOutputWhilePublisherLive);
    }

    public static void Commands()
    {
        using var lab = new Lab();
        var denied = new AuthorizedRouterCommands(lab.Coordinator, new Auth("Operator"));
        try { denied.ExecuteAsync("enable-live-hold", Source.Value).GetAwaiter().GetResult(); throw new Exception("Expected denial"); }
        catch (UnauthorizedAccessException) { }
        Check(!lab.Current.HoldOutputWhilePublisherLive);
        lab.Command("enable-live-hold");
        Check(lab.Current.HoldOutputWhilePublisherLive);
        Check(lab.Coordinator.Snapshot.Routes.Single().HoldOutputWhilePublisherLive);
        var stale = Route() with { State = RouteState.Reconnecting };
        Invoke(lab.Coordinator, "ReplaceRouteAsync", stale, RouteState.Running, CancellationToken.None, true).GetAwaiter().GetResult();
        Check(lab.Current.HoldOutputWhilePublisherLive);
        Check(lab.Store.LoadRoutesAsync().Result.Single().HoldOutputWhilePublisherLive);
        lab.Command("disable-live-hold");
        Check(!lab.Current.HoldOutputWhilePublisherLive);
        Check(lab.Store.ReadConfigurationAuditAsync().Result.Count == 2);
        Get<OperatorSettings>(lab.Coordinator, "_settings").WowzaServers.Clear();
        try { lab.Command("enable-live-hold"); throw new Exception("Expected Wowza validation"); }
        catch (InvalidOperationException) { }
    }

    public static void Watchdogs()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var lab = new Lab();
        lab.StartFaultedProcess();
        var pid = lab.Process.ProcessId;
        lab.Command("enable-live-hold");
        Get<PublisherLiveHoldPolicy>(lab.Coordinator, "_publisherLiveHold").Observe(Source.Value, true);
        Invoke(lab.Coordinator, "MonitorProcessesAsync", CancellationToken.None).GetAwaiter().GetResult();
        lab.RunFastInput();
        Check(lab.Process.Running && lab.Process.ProcessId == pid);
        Check(lab.Store.ReadLogsAsync("RecoveryHold").Result.Count == 1);
        lab.Command("disable-live-hold");
        lab.RunFastInput();
        Check(!lab.Process.Running);
        Check(lab.Current.State == RouteState.Reconnecting);
    }

    public static void DisconnectAndExit()
    {
        if (!OperatingSystem.IsWindows()) return;
        using (var lab = new Lab())
        {
            lab.StartFaultedProcess();
            lab.Command("enable-live-hold");
            lab.Http.Connected = true;
            lab.PollPublisher();
            Check(lab.Process.Running);
            lab.Http.Fail = true;
            lab.PollPublisher();
            lab.RunFastInput();
            Check(lab.Process.Running);
            lab.Http.Fail = false;
            lab.Http.Connected = false;
            lab.PollPublisher();
            Check(lab.Process.Running); // first negative is deliberately debounced
            lab.PollPublisher();
            Check(!lab.Process.Running);
            Check(lab.Current.FailureCategory == "PublisherDisconnected");
        }
        using (var lab = new Lab())
        {
            lab.StartFaultedProcess();
            lab.Command("enable-live-hold");
            Get<PublisherLiveHoldPolicy>(lab.Coordinator, "_publisherLiveHold").Observe(Source.Value, true);
            lab.Supervisor.StopAsync(Source, CancellationToken.None).GetAwaiter().GetResult();
            Invoke(lab.Coordinator, "MonitorProcessesAsync", CancellationToken.None).GetAwaiter().GetResult();
            Check(lab.Current.State == RouteState.Reconnecting && lab.Current.RetryAt is not null);
            Check(lab.Current.HoldOutputWhilePublisherLive);
        }
    }

    private static ConfigurationAuditEntry Audit(RuntimeRoute route, string actor) => new(0, DateTimeOffset.UtcNow,
        "PublisherLiveRecoveryHold", route.SourceId, "", "", "False", "True", actor, "QA", "Applied", route.SourceId);
    private static T Get<T>(object target, string field) => (T)target.GetType().GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target)!;
    private static Task Invoke(object target, string method, params object?[] args) =>
        (Task)target.GetType().GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(target, args)!;

    private sealed class Lab : IDisposable
    {
        private readonly string directory = Path.Combine(Path.GetTempPath(), "BroadcastRouter-hold-" + Guid.NewGuid().ToString("N"));
        public SqliteDataStore Store { get; }
        public RouterCoordinator Coordinator { get; }
        public TestHttp Http { get; } = new();
        public FfmpegProcessSupervisor Supervisor { get; } = new(new FfmpegRouteOptions("unused.exe"), TimeSpan.FromMilliseconds(200));
        public RuntimeRoute Current => Get<Dictionary<string, RuntimeRoute>>(Coordinator, "_routes")[Source.Value];
        public RouteProcessSnapshot Process => Supervisor.Snapshot().Single(value => value.Source == Source);
        public Lab()
        {
            Store = new(Path.Combine(directory, "qa.db"));
            Store.InitializeAsync().GetAwaiter().GetResult();
            Coordinator = new(Store, Http, null!, NullLogger<RouterCoordinator>.Instance);
            typeof(RouterCoordinator).GetField("_nextDeckLinkReferenceStatusCheck", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(Coordinator, DateTimeOffset.MaxValue);
            Get<OperatorSettings>(Coordinator, "_settings").WowzaServers.Add(new() { ServerId = "QA" });
            Get<Dictionary<string, RuntimeRoute>>(Coordinator, "_routes")[Source.Value] = Route();
            Get<Dictionary<string, DiscoveredSource>>(Coordinator, "_sources")[Source.Value] = new(Source, "Test feed",
                new Uri("rtsp://127.0.0.1:1935/live/hold-test"), SourceState.Ready, 0);
            typeof(RouterCoordinator).GetField("_supervisor", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(Coordinator, Supervisor);
        }
        public void Command(string command) => new AuthorizedRouterCommands(Coordinator, new Auth("Administrator"))
            .ExecuteAsync(command, Source.Value).GetAwaiter().GetResult();
        public void StartFaultedProcess()
        {
            var start = new ProcessStartInfo("powershell.exe")
            {
                UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true
            };
            start.ArgumentList.Add("-NoProfile");
            start.ArgumentList.Add("-Command");
            start.ArgumentList.Add("[Console]::Error.WriteLine('[in#0/rtsp] CSeq 96 expected, 0 received.'); [Console]::Out.WriteLine('frame=1'); [Console]::Out.WriteLine('progress=continue'); [Console]::In.ReadLine() | Out-Null");
            Supervisor.StartOwnedProcessForTestingAsync(Source, start).GetAwaiter().GetResult();
            Check(SpinWait.SpinUntil(() => Process.InputFailure is not null, TimeSpan.FromSeconds(10)));
        }
        public void PollPublisher() => Invoke(Coordinator, "SuperviseWowzaPublisherPresenceAsync", Supervisor, CancellationToken.None).GetAwaiter().GetResult();
        public void RunFastInput()
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(600));
            try { Invoke(Coordinator, "RunFastInputSupervisionAsync", timeout.Token).GetAwaiter().GetResult(); }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested) { }
        }
        public void Dispose()
        {
            Supervisor.DisposeAsync().AsTask().GetAwaiter().GetResult();
            Coordinator.Dispose();
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Directory.Delete(directory, true);
        }
    }
    private sealed class Auth(string role) : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync() => Task.FromResult(new AuthenticationState(
            new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, "qa-admin"), new Claim(ClaimTypes.Role, role)], "test"))));
    }
    private sealed class TestHttp : HttpMessageHandler, IHttpClientFactory
    {
        public bool Connected { get; set; }
        public bool Fail { get; set; }
        public HttpClient CreateClient(string name) => new(this, disposeHandler: false);
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(Fail ? System.Net.HttpStatusCode.ServiceUnavailable : System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(new { incomingStreams = new[] { new { name = "hold-test", isConnected = Connected } } }))
            });
    }
}
