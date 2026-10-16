using BroadcastRouter.StandbyCheck;
using Xunit;

namespace BroadcastRouter.Tests;

public sealed class StandbyQualificationTests
{
    [Theory]
    [InlineData("forced")]
    [InlineData("nonzero")]
    [InlineData("missing")]
    [InlineData("remaining")]
    [InlineData("late")]
    public void SupervisorRunnerAbortsOperationalFailureButKeepsAllCleanTimingMisses(string fault)
    {
        var method = typeof(QualificationContract).GetMethod("AcceptSupervisorGroup");
        Assert.NotNull(method);
        var visited = 0;
        void Run()
        {
            for (var group = 0; group < 3; group++)
            {
                visited++;
                var passed = (bool)method.Invoke(null, [5, fault == "missing" ? 4 : 5,
                    fault == "nonzero" ? 4 : 5, fault == "forced" ? 1 : 0,
                    fault == "remaining" ? 1 : 0, 1001.0])!;
                Assert.False(passed); // Clean late groups remain failures, never green.
            }
        }
        if (fault == "late") { Run(); Assert.Equal(3, visited); }
        else { Assert.Throws<System.Reflection.TargetInvocationException>(Run); Assert.Equal(1, visited); }
    }

    [Theory]
    [InlineData("forced")]
    [InlineData("nonzero")]
    [InlineData("missing")]
    [InlineData("quit")]
    [InlineData("late")]
    public void DirectRunnerAbortsOperationalFailureButKeepsAllCleanTimingMisses(string fault)
    {
        var method = typeof(QualificationContract).GetMethod("AcceptReceipts");
        Assert.NotNull(method);
        var visited = 0;
        void Run()
        {
            for (var group = 0; group < 3; group++)
            {
                visited++;
                var rows = Enumerable.Range(0, 5).Select(_ => new StopReceipt(Guid.NewGuid(), true, false, 0, 1, 1000, 1001, 1002)).ToList();
                if (fault == "forced") rows[0] = rows[0] with { Forced = true };
                if (fault == "nonzero") rows[0] = rows[0] with { ExitCode = -5 };
                if (fault == "missing") rows.RemoveAt(0);
                if (fault == "quit") rows[0] = rows[0] with { QuitDelivered = false };
                Assert.False((bool)method.Invoke(null, [5, rows])!);
            }
        }
        if (fault == "late") { Run(); Assert.Equal(3, visited); }
        else { Assert.Throws<System.Reflection.TargetInvocationException>(Run); Assert.Equal(1, visited); }
    }
    [Fact]
    public void ExactBaselineRequiresMatchingInputNotOnlyMatchingFilterGraph()
    {
        var method = typeof(QualificationContract).GetMethod("VerifyBaselineInput");
        Assert.NotNull(method);
        Assert.Throws<System.Reflection.TargetInvocationException>(() => method.Invoke(null,
            ["testsrc2=size=1920x1080:rate=50/1", new[] { "-re", "-f", "lavfi", "-i", "smptehdbars=size=1920x1080:rate=50/1" }]));
        method.Invoke(null, ["testsrc2=size=1920x1080:rate=50/1", new[] { "-re", "-f", "lavfi", "-i", "testsrc2=size=1920x1080:rate=50/1" }]);
    }
    [Fact]
    public void CpuSamplesRefreshCachedProcessTimesBeforeReading()
    {
        var method = typeof(QualificationContract).GetMethod("ReadCpuSeconds");
        Assert.NotNull(method);
        double cached = 1;
        var sample = (double)method.Invoke(null, [(Action)(() => cached = 3), (Func<double>)(() => cached)])!;
        Assert.Equal(3, sample);
    }
    [Theory]
    [InlineData("decklink")]
    [InlineData("rtmp://localhost/live")]
    [InlineData("file")]
    public void NonLocalSinksAreRefused(string sink) =>
        Assert.Throws<ArgumentException>(() => QualificationContract.ValidatePlan(5, sink));

    [Theory]
    [InlineData("missing")]
    [InlineData("duplicate")]
    [InlineData("forced")]
    [InlineData("no-exit")]
    [InlineData("late")]
    [InlineData("no-ready")]
    [InlineData("unordered")]
    [InlineData("quit-failed")]
    public void IncompleteOrFailedReceiptsCannotPass(string fault)
    {
        var rows = Enumerable.Range(0, 5).Select(_ => new StopReceipt(Guid.NewGuid(), true, false, 0, 2, 100, 110, 120)).ToList();
        if (fault == "missing") rows.RemoveAt(0);
        if (fault == "duplicate") rows[0] = rows[1];
        if (fault == "forced") rows[0] = rows[0] with { Forced = true };
        if (fault == "no-exit") rows[0] = rows[0] with { ExitCode = null };
        if (fault == "late") rows[0] = rows[0] with { ReapedMilliseconds = 1000 };
        if (fault == "no-ready") rows[0] = rows[0] with { Ready = false };
        if (fault == "unordered") rows[0] = rows[0] with { DrainMilliseconds = 99 };
        if (fault == "quit-failed") rows[0] = rows[0] with { QuitDelivered = false };
        Assert.Throws<InvalidDataException>(() => QualificationContract.ValidateReceipts(5, rows));
    }

    [Fact]
    public void SupervisorReceiptMustProveEveryStartedSessionExitedCleanly()
    {
        var method = typeof(QualificationContract).GetMethod("ValidateSupervisorGroup");
        Assert.NotNull(method);
        Assert.Throws<System.Reflection.TargetInvocationException>(() => method.Invoke(null, [5, 5, 4, 0, 0, 500.0]));
        Assert.Throws<System.Reflection.TargetInvocationException>(() => method.Invoke(null, [5, 5, 5, 1, 0, 500.0]));
        Assert.Throws<System.Reflection.TargetInvocationException>(() => method.Invoke(null, [5, 5, 5, 0, 1, 500.0]));
        Assert.Throws<System.Reflection.TargetInvocationException>(() => method.Invoke(null, [5, 5, 5, 0, 0, 1000.0]));
        method.Invoke(null, [5, 5, 5, 0, 0, 500.0]);
    }

    [Fact]
    public async Task StopBarrierDoesNotSerializeOwners()
    {
        var admitted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var count = 0;
        async Task Stop() { if (Interlocked.Increment(ref count) == 8) admitted.SetResult(); await admitted.Task; }
        await QualificationContract.StopTogetherAsync(Enumerable.Range(0, 8).Select(_ => (Func<Task>)Stop)).WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(8, count);
    }
}
