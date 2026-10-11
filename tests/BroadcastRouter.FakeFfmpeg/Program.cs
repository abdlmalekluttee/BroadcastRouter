using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;

// Test-only child: no network, media devices or application endpoints.
if (args.Length != 1) return 64;
if (OperatingSystem.IsWindows()) Native.SetErrorMode(0x0002 | 0x8000);
var script = JsonSerializer.Deserialize<Script>(await File.ReadAllTextAsync(args[0]),
    new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? throw new InvalidDataException("Missing script");
if (script.Events.Any(e => e.AtMilliseconds is < 0 or > 60000)
    || script.ExitAfterMilliseconds is < 0 or > 60000) return 65;
using var complete = new CancellationTokenSource();
var timer = Stopwatch.StartNew();
var quit = Task.Run(async () =>
{
    while (await Console.In.ReadLineAsync() is { } line)
        if (line == "q" && !script.IgnoreQuit) { complete.Cancel(); return; }
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

internal sealed record Script(Event[] Events, int? ExitAfterMilliseconds, int ExitCode, bool IgnoreQuit, bool Crash);
internal sealed record Event(int AtMilliseconds, string Channel, string Text);
internal static class Native
{
    [DllImport("kernel32.dll")]
    internal static extern uint SetErrorMode(uint mode);
}
