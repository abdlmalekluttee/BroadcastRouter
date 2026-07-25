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
    int? PlayerProcessId,
    string PlaybackStatistics,
    string? ErrorMessage)
{
    public static PreviewSnapshot Stopped { get; } = new(
        PreviewState.Stopped, null, null, "No source selected", "No preview audio", false,
        null, null, null, "Waiting for an operator to start preview.", null);
}

public sealed record FfplayPreviewCommandPlan(ProcessStartInfo Producer, ProcessStartInfo Player);

public static class FfplayPreviewCommandBuilder
{
    public const int WindowWidth = 1440;
    public const int WindowHeight = 900;

    public static FfplayPreviewCommandPlan Build(MediaToolPaths tools, DiscoveredSource source)
    {
        if (string.IsNullOrWhiteSpace(tools.FfmpegPath)) throw new InvalidOperationException("FFmpeg is not configured.");
        if (string.IsNullOrWhiteSpace(tools.FfplayPath)) throw new InvalidOperationException("FFplay is not configured.");

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

        var filter = hasAudio
            ? "[0:v:0]scale=1440:810:force_original_aspect_ratio=decrease,pad=1440:810:(ow-iw)/2:(oh-ih)/2:color=0x060b12,pad=1440:900:0:0:color=0x060b12[canvas];" +
              "[0:a:0]asplit=2[previewaudio][meter];" +
              "[meter]showvolume=w=1400:h=70:r=25:b=3:f=0.25:t=1:v=1:dm=1:dmc=orange:o=h:p=0.25:m=p:ds=log[vu];" +
              "[canvas][vu]overlay=20:820:shortest=1[outv];[previewaudio]anull[outa]"
            : "[0:v:0]scale=1440:810:force_original_aspect_ratio=decrease,pad=1440:810:(ow-iw)/2:(oh-ih)/2:color=0x060b12,pad=1440:900:0:0:color=0x060b12[outv]";

        Add(producer, "-filter_complex", filter, "-map", "[outv]");
        if (hasAudio)
            Add(producer, "-map", "[outa]", "-c:a", "mp2", "-b:a", "192k", "-ar", "48000", "-ac", "2");
        else
            producer.ArgumentList.Add("-an");
        Add(producer,
            "-c:v", "mpeg2video", "-q:v", "4", "-pix_fmt", "yuv420p",
            "-f", "mpegts", "pipe:1");

        var player = new ProcessStartInfo
        {
            FileName = tools.FfplayPath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        Add(player,
            "-hide_banner", "-loglevel", "info", "-stats", "-autoexit",
            "-fflags", "nobuffer", "-flags", "low_delay", "-framedrop",
            "-x", WindowWidth.ToString(), "-y", WindowHeight.ToString(),
            "-window_title", BuildWindowTitle(source),
            "-f", "mpegts", "-i", "pipe:0");
        return new(producer, player);
    }

    private static string BuildWindowTitle(DiscoveredSource source)
    {
        var safeName = new string(source.FriendlyName.Where(character => !char.IsControl(character)).ToArray()).Trim();
        if (safeName.Length > 70) safeName = safeName[..70];
        var media = source.Media;
        var video = media is null ? "unprobed" : $"{media.Width}x{media.Height} {media.FramesPerSecond:0.##}fps {media.VideoCodec}";
        var audio = string.IsNullOrWhiteSpace(media?.AudioCodec) ? "no audio" : $"{media.AudioCodec} {media.AudioSampleRate}Hz {media.AudioChannels}ch";
        return $"BroadcastRouter Preview | {safeName} | {video} | {audio}";
    }

    private static void Add(ProcessStartInfo start, params string[] arguments)
    {
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
    }
}

public sealed class FfplayPreviewSupervisor : IAsyncDisposable
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
            if (!Environment.UserInteractive)
                throw new InvalidOperationException("Desktop preview requires an interactive Windows logon and is unavailable in service/Session 0 mode.");
            if (!File.Exists(tools.FfmpegPath)) throw new FileNotFoundException("Configured FFmpeg executable was not found.");
            if (!File.Exists(tools.FfplayPath)) throw new FileNotFoundException("Configured FFplay executable was not found.");

            var plan = FfplayPreviewCommandBuilder.Build(tools, source);
            candidate = new PreviewSession(source, new Process { StartInfo = plan.Producer }, new Process { StartInfo = plan.Player });
            SetSnapshot(CreateSnapshot(candidate, PreviewState.Starting, "Opening the large FFplay monitor...", null));

            if (!candidate.Player.Start()) throw new InvalidOperationException("FFplay did not start.");
            if (!candidate.Producer.Start()) throw new InvalidOperationException("FFmpeg preview producer did not start.");

            _current = candidate;
            candidate.ProducerErrors = PumpErrorsAsync(candidate, candidate.Producer.StandardError, isPlayer: false);
            candidate.PlayerErrors = PumpErrorsAsync(candidate, candidate.Player.StandardError, isPlayer: true);
            candidate.PlayerOutput = DrainAsync(candidate.Player.StandardOutput);
            candidate.Pipe = PipeAsync(candidate);
            SetSnapshot(CreateSnapshot(candidate, PreviewState.Running, "Live playback statistics are initializing...", null));
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
            await Task.WhenAny(session.Producer.WaitForExitAsync(), session.Player.WaitForExitAsync(), session.Pipe ?? Task.CompletedTask)
                .ConfigureAwait(false);
            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                if (!ReferenceEquals(_current, session)) return;
                _current = null;
                var expected = HasExitedSuccessfully(session.Player) || HasExitedSuccessfully(session.Producer);
                await StopSessionAsync(session).ConfigureAwait(false);
                var error = expected ? null : session.LastError;
                SetSnapshot(CreateSnapshot(session, expected ? PreviewState.Stopped : PreviewState.Failed,
                    expected ? "Preview window closed." : "Preview ended unexpectedly.", error));
            }
            finally { _gate.Release(); }
        }
        catch (Exception exception)
        {
            session.AddError(LogRedactor.Redact(exception.Message));
        }
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

    private static async Task PipeAsync(PreviewSession session)
    {
        try
        {
            await session.Producer.StandardOutput.BaseStream
                .CopyToAsync(session.Player.StandardInput.BaseStream, session.Cancellation.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (session.Cancellation.IsCancellationRequested) { }
        catch (Exception exception) { session.AddError(LogRedactor.Redact(exception.Message)); }
        finally
        {
            try { session.Player.StandardInput.Close(); } catch { }
        }
    }

    private async Task PumpErrorsAsync(PreviewSession session, StreamReader reader, bool isPlayer)
    {
        try
        {
            while (await reader.ReadLineAsync().ConfigureAwait(false) is { } line)
            {
                var safe = LogRedactor.Redact(line.Trim());
                if (string.IsNullOrWhiteSpace(safe)) continue;
                session.AddError(safe);
                if (isPlayer && (safe.Contains("A-V:", StringComparison.OrdinalIgnoreCase) || safe.Contains("aq=", StringComparison.OrdinalIgnoreCase)))
                {
                    if (safe.Length > 220) safe = safe[..220];
                    var now = DateTimeOffset.UtcNow;
                    if (now - session.LastStatisticsPublishedAt >= TimeSpan.FromMilliseconds(500))
                    {
                        session.LastStatisticsPublishedAt = now;
                        UpdateStatistics(session, safe);
                    }
                }
            }
        }
        catch (ObjectDisposedException) { }
        catch (InvalidOperationException) { }
    }

    private static async Task DrainAsync(StreamReader reader)
    {
        try { while (await reader.ReadLineAsync().ConfigureAwait(false) is not null) { } }
        catch (ObjectDisposedException) { }
        catch (InvalidOperationException) { }
    }

    private async Task StopSessionAsync(PreviewSession session)
    {
        session.Cancellation.Cancel();
        await StopOwnedProcessAsync(session.Producer).ConfigureAwait(false);
        try { session.Player.StandardInput.Close(); } catch { }
        await StopOwnedProcessAsync(session.Player).ConfigureAwait(false);
        await IgnoreFailure(session.Pipe).ConfigureAwait(false);
        await IgnoreFailure(session.ProducerErrors).ConfigureAwait(false);
        await IgnoreFailure(session.PlayerErrors).ConfigureAwait(false);
        await IgnoreFailure(session.PlayerOutput).ConfigureAwait(false);
        session.Producer.Dispose();
        session.Player.Dispose();
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

    private void UpdateStatistics(PreviewSession session, string statistics)
    {
        lock (_snapshotLock)
        {
            if (!ReferenceEquals(_current, session) || _snapshot.State != PreviewState.Running) return;
            _snapshot = _snapshot with { PlaybackStatistics = statistics };
        }
        Changed?.Invoke();
    }

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
            active ? SafeProcessId(session.Producer) : null, active ? SafeProcessId(session.Player) : null, statistics, error);
    }

    private static string VideoSummary(DiscoveredSource source) => source.Media is null
        ? "Video properties not probed"
        : $"{source.Media.VideoCodec} · {source.Media.Width}×{source.Media.Height} · {source.Media.FramesPerSecond:0.##} fps";

    private static string AudioSummary(DiscoveredSource source) => HasAudio(source)
        ? $"{source.Media!.AudioCodec} · {source.Media.AudioSampleRate} Hz · {source.Media.AudioChannels} ch"
        : "No audio track detected";

    private static bool HasAudio(DiscoveredSource source) => !string.IsNullOrWhiteSpace(source.Media?.AudioCodec);
    private static int? SafeProcessId(Process process) { try { return process.Id; } catch { return null; } }
    private static bool HasExitedSuccessfully(Process process) { try { return process.HasExited && process.ExitCode == 0; } catch { return false; } }

    private sealed class PreviewSession(DiscoveredSource source, Process producer, Process player)
    {
        private readonly ConcurrentQueue<string> _errors = new();
        public DiscoveredSource Source { get; } = source;
        public Process Producer { get; } = producer;
        public Process Player { get; } = player;
        public DateTimeOffset StartedAt { get; } = DateTimeOffset.UtcNow;
        public CancellationTokenSource Cancellation { get; } = new();
        public Task? Pipe { get; set; }
        public Task? ProducerErrors { get; set; }
        public Task? PlayerErrors { get; set; }
        public Task? PlayerOutput { get; set; }
        public DateTimeOffset LastStatisticsPublishedAt { get; set; }
        public string? LastError => _errors.LastOrDefault();

        public void AddError(string error)
        {
            _errors.Enqueue(error);
            while (_errors.Count > 40) _errors.TryDequeue(out _);
        }
    }
}
