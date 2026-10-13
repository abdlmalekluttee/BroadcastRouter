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
        lab.Settings.SourceMediaProfiles.Add(new() { SourceId = lab.Source.Value, Expectation = SourceMediaExpectation.AudioOnly });
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
        Assert.Contains("Output graph: generated black (declared audio-only)", html);
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
        return await renderer.Dispatcher.InvokeAsync(async () =>
            (await renderer.RenderComponentAsync<SourceMediaStatus>(ParameterView.FromDictionary(new Dictionary<string, object?>
            { ["Source"] = source, ["Output"] = selected }))).ToHtmlString());
    }
}
