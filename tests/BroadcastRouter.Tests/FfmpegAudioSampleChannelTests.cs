using System.Collections.Concurrent;
using System.IO.Pipes;
using BroadcastRouter.Infrastructure;
using Xunit;

namespace BroadcastRouter.Tests;

public sealed class FfmpegAudioSampleChannelTests
{
    [Fact]
    public async Task EmptyGraphInspectionOpenDoesNotConsumePlaybackOrReinitializationListener()
    {
        var received = new ConcurrentQueue<FfmpegAudioSample?>();
        await using var channel = new FfmpegAudioSampleChannel(received.Enqueue);
        for (var session = 0; session < 4; session++)
        {
            // FFmpeg opens/closes the metadata URL while inspecting the graph.
            await using (var inspection = await Connect(channel)) { }
            StreamWriter? writer = null;
            var openError = await Record.ExceptionAsync(async () => writer = await Connect(channel));
            Assert.Null(openError);
            await using (writer!)
            {
                await writer!.WriteLineAsync($"frame:0 pts:0 pts_time:0\nlavfi.astats.Overall.Number_of_samples={(session+1)*1024}.000000");
                await FaultHarnessTests.Eventually(() => received.Any(item => item?.Samples == (session+1)*1024));
            }
        }
        Assert.Equal(new long[] {1024,2048,3072,4096}, received.OfType<FfmpegAudioSample>().Select(item => item.Samples));
    }

    [Fact]
    public async Task OversizedOrUnterminatedRecordsCannotCreateSamplesAndNextPairCanRecover()
    {
        var received = new ConcurrentQueue<FfmpegAudioSample?>();
        await using var channel = new FfmpegAudioSampleChannel(received.Enqueue);
        await using (var writer = await Connect(channel))
        {
            await writer.WriteLineAsync(new string('x',100_000));
            await writer.WriteLineAsync("frame:0 pts:0 pts_time:0\nlavfi.astats.Overall.Number_of_samples=1024.000000");
            var sampleError = await Record.ExceptionAsync(() => FaultHarnessTests.Eventually(() => received.Any(item => item?.Samples == 1024)));
            Assert.Null(sampleError);
            await writer.WriteAsync("frame:1 pts:1024 pts_time:0.0213333\nlavfi.astats.Overall.Number_of_samples=2048.000000");
        }
        await FaultHarnessTests.Eventually(() => received.TryPeek(out _) && received.Last() is null);
        Assert.Equal(1024, Assert.Single(received.OfType<FfmpegAudioSample>()).Samples);
    }

    [Fact]
    public async Task OverlappingWritersAreUnknownUntilOnlyOneRemainsAndProducesAnotherSample()
    {
        var received = new ConcurrentQueue<FfmpegAudioSample?>();
        await using var channel = new FfmpegAudioSampleChannel(received.Enqueue);
        await using var first = await Connect(channel);
        StreamWriter? secondWriter = null;
        var openError = await Record.ExceptionAsync(async () => secondWriter = await Connect(channel));
        Assert.Null(openError);
        await using var second = secondWriter!;
        try
        {
            await first.WriteLineAsync("frame:0 pts:0 pts_time:0\nlavfi.astats.Overall.Number_of_samples=1024.000000");
            await FaultHarnessTests.Eventually(() => received.Any(item => item?.Samples == 1024));
            await second.WriteLineAsync("frame:0 pts:0 pts_time:0\nlavfi.astats.Overall.Number_of_samples=2048.000000");
            await FaultHarnessTests.Eventually(() => received.Last() is null);
            Assert.DoesNotContain(received, item => item?.Samples == 2048);
        }
        finally { await first.DisposeAsync(); }
        // Allow EOF cleanup; observe it via the ability to accept the next graph
        // inspection connection rather than assuming a sleep duration.
        await using (var inspection = await Connect(channel)) { }
        await second.WriteLineAsync("frame:1 pts:1024 pts_time:0.0213333\nlavfi.astats.Overall.Number_of_samples=3072.000000");
        await FaultHarnessTests.Eventually(() => received.Any(item => item?.Samples == 3072));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DisposalClosesEveryListenerWithinDeadline(bool connected)
    {
        var channel = new FfmpegAudioSampleChannel(_ => {});
        await using var writer = connected ? await Connect(channel) : null;
        await channel.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
        await channel.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(channel.Completion.IsCompletedSuccessfully);
        using var client = new NamedPipeClientStream(".", channel.PipeName, PipeDirection.Out, PipeOptions.Asynchronous);
        using var deadline = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.ConnectAsync(deadline.Token));
    }

    private static async Task<StreamWriter> Connect(FfmpegAudioSampleChannel channel)
    {
        var pipe = new NamedPipeClientStream(".",channel.PipeName,PipeDirection.Out,PipeOptions.Asynchronous);
        try
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await pipe.ConnectAsync(deadline.Token);
            return new StreamWriter(pipe) { AutoFlush=true };
        }
        catch { pipe.Dispose(); throw; }
    }
}
