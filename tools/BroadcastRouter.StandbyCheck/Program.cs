using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using BroadcastRouter.Domain;
using BroadcastRouter.Infrastructure;
using BroadcastRouter.StandbyCheck;

// Local qualification only. No arbitrary argv, URL input or DeckLink sink option.
var arguments = args.Chunk(2).ToDictionary(a => a[0], a => a.Length == 2 ? a[1] : throw new ArgumentException("Missing option value."));
string Required(string key) => arguments.TryGetValue(key, out var value) ? value : throw new ArgumentException("Missing " + key);
var executable = Path.GetFullPath(Required("--ffmpeg"));
var concurrency = int.Parse(Required("--concurrency"));
var sink = Required("--sink");
QualificationContract.ValidatePlan(concurrency, sink);
if (sink != "null") throw new ArgumentException("Capture qualification is a separate bounded operation; this runner accepts null only.");
var variant = Required("--variant");
if (variant is not ("production" or "static" or "static-clock")) throw new ArgumentException("Unknown graph variant.");
var ownership = Required("--ownership");
if (ownership is not ("direct" or "contained" or "supervisor")) throw new ArgumentException("Unknown ownership mode.");
var root = Path.GetFullPath(Required("--evidence-root"));
if (Directory.Exists(root)) throw new IOException("Evidence directory must be new.");
var iterations = arguments.TryGetValue("--iterations", out var iterationText) ? int.Parse(iterationText) : 1;
if (iterations is < 1 or > 10) throw new ArgumentException("One through ten iterations required.");
var logo = arguments.TryGetValue("--logo", out var logoPath) ? Path.GetFullPath(logoPath) : null;
if (!File.Exists(executable) || logo is not null && !File.Exists(logo)) throw new FileNotFoundException();
var preset = OutputPresetProfile.CommonDefaults().Single(p => p.Id == "1080i50").ToDomain();
var port = new DeckLinkPort("LOCAL-ONLY", "LOCAL-ONLY", "Synthetic card", 0, 0, null, [], CardFriendlyName: "Qualification card");
var configuration = new PortStandbyConfiguration(StandbyPattern.TestSource, logo, "Qualification output", true);
string? expectedInput = null;
if (arguments.TryGetValue("--configuration", out var configPath))
{
    var config = JsonSerializer.Deserialize<GraphConfiguration>(await File.ReadAllTextAsync(configPath)) ?? throw new InvalidDataException();
    port = config.Port; preset = config.Preset; configuration = config.Configuration with { LogoPath = logo };
    expectedInput = config.ExpectedInputGraph;
    if (string.IsNullOrWhiteSpace(expectedInput)) throw new InvalidDataException("Private baseline configuration must record the expected original input.");
}
Directory.CreateDirectory(root);
PreparedStandbySlate? prepared = null;
if (variant != "production")
{
    configuration = configuration with { ShowClock = variant == "static-clock" };
    var rendering = Stopwatch.StartNew();
    prepared = await new StandbySlateCache(Path.Combine(root, "cache")).PrepareAsync(new(executable), port, preset, configuration, default);
    await File.WriteAllTextAsync(Path.Combine(root, "render.json"), JsonSerializer.Serialize(new { prepared.Key, Milliseconds = rendering.Elapsed.TotalMilliseconds }));
}
var executableHash = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(executable)));
await File.WriteAllTextAsync(Path.Combine(root, "plan.json"), JsonSerializer.Serialize(new
{ StartedUtc = DateTimeOffset.UtcNow, executableHash, concurrency, sink, variant, ownership, iterations, logicalProcessors = Environment.ProcessorCount,
  expectedInput, checkerMvid = typeof(QualificationContract).Module.ModuleVersionId, infrastructureMvid = typeof(FfmpegCommandBuilder).Module.ModuleVersionId,
  substitutions = "Local logo path and null/rawvideo output only; exact original graph recorded privately in each argv file." }));
for (var iteration = 1; iteration <= iterations; iteration++)
{
    var children = new List<CheckChild>();
    var receipts = new List<StopReceipt>();
    var group = Path.Combine(root, "group-" + iteration); Directory.CreateDirectory(group);
    var started = DateTimeOffset.UtcNow;
    try
    {
        var starts = new List<ProcessStartInfo>();
        for (var index = 0; index < concurrency; index++)
        {
            var options = new FfmpegRouteOptions(executable, UseWindowsDeckLinkSafeTerminate: true);
            var start = prepared is null ? FfmpegCommandBuilder.BuildPortStandby(options, port, preset, configuration)
                : FfmpegCommandBuilder.BuildPreparedStandby(options, port, preset, configuration, prepared);
            var tokens = start.ArgumentList;
            if (variant == "production" && expectedInput is not null) QualificationContract.VerifyBaselineInput(expectedInput, tokens.ToArray());
            if (tokens[^3] != "-f" || tokens[^2] != "decklink") throw new InvalidDataException("Unexpected output graph.");
            for (var i = 0; i < 3; i++) tokens.RemoveAt(tokens.Count - 1);
            if (tokens[^2] == "-win_safe_terminate") { tokens.RemoveAt(tokens.Count - 1); tokens.RemoveAt(tokens.Count - 1); }
            foreach (var token in new[] { "-c:v", "rawvideo", "-threads:v", "1", "-f", "null", "-" }) tokens.Add(token);
            await File.WriteAllTextAsync(Path.Combine(group, index + ".argv.json"), JsonSerializer.Serialize(tokens));
            starts.Add(start);
            if (ownership != "supervisor") children.Add(new CheckChild(start, ownership == "contained", Path.Combine(group, index.ToString())));
        }
        if (ownership == "supervisor") { await SupervisorCohort.Run(starts, group); continue; }
        await Task.WhenAll(children.Select(c => c.Ready.Task)).WaitAsync(TimeSpan.FromSeconds(30));
        var baseline = children.Select(c => c.CpuSeconds()).ToArray();
        var framesBefore = children.Select(c => c.Frames).ToArray();
        var measured = Stopwatch.StartNew();
        await Task.Delay(TimeSpan.FromSeconds(10)); // fixed measurement interval, not a correctness wait
        var elapsed = measured.Elapsed.TotalSeconds;
        var metrics = children.Select((c, i) => new { c.Owner, c.ProcessId, c.ProcessStartedUtc, c.StartedUtc, c.ReadyUtc, CpuBefore = baseline[i], CpuAfter = c.CpuSeconds(),
            PeakWorkingSet = c.Cpu.PeakWorkingSet64, FramesBefore = framesBefore[i], Frames = c.Frames }).ToArray();
        if (metrics.Any(m => m.Frames - m.FramesBefore < 200)) throw new InvalidDataException("Output did not continue advancing at the required cadence.");
        var stop = Stopwatch.GetTimestamp();
        await QualificationContract.StopTogetherAsync(children.Select(c => (Func<Task>)(async () =>
        {
            var receipt = await c.StopAsync(stop);
            lock (receipts) receipts.Add(receipt);
        })));
        await File.WriteAllTextAsync(Path.Combine(group, "result.json"), JsonSerializer.Serialize(new
        { StartedUtc = started, EndedUtc = DateTimeOffset.UtcNow, elapsed, metrics, receipts, GroupStopMilliseconds = Stopwatch.GetElapsedTime(stop).TotalMilliseconds }, new JsonSerializerOptions { WriteIndented = true }));
        if (!QualificationContract.AcceptReceipts(concurrency, receipts))
        {
            await File.WriteAllTextAsync(Path.Combine(group, "target-failure.json"), JsonSerializer.Serialize(new { error = "Clean stop exceeded the unchanged one-second target." }));
            Environment.ExitCode = 1; // Complete the predeclared cohort; retain every failed barrier, never rerun it.
        }
        Console.WriteLine($"Group {iteration}: {receipts.Count} exits, max reap {receipts.Max(r => r.ReapedMilliseconds):F3} ms, core/standby {metrics.Average(m => (m.CpuAfter - m.CpuBefore) / elapsed):F3}");
    }
    catch (Exception error)
    {
        await File.WriteAllTextAsync(Path.Combine(group, "failure.json"), JsonSerializer.Serialize(new { Utc = DateTimeOffset.UtcNow, error = error.ToString() }));
        throw; // Never silently retry a failed group.
    }
    finally { foreach (var child in children) await child.DisposeAsync(); }
}

public sealed record GraphConfiguration(DeckLinkPort Port, OutputPreset Preset, PortStandbyConfiguration Configuration, string? ExpectedInputGraph = null);

internal sealed class CheckChild : IAsyncDisposable
{
    private readonly Process? _direct;
    private readonly OwnedChildProcess? _contained;
    private readonly StreamWriter _input;
    private readonly Task _stdout, _stderr;
    public Process Cpu { get; }
    public int ProcessId { get; }
    public DateTimeOffset ProcessStartedUtc { get; }
    public DateTimeOffset StartedUtc { get; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? ReadyUtc { get; private set; }
    private bool _disposed;
    public Guid Owner { get; } = Guid.NewGuid();
    public TaskCompletionSource Ready { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public long Frames;
    public CheckChild(ProcessStartInfo start, bool contained, string prefix)
    {
        if (contained) { _contained = OwnedChildProcess.Start(start); Cpu = Process.GetProcessById(_contained.Id); }
        else { _direct = Process.Start(start) ?? throw new IOException("Start failed."); Cpu = _direct; }
        ProcessId = Cpu.Id; ProcessStartedUtc = Cpu.StartTime.ToUniversalTime();
        _input = _contained?.StandardInput ?? _direct!.StandardInput;
        _stdout = Pump(_contained?.StandardOutput ?? _direct!.StandardOutput, prefix + ".stdout.txt", true);
        _stderr = Pump(_contained?.StandardError ?? _direct!.StandardError, prefix + ".stderr.txt", false);
    }
    private async Task Pump(StreamReader reader, string path, bool progress)
    {
        await using var output = new StreamWriter(new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read));
        while (await reader.ReadLineAsync() is { } line)
        {
            await output.WriteLineAsync(line);
            if (progress && line.StartsWith("frame=") && long.TryParse(line.AsSpan(6), out var frame))
            { Frames = frame; if (frame >= 50 && !Ready.Task.IsCompleted) { ReadyUtc = DateTimeOffset.UtcNow; Ready.TrySetResult(); } }
        }
        if (progress && !Ready.Task.IsCompleted) Ready.TrySetException(new IOException("Exit before advancing output."));
    }
    private Task Exit() => _contained?.WaitForExitAsync() ?? _direct!.WaitForExitAsync();
    private bool HasExited => _contained?.HasExited ?? _direct!.HasExited;
    private void Kill() { if (_contained is not null) _contained.Kill(true); else _direct!.Kill(true); }
    public double CpuSeconds() => QualificationContract.ReadCpuSeconds(Cpu.Refresh, () => Cpu.TotalProcessorTime.TotalSeconds);
    public async Task<StopReceipt> StopAsync(long barrier)
    {
        var forced = false;
        var delivered = true;
        try { await _input.WriteLineAsync("q").WaitAsync(TimeSpan.FromMilliseconds(250)); await _input.FlushAsync().WaitAsync(TimeSpan.FromMilliseconds(250)); }
        catch (Exception) { delivered = false; }
        var quit = Stopwatch.GetElapsedTime(barrier).TotalMilliseconds;
        try { await Exit().WaitAsync(TimeSpan.FromSeconds(5)); }
        catch (TimeoutException) { forced = true; Kill(); await Exit().WaitAsync(TimeSpan.FromSeconds(5)); }
        var exited = Stopwatch.GetElapsedTime(barrier).TotalMilliseconds;
        await Task.WhenAll(_stdout, _stderr).WaitAsync(TimeSpan.FromSeconds(5));
        var drained = Stopwatch.GetElapsedTime(barrier).TotalMilliseconds;
        var code = _contained?.ExitCode ?? _direct!.ExitCode;
        await DisposeAsync();
        return new(Owner, Ready.Task.IsCompletedSuccessfully, forced, code, quit, exited, drained, Stopwatch.GetElapsedTime(barrier).TotalMilliseconds, delivered);
    }
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        try { if (!HasExited) Kill(); await Exit().WaitAsync(TimeSpan.FromSeconds(5)); await Task.WhenAll(_stdout, _stderr).WaitAsync(TimeSpan.FromSeconds(5)); }
        finally { if (_contained is not null) { _contained.Dispose(); Cpu.Dispose(); } else _direct!.Dispose(); _disposed = true; }
    }
}
