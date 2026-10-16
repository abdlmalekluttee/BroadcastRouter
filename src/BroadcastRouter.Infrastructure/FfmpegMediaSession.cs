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
    private readonly bool _sparseVideo;
    private readonly List<IAsyncDisposable> _channels = [];
    private bool _attached;
    private Task? _dispose;
    // Internal observation boundary for deterministic integration fixtures. Fired
    // after ingestion, never as a substitute for media evidence or health policy.
    internal event Action<MediaSignalKind, long>? Observed;

    internal FfmpegMediaSession(MediaProperties? media, bool includeAudio, SupervisionClock clock)
    {
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _sparseVideo = media?.Expectation == SourceMediaExpectation.SparseVideo;
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
                    if (kind == MediaSignalKind.InputAudio)
                    {
                        AttachAudioSamples(start);
                        continue;
                    }
                    var channel = new FfmpegMediaChannel(kind, observation =>
                    {
                        if (observation is { } sample) Observe(kind, sample.Counter);
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

    internal MediaEvidenceSnapshot Snapshot(bool processAlive)
    {
        var observed = _evidence.Snapshot(_clock.Now, processAlive);
        if (!_sparseVideo) return observed;
        // Sparse input has no continuous cadence contract. Keep its last
        // advancement visible, but do not turn an intentional gap into a vote.
        var signals = observed.Signals.Select(s => s.Kind == MediaSignalKind.InputVideo && s.State != MediaSignalState.Healthy
            ? s with { State = MediaSignalState.Unknown } : s).ToArray();
        var failed = signals.Count(s => s.State == MediaSignalState.Failed) >= 2;
        return observed with { Signals = signals, ConfirmedMediaFailure = failed, MayRecover = !processAlive || failed };
    }
    internal void ObserveHeartbeat() => _evidence.ObserveHeartbeat(_clock.Now);

    private void Observe(MediaSignalKind kind, long counter)
    {
        _evidence.Observe(kind, counter, _clock.Now);
        Observed?.Invoke(kind, counter);
    }

    private void AttachAudioSamples(ProcessStartInfo start)
    {
        var filterIndex = start.ArgumentList.IndexOf("-af");
        if (filterIndex < 0 || filterIndex + 1 >= start.ArgumentList.Count)
            throw new ArgumentException("A mapped source audio filter is required for PCM observation.", nameof(start));
        var channel = new FfmpegAudioSampleChannel(observation =>
        {
            if (observation is { } sample) Observe(MediaSignalKind.InputAudio, sample.Samples);
            else _evidence.Invalidate(MediaSignalKind.InputAudio, _clock.Now);
        });
        _channels.Add(channel);
        // Observe decoded input before async resampling can insert silence or
        // change sample count. This is a private generated pipe name, never a
        // source URL/path. Quoting is FFmpeg filter syntax inside one argv token.
        var endpoint = channel.Endpoint.Replace("\\", "\\\\", StringComparison.Ordinal);
        start.ArgumentList[filterIndex + 1] =
            "astats=metadata=1:measure_perchannel=none:measure_overall=Number_of_samples," +
            $"ametadata=mode=print:key=lavfi.astats.Overall.Number_of_samples:file='{endpoint}':direct=1," +
            start.ArgumentList[filterIndex + 1];
    }

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
