using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Text;
using BroadcastRouter.Domain;
using BroadcastRouter.Infrastructure;
using Xunit;

namespace BroadcastRouter.Tests;

public sealed class FfmpegMediaChannelTests
{
    [Fact]
    public async Task IndependentPipesNeverMixAudioVideoAndSubmissionRecords()
    {
        var received = new ConcurrentBag<FfmpegMediaObservation>();
        var kinds = Enum.GetValues<MediaSignalKind>();
        var lines = new[] { "brmedia|pre|v|7|7|1/25|14", "brmedia|pre|a|9|9216|1/48000|9",
            "brmedia|mux|v|14", "brmedia|mux|a|9" };
        var channels = kinds.Select(kind => new FfmpegMediaChannel(kind, item =>
        { if (item is { } observation) received.Add(observation); })).ToArray();
        try
        {
            Assert.Equal(4, channels.Select(channel => channel.PipeName).Distinct().Count());
            await Task.WhenAll(channels.Select((channel, index) => Send(channel, lines[index] + "\n")));
            await Task.WhenAll(channels.Select(channel => channel.Completion)).WaitAsync(TimeSpan.FromSeconds(3));
            Assert.Equal(4, received.Count);
            Assert.Equal(new long[] { 7, 9, 14, 9 }, kinds.Select(kind => Assert.Single(received, item => item.Kind == kind).Counter));
        }
        finally { foreach (var channel in channels) await channel.DisposeAsync(); }
    }

    [Fact]
    public async Task OversizedAndMalformedRecordsInvalidateWithoutPoisoningNextRecord()
    {
        var received = new ConcurrentQueue<FfmpegMediaObservation?>();
        await using var channel = new FfmpegMediaChannel(MediaSignalKind.OutputAudio, received.Enqueue);
        await Send(channel, new string('x', 100_000) + "\nwrong\nbrmedia|mux|a|47\n");
        await channel.Completion.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(47, Assert.Single(received.OfType<FfmpegMediaObservation>()).Counter);
        // Oversize, wrong schema and transport EOF are Unknown, not invented media loss.
        Assert.Equal(3, received.Count(item => item is null));
    }

    [Fact]
    public async Task UnterminatedTailNeverBecomesAValidMediaObservation()
    {
        var received = new ConcurrentQueue<FfmpegMediaObservation?>();
        await using var channel = new FfmpegMediaChannel(MediaSignalKind.OutputVideo, received.Enqueue);
        await Send(channel, "brmedia|mux|v|99");
        await channel.Completion.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.DoesNotContain(received, item => item is not null);
        Assert.Contains(received, item => item is null);
    }

    [Fact]
    public async Task DisposeUnconnectedAndConnectedReadersIsBoundedAndIdempotent()
    {
        var missing = new FfmpegMediaChannel(MediaSignalKind.InputAudio, _ => { });
        await missing.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
        await missing.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
        var waiting = new FfmpegMediaChannel(MediaSignalKind.InputVideo, _ => { });
        await using var client = new NamedPipeClientStream(".", waiting.PipeName, PipeDirection.Out, PipeOptions.Asynchronous);
        await client.ConnectAsync().WaitAsync(TimeSpan.FromSeconds(3));
        await waiting.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(waiting.Completion.IsCompletedSuccessfully);
    }

    private static async Task Send(FfmpegMediaChannel channel, string records)
    {
        await using var client = new NamedPipeClientStream(".", channel.PipeName, PipeDirection.Out, PipeOptions.Asynchronous);
        await client.ConnectAsync().WaitAsync(TimeSpan.FromSeconds(3));
        var data = Encoding.UTF8.GetBytes(records);
        // Split records across reads; a write boundary is not a record boundary.
        for (var start = 0; start < data.Length; start += 37)
            await client.WriteAsync(data.AsMemory(start, Math.Min(37, data.Length - start)));
        await client.FlushAsync();
    }
}
