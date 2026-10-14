using System.Diagnostics;
using System.Text.Json;
using BroadcastRouter.Domain;
using BroadcastRouter.Infrastructure;
using Xunit;

namespace BroadcastRouter.Tests;

public sealed class BenchOutputGuardTests
{
    [Theory]
    [InlineData("six-ports")]
    [InlineData("four-ports")]
    [InlineData("duplicate-handle")]
    [InlineData("duplicate-id")]
    [InlineData("wrong-connector")]
    [InlineData("mixed-card")]
    public void AmbiguousOrBroadenedPolicyCannotBeConstructed(string defect)
    {
        var bindings = Bindings().ToList();
        switch (defect)
        {
            case "six-ports": bindings.Add(new(6, Port(6).StableId, "TEST-HANDLE-6", "TEST-CARD", 5)); break;
            case "four-ports": bindings.RemoveAt(4); break;
            case "duplicate-handle": bindings[4] = bindings[4] with { DeviceHandle = "TEST-HANDLE-1" }; break;
            case "duplicate-id": bindings[4] = bindings[4] with { StableId = Port(1).StableId }; break;
            case "wrong-connector": bindings[4] = bindings[4] with { Connector = 6 }; break;
            case "mixed-card": bindings[4] = bindings[4] with { DeviceGroupId = "OTHER-CARD" }; break;
        }
        Assert.Throws<BenchSafetyException>(() => new BenchOutputGuard(bindings));
    }

    public static IEnumerable<object[]> RejectedStarts() =>
        from path in new[] { "live", "fallback", "standby", "recovery-standby" }
        from defect in new[] { "sixth", "alias", "handle", "group", "connector", "unverified" }
        select new object[] { path, defect };

    [Theory, MemberData(nameof(RejectedStarts))]
    public async Task EveryStartPathRefusesWrongPhysicalTargetBeforeCreatingAnyChild(string path, string defect)
    {
        await using var fixture = new FakeChild();
        var port = Port(1);
        port = defect switch
        {
            "sixth" => Port(6),
            "alias" => port with { FfmpegName = "DeckLink identical model (1)" },
            "handle" => port with { DeviceHandle = "TEST-HANDLE-6" },
            "group" => port with { DeviceGroupId = "OTHER-CARD" },
            "connector" => port with { SubdeviceIndex = 5 },
            "unverified" => port with { PersistentId = null },
            _ => throw new ArgumentOutOfRangeException(nameof(defect))
        };
        await using var supervisor = new FfmpegProcessSupervisor(new(fixture.Executable), TimeSpan.FromSeconds(1),
            benchGuard: new(Bindings()));
        var error = await Assert.ThrowsAsync<BenchSafetyException>(() => Start(supervisor, path, port));
        Assert.Contains("Bench output refused", error.Message);
        Assert.Empty(supervisor.Snapshot());
        Assert.False(File.Exists(Path.Combine(fixture.Root, "arguments.json")));
    }

    [Theory]
    [InlineData("live")]
    [InlineData("fallback")]
    [InlineData("standby")]
    [InlineData("recovery-standby")]
    public async Task AllowedStartUsesExactHandleNotProductName(string path)
    {
        await using var fixture = new FakeChild();
        await using var supervisor = new FfmpegProcessSupervisor(new(fixture.Executable), TimeSpan.FromSeconds(1),
            benchGuard: new(Bindings()));
        await Start(supervisor, path, Port(3));
        await FaultHarnessTests.Eventually(() => File.Exists(Path.Combine(fixture.Root, "arguments.json")));
        var args = JsonSerializer.Deserialize<string[]>(await File.ReadAllTextAsync(Path.Combine(fixture.Root, "arguments.json")))!;
        Assert.Equal("TEST-HANDLE-3", args[^1]);
        Assert.Single(supervisor.RunningSnapshot());
        await supervisor.StopAsync(Source, CancellationToken.None);
        Assert.Empty(supervisor.RunningSnapshot());
    }

    [Theory]
    [InlineData("different-output")]
    [InlineData("extra-decklink-output")]
    [InlineData("shell")]
    [InlineData("raw-arguments")]
    [InlineData("no-port")]
    public void FinalCommandCannotBypassPortIdentityValidation(string defect)
    {
        var start = new ProcessStartInfo("synthetic.exe") { UseShellExecute = false };
        foreach (var arg in new[] { "-f", "decklink", "TEST-HANDLE-1" }) start.ArgumentList.Add(arg);
        if (defect == "different-output") start.ArgumentList[^1] = "TEST-HANDLE-6";
        if (defect == "extra-decklink-output")
            foreach (var arg in new[] { "-f", "decklink", "TEST-HANDLE-1" }) start.ArgumentList.Add(arg);
        if (defect == "shell") start.UseShellExecute = true;
        if (defect == "raw-arguments") start.Arguments = "-f decklink TEST-HANDLE-6";
        Assert.Throws<BenchSafetyException>(() => new BenchOutputGuard(Bindings()).ValidateLaunch(defect == "no-port" ? null : Port(1), start));
    }

    [Fact]
    public void CallerCannotWidenAllowlistAfterConstruction()
    {
        var bindings = Bindings().ToList();
        var guard = new BenchOutputGuard(bindings);
        bindings[0] = new(1, Port(6).StableId, "TEST-HANDLE-6", "TEST-CARD", 5);
        var start = new ProcessStartInfo("synthetic.exe") { UseShellExecute = false };
        foreach (var arg in new[] { "-f", "decklink", "TEST-HANDLE-6" }) start.ArgumentList.Add(arg);
        Assert.Throws<BenchSafetyException>(() => guard.ValidateLaunch(Port(6), start));
    }

    internal static IEnumerable<BenchPortBinding> Bindings() => Enumerable.Range(1, 5).Select(n =>
        new BenchPortBinding(n, Port(n).StableId, $"TEST-HANDLE-{n}", "TEST-CARD", n - 1));

    internal static DeckLinkPort Port(int n) => new($"DECKLINK-PERSISTENT-{256 + n:X8}", $"TEST-HANDLE-{n}",
        "Synthetic identical model", 0, n - 1, null, [], PersistentId: $"0x{256 + n:X8}", DeviceGroupId: "TEST-CARD",
        DeviceHandle: $"TEST-HANDLE-{n}", IsOutputPort: true);

    private static readonly SourceIdentity Source = new("TEST", "fixture", "default", "synthetic");
    private static Task Start(FfmpegProcessSupervisor supervisor, string path, DeckLinkPort port)
    {
        var preset = new OutputPreset("test", "Test", new(1920, 1080, 25, 1, "uyvy422"), false, 0);
        return path switch
        {
            "live" => supervisor.StartAsync(new(Source, port.StableId, "test", RouteState.Starting, AssignmentMode.Manual, false),
                new(Source, "Synthetic", new Uri("rtsp://example.invalid/test"), SourceState.Ready, 0, RecoveryReadinessTests.Av().Media),
                port, preset, CancellationToken.None),
            "fallback" => supervisor.StartFallbackAsync(Source, port, preset, FallbackMode.Black, null, CancellationToken.None),
            "standby" => supervisor.StartPortStandbyAsync(Source, port, preset, new(StandbyPattern.Black, null, "Synthetic", false), CancellationToken.None),
            "recovery-standby" => supervisor.StartRecoveryStandbyAsync(Source, port, preset, new(StandbyPattern.Black, null, "Synthetic", false), CancellationToken.None),
            _ => throw new ArgumentOutOfRangeException(nameof(path))
        };
    }

    private sealed class FakeChild : IAsyncDisposable
    {
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), "bench-guard-" + Guid.NewGuid().ToString("N"));
        internal string Executable => Path.Combine(Root, "BroadcastRouter.FakeFfmpeg.exe");
        internal FakeChild()
        {
            Directory.CreateDirectory(Root);
            var bin = Path.Combine(FaultHarnessTests.RepositoryRoot(), "tests", "BroadcastRouter.FakeFfmpeg", "bin", "Release", "net8.0");
            foreach (var file in Directory.GetFiles(bin, "BroadcastRouter.FakeFfmpeg.*")) File.Copy(file, Path.Combine(Root, Path.GetFileName(file)));
            File.WriteAllText(Path.Combine(Root, "live.json"), "{\"captureArguments\":true,\"events\":[]}");
        }
        public ValueTask DisposeAsync() { Directory.Delete(Root, true); return ValueTask.CompletedTask; }
    }
}
