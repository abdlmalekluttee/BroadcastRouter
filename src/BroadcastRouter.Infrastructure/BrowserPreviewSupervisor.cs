using System.Collections.Concurrent;
using System.Diagnostics;
using BroadcastRouter.Domain;

namespace BroadcastRouter.Infrastructure;

public enum PreviewState { Stopped, Starting, Running, Failed }

public sealed record PreviewSnapshot(
    PreviewState State,
    string? SourceId,
    string? SourceName,
    string VideoSummary,
    string AudioSummary,
    bool AudioMeterEnabled,
    DateTimeOffset? StartedAt,
    int? ProducerProcessId,
    string? StreamToken,
    string PlaybackStatistics,
    string? ErrorMessage)
{
    public static PreviewSnapshot Stopped { get; } = new(
        PreviewState.Stopped, null, null, "No source selected", "No preview audio", false,
        null, null, null, "Waiting for an operator to start preview.", null);
}

public sealed record BrowserPreviewCommandPlan(ProcessStartInfo Producer);

public static class BrowserPreviewCommandBuilder
{
    public const int CanvasWidth = 720;
    public const int CanvasHeight = 450;
    public const int VideoHeight = 404;

    public static BrowserPreviewCommandPlan Build(MediaToolPaths tools, DiscoveredSource source)
    {
        if (string.IsNullOrWhiteSpace(tools.FfmpegPath))
            throw new InvalidOperationException("FFmpeg is not configured.");

        var hasAudio = !string.IsNullOrWhiteSpace(source.Media?.AudioCodec);
        var producer = new ProcessStartInfo
        {
            FileName = tools.FfmpegPath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = false
        };

        Add(producer,
            "-hide_banner", "-loglevel", "warning", "-nostdin",
            "-rtsp_transport", "tcp", "-fflags", "nobuffer", "-flags", "low_delay",
            "-analyzeduration", "1000000", "-probesize", "1000000",
            "-i", source.RtspUri.AbsoluteUri);

        var audioLed = hasAudio && source.Media?.HasUsableVideo == false;
        if (audioLed)
            Add(producer, "-re", "-f", "lavfi", "-i", "color=c=0x060b12:size=720x404:rate=25");

        var videoInput = audioLed ? "[1:v:0]" : "[0:v:0]";
        var filter = hasAudio
            ? $"{videoInput}scale=720:404:force_original_aspect_ratio=decrease,pad=720:404:(ow-iw)/2:(oh-ih)/2:color=0x060b12,pad=720:450:0:0:color=0x060b12[canvas];" +
              "[0:a:0]asplit=2[previewaudio][meter];" +
              "[meter]showvolume=w=700:h=36:r=25:b=2:f=0.25:t=1:v=1:dm=1:dmc=orange:o=h:p=0.25:m=p:ds=log[vu];" +
              "[canvas][vu]overlay=10:414:shortest=1[outv];[previewaudio]anull[outa]"
            : "[0:v:0]scale=720:404:force_original_aspect_ratio=decrease,pad=720:404:(ow-iw)/2:(oh-ih)/2:color=0x060b12,pad=720:450:0:0:color=0x060b12[outv]";

        Add(producer, "-filter_complex", filter, "-map", "[outv]");
        if (hasAudio)
            Add(producer, "-map", "[outa]", "-c:a", "aac", "-b:a", "128k", "-ar", "48000", "-ac", "2");
        else
            producer.ArgumentList.Add("-an");
        if (audioLed) producer.ArgumentList.Add("-shortest");

        Add(producer,
            "-c:v", "libx264", "-preset", "veryfast", "-tune", "zerolatency",
            "-profile:v", "main", "-level", "3.1", "-pix_fmt", "yuv420p",
            "-g", "25", "-keyint_min", "25", "-sc_threshold", "0",
            "-movflags", "frag_keyframe+empty_moov+default_base_moof",
            "-flush_packets", "1", "-progress", "pipe:2", "-nostats",
            "-f", "mp4", "pipe:1");

        return new(producer);
    }

    private static void Add(ProcessStartInfo start, params string[] arguments)
    {
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
    }
}

public sealed class BrowserPreviewSupervisor : IAsyncDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _snapshotLock = new();
    private PreviewSession? _current;
    private PreviewSnapshot _snapshot = PreviewSnapshot.Stopped;

    public event Action? Changed;

    public PreviewSnapshot Snapshot
    {
        get { lock (_snapshotLock) return _snapshot; }
    }

    public async Task StartAsync(DiscoveredSource source, MediaToolPaths tools, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        PreviewSession? candidate = null;
        try
        {
            await StopCurrentCoreAsync().ConfigureAwait(false);
            if (!File.Exists(tools.FfmpegPath))
                throw new FileNotFoundException("Configured FFmpeg executable was not found.");

            var plan = BrowserPreviewCommandBuilder.Build(tools, source);
            candidate = new PreviewSession(source, new Process { StartInfo = plan.Producer });
            SetSnapshot(CreateSnapshot(candidate, PreviewState.Starting, "Preparing the embedded browser stream...", null));

            if (!candidate.Producer.Start())
                throw new InvalidOperationException("FFmpeg preview producer did not start.");

            _current = candidate;
            candidate.ProducerErrors = PumpErrorsAsync(candidate, candidate.Producer.StandardError);
            SetSnapshot(CreateSnapshot(candidate, PreviewState.Running, "Connecting the browser player...", null));
            _ = ObserveAsync(candidate);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (candidate is not null) await StopSessionAsync(candidate).ConfigureAwait(false);
            SetSnapshot(PreviewSnapshot.Stopped);
            throw;
        }
        catch (Exception exception)
        {
            if (candidate is not null) await StopSessionAsync(candidate).ConfigureAwait(false);
            var safe = LogRedactor.Redact(exception.Message);
            SetSnapshot(new(PreviewState.Failed, source.Identity.Value, source.FriendlyName,
                VideoSummary(source), AudioSummary(source), HasAudio(source), null, null, null,
                "Preview failed to start.", safe));
            throw new InvalidOperationException($"Preview could not start: {safe}", exception);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task CopyStreamToAsync(string token, Stream destination, CancellationToken cancellationToken)
    {
        PreviewSession session;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            session = _current is { } current && current.StreamToken.Equals(token, StringComparison.Ordinal)
                ? current
                : throw new InvalidOperationException("The preview stream is no longer available.");
            if (Interlocked.CompareExchange(ref session.StreamClaimed, 1, 0) != 0)
                throw new InvalidOperationException("The preview stream is already open in another browser player.");
        }
        finally
        {
            _gate.Release();
        }

        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, session.Cancellation.Token);
        try
        {
            await session.Producer.StandardOutput.BaseStream
                .CopyToAsync(destination, 64 * 1024, linkedCancellation.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (linkedCancellation.IsCancellationRequested) { }
        catch (IOException) { }
        catch (ObjectDisposedException) { }
        finally
        {
            Interlocked.Exchange(ref session.StreamClaimed, 0);
            await CompleteStreamSessionAsync(session).ConfigureAwait(false);
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { await StopCurrentCoreAsync().ConfigureAwait(false); }
        finally { _gate.Release(); }
    }

    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try { await StopCurrentCoreAsync().ConfigureAwait(false); }
        finally { _gate.Release(); _gate.Dispose(); }
    }

    private async Task ObserveAsync(PreviewSession session)
    {
        try
        {
            await session.Producer.WaitForExitAsync().ConfigureAwait(false);
            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                if (!ReferenceEquals(_current, session)) return;
                _current = null;
                var success = HasExitedSuccessfully(session.Producer);
                await StopSessionAsync(session).ConfigureAwait(false);
                SetSnapshot(CreateSnapshot(session, success ? PreviewState.Stopped : PreviewState.Failed,
                    success ? "Browser preview ended." : "Browser preview ended unexpectedly.",
                    success ? null : session.LastError));
            }
            finally { _gate.Release(); }
        }
        catch (Exception exception)
        {
            session.AddError(LogRedactor.Redact(exception.Message));
        }
    }

    private async Task CompleteStreamSessionAsync(PreviewSession session)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!ReferenceEquals(_current, session)) return;
            _current = null;
            var exited = HasExited(session.Producer);
            var success = !exited || HasExitedSuccessfully(session.Producer);
            await StopSessionAsync(session).ConfigureAwait(false);
            SetSnapshot(CreateSnapshot(session, success ? PreviewState.Stopped : PreviewState.Failed,
                success ? "Browser preview closed." : "Browser preview ended unexpectedly.",
                success ? null : session.LastError));
        }
        finally { _gate.Release(); }
    }

    private async Task StopCurrentCoreAsync()
    {
        var session = _current;
        _current = null;
        if (session is null)
        {
            if (Snapshot.State != PreviewState.Failed) SetSnapshot(PreviewSnapshot.Stopped);
            return;
        }
        await StopSessionAsync(session).ConfigureAwait(false);
        SetSnapshot(CreateSnapshot(session, PreviewState.Stopped, "Preview stopped by the operator.", null));
    }

    private async Task PumpErrorsAsync(PreviewSession session, StreamReader reader)
    {
        try
        {
            while (await reader.ReadLineAsync().ConfigureAwait(false) is { } line)
            {
                var safe = LogRedactor.Redact(line.Trim());
                if (string.IsNullOrWhiteSpace(safe)) continue;
                var separator = safe.IndexOf('=');
                if (separator > 0 && ProgressKeys.Contains(safe[..separator]))
                {
                    session.Progress[safe[..separator]] = safe[(separator + 1)..];
                    if (safe.StartsWith("progress=", StringComparison.Ordinal))
                        UpdateStatistics(session);
                    continue;
                }
                session.AddError(safe);
            }
        }
        catch (ObjectDisposedException) { }
        catch (InvalidOperationException) { }
    }

    private async Task StopSessionAsync(PreviewSession session)
    {
        session.Cancellation.Cancel();
        await StopOwnedProcessAsync(session.Producer).ConfigureAwait(false);
        await IgnoreFailure(session.ProducerErrors).ConfigureAwait(false);
        session.Producer.Dispose();
        session.Cancellation.Dispose();
    }

    private static async Task StopOwnedProcessAsync(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync().ConfigureAwait(false);
            }
        }
        catch (InvalidOperationException) { }
        catch (System.ComponentModel.Win32Exception) { }
    }

    private static async Task IgnoreFailure(Task? task)
    {
        if (task is null) return;
        try { await task.ConfigureAwait(false); } catch { }
    }

    private void UpdateStatistics(PreviewSession session)
    {
        var parts = new[]
        {
            Value(session, "frame", "frame"),
            Value(session, "fps", "fps"),
            Value(session, "bitrate", "bitrate"),
            Value(session, "speed", "speed")
        }.Where(value => value is not null);
        var statistics = string.Join(" · ", parts!);
        if (string.IsNullOrWhiteSpace(statistics)) return;
        lock (_snapshotLock)
        {
            if (!ReferenceEquals(_current, session) || _snapshot.State != PreviewState.Running) return;
            _snapshot = _snapshot with { PlaybackStatistics = statistics };
        }
        Changed?.Invoke();
    }

    private static string? Value(PreviewSession session, string key, string label) =>
        session.Progress.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)
            ? $"{label} {value.Trim()}"
            : null;

    private void SetSnapshot(PreviewSnapshot snapshot)
    {
        lock (_snapshotLock) _snapshot = snapshot;
        Changed?.Invoke();
    }

    private static PreviewSnapshot CreateSnapshot(PreviewSession session, PreviewState state, string statistics, string? error)
    {
        var active = state is PreviewState.Starting or PreviewState.Running;
        return new(state, session.Source.Identity.Value, session.Source.FriendlyName,
            VideoSummary(session.Source), AudioSummary(session.Source), HasAudio(session.Source), session.StartedAt,
            active ? SafeProcessId(session.Producer) : null, active ? session.StreamToken : null, statistics, error);
    }

    private static string VideoSummary(DiscoveredSource source) => source.Media switch
    {
        null => "Video properties not probed",
        { HasUsableVideo: false, AudioCodec: not null } => "Generated black video · audio-led input",
        var media => $"{media.VideoCodec} · {media.Width}×{media.Height} · {media.FramesPerSecond:0.##} fps"
    };

    private static string AudioSummary(DiscoveredSource source) => HasAudio(source)
        ? $"{source.Media!.AudioCodec} · {source.Media.AudioSampleRate} Hz · {source.Media.AudioChannels} ch"
        : "No audio track detected";

    private static bool HasAudio(DiscoveredSource source) => !string.IsNullOrWhiteSpace(source.Media?.AudioCodec);
    private static int? SafeProcessId(Process process) { try { return process.Id; } catch { return null; } }
    private static bool HasExited(Process process) { try { return process.HasExited; } catch { return true; } }
    private static bool HasExitedSuccessfully(Process process) { try { return process.HasExited && process.ExitCode == 0; } catch { return false; } }

    private static readonly HashSet<string> ProgressKeys = new(StringComparer.Ordinal)
    {
        "frame", "fps", "stream_0_0_q", "bitrate", "total_size", "out_time_us", "out_time_ms",
        "out_time", "dup_frames", "drop_frames", "speed", "progress"
    };

    private sealed class PreviewSession(DiscoveredSource source, Process producer)
    {
        private readonly ConcurrentQueue<string> _errors = new();
        public DiscoveredSource Source { get; } = source;
        public Process Producer { get; } = producer;
        public string StreamToken { get; } = Guid.NewGuid().ToString("N");
        public DateTimeOffset StartedAt { get; } = DateTimeOffset.UtcNow;
        public CancellationTokenSource Cancellation { get; } = new();
        public ConcurrentDictionary<string, string> Progress { get; } = new(StringComparer.Ordinal);
        public Task? ProducerErrors { get; set; }
        public int StreamClaimed;
        public string? LastError => _errors.LastOrDefault();

        public void AddError(string error)
        {
            _errors.Enqueue(error);
            while (_errors.Count > 40) _errors.TryDequeue(out _);
        }
    }
}
