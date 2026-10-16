using BroadcastRouter.Domain;
using System.Diagnostics;
using Xunit;

namespace BroadcastRouter.Tests;

public sealed class CoordinatorIsolationTests
{
    [Fact]
    public async Task InvalidCommandMediaEndpointExitsWithTheOriginalFailure()
    {
        await using var fixture = new SupervisorMediaEvidenceTests.MediaChildFixture();
        fixture.EnableCommandedMedia();
        using var child = Process.Start(new ProcessStartInfo(fixture.Executable)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            ArgumentList = { "-progress", "pipe:1", "-stats_enc_pre:v:0", "invalid-test-endpoint" }
        })!;
        var stdout = child.StandardOutput.ReadToEndAsync();
        var stderr = child.StandardError.ReadToEndAsync();
        try
        {
            await child.StandardInput.WriteLineAsync("media 0 1 0");
            await child.StandardInput.FlushAsync();
            await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            Assert.NotEqual(0, child.ExitCode);
            Assert.Contains("Invalid synthetic observation endpoint", await stderr);
        }
        finally
        {
            if (!child.HasExited) child.Kill(entireProcessTree: true);
            await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            await Task.WhenAll(stdout, stderr).WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [Fact]
    public async Task QualificationDoesNotDependOnRemainingWallClockScript()
    {
        await using var fixture = new SupervisorMediaEvidenceTests.MediaChildFixture();
        await using var lab = await CoordinatorIndependentMediaTests.Lab.Create(fixture, true);
        await lab.Observed(() => lab.Owner.RecentErrors.Any(e => e.Contains("Synthetic observation batch complete")));
        lab.Time.Advance(TimeSpan.FromSeconds(25));
        await lab.Qualify();
        Assert.All(lab.Owner.IndependentMedia!.Signals, signal => Assert.Equal(MediaSignalState.Healthy, signal.State));
    }

    [Fact]
    public async Task EachMediaFixtureOwnsAnIndependentExecutableCopy()
    {
        await using var one = new SupervisorMediaEvidenceTests.MediaChildFixture();
        await using var two = new SupervisorMediaEvidenceTests.MediaChildFixture();
        Assert.NotEqual(one.Executable, two.Executable);
        Assert.NotEqual(Path.Combine(FaultHarnessTests.RepositoryRoot(), "tests", "BroadcastRouter.FakeFfmpeg",
            "bin", "Release", "net8.0", "BroadcastRouter.FakeFfmpeg.exe"), one.Executable);
        using var exclusive = File.Open(one.Executable, FileMode.Open, FileAccess.Read, FileShare.None);
        using var independent = File.Open(two.Executable, FileMode.Open, FileAccess.Read, FileShare.None);
    }
}
