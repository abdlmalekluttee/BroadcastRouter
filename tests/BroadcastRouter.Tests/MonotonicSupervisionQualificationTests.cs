using System.Collections.Concurrent;
using System.Diagnostics;
using BroadcastRouter.Application;
using BroadcastRouter.Domain;
using BroadcastRouter.Infrastructure;
using Xunit;

namespace BroadcastRouter.Tests;

public sealed class MonotonicSupervisionQualificationTests
{
    // Break caught: using wall time at a real supervision/evidence boundary.
    [Theory]
    [InlineData(2, false, SourceMediaExpectation.ContinuousVideo)]
    [InlineData(-2, false, SourceMediaExpectation.ContinuousVideo)]
    [InlineData(2, true, SourceMediaExpectation.ContinuousVideo)]
    [InlineData(-2, true, SourceMediaExpectation.ContinuousVideo)]
    [InlineData(2, false, SourceMediaExpectation.AudioOnly)]
    [InlineData(-2, false, SourceMediaExpectation.AudioOnly)]
    [InlineData(2, true, SourceMediaExpectation.AudioOnly)]
    [InlineData(-2, true, SourceMediaExpectation.AudioOnly)]
    [InlineData(2, false, SourceMediaExpectation.SparseVideo)]
    [InlineData(-2, false, SourceMediaExpectation.SparseVideo)]
    [InlineData(2, true, SourceMediaExpectation.SparseVideo)]
    [InlineData(-2, true, SourceMediaExpectation.SparseVideo)]
    public async Task ClockStepPreservesQualifiedOwnersAndMediaMeaning(int step, bool fast,
        SourceMediaExpectation expectation)
    {
        var sparse = expectation == SourceMediaExpectation.SparseVideo;
        await using var fixture = new SupervisorMediaEvidenceTests.MediaChildFixture(
            expectation: expectation, stopInputVideoAfterBatch: sparse ? 30 : null);
        await using var lab = await CoordinatorIndependentMediaTests.Lab.Create(fixture, true);
        lab.Time.Advance(TimeSpan.FromSeconds(25));
        await lab.Qualify();
        if (sparse)
        {
            lab.Time.Advance(TimeSpan.FromSeconds(5));
            await lab.Qualify(includeInputVideo: false);
        }
        await lab.Progress(1000);
        lab.Routes[fixture.Source.Identity.Value] = lab.Route with { State = RouteState.Running };
        var before = lab.Owner;
        var signals = before.IndependentMedia!.Signals;
        if (expectation == SourceMediaExpectation.AudioOnly)
            Assert.DoesNotContain(signals, s => s.Kind is MediaSignalKind.InputVideo or MediaSignalKind.OutputVideo);
        if (sparse) Assert.Equal(MediaSignalState.Unknown, signals.Single(s => s.Kind == MediaSignalKind.InputVideo).State);
        Assert.All(signals.Where(s => !sparse || s.Kind != MediaSignalKind.InputVideo),
            s => Assert.Equal(MediaSignalState.Healthy, s.State));

        lab.Time.StepUtc(TimeSpan.FromSeconds(step));
        Assert.Equal(signals.Select(s => s.State), lab.Owner.IndependentMedia!.Signals.Select(s => s.State));
        if (fast) await lab.FastInputTurn(); else await lab.Monitor();
        Assert.True(lab.Owner.Running);
        Assert.Equal(before.SessionId, lab.Owner.SessionId);
        Assert.Equal(before.ProcessId, lab.Owner.ProcessId);
        Assert.Equal(0, lab.Route.RestartCount);
        Assert.False(lab.Owner.IndependentMedia!.MayRecover);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(-2)]
    public async Task StaleMediaRecoversAtFourElapsedSecondsNotUtcDeadline(int step)
    {
        await using var fixture = new SupervisorMediaEvidenceTests.MediaChildFixture();
        await using var lab = await CoordinatorIndependentMediaTests.Lab.Create(fixture, true);
        lab.Time.Advance(TimeSpan.FromSeconds(25));
        await lab.Qualify();
        await lab.Progress(1000);
        lab.Routes[fixture.Source.Identity.Value] = lab.Route with { State = RouteState.Running };
        var owner = lab.Owner.SessionId;
        lab.Time.Advance(TimeSpan.FromMilliseconds(3900));
        lab.Time.StepUtc(TimeSpan.FromSeconds(step));
        Assert.False(lab.Owner.IndependentMedia!.MayRecover);
        await lab.Monitor();
        Assert.True(lab.Owner.Running);
        Assert.Equal(owner, lab.Owner.SessionId);
        lab.Time.Advance(TimeSpan.FromMilliseconds(100));
        Assert.True(lab.Owner.IndependentMedia!.ConfirmedMediaFailure);
        await lab.Monitor();
        Assert.False(lab.Owner.Running);
        Assert.Equal(1, lab.Route.RestartCount);
        Assert.Equal("MediaAdvancementLost", lab.Route.FailureCategory);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(-2)]
    public void PublisherRetryAndCircuitBoundariesIgnoreUtcSteps(int step)
    {
        var time = new ManualTimeProvider();
        var clock = new SupervisionClock(time);
        var publisher = new PublisherLiveHoldPolicy(clock);
        publisher.Observe("synthetic", true);
        time.Advance(TimeSpan.FromMilliseconds(9900));
        time.StepUtc(TimeSpan.FromSeconds(step));
        Assert.Equal(MediaSignalState.Healthy, publisher.Presence("synthetic"));
        time.Advance(TimeSpan.FromMilliseconds(100));
        Assert.Equal(MediaSignalState.Unknown, publisher.Presence("synthetic"));

        var retry = new StandbyRetryBudget();
        retry.RecordFailure(Guid.NewGuid(), clock.Now, "synthetic", 0);
        time.Advance(TimeSpan.FromMilliseconds(900));
        time.StepUtc(TimeSpan.FromSeconds(step));
        Assert.False(retry.CanAttempt(clock.Now));
        time.Advance(TimeSpan.FromMilliseconds(100));
        Assert.True(retry.CanAttempt(clock.Now));
        retry.RecordFailure(Guid.NewGuid(), clock.Now, "synthetic", 0);
        retry.RecordFailure(Guid.NewGuid(), clock.Now, "synthetic", 0);
        Assert.True(retry.CircuitOpen);
        time.Advance(TimeSpan.FromMilliseconds(299900));
        time.StepUtc(TimeSpan.FromSeconds(step));
        Assert.False(retry.CanAttempt(clock.Now));
        time.Advance(TimeSpan.FromMilliseconds(100));
        Assert.True(retry.CanAttempt(clock.Now));
    }

    [Theory]
    [InlineData(2)]
    [InlineData(-2)]
    public async Task UtcStepMovesPublicDatesButNotTimersOrRemainingDeadline(int step)
    {
        var time = new ManualTimeProvider();
        var clock = new SupervisionClock(time);
        using var timer = clock.CreatePeriodicTimer(TimeSpan.FromSeconds(1));
        var tick = timer.WaitForNextTickAsync().AsTask();
        var delay = clock.DelayAsync(TimeSpan.FromSeconds(1), CancellationToken.None);
        var deadline = clock.Now.AddSeconds(30);
        var originalUtc = clock.UtcNow;
        time.StepUtc(TimeSpan.FromSeconds(step));
        Assert.Equal(originalUtc.AddSeconds(step), clock.UtcNow);
        Assert.False(tick.IsCompleted);
        Assert.False(delay.IsCompleted);
        var boundary = clock.CaptureBoundary();
        var stored = boundary.ToUtc(deadline);
        Assert.Equal(clock.UtcNow.AddSeconds(30), stored);
        Assert.Equal(deadline, boundary.FromUtc(stored));
        time.Advance(TimeSpan.FromSeconds(1));
        Assert.True(await tick.WaitAsync(TimeSpan.FromSeconds(1)));
        await delay.WaitAsync(TimeSpan.FromSeconds(1));
        var nextRun = new SupervisionClock(time);
        Assert.Equal(TimeSpan.FromSeconds(29), nextRun.CaptureBoundary().FromUtc(stored) - nextRun.Now);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(-2)]
    public async Task UtcStepDoesNotBypassStopGraceAndOwnedHelperTerminatesBoundedly(int step)
    {
        var time = new ManualTimeProvider();
        var clock = new SupervisionClock(time);
        await using var fixture = new GracefulStopTests.ChildFixture(new
        {
            events = new[] { new { atMilliseconds = 0, channel = "stderr", text = "ready" } }, ignoreQuit = true
        });
        await using var supervisor = new FfmpegProcessSupervisor(new("unused"), TimeSpan.FromMilliseconds(300), clock);
        var events = new ConcurrentQueue<RouteProcessLifecycleEvent>();
        supervisor.LifecycleChanged += events.Enqueue;
        await fixture.Start(supervisor);
        var before = Assert.Single(supervisor.RunningSnapshot());
        using var exactOwner = Process.GetProcessById(before.ProcessId);
        _ = exactOwner.SafeHandle;
        var elapsed = Stopwatch.StartNew();
        var stop = supervisor.StopAsync(fixture.Source, CancellationToken.None);
        time.StepUtc(TimeSpan.FromSeconds(step));
        await stop.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(exactOwner.HasExited);
        Assert.InRange(elapsed.Elapsed.TotalMilliseconds, 250, 5000);
        Assert.Single(events, e => e.State == RouteProcessLifecycleState.ForcedTermination);
        Assert.False(Assert.Single(supervisor.Snapshot()).Running);
    }
}
