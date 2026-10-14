using System.Diagnostics;
using System.IO.Pipes;
using System.Reflection;
using System.Text.Json;
using BroadcastRouter.Application;
using BroadcastRouter.Domain;
using BroadcastRouter.Infrastructure;
using Xunit;

namespace BroadcastRouter.Tests;

public sealed class SupervisorAudioRecoveryTests
{
    [Theory]
    [InlineData("[aac @ 0x1] Error submitting packet to decoder", "AudioDecoderCorrupt")]
    [InlineData("[aist#0:0/aac] PTS 999, next:1 invalid dropping", "AudioTimestampDiscontinuous")]
    [InlineData("No buffered audio", "DeckLinkAudioStarved")]
    public async Task ConfirmedPcmFaultClearsOnlyAfterQualifiedSampleRecovery(string warning, string category)
    {
        await using var lab = await Lab.Create();
        await lab.Qualify();
        lab.Time.Advance(TimeSpan.FromSeconds(23));
        Assert.True(lab.Owner.IndependentMedia!.ConfirmedMediaFailure);
        await lab.Warning(warning);
        Assert.Equal(category, lab.Owner.InputFailure?.Category);
        for (var step = 0; step <= 4; step++)
        {
            if (step > 0) lab.Time.Advance(TimeSpan.FromMilliseconds(500));
            await lab.Media();
            if (step < 4) Assert.NotNull(lab.Owner.InputFailure);
        }
        Assert.Null(lab.Owner.InputFailure);
        Assert.True(lab.Owner.Running);
        Assert.False(lab.Owner.IndependentMedia!.ConfirmedMediaFailure);
    }

    [Theory]
    [InlineData(1, "[aac @ 0x1] Error submitting packet to decoder")]
    [InlineData(2, "[aac @ 0x1] Error submitting packet to decoder")]
    [InlineData(32, "[aac @ 0x1] Error submitting packet to decoder")]
    [InlineData(1, "[aist#0:0/aac] PTS 999, next:1 invalid dropping")]
    [InlineData(2, "[aist#0:0/aac] PTS 999, next:1 invalid dropping")]
    [InlineData(32, "[aist#0:0/aac] PTS 999, next:1 invalid dropping")]
    [InlineData(1, "No buffered audio")]
    [InlineData(2, "No buffered audio")]
    [InlineData(32, "No buffered audio")]
    public async Task HealthyPcmWarningBurstRetainsOwnerWithoutInventingFailure(int burstSize, string warning)
    {
        await using var lab = await Lab.Create();
        // Classifiers ignore the first five seconds. Requalify after that grace
        // with current PCM, then inspect raw classification before any status
        // snapshot can clear a wrongly classified Healthy-audio failure.
        lab.Time.Advance(TimeSpan.FromSeconds(6));
        await lab.Qualify();
        var session = lab.Owner.SessionId;
        for (var i = 0; i < burstSize; i++)
        {
            await lab.Warning("Synthetic unrelated diagnostic");
            await lab.Warning(warning);
            Assert.Null(lab.ClassifiedFailure);
        }
        Assert.Null(lab.Owner.InputFailure);
        Assert.Equal(session, lab.Owner.SessionId);
        Assert.True(lab.Owner.Running);
        Assert.All(lab.Owner.IndependentMedia!.Signals, signal => Assert.Equal(MediaSignalState.Healthy, signal.State));
    }

    [Fact]
    public async Task LaterProtocolObservationReplacesFirstFailureAndQualifiedRecoveryClearsIt()
    {
        await using var lab = await Lab.Create();
        await lab.Qualify();
        lab.Time.Advance(TimeSpan.FromSeconds(23));
        await lab.Warning("[in#0/rtsp] CSeq 96 expected, 0 received.");
        Assert.Contains("CSeq 96", lab.Owner.InputFailure!.Detail);
        await lab.Warning("[in#0/rtsp] CSeq 97 expected, 0 received.");
        Assert.Contains("CSeq 97", lab.Owner.InputFailure!.Detail);
        await lab.Qualify();
        Assert.Null(lab.Owner.InputFailure);
    }

    [Fact]
    public async Task ProtocolFailureClearsWithoutAnotherDiagnosticAfterQualifiedRecovery()
    {
        await using var lab = await Lab.Create();
        await lab.Qualify();
        lab.Time.Advance(TimeSpan.FromSeconds(23));
        await lab.Warning("[in#0/rtsp] CSeq 96 expected, 0 received.");
        Assert.NotNull(lab.Owner.InputFailure);
        await lab.Qualify();
        Assert.Null(lab.Owner.InputFailure);
    }

    [Fact]
    public async Task StaleHealthySnapshotCannotClearANewerDiagnostic()
    {
        await using var lab = await Lab.Create();
        await lab.Qualify();
        var measured = lab.Owner.IndependentMedia!;
        lab.Time.Advance(TimeSpan.FromSeconds(23));
        await lab.Warning("[in#0/rtsp] CSeq 96 expected, 0 received.");
        var oldFailure = lab.Owner.InputFailure!;
        await lab.Warning("[in#0/rtsp] CSeq 97 expected, 0 received.");
        // Deterministic interleaving: snapshot read old failure and Healthy
        // media, stderr then published a newer failure before the clear.
        var retained = lab.RefreshObservedFailure(oldFailure, measured);
        Assert.Contains("CSeq 97", retained!.Detail);
        Assert.Contains("CSeq 97", lab.Owner.InputFailure!.Detail);
    }

    [Fact]
    public async Task LostInstrumentationDoesNotPretendClassifiedFailureRecovered()
    {
        await using var lab = await Lab.Create();
        await lab.Qualify();
        lab.Time.Advance(TimeSpan.FromSeconds(23));
        await lab.Warning("[in#0/rtsp] CSeq 96 expected, 0 received.");
        await lab.CloseMeasurements();
        await FaultHarnessTests.Eventually(() => lab.Owner.IndependentMedia!.Signals.All(signal => signal.State == MediaSignalState.Unknown));
        Assert.NotNull(lab.Owner.InputFailure);
        Assert.False(lab.Owner.IndependentMedia!.ConfirmedMediaFailure);
    }

    // Shared real-child diagnostic fixture; no test hook in production code.
    internal sealed class Lab : IAsyncDisposable
    {
        private readonly SupervisorMediaEvidenceTests.MediaChildFixture _fixture = new();
        internal ManualTimeProvider Time { get; } = new();
        private readonly SupervisionClock _clock;
        private readonly FfmpegProcessSupervisor _supervisor;
        private readonly Dictionary<string, StreamWriter> _writers = new();
        private OwnedChildProcess _process = null!;
        private object _managed = null!;
        private int _sequence;
        internal RouteProcessSnapshot Owner => Assert.Single(_supervisor.RunningSnapshot());
        internal FfmpegInputFailure? ClassifiedFailure =>
            (FfmpegInputFailure?)_managed.GetType().GetProperty("InputFailure")!.GetValue(_managed);
        private Lab()
        {
            _clock = new(Time);
            _supervisor = new(new(_fixture.Executable, UseIndependentMediaStats: true), TimeSpan.FromSeconds(1), _clock);
        }
        internal static async Task<Lab> Create()
        {
            var lab = new Lab();
            try
            {
                await File.WriteAllTextAsync(Path.Combine(lab._fixture.Root, "live.json"),
                    JsonSerializer.Serialize(new { events = Array.Empty<object>(), captureArguments = true, progressCommands = true }));
                await lab._fixture.Start(lab._supervisor);
                string[]? arguments = null;
                await FaultHarnessTests.Eventually(() =>
                {
                    try { arguments = JsonSerializer.Deserialize<string[]>(File.ReadAllText(Path.Combine(lab._fixture.Root, "arguments.json"))); return arguments is not null; }
                    catch (Exception ex) when (ex is IOException or JsonException) { return false; }
                });
                var args = arguments!;
                foreach (var key in new[] { "-stats_enc_pre:v:0", "-stats_mux_pre:v:0", "-stats_mux_pre:a:0", "pcm" })
                {
                    var endpoint = key == "pcm" ? FfmpegAudioSessionIntegrationTests.Endpoints(args).Last()
                        : args[Array.IndexOf(args, key) + 1];
                    var pipe = new NamedPipeClientStream(".", endpoint[9..], PipeDirection.Out, PipeOptions.Asynchronous);
                    try { await pipe.ConnectAsync().WaitAsync(TimeSpan.FromSeconds(3)); }
                    catch { pipe.Dispose(); throw; }
                    lab._writers.Add(key, new(pipe) { AutoFlush = true });
                }
                var owners = typeof(FfmpegProcessSupervisor).GetField("_running", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(lab._supervisor)!;
                var managed = owners.GetType().GetProperty("Item")!.GetValue(owners, [lab._fixture.Source.Identity.Value])!;
                lab._managed = managed;
                lab._process = (OwnedChildProcess)managed.GetType().GetProperty("Process")!.GetValue(managed)!;
                return lab;
            }
            catch { await lab.DisposeAsync(); throw; }
        }
        internal async Task Warning(string line)
        {
            var barrier = "Synthetic diagnostic barrier " + Guid.NewGuid().ToString("N");
            await _process.StandardInput.WriteLineAsync("stderr " + line);
            await _process.StandardInput.WriteLineAsync("stderr " + barrier);
            await _process.StandardInput.FlushAsync();
            // One stderr reader consumes in order. Seeing the next record means
            // classification of the previous warning has completed.
            // Reading Errors has no recovery side effect. Snapshot() does, and
            // would mask the exact spurious-classification regression we test.
            var errors = (IEnumerable<string>)_managed.GetType().GetProperty("Errors")!.GetValue(_managed)!;
            await FaultHarnessTests.Eventually(() => errors.Contains(barrier));
        }
        internal FfmpegInputFailure? RefreshObservedFailure(FfmpegInputFailure failure, MediaEvidenceSnapshot media) =>
            (FfmpegInputFailure?)_managed.GetType().GetMethod("RefreshInputFailure")!.Invoke(_managed, [failure, media]);
        internal async Task CloseMeasurements()
        {
            foreach (var writer in _writers.Values) await writer.DisposeAsync();
            _writers.Clear();
        }
        internal async Task Qualify()
        {
            for (var i = 0; i <= 4; i++)
            {
                if (i > 0) Time.Advance(TimeSpan.FromMilliseconds(500));
                await Media();
            }
        }
        internal async Task Media()
        {
            var i = ++_sequence;
            await _writers["-stats_enc_pre:v:0"].WriteLineAsync($"brmedia|pre|v|{i}|{i}|1/25|{i}");
            await _writers["-stats_mux_pre:v:0"].WriteLineAsync($"brmedia|mux|v|{i}");
            await _writers["-stats_mux_pre:a:0"].WriteLineAsync($"brmedia|mux|a|{i}");
            await _writers["pcm"].WriteLineAsync($"frame:{i} pts:{i*1024} pts_time:0\nlavfi.astats.Overall.Number_of_samples={(i+1)*1024}.000000");
            await FaultHarnessTests.Eventually(() => Owner.IndependentMedia!.Signals.All(signal => signal.LastAdvancedAt == _clock.Now));
        }
        public async ValueTask DisposeAsync()
        {
            await CloseMeasurements();
            await _supervisor.DisposeAsync();
            await _fixture.DisposeAsync();
        }
    }
}
