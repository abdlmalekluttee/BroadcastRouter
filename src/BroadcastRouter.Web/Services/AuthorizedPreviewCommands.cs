using BroadcastRouter.Application;
using BroadcastRouter.Infrastructure;
using Microsoft.AspNetCore.Components.Authorization;

namespace BroadcastRouter.Web.Services;

public sealed class AuthorizedPreviewCommands(
    RouterCoordinator coordinator,
    BrowserPreviewSupervisor preview,
    AuthenticationStateProvider authenticationStateProvider)
{
    public async Task StartAsync(string sourceId, CancellationToken cancellationToken = default)
    {
        var principal = (await authenticationStateProvider.GetAuthenticationStateAsync()).User;
        OperatorAuthorization.EnsureAdministrator(principal);
        var source = coordinator.Snapshot.Sources.FirstOrDefault(item => item.Identity.Value.Equals(sourceId, StringComparison.Ordinal))
                     ?? throw new InvalidOperationException("The selected source is no longer available.");
        await preview.StartAsync(source, coordinator.GetSettings().MediaTools, cancellationToken);
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        var principal = (await authenticationStateProvider.GetAuthenticationStateAsync()).User;
        OperatorAuthorization.EnsureAdministrator(principal);
        await preview.StopAsync(cancellationToken);
    }
}
