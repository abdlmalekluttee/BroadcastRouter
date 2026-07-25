using BroadcastRouter.Application;

namespace BroadcastRouter.Infrastructure;

public sealed class StaticCredentialResolver(CredentialValue credential) : ICredentialResolver
{
    public Task<CredentialValue> ResolveAsync(string reference, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(credential);
    }
}
