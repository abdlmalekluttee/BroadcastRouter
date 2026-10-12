using BroadcastRouter.Infrastructure;
using Xunit;

namespace BroadcastRouter.Tests;

public sealed class TelemetryDiskBudgetTests
{
    // Real storage/rotation, fake volume capacity only: never fill a developer disk.
    [Fact]
    public async Task TotalBudgetEvictsOldestBeforeExceedingByteCap()
    {
        using var fixture = new VolumeFixture(100_000_000_000, 80_000_000_000);
        await using var sink = fixture.Sink(maxTotalBytes: 196608);
        for (var i = 0; i < 250; i++) sink.Stderr(Guid.Empty, $"sequence-{i:D3} " + new string('x', 2000));
        await sink.FlushAsync();
        Assert.True(fixture.StoredBytes <= 196608, "Owned telemetry must stay within the configured byte cap.");
        Assert.True(sink.Status.CapacityEvictions > 0);
        Assert.False(sink.Status.RetentionComplete);
        var text = fixture.Text;
        Assert.DoesNotContain("sequence-000", text);
        Assert.Contains("sequence-249", text);
    }

    [Theory]
    [InlineData(50_000_000_000, 20_000_000_000)]
    [InlineData(300_000_000_000, 60_000_000_000)]
    public async Task EveryAppendRespectsLargerOfTwentyPercentAndTwentyGb(long total, long floor)
    {
        using var fixture = new VolumeFixture(total, floor + 200000);
        await using var sink = fixture.Sink();
        for (var i = 0; i < 250; i++) sink.Stderr(Guid.Empty, $"sequence-{i:D3} " + new string('x', 2000));
        await sink.FlushAsync();
        Assert.True(fixture.FreeBytes >= floor, "Telemetry must not consume the protected disk reserve.");
        Assert.False(sink.Status.RetentionComplete);
        Assert.Contains("sequence-249", fixture.Text);
    }

    [Fact]
    public async Task PressureWithoutReclaimableSegmentsStopsWritingAndSurvivesRestart()
    {
        using var fixture = new VolumeFixture(100_000_000_000, 80_000_000_000);
        await using (var sink = fixture.Sink())
        {
            await sink.FlushAsync();
            fixture.FreeBeforeFiles = 19_000_000_000;
            sink.Stderr(Guid.Empty, "must-not-consume-reserve");
            await sink.FlushAsync();
            Assert.Equal(0, sink.Status.Written);
            Assert.False(sink.Status.RetentionComplete);
        }
        fixture.FreeBeforeFiles = 80_000_000_000;
        await using var restarted = fixture.Sink();
        await restarted.FlushAsync();
        Assert.False(restarted.Status.RetentionComplete, "Disk-pressure evidence loss must stay visible after restart.");
    }

    [Fact]
    public async Task UnreadableVolumeFailsClosedWithoutDeletingForeignFiles()
    {
        using var fixture = new VolumeFixture(100_000_000_000, 80_000_000_000);
        var foreign = Path.Combine(fixture.Root, "operator-note.jsonl");
        await File.WriteAllTextAsync(foreign, "retain this");
        await using var sink = new SupervisionTelemetry(fixture.Root,
            readVolumeSpace: _ => throw new IOException("synthetic capacity query failure"));
        sink.Stderr(Guid.Empty, "must-not-write-with-unknown-free-space");
        try { await sink.FlushAsync(); } catch (IOException) { }
        Assert.Equal(0, sink.Status.Written);
        Assert.True(sink.Status.WriteFailures > 0);
        Assert.Equal("retain this", await File.ReadAllTextAsync(foreign));
    }

    [Fact]
    public async Task BudgetCannotBeConfiguredAboveTenGb()
    {
        using var fixture = new VolumeFixture(100_000_000_000, 80_000_000_000);
        SupervisionTelemetry? created = null;
        try
        {
            var error = Record.Exception(() => created = fixture.Sink(maxTotalBytes: 10_000_000_001));
            Assert.IsType<ArgumentOutOfRangeException>(error);
        }
        finally { if (created is not null) await created.DisposeAsync(); }
    }

    [Fact]
    public async Task EmptyMarkerObjectMustNotClearAnUnknownRetentionHistory()
    {
        using var fixture = new VolumeFixture(100_000_000_000, 80_000_000_000);
        await File.WriteAllTextAsync(Path.Combine(fixture.Root, "retention-loss.json"), "{}");
        await using var sink = fixture.Sink();
        try { await sink.FlushAsync(); } catch (IOException) { }
        Assert.False(sink.Status.RetentionComplete, "A marker missing its schema and counters is not a healthy history.");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FreshStartPressureStaysUnhealthyAfterSpaceRecoveryAndRestart(bool restartBeforeSpaceRecovery)
    {
        using var fixture = new VolumeFixture(100_000_000_000, 19_000_000_000);
        await using (var first = fixture.Sink())
        {
            first.Stderr(Guid.Empty, "lost-at-initial-disk-pressure");
            try { await first.FlushAsync(); } catch (IOException) { }
            Assert.Equal(0, first.Status.Written);
            if (!restartBeforeSpaceRecovery)
            {
                fixture.FreeBeforeFiles = 80_000_000_000;
                await first.FlushAsync();
            }
        }
        fixture.FreeBeforeFiles = 80_000_000_000;
        await using var restarted = fixture.Sink();
        await restarted.FlushAsync();
        Assert.False(restarted.Status.RetentionComplete, "Initial pressure must not become a healthy zero-loss epoch after restart.");
    }

    [Fact]
    public async Task UncertainAppendCannotHideBytesFromAdmissionControl()
    {
        using var fixture = new VolumeFixture(100_000_000_000, 80_000_000_000);
        await using var sink = new SupervisionTelemetry(fixture.Root, maxFileBytes: 16384, maxFiles: 1000,
            maxTotalBytes: 196608, readVolumeSpace: _ => new(100_000_000_000, fixture.FreeBytes),
            openAppend: path => new UncertainFileStream(path));
        for (var i = 0; i < 250; i++) sink.Stderr(Guid.Empty, new string('x', 2000));
        // A flush inventory would mask the undercount. Observe the actual files
        // after every accepted write has reported its failure, before flushing.
        // This is a byte-accounting assertion, not a filesystem throughput
        // target. 250 flush-to-disk failures can exceed the generic5s UI wait.
        await FaultHarnessTests.Eventually(() => sink.Status.WriteFailures >= 250, TimeSpan.FromSeconds(30));
        Assert.True(fixture.StoredBytes <= 196608, "Partially successful writes must be counted before any further append.");
    }

    private sealed class UncertainFileStream(string path) : FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read)
    {
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await base.WriteAsync(buffer, cancellationToken);
            await base.FlushAsync(cancellationToken);
            throw new IOException("Synthetic write completed partially/uncertainly before failure");
        }
    }

    private sealed class VolumeFixture(long total, long free) : IDisposable
    {
        public string Root { get; } = CreateRoot();
        public long FreeBeforeFiles = free;
        public long StoredBytes => Directory.GetFiles(Root).Sum(p => new FileInfo(p).Length);
        public long FreeBytes => FreeBeforeFiles - StoredBytes;
        public string Text => string.Join("\n", Directory.GetFiles(Root, "run-*.jsonl").Select(File.ReadAllText));
        public SupervisionTelemetry Sink(long maxTotalBytes = 10_000_000_000) => new(Root,
            maxFileBytes: 16384, maxFiles: 1000, maxTotalBytes: maxTotalBytes,
            readVolumeSpace: _ => new(total, FreeBytes));
        private static string CreateRoot()
        {
            var path = Path.Combine(Path.GetTempPath(), "router-disk-budget-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(path);
            return path;
        }
        public void Dispose() => Directory.Delete(Root, true);
    }
}
