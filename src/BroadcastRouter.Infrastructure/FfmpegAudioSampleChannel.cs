using BroadcastRouter.Domain;

namespace BroadcastRouter.Infrastructure;

internal sealed class FfmpegAudioSampleChannel : IAsyncDisposable
{
    private readonly FfmpegMediaChannel _legacy;
    internal FfmpegAudioSampleChannel(Action<FfmpegAudioSample?> observe) =>
        _legacy = new(MediaSignalKind.InputAudio, value => observe(null));
    internal string PipeName => _legacy.PipeName;
    internal string Endpoint => _legacy.Endpoint;
    internal Task Completion => _legacy.Completion;
    public ValueTask DisposeAsync() => _legacy.DisposeAsync();
}
