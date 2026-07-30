using System.Diagnostics;
using System.Globalization;
using BroadcastRouter.Domain;

namespace BroadcastRouter.Infrastructure;

public sealed record FfmpegRouteOptions(
    string ExecutablePath,
    bool UseTcpTransport = true,
    TimeSpan? ReadTimeout = null,
    string LogLevel = "warning",
    bool UseWindowsDeckLinkSafeTerminate = false);

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
        if (preset.LowLatency)
            Add(start,
                "-fflags", "nobuffer",
                "-flags", "low_delay",
                "-analyzeduration", "1000000",
                "-probesize", "1000000",
                "-fpsprobesize", "0");
        Add(start, "-i", source.RtspUri.AbsoluteUri);

        var audioLed = source.Media is { HasUsableVideo: false } media
            && !string.IsNullOrWhiteSpace(media.AudioCodec);
        if (audioLed)
        {
            if (!preset.IncludeAudio)
                throw new InvalidOperationException("Audio-led sources require an audio-enabled output preset.");

            Add(start, "-re", "-f", "lavfi", "-i", BlackVideoInput(preset));
            Add(start,
                "-map", "1:v:0",
                "-vf", BuildVideoFilter(preset, sourceIsInterlaced: false),
                "-pix_fmt", preset.Mode.PixelFormat,
                "-map", "0:a:0",
                "-ar", "48000", "-ac", "2", "-c:a", "pcm_s16le",
                "-shortest");
        }
        else
        {
            var videoFilter = BuildVideoFilter(preset, source.Media?.Interlaced == true);
            Add(start,
                "-map", "0:v:0",
                "-vf", videoFilter,
                "-pix_fmt", preset.Mode.PixelFormat);
            if (preset.IncludeAudio)
                Add(start, "-map", "0:a:0?", "-ar", "48000", "-ac", "2", "-c:a", "pcm_s16le");
            else Add(start, "-an");
        }
        AddDeckLinkOutput(start, options, port.FfmpegName);
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
        // FFmpeg requires every input to be declared before output-only options
        // such as -map. Declaring anullsrc after the video map makes FFmpeg
        // interpret the map as an input option for anullsrc and reject startup.
        if (preset.IncludeAudio) Add(start, "-f", "lavfi", "-i", "anullsrc=r=48000:cl=stereo");
        Add(start, "-map", "0:v:0", "-vf", BuildVideoFilter(preset, sourceIsInterlaced: false), "-pix_fmt", preset.Mode.PixelFormat);
        if (preset.IncludeAudio) Add(start, "-map", "1:a:0", "-ar", "48000", "-ac", "2", "-c:a", "pcm_s16le", "-shortest");
        else Add(start, "-an");
        AddDeckLinkOutput(start, options, port.FfmpegName);
        return start;
    }

    public static ProcessStartInfo BuildPortStandby(
        FfmpegRouteOptions options,
        DeckLinkPort port,
        OutputPreset preset,
        PortStandbyConfiguration configuration)
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
        var rate = preset.Interlaced
            ? $"{checked(preset.Mode.FrameRateNumerator * 2)}/{preset.Mode.FrameRateDenominator}"
            : $"{preset.Mode.FrameRateNumerator}/{preset.Mode.FrameRateDenominator}";
        var source = configuration.Pattern switch
        {
            StandbyPattern.SmpteBars => $"smptebars=size={preset.Mode.Width}x{preset.Mode.Height}:rate={rate}",
            StandbyPattern.SmpteHdBars => $"smptehdbars=size={preset.Mode.Width}x{preset.Mode.Height}:rate={rate}",
            StandbyPattern.TestSource => $"testsrc2=size={preset.Mode.Width}x{preset.Mode.Height}:rate={rate}",
            _ => $"color=c=black:size={preset.Mode.Width}x{preset.Mode.Height}:rate={rate}"
        };
        Add(start, "-re", "-f", "lavfi", "-i", source);

        var hasLogo = !string.IsNullOrWhiteSpace(configuration.LogoPath);
        if (hasLogo)
        {
            if (!File.Exists(configuration.LogoPath)) throw new InvalidOperationException("The configured per-port standby logo does not exist.");
            Add(start, "-loop", "1", "-framerate", rate, "-i", configuration.LogoPath!);
        }
        var audioInput = hasLogo ? 2 : 1;
        if (preset.IncludeAudio) Add(start, "-f", "lavfi", "-i", "anullsrc=r=48000:cl=stereo");

        var cardAndPort = SafeOverlayText($"{DeckLinkDisplayName.Card(port)}  -  SDI {port.SubdeviceIndex + 1}");
        var outputLabel = SafeOverlayText(string.IsNullOrWhiteSpace(configuration.PortLabel)
            ? DeckLinkDisplayName.Connector(port)
            : configuration.PortLabel);
        var labelFontSize = Math.Clamp(preset.Mode.Height / 26, 24, 64);
        var clockFontSize = Math.Clamp(preset.Mode.Height / 9, 64, 144);
        var dateFontSize = Math.Clamp(preset.Mode.Height / 24, 28, 64);
        var margin = Math.Clamp(preset.Mode.Height / 36, 16, 48);
        const string windowsFont = "fontfile='C\\:/Windows/Fonts/arial.ttf':";
        var textFilters = new List<string>
        {
            $"drawtext={windowsFont}text='{cardAndPort}':fontcolor=white:fontsize={labelFontSize}:box=1:boxcolor=black@0.72:boxborderw={margin / 2}:x=(w-tw)/2:y={margin}"
        };
        if (configuration.ShowClock)
        {
            // %T is not implemented by the Windows C runtime used by FFmpeg. The
            // literal colons in an explicit HH:mm:ss format must survive both the
            // filter-graph parser and drawtext's expansion parser.
            textFilters.Add($"drawtext={windowsFont}text='%{{localtime\\:%H\\\\\\:%M\\\\\\:%S}}':fontcolor=white:fontsize={clockFontSize}:box=1:boxcolor=black@0.72:boxborderw={margin / 2}:x=(w-tw)/2:y=h/2-th-{dateFontSize / 3}");
            textFilters.Add($"drawtext={windowsFont}text='%{{localtime\\:%A %d %B %Y}}':fontcolor=white:fontsize={dateFontSize}:box=1:boxcolor=black@0.72:boxborderw={margin / 3}:x=(w-tw)/2:y=h/2+{dateFontSize / 2}");
        }
        if (!string.IsNullOrWhiteSpace(outputLabel))
            textFilters.Add($"drawtext={windowsFont}text='{outputLabel}':fontcolor=white:fontsize={labelFontSize}:box=1:boxcolor=black@0.72:boxborderw={margin / 2}:x=(w-tw)/2:y=h-th-{margin}");

        var baseFilter = BuildVideoFilter(preset, sourceIsInterlaced: false);
        string filterGraph;
        if (hasLogo)
        {
            var logoSize = Math.Clamp(Math.Min(preset.Mode.Width, preset.Mode.Height) / 7, 72, 220);
            var tail = string.Join(',', textFilters);
            filterGraph = $"[0:v:0]{baseFilter}[base];" +
                $"[1:v:0]scale={logoSize}:{logoSize}:force_original_aspect_ratio=decrease,split=4[logo_tl][logo_tr][logo_bl][logo_br];" +
                $"[base][logo_tl]overlay=x={margin}:y={margin}[corner1];" +
                $"[corner1][logo_tr]overlay=x=W-w-{margin}:y={margin}[corner2];" +
                $"[corner2][logo_bl]overlay=x={margin}:y=H-h-{margin}[corner3];" +
                $"[corner3][logo_br]overlay=x=W-w-{margin}:y=H-h-{margin}[composite];" +
                $"[composite]{tail}[outv]";
        }
        else
        {
            filterGraph = $"[0:v:0]{baseFilter},{string.Join(',', textFilters)}[outv]";
        }

        Add(start, "-filter_complex", filterGraph, "-map", "[outv]", "-pix_fmt", preset.Mode.PixelFormat);
        if (preset.IncludeAudio)
            Add(start, "-map", $"{audioInput}:a:0", "-ar", "48000", "-ac", "2", "-c:a", "pcm_s16le");
        else Add(start, "-an");
        AddDeckLinkOutput(start, options, port.FfmpegName);
        return start;
    }

    private static void AddDeckLinkOutput(ProcessStartInfo start, FfmpegRouteOptions options, string ffmpegName)
    {
        if (options.UseWindowsDeckLinkSafeTerminate) Add(start, "-win_safe_terminate", "1");
        Add(start, "-f", "decklink", ffmpegName);
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

    private static string BlackVideoInput(OutputPreset preset)
    {
        var rate = preset.Interlaced
            ? $"{checked(preset.Mode.FrameRateNumerator * 2)}/{preset.Mode.FrameRateDenominator}"
            : $"{preset.Mode.FrameRateNumerator}/{preset.Mode.FrameRateDenominator}";
        return $"color=c=black:size={preset.Mode.Width}x{preset.Mode.Height}:rate={rate}";
    }

    private static void ValidateToken(string value, string name)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Any(char.IsControl))
            throw new ArgumentException("FFmpeg argument values cannot be empty or contain control characters.", name);
    }

    private static string SafeOverlayText(string value) => new(value
        .Where(character => char.IsLetterOrDigit(character) || char.IsWhiteSpace(character)
            || character is '-' or '_' or '.' or '/' or '#' or '(' or ')')
        .Take(160)
        .ToArray());

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
