using BroadcastRouter.Domain;

namespace BroadcastRouter.Infrastructure;

internal sealed class FfmpegMediaChannel : IAsyncDisposable
{
    internal FfmpegMediaChannel(MediaSignalKind kind, Action<FfmpegMediaObservation?> observe)
        => throw new NotSupportedException("Media channel implementation pending RED verification.");
    internal string PipeName => "not-implemented";
    internal string Endpoint => "not-implemented";
    internal Task Completion => Task.CompletedTask;
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
