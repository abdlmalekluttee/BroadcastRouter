using System.Diagnostics;
using BroadcastRouter.Application;
using BroadcastRouter.Domain;

namespace BroadcastRouter.Infrastructure;

internal sealed class FfmpegMediaSession : IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly SupervisionClock _clock;
    private readonly MediaEvidenceTracker _evidence;
    private readonly MediaSignalKind[] _required;
    private readonly List<FfmpegMediaChannel> _channels = [];
    private bool _attached;
    private Task? _dispose;

    internal FfmpegMediaSession(MediaProperties? media, bool includeAudio, SupervisionClock clock)
    {
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        var video = media?.HasUsableVideo == true;
        var audio = includeAudio && !string.IsNullOrWhiteSpace(media?.AudioCodec);
        _required = Enum.GetValues<MediaSignalKind>().Where(kind =>
            kind is MediaSignalKind.InputVideo or MediaSignalKind.OutputVideo ? video : audio).ToArray();
        _evidence = new(_required, clock.Now);
    }

    internal async Task AttachToAsync(ProcessStartInfo start)
    {
        ArgumentNullException.ThrowIfNull(start);
        var creatingChannels = false;
        try
        {
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_dispose is not null, this);
                if (_attached) throw new InvalidOperationException("Media evidence belongs to one process session.");
                if (start.ArgumentList.Count == 0) throw new ArgumentException("An output destination is required.", nameof(start));
                _attached = true;
                creatingChannels = true;
                foreach (var kind in _required)
                {
                    var channel = new FfmpegMediaChannel(kind, observation =>
                    {
                        if (observation is { } sample) _evidence.Observe(kind, sample.Counter, _clock.Now);
                        else _evidence.Invalidate(kind, _clock.Now);
                    });
                    _channels.Add(channel);
                    var input = kind is MediaSignalKind.InputVideo or MediaSignalKind.InputAudio;
                    var stream = kind is MediaSignalKind.InputVideo or MediaSignalKind.OutputVideo ? "v" : "a";
                    var option = input ? "-stats_enc_pre" : "-stats_mux_pre";
                    var format = input ? $"brmedia|pre|{stream}|{{ni}}|{{ptsi}}|{{tbi}}|{{n}}"
                        : $"brmedia|mux|{stream}|{{n}}";
                    // One argv token per value, immediately before the existing
                    // output destination. Never put provenance on generated tracks.
                    foreach (var token in new[] { $"{option}:{stream}:0", channel.Endpoint,
                                 $"{option}_fmt:{stream}:0", format })
                        start.ArgumentList.Insert(start.ArgumentList.Count - 1, token);
                }
            }
        }
        catch
        {
            // A rejected second attachment must not tear down the first owner.
            if (creatingChannels) await DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    internal MediaEvidenceSnapshot Snapshot(bool processAlive) => _evidence.Snapshot(_clock.Now, processAlive);
    internal void ObserveHeartbeat() => _evidence.ObserveHeartbeat(_clock.Now);

    public ValueTask DisposeAsync()
    {
        lock (_gate) return new(_dispose ??= DisposeCoreAsync());
    }

    private async Task DisposeCoreAsync()
    {
        await Task.WhenAll(_channels.Select(channel => channel.DisposeAsync().AsTask())).ConfigureAwait(false);
        foreach (var kind in _required) _evidence.Invalidate(kind, _clock.Now);
    }
}
