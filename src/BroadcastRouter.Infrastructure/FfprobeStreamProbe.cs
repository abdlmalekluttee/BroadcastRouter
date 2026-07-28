using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using BroadcastRouter.Application;
using BroadcastRouter.Domain;

namespace BroadcastRouter.Infrastructure;

public sealed class FfprobeStreamProbe(string executablePath, TimeSpan timeout) : IStreamProbe
{
    public async Task<StreamProbeResult> ProbeAsync(Uri rtspUri, CancellationToken cancellationToken)
    {
        if (!File.Exists(executablePath))
            return new(false, false, null, "FfprobeMissing", "The configured FFprobe executable does not exist.");

        var start = new ProcessStartInfo(executablePath)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        Add(start, "-v", "error", "-rtsp_transport", "tcp", "-rw_timeout",
            ((long)timeout.TotalMicroseconds).ToString(CultureInfo.InvariantCulture),
            "-read_intervals", "%+2", "-count_frames", "-count_packets",
            "-show_streams", "-show_format", "-of", "json", rtspUri.AbsoluteUri);

        using var process = Process.Start(start);
        if (process is null) return new(false, false, null, "ProcessStart", "FFprobe could not be started.");

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        var stdoutTask = process.StandardOutput.ReadToEndAsync(deadline.Token);
        var stderrTask = process.StandardError.ReadToEndAsync(deadline.Token);
        try
        {
            await process.WaitForExitAsync(deadline.Token).ConfigureAwait(false);
            var stdout = await stdoutTask.ConfigureAwait(false);
            var stderr = await stderrTask.ConfigureAwait(false);
            if (process.ExitCode != 0)
            {
                var category = FfmpegErrorClassifier.Classify(process.ExitCode, stderr).ToString();
                return new(false, false, null, category, SanitizeDetail(stderr));
            }

            return Parse(stdout);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            await TerminateAsync(process, stdoutTask, stderrTask).ConfigureAwait(false);
            return new(false, false, null, "Timeout", $"FFprobe exceeded the {timeout.TotalSeconds:0.#} second deadline.");
        }
        catch (OperationCanceledException)
        {
            await TerminateAsync(process, stdoutTask, stderrTask).ConfigureAwait(false);
            throw;
        }
        catch
        {
            await TerminateAsync(process, stdoutTask, stderrTask).ConfigureAwait(false);
            throw;
        }
    }

    public static StreamProbeResult Parse(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return new(false, false, null, "InvalidOutput", "FFprobe returned empty output.");
        try { return ParseDocument(json); }
        catch (JsonException)
        {
            return new(false, false, null, "InvalidOutput", "FFprobe returned malformed JSON.");
        }
    }

    private static StreamProbeResult ParseDocument(string json)
    {
        using var document = JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty("streams", out var streams) || streams.ValueKind != JsonValueKind.Array)
            return new(true, false, null, "NoStreams", "FFprobe opened the input but returned no streams.");

        JsonElement? video = null;
        JsonElement? audio = null;
        foreach (var stream in streams.EnumerateArray())
        {
            var codecType = Text(stream, "codec_type");
            if (codecType == "video" && video is null) video = stream.Clone();
            if (codecType == "audio" && audio is null) audio = stream.Clone();
        }

        var audioCount = audio is null ? 0 : ReadCount(audio.Value);
        var audioReceived = audioCount > 0;
        if (video is null)
        {
            var audioMedia = new MediaProperties(null, audio is null ? null : Text(audio.Value, "codec_name"), null, null, null, null,
                audio is null ? null : Integer(audio.Value, "sample_rate"), audio is null ? null : Integer(audio.Value, "channels"), false);
            return audioReceived
                ? new(true, false, audioMedia, null, $"Received {audioCount} audio packet(s)/frame(s); continuous black video will be generated for routing.", true)
                : new(true, false, audioMedia, "AudioOnly", "Audio metadata was detected, but no audio packets or frames were received.");
        }

        var frameCount = Integer64(video.Value, "nb_read_frames") ?? 0;
        var fps = Rational(Text(video.Value, "avg_frame_rate")) ?? Rational(Text(video.Value, "r_frame_rate"));
        var sustainedVideo = frameCount > 0 && (!audioReceived || HasSustainedVideo(frameCount, fps));
        var bitRate = Integer64(video.Value, "bit_rate");
        var fieldOrder = Text(video.Value, "field_order");
        var interlaced = fieldOrder switch
        {
            "tt" or "bb" or "tb" or "bt" => true,
            "progressive" => false,
            _ => (bool?)null
        };
        if (bitRate is null && document.RootElement.TryGetProperty("format", out var format)) bitRate = Integer64(format, "bit_rate");
        var media = new MediaProperties(
            Text(video.Value, "codec_name"),
            audio is null ? null : Text(audio.Value, "codec_name"),
            Integer(video.Value, "width"),
            Integer(video.Value, "height"),
            fps,
            bitRate,
            audio is null ? null : Integer(audio.Value, "sample_rate"),
            audio is null ? null : Integer(audio.Value, "channels"),
            sustainedVideo,
            interlaced);
        return sustainedVideo
            ? new(true, true, media, null, $"Received {frameCount} video frame(s) during validation.")
            : audioReceived
                ? new(true, false, media, null, $"Received {audioCount} audio packet(s)/frame(s) while video delivered only {frameCount} frame(s); continuous black video will be generated for routing.", true)
            : new(true, false, media, "NoVideoFrames", "Video metadata was detected but no decoded/read frames were reported.");
    }

    private static bool HasSustainedVideo(long frameCount, double? framesPerSecond)
    {
        // The probe samples two seconds. Require roughly one second of the advertised cadence
        // before video can override verified continuous audio; an isolated still must not do so.
        var cadence = Math.Clamp(framesPerSecond.GetValueOrDefault(2), 1, 60);
        var minimumFrames = Math.Max(2L, (long)Math.Ceiling(cadence));
        return frameCount >= minimumFrames;
    }

    private static long ReadCount(JsonElement stream) => Math.Max(
        Integer64(stream, "nb_read_frames") ?? 0,
        Integer64(stream, "nb_read_packets") ?? 0);

    private static string? Text(JsonElement element, string name) => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    private static int? Integer(JsonElement element, string name) => int.TryParse(Text(element, name) ?? (element.TryGetProperty(name, out var value) ? value.ToString() : null), NumberStyles.Integer, CultureInfo.InvariantCulture, out var result) ? result : null;
    private static long? Integer64(JsonElement element, string name) => long.TryParse(Text(element, name) ?? (element.TryGetProperty(name, out var value) ? value.ToString() : null), NumberStyles.Integer, CultureInfo.InvariantCulture, out var result) ? result : null;
    private static double? Rational(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var parts = value.Split('/');
        if (parts.Length == 2 && double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var numerator) && double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var denominator) && denominator != 0) return numerator / denominator;
        return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var scalar) ? scalar : null;
    }

    private static void Add(ProcessStartInfo start, params string[] arguments) { foreach (var argument in arguments) start.ArgumentList.Add(argument); }
    private static string SanitizeDetail(string stderr)
    {
        var line = stderr.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? "FFprobe failed.";
        return line.Length <= 500 ? line : line[..500];
    }
    private static async Task TerminateAsync(Process process, Task<string> stdoutTask, Task<string> stderrTask)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
        try { await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false); } catch { }
        try { await stdoutTask.ConfigureAwait(false); } catch { }
        try { await stderrTask.ConfigureAwait(false); } catch { }
    }
}
