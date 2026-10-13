using System.Text.Json;
using BroadcastRouter.Application;
using BroadcastRouter.Domain;
using BroadcastRouter.Infrastructure;
using Xunit;

namespace BroadcastRouter.Tests;

public sealed class IndependentMediaTelemetryTests
{
    [Fact]
    public void DecisionEvidenceRetainsEachIndependentSignalAndItsMonotonicAge()
    {
        var time = new ManualTimeProvider();
        var clock = new SupervisionClock(time);
        var advanced = clock.Now;
        time.Advance(TimeSpan.FromSeconds(7));
        var media = new MediaEvidenceSnapshot(MediaSignalState.Healthy, MediaSignalState.Healthy,
            [new(MediaSignalKind.InputAudio, MediaSignalState.Failed, advanced),
             new(MediaSignalKind.OutputAudio, MediaSignalState.Failed, advanced),
             new(MediaSignalKind.InputVideo, MediaSignalState.Unknown, null)], true, true);
        var process = new RouteProcessSnapshot(new("TEST", "fixture", "default", "synthetic"),
            RouteProcessPurpose.Live, 123, advanced, true, null, [], null, null,
            new(0, 0, 0, 0, 0, null, null), IndependentMedia: media);
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(SupervisionTelemetry.Evidence(process, clock)));
        Assert.True(json.RootElement.TryGetProperty("IndependentMedia", out var evidence),
            "The destructive decision omitted independent media evidence.");
        Assert.Equal("Healthy", evidence.GetProperty("Process").GetString());
        Assert.Equal("Healthy", evidence.GetProperty("Heartbeat").GetString());
        Assert.True(evidence.GetProperty("ConfirmedMediaFailure").GetBoolean());
        var signals = evidence.GetProperty("Signals").EnumerateArray().ToArray();
        Assert.Equal(3, signals.Length);
        Assert.Equal("InputAudio", signals[0].GetProperty("Kind").GetString());
        Assert.Equal("Failed", signals[0].GetProperty("State").GetString());
        Assert.Equal(7000, signals[0].GetProperty("LastAdvanceAgeMilliseconds").GetDouble());
        Assert.Equal("OutputAudio", signals[1].GetProperty("Kind").GetString());
        Assert.Equal("Unknown", signals[2].GetProperty("State").GetString());
        Assert.Equal(JsonValueKind.Null, signals[2].GetProperty("LastAdvanceAgeMilliseconds").ValueKind);
    }

    [Fact]
    public void UninstrumentedOwnerDoesNotManufactureFailedMediaEvidence()
    {
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(
            SupervisionTelemetry.Evidence(null, new(new ManualTimeProvider()))));
        Assert.False(json.RootElement.TryGetProperty("IndependentMedia", out _));
    }
}
