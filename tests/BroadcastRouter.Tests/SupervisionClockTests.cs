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

    [Fact]
    public void SupervisionNamespacesDoNotReadAmbientClock()
    {
        var root = FaultHarnessTests.RepositoryRoot();
        var allowed = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["OperatorTime.cs"] = "UI-only relative timestamp formatting; no supervision action",
            ["SqliteDataStore.cs"] = "Durable UTC database audit timestamps; no recovery decision",
            ["DeckLinkSoftwareInformationProvider.cs"] = "External driver update cache; not routing/recovery",
            ["DeckLinkIdentityMigration.cs"] = "Durable migration audit timestamp; not a deadline"
        };
        var expression = new System.Text.RegularExpressions.Regex(
            @"\b(?:DateTime(?:Offset)?\s*\.\s*(?:Now|UtcNow)|Environment\s*\.\s*TickCount(?:64)?|Stopwatch\s*\.)");
        var files = Directory.EnumerateFiles(Path.Combine(root, "src", "BroadcastRouter.Application"), "*.cs")
            .Concat(Directory.EnumerateFiles(Path.Combine(root, "src", "BroadcastRouter.Infrastructure"), "*.cs"))
            .Concat(Directory.EnumerateFiles(Path.Combine(root, "src", "BroadcastRouter.Web", "Services"), "*.cs"));
        var violations = files.Where(file => !allowed.ContainsKey(Path.GetFileName(file)))
            .Where(file => expression.IsMatch(File.ReadAllText(file))).Select(Path.GetFileName).ToArray();
        Assert.True(violations.Length == 0, "Ambient clock remains in supervision: " + string.Join(", ", violations));
    }
}

public sealed class ManualTimeProvider : TimeProvider
{
    private DateTimeOffset _utc = DateTimeOffset.Parse("2025-01-01T00:00:00Z");
    private long _ticks;
    public override DateTimeOffset GetUtcNow() => _utc;
    public override long GetTimestamp() => _ticks;
    public override long TimestampFrequency => TimeSpan.TicksPerSecond;
    public void Advance(TimeSpan duration) { _utc += duration; _ticks += duration.Ticks; }
}
