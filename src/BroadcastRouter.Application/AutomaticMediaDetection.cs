using BroadcastRouter.Domain;

namespace BroadcastRouter.Application;

public sealed record MediaDetectionResult(StreamProbeResult Probe, DetectedMediaProfile Profile,
    TimeSpan ObservationDuration, bool WindowComplete)
{
    public bool Qualified => WindowComplete && Probe is { Opened: true, Media: not null, FailureCategory: null };
}

public sealed record MediaGrowthObservation(StreamProbeResult? Probe, TimeSpan Duration, DateTimeOffset CompletedAt);

public sealed class AutomaticMediaDetection(IStreamProbe probe, SupervisionClock clock)
{
    public static readonly TimeSpan DetectionWindow = TimeSpan.FromSeconds(5);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Task<StreamProbeResult>? _pending;

    public async Task<MediaDetectionResult> DetectAsync(Uri uri, SourceMediaOverride mode, CancellationToken token,
        IStreamProbe? requestProbe = null)
    {
        var start = clock.Now;
        if (!await _gate.WaitAsync(0, token).ConfigureAwait(false))
            return Unknown("Detection already in progress.");
        CancellationTokenSource? helper = null;
        using var timer = CancellationTokenSource.CreateLinkedTokenSource(token);
        try
        {
            if (_pending is { IsCompleted: false }) return Unknown("Previous probe cleanup is still pending. Standby retained.");
            helper = CancellationTokenSource.CreateLinkedTokenSource(token);
            var deadline = clock.DelayAsync(DetectionWindow, timer.Token);
            StreamProbeResult? latest = null;
            var seenVideo = false;
            var seenAudio = false;
            // Reopen the input: libavformat cannot reliably attach late tracks to
            // an already initialized input. No durations are added across opens.
            for (var slot = 0; slot < 4; slot++)
            {
                var due = start + TimeSpan.FromSeconds(slot * 1.25);
                if (clock.Now < due) await clock.DelayAsync(due - clock.Now, token).ConfigureAwait(false);
                if (clock.Now >= start + DetectionWindow) return Unknown("Detection deadline reached before all observations completed. Standby retained.");
                _pending = (requestProbe ?? probe).ProbeAsync(uri, helper.Token);
                if (await Task.WhenAny(_pending, deadline).ConfigureAwait(false) != _pending)
                {
                    token.ThrowIfCancellationRequested();
                    return Unknown("Detection deadline exceeded. Standby retained.");
                }
                latest = await _pending.ConfigureAwait(false);
                // A failed open is not a missing-track observation. Preserve the
                // classified transport failure for the existing bounded backoff;
                // do not turn one offline-source retry into four new connections.
                if (!latest.Opened && !seenVideo && !seenAudio)
                    return new(latest, DetectedMediaProfile.Unknown, clock.Now - start, false);
                seenVideo |= latest.FramesReceived && latest.Media?.HasUsableVideo == true;
                seenAudio |= latest.DecodedAudioReceived && latest.Media?.AudioCodec is not null;
            }
            await deadline.ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            var detected = Profile(seenVideo, seenAudio);
            if (latest is null || detected == DetectedMediaProfile.Unknown)
                return Unknown("No decoded input tracks observed. Standby retained.");
            var required = mode != SourceMediaOverride.Auto ? mode : detected switch {
                DetectedMediaProfile.AudioVideo => SourceMediaOverride.ForceAudioVideo,
                DetectedMediaProfile.VideoOnly => SourceMediaOverride.ForceVideoOnly,
                _ => SourceMediaOverride.ForceAudioOnly };
            // The final observation must qualify ALL tracks seen in this window;
            // a single earlier keyframe cannot be discarded to admit audio-only.
            var qualified = SourceMediaPolicy.Apply(latest, new() { Override = required });
            if (qualified.Media is not null) qualified = qualified with {
                Media = qualified.Media with { DetectedProfile = detected, SelectedOverride = mode } };
            return new(qualified, detected, clock.Now - start, true);
        }
        finally
        {
            timer.Cancel();
            if (helper is not null)
            {
                helper.Cancel();
                if (_pending is { IsCompleted: false } cleanup) _ = FinishCleanupAsync(cleanup, helper);
                else helper.Dispose();
            }
            _gate.Release();
        }

        MediaDetectionResult Unknown(string reason) => new(new(false, false, null, "Unknown", reason),
            DetectedMediaProfile.Unknown, clock.Now - start, false);
    }

    public static DetectedMediaProfile Profile(bool video, bool audio) => (video, audio) switch {
        (true, true) => DetectedMediaProfile.AudioVideo, (true, false) => DetectedMediaProfile.VideoOnly,
        (false, true) => DetectedMediaProfile.AudioOnly, _ => DetectedMediaProfile.Unknown };

    public async Task<MediaGrowthObservation> ConfirmGrowthAsync(Uri uri, IStreamProbe growthProbe, CancellationToken token)
    {
        var started = clock.Now;
        if (!await _gate.WaitAsync(0, token).ConfigureAwait(false)) return new(null, TimeSpan.Zero, clock.Now);
        CancellationTokenSource? helper = null;
        using var timer = CancellationTokenSource.CreateLinkedTokenSource(token);
        try
        {
            if (_pending is { IsCompleted: false }) return new(null, TimeSpan.Zero, clock.Now);
            helper = CancellationTokenSource.CreateLinkedTokenSource(token);
            _pending = growthProbe.ProbeAsync(uri, helper.Token);
            var deadline = clock.DelayAsync(TimeSpan.FromSeconds(12), timer.Token);
            if (await Task.WhenAny(_pending, deadline).ConfigureAwait(false) != _pending)
            {
                token.ThrowIfCancellationRequested();
                return new(null, clock.Now - started, clock.Now);
            }
            return new(await _pending.ConfigureAwait(false), clock.Now - started, clock.Now);
        }
        catch (Exception) when (!token.IsCancellationRequested) { return new(null, clock.Now - started, clock.Now); }
        finally
        {
            timer.Cancel();
            if (helper is not null)
            {
                helper.Cancel();
                if (_pending is { IsCompleted: false } cleanup) _ = FinishCleanupAsync(cleanup, helper);
                else helper.Dispose();
            }
            _gate.Release();
        }
    }

    private static async Task FinishCleanupAsync(Task<StreamProbeResult> pending, CancellationTokenSource lifetime)
    {
        try { await pending.ConfigureAwait(false); }
        catch { /* Cleanup failure is not admission evidence. */ }
        finally { lifetime.Dispose(); }
    }
}
