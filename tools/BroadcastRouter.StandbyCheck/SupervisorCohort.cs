using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using BroadcastRouter.Domain;
using BroadcastRouter.Infrastructure;

namespace BroadcastRouter.StandbyCheck;

internal static class SupervisorCohort
{
    internal static async Task Run(IReadOnlyList<ProcessStartInfo> starts, string root)
    {
        // The production supervisor/controller/containment code, with local null
        // commands only. No telemetry disk-space override: lifecycle and snapshot
        // evidence below does not pretend to include internal q/drain phases.
        await using var supervisor = new FfmpegProcessSupervisor(new(starts[0].FileName), TimeSpan.FromSeconds(5));
        var events = new ConcurrentQueue<RouteProcessLifecycleEvent>();
        supervisor.LifecycleChanged += events.Enqueue;
        var sources = Enumerable.Range(0, starts.Count).Select(i => new SourceIdentity("local", "qualification", "synthetic", i.ToString())).ToArray();
        var processes = new List<Process>();
        var started = DateTimeOffset.UtcNow;
        try
        {
            for (var i = 0; i < starts.Count; i++)
                await supervisor.StartOwnedProcessForTestingAsync(sources[i], starts[i], purpose: RouteProcessPurpose.PortStandby);
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            while (true)
            {
                var current = supervisor.Snapshot();
                if (current.Count == starts.Count && current.All(p => p.Running && p.Progress?.Frame >= 50)) break;
                if (current.Any(p => !p.Running)) throw new IOException("Supervisor owner exited before readiness.");
                await Task.Delay(50, deadline.Token); // condition polling, never a success substitute
            }
            var before = supervisor.Snapshot();
            foreach (var owner in before) processes.Add(Process.GetProcessById(owner.ProcessId));
            var cpuBefore = processes.Select(ReadCpu).ToArray();
            var measured = Stopwatch.StartNew();
            await Task.Delay(TimeSpan.FromSeconds(10));
            var elapsed = measured.Elapsed.TotalSeconds;
            var cpuAfter = processes.Select(ReadCpu).ToArray();
            var advancing = supervisor.Snapshot();
            if (advancing.Any(p => !p.Running || p.Progress!.Frame - before.Single(b => b.SessionId == p.SessionId).Progress!.Frame < 200))
                throw new InvalidDataException("Supervisor output did not keep advancing.");
            var stop = Stopwatch.GetTimestamp();
            await QualificationContract.StopTogetherAsync(sources.Select(s => (Func<Task>)(() => supervisor.StopAsync(s, default))));
            var stoppedMilliseconds = Stopwatch.GetElapsedTime(stop).TotalMilliseconds;
            var after = supervisor.Snapshot();
            var lifecycle = events.ToArray();
            var startCount = lifecycle.Where(e => e.State == RouteProcessLifecycleState.Started).Select(e => e.ProcessId).Distinct().Count();
            var clean = lifecycle.Where(e => e.State == RouteProcessLifecycleState.Exited && e.ExitCode == 0).Select(e => e.ProcessId).Distinct().Count();
            var forced = lifecycle.Count(e => e.State == RouteProcessLifecycleState.ForcedTermination);
            await File.WriteAllTextAsync(Path.Combine(root, "result.json"), JsonSerializer.Serialize(new
            {
                StartedUtc = started, EndedUtc = DateTimeOffset.UtcNow, elapsed, stoppedMilliseconds, startCount, clean, forced,
                cpuBefore, cpuAfter, before, advancing, after, lifecycle,
                boundary = "Actual supervisor stops; internal q/drain phase telemetry unavailable here. No hardware or production-storage qualification."
            }, new JsonSerializerOptions { WriteIndented = true }));
            if (!QualificationContract.AcceptSupervisorGroup(starts.Count, startCount, clean, forced, after.Count(p => p.Running), stoppedMilliseconds))
            {
                await File.WriteAllTextAsync(Path.Combine(root, "target-failure.json"), JsonSerializer.Serialize(new { error = "Clean stop exceeded the unchanged one-second target." }));
                Environment.ExitCode = 1;
            }
            Console.WriteLine($"Supervisor: {clean}/{starts.Count} clean, {forced} forced, stop {stoppedMilliseconds:F3} ms, cores/standby {cpuAfter.Zip(cpuBefore, (a,b) => (a-b)/elapsed).Average():F3}");
        }
        finally { foreach (var process in processes) process.Dispose(); }
    }
    private static double ReadCpu(Process process) => QualificationContract.ReadCpuSeconds(process.Refresh, () => process.TotalProcessorTime.TotalSeconds);
}
