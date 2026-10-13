using System.Text.Json;
using BroadcastRouter.Domain;
using BroadcastRouter.Web.Components.Shared;
using BroadcastRouter.Web.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace BroadcastRouter.Tests;

public sealed class MutedPresetWarningTests
{
    private const string Warning = "incompatible preset: audio-only source on a muted output";

    [Fact]
    public async Task PresetSaveSucceedsAndReturnsCompatibilityWarningFromBackend()
    {
        await using var lab = await OfflineFallbackTests.RecoveryLab.Create();
        lab.Settings.DeckLinkPortOverrides.Add(new() { StableId = "TEST-OUTPUT", IsOutputPort = true,
            StandbyPresetId = lab.Settings.Presets[0].Id });
        lab.Sources[lab.Source.Value] = lab.Sources[lab.Source.Value] with {
            Media = RecoveryReadinessTests.Av().Media! with {
                HasUsableVideo = false, DetectedProfile = DetectedMediaProfile.AudioOnly } };
        var submitted = lab.Coordinator.GetSettings();
        submitted.Presets[0].IncludeAudio = false;
        var result = await lab.Coordinator.SaveSettingsAsync(submitted, "Test administrator");
        Assert.False(result.Settings.Presets[0].IncludeAudio); // warning never blocks save
        Assert.False((await lab.Store.LoadSettingsAsync()).Presets[0].IncludeAudio);
        var json = JsonSerializer.SerializeToElement(result);
        Assert.True(json.TryGetProperty("Warnings", out var warnings), "Save confirmation must include backend compatibility warnings.");
        Assert.Contains(Warning, warnings.ToString());
    }

    [Fact]
    public async Task AssignmentShowsWarningButSaveRemainsEnabled()
    {
        await using var lab = await OfflineFallbackTests.RecoveryLab.Create();
        lab.Settings.Presets[0].IncludeAudio = false;
        lab.Sources[lab.Source.Value] = lab.Sources[lab.Source.Value] with {
            Media = RecoveryReadinessTests.Av().Media! with { HasUsableVideo = false,
                DetectedProfile = DetectedMediaProfile.AudioOnly } };
        typeof(RouterCoordinator).GetMethod("Publish", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .Invoke(lab.Coordinator, ["Test warning"]);
        await using var provider = new ServiceCollection().AddLogging()
            .AddSingleton(new AuthorizedRouterCommands(lab.Coordinator, null!))
            .AddSingleton<OperatorErrorMessage>().BuildServiceProvider();
        await using var renderer = new HtmlRenderer(provider, provider.GetRequiredService<ILoggerFactory>());
        var html = await renderer.Dispatcher.InvokeAsync(async () =>
            (await renderer.RenderComponentAsync<RouteDetailPanel>(ParameterView.FromDictionary(new Dictionary<string, object?> {
                ["Snapshot"] = lab.Coordinator.Snapshot, ["SelectedSourceId"] = lab.Source.Value,
                ["Presets"] = lab.Settings.Presets }))).ToHtmlString());
        Assert.Contains(Warning, html);
        Assert.Contains("role=\"alert\"", html);
        var button = html[html.IndexOf("class=\"btn primary detail-save\"", StringComparison.Ordinal)..];
        Assert.DoesNotContain("disabled", button[..button.IndexOf("</button>", StringComparison.Ordinal)]);
    }
}
