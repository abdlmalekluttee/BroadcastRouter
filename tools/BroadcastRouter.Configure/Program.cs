using System.Text.Json;
using BroadcastRouter.Application;
using BroadcastRouter.Domain;
using BroadcastRouter.Infrastructure;

static string Required(IReadOnlyDictionary<string, string> options, string name) =>
    options.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value)
        ? value
        : throw new ArgumentException($"Missing required option {name}.");

var options = args.Chunk(2).Where(x => x.Length == 2).ToDictionary(x => x[0], x => x[1], StringComparer.OrdinalIgnoreCase);
var databasePath = Required(options, "--database");
var host = Required(options, "--host");
var username = Required(options, "--username");
var application = Required(options, "--application");
var managementPort = int.Parse(Required(options, "--management-port"));
var mediaPort = int.Parse(Required(options, "--media-port"));
options.TryGetValue("--ffprobe", out var ffprobePath);

string? password;
if (Console.IsInputRedirected)
{
    password = await Console.In.ReadLineAsync();
}
else
{
    var secret = new List<char>();
    while (true)
    {
        var key = Console.ReadKey(intercept: true);
        if (key.Key == ConsoleKey.Enter) break;
        if (key.Key == ConsoleKey.Backspace)
        {
            if (secret.Count > 0) secret.RemoveAt(secret.Count - 1);
            continue;
        }
        if (!char.IsControl(key.KeyChar)) secret.Add(key.KeyChar);
    }
    password = new string(secret.ToArray());
    secret.Clear();
}
if (string.IsNullOrEmpty(password)) throw new InvalidOperationException("A password must be supplied on standard input.");

var store = new SqliteDataStore(databasePath);
await store.InitializeAsync();
var settings = await store.LoadSettingsAsync();
settings.SimulationMode = false;
settings.WowzaServers.RemoveAll(x => x.ServerId.Equals("WOWZA-PROD", StringComparison.OrdinalIgnoreCase));
var profile = new WowzaServerProfile
{
    FriendlyName = "Production Wowza",
    ServerId = "WOWZA-PROD",
    ManagementUrl = $"http://{host}:{managementPort}/",
    Username = username,
    ProtectedPassword = WindowsDpapi.Protect(password),
    ValidateTlsCertificate = true,
    RtspHost = host,
    RtspPort = mediaPort,
    Applications = application,
    ApplicationInstances = "_definst_",
    RtspUrlTemplate = "rtsp://{wowza-host}:{rtsp-port}/{application}/{stream-name}",
    PollingIntervalSeconds = 5,
    ConnectionTimeoutSeconds = 8,
    Enabled = true,
    Priority = 100
};
settings.WowzaServers.Add(profile);
await store.SaveSettingsAsync(settings);

var result = new Dictionary<string, object?>
{
    ["configurationSaved"] = true,
    ["simulationMode"] = settings.SimulationMode,
    ["serverId"] = profile.ServerId,
    ["managementUrl"] = profile.ManagementUrl,
    ["mediaHost"] = profile.RtspHost,
    ["mediaPort"] = profile.RtspPort,
    ["application"] = profile.Applications,
    ["instance"] = profile.ApplicationInstances
};

var connectionTest = await WowzaConnectionTester.TestAsync(profile, password, CancellationToken.None);
result["managementReachable"] = connectionTest.Reachable;
result["managementAuthenticated"] = connectionTest.Authenticated;
result["managementSummary"] = LogRedactor.Redact(connectionTest.Summary);
result["wowzaVersion"] = connectionTest.DetectedVersion;

var configuration = new WowzaServerConfiguration(
    profile.FriendlyName, profile.ServerId, new Uri(profile.ManagementUrl), "wowza:prod", profile.ValidateTlsCertificate,
    profile.RtspHost, profile.RtspPort, [application], ["_definst_"], profile.RtspUrlTemplate,
    TimeSpan.FromSeconds(profile.PollingIntervalSeconds), TimeSpan.FromSeconds(profile.ConnectionTimeoutSeconds), true, profile.Priority);

try
{
    using var applicationsClient = new HttpClient { Timeout = TimeSpan.FromSeconds(profile.ConnectionTimeoutSeconds) };
    using var applications = await new WowzaRestClient(applicationsClient).GetApplicationsAsync(configuration, username, password, CancellationToken.None);
    result["applicationsEndpointReachable"] = true;
    result["applicationsResponseDetected"] = applications.RootElement.ValueKind is JsonValueKind.Object or JsonValueKind.Array;
}
catch (Exception ex)
{
    result["applicationsEndpointReachable"] = false;
    result["applicationsError"] = LogRedactor.Redact(ex.Message);
}

IReadOnlyList<DiscoveredSource> sources = [];
try
{
    using var discoveryClient = new HttpClient { Timeout = TimeSpan.FromSeconds(profile.ConnectionTimeoutSeconds) };
    var discovery = new WowzaDiscoveryProvider(discoveryClient, configuration, new StaticCredentialResolver(new CredentialValue(username, password)));
    sources = await discovery.DiscoverAsync(CancellationToken.None);
    result["discoverySucceeded"] = true;
    result["activeStreamCount"] = sources.Count;
    result["activeStreams"] = sources.Select(x => x.Identity.StreamName).ToArray();
}
catch (Exception ex)
{
    result["discoverySucceeded"] = false;
    result["activeStreamCount"] = 0;
    result["discoveryError"] = LogRedactor.Redact(ex.Message);
}

if (sources.Count > 0 && !string.IsNullOrWhiteSpace(ffprobePath) && File.Exists(ffprobePath))
{
    var probe = new FfprobeStreamProbe(ffprobePath, TimeSpan.FromSeconds(10));
    var probeResults = new List<object>();
    foreach (var source in sources.Take(10))
    {
        var media = await probe.ProbeAsync(source.RtspUri, CancellationToken.None);
        probeResults.Add(new
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
            detail = LogRedactor.Redact(media.Detail ?? "")
        });
    }
    result["rtspProbes"] = probeResults;
}
else
{
    result["rtspProbes"] = Array.Empty<object>();
    result["rtspProbeSummary"] = sources.Count == 0
        ? "No active stream name was discovered, so RTSP frame probing was not attempted."
        : "FFprobe was not available.";
}

Console.WriteLine(JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
