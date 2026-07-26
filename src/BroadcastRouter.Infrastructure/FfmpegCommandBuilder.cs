using System.Diagnostics;
using System.Globalization;
using BroadcastRouter.Domain;

namespace BroadcastRouter.Infrastructure;

public sealed record FfmpegRouteOptions(
    string ExecutablePath,
    bool UseTcpTransport = true,
    TimeSpan? ReadTimeout = null,
    string LogLevel = "warning");

public static class FfmpegCommandBuilder
{
    public static ProcessStartInfo Build(
        FfmpegRouteOptions options,
        DiscoveredSource source,
        DeckLinkPort port,
        OutputPreset preset)
    {
        if (string.IsNullOrWhiteSpace(options.ExecutablePath)) throw new ArgumentException("FFmpeg path is required.", nameof(options));
        ValidateToken(port.FfmpegName, nameof(port.FfmpegName));
        ValidateToken(preset.Mode.PixelFormat, nameof(preset.Mode.PixelFormat));

        var start = new ProcessStartInfo(options.ExecutablePath)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            CreateNoWindow = true
        };

        Add(start, "-hide_banner", "-loglevel", options.LogLevel, "-progress", "pipe:1", "-nostats");
        if (options.UseTcpTransport) Add(start, "-rtsp_transport", "tcp");
        if (options.ReadTimeout is { } timeout)
            Add(start, "-timeout", ((long)timeout.TotalMicroseconds).ToString(CultureInfo.InvariantCulture));
        if (preset.BufferSizeMegabytes > 0) Add(start, "-buffer_size", $"{preset.BufferSizeMegabytes}M");
        if (preset.LowLatency) Add(start, "-flags", "low_delay");
        Add(start, "-i", source.RtspUri.AbsoluteUri);

        var videoFilter = BuildVideoFilter(preset, source.Media?.Interlaced == true);
        Add(start,
            "-map", "0:v:0",
            "-vf", videoFilter,
            "-pix_fmt", preset.Mode.PixelFormat);
        if (preset.IncludeAudio)
            Add(start, "-map", "0:a:0?", "-ar", "48000", "-ac", "2", "-c:a", "pcm_s16le");
        else Add(start, "-an");
        Add(start, "-f", "decklink", port.FfmpegName);
        return start;
    }

    public static ProcessStartInfo BuildFallback(
        FfmpegRouteOptions options,
        DeckLinkPort port,
        OutputPreset preset,
        FallbackMode mode,
        string? value)
    {
        if (string.IsNullOrWhiteSpace(options.ExecutablePath)) throw new ArgumentException("FFmpeg path is required.", nameof(options));
        ValidateToken(port.FfmpegName, nameof(port.FfmpegName));
        var start = new ProcessStartInfo(options.ExecutablePath)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            CreateNoWindow = true
        };
        Add(start, "-hide_banner", "-loglevel", options.LogLevel, "-progress", "pipe:1", "-nostats");
        var rate = preset.Interlaced
            ? $"{checked(preset.Mode.FrameRateNumerator * 2)}/{preset.Mode.FrameRateDenominator}"
            : $"{preset.Mode.FrameRateNumerator}/{preset.Mode.FrameRateDenominator}";
        switch (mode)
        {
            case FallbackMode.TestPattern:
                Add(start, "-re", "-f", "lavfi", "-i", $"smptebars=size={preset.Mode.Width}x{preset.Mode.Height}:rate={rate}");
                break;
            case FallbackMode.File:
                if (string.IsNullOrWhiteSpace(value) || !File.Exists(value)) throw new InvalidOperationException("The configured standby media file does not exist.");
                Add(start, "-stream_loop", "-1", "-re", "-i", value);
                break;
            case FallbackMode.FreezeLastFrame:
                if (string.IsNullOrWhiteSpace(value) || !File.Exists(value)) throw new InvalidOperationException("Freeze-frame standby requires a configured image file.");
                Add(start, "-loop", "1", "-framerate", rate, "-i", value);
                break;
            case FallbackMode.StandbySource:
                if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != "rtsp") throw new InvalidOperationException("Standby source must be an absolute RTSP URL.");
                Add(start, "-rtsp_transport", "tcp", "-i", uri.AbsoluteUri);
                break;
            default:
                Add(start, "-re", "-f", "lavfi", "-i", $"color=c=black:size={preset.Mode.Width}x{preset.Mode.Height}:rate={rate}");
                break;
        }
        Add(start, "-map", "0:v:0", "-vf", BuildVideoFilter(preset, sourceIsInterlaced: false), "-pix_fmt", preset.Mode.PixelFormat);
        if (preset.IncludeAudio) Add(start, "-f", "lavfi", "-i", "anullsrc=r=48000:cl=stereo", "-map", "1:a:0", "-ar", "48000", "-ac", "2", "-c:a", "pcm_s16le", "-shortest");
        else Add(start, "-an");
        Add(start, "-f", "decklink", port.FfmpegName);
        return start;
    }

    public static string ToRedactedDisplay(ProcessStartInfo start)
    {
        var tokens = start.ArgumentList.Select(RedactUri).Select(QuoteForDisplay);
        return $"{QuoteForDisplay(start.FileName)} {string.Join(' ', tokens)}";
    }

    private static void Add(ProcessStartInfo start, params string[] arguments)
    {
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
    }

    private static string BuildVideoFilter(OutputPreset preset, bool sourceIsInterlaced)
    {
        var outputRate = $"{preset.Mode.FrameRateNumerator}/{preset.Mode.FrameRateDenominator}";
        var scale = $"scale={preset.Mode.Width}:{preset.Mode.Height}:flags=lanczos";
        if (preset.Interlaced)
        {
            var fieldRate = $"{checked(preset.Mode.FrameRateNumerator * 2)}/{preset.Mode.FrameRateDenominator}";
            var deinterlace = sourceIsInterlaced ? "yadif=mode=send_field:parity=auto:deint=interlaced," : "";
            return $"{deinterlace}{scale},fps={fieldRate},tinterlace=interleave_top:flags=vlpf,setfield=tff";
        }

        var progressive = sourceIsInterlaced ? "yadif=mode=send_frame:parity=auto:deint=interlaced," : "";
        return $"{progressive}{scale},fps={outputRate}";
    }

    private static void ValidateToken(string value, string name)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Any(char.IsControl))
            throw new ArgumentException("FFmpeg argument values cannot be empty or contain control characters.", name);
    }

    private static string RedactUri(string token)
    {
        if (!Uri.TryCreate(token, UriKind.Absolute, out var uri) || string.IsNullOrEmpty(uri.UserInfo)) return token;
        var builder = new UriBuilder(uri) { UserName = "***", Password = "***" };
        return builder.Uri.AbsoluteUri;
    }

    private static string QuoteForDisplay(string value) => value.Any(char.IsWhiteSpace) || value.Contains('"')
        ? $"\"{value.Replace("\"", "\\\"")}\""
        : value;
}
