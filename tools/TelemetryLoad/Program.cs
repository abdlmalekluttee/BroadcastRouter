using System.Diagnostics;
using System.Text.Json;
using BroadcastRouter.Infrastructure;

// Local storage-only measurement. No server, network, process supervisor or device.
// Leaves the synthetic output for inspection; never deletes an existing directory.
var count = args.Length == 1 ? int.Parse(args[0]) : 1000;
if (args.Length > 1 || count is < 1 or > 10000) throw new ArgumentException("Usage: TelemetryLoad [sessions:1..10000]");
var root = Path.Combine(Path.GetTempPath(), "router-telemetry-measure-" + Guid.NewGuid().ToString("N"));
var started = Stopwatch.StartNew();
await using var sink = new SupervisionTelemetry(root);
for (var index = 0; index < count; index++)
{
    var session = Guid.NewGuid();
    sink.Record("synthetic-owner", "synthetic-output", null, "OwnedProcessLifecycle", new(ProcessAlive: true), null,
        "OS process lifecycle", null, [], "Started", "Observed");
    for (var line = 0; line < 100; line++)
        sink.Stderr(session, $"Synthetic startup diagnostic {line:D3}: " + new string('x', 256));
    sink.Record("synthetic-owner", "synthetic-output", null, "OwnedProcessLifecycle", new(ProcessAlive: false, ExitCode: -5), null,
        "OS process lifecycle", null, [], "Exited", "Exit code -5");
    sink.RouterLog("Warning", "SyntheticLifecycle", "Synthetic child exited; retry evidence retained");
    // Size/throughput fixture, not a queue-overload test. Backpressure is explicit.
    if (index % 10 == 9) await sink.FlushAsync();
}
await sink.FlushAsync();
var files = Directory.GetFiles(root, "*.jsonl").Select(path => new FileInfo(path)).ToArray();
var bytes = files.Sum(file => file.Length);
Console.WriteLine(JsonSerializer.Serialize(new
{
    Root = root, Sessions = count, StderrLinesPerSession = 100,
    ElapsedSeconds = started.Elapsed.TotalSeconds, Files = files.Length, Bytes = bytes,
    sink.Status, SegmentBytes = SupervisionTelemetry.DefaultSegmentBytes,
    MaxSegments = SupervisionTelemetry.DefaultMaxSegments,
    ConfiguredCapacityBytes = (long)SupervisionTelemetry.DefaultSegmentBytes * SupervisionTelemetry.DefaultMaxSegments,
    SingleStormThirtyDayTimesTwoBytes = (long)Math.Ceiling(bytes / (double)count * 439315 * 2),
    AllOwnersThirtyDayTimesTwoBytes = (long)Math.Ceiling(bytes / (double)count * 624413 * 2)
}, new JsonSerializerOptions { WriteIndented = true }));
if (!sink.Status.RetentionComplete || sink.Status.Accepted != sink.Status.Written) Environment.ExitCode = 1;
