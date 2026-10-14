using System.Security.Claims;
using BroadcastRouter.Infrastructure;
using BroadcastRouter.Web.Components.Layout;
using BroadcastRouter.Web.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.JSInterop;
using Xunit;

namespace BroadcastRouter.Tests;

public sealed class TelemetryAlarmTests
{
    [Fact]
    public async Task DiagnosticQueueLossIsVisibleEvenWhenFileTelemetryIsHealthy()
    {
        var root = Path.Combine(Path.GetTempPath(), "router-diagnostic-ui-" + Guid.NewGuid().ToString("N"));
        var store = new SqliteDataStore(Path.Combine(root, "router.db"), readDiagnosticVolumeSpace: TelemetryTestVolume.Read);
        try
        {
            await store.InitializeAsync();
            Assert.False(store.EnqueueDiagnostic("Warning", "Fixture", new string('x', 10000)));
            await using var telemetry = new SupervisionTelemetry(Path.Combine(root, "telemetry"), readVolumeSpace: TelemetryTestVolume.Read);
            await telemetry.FlushAsync();
            Assert.True(telemetry.Status.IsHealthy);
            using var coordinator = new RouterCoordinator(store, null!, null!, NullLogger<RouterCoordinator>.Instance);
            var services = new ServiceCollection();
            services.AddLogging(); services.AddAuthorizationCore();
            services.AddSingleton(coordinator); services.AddSingleton(telemetry);
            services.AddSingleton<NavigationManager>(new TestNavigation()); services.AddSingleton<IJSRuntime>(new NoScript());
            await using var provider = services.BuildServiceProvider();
            await using var renderer = new HtmlRenderer(provider, provider.GetRequiredService<ILoggerFactory>());
            var html = await renderer.Dispatcher.InvokeAsync(async () => (await renderer.RenderComponentAsync<TestRoot>()).ToHtmlString());
            Assert.Contains("data-diagnostic-persistence-alarm", html);
            Assert.Contains("role=\"alert\"", html);
            Assert.Contains("Diagnostic summaries are incomplete", html);
        }
        finally
        {
            await store.StopDiagnosticsAsync();
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(root, true);
        }
    }

    [Fact]
    public void EarlyEvictionDegradesTheHealthSignal()
    {
        var status = new TelemetryStatus(100, 100, 0, 0, 0, 1, 0, null);
        Assert.False(status.IsHealthy, "Health must be degraded when telemetry was evicted before retention age.");
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task ApplicationLayoutShowsPersistentRetentionAlarmOnlyForIncompleteHistory(bool loseEvidence, bool controllerStall)
    {
        var root = Path.Combine(Path.GetTempPath(), "router-alarm-ui-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using var telemetry = new SupervisionTelemetry(root, maxFileBytes: 16384, maxFiles: 2, readVolumeSpace: TelemetryTestVolume.Read);
            if (loseEvidence)
                for (var index = 0; index < 50; index++) telemetry.Stderr(Guid.NewGuid(), new string('x', 2000));
            await telemetry.FlushAsync();
            using var coordinator = new RouterCoordinator(null!, null!, null!, NullLogger<RouterCoordinator>.Instance);
            if (controllerStall) coordinator.RecoverIdleControllersAndAlarm("Synthetic controller operation stalled");
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddAuthorizationCore();
            services.AddSingleton(coordinator);
            services.AddSingleton(telemetry);
            services.AddSingleton<NavigationManager>(new TestNavigation());
            services.AddSingleton<IJSRuntime>(new NoScript());
            await using var provider = services.BuildServiceProvider();
            await using var renderer = new HtmlRenderer(provider, provider.GetRequiredService<ILoggerFactory>());
            var html = await renderer.Dispatcher.InvokeAsync(async () =>
                (await renderer.RenderComponentAsync<TestRoot>()).ToHtmlString());
            if (loseEvidence)
            {
                Assert.Contains("data-telemetry-alarm", html);
                Assert.Contains("role=\"alert\"", html);
            }
            else Assert.DoesNotContain("data-telemetry-alarm", html);
            if (controllerStall)
            {
                Assert.True(coordinator.HasOutputControllerAlarm);
                Assert.Contains("data-controller-alarm", html);
                Assert.Contains("Existing output owners were preserved", html);
                Assert.Contains("Synthetic controller operation stalled", html);
                Assert.Contains("role=\"alert\"", html);
            }
            else Assert.DoesNotContain("data-controller-alarm", html);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    public sealed class TestRoot : ComponentBase
    {
        protected override void BuildRenderTree(RenderTreeBuilder builder)
        {
            builder.OpenComponent<CascadingValue<Task<AuthenticationState>>>(0);
            builder.AddAttribute(1, "Value", Task.FromResult(new AuthenticationState(new ClaimsPrincipal(new ClaimsIdentity()))));
            builder.AddAttribute(2, "ChildContent", (RenderFragment)(child =>
            {
                child.OpenComponent<MainLayout>(0);
                child.CloseComponent();
            }));
            builder.CloseComponent();
        }
    }
    private sealed class TestNavigation : NavigationManager
    {
        public TestNavigation() => Initialize("http://localhost/", "http://localhost/");
        protected override void NavigateToCore(string uri, bool forceLoad) { }
    }
    private sealed class NoScript : IJSRuntime
    {
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) => throw new NotSupportedException();
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args) => throw new NotSupportedException();
    }
}
