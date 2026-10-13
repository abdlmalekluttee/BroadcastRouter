using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.IO.Pipes;
using System.Text.RegularExpressions;

// Test-only child: no network, media devices or application endpoints.
// In an isolated copied fixture directory, accept the application's real tokenized
// media-tool arguments. This executable never opens URLs or hardware.
var scriptPath = args.Length == 1 ? args[0]
    : args is ["-hide_banner", "-h", "full"] ? Path.Combine(AppContext.BaseDirectory, "help-full.json")
    : args is ["-hide_banner", "-h", "filter=astats"] ? Path.Combine(AppContext.BaseDirectory, "help-astats.json")
    : args is ["-hide_banner", "-h", "filter=ametadata"] ? Path.Combine(AppContext.BaseDirectory, "help-ametadata.json")
    : args.Contains("-show_streams") ? Path.Combine(AppContext.BaseDirectory, "probe.json")
    : args.Contains("-progress") ? Path.Combine(AppContext.BaseDirectory, "live.json") : null;
if (scriptPath is null || !File.Exists(scriptPath)) return 64;
if (args.Contains("-show_streams"))
{
    // Independent probes must not crash (or lose counts) by sharing an append
    // handle. Publish one completed marker per invocation, atomically after close.
    var marker = Path.Combine(AppContext.BaseDirectory, $"probe-starts.log.{Guid.NewGuid():N}.pending");
    await File.WriteAllTextAsync(marker, Environment.ProcessId + "\n");
    File.Move(marker, Path.ChangeExtension(marker, "started"));
}
if (OperatingSystem.IsWindows()) Native.SetErrorMode(0x0002 | 0x8000);
var script = JsonSerializer.Deserialize<Script>(await File.ReadAllTextAsync(scriptPath),
    new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? throw new InvalidDataException("Missing script");
if (script.Events.Any(e => e.AtMilliseconds is < 0 or > 60000)
    || script.ExitAfterMilliseconds is < 0 or > 60000
    || script.QuitDelayMilliseconds is < 0 or > 60000) return 65;
if (script.StartMarker is { } startMarker)
{
    using var self = Process.GetCurrentProcess();
    var pending = startMarker + ".pending";
    await File.WriteAllTextAsync(pending, JsonSerializer.Serialize(new
        { id = Environment.ProcessId, startedUtc = self.StartTime.ToUniversalTime() }));
    File.Move(pending, startMarker);
}
// Test-only descendant deliberately survives normal parent exit unless the real
// OS job contains it. The owning test retains its exact handle for RED cleanup.
using var descendant = script.SpawnChildScript is { } childScript
    ? Process.Start(new ProcessStartInfo(Environment.ProcessPath!)
    {
        UseShellExecute = false, CreateNoWindow = true,
        ArgumentList = { childScript }
    }) : null;
using var complete = new CancellationTokenSource();
var mediaWriters = new Dictionary<string, StreamWriter>(StringComparer.Ordinal);
if (script.CaptureArguments)
{
    // Existence is the test reader's ready signal. Publish only after the write
    // handle closes, so a reader cannot see a partial or write-locked document.
    var pendingArguments = Path.Combine(AppContext.BaseDirectory, $"arguments.{Guid.NewGuid():N}.pending");
    await File.WriteAllTextAsync(pendingArguments, JsonSerializer.Serialize(args));
    File.Move(pendingArguments, Path.Combine(AppContext.BaseDirectory, "arguments.json"), overwrite: true);
}
var timer = Stopwatch.StartNew();
var quit = Task.Run(async () =>
{
    while (await Console.In.ReadLineAsync() is { } line)
    {
        if (script.ProgressCommands && line.StartsWith("stderr ", StringComparison.Ordinal))
        {
            await Console.Error.WriteLineAsync(line[7..]);
            await Console.Error.FlushAsync();
        }
        if (script.ProgressCommands && line.Split(' ') is ["progress", var frame, var microseconds]
            && long.TryParse(frame, out _) && long.TryParse(microseconds, out _))
        {
            await Console.Out.WriteLineAsync($"frame={frame}\nfps=25\nspeed=1\nout_time_us={microseconds}\nprogress=continue");
            await Console.Out.FlushAsync();
        }
        if (script.ProgressCommands && line.Split(' ') is ["progress-duplicates", var duplicateFrame, var duplicateTime, var duplicates]
            && long.TryParse(duplicateFrame, out _) && long.TryParse(duplicateTime, out _) && long.TryParse(duplicates, out _))
        {
            await Console.Out.WriteLineAsync($"frame={duplicateFrame}\nfps=25\nspeed=1\nout_time_us={duplicateTime}\ndup_frames={duplicates}\nprogress=continue");
            await Console.Out.FlushAsync();
        }
        if (line == "q" && !script.IgnoreQuit)
        {
            await Console.Error.WriteLineAsync("synthetic quit received");
            await Console.Error.FlushAsync();
            await Task.Delay(script.QuitDelayMilliseconds);
            foreach (var text in script.QuitStderr ?? []) await Console.Error.WriteLineAsync(text);
            await Console.Error.FlushAsync();
            complete.Cancel();
            return;
        }
    }
});
try
{
    while (script.ContinueFile is { } continueFile && !File.Exists(continueFile))
        await Task.Delay(10, complete.Token);
    foreach (var item in script.Events.OrderBy(e => e.AtMilliseconds))
    {
        var wait = TimeSpan.FromMilliseconds(item.AtMilliseconds) - timer.Elapsed;
        if (wait > TimeSpan.Zero) await Task.Delay(wait, complete.Token);
        TextWriter writer;
        if (item.Channel.StartsWith("-stats_", StringComparison.Ordinal) || item.Channel == "pcm")
        {
            var index = Array.IndexOf(args, item.Channel == "pcm" ? "-af" : item.Channel);
            if (index < 0) continue; // Missing instrumentation is observable by the parent, not a child crash.
            if (!mediaWriters.TryGetValue(item.Channel, out var mediaWriter))
            {
                if (index + 1 >= args.Length) return 66;
                var endpoint = args[index + 1];
                if (item.Channel == "pcm")
                {
                    var match = Regex.Match(endpoint, "file='([^']+)'", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
                    if (!match.Success) continue;
                    endpoint = match.Groups[1].Value.Replace("\\\\", "\\", StringComparison.Ordinal);
                }
                const string prefix = @"\\.\pipe\broadcast-router-media-";
                if (!endpoint.StartsWith(prefix, StringComparison.Ordinal)
                    || !Guid.TryParseExact(endpoint[prefix.Length..], "N", out _)) return 66;
                var pipe = new NamedPipeClientStream(".", endpoint[9..], PipeDirection.Out, PipeOptions.Asynchronous);
                try { await pipe.ConnectAsync(3000, complete.Token); }
                catch { pipe.Dispose(); throw; }
                mediaWriter = new StreamWriter(pipe);
                mediaWriters.Add(item.Channel, mediaWriter);
            }
            writer = mediaWriter;
        }
        else writer = item.Channel == "stderr" ? Console.Error : Console.Out;
        await writer.WriteLineAsync(item.Text);
        await writer.FlushAsync();
    }
    if (script.ExitAfterMilliseconds is { } deadline)
    {
        var wait = TimeSpan.FromMilliseconds(deadline) - timer.Elapsed;
        if (wait > TimeSpan.Zero) await Task.Delay(wait, complete.Token);
        if (script.Crash) Environment.FailFast("Synthetic test child crash");
        return script.ExitCode;
    }
    await Task.Delay(Timeout.InfiniteTimeSpan, complete.Token);
}
catch (OperationCanceledException) when (complete.IsCancellationRequested) { }
finally { foreach (var writer in mediaWriters.Values) await writer.DisposeAsync(); }
return 0;

internal sealed record Script(Event[] Events, int? ExitAfterMilliseconds, int ExitCode, bool IgnoreQuit, bool Crash,
    int QuitDelayMilliseconds = 0, string[]? QuitStderr = null, bool ProgressCommands = false, string? ContinueFile = null,
    bool CaptureArguments = false, string? StartMarker = null, string? SpawnChildScript = null);
internal sealed record Event(int AtMilliseconds, string Channel, string Text);
internal static class Native
{
    [DllImport("kernel32.dll")]
    internal static extern uint SetErrorMode(uint mode);
}
