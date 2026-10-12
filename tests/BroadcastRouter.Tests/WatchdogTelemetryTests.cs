using System.Text.Json;
using BroadcastRouter.Application;
using BroadcastRouter.Infrastructure;
using BroadcastRouter.Web.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BroadcastRouter.Tests;

public sealed class WatchdogTelemetryTests
{
    [Theory]
    [InlineData("healthy")]
    [InlineData("async-stall")]
    [InlineData("synchronous-stall")]
    public async Task TerminationEvidenceIsDrainedBeforeFakeTerminationAndSlowDatabaseIsBounded(string databaseMode)
    {
        var root = Path.Combine(Path.GetTempPath(), "router-watchdog-log-" + Guid.NewGuid().ToString("N"));
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            var clock = new SupervisionClock(new ManualTimeProvider());
            await using var telemetry = new SupervisionTelemetry(root, clock, readVolumeSpace: TelemetryTestVolume.Read);
            using var coordinator = new RouterCoordinator(null!, null!, null!, NullLogger<RouterCoordinator>.Instance, clock, telemetry);
            var calls = 0;
            using var watchdog = new RouterCoordinatorWatchdog(coordinator, null!, NullLogger<RouterCoordinatorWatchdog>.Instance, clock, telemetry)
            {
                LogBudget = TimeSpan.FromMilliseconds(100),
                PersistRecoveryRecordForTesting = _ =>
                {
                    if (databaseMode == "synchronous-stall") release.Task.GetAwaiter().GetResult();
                    return databaseMode == "async-stall" ? release.Task : Task.CompletedTask;
                },
                TerminateProcess = _ => calls++
            };
            await Task.Run(() => watchdog.TerminateForStallAsync("synthetic stall", "CoordinatorStall", new(ProgressAgeMilliseconds: 121000),
                TimeSpan.FromMinutes(2), CancellationToken.None)).WaitAsync(TimeSpan.FromSeconds(2));
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
}
