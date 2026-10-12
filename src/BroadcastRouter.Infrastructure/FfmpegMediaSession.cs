using System.Diagnostics;
using BroadcastRouter.Application;
using BroadcastRouter.Domain;

namespace BroadcastRouter.Infrastructure;

internal sealed class FfmpegMediaSession : IAsyncDisposable
{
    internal FfmpegMediaSession(MediaProperties? media, bool includeAudio, SupervisionClock clock) { }
    internal Task AttachToAsync(ProcessStartInfo start) => Task.CompletedTask;
    internal MediaEvidenceSnapshot Snapshot(bool processAlive) => new(
        processAlive ? MediaSignalState.Healthy : MediaSignalState.Failed,
        MediaSignalState.Unknown, [], false, !processAlive);
    internal void ObserveHeartbeat() { }
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
