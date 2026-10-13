using BroadcastRouter.Application;
using BroadcastRouter.Domain;
using Microsoft.AspNetCore.Components.Authorization;

namespace BroadcastRouter.Web.Services;

public sealed class AuthorizedRouterCommands(
    RouterCoordinator coordinator,
    AuthenticationStateProvider authenticationStateProvider)
{
    public async Task<SettingsApplyResult> SetSourceMediaOverrideAsync(string sourceId, SourceMediaOverride mode,
        long expectedRevision, CancellationToken cancellationToken = default)
    {
        var principal = (await authenticationStateProvider.GetAuthenticationStateAsync()).User;
        OperatorAuthorization.EnsureAdministrator(principal);
        var settings = coordinator.GetSettings();
        SettingsConcurrencyPolicy.EnsureCurrent(expectedRevision, settings.ConfigurationRevision);
        var profile = settings.SourceMediaProfiles.SingleOrDefault(p => p.SourceId == sourceId);
        if (profile is null) settings.SourceMediaProfiles.Add(profile = new() { SourceId = sourceId });
        profile.Override = mode;
        SourceMediaPolicy.Validate(settings);
        // SaveSettings repeats the revision check under the mutation gate.
        return await coordinator.SaveSettingsAsync(settings, principal.Identity?.Name ?? "administrator", cancellationToken);
    }

    public async Task<SettingsApplyResult> SaveSettingsAsync(OperatorSettings settings,
        CancellationToken cancellationToken = default)
    {
        var principal = (await authenticationStateProvider.GetAuthenticationStateAsync()).User;
        OperatorAuthorization.EnsureAdministrator(principal);
        return await coordinator.SaveSettingsAsync(settings, principal.Identity?.Name ?? "administrator", cancellationToken);
    }

    public async Task ExecuteAsync(string action, string? sourceId = null, string? portId = null, string? presetId = null,
        AssignmentMode assignmentMode = AssignmentMode.Manual, bool reserveWhileOffline = true,
        bool allowTemporaryUse = false,
        CancellationToken cancellationToken = default)
    {
        var principal = (await authenticationStateProvider.GetAuthenticationStateAsync()).User;
        OperatorAuthorization.EnsureAdministrator(principal);
        await coordinator.CommandAsync(action, sourceId, portId, presetId, assignmentMode, reserveWhileOffline,
            allowTemporaryUse, principal.Identity?.Name ?? "administrator", cancellationToken);
    }

    public async Task<IReadOnlyList<string>> SaveAssignmentAsync(string sourceId, string portId, string presetId,
        AssignmentMode mode, bool reserveWhileOffline, bool allowTemporaryUse, CancellationToken cancellationToken = default)
    {
        await ExecuteAsync("save-assignment", sourceId, portId, presetId, mode, reserveWhileOffline, allowTemporaryUse, cancellationToken);
        return coordinator.GetMediaCompatibilityWarnings(sourceId);
    }
}
