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

    [Fact]
    public async Task HealthyPcmWarningBurstRetainsOwnerWithoutInventingFailure()
    {
        await using var lab = await Lab.Create();
        await lab.Qualify();
        var session = lab.Owner.SessionId;
        foreach (var warning in new[] { "[aac @ 0x1] channel element 1.0 is not allocated",
                     "[aac @ 0x1] Error submitting packet to decoder", "No buffered audio" })
            await lab.Warning(warning);
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

    private sealed class Lab : IAsyncDisposable
    {
        private readonly SupervisorMediaEvidenceTests.MediaChildFixture _fixture = new();
        internal ManualTimeProvider Time { get; } = new();
        private readonly SupervisionClock _clock;
        private readonly FfmpegProcessSupervisor _supervisor;
        private readonly Dictionary<string, StreamWriter> _writers = new();
        private Process _process = null!;
        private int _sequence;
        internal RouteProcessSnapshot Owner => Assert.Single(_supervisor.RunningSnapshot());
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
                lab._process = (Process)managed.GetType().GetProperty("Process")!.GetValue(managed)!;
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
            await FaultHarnessTests.Eventually(() => _supervisor.Snapshot().Single().RecentErrors.Contains(barrier));
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
            foreach (var writer in _writers.Values) await writer.DisposeAsync();
            await _supervisor.DisposeAsync();
            await _fixture.DisposeAsync();
        }
    }
}
