using BroadcastRouter.Application;
using BroadcastRouter.Domain;
using Microsoft.AspNetCore.Components.Authorization;

namespace BroadcastRouter.Web.Services;

public sealed class AuthorizedRouterCommands(
    RouterCoordinator coordinator,
    AuthenticationStateProvider authenticationStateProvider)
{
    public async Task ExecuteAsync(string action, string? sourceId = null, string? portId = null, string? presetId = null,
        AssignmentMode assignmentMode = AssignmentMode.Manual, bool reserveWhileOffline = true,
        bool allowTemporaryUse = false,
        CancellationToken cancellationToken = default)
    {
        var principal = (await authenticationStateProvider.GetAuthenticationStateAsync()).User;
        OperatorAuthorization.EnsureAdministrator(principal);
        await coordinator.CommandAsync(action, sourceId, portId, presetId, assignmentMode, reserveWhileOffline,
            allowTemporaryUse, cancellationToken);
    }
}
