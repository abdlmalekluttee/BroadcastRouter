using System.Text;
using BroadcastRouter.Infrastructure;
using Xunit;

namespace BroadcastRouter.Tests;

public sealed class ProbeMarkerTests
{
    [Fact]
    public async Task AnotherProbeMarkerWriterCannotTurnValidMediaIntoFailure()
    {
        await using var lab = await OfflineFallbackTests.RecoveryLab.Create();
        await lab.InstallFakeMediaTools();
        var path = Path.Combine(lab.Root, "probe-starts.log");
        // Deterministically model the write handle held by a simultaneous child.
        using var otherWriter = new FileStream(path, FileMode.OpenOrCreate, FileAccess.Write, FileShare.Read);
        var start = new System.Diagnostics.ProcessStartInfo(lab.Settings.MediaTools.FfprobePath)
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add("-show_streams");
        start.ArgumentList.Add("-of");
        start.ArgumentList.Add("json");
        using var process = System.Diagnostics.Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(8));
        Assert.True(process.ExitCode == 0, await stderr);
        var result = FfprobeStreamProbe.Parse(await stdout);
        Assert.True(result.Opened, result.Detail);
        Assert.True(result.FramesReceived);
        Assert.True(result.DecodedAudioReceived);
        Assert.Equal(1, ProbeMarker.CountStarted(path));
    }

    [Fact]
    public async Task ConcurrentProbeStartsAreCountedExactlyWithoutMediaFailures()
    {
        await using var lab = await OfflineFallbackTests.RecoveryLab.Create();
        await lab.InstallFakeMediaTools();
        var probes = Enumerable.Range(0, 16).Select(_ =>
            new FfprobeStreamProbe(lab.Settings.MediaTools.FfprobePath, TimeSpan.FromSeconds(8))
                .ProbeAsync(new Uri("rtsp://example.invalid/fixture"), CancellationToken.None));
        var results = await Task.WhenAll(probes);
        Assert.All(results, result => Assert.True(result.Opened && result.FramesReceived && result.DecodedAudioReceived, result.Detail));
        Assert.Equal(16, ProbeMarker.CountStarted(Path.Combine(lab.Root, "probe-starts.log")));
    }

    [Fact]
    public void ProbeStartCanBeObservedWhileChildStillOwnsAppendHandle()
    {
        var path = Path.Combine(Path.GetTempPath(), "probe-marker-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var writer = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
            writer.Write(Encoding.UTF8.GetBytes("1234\n56"));
            writer.Flush();
            // Exactly one complete child marker; no exception and no claim based on a partial line.
            Assert.Equal(1, ProbeMarker.CountStarted(path));
            writer.Write(Encoding.UTF8.GetBytes("78\n"));
            writer.Flush();
            Assert.Equal(2, ProbeMarker.CountStarted(path));
        }
        finally { File.Delete(path); }
    }
}
