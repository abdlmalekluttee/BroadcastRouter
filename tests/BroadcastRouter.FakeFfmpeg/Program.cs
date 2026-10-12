using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;

// Test-only child: no network, media devices or application endpoints.
// In an isolated copied fixture directory, accept the application's real tokenized
// media-tool arguments. This executable never opens URLs or hardware.
var scriptPath = args.Length == 1 ? args[0]
    : args.Contains("-show_streams") ? Path.Combine(AppContext.BaseDirectory, "probe.json")
    : args.Contains("-progress") ? Path.Combine(AppContext.BaseDirectory, "live.json") : null;
if (scriptPath is null || !File.Exists(scriptPath)) return 64;
if (args.Contains("-show_streams"))
    await File.AppendAllTextAsync(Path.Combine(AppContext.BaseDirectory, "probe-starts.log"), Environment.ProcessId + "\n");
if (OperatingSystem.IsWindows()) Native.SetErrorMode(0x0002 | 0x8000);
var script = JsonSerializer.Deserialize<Script>(await File.ReadAllTextAsync(scriptPath),
    new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? throw new InvalidDataException("Missing script");
if (script.Events.Any(e => e.AtMilliseconds is < 0 or > 60000)
    || script.ExitAfterMilliseconds is < 0 or > 60000
    || script.QuitDelayMilliseconds is < 0 or > 60000) return 65;
using var complete = new CancellationTokenSource();
var timer = Stopwatch.StartNew();
var quit = Task.Run(async () =>
{
    while (await Console.In.ReadLineAsync() is { } line)
    {
        if (script.ProgressCommands && line.Split(' ') is ["progress", var frame, var microseconds]
            && long.TryParse(frame, out _) && long.TryParse(microseconds, out _))
        {
            await Console.Out.WriteLineAsync($"frame={frame}\nfps=25\nspeed=1\nout_time_us={microseconds}\nprogress=continue");
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
    foreach (var item in script.Events.OrderBy(e => e.AtMilliseconds))
    {
        var wait = TimeSpan.FromMilliseconds(item.AtMilliseconds) - timer.Elapsed;
        if (wait > TimeSpan.Zero) await Task.Delay(wait, complete.Token);
        var writer = item.Channel == "stderr" ? Console.Error : Console.Out;
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
return 0;

internal sealed record Script(Event[] Events, int? ExitAfterMilliseconds, int ExitCode, bool IgnoreQuit, bool Crash,
    int QuitDelayMilliseconds = 0, string[]? QuitStderr = null, bool ProgressCommands = false);
internal sealed record Event(int AtMilliseconds, string Channel, string Text);
internal static class Native
{
    [DllImport("kernel32.dll")]
    internal static extern uint SetErrorMode(uint mode);
}
