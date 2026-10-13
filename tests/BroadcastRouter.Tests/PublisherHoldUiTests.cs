using System.Reflection;
using BroadcastRouter.Application;
using BroadcastRouter.Domain;
using BroadcastRouter.Web.Components.Shared;
using BroadcastRouter.Web.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BroadcastRouter.Tests;

public sealed class PublisherHoldUiTests
{
    [Theory]
    [InlineData(0, "Healthy", "Live confirmed")]
    [InlineData(10, "Unknown", "Unknown")]
    public async Task RenderedHoldUsesFreshPublisherEvidenceWithoutChangingTheSourceState(int age, string state, string label)
    {
        var time = new ManualTimeProvider();
        var clock = new SupervisionClock(time);
        using var coordinator = new RouterCoordinator(null!, null!, null!, NullLogger<RouterCoordinator>.Instance, clock);
        var source = new SourceIdentity("TEST", "fixture", "default", "synthetic");
        Get<Dictionary<string, DiscoveredSource>>(coordinator, "_sources")[source.Value] =
            new(source, "Synthetic", new Uri("rtsp://example.invalid/fixture"), SourceState.Ready, 0);
        Get<Dictionary<string, RuntimeRoute>>(coordinator, "_routes")[source.Value] = new(source.Value,
            "Synthetic", "test-output", "Output", "1080i50", RouteState.Running, AssignmentMode.Manual,
            false, 0, 0, 100, 25, 1, 0, 0, clock.Now, clock.Now, null, null, HoldOutputWhilePublisherLive: true);
        Get<PublisherLiveHoldPolicy>(coordinator, "_publisherLiveHold").Observe(source.Value, true);
        time.Advance(TimeSpan.FromSeconds(age));
        typeof(RouterCoordinator).GetMethod("Publish", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(coordinator, ["Synthetic fixture"]);
        var services = new ServiceCollection().AddLogging();
        services.AddSingleton(new AuthorizedRouterCommands(coordinator, null!));
        services.AddSingleton<OperatorErrorMessage>();
        await using var provider = services.BuildServiceProvider();
        await using var renderer = new HtmlRenderer(provider, provider.GetRequiredService<ILoggerFactory>());
        var html = await renderer.Dispatcher.InvokeAsync(async () =>
            (await renderer.RenderComponentAsync<RouteDetailPanel>(ParameterView.FromDictionary(new Dictionary<string, object?>
            { ["Snapshot"] = coordinator.Snapshot, ["SelectedSourceId"] = source.Value }))).ToHtmlString());
        Assert.Contains($"data-publisher-presence=\"{state}\"", html);
        Assert.Contains($"Publisher observation: {label}", html);
        Assert.Contains("Confirmed media failure and process exit still recover automatically", html);
        Assert.DoesNotContain("Automatic media recovery is held", html);
        Assert.Equal(SourceState.Ready, Assert.Single(coordinator.Snapshot.Sources).State);
    }

    private static T Get<T>(object instance, string field) => (T)typeof(RouterCoordinator)
        .GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(instance)!;
}
