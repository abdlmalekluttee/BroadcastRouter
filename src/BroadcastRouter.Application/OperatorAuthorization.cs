using System.Security.Claims;

namespace BroadcastRouter.Application;

public static class OperatorAuthorization
{
    public static void EnsureAdministrator(ClaimsPrincipal principal)
    {
        if (principal.Identity?.IsAuthenticated != true || !principal.IsInRole("Administrator"))
            throw new UnauthorizedAccessException("Administrator authorization is required for route-control commands.");
    }
}
