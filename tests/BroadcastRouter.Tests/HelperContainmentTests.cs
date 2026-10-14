using System.Diagnostics;
using System.Text.Json;
using Xunit;

namespace BroadcastRouter.Tests;

public sealed class HelperContainmentTests
{
    [Fact]
    public async Task HostCrashTerminatesEveryOwnedHelperAndDescendantButNotSibling()
    {
        var root = Path.Combine(Path.GetTempPath(), "router-helper-containment-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var held = new List<(string Name, Process Process)>();
        Process? host = null; Process? sibling = null;
        try
        {
            foreach (var name in new[] { "live", "preview", "probe", "auxiliary", "sibling" })
            {
                var directory = Path.Combine(root, name); Directory.CreateDirectory(directory);
                var fakeBin = Path.Combine(FaultHarnessTests.RepositoryRoot(), "tests", "BroadcastRouter.FakeFfmpeg", "bin", "Release", "net8.0");
                foreach (var file in Directory.GetFiles(fakeBin)) File.Copy(file, Path.Combine(directory, Path.GetFileName(file)));
                var childScript = Path.Combine(directory, "descendant.json");
                await File.WriteAllTextAsync(childScript, JsonSerializer.Serialize(new
                    { events = Array.Empty<object>(), startMarker = Path.Combine(directory, "descendant.started"), ignoreQuit = true }));
                var script = JsonSerializer.Serialize(new
                {
                    events = Array.Empty<object>(), startMarker = Path.Combine(directory, "parent.started"),
                    spawnChildScript = name == "sibling" ? null : childScript, ignoreQuit = true
                });
                foreach (var file in new[] { "live.json", "probe.json", "auxiliary.json" })
                    await File.WriteAllTextAsync(Path.Combine(directory, file), script);
            }
            sibling = Process.Start(Start(Path.Combine(root, "sibling", "BroadcastRouter.FakeFfmpeg.exe"),
                Path.Combine(root, "sibling", "live.json")))!;
            _ = sibling.SafeHandle;
            var hostExe = Path.Combine(FaultHarnessTests.RepositoryRoot(), "tests", "BroadcastRouter.OwnerHost", "bin", "Release", "net8.0", "BroadcastRouter.OwnerHost.exe");
            host = Process.Start(Start(hostExe, "helpers", root))!;
            _ = host.SafeHandle;
            var hostErrors = host.StandardError.ReadToEndAsync();
            foreach (var name in new[] { "live", "preview", "probe", "auxiliary" })
            foreach (var kind in new[] { "parent", "descendant" })
            {
                var marker = Path.Combine(root, name, kind + ".started");
                await FaultHarnessTests.Eventually(() => File.Exists(marker) || host.HasExited);
                Assert.False(host.HasExited, "Synthetic owner host exited before readiness: " + (host.HasExited ? await hostErrors : ""));
                using var identity = JsonDocument.Parse(await File.ReadAllTextAsync(marker));
                var process = Process.GetProcessById(identity.RootElement.GetProperty("id").GetInt32());
                _ = process.SafeHandle; // retain the exact OS identity, not a later PID lookup
                held.Add((name + "/" + kind, process));
                Assert.Equal(identity.RootElement.GetProperty("startedUtc").GetDateTime(), process.StartTime.ToUniversalTime());
                Assert.False(process.HasExited);
            }
            // Deliberately NOT entireProcessTree: the real OS containment must do
            // the cleanup, otherwise this test would test Process.Kill instead.
            host.Kill(); await host.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            var deadline = Stopwatch.StartNew();
            while (held.Any(item => !item.Process.HasExited) && deadline.Elapsed < TimeSpan.FromSeconds(5))
                await Task.Delay(20);
            Assert.False(sibling.HasExited);
            var survivors = held.Where(item => !item.Process.HasExited).Select(item => item.Name).ToArray();
            Assert.True(survivors.Length == 0, "Owned helpers survived host crash: " + string.Join(", ", survivors));
        }
        finally
        {
            // Includes expected RED survivors. Never enumerate/kill by name.
            if (host is not null) await Reap(host);
            foreach (var item in held) await Reap(item.Process);
            if (sibling is not null) await Reap(sibling);
            var cleanup = Stopwatch.StartNew();
            while (true)
            {
                try { Directory.Delete(root, true); break; }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException && cleanup.Elapsed < TimeSpan.FromSeconds(5))
                { await Task.Delay(50); }
            }
        }
    }

    private static ProcessStartInfo Start(string executable, params string[] args)
    {
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in args) start.ArgumentList.Add(argument);
        return start;
    }

    private static async Task Reap(Process process)
    {
        try { if (!process.HasExited) process.Kill(); await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)); }
        finally { process.Dispose(); }
    }
}
