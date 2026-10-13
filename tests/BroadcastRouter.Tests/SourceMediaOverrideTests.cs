using System.Security.Claims;
using BroadcastRouter.Domain;
using BroadcastRouter.Web.Components.Shared;
using BroadcastRouter.Web.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace BroadcastRouter.Tests;

public sealed class SourceMediaOverrideTests
{
    [Fact]
    public async Task SourcesPagePassesTheActualSourceIdentityToTheEditor()
    {
        await using var lab = await OfflineFallbackTests.RecoveryLab.Create();
        typeof(RouterCoordinator).GetMethod("Publish", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .Invoke(lab.Coordinator, ["Test source page"]);
        var services = new ServiceCollection().AddLogging().AddSingleton(lab.Coordinator)
            .AddSingleton<AuthenticationStateProvider>(new Auth("Administrator"))
            .AddSingleton<AuthorizedRouterCommands>().AddSingleton<AuthorizedPreviewCommands>()
            .AddSingleton<BroadcastRouter.Infrastructure.BrowserPreviewSupervisor>()
            .AddSingleton<OperatorErrorMessage>().AddSingleton<NavigationManager>(new TestNavigation());
        services.AddAuthorizationCore();
        await using var provider = services.BuildServiceProvider();
        await using var renderer = new HtmlRenderer(provider, provider.GetRequiredService<ILoggerFactory>());
        var html = await renderer.Dispatcher.InvokeAsync(async () =>
            (await renderer.RenderComponentAsync<CascadingAuthenticationState>(ParameterView.FromDictionary(new Dictionary<string, object?> {
                ["ChildContent"] = (RenderFragment)(builder => {
                    builder.OpenComponent<BroadcastRouter.Web.Components.Pages.Sources>(0);
                    builder.CloseComponent();
                }) }))).ToHtmlString());
        Assert.Contains($"aria-label=\"Media selection for {lab.Source.Value}\"", html);
        Assert.DoesNotContain("Media selection for source.Identity.Value", html);
    }

    private sealed class TestNavigation : NavigationManager
    {
        public TestNavigation() => Initialize("http://localhost/", "http://localhost/sources");
    }

    [Fact]
    public async Task FailedSaveKeepsDraftAndLastConfirmedRevision()
    {
        await using var lab = await OfflineFallbackTests.RecoveryLab.Create();
        var editor = new SourceMediaOverrideEditor();
        const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public;
        typeof(SourceMediaOverrideEditor).GetProperty("Coordinator", flags)!.SetValue(editor, lab.Coordinator);
        typeof(SourceMediaOverrideEditor).GetProperty("Commands", flags)!.SetValue(editor,
            new AuthorizedRouterCommands(lab.Coordinator, new Auth("Administrator")));
        typeof(SourceMediaOverrideEditor).GetProperty("OperatorErrors", flags)!.SetValue(editor,
            new OperatorErrorMessage(Microsoft.Extensions.Logging.Abstractions.NullLogger<OperatorErrorMessage>.Instance));
        typeof(SourceMediaOverrideEditor).GetProperty("SourceId", flags)!.SetValue(editor, lab.Source.Value);
        typeof(SourceMediaOverrideEditor).GetMethod("OnParametersSet", flags)!.Invoke(editor, null);
        typeof(SourceMediaOverrideEditor).GetField("mode", flags)!.SetValue(editor, SourceMediaOverride.ForceAudioOnly);
        lab.Settings.ConfigurationRevision++; // another operator changed settings
        await (Task)typeof(SourceMediaOverrideEditor).GetMethod("Save", flags)!.Invoke(editor, null)!;
        Assert.Equal(SourceMediaOverride.ForceAudioOnly, typeof(SourceMediaOverrideEditor).GetField("mode", flags)!.GetValue(editor));
        Assert.Equal(SourceMediaOverride.Auto, typeof(SourceMediaOverrideEditor).GetField("savedMode", flags)!.GetValue(editor));
        Assert.Equal(0L, typeof(SourceMediaOverrideEditor).GetField("revision", flags)!.GetValue(editor));
        Assert.Contains("selection is retained", (string)typeof(SourceMediaOverrideEditor).GetField("error", flags)!.GetValue(editor)!);
    }

    [Fact]
    public async Task OnlyAdministratorCanChangeOverride()
    {
        await using var lab = await OfflineFallbackTests.RecoveryLab.Create();
        var commands = new AuthorizedRouterCommands(lab.Coordinator, new Auth("Operator"));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => commands.SetSourceMediaOverrideAsync(
            lab.Source.Value, SourceMediaOverride.ForceAudioOnly, 0));
        Assert.Equal(SourceMediaOverride.Auto, lab.Settings.SourceMediaProfiles[0].Override);
    }

    [Fact]
    public async Task OverridePersistsWithRevisionGuardWithoutTouchingCurrentOwner()
    {
        await using var lab = await OfflineFallbackTests.RecoveryLab.Create();
        lab.Settings.DeckLinkPortOverrides.Add(new() { StableId = "TEST-OUTPUT", IsOutputPort = true,
            StandbyPresetId = lab.Settings.Presets[0].Id });
        var commands = new AuthorizedRouterCommands(lab.Coordinator, new Auth("Administrator"));
        var owner = lab.Owner.SessionId;
        var result = await commands.SetSourceMediaOverrideAsync(lab.Source.Value, SourceMediaOverride.ForceVideoOnly, 0);
        Assert.Equal(SourceMediaOverride.ForceVideoOnly, (await lab.Store.LoadSettingsAsync()).SourceMediaProfiles[0].Override);
        Assert.Equal(1, result.ConfigurationRevision);
        Assert.Equal(owner, lab.Owner.SessionId);
        Assert.Contains(await lab.Coordinator.ReadConfigurationAuditAsync(), entry =>
            entry.EventType == "SourceMediaOverride" && entry.PreviousState == "Auto" && entry.NewState == "ForceVideoOnly");
        await Assert.ThrowsAsync<InvalidOperationException>(() => commands.SetSourceMediaOverrideAsync(
            lab.Source.Value, SourceMediaOverride.ForceAudioOnly, 0));
        Assert.Equal(SourceMediaOverride.ForceVideoOnly, lab.Settings.SourceMediaProfiles[0].Override);
    }

    [Fact]
    public async Task FourOptionsDefaultToAutoInsideClosedAdvancedDetails()
    {
        await using var lab = await OfflineFallbackTests.RecoveryLab.Create();
        await using var provider = new ServiceCollection().AddLogging().AddSingleton(lab.Coordinator)
            .AddSingleton(new AuthorizedRouterCommands(lab.Coordinator, new Auth("Administrator")))
            .AddSingleton<OperatorErrorMessage>().BuildServiceProvider();
        await using var renderer = new HtmlRenderer(provider, provider.GetRequiredService<ILoggerFactory>());
        var html = System.Net.WebUtility.HtmlDecode(await renderer.Dispatcher.InvokeAsync(async () =>
            (await renderer.RenderComponentAsync<SourceMediaOverrideEditor>(ParameterView.FromDictionary(new Dictionary<string, object?> {
                ["SourceId"] = lab.Source.Value }))).ToHtmlString()));
        Assert.Contains("Advanced source details", html);
        Assert.DoesNotContain("<details open", html);
        Assert.Equal(4, System.Text.RegularExpressions.Regex.Matches(html, "<option ").Count);
        Assert.Contains("Auto (recommended)", html);
        Assert.Contains("Force video + audio", html);
        Assert.Contains("Force video-only", html);
        Assert.Contains("Force audio-only", html);
        Assert.Contains("audio disabled by preset", html);
    }

    internal sealed class Auth(string role) : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync() => Task.FromResult(new AuthenticationState(
            new ClaimsPrincipal(new ClaimsIdentity([new(ClaimTypes.Name, "Test administrator"), new(ClaimTypes.Role, role)], "Test"))));
    }
}
