using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using BroadcastRouter.Application;
using BroadcastRouter.Domain;
using BroadcastRouter.Infrastructure;
using Microsoft.AspNetCore.SignalR;

namespace BroadcastRouter.Web.Services;

public sealed class RouterCoordinator(
    SqliteDataStore store,
    IHttpClientFactory httpClientFactory,
    IHubContext<StatusHub> hub,
    ILogger<RouterCoordinator> logger) : BackgroundService
{
    private readonly object _gate = new();
    private readonly PortReservationManager _reservations = new();
    private readonly PriorityWaitingQueue _waiting = new();
    private readonly Dictionary<string, DiscoveredSource> _sources = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DeckLinkPort> _ports = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, RuntimeRoute> _routes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ServerHealth> _servers = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, string> _simulationFaults = new(StringComparer.Ordinal);
    private readonly DateTimeOffset _startedAt = DateTimeOffset.UtcNow;
    private FfmpegProcessSupervisor? _supervisor;
    private string? _supervisorPath;
    private OperatorSettings _settings = new();
    private MediaToolValidation _validation = MediaToolValidation.NotConfigured;
    private bool _emergencyStopped;
    private bool _forceDiscovery = true;
    private bool _forceToolValidation = true;
    private DateTimeOffset _lastDiscovery = DateTimeOffset.MinValue;
    private DateTimeOffset _lastToolValidation = DateTimeOffset.MinValue;
    private TimeSpan _lastCpu;
    private DateTimeOffset _lastCpuAt = DateTimeOffset.UtcNow;

    public event Action? Changed;

    public RouterSnapshot Snapshot { get; private set; } = new([], [], [], [], [], MediaToolValidation.NotConfigured,
        DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, 0, 0, true, false, "Starting");

    public OperatorSettings GetSettings()
    {
        lock (_gate) return Clone(_settings);
    }

    public object GetSanitizedSettings()
    {
        var settings = GetSettings();
        foreach (var server in settings.WowzaServers)
        {
            server.Username = string.IsNullOrWhiteSpace(server.Username) ? "" : "***";
            server.ProtectedPassword = string.IsNullOrWhiteSpace(server.ProtectedPassword) ? "" : "<DPAPI protected; omitted>";
        }
        return settings;
    }

    public async Task SaveSettingsAsync(OperatorSettings settings, CancellationToken cancellationToken = default)
    {
        foreach (var rule in settings.Rules)
        {
            RoutingRuleEvaluator.ValidatePattern(rule.ServerPattern);
            RoutingRuleEvaluator.ValidatePattern(rule.ApplicationPattern);
            RoutingRuleEvaluator.ValidatePattern(rule.InstancePattern);
            RoutingRuleEvaluator.ValidatePattern(rule.StreamPattern);
        }
        await store.SaveSettingsAsync(settings, cancellationToken);
        lock (_gate)
        {
            _settings = Clone(settings);
            _forceDiscovery = true;
            _forceToolValidation = true;
        }
        await LogAsync("Information", "Settings", "Operator settings saved and queued for reconciliation.", cancellationToken: cancellationToken);
        Publish("Settings updated");
    }

    public async Task SetWowzaPasswordAsync(WowzaServerProfile server, string plaintext, CancellationToken cancellationToken = default)
    {
        server.ProtectedPassword = string.IsNullOrEmpty(plaintext) ? "" : WindowsDpapi.Protect(plaintext);
        await Task.CompletedTask;
    }

    public async Task<WowzaConnectionTestResult> TestWowzaAsync(WowzaServerProfile profile, string? plaintextPassword = null, CancellationToken cancellationToken = default)
    {
        var password = plaintextPassword;
        if (password is null && !string.IsNullOrWhiteSpace(profile.ProtectedPassword))
        {
            try { password = WindowsDpapi.Unprotect(profile.ProtectedPassword); }
            catch { password = ""; }
        }
        var result = await WowzaConnectionTester.TestAsync(profile, password ?? "", cancellationToken);
        await LogAsync(result.Authenticated ? "Information" : "Warning", "WowzaTest", $"{profile.ServerId}: {result.Summary}", cancellationToken: cancellationToken);
        return result;
    }

    public async Task<IReadOnlyList<StructuredLogEntry>> ReadLogsAsync(string? search, int limit = 500, CancellationToken cancellationToken = default) =>
        await store.ReadLogsAsync(search, limit, cancellationToken);

    public async Task CommandAsync(string action, string? sourceId = null, string? portId = null, CancellationToken cancellationToken = default)
    {
        action = action.Trim().ToLowerInvariant();
        if (action == "emergency-stop")
        {
            _emergencyStopped = true;
            foreach (var activeRoute in RoutesCopy()) await StopRouteAsync(activeRoute.SourceId, forceRelease: true, cancellationToken);
            await LogAsync("Critical", "Operator", "Emergency stop activated. All owned FFmpeg processes were stopped.", correlationId: NewCorrelation(), cancellationToken: cancellationToken);
            Publish("Emergency stop active");
            return;
        }
        if (action == "clear-emergency")
        {
            _emergencyStopped = false;
            await LogAsync("Warning", "Operator", "Emergency stop cleared; automatic routing may resume.", cancellationToken: cancellationToken);
            Publish("Emergency stop cleared");
            return;
        }
        if (action == "rescan")
        {
            _forceToolValidation = true;
            _forceDiscovery = true;
            await LogAsync("Information", "Operator", "Hardware and source rescan requested.", cancellationToken: cancellationToken);
            Publish("Rescan requested");
            return;
        }
        if (string.IsNullOrWhiteSpace(sourceId)) throw new ArgumentException("A source ID is required for this action.", nameof(sourceId));

        RuntimeRoute? route;
        lock (_gate) _routes.TryGetValue(sourceId, out route);
        switch (action)
        {
            case "start":
            case "restore":
                _emergencyStopped = false;
                await EnsureRouteAsync(sourceId, portId, manual: true, cancellationToken);
                break;
            case "stop":
                await StopRouteAsync(sourceId, forceRelease: false, cancellationToken);
                break;
            case "restart":
                await StopRouteAsync(sourceId, forceRelease: true, cancellationToken);
                await EnsureRouteAsync(sourceId, portId, manual: true, cancellationToken);
                break;
            case "reassign":
                await StopRouteAsync(sourceId, forceRelease: true, cancellationToken);
                await EnsureRouteAsync(sourceId, portId, manual: true, cancellationToken);
                break;
            case "reprobe":
                lock (_gate)
                {
                    if (_sources.TryGetValue(sourceId, out var source)) _sources[sourceId] = source with { State = SourceState.Probing, Media = null };
                    _forceDiscovery = true;
                }
                break;
            case "lock":
                if (route?.PortId is not null)
                {
                    var identity = SourceIdentityFromValue(route.SourceId);
                    _reservations.TryReserve(route.PortId, identity, true, DateTimeOffset.UtcNow, out _);
                    await ReplaceRouteAsync(route with { Locked = true, UpdatedAt = DateTimeOffset.UtcNow }, route.State, cancellationToken);
                }
                break;
            case "unlock":
                if (route?.PortId is not null)
                {
                    var identity = SourceIdentityFromValue(route.SourceId);
                    _reservations.Release(route.PortId, identity, force: true);
                    _reservations.TryReserve(route.PortId, identity, false, DateTimeOffset.UtcNow, out _);
                    await ReplaceRouteAsync(route with { Locked = false, UpdatedAt = DateTimeOffset.UtcNow }, route.State, cancellationToken);
                }
                break;
            case "standby":
                if (route is not null)
                {
                    if (_supervisor is not null) await _supervisor.StopAsync(SourceIdentityFromValue(sourceId), cancellationToken);
                    var fallback = route with { State = RouteState.Fallback, RetryAt = null, UpdatedAt = DateTimeOffset.UtcNow, FailureCategory = "OperatorStandby", FailureMessage = "Standby selected by operator." };
                    if (!_settings.SimulationMode) await StartFallbackAsync(fallback, cancellationToken);
                    await ReplaceRouteAsync(fallback, route.State, cancellationToken);
                }
                break;
            case "simulate-failure":
                _simulationFaults[sourceId] = "FFmpegCrash";
                break;
            case "simulate-stall":
                _simulationFaults[sourceId] = "VideoStall";
                break;
            case "clear-failure":
                _simulationFaults.TryRemove(sourceId, out _);
                if (route is not null) await ReplaceRouteAsync(route with { State = RouteState.Reconnecting, RetryAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow }, route.State, cancellationToken);
                break;
            default: throw new ArgumentOutOfRangeException(nameof(action), $"Unknown route action '{action}'.");
        }
        await LogAsync("Information", "Operator", $"Action '{action}' requested.", sourceId, NewCorrelation(), cancellationToken);
        Publish($"{action} requested");
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await store.InitializeAsync(stoppingToken);
        _settings = await store.LoadSettingsAsync(stoppingToken);
        foreach (var route in await store.LoadRoutesAsync(stoppingToken)) _routes[route.SourceId] = route;
        await LogAsync("Information", "Host", "BroadcastRouter server started; persisted routes will be reconciled.", cancellationToken: stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RefreshToolsAndPortsAsync(stoppingToken);
                await DiscoverAndProbeAsync(stoppingToken);
                await ReconcileRoutesAsync(stoppingToken);
                await MonitorProcessesAsync(stoppingToken);
                Publish("Running");
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                logger.LogError(ex, "Router reconciliation cycle failed");
                await LogAsync("Error", "Coordinator", $"Reconciliation cycle failed: {ex.Message}", cancellationToken: stoppingToken);
                Publish("Reconciliation degraded");
            }
            await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_supervisor is not null) await _supervisor.DisposeAsync();
        await base.StopAsync(cancellationToken);
    }

    private async Task RefreshToolsAndPortsAsync(CancellationToken cancellationToken)
    {
        var settings = GetSettings();
        if (settings.SimulationMode)
        {
            _validation = new(ToolValidationState.Valid, "simulation-ffmpeg 1.0", "simulation-ffprobe 1.0", true, true, 4,
                ["Simulation mode: no real FFmpeg or DeckLink device is opened."], DateTimeOffset.UtcNow);
            var simulatedPorts = ApplyOverrides(BuildSimulationPorts(), settings.DeckLinkPortOverrides);
            lock (_gate)
            {
                _ports.Clear();
                foreach (var port in simulatedPorts) _ports[port.StableId] = port;
            }
            foreach (var port in simulatedPorts) await store.UpsertPortAsync(port, cancellationToken);
            _forceToolValidation = false;
            return;
        }

        if (!_forceToolValidation && DateTimeOffset.UtcNow - _lastToolValidation < TimeSpan.FromMinutes(5)) return;
        _validation = new(ToolValidationState.Validating, null, null, false, false, 0, ["Validation in progress."], DateTimeOffset.UtcNow);
        Publish("Validating media tools");
        _validation = await MediaToolValidator.ValidateAsync(settings.MediaTools, cancellationToken);
        _lastToolValidation = DateTimeOffset.UtcNow;
        _forceToolValidation = false;
        IReadOnlyList<DeckLinkPort> ports = [];
        if (_validation.DeckLinkCompiled && File.Exists(settings.MediaTools.FfmpegPath))
            ports = ApplyOverrides(await new FfmpegDeckLinkEnumerator(settings.MediaTools.FfmpegPath).EnumerateAsync(cancellationToken), settings.DeckLinkPortOverrides);
        lock (_gate)
        {
            _ports.Clear();
            foreach (var port in ports) _ports[port.StableId] = port;
        }
        foreach (var port in ports) await store.UpsertPortAsync(port, cancellationToken);
        await LogAsync(_validation.CanStartHardwareRoutes ? "Information" : "Error", "MediaTools",
            _validation.CanStartHardwareRoutes ? $"Validation passed; {ports.Count} DeckLink output(s) available." : "Validation failed; hardware routes are blocked.", cancellationToken: cancellationToken);
    }

    private async Task DiscoverAndProbeAsync(CancellationToken cancellationToken)
    {
        var settings = GetSettings();
        var minimumPoll = settings.WowzaServers.Where(x => x.Enabled).Select(x => Math.Clamp(x.PollingIntervalSeconds, 1, 300)).DefaultIfEmpty(2).Min();
        if (!_forceDiscovery && DateTimeOffset.UtcNow - _lastDiscovery < TimeSpan.FromSeconds(minimumPoll)) return;
        _forceDiscovery = false;
        _lastDiscovery = DateTimeOffset.UtcNow;

        var observations = new List<DiscoveredSource>();
        var successfullyPolledServerIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (settings.SimulationMode) observations.AddRange(BuildSimulationSources());
        else
        {
            foreach (var profile in settings.WowzaServers.Where(x => x.Enabled))
            {
                try
                {
                    var password = string.IsNullOrWhiteSpace(profile.ProtectedPassword) ? "" : WindowsDpapi.Unprotect(profile.ProtectedPassword);
                    var server = ToConfiguration(profile);
                    using var client = httpClientFactory.CreateClient();
                    var provider = new WowzaDiscoveryProvider(client, server, new StaticCredentialResolver(new CredentialValue(profile.Username, password)));
                    var discovered = await provider.DiscoverAsync(cancellationToken);
                    observations.AddRange(discovered);
                    successfullyPolledServerIds.Add(profile.ServerId);
                    lock (_gate) _servers[profile.ServerId] = new(profile.ServerId, profile.FriendlyName, true, true, discovered.Count, "Discovery succeeded.", DateTimeOffset.UtcNow);
                }
                catch (Exception ex)
                {
                    lock (_gate) _servers[profile.ServerId] = new(profile.ServerId, profile.FriendlyName, false, false, 0, LogRedactor.Redact(ex.Message), DateTimeOffset.UtcNow);
                    await LogAsync("Warning", "WowzaDiscovery", $"{profile.ServerId} discovery failed: {ex.Message}. Healthy routes were retained.", cancellationToken: cancellationToken);
                }
            }
        }

        foreach (var manual in settings.ManualSources.Where(x => x.Enabled))
        {
            if (!Uri.TryCreate(manual.RtspUrl, UriKind.Absolute, out var uri) || uri.Scheme != "rtsp") continue;
            var identity = new SourceIdentity("MANUAL", "manual", "_definst_", manual.StableId);
            observations.Add(new(identity, manual.FriendlyName, uri, SourceState.PublisherActive, manual.Priority, Tags: new HashSet<string> { "manual" },
                FixedPortId: EmptyToNull(manual.FixedPortId), AssignmentLocked: manual.Locked, LastObservedAt: DateTimeOffset.UtcNow));
        }

        var enabledServerIds = settings.WowzaServers.Where(profile => profile.Enabled).Select(profile => profile.ServerId).ToArray();
        var staleSources = SourceObservationReconciler.FindStaleSources(
            SourcesCopy(), observations, enabledServerIds, successfullyPolledServerIds, settings.SimulationMode);
        if (staleSources.Count > 0)
        {
            lock (_gate)
            {
                foreach (var stale in staleSources)
                {
                    _sources.Remove(stale.Identity.Value);
                    _waiting.Remove(stale.Identity);
                }
            }
            foreach (var stale in staleSources) await store.DeleteSourceAsync(stale.Identity.Value, cancellationToken);
            await LogAsync("Information", "Discovery", $"Removed {staleSources.Count} stale source observation(s).", cancellationToken: cancellationToken);
        }

        lock (_gate)
        {
            var activeHealthIds = enabledServerIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (settings.SimulationMode) activeHealthIds.Add("SIM-WOWZA");
            foreach (var staleServerId in _servers.Keys.Where(id => !activeHealthIds.Contains(id)).ToArray())
                _servers.Remove(staleServerId);
        }

        foreach (var source in observations)
        {
            var probed = source;
            if (source.Media is null)
            {
                var probe = settings.SimulationMode
                    ? await new SimulationStreamProbe().ProbeAsync(source.RtspUri, cancellationToken)
                    : await new FfprobeStreamProbe(settings.MediaTools.FfprobePath, TimeSpan.FromSeconds(8)).ProbeAsync(source.RtspUri, cancellationToken);
                probed = source with
                {
                    State = probe.FramesReceived ? SourceState.Ready : probe.Opened ? SourceState.UnsupportedMedia : SourceState.RtspUnavailable,
                    Media = probe.Media
                };
                if (!probe.FramesReceived)
                    await LogAsync("Warning", "Probe", $"Probe failed: {probe.FailureCategory}: {probe.Detail}", source.Identity.Value, cancellationToken: cancellationToken);
            }
            lock (_gate) _sources[probed.Identity.Value] = probed;
            await store.UpsertSourceAsync(probed, cancellationToken);
        }

        if (settings.SimulationMode)
            lock (_gate) _servers["SIM-WOWZA"] = new("SIM-WOWZA", "Simulated Wowza", true, true, observations.Count, "Simulation API healthy.", DateTimeOffset.UtcNow);
    }

    private async Task ReconcileRoutesAsync(CancellationToken cancellationToken)
    {
        if (_emergencyStopped) return;
        var settings = GetSettings();
        await RestoreReservationsAndProcessesAsync(settings, cancellationToken);
        if (!settings.Routing.AutomaticRoutingEnabled) return;
        var sources = SourcesCopy().Where(x => x.State == SourceState.Ready && x.AutomaticRoutingEnabled).OrderByDescending(x => x.Priority).ToArray();
        foreach (var source in sources)
        {
            RuntimeRoute? route;
            lock (_gate) _routes.TryGetValue(source.Identity.Value, out route);
            if (route is null || route.State is RouteState.Released or RouteState.Known or RouteState.Ready or RouteState.WaitingForPort)
                await EnsureRouteAsync(source.Identity.Value, null, manual: false, cancellationToken);
            else if (route.State is RouteState.Reconnecting or RouteState.Fallback && route.RetryAt <= DateTimeOffset.UtcNow)
                await RestartReservedRouteAsync(route, cancellationToken);
        }
    }

    private async Task EnsureRouteAsync(string sourceId, string? requestedPortId, bool manual, CancellationToken cancellationToken)
    {
        DiscoveredSource source;
        DeckLinkPort[] ports;
        OperatorSettings settings;
        lock (_gate)
        {
            if (!_sources.TryGetValue(sourceId, out source!)) throw new InvalidOperationException("Source is not currently known.");
            ports = _ports.Values.ToArray();
            settings = Clone(_settings);
        }
        if (!settings.SimulationMode && !_validation.CanStartHardwareRoutes)
            throw new InvalidOperationException("DeckLink route start refused because Media Tools validation has not passed.");

        var defaultPreset = settings.Presets.First().Id;
        var decision = RoutingRuleEvaluator.Evaluate(source, settings.Rules, defaultPreset);
        var effective = source with
        {
            FixedPortId = requestedPortId ?? decision.FixedPortId,
            AssignmentLocked = decision.Locked,
            Priority = decision.Priority,
            AutomaticRoutingEnabled = true
        };
        var presetProfile = settings.Presets.FirstOrDefault(x => x.Id.Equals(decision.PresetId, StringComparison.OrdinalIgnoreCase)) ?? settings.Presets.First();
        var assignment = new AutomaticAssignmentEngine(_reservations, _waiting).Assign(effective, ports,
            port => Compatible(port, presetProfile, decision.PortGroup));
        if (!assignment.Assigned)
        {
            var waiting = new RuntimeRoute(sourceId, source.FriendlyName, null, null, decision.PresetId, RouteState.WaitingForPort,
                assignment.Mode, decision.Locked, decision.Priority, 0, null, null, null, 0, 0, null, DateTimeOffset.UtcNow,
                "NoOutput", assignment.Reason);
            await ReplaceRouteAsync(waiting, null, cancellationToken);
            return;
        }

        var port = assignment.Port!;
        _waiting.Remove(source.Identity);
        var now = DateTimeOffset.UtcNow;
        var route = new RuntimeRoute(sourceId, source.FriendlyName, port.StableId, port.FriendlyName ?? port.FfmpegName, presetProfile.Id,
            RouteState.Reserved, manual ? AssignmentMode.Manual : assignment.Mode, decision.Locked, decision.Priority, 0,
            null, null, null, 0, 0, null, now, null, null);
        await ReplaceRouteAsync(route, null, cancellationToken);
        await StartRouteAsync(route, source, port, presetProfile, cancellationToken);
    }

    private async Task StartRouteAsync(RuntimeRoute route, DiscoveredSource source, DeckLinkPort port, OutputPresetProfile preset, CancellationToken cancellationToken)
    {
        var starting = route with { State = RouteState.Starting, UpdatedAt = DateTimeOffset.UtcNow, FailureCategory = null, FailureMessage = null, RetryAt = null };
        await ReplaceRouteAsync(starting, route.State, cancellationToken);
        if (_settings.SimulationMode)
        {
            await ReplaceRouteAsync(starting with { State = RouteState.Running, StartedAt = DateTimeOffset.UtcNow, Frame = 0, Fps = preset.FrameRateNumerator / (double)preset.FrameRateDenominator, Speed = 1 }, RouteState.Starting, cancellationToken);
            return;
        }

        EnsureSupervisor(_settings.MediaTools.FfmpegPath);
        var domainRoute = new RouteRecord(source.Identity, port.StableId, preset.Id, RouteState.Starting, starting.AssignmentMode, starting.Locked, starting.RestartCount);
        await _supervisor!.StartAsync(domainRoute, source, port, preset.ToDomain(), cancellationToken);
    }

    private async Task MonitorProcessesAsync(CancellationToken cancellationToken)
    {
        if (_settings.SimulationMode)
        {
            foreach (var route in RoutesCopy().Where(x => x.State == RouteState.Running))
            {
                if (_simulationFaults.TryGetValue(route.SourceId, out var fault))
                {
                    var permanent = fault == "Configuration";
                    var failed = route with
                    {
                        State = permanent ? RouteState.Failed : RouteState.Reconnecting,
                        RestartCount = route.RestartCount + 1,
                        FailureCategory = fault,
                        FailureMessage = $"Simulated {fault} injected.",
                        RetryAt = permanent ? null : DateTimeOffset.UtcNow + RetryDelay(route.RestartCount + 1),
                        UpdatedAt = DateTimeOffset.UtcNow
                    };
                    await ReplaceRouteAsync(failed, route.State, cancellationToken);
                    continue;
                }
                var elapsed = DateTimeOffset.UtcNow - (route.StartedAt ?? DateTimeOffset.UtcNow);
                var fps = route.Fps ?? 25;
                await ReplaceRouteAsync(route with { Frame = (long)(elapsed.TotalSeconds * fps), Speed = 1, UpdatedAt = DateTimeOffset.UtcNow }, route.State, cancellationToken, persistHistory: false);
            }
            return;
        }

        if (_supervisor is null) return;
        foreach (var process in _supervisor.Snapshot())
        {
            RuntimeRoute? route;
            lock (_gate) _routes.TryGetValue(process.Source.Value, out route);
            if (route is null) continue;
            var progress = process.Progress;
            if (process.Running && FfmpegStallDetector.IsStalled(true, progress, DateTimeOffset.UtcNow, TimeSpan.FromSeconds(_settings.Routing.StallTimeoutSeconds)))
            {
                await _supervisor.StopAsync(process.Source, cancellationToken);
                await ScheduleRetryAsync(route, "VideoStalled", "FFmpeg remained alive but stopped producing progress.", cancellationToken);
            }
            else if (process.Running)
            {
                var state = route.State == RouteState.Fallback ? RouteState.Fallback : progress?.Frame > 0 ? RouteState.Running : RouteState.Starting;
                await ReplaceRouteAsync(route with
                {
                    State = state,
                    Frame = progress?.Frame,
                    Fps = progress?.Fps,
                    Speed = progress?.Speed,
                    DroppedFrames = progress?.DroppedFrames ?? 0,
                    DuplicatedFrames = progress?.DuplicatedFrames ?? 0,
                    StartedAt = process.StartedAt,
                    UpdatedAt = DateTimeOffset.UtcNow
                }, route.State, cancellationToken, persistHistory: state != route.State);
            }
            else if (route.State is RouteState.Starting or RouteState.Running)
            {
                var detail = string.Join(" | ", process.RecentErrors.TakeLast(5));
                var category = FfmpegErrorClassifier.Classify(process.ExitCode, detail);
                if (IsPermanent(category))
                    await ReplaceRouteAsync(route with { State = RouteState.Failed, FailureCategory = category.ToString(), FailureMessage = detail, UpdatedAt = DateTimeOffset.UtcNow }, route.State, cancellationToken);
                else await ScheduleRetryAsync(route, category.ToString(), detail, cancellationToken);
            }
        }
    }

    private async Task ScheduleRetryAsync(RuntimeRoute route, string category, string detail, CancellationToken cancellationToken)
    {
        var count = route.RestartCount + 1;
        var retry = route with { State = RouteState.Reconnecting, RestartCount = count, FailureCategory = category,
            FailureMessage = string.IsNullOrWhiteSpace(detail) ? "FFmpeg stopped unexpectedly." : detail,
            RetryAt = DateTimeOffset.UtcNow + RetryDelay(count), UpdatedAt = DateTimeOffset.UtcNow };
        if (!_settings.SimulationMode && route.PortId is not null)
        {
            try
            {
                await StartFallbackAsync(retry, cancellationToken);
                retry = retry with { State = RouteState.Fallback };
            }
            catch (Exception ex)
            {
                await LogAsync("Warning", "Fallback", $"Fallback output could not start: {ex.Message}", route.SourceId, cancellationToken: cancellationToken);
            }
        }
        await ReplaceRouteAsync(retry, route.State, cancellationToken);
    }

    private async Task RestartReservedRouteAsync(RuntimeRoute route, CancellationToken cancellationToken)
    {
        if (route.PortId is null) return;
        DiscoveredSource? source;
        DeckLinkPort? port;
        OutputPresetProfile? preset;
        lock (_gate)
        {
            _sources.TryGetValue(route.SourceId, out source);
            _ports.TryGetValue(route.PortId, out port);
            preset = _settings.Presets.FirstOrDefault(x => x.Id == route.PresetId);
        }
        if (source is null || port is null || preset is null) return;
        if (_simulationFaults.ContainsKey(route.SourceId)) return;
        if (_supervisor is not null && route.State == RouteState.Fallback) await _supervisor.StopAsync(source.Identity, cancellationToken);
        await StartRouteAsync(route, source, port, preset, cancellationToken);
    }

    private async Task StartFallbackAsync(RuntimeRoute route, CancellationToken cancellationToken)
    {
        if (route.PortId is null) return;
        DeckLinkPort? port;
        OutputPresetProfile? preset;
        lock (_gate)
        {
            _ports.TryGetValue(route.PortId, out port);
            preset = _settings.Presets.FirstOrDefault(x => x.Id == route.PresetId);
        }
        if (port is null || preset is null) throw new InvalidOperationException("The reserved port or output preset is unavailable.");
        EnsureSupervisor(_settings.MediaTools.FfmpegPath);
        await _supervisor!.StartFallbackAsync(SourceIdentityFromValue(route.SourceId), port, preset.ToDomain(), preset.StandbyMode, preset.StandbyValue, cancellationToken);
    }

    private async Task StopRouteAsync(string sourceId, bool forceRelease, CancellationToken cancellationToken)
    {
        RuntimeRoute? route;
        lock (_gate) _routes.TryGetValue(sourceId, out route);
        if (route is null) return;
        var identity = SourceIdentityFromValue(sourceId);
        _waiting.Remove(identity);
        if (_supervisor is not null) await _supervisor.StopAsync(identity, cancellationToken);
        if (route.PortId is not null)
        {
            var released = _reservations.Release(route.PortId, identity, forceRelease);
            if (!released && route.Locked && !forceRelease)
                throw new InvalidOperationException("This assignment is locked. Unlock it or use emergency stop before releasing it.");
        }
        await ReplaceRouteAsync(route with { State = RouteState.Released, PortId = null, PortName = null, UpdatedAt = DateTimeOffset.UtcNow }, route.State, cancellationToken);
    }

    private async Task RestoreReservationsAndProcessesAsync(OperatorSettings settings, CancellationToken cancellationToken)
    {
        var candidates = RoutesCopy().Where(x => x.PortId is not null && x.State is not RouteState.Released and not RouteState.Disabled)
            .OrderByDescending(x => x.Locked).ThenByDescending(x => x.Priority).ToArray();
        foreach (var route in candidates)
        {
            DiscoveredSource? source;
            DeckLinkPort? port;
            lock (_gate)
            {
                _sources.TryGetValue(route.SourceId, out source);
                _ports.TryGetValue(route.PortId!, out port);
            }
            if (source is null || port is null) continue;
            if (!_reservations.TryReserve(route.PortId!, source.Identity, route.Locked, DateTimeOffset.UtcNow, out var existing))
            {
                var waiting = route with { PortId = null, PortName = null, State = RouteState.WaitingForPort,
                    FailureCategory = "DuplicateReservation", FailureMessage = $"Persisted output is already reserved by {existing.Source.Value}.", UpdatedAt = DateTimeOffset.UtcNow };
                _waiting.Enqueue(source.Identity, route.Priority, waiting.FailureMessage!);
                await ReplaceRouteAsync(waiting, route.State, cancellationToken);
                continue;
            }
            _waiting.Remove(source.Identity);
            if (settings.SimulationMode) continue;
            var ownsProcess = _supervisor?.Snapshot().Any(x => x.Source.Value == route.SourceId && x.Running) ?? false;
            if (!ownsProcess && route.State is RouteState.Running or RouteState.Starting or RouteState.Reserved)
            {
                var recovering = route with { State = RouteState.Reconnecting, RetryAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
                    FailureCategory = "RestartRecovery", FailureMessage = "Restoring persisted route after host restart." };
                await ReplaceRouteAsync(recovering, route.State, cancellationToken);
                await RestartReservedRouteAsync(recovering, cancellationToken);
            }
        }
    }

    private async Task ReplaceRouteAsync(RuntimeRoute route, RouteState? previousState, CancellationToken cancellationToken, bool persistHistory = true)
    {
        lock (_gate) _routes[route.SourceId] = route;
        await store.SaveRouteAsync(route, persistHistory ? previousState : route.State, cancellationToken);
    }

    private void EnsureSupervisor(string ffmpegPath)
    {
        if (_supervisor is not null && string.Equals(_supervisorPath, ffmpegPath, StringComparison.OrdinalIgnoreCase)) return;
        if (_supervisor is not null) _supervisor.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _supervisor = new FfmpegProcessSupervisor(new FfmpegRouteOptions(ffmpegPath, true, TimeSpan.FromSeconds(10)),
            TimeSpan.FromSeconds(Math.Clamp(_settings.Routing.GracefulStopSeconds, 1, 30)));
        _supervisorPath = ffmpegPath;
    }

    private TimeSpan RetryDelay(int count)
    {
        var delays = _settings.Routing.RetryDelaysSeconds.Length == 0 ? new[] { 1, 2, 5, 10, 20, 30 } : _settings.Routing.RetryDelaysSeconds;
        var seconds = Math.Max(0, delays[Math.Min(count - 1, delays.Length - 1)]);
        var jitter = Random.Shared.NextDouble() * Math.Min(1, seconds * .2);
        return TimeSpan.FromSeconds(seconds + jitter);
    }

    private void Publish(string status)
    {
        RouterSnapshot snapshot;
        lock (_gate)
        {
            var now = DateTimeOffset.UtcNow;
            var process = Process.GetCurrentProcess();
            var cpuTime = process.TotalProcessorTime;
            var interval = Math.Max(.001, (now - _lastCpuAt).TotalSeconds);
            var cpu = Math.Clamp((cpuTime - _lastCpu).TotalSeconds / (interval * Environment.ProcessorCount) * 100, 0, 100);
            _lastCpu = cpuTime;
            _lastCpuAt = now;
            snapshot = new(_sources.Values.OrderBy(x => x.Identity.Value).ToArray(), _ports.Values.OrderBy(x => x.CardIndex).ThenBy(x => x.SubdeviceIndex).ToArray(),
                _routes.Values.OrderByDescending(x => x.Priority).ThenBy(x => x.SourceName).ToArray(),
                _waiting.Snapshot().Select(x => new QueueItemSnapshot(x.Source.Value, x.Priority, x.Reason, x.Sequence)).ToArray(),
                _servers.Values.OrderBy(x => x.FriendlyName).ToArray(), _validation, _startedAt, now, cpu, process.WorkingSet64,
                _settings.SimulationMode, _emergencyStopped, status);
            Snapshot = snapshot;
        }
        try { Changed?.Invoke(); } catch { }
        _ = hub.Clients.All.SendAsync("SnapshotChanged", snapshot.UpdatedAt);
    }

    private async Task LogAsync(string level, string category, string message, string? sourceId = null, string? correlationId = null, CancellationToken cancellationToken = default)
    {
        await store.WriteLogAsync(level, category, message, sourceId, correlationId, cancellationToken);
        logger.Log(level switch { "Error" or "Critical" => LogLevel.Error, "Warning" => LogLevel.Warning, _ => LogLevel.Information }, "{Category}: {Message}", category, LogRedactor.Redact(message));
    }

    private static bool Compatible(DeckLinkPort port, OutputPresetProfile preset, string? requiredGroup)
    {
        if (!port.IsAvailable) return false;
        if (!string.IsNullOrWhiteSpace(requiredGroup) && !string.Equals(port.PortGroup, requiredGroup, StringComparison.OrdinalIgnoreCase)) return false;
        if (port.SupportedModes.Count == 0) return true;
        return port.SupportedModes.Any(mode => mode.Width == preset.Width && mode.Height == preset.Height
            && Math.Abs(mode.FramesPerSecond - preset.FrameRateNumerator / (double)Math.Max(1, preset.FrameRateDenominator)) < .02
            && mode.PixelFormat.Equals(preset.PixelFormat, StringComparison.OrdinalIgnoreCase));
    }
    private static bool IsPermanent(FfmpegFailureCategory category) => category is FfmpegFailureCategory.Authentication or FfmpegFailureCategory.Codec
        or FfmpegFailureCategory.UnsupportedMedia or FfmpegFailureCategory.DeckLinkFormat or FfmpegFailureCategory.Configuration;
    private static string NewCorrelation() => Guid.NewGuid().ToString("N")[..12];
    private static string? EmptyToNull(string value) => string.IsNullOrWhiteSpace(value) ? null : value;

    private DiscoveredSource[] SourcesCopy() { lock (_gate) return _sources.Values.ToArray(); }
    private RuntimeRoute[] RoutesCopy() { lock (_gate) return _routes.Values.ToArray(); }

    private static OperatorSettings Clone(OperatorSettings value) => JsonSerializer.Deserialize<OperatorSettings>(JsonSerializer.Serialize(value))!;

    private static WowzaServerConfiguration ToConfiguration(WowzaServerProfile profile) => new(profile.FriendlyName, profile.ServerId,
        new Uri(profile.ManagementUrl.EndsWith('/') ? profile.ManagementUrl : profile.ManagementUrl + "/"), $"wowza:{profile.ServerId}", profile.ValidateTlsCertificate,
        profile.RtspHost, profile.RtspPort, Split(profile.Applications), Split(profile.ApplicationInstances), profile.RtspUrlTemplate,
        TimeSpan.FromSeconds(Math.Clamp(profile.PollingIntervalSeconds, 1, 300)), TimeSpan.FromSeconds(Math.Clamp(profile.ConnectionTimeoutSeconds, 2, 60)),
        profile.Enabled, profile.Priority);

    private static IReadOnlyList<string> Split(string value) => value.Split([',', ';', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static SourceIdentity SourceIdentityFromValue(string value)
    {
        var parts = value.Split('/');
        if (parts.Length != 4) throw new FormatException("Persisted source identity is invalid.");
        return new(Uri.UnescapeDataString(parts[0]), Uri.UnescapeDataString(parts[1]), Uri.UnescapeDataString(parts[2]), Uri.UnescapeDataString(parts[3]));
    }

    private static IReadOnlyList<DiscoveredSource> BuildSimulationSources()
    {
        var now = DateTimeOffset.UtcNow;
        return
        [
            Sim("news-hd.stream", "News HD", 100, 1920, 1080, 25, true, now),
            Sim("studio-b.stream", "Studio B", 80, 1920, 1080, 50, true, now),
            Sim("sports-720.stream", "Sports 720p", 70, 1280, 720, 50, true, now),
            Sim("clean-feed.stream", "Clean Feed", 60, 1920, 1080, 25, false, now),
            Sim("waiting.stream", "Waiting Queue Demo", 20, 1920, 1080, 25, true, now)
        ];
    }

    private static DiscoveredSource Sim(string stream, string name, int priority, int width, int height, double fps, bool audio, DateTimeOffset now)
    {
        var identity = new SourceIdentity("SIM-WOWZA", "live", "_definst_", stream);
        return new(identity, name, new Uri($"rtsp://127.0.0.1:1935/live/{stream}"), SourceState.Ready, priority,
            new MediaProperties("h264", audio ? "aac" : null, width, height, fps, 3_000_000, audio ? 48_000 : null, audio ? 2 : null, true),
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "simulation", width == 1920 ? "hd" : "720p" }, LastObservedAt: now);
    }

    private static IReadOnlyList<DeckLinkPort> BuildSimulationPorts()
    {
        var modes = new[]
        {
            new VideoMode(1920, 1080, 25, 1, "uyvy422"), new VideoMode(1920, 1080, 50, 1, "uyvy422"),
            new VideoMode(1280, 720, 50, 1, "uyvy422")
        };
        return
        [
            new("SIM-CARD-A-1", "DeckLink Quad 2 (1)", "DeckLink Quad 2", 0, 0, "PCI:01:00.0", modes, true, "PGM Return 1", "Simulation stable ID"),
            new("SIM-CARD-A-2", "DeckLink Quad 2 (2)", "DeckLink Quad 2", 0, 1, "PCI:01:00.0", modes, true, "PGM Return 2", "Simulation stable ID"),
            new("SIM-CARD-B-1", "DeckLink Quad 2 (5)", "DeckLink Quad 2", 1, 0, "PCI:02:00.0", modes, true, "Transmission 1", "Simulation stable ID"),
            new("SIM-CARD-B-2", "DeckLink Quad 2 (6)", "DeckLink Quad 2", 1, 1, "PCI:02:00.0", modes, true, "Transmission 2", "Simulation stable ID")
        ];
    }

    private static IReadOnlyList<DeckLinkPort> ApplyOverrides(IReadOnlyList<DeckLinkPort> ports, IReadOnlyList<DeckLinkPortOverride> overrides)
    {
        var byId = overrides.ToDictionary(x => x.StableId, StringComparer.OrdinalIgnoreCase);
        return ports.Select(port => byId.TryGetValue(port.StableId, out var value)
            ? port with { FriendlyName = string.IsNullOrWhiteSpace(value.FriendlyName) ? port.FriendlyName : value.FriendlyName, PortGroup = value.PortGroup, Reserved = value.Reserved }
            : port).ToArray();
    }
}
