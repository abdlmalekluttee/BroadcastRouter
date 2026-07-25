using BroadcastRouter.Domain;

namespace BroadcastRouter.Application;

public interface IStreamDiscoveryProvider
{
    Task<IReadOnlyList<DiscoveredSource>> DiscoverAsync(CancellationToken cancellationToken);
}

public interface IStreamProbe
{
    Task<StreamProbeResult> ProbeAsync(Uri rtspUri, CancellationToken cancellationToken);
}

public sealed record StreamProbeResult(bool Opened, bool FramesReceived, MediaProperties? Media, string? FailureCategory, string? Detail);

public interface IDeckLinkEnumerator
{
    Task<IReadOnlyList<DeckLinkPort>> EnumerateAsync(CancellationToken cancellationToken);
}

public interface IRouteProcessSupervisor
{
    Task StartAsync(RouteRecord route, DiscoveredSource source, DeckLinkPort port, OutputPreset preset, CancellationToken cancellationToken);
    Task StopAsync(SourceIdentity source, CancellationToken cancellationToken);
}

public interface IProfileRepository
{
    Task SaveAsync(string profileName, CancellationToken cancellationToken);
    Task LoadAsync(string profileName, CancellationToken cancellationToken);
}

public interface ICredentialResolver
{
    Task<CredentialValue> ResolveAsync(string reference, CancellationToken cancellationToken);
}

public sealed record CredentialValue(string UserName, string Password);
