using System.Text.Json;
using System.Collections.Concurrent;
using BroadcastRouter.Application;
using BroadcastRouter.Infrastructure;
using BroadcastRouter.Web.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging;
using Xunit;

namespace BroadcastRouter.Tests;

public sealed class WatchdogTelemetryTests
{
    [Theory]
    [InlineData("healthy")]
    [InlineData("healthy-slow-disk")]
    [InlineData("async-stall")]
    [InlineData("synchronous-stall")]
    public async Task TerminationEvidenceIsDrainedBeforeFakeTerminationAndSlowDatabaseIsBounded(string databaseMode)
    {
        var root = Path.Combine(Path.GetTempPath(), "router-watchdog-log-" + Guid.NewGuid().ToString("N"));
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            var clock = new SupervisionClock(new ManualTimeProvider());
            await using var telemetry = new SupervisionTelemetry(root, clock, readVolumeSpace: TelemetryTestVolume.Read)
            {
                // A bounded, successful append can exceed the fixture's old
                // 100ms budget without being a database stall or storage fault.
                BeforeWriteForTesting = databaseMode == "healthy-slow-disk" ? () => Task.Delay(250) : null
            };
            using var coordinator = new RouterCoordinator(null!, null!, null!, NullLogger<RouterCoordinator>.Instance, clock, telemetry);
            var calls = 0;
            using var watchdog = new RouterCoordinatorWatchdog(coordinator, null!, NullLogger<RouterCoordinatorWatchdog>.Instance, clock, telemetry)
            {
                PersistRecoveryRecordForTesting = _ =>
                {
                    if (databaseMode == "synchronous-stall") release.Task.GetAwaiter().GetResult();
                    return databaseMode == "async-stall" ? release.Task : Task.CompletedTask;
                },
                TerminateProcess = _ => calls++
            };
            await Task.Run(() => watchdog.TerminateForStallAsync("synthetic stall", "CoordinatorStall", new(ProgressAgeMilliseconds: 121000),
                TimeSpan.FromMinutes(2), CancellationToken.None)).WaitAsync(TimeSpan.FromSeconds(7));
            Assert.Equal(1, calls);
            // The queue is flushed before the database wait, so a hung DB cannot consume the evidence budget first.
            var entries = Directory.GetFiles(root, "*.decisions.*.jsonl").SelectMany(File.ReadAllLines).ToArray();
            Assert.Single(entries);
            using var document = JsonDocument.Parse(entries[0]);
            var value = document.RootElement.GetProperty("Event");
            Assert.Equal("HostFailFast", value.GetProperty("Action").GetString());
            Assert.Equal("TerminationRequestedNotConfirmed", value.GetProperty("Result").GetString());
            Assert.Equal(120000, value.GetProperty("WindowMilliseconds").GetDouble());
        }
        finally { release.TrySetResult(); if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task BlockedTelemetryStillHonorsDeadlineAndReportsIncompleteEvidence()
    {
        var root = Path.Combine(Path.GetTempPath(), "router-watchdog-log-" + Guid.NewGuid().ToString("N"));
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            var clock = new SupervisionClock(new ManualTimeProvider());
            await using var telemetry = new SupervisionTelemetry(root, clock, readVolumeSpace: TelemetryTestVolume.Read)
                { BeforeWriteForTesting = () => release.Task };
            using var coordinator = new RouterCoordinator(null!, null!, null!, NullLogger<RouterCoordinator>.Instance, clock, telemetry);
            var logger = new CapturingLogger();
            var calls = 0;
            using var watchdog = new RouterCoordinatorWatchdog(coordinator, null!, logger, clock, telemetry)
            {
                LogBudget = TimeSpan.FromMilliseconds(100),
                PersistRecoveryRecordForTesting = _ => Task.CompletedTask,
                TerminateProcess = _ => calls++
            };
            try
            {
                await watchdog.TerminateForStallAsync("synthetic stall", "CoordinatorStall", new(),
                    TimeSpan.FromMinutes(2), CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2));
                Assert.Equal(1, calls);
                Assert.Equal(0, telemetry.Status.Written);
                Assert.Contains(logger.Messages, message => message.Contains("termination evidence may be incomplete", StringComparison.Ordinal));
            }
            finally { release.TrySetResult(); }
            await telemetry.FlushAsync().WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(1, telemetry.Status.Written);
        }
        finally { release.TrySetResult(); if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    private sealed class CapturingLogger : ILogger<RouterCoordinatorWatchdog>
    {
        public ConcurrentQueue<string> Messages { get; } = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => true;
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => Messages.Enqueue(formatter(state, exception));
    }
}
