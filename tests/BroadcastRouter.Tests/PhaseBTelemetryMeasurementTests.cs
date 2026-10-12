using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using BroadcastRouter.Application;
using BroadcastRouter.Domain;
using BroadcastRouter.Infrastructure;
using Xunit;
using Xunit.Abstractions;

namespace BroadcastRouter.Tests;

public sealed class PhaseBTelemetryMeasurementTests(ITestOutputHelper output)
{
    [Fact]
    public async Task NormalOutputAndOneOfflineSourceRetainAFullSimulatedDayWithoutRetryStorm()
    {
        await using var lab = await OfflineFallbackTests.RecoveryLab.Create(captureTelemetry: true);
        await lab.InstallFakeMediaTools();
        await File.WriteAllTextAsync(Path.Combine(lab.Root, "probe.json"), JsonSerializer.Serialize(new
        {
            events = new[] { new { atMilliseconds = 0, channel = "stderr", text = "RTSP DESCRIBE failed: 404 Not Found" } },
            exitAfterMilliseconds = 0, exitCode = FfmpegErrorClassifier.AvErrorHttpNotFound
        }));
        // This measurement invokes actual process monitoring, but never hardware discovery.
        lab.Set("_nextDeckLinkReferenceStatusCheck", DateTimeOffset.MaxValue);
        var sink = lab.Telemetry!;
        var original = lab.Owner;
        var liveSource = new SourceIdentity("TEST", "fixture", "_default_", "source-B");
        lab.Sources[liveSource.Value] = new(liveSource, "Synthetic live", new Uri("rtsp://example.invalid/live"),
            SourceState.Ready, 0, RecoveryReadinessTests.Av().Media, LastObservedAt: lab.Clock.Now);
        var livePort = new DeckLinkPort("TEST-LIVE", "Synthetic live output", "Synthetic model", 0, 1, null,
            [], true, IsOutputPort: true);
        lab.Get<Dictionary<string, DeckLinkPort>>("_ports")[livePort.StableId] = livePort;
        lab.Get<PortReservationManager>("_reservations").TryReserve(livePort.StableId, liveSource, true, lab.Clock.Now, out _);
        lab.Routes[liveSource.Value] = lab.Route with
        {
            SourceId = liveSource.Value, SourceName = "Synthetic live", PortId = livePort.StableId,
            DesiredPortId = livePort.StableId, State = RouteState.Running, RetryAt = null,
            FailureCategory = null, FailureMessage = null, RestartCount = 0
        };
        var script = Path.Combine(lab.Root, "normal.json");
        await File.WriteAllTextAsync(script, JsonSerializer.Serialize(new
        {
            progressCommands = true, events = Enumerable.Range(0, 100).Select(i => new
            { atMilliseconds = 0, channel = "stderr", text = $"Synthetic healthy startup line {i:D3}: " + new string('x', 256) })
        }));
        var start = new ProcessStartInfo(Path.Combine(lab.Root, "BroadcastRouter.FakeFfmpeg.exe"))
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add(script);
        await lab.Supervisor.StartOwnedProcessForTestingAsync(liveSource, start);
        Assert.Equal(2, lab.Supervisor.Snapshot().Count(p => p.Running));
        sink.ObservePublisher(liveSource.Value, true);
        var liveSession = lab.Supervisor.Snapshot().Single(p => p.Source == liveSource).SessionId;
        // Accelerated 24h: actual child pipes, parser, supervisor, coordinator,
        // SQLite writes and real telemetry worker. External media/hardware are fake.
        for (var second = 30; second <= 86400; second += 30)
        {
            lab.Time.Advance(TimeSpan.FromSeconds(30));
            await Progress(lab, liveSource, second);
            await Progress(lab, lab.Source, second);
            await lab.Invoke("MonitorProcessesAsync", CancellationToken.None);
            await lab.RestartDue();
            await CompleteProbe(lab);
            await lab.Invoke("ReconcileRoutesAsync", CancellationToken.None);
            await CompleteProbe(lab);
            Assert.Equal(RouteState.Running, lab.Routes[liveSource.Value].State);
            Assert.Equal(original.SessionId, lab.Owner.SessionId);
            if (second % 300 == 0) await sink.FlushAsync();
        }
        await sink.FlushAsync();
        var files = Directory.GetFiles(Path.Combine(lab.Root, "telemetry"), "*.jsonl");
        var bytes = files.Sum(p => new FileInfo(p).Length);
        var decisions = files.SelectMany(File.ReadAllLines).Select(line =>
        {
            using var document = JsonDocument.Parse(line);
            return document.RootElement.GetProperty("Event").Deserialize<SupervisionDecisionRecord>();
        }).Where(record => record?.Trigger == "RecoveryReadinessDue" && record.Result != "Requested").ToArray();
        Assert.NotEmpty(decisions);
        Assert.All(decisions, record => Assert.Equal("RtspNotFound", record!.Result));
        var live = lab.Supervisor.Snapshot().Single(p => p.Source == liveSource);
        var result = new
        {
            Scenario = "24h fake normal advancing output plus one permanently offline reserved source",
            SimulatedSeconds = 86400, SupervisionCadenceSeconds = 30, Frames = live.Progress?.Frame, SegmentBytesPerDay = bytes,
            ControlFileBytes = new FileInfo(Path.Combine(lab.Root, "telemetry", "retention-loss.json")).Length,
            CapacityDaysIgnoringAgeLimit = (10_000_000_000L - 65536) / (double)bytes,
            ActualAgeRetentionDays = 30, Files = files.Length, sink.Status,
            OfflineProbes = decisions.Length,
            OwnedStarts = lab.Events.Count(e => e.State == RouteProcessLifecycleState.Started)
        };
        output.WriteLine(JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
        Assert.Equal(2160000, live.Progress?.Frame);
        Assert.Equal(liveSession, live.SessionId);
        Assert.Equal(original.SessionId, lab.Owner.SessionId);
        Assert.Equal(2, lab.Events.Count(e => e.State == RouteProcessLifecycleState.Started));
        Assert.DoesNotContain(lab.Events, e => e.State == RouteProcessLifecycleState.StopRequested);
        Assert.True(sink.Status.IsHealthy);
        Assert.Equal(sink.Status.Accepted, sink.Status.Written);
        Assert.InRange(bytes, 1, 10_000_000_000L);
    }

    private static async Task Progress(OfflineFallbackTests.RecoveryLab lab, SourceIdentity source, int second)
    {
        var owners = typeof(FfmpegProcessSupervisor).GetField("_running", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(lab.Supervisor)!;
        var managed = owners.GetType().GetProperty("Item")!.GetValue(owners, [source.Value])!;
        var process = (Process)managed.GetType().GetProperty("Process")!.GetValue(managed)!;
        await process.StandardInput.WriteLineAsync($"progress {second * 25L} {second * 1000000L}");
        await process.StandardInput.FlushAsync();
        await FaultHarnessTests.Eventually(() => lab.Supervisor.Snapshot().Single(p => p.Source == source).Progress?.Frame == second * 25L);
    }

    private static async Task CompleteProbe(OfflineFallbackTests.RecoveryLab lab)
    {
        var gates = lab.Get<System.Collections.Concurrent.ConcurrentDictionary<string, RecoveryReadinessGate>>("_recoveryReadiness");
        if (gates.TryGetValue(lab.Source.Value, out var gate)
            && typeof(RecoveryReadinessGate).GetField("_pending", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(gate) is Task pending)
        {
            await pending.WaitAsync(TimeSpan.FromSeconds(5));
            await lab.RestartDue();
        }
    }
}
