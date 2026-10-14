using System.Reflection;
using Xunit;

namespace BroadcastRouter.Tests;

public sealed class SupervisionClockTests
{
    [Fact]
    public void SupervisionClockHasSeparateUtcAndMonotonicTime()
    {
        var type = typeof(BroadcastRouter.Application.AutomaticAssignmentEngine).Assembly
            .GetType("BroadcastRouter.Application.SupervisionClock");
        Assert.NotNull(type);
        var provider = new ManualTimeProvider();
        dynamic clock = Activator.CreateInstance(type, provider)!;
        var before = (DateTimeOffset)clock.Now;
        var stamp = (long)clock.GetTimestamp();
        provider.Advance(TimeSpan.FromSeconds(2));
        Assert.Equal(TimeSpan.FromSeconds(2), (DateTimeOffset)clock.Now - before);
        Assert.Equal(TimeSpan.FromSeconds(2), (TimeSpan)clock.GetElapsedTime(stamp));
        Assert.Equal(provider.GetUtcNow(), (DateTimeOffset)clock.UtcNow);
    }

    [Theory]
    [InlineData(10)]
    [InlineData(-10)]
    [InlineData(2)]
    [InlineData(-2)]
    public void PublishingAfterWallStepDoesNotMakeFreshStatusStale(int seconds)
    {
        var provider = new ManualTimeProvider();
        var clock = NewClock(provider);
        using var coordinator = (BroadcastRouter.Web.Services.RouterCoordinator)Activator.CreateInstance(
            typeof(BroadcastRouter.Web.Services.RouterCoordinator), null, null, null,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<BroadcastRouter.Web.Services.RouterCoordinator>.Instance, (object)clock, null)!;
        provider.Advance(TimeSpan.FromSeconds(2));
        provider.StepUtc(TimeSpan.FromSeconds(seconds));
        typeof(BroadcastRouter.Web.Services.RouterCoordinator).GetMethod("Publish", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(coordinator, ["test"]);
        Assert.Equal(provider.GetUtcNow(), coordinator.Snapshot.UpdatedAt);
        Assert.Equal(TimeSpan.FromSeconds(2), provider.GetUtcNow() - coordinator.Snapshot.StartedAt);
        // Policies retain the monotonic interval, not the external wall-clock step.
        Assert.Equal(TimeSpan.FromSeconds(2), clock.Now - coordinator.GetLiveness().StartedAt);
    }

    [Theory]
    [InlineData(10)]
    [InlineData(-10)]
    [InlineData(2)]
    [InlineData(-2)]
    public void PersistedDeadlineRebasesIntoNextServiceRun(int wallStep)
    {
        var provider = new ManualTimeProvider();
        dynamic clock = NewClock(provider);
        var deadline = (DateTimeOffset)clock.Now + TimeSpan.FromSeconds(30);
        provider.Advance(TimeSpan.FromSeconds(5));
        provider.StepUtc(TimeSpan.FromSeconds(wallStep));
        var method = clock.GetType().GetMethod("CaptureBoundary");
        Assert.True(method is not null, "UTC persistence boundary is missing");
        dynamic boundary = method!.Invoke(clock, null)!;
        var persistedUtc = (DateTimeOffset)boundary.ToUtc(deadline);
        Assert.Equal(provider.GetUtcNow() + TimeSpan.FromSeconds(25), persistedUtc);
        provider.Advance(TimeSpan.FromSeconds(3));
        dynamic nextClock = NewClock(provider);
        dynamic nextBoundary = nextClock.CaptureBoundary();
        Assert.Equal(TimeSpan.FromSeconds(22), (DateTimeOffset)nextBoundary.FromUtc(persistedUtc) - (DateTimeOffset)nextClock.Now);
    }

    [Fact]
    public void WallStepDoesNotAdvanceMonotonicTime()
    {
        var provider = new ManualTimeProvider();
        var clock = NewClock(provider);
        var before = clock.Now;
        provider.StepUtc(TimeSpan.FromHours(1));
        Assert.Equal(before, clock.Now);
        Assert.Equal(before.AddHours(1), clock.UtcNow);
    }

    [Fact]
    public async Task SupervisionDelayAndPeriodicTimerCanBeFastForwarded()
    {
        var provider = new ManualTimeProvider();
        var clock = new BroadcastRouter.Application.SupervisionClock(provider);
        using var timer = clock.CreatePeriodicTimer(TimeSpan.FromSeconds(10));
        var tick = timer.WaitForNextTickAsync().AsTask();
        var delay = clock.DelayAsync(TimeSpan.FromSeconds(10), CancellationToken.None);
        provider.StepUtc(TimeSpan.FromHours(1));
        Assert.False(tick.IsCompleted);
        Assert.False(delay.IsCompleted);
        provider.Advance(TimeSpan.FromSeconds(10));
        Assert.True(await tick.WaitAsync(TimeSpan.FromSeconds(1)));
        await delay.WaitAsync(TimeSpan.FromSeconds(1));
    }

    private static dynamic NewClock(TimeProvider provider)
    {
        var type = typeof(BroadcastRouter.Application.AutomaticAssignmentEngine).Assembly.GetType("BroadcastRouter.Application.SupervisionClock");
        Assert.NotNull(type);
        return Activator.CreateInstance(type, provider)!;
    }

    [Fact]
    public void SupervisionNamespacesDoNotReadAmbientClock()
    {
        var root = FaultHarnessTests.RepositoryRoot();
        var allowed = new Dictionary<string, (int Count, string Reason)>(StringComparer.Ordinal)
        {
            ["SupervisionClock.cs"] = (1, "Sole ambient TimeProvider factory at service-run boundary"),
            ["OperatorTime.cs"] = (1, "UI-only relative timestamp formatting; no supervision action"),
            ["SqliteDataStore.cs"] = (4, "Durable UTC database audit timestamps; no recovery decision"),
            ["DeckLinkSoftwareInformationProvider.cs"] = (1, "External driver update cache; not routing/recovery")
        };
        var expression = new System.Text.RegularExpressions.Regex(
            @"\b(?:DateTime(?:Offset)?\s*\.\s*(?:Now|UtcNow)|Environment\s*\.\s*TickCount(?:64)?|Stopwatch\s*\.|TimeProvider\s*\.\s*System)");
        var files = Directory.EnumerateFiles(Path.Combine(root, "src", "BroadcastRouter.Application"), "*.cs", SearchOption.AllDirectories)
            .Concat(Directory.EnumerateFiles(Path.Combine(root, "src", "BroadcastRouter.Infrastructure"), "*.cs", SearchOption.AllDirectories))
            .Concat(Directory.EnumerateFiles(Path.Combine(root, "src", "BroadcastRouter.Web", "Services"), "*.cs", SearchOption.AllDirectories))
            .Where(file => !file.Split(Path.DirectorySeparatorChar).Any(segment => segment is "obj" or "bin"));
        var violations = files.Where(file => expression.Matches(File.ReadAllText(file)).Count
                != (allowed.TryGetValue(Path.GetFileName(file), out var exception) ? exception.Count : 0))
            .Select(Path.GetFileName).ToArray();
        Assert.True(violations.Length == 0, "Ambient clock remains in supervision: " + string.Join(", ", violations));
    }
}

public sealed class ManualTimeProvider : TimeProvider
{
    private readonly Microsoft.Extensions.Time.Testing.FakeTimeProvider _inner = new(DateTimeOffset.Parse("2025-01-01T00:00:00Z"));
    private TimeSpan _utcOffset;
    public override DateTimeOffset GetUtcNow() => _inner.GetUtcNow() + _utcOffset;
    public override long GetTimestamp() => _inner.GetTimestamp();
    public override long TimestampFrequency => _inner.TimestampFrequency;
    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) =>
        _inner.CreateTimer(callback, state, dueTime, period);
    public void Advance(TimeSpan duration) => _inner.Advance(duration);
    public void StepUtc(TimeSpan duration) => _utcOffset += duration;
}
