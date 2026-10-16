using System.Diagnostics;
using System.Reflection;
using BroadcastRouter.Domain;
using BroadcastRouter.Infrastructure;
using Xunit;

namespace BroadcastRouter.Tests;

public sealed class StandbySlateTests
{
    [Fact]
    public async Task DisposalAfterPreparationPreservesClosedAdmissionContract()
    {
        using var lab = new Lab(); var registry = new BroadcastRouter.Application.PortReservationManager();
        await using var supervisor = new FfmpegProcessSupervisor(lab.Options, TimeSpan.FromSeconds(1), reservations: registry);
        var source = new SourceIdentity("test", "standby", "owned", "queued");
        var gates = (System.Collections.Concurrent.ConcurrentDictionary<string, SemaphoreSlim>)typeof(FfmpegProcessSupervisor)
            .GetField("_ownerGates", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(supervisor)!;
        var held = gates.GetOrAdd(source.Value, _ => new(1, 1));
        await held.WaitAsync();
        Task? start = null; Task? dispose = null;
        try
        {
            start = supervisor.StartPortStandbyAsync(source, Port, Preset, Config, default);
            await FaultHarnessTests.Eventually(() => registry.ControllerFor(Port.StableId).Status.OperationActive);
            dispose = supervisor.DisposeAsync().AsTask();
            held.Release();
            var error = await Record.ExceptionAsync(() => start);
            Assert.IsType<ObjectDisposedException>(error); // No new cancellation after preparation already transferred.
            await dispose.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Empty(supervisor.Snapshot());
        }
        finally
        {
            if (held.CurrentCount == 0) held.Release();
            if (start is not null) { try { await start; } catch { } }
            if (dispose is not null) await dispose;
        }
    }
    [Fact]
    public async Task PreparationDoesNotDependOnOperatorSynchronizationContext()
    {
        using var lab = new Lab();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cache = new StandbySlateCache(lab.Cache, async (start, token) =>
        { entered.SetResult(); await File.WriteAllBytesAsync(start.ArgumentList[^1], Image, token); });
        var heldContext = new HeldContext(); var original = SynchronizationContext.Current;
        Task<PreparedStandbySlate> preparation;
        try
        {
            SynchronizationContext.SetSynchronizationContext(heldContext);
            preparation = cache.PrepareAsync(lab.Options, Port, Preset, Config, default);
        }
        finally { SynchronizationContext.SetSynchronizationContext(original); }
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.True(File.Exists((await preparation.WaitAsync(TimeSpan.FromSeconds(2))).Path));
        }
        finally { heldContext.Drain(); await preparation.WaitAsync(TimeSpan.FromSeconds(2)); }
    }

    private sealed class HeldContext : SynchronizationContext
    {
        private readonly System.Collections.Concurrent.ConcurrentQueue<(SendOrPostCallback Callback, object? State)> _posts = new();
        public override void Post(SendOrPostCallback callback, object? state) => _posts.Enqueue((callback, state));
        public void Drain() { while (_posts.TryDequeue(out var item)) item.Callback(item.State); }
    }
    [Fact]
    public async Task BenchIdentityRefusalPrecedesAnyPreparationInputAccess()
    {
        using var lab = new Lab(); var invoked = false;
        var cache = new StandbySlateCache(lab.Cache, (_, _) => { invoked = true; throw new IOException("Must not render"); });
        await using var supervisor = new FfmpegProcessSupervisor(new("missing-bench-tool.exe"), TimeSpan.FromSeconds(1),
            benchGuard: new BenchOutputGuard(BenchOutputGuardTests.Bindings()), standbySlates: cache);
        await Assert.ThrowsAsync<BenchSafetyException>(() => supervisor.StartPortStandbyAsync(new("test", "guard", "default", "source"),
            BenchOutputGuardTests.Port(6), Preset, Config, default));
        Assert.False(invoked);
        Assert.Empty(supervisor.Snapshot());
        Assert.False(Directory.Exists(lab.Cache));
    }
    [Theory]
    [InlineData("", "png_pipe", "png decoder")]
    [InlineData("png", "", "png_pipe demuxer")]
    public void PreparedPlaybackRequiresDecodeAndDemuxSupport(string decoders, string demuxers, string missing)
    {
        var method = typeof(MediaToolValidator).GetMethod("MissingStandbyInputTools", BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(method);
        Assert.Contains(missing, (string[])method.Invoke(null, [decoders, demuxers])!);
        Assert.Empty((string[])method.Invoke(null, [" V....D png PNG image", " D png_pipe piped PNG sequence"])!);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StopAndDisposalCancelAndDrainAlreadyAdmittedPreparation(bool disposal)
    {
        using var lab = new Lab(); using var caller = new CancellationTokenSource();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cleanup = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var exited = false;
        var cache = new StandbySlateCache(lab.Cache, async (_, token) =>
        {
            entered.SetResult();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
            finally { cancelled.SetResult(); await cleanup.Task; exited = true; }
        });
        await using var supervisor = new FfmpegProcessSupervisor(lab.Options, TimeSpan.FromSeconds(1), standbySlates: cache);
        var source = new SourceIdentity("test", "standby", "owned", "one");
        Task request = disposal ? supervisor.PrepareStandbyAsync(Port, Preset, Config, caller.Token)
            : supervisor.StartPortStandbyAsync(source, Port, Preset, Config, caller.Token);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            var stop = disposal ? supervisor.DisposeAsync().AsTask() : supervisor.StopAsync(source, default);
            Assert.False(stop.IsCompleted, "Stop must not acknowledge while its admitted renderer still owns cleanup.");
            await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.False(stop.IsCompleted);
            cleanup.SetResult();
            await stop.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.True(exited);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request);
            Assert.Empty(supervisor.RunningSnapshot());
            Assert.Empty(Directory.GetFiles(lab.Cache, "*.png"));
        }
        finally
        {
            caller.Cancel(); cleanup.TrySetResult();
            try { await request; } catch (OperationCanceledException) { }
        }
    }
    [Theory]
    [InlineData("movie")]
    [InlineData("loop")]
    [InlineData("setpts")]
    [InlineData("drawbox")]
    [InlineData("format")]
    [InlineData("fps")]
    [InlineData("png")]
    [InlineData("image2")]
    public void StandbyCapabilityRequiresPreparedPlaybackAndRenderTools(string missing)
    {
        var method = typeof(MediaToolValidator).GetMethod("MissingStandbyTools", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
        Assert.NotNull(method);
        var filters = "color smptebars smptehdbars testsrc2 overlay drawtext movie loop setpts drawbox format fps anullsrc aresample asetpts volume";
        var found = (IReadOnlyList<string>)method.Invoke(null, [string.Join(' ', filters.Split(' ').Where(x => x != missing)), missing == "png" ? "" : "png", missing == "image2" ? "" : "image2"])!;
        Assert.Contains(found, x => x.Contains(missing, StringComparison.Ordinal));
    }

    [Fact]
    public void ValidChunkChecksumsDoNotMakeCorruptPixelDataSafe()
    {
        var corrupt = (byte[])Image.Clone();
        var offset = 8;
        while (offset < corrupt.Length)
        {
            var size = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(corrupt.AsSpan(offset, 4));
            if (System.Text.Encoding.ASCII.GetString(corrupt, offset + 4, 4) == "IDAT")
            {
                corrupt[offset + 8] = 0;
                uint crc = uint.MaxValue;
                foreach (var value in corrupt.AsSpan(offset + 4, size + 4))
                { crc ^= value; for (var i = 0; i < 8; i++) crc = (crc >> 1) ^ ((crc & 1) == 0 ? 0 : 0xEDB88320U); }
                System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(corrupt.AsSpan(offset + size + 8), ~crc);
                break;
            }
            offset += size + 12;
        }
        Assert.ThrowsAny<InvalidDataException>(() => StandbySlateCache.ValidatePng(corrupt, 2, 2));
    }
    private static readonly DeckLinkPort Port = new("SYNTHETIC", "SYNTHETIC", "Test card", 0, 0, null, [], CardFriendlyName: "Card A");
    private static readonly OutputPreset Preset = new("synthetic", "synthetic", new(2, 2, 25, 1, "uyvy422"), false, 0, true, true);
    private static readonly PortStandbyConfiguration Config = new(StandbyPattern.TestSource, null, "Label", true);
    private static readonly byte[] Image = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAIAAAACCAIAAAD91JpzAAAACXBIWXMAAAABAAAAAQBPJcTWAAAAE0lEQVR4nGP8//8/AwMDCwMYAAAkFAMDuxa40wAAAABJRU5ErkJggg==");

    [Fact]
    public async Task ContentCacheInvalidatesChangedLogoBytesAndSettings()
    {
        using var lab = new Lab(); var renders = 0;
        var cache = new StandbySlateCache(lab.Cache, async (start, token) => { renders++; await File.WriteAllBytesAsync(start.ArgumentList[^1], Image, token); });
        var logo = Path.Combine(lab.Root, "logo.png"); await File.WriteAllBytesAsync(logo, [1, 2]);
        var config = Config with { LogoPath = logo };
        var first = await cache.PrepareAsync(lab.Options, Port, Preset, config, default);
        var same = await cache.PrepareAsync(lab.Options, Port, Preset, config, default);
        Assert.Equal(first.Key, same.Key); Assert.Equal(1, renders);
        await File.WriteAllBytesAsync(logo, [3, 4]);
        var changed = await cache.PrepareAsync(lab.Options, Port, Preset, config, default);
        Assert.NotEqual(first.Key, changed.Key);
        var labelled = await cache.PrepareAsync(lab.Options, Port, Preset, config with { PortLabel = "Changed" }, default);
        Assert.NotEqual(changed.Key, labelled.Key);
        Assert.Equal(Image, await File.ReadAllBytesAsync(first.Path)); // Old owner never loses its immutable input.
    }

    [Fact]
    public async Task ConcurrentSameContentRendersOnceAndNeverPublishesPartialArtifact()
    {
        using var lab = new Lab(); var renders = 0;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cache = new StandbySlateCache(lab.Cache, async (start, token) =>
        { Interlocked.Increment(ref renders); entered.SetResult(); await release.Task.WaitAsync(token); await File.WriteAllBytesAsync(start.ArgumentList[^1], Image, token); });
        var first = cache.PrepareAsync(lab.Options, Port, Preset, Config, default);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var second = cache.PrepareAsync(lab.Options, Port, Preset, Config, default);
        Assert.Empty(Directory.GetFiles(lab.Cache, "*.png"));
        release.SetResult();
        var all = await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(1, renders); Assert.Equal(all[0].Path, all[1].Path);
    }

    [Theory]
    [InlineData("cancel")]
    [InlineData("malformed")]
    [InlineData("failure")]
    [InlineData("capacity")]
    public async Task FailedPreparationLeavesExistingArtifactUntouched(string failure)
    {
        using var lab = new Lab(); using var cancel = new CancellationTokenSource();
        var render = new StandbySlateCache(lab.Cache, (start, token) => File.WriteAllBytesAsync(start.ArgumentList[^1], Image, token));
        var original = await render.PrepareAsync(lab.Options, Port, Preset, Config, default);
        var candidate = new StandbySlateCache(lab.Cache, async (start, token) =>
        {
            if (failure == "cancel") { cancel.Cancel(); token.ThrowIfCancellationRequested(); }
            if (failure == "failure") throw new IOException("Synthetic render failed");
            await File.WriteAllBytesAsync(start.ArgumentList[^1], [1, 2, 3], token);
        }, failure == "capacity" ? Image.Length : 268435456);
        await Assert.ThrowsAnyAsync<Exception>(() => candidate.PrepareAsync(lab.Options, Port, Preset, Config with { PortLabel = "Changed" }, cancel.Token));
        Assert.Equal(Image, await File.ReadAllBytesAsync(original.Path));
        Assert.Single(Directory.GetFiles(lab.Cache, "*.png"));
        Assert.Empty(Directory.GetDirectories(lab.Cache, ".render-*"));
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void PreparedPlaybackKeepsLivenessInterlaceAndExplicitAudioPolicy(bool clock, bool audio)
    {
        var method = typeof(FfmpegCommandBuilder).GetMethod("BuildPreparedStandby");
        Assert.NotNull(method);
        var start = (ProcessStartInfo)method.Invoke(null, [new FfmpegRouteOptions("ffmpeg.exe"), Port, Preset with { IncludeAudio = audio }, Config with { ShowClock = clock }, new PreparedStandbySlate("slate.png", "key", 2, 2)])!;
        var tokens = start.ArgumentList.ToList(); var graph = tokens[tokens.IndexOf("-vf") + 1];
        Assert.Contains("tinterlace=interleave_top:flags=vlpf,setfield=tff", graph);
        Assert.DoesNotContain("overlay=", graph); Assert.DoesNotContain("split=", graph);
        Assert.DoesNotContain("testsrc2", string.Join(' ', tokens));
        Assert.Contains(clock ? "localtime" : "enable=", graph);
        Assert.Contains("50/1", string.Join(' ', tokens));
        if (audio) { Assert.Contains("pcm_s16le", tokens); Assert.Contains("volume=0", tokens[tokens.IndexOf("-af") + 1]); }
        else Assert.Contains("-an", tokens);
    }

    [Fact]
    public void PreparedImageIsDecodedAndConvertedOnceThenRepeatedAtFieldRate()
    {
        var start = FfmpegCommandBuilder.BuildPreparedStandby(new("ffmpeg.exe"), Port, Preset, Config, new("slate.png", "key", 2, 2));
        Assert.DoesNotContain("-loop", start.ArgumentList);
        var input = start.ArgumentList[start.ArgumentList.IndexOf("-i") + 1];
        Assert.Contains("movie=", input);
        Assert.Contains("dec_threads=1,format=yuv422p,loop=", input);
        Assert.Contains("fps=50/1", input);
        Assert.Contains("-re", start.ArgumentList);
    }

    [Fact]
    public async Task CleanupFailureCannotPermanentlyHoldPreparationGate()
    {
        using var lab = new Lab(); FileStream? held = null; var first = true;
        var cache = new StandbySlateCache(lab.Cache, async (start, token) =>
        {
            await File.WriteAllBytesAsync(start.ArgumentList[^1], Image, token);
            if (first) { first = false; held = new FileStream(start.ArgumentList[^1], FileMode.Open, FileAccess.Read, FileShare.None); }
        });
        try { await Assert.ThrowsAnyAsync<IOException>(() => cache.PrepareAsync(lab.Options, Port, Preset, Config, default)); }
        finally { held?.Dispose(); }
        await cache.PrepareAsync(lab.Options, Port, Preset, Config, default).WaitAsync(TimeSpan.FromSeconds(2));
    }

    private sealed class Lab : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "standby-slate-test-" + Guid.NewGuid().ToString("N"));
        public string Cache => Path.Combine(Root, "cache");
        public FfmpegRouteOptions Options { get; }
        public Lab() { Directory.CreateDirectory(Root); var exe = Path.Combine(Root, "fake.exe"); File.WriteAllText(exe, "fixture"); Options = new(exe); }
        public void Dispose() => Directory.Delete(Root, true);
    }
}
