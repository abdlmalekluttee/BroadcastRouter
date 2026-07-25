using System.Text.Json;
using BroadcastRouter.Application;
using BroadcastRouter.Domain;
using BroadcastRouter.Infrastructure;

static string Required(IReadOnlyDictionary<string, string> options, string name) =>
    options.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value)
        ? value
        : throw new ArgumentException($"Missing required option {name}.");

var options = args.Chunk(2)
    .Where(pair => pair.Length == 2)
    .ToDictionary(pair => pair[0], pair => pair[1], StringComparer.OrdinalIgnoreCase);

var databasePath = Required(options, "--database");
options.TryGetValue("--ffprobe", out var ffprobePath);
var previewSeconds = options.TryGetValue("--preview-seconds", out var previewSecondsText)
    && int.TryParse(previewSecondsText, out var parsedPreviewSeconds)
        ? Math.Clamp(parsedPreviewSeconds, 1, 30)
        : 0;

var store = new SqliteDataStore(databasePath);
await store.InitializeAsync();
var settings = await store.LoadSettingsAsync();
var profile = settings.WowzaServers.FirstOrDefault(server =>
                  server.ServerId.Equals("WOWZA-PROD", StringComparison.OrdinalIgnoreCase))
              ?? settings.WowzaServers.FirstOrDefault(server => server.Enabled)
              ?? throw new InvalidOperationException("No enabled Wowza profile is configured.");

var password = WindowsDpapi.Unprotect(profile.ProtectedPassword);
try
{
    var result = new Dictionary<string, object?>
    {
        ["serverId"] = profile.ServerId,
        ["managementUrl"] = profile.ManagementUrl,
        ["application"] = profile.Applications,
        ["instance"] = profile.ApplicationInstances,
        ["simulationMode"] = settings.SimulationMode
    };

    var connection = await WowzaConnectionTester.TestAsync(profile, password, CancellationToken.None);
    result["managementReachable"] = connection.Reachable;
    result["managementAuthenticated"] = connection.Authenticated;
    result["managementStatus"] = connection.HttpStatus;
    result["managementSummary"] = LogRedactor.Redact(connection.Summary);
    result["wowzaVersion"] = connection.DetectedVersion;

    var applications = profile.Applications
        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    var instances = profile.ApplicationInstances
        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    var configuration = new WowzaServerConfiguration(
        profile.FriendlyName,
        profile.ServerId,
        new Uri(profile.ManagementUrl),
        "wowza:verify",
        profile.ValidateTlsCertificate,
        profile.RtspHost,
        profile.RtspPort,
        applications,
        instances,
        profile.RtspUrlTemplate,
        TimeSpan.FromSeconds(profile.PollingIntervalSeconds),
        TimeSpan.FromSeconds(profile.ConnectionTimeoutSeconds),
        profile.Enabled,
        profile.Priority);

    try
    {
        using var applicationsClient = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(profile.ConnectionTimeoutSeconds)
        };
        using var document = await new WowzaRestClient(applicationsClient)
            .GetApplicationsAsync(configuration, profile.Username, password, CancellationToken.None);
        result["applicationsEndpointSucceeded"] = true;
        result["applicationsResponseKind"] = document.RootElement.ValueKind.ToString();
    }
    catch (Exception exception)
    {
        result["applicationsEndpointSucceeded"] = false;
        result["applicationsError"] = LogRedactor.Redact(exception.Message);
    }

    IReadOnlyList<DiscoveredSource> sources = [];
    try
    {
        using var discoveryClient = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(profile.ConnectionTimeoutSeconds)
        };
        var discovery = new WowzaDiscoveryProvider(
            discoveryClient,
            configuration,
            new StaticCredentialResolver(new CredentialValue(profile.Username, password)));
        sources = await discovery.DiscoverAsync(CancellationToken.None);
        result["discoverySucceeded"] = true;
        result["activeStreamCount"] = sources.Count;
        result["activeStreams"] = sources.Select(source => source.Identity.StreamName).ToArray();
    }
    catch (Exception exception)
    {
        result["discoverySucceeded"] = false;
        result["activeStreamCount"] = 0;
        result["discoveryError"] = LogRedactor.Redact(exception.Message);
    }

    var probes = new List<object>();
    DiscoveredSource? previewSource = null;
    if (sources.Count > 0 && !string.IsNullOrWhiteSpace(ffprobePath) && File.Exists(ffprobePath))
    {
        var probe = new FfprobeStreamProbe(ffprobePath, TimeSpan.FromSeconds(12));
        foreach (var source in sources)
        {
            var media = await probe.ProbeAsync(source.RtspUri, CancellationToken.None);
            if (previewSource is null && media.Opened && media.Media is not null)
                previewSource = source with { Media = media.Media };
            probes.Add(new
            {
                stream = source.Identity.StreamName,
                opened = media.Opened,
                framesReceived = media.FramesReceived,
                videoCodec = media.Media?.VideoCodec,
                width = media.Media?.Width,
                height = media.Media?.Height,
                framesPerSecond = media.Media?.FramesPerSecond,
                audioCodec = media.Media?.AudioCodec,
                failureCategory = media.FailureCategory,
                detail = LogRedactor.Redact(media.Detail ?? string.Empty)
            });
        }
    }
    result["rtspProbes"] = probes;
    result["rtspProbeSummary"] = sources.Count == 0
        ? "No active publishers were discovered."
        : probes.Count == 0
            ? "FFprobe was unavailable."
            : $"Probed {probes.Count} active stream(s).";

    if (previewSeconds > 0)
    {
        if (previewSource is null)
            throw new InvalidOperationException("A successfully probed source is required for the live preview check.");

        await using var preview = new FfplayPreviewSupervisor();
        await preview.StartAsync(previewSource, settings.MediaTools);
        var running = preview.Snapshot;
        await Task.Delay(TimeSpan.FromSeconds(previewSeconds));
        await preview.StopAsync();
        result["previewCheck"] = new
        {
            started = running.State == PreviewState.Running,
            audioMeterEnabled = running.AudioMeterEnabled,
            producerProcessStarted = running.ProducerProcessId.HasValue,
            playerProcessStarted = running.PlayerProcessId.HasValue,
            stoppedCleanly = preview.Snapshot.State == PreviewState.Stopped
        };
    }

    Console.WriteLine(JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
}
finally
{
    password = string.Empty;
}
