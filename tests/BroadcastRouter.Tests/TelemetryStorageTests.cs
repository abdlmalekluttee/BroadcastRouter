using System.Diagnostics;
using System.Text.Json;
using BroadcastRouter.Application;
using BroadcastRouter.Domain;
using BroadcastRouter.Infrastructure;
using Xunit;

namespace BroadcastRouter.Tests;

public sealed class TelemetryStorageTests
{
    [Fact]
    public async Task ShortLivedSessionsShareCapacityInsteadOfEvictingRecentEvidence()
    {
        var root = Path.Combine(Path.GetTempPath(), "router-shared-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using var telemetry = new SupervisionTelemetry(root, maxFileBytes: 65536, maxFiles: 20, readVolumeSpace: TelemetryTestVolume.Read);
            for (var index = 0; index < 100; index++)
                telemetry.Stderr(Guid.NewGuid(), $"session-evidence-{index:D3}");
            await telemetry.FlushAsync();
            Assert.True(telemetry.Status.RetentionComplete, "Short-lived sessions must share segments, not evict fresh evidence by session count.");
            var retained = Directory.GetFiles(root, "*.jsonl").SelectMany(File.ReadAllLines).ToArray();
            Assert.Equal(100, retained.Length);
            Assert.InRange(Directory.GetFiles(root, "*.jsonl").Length, 1, 3);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task CapacityLossRemainsVisibleAfterTelemetryRestart()
    {
        var root = Path.Combine(Path.GetTempPath(), "router-retention-alarm-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using (var first = new SupervisionTelemetry(root, maxFileBytes: 16384, maxFiles: 2, readVolumeSpace: TelemetryTestVolume.Read))
            {
                for (var index = 0; index < 60; index++) first.Stderr(Guid.NewGuid(), new string('x', 2000));
                await first.FlushAsync();
                Assert.False(first.Status.RetentionComplete);
            }
            await using var next = new SupervisionTelemetry(root, maxFileBytes: 16384, maxFiles: 2, readVolumeSpace: TelemetryTestVolume.Read);
            Assert.False(next.Status.RetentionComplete, "An idle restart must not report healthy before loading the existing retention gap.");
            await FaultHarnessTests.Eventually(() => next.Status.CapacityEvictions > 0);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task InvalidRetentionMarkerIsUnhealthyWithoutAnyNewRecordOrFlush()
    {
        var root = Path.Combine(Path.GetTempPath(), "router-invalid-loss-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var marker = Path.Combine(root, "retention-loss.json");
            await File.WriteAllTextAsync(marker, "not valid state");
            await using var telemetry = new SupervisionTelemetry(root, readVolumeSpace: TelemetryTestVolume.Read);
            Assert.False(telemetry.Status.IsHealthy, "Unread retention state must not be reported healthy.");
            await FaultHarnessTests.Eventually(() => telemetry.Status.WriteFailures > 0);
            Assert.Equal("not valid state", await File.ReadAllTextAsync(marker));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task SessionReconstructionSurvivesInterleavingRotationAndServiceEpochs()
    {
        var root = Path.Combine(Path.GetTempPath(), "router-reconstruct-" + Guid.NewGuid().ToString("N"));
        var session = Guid.NewGuid();
        var foreignSession = Guid.NewGuid();
        try
        {
            string reference;
            Guid run;
            await using (var first = new SupervisionTelemetry(root, maxFileBytes: 16384, maxFiles: 100, readVolumeSpace: TelemetryTestVolume.Read))
            {
                run = first.ServiceRunId;
                reference = first.StderrReference(session);
                for (var index = 0; index < 100; index++)
                {
                    first.Stderr(session, $"wanted-{index:D3} " + new string('x', 600));
                    first.Stderr(foreignSession, "unrelated session");
                }
                await first.FlushAsync();
                Assert.True(first.Status.RetentionComplete);
            }
            await using (var second = new SupervisionTelemetry(root, maxFileBytes: 16384, maxFiles: 100, readVolumeSpace: TelemetryTestVolume.Read))
            {
                second.Stderr(session, "different service epoch");
                await second.FlushAsync();
            }
            var records = Directory.GetFiles(root, reference).SelectMany(File.ReadAllLines)
                .Select(line => JsonSerializer.Deserialize<JsonElement>(line))
                .Where(record => record.GetProperty("Event").GetProperty("ServiceRunId").GetGuid() == run
                    && record.GetProperty("Event").GetProperty("SessionId").GetGuid() == session)
                .OrderBy(record => record.GetProperty("Sequence").GetInt64()).ToArray();
            Assert.Equal(100, records.Length);
            Assert.Equal(100, records.Select(record => record.GetProperty("Sequence").GetInt64()).Distinct().Count());
            for (var index = 0; index < 100; index++)
                Assert.Equal($"wanted-{index:D3} " + new string('x', 600), records[index].GetProperty("Event").GetProperty("Text").GetString());
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData("Authorization: Basic FAKE_BASIC_PAYLOAD", "FAKE_BASIC_PAYLOAD")]
    [InlineData("Authorization: Bearer FAKE_BEARER_PAYLOAD", "FAKE_BEARER_PAYLOAD")]
    [InlineData("password='FAKE_PASSWORD' token=FAKE_TOKEN", "FAKE_PASSWORD")]
    [InlineData("rtsp://FAKE_USER:FAKE_PASSWORD@192.0.2.5/private-feed", "private-feed")]
    [InlineData("C:\\Users\\FAKE_PERSON\\private-file.txt", "FAKE_PERSON")]
    [InlineData("{\"password\":\"FAKE_JSON_PASSWORD\"}", "FAKE_JSON_PASSWORD")]
    [InlineData("{\"Authorization\":\"Basic FAKE_JSON_AUTH\"}", "FAKE_JSON_AUTH")]
    [InlineData("peer=2001:db8::1234", "2001:db8::1234")]
    [InlineData("{\"password\":\"prefix\\\"FAKE_SECRET_SUFFIX\"}", "FAKE_SECRET_SUFFIX")]
    [InlineData("peer=2001:db8::1234.", "2001:db8::1234")]
    [InlineData("peer=2001:db8::1234: connection failed", "2001:db8::1234")]
    public void PrivateTelemetryDoesNotRetainSecrets(string input, string forbidden) =>
        Assert.DoesNotContain(forbidden, TelemetryRedactor.Redact(input));

    [Fact]
    public async Task OwnedFaultCanBeReconstructedBeyondTheMemoryRing()
    {
        var directory = Path.Combine(Path.GetTempPath(), "router-telemetry-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var script = Path.Combine(directory, "child.json");
        var logs = Path.Combine(directory, "logs");
        var executable = Path.Combine(FaultHarnessTests.RepositoryRoot(), "tests", "BroadcastRouter.FakeFfmpeg", "bin", "Release", "net8.0", "BroadcastRouter.FakeFfmpeg.exe");
        var source = new SourceIdentity("TEST", "fixture", "_default_", "private-synthetic-name");
        try
        {
            await File.WriteAllTextAsync(script, JsonSerializer.Serialize(new
            {
                events = Enumerable.Range(0, 150).Select(index => new { atMilliseconds = 0, channel = "stderr", text = "evidence-line-" + index }).ToArray()
            }));
            var clock = new SupervisionClock(new ManualTimeProvider());
            await using var telemetry = new SupervisionTelemetry(logs, clock, readVolumeSpace: TelemetryTestVolume.Read);
            await using var supervisor = new FfmpegProcessSupervisor(new FfmpegRouteOptions("unused"), TimeSpan.FromSeconds(1), clock, telemetry);
            telemetry.ObservePublisher(source.Value, true);
            var start = new ProcessStartInfo(executable)
            {
                UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true
            };
            start.ArgumentList.Add(script);
            await supervisor.StartOwnedProcessForTestingAsync(source, start);
            await FaultHarnessTests.Eventually(() => supervisor.Snapshot().Single().RecentErrors.Contains("evidence-line-149"));
            var before = supervisor.Snapshot().Single();
            Assert.Equal(100, before.RecentErrors.Count);
            Assert.NotNull(before.ProcessStartUtc);
            Assert.NotEqual(Guid.Empty, before.SessionId);
            using (var decision = telemetry.BeginDecision(source.Value, "synthetic-port", before, "InjectedFault",
                "Synthetic stop request", 1000, ["Keep owner"], "Stop"))
            {
                await supervisor.StopAsync(source, CancellationToken.None);
                decision.Complete(supervisor.Snapshot().Single(), "Stopped");
            }
            await telemetry.FlushAsync().WaitAsync(TimeSpan.FromSeconds(5));
            var text = string.Join("\n", Directory.GetFiles(logs, "*.jsonl").Select(File.ReadAllText));
            Assert.Contains("evidence-line-0\"", text);
            Assert.Contains("evidence-line-149\"", text);
            Assert.DoesNotContain(source.Value, text);
            Assert.True(telemetry.Status.IsHealthy);
            Assert.Equal(telemetry.Status.Accepted, telemetry.Status.Written);
            Assert.NotEmpty(Directory.GetFiles(logs, before.StderrLog!));
            var events = Directory.GetFiles(logs, "*.decisions.*.jsonl").SelectMany(File.ReadAllLines)
                .Select(line => JsonDocument.Parse(line)).ToArray();
            try
            {
                var fault = events.Select(doc => doc.RootElement.GetProperty("Event"))
                    .Where(evt => evt.GetProperty("Trigger").GetString() == "InjectedFault").ToArray();
                Assert.Equal(2, fault.Length);
                Assert.Single(fault.Select(evt => evt.GetProperty("DecisionId").GetGuid()).Distinct());
                Assert.All(fault, evt =>
                {
                    Assert.Equal(before.SessionId, evt.GetProperty("SessionId").GetGuid());
                    Assert.Equal(before.ProcessId, evt.GetProperty("ProcessId").GetInt32());
                    Assert.Equal(before.ProcessStartUtc, evt.GetProperty("ProcessStartUtc").GetDateTimeOffset());
                    Assert.Equal(1, evt.GetProperty("SourceEpoch").GetInt64());
                    Assert.Equal(telemetry.ServiceRunId, evt.GetProperty("ServiceRunId").GetGuid());
                });
                var completed = fault.Single(evt => evt.GetProperty("Result").GetString() == "Stopped");
                Assert.True(completed.GetProperty("Before").GetProperty("ProcessAlive").GetBoolean());
                Assert.False(completed.GetProperty("After").GetProperty("ProcessAlive").GetBoolean());
            }
            finally { foreach (var document in events) document.Dispose(); }
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public async Task RotationAndAgeRetentionAreBoundedAndIgnoreForeignFiles()
    {
        var directory = Path.Combine(Path.GetTempPath(), "router-rotation-" + Guid.NewGuid().ToString("N"));
        var provider = new ManualTimeProvider();
        try
        {
            await using var telemetry = new SupervisionTelemetry(directory, new SupervisionClock(provider), maxFileBytes: 16384, maxFiles: 3, readVolumeSpace: TelemetryTestVolume.Read);
            var foreign = Path.Combine(directory, "unrelated.jsonl");
            await File.WriteAllTextAsync(foreign, "must survive");
            var session = Guid.NewGuid();
            for (var index = 0; index < 100; index++) telemetry.Stderr(session, new string('x', 2000));
            await telemetry.FlushAsync().WaitAsync(TimeSpan.FromSeconds(5));
            var owned = Directory.GetFiles(directory, "*.stderr.*.jsonl");
            Assert.InRange(owned.Length, 1, 3);
            Assert.All(owned, file => Assert.InRange(new FileInfo(file).Length, 1, 16384));
            Assert.True(telemetry.Status.CapacityEvictions > 0);
            Assert.False(telemetry.Status.RetentionComplete);
            Assert.NotNull(telemetry.Status.OldestRetainedSegmentWriteUtc);
            provider.Advance(TimeSpan.FromDays(31));
            await telemetry.FlushAsync().WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Empty(Directory.GetFiles(directory, "*.stderr.*.jsonl"));
            Assert.Equal("must survive", await File.ReadAllTextAsync(foreign));
            Assert.Equal(0, telemetry.Status.WriteFailures);
            Assert.False(telemetry.Status.IsHealthy); // Successful age pruning cannot repair earlier evidence loss.
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task StalledStorageCannotBlockShutdownIndefinitely()
    {
        var root = Path.Combine(Path.GetTempPath(), "router-stalled-disk-" + Guid.NewGuid().ToString("N"));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            var telemetry = new SupervisionTelemetry(root, readVolumeSpace: TelemetryTestVolume.Read)
            {
                ShutdownDrainTimeout = TimeSpan.FromMilliseconds(100),
                BeforeWriteForTesting = () => { entered.TrySetResult(); return release.Task; }
            };
            telemetry.Stderr(Guid.NewGuid(), "synthetic stalled storage");
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await telemetry.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal(1, telemetry.Status.AbandonedAtShutdown);
            Assert.False(telemetry.Status.IsHealthy);
            release.TrySetResult();
            await telemetry.WorkerCompletionForTesting.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally { release.TrySetResult(); if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public void ReparseAncestorIsRejectedBeforeAnyDirectoryIsCreated()
    {
        if (!OperatingSystem.IsWindows()) return;
        var parent = Path.Combine(Path.GetTempPath(), "router-junction-" + Guid.NewGuid().ToString("N"));
        var target = Path.Combine(parent, "target");
        var link = Path.Combine(parent, "link");
        Directory.CreateDirectory(target);
        try
        {
            var start = new ProcessStartInfo("powershell.exe") { UseShellExecute = false, CreateNoWindow = true };
            start.ArgumentList.Add("-NoProfile");
            start.ArgumentList.Add("-Command");
            start.ArgumentList.Add($"New-Item -ItemType Junction -Path '{link.Replace("'", "''")}' -Target '{target.Replace("'", "''")}' -ErrorAction Stop | Out-Null");
            using var process = Process.Start(start)!;
            Assert.True(process.WaitForExit(10000));
            Assert.Equal(0, process.ExitCode);
            Assert.Throws<IOException>(() => new SupervisionTelemetry(Path.Combine(link, "must-not-exist")));
            Assert.Empty(Directory.GetFileSystemEntries(target));
        }
        finally
        {
            if (Directory.Exists(link)) Directory.Delete(link); // Unlink exact fixture junction, never recurse through it.
            Directory.Delete(parent, true);
        }
    }

    [Fact]
    public async Task UnwritableStorageIsExplicitAndDoesNotThrowInMediaCallback()
    {
        var parent = Path.Combine(Path.GetTempPath(), "router-diskfault-" + Guid.NewGuid().ToString("N"));
        var root = Path.Combine(parent, "logs");
        try
        {
            await using var telemetry = new SupervisionTelemetry(root, readVolumeSpace: TelemetryTestVolume.Read);
            await telemetry.FlushAsync();
            Directory.Delete(root, true); // Exact newly created fixture, including its initialized marker.
            await File.WriteAllTextAsync(root, "synthetic disk fault");
            telemetry.Stderr(Guid.NewGuid(), "must not block caller");
            await Assert.ThrowsAsync<IOException>(() => telemetry.FlushAsync().WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.False(telemetry.Status.IsHealthy);
            Assert.True(telemetry.Status.WriteFailures > 0);
        }
        finally { if (Directory.Exists(parent)) Directory.Delete(parent, true); }
    }
}
