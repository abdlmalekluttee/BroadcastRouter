using System.Reflection;
using System.Text.Json;
using BroadcastRouter.Domain;
using BroadcastRouter.Infrastructure;
using BroadcastRouter.Web.Components.Shared;
using BroadcastRouter.Web.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace BroadcastRouter.Tests;

public sealed class SourceMediaReportingTests
{
    [Fact]
    public async Task HandoffDecisionRetainsDecodedQualificationIntervals()
    {
        await using var lab = await OfflineFallbackTests.RecoveryLab.Create(captureTelemetry: true);
        await lab.InstallFakeMediaTools();
        await AutomaticMediaCoordinatorTests.Poll(lab);
        await lab.Telemetry!.FlushAsync();
        var records = Directory.GetFiles(Path.Combine(lab.Root, "telemetry"), "*.decisions.*.jsonl")
            .SelectMany(File.ReadLines).Select(line => JsonSerializer.Deserialize<JsonElement>(line)).ToArray();
        var decision = Assert.Single(records.Select(e => e.GetProperty("Event")), e => e.GetProperty("Trigger").GetString() == "MediaQualificationAccepted");
        var qualification = decision.GetProperty("After").GetProperty("Qualification");
        Assert.Equal("AudioVideo", qualification.GetProperty("DetectedProfile").GetString());
        Assert.True(qualification.GetProperty("AdvancingVideoSeconds").GetDouble() >= 1);
        Assert.True(qualification.GetProperty("AdvancingAudioSeconds").GetDouble() >= 1);
        Assert.Equal(5000, decision.GetProperty("WindowMilliseconds").GetDouble());
    }

    [Fact]
    public async Task MutedGraphReportsDetectedTracksAndAuthoritativePresetMute()
    {
        var media = RecoveryReadinessTests.Av().Media! with {
            DetectedProfile = DetectedMediaProfile.AudioVideo, AudioDisabledByPreset = true };
        var source = new DiscoveredSource(new("TEST", "fixture", "default", "muted"), "Muted",
            new Uri("rtsp://example.invalid/test"), SourceState.Ready, 0, media);
        var html = await Render(source, new(Guid.NewGuid(), false, media));
        Assert.Contains("Detected profile: video + audio", html);
        Assert.Contains("audio disabled by preset", html);
    }

    [Theory]
    [InlineData(false, true, "generated black + source audio")]
    [InlineData(true, false, "source video + generated silence")]
    [InlineData(true, true, "source video + source audio")]
    public async Task SelectedGraphIsExplicit(bool video, bool audio, string expected)
    {
        var media = RecoveryReadinessTests.Av().Media! with { HasUsableVideo = video,
            AudioCodec = audio ? "aac" : null, Expectation = video ? SourceMediaExpectation.ContinuousVideo : SourceMediaExpectation.AudioOnly };
        var source = new DiscoveredSource(new("TEST", "fixture", "default", "synthetic"), "Synthetic",
            new Uri("rtsp://example.invalid/test"), SourceState.Ready, 0, media);
        Assert.Contains(expected, await Render(source, new(Guid.NewGuid(), false, media)));
    }

    [Fact]
    public async Task DecisionRecordIncludesDetectedProfileOverrideAndMute()
    {
        await using var lab = await OfflineFallbackTests.RecoveryLab.Create();
        await lab.InstallFakeMediaTools();
        lab.Settings.Presets[0].IncludeAudio = false;
        await AutomaticMediaCoordinatorTests.Poll(lab);
        var json = JsonSerializer.SerializeToElement(SupervisionTelemetry.Evidence(lab.Owner, lab.Clock));
        var media = json.GetProperty("SelectedMedia");
        Assert.True(media.TryGetProperty("DetectedProfile", out var profile), "Decision evidence must carry the detected profile.");
        Assert.Equal("AudioVideo", profile.GetString());
        Assert.Equal("Auto", media.GetProperty("Override").GetString());
        Assert.True(media.GetProperty("AudioDisabledByPreset").GetBoolean());
        Assert.False(media.GetProperty("SourceAudioSelected").GetBoolean());
    }

    [Fact]
    public async Task QualificationFailureShowsOperatorReasonAsAnAlarm()
    {
        var source = new DiscoveredSource(new("TEST", "fixture", "default", "unknown"), "Unknown",
            new Uri("rtsp://example.invalid/test"), SourceState.Unknown, 0);
        var json = System.Text.Json.Nodes.JsonNode.Parse(JsonSerializer.Serialize(source))!;
        json["MediaQualificationDetail"] = "Required one second of decoded audio samples missing. Standby retained.";
        var html = await Render(JsonSerializer.Deserialize<DiscoveredSource>(json.ToJsonString())!, new(Guid.NewGuid(), true, null));
        Assert.Contains("role=\"alert\"", html);
        Assert.Contains("Required one second of decoded audio samples missing. Standby retained.", html);
    }

    [Fact]
    public async Task SparseVideoWithoutAudioIsExplicitlyUnknownWithObservationTime()
    {
        var source = new DiscoveredSource(new("TEST", "fixture", "default", "sparse"), "Sparse",
            new Uri("rtsp://example.invalid/test"), SourceState.Ready, 0,
            RecoveryReadinessTests.Av().Media! with { Expectation = SourceMediaExpectation.SparseVideo, AudioCodec = null },
            LastObservedAt: DateTimeOffset.Parse("2026-01-02T03:04:05Z"));
        var html = await Render(source, new(Guid.NewGuid(), false, source.Media));
        Assert.Contains("media status unknown", html);
        Assert.Contains("2026-01-02 03:04:05 UTC", html);
        Assert.DoesNotContain("video qualified", html);
        Assert.DoesNotContain("success", html);
    }

    [Fact]
    public async Task PublishedSelectionAndDecisionEvidenceStayWithRunningGraph()
    {
        await using var lab = await OfflineFallbackTests.RecoveryLab.Create();
        await lab.InstallFakeMediaTools();
        lab.Settings.SourceMediaProfiles[0].Override = SourceMediaOverride.ForceAudioOnly;
        await DeclaredMediaCoordinatorTests.Poll(lab);
        var session = lab.Owner.SessionId;
        // A newer observation qualifies video; it cannot change the running graph.
        lab.Sources[lab.Source.Value] = lab.Sources[lab.Source.Value] with { State = SourceState.Ready, Media = RecoveryReadinessTests.Av().Media };
        typeof(RouterCoordinator).GetMethod("Publish", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(lab.Coordinator, ["Synthetic fixture"]);
        var snapshot = lab.Coordinator.Snapshot;
        Assert.NotNull(snapshot.OutputSelections);
        var selected = Assert.Single(snapshot.OutputSelections).Value;
        Assert.Equal(session, selected.SessionId);
        Assert.False(selected.IsFallback);
        Assert.Equal(SourceMediaExpectation.AudioOnly, selected.Media!.Expectation);
        var html = await Render(Assert.Single(snapshot.Sources), selected);
        Assert.Contains("Observed: video qualified", html);
        Assert.Contains("Output graph: generated black + source audio", html);
        Assert.Contains(session.ToString("N"), html);
        var evidence = JsonSerializer.SerializeToElement(SupervisionTelemetry.Evidence(lab.Owner, lab.Clock));
        Assert.True(evidence.TryGetProperty("SelectedMedia", out var media), "Selected graph is missing from decision evidence.");
        Assert.Equal("AudioOnly", media.GetProperty("Expectation").GetString());
        Assert.False(media.GetProperty("SourceVideoSelected").GetBoolean());
    }

    [Fact]
    public async Task WaitingVideoIsNotReportedAsGeneratedBlack()
    {
        var identity = new SourceIdentity("TEST", "fixture", "default", "synthetic");
        var source = new DiscoveredSource(identity, "Synthetic", new Uri("rtsp://example.invalid/test"),
            SourceState.WaitingForKeyframe, 0, Media: RecoveryReadinessTests.Av().Media! with { HasUsableVideo = false });
        var html = await Render(source, new(Guid.NewGuid(), false, RecoveryReadinessTests.Av().Media));
        Assert.Contains("Observed: waiting for decoded video", html);
        Assert.Contains("Output graph: source video", html);
        Assert.DoesNotContain("generated black", html);
    }

    private static async Task<string> Render(DiscoveredSource source, OutputMediaSelection selected)
    {
        await using var provider = new ServiceCollection().AddLogging().BuildServiceProvider();
        await using var renderer = new HtmlRenderer(provider, provider.GetRequiredService<ILoggerFactory>());
        return System.Net.WebUtility.HtmlDecode(await renderer.Dispatcher.InvokeAsync(async () =>
            (await renderer.RenderComponentAsync<SourceMediaStatus>(ParameterView.FromDictionary(new Dictionary<string, object?>
            { ["Source"] = source, ["Output"] = selected }))).ToHtmlString()));
    }
}
