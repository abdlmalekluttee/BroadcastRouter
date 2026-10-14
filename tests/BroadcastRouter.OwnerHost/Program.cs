using System.Text.Json;
using BroadcastRouter.Domain;
using BroadcastRouter.Infrastructure;

// Dedicated crash-test host; never opens network or hardware. The only executables
// supplied by its caller are copied synthetic media tools under a unique temp root.
if (args is not ["helpers", var root]) return 64;
string Tool(string name) => Path.Combine(root, name, "BroadcastRouter.FakeFfmpeg.exe");
var source = new DiscoveredSource(new("TEST", "fixture", "default", "synthetic"), "Synthetic",
    new Uri("rtsp://localhost/fixture/synthetic"), SourceState.Ready, 0,
    new("h264", "aac", 1920, 1080, 25, null, 48000, 2, true));
var port = new DeckLinkPort("synthetic-port", "synthetic-handle", "Synthetic", 0, 0, null, []);
var preset = new OutputPreset("synthetic", "Synthetic", new(1920, 1080, 25, 1, "uyvy422"), false, 256);
await using var live = new FfmpegProcessSupervisor(new(Tool("live")), TimeSpan.FromSeconds(1));
await using var preview = new BrowserPreviewSupervisor();
using var stop = new CancellationTokenSource();
await live.StartAsync(new(source.Identity, port.StableId, preset.Id, RouteState.Starting,
    AssignmentMode.Manual, false), source, port, preset, stop.Token);
await preview.StartAsync(source, new() { FfmpegPath = Tool("preview"), FfprobePath = Tool("probe") }, stop.Token);
var probe = new FfprobeStreamProbe(Tool("probe"), TimeSpan.FromMinutes(2)).ProbeAsync(source.RtspUri, stop.Token);
var auxiliary = ExternalCommandRunner.RunAsync(Tool("auxiliary"),
    [Path.Combine(root, "auxiliary", "auxiliary.json")], TimeSpan.FromMinutes(2), stop.Token);
await File.WriteAllTextAsync(Path.Combine(root, "host-started.json"), JsonSerializer.Serialize(new { id = Environment.ProcessId }));
await Task.Delay(Timeout.InfiniteTimeSpan);
return 0;
