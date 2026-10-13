using System.Diagnostics;
using System.IO.Pipes;
using BroadcastRouter.Application;
using BroadcastRouter.Domain;
using BroadcastRouter.Infrastructure;
using Xunit;

namespace BroadcastRouter.Tests;

public sealed class FfmpegMediaSessionTests
{
    private static readonly MediaProperties Av = new("h264", "aac", 1920, 1080, 25, null, 48000, 2, true);

    [Fact]
    public async Task SparseInputHasNoCadenceFailureButPcmAndOutputLossRemainAuthoritative()
    {
        var time = new ManualTimeProvider();
        var clock = new SupervisionClock(time);
        await using var session = new FfmpegMediaSession(Av with { Expectation = SourceMediaExpectation.SparseVideo }, true, clock);
        var command = Command();
        await session.AttachToAsync(command);
        var writers = await Connect(PipeNames(command));
        try
        {
            await Qualify(session, time, clock, writers);
            time.Advance(TimeSpan.FromSeconds(25));
            for (var i = 0; i <= 4; i++)
            {
                if (i > 0) time.Advance(TimeSpan.FromMilliseconds(500));
                await writers[1].WriteLineAsync($"brmedia|mux|v|{100 + i}");
                await writers[2].WriteLineAsync($"brmedia|mux|a|{100 + i}");
                await writers[3].WriteLineAsync($"frame:{100+i} pts:{(100+i)*1024} pts_time:0\nlavfi.astats.Overall.Number_of_samples={(101+i)*1024}.000000");
                await FaultHarnessTests.Eventually(() => session.Snapshot(true).Signals.Where(s => s.Kind != MediaSignalKind.InputVideo)
                    .All(s => s.LastAdvancedAt == clock.Now));
            }
            var still = session.Snapshot(true);
            Assert.Equal(MediaSignalState.Unknown, still.Signals.Single(s => s.Kind == MediaSignalKind.InputVideo).State);
            Assert.All(still.Signals.Where(s => s.Kind != MediaSignalKind.InputVideo), s => Assert.Equal(MediaSignalState.Healthy, s.State));
            Assert.False(still.MayRecover);
            time.Advance(TimeSpan.FromSeconds(5));
            Assert.True(session.Snapshot(true).ConfirmedMediaFailure);
        }
        finally { foreach (var writer in writers) await writer.DisposeAsync(); }
    }

    // Break caught: generated output tracks must never be labelled source advancement.
    [Theory]
    [InlineData(true, true, true, 4)]
    [InlineData(false, true, true, 2)]
    [InlineData(true, false, true, 2)]
    [InlineData(true, true, false, 2)]
    [InlineData(false, false, true, 0)]
    public async Task OnlyMappedRealSourceTracksGetIndependentInputAndOutputChannels(
        bool video, bool audio, bool includeAudio, int expectedChannels)
    {
        var media = Av with { HasUsableVideo = video, AudioCodec = audio ? "aac" : null };
        await using var session = new FfmpegMediaSession(media, includeAudio, new SupervisionClock(new ManualTimeProvider()));
        var start = Command();
        await session.AttachToAsync(start);
        var args = start.ArgumentList.ToArray();
        var destinations = FfmpegAudioSessionIntegrationTests.Endpoints(args);
        Assert.Equal(expectedChannels, destinations.Length);
        Assert.Equal(expectedChannels, destinations.Distinct().Count());
        Assert.Equal("NUL", args[^1]);
        Assert.Equal(video, args.Contains("-stats_enc_pre:v:0"));
        Assert.DoesNotContain("-stats_enc_pre:a:0", args);
        Assert.Equal(audio && includeAudio, args.Any(arg => arg.StartsWith("astats=", StringComparison.Ordinal)));
        Assert.Equal(video, args.Contains("-stats_mux_pre:v:0"));
        Assert.Equal(audio && includeAudio, args.Contains("-stats_mux_pre:a:0"));
        Assert.Equal(expectedChannels, session.Snapshot(true).Signals.Count);
        Assert.All(session.Snapshot(true).Signals, signal => Assert.Equal(MediaSignalState.Unknown, signal.State));
    }

    [Fact]
    public async Task IncomingRecordsReachHealthSnapshotWhileHeartbeatCannotHideStalledMedia()
    {
        var time = new ManualTimeProvider();
        var clock = new SupervisionClock(time);
        await using var session = new FfmpegMediaSession(Av, true, clock);
        var start = Command();
        await session.AttachToAsync(start);
        var names = PipeNames(start);
        Assert.Equal(4, names.Length);
        var writers = await Connect(names);
        try
        {
            await Qualify(session, time, clock, writers);
            time.Advance(TimeSpan.FromSeconds(23));
            time.StepUtc(TimeSpan.FromHours(-12));
            session.ObserveHeartbeat();
            var failed = session.Snapshot(true);
            Assert.Equal(MediaSignalState.Healthy, failed.Heartbeat);
            Assert.All(failed.Signals, signal => Assert.Equal(MediaSignalState.Failed, signal.State));
            Assert.True(failed.ConfirmedMediaFailure);
        }
        finally { foreach (var writer in writers) await writer.DisposeAsync(); }
    }

    [Fact]
    public async Task PipeLossInvalidatesPreviouslyQualifiedEvidenceInsteadOfInventingMediaFailure()
    {
        var time = new ManualTimeProvider();
        var clock = new SupervisionClock(time);
        await using var session = new FfmpegMediaSession(Av, true, clock);
        var start = Command();
        await session.AttachToAsync(start);
        var names = PipeNames(start);
        Assert.Equal(4, names.Length);
        var writers = await Connect(names);
        try { await Qualify(session, time, clock, writers); }
        finally { foreach (var writer in writers) await writer.DisposeAsync(); }
        await FaultHarnessTests.Eventually(() => session.Snapshot(true).Signals.All(signal => signal.State == MediaSignalState.Unknown));
        time.Advance(TimeSpan.FromMinutes(1));
        Assert.False(session.Snapshot(true).MayRecover);
        Assert.True(session.Snapshot(false).MayRecover); // OS exit remains definitive.
    }

    [Fact]
    public async Task SessionRejectsReuseRatherThanAddingDuplicateObserversToAnotherOwner()
    {
        await using var session = new FfmpegMediaSession(Av, true, new SupervisionClock(new ManualTimeProvider()));
        await session.AttachToAsync(Command());
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.AttachToAsync(Command()));
        await session.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
        await session.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task RejectedSecondAttachDoesNotCloseTheFirstSessionsPipes()
    {
        var time = new ManualTimeProvider();
        var clock = new SupervisionClock(time);
        await using var session = new FfmpegMediaSession(Av, true, clock);
        var start = Command();
        await session.AttachToAsync(start);
        var writers = await Connect(PipeNames(start));
        try
        {
            await Qualify(session, time, clock, writers);
            await Assert.ThrowsAsync<InvalidOperationException>(() => session.AttachToAsync(Command()));
            time.Advance(TimeSpan.FromMilliseconds(500));
            var error = await Record.ExceptionAsync(() => writers[0].WriteLineAsync("brmedia|pre|v|99|99|1/25|99"));
            Assert.Null(error);
            await FaultHarnessTests.Eventually(() => session.Snapshot(true).Signals
                .Single(signal => signal.Kind == MediaSignalKind.InputVideo).LastAdvancedAt == clock.Now);
        }
        finally { foreach (var writer in writers) { try { await writer.DisposeAsync(); } catch (IOException) { } } }
    }

    [Fact]
    public async Task DisposingInstrumentationCannotCreateAFailedMediaVerdict()
    {
        var time = new ManualTimeProvider();
        var clock = new SupervisionClock(time);
        await using var session = new FfmpegMediaSession(Av, true, clock);
        var start = Command();
        await session.AttachToAsync(start);
        var writers = await Connect(PipeNames(start));
        try
        {
            await Qualify(session, time, clock, writers);
            await session.DisposeAsync();
            time.Advance(TimeSpan.FromMinutes(1));
            Assert.All(session.Snapshot(true).Signals, signal => Assert.Equal(MediaSignalState.Unknown, signal.State));
            Assert.False(session.Snapshot(true).MayRecover);
        }
        finally { foreach (var writer in writers) await writer.DisposeAsync(); }
    }

    private static ProcessStartInfo Command()
    {
        var start = new ProcessStartInfo("synthetic.exe");
        foreach (var arg in new[] { "-af", "aresample=48000:async=1:first_pts=0", "-f", "null", "NUL" }) start.ArgumentList.Add(arg);
        return start;
    }

    private static string[] PipeNames(ProcessStartInfo start) => FfmpegAudioSessionIntegrationTests.Endpoints(start.ArgumentList)
        .Select(arg => arg[9..]).ToArray();

    private static async Task<StreamWriter[]> Connect(string[] names)
    {
        var writers = new List<StreamWriter>();
        try
        {
            foreach (var name in names)
            {
                var client = new NamedPipeClientStream(".", name, PipeDirection.Out, PipeOptions.Asynchronous);
                try { await client.ConnectAsync().WaitAsync(TimeSpan.FromSeconds(3)); }
                catch { client.Dispose(); throw; }
                writers.Add(new StreamWriter(client) { AutoFlush = true });
            }
            return writers.ToArray();
        }
        catch { foreach (var writer in writers) await writer.DisposeAsync(); throw; }
    }

    private static async Task Qualify(FfmpegMediaSession session, ManualTimeProvider time, SupervisionClock clock, StreamWriter[] writers)
    {
        for (var i = 0; i <= 4; i++)
        {
            if (i > 0) time.Advance(TimeSpan.FromMilliseconds(500));
            var at = clock.Now;
            var records = new[] { $"brmedia|pre|v|{i}|{i}|1/25|{i}",
                $"brmedia|mux|v|{i}", $"brmedia|mux|a|{i}",
                $"frame:{i} pts:{i * 1024} pts_time:0\nlavfi.astats.Overall.Number_of_samples={(i + 1) * 1024}.000000" };
            for (var index = 0; index < writers.Length; index++) await writers[index].WriteLineAsync(records[index]);
            await FaultHarnessTests.Eventually(() => session.Snapshot(true).Signals.All(signal => signal.LastAdvancedAt == at));
        }
        Assert.All(session.Snapshot(true).Signals, signal => Assert.Equal(MediaSignalState.Healthy, signal.State));
    }
}
