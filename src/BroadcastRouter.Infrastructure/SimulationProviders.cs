using BroadcastRouter.Application;
using BroadcastRouter.Domain;

namespace BroadcastRouter.Infrastructure;

public sealed class SimulationDiscoveryProvider : IStreamDiscoveryProvider
{
    public Task<IReadOnlyList<DiscoveredSource>> DiscoverAsync(CancellationToken cancellationToken)
    {
        var source = new DiscoveredSource(
            new SourceIdentity("SIM-WOWZA", "live", "_definst_", "tip.stream"),
            "Simulation Feed",
            new Uri("rtsp://127.0.0.1:1935/live/tip.stream"),
            SourceState.Ready,
            100,
            new MediaProperties("h264", "aac", 1920, 1080, 25, 3_000_000, 48_000, 2, true),
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "simulation" },
            LastObservedAt: DateTimeOffset.UtcNow);
        return Task.FromResult<IReadOnlyList<DiscoveredSource>>([source]);
    }
}

public sealed class SimulationDeckLinkEnumerator : IDeckLinkEnumerator
{
    public Task<IReadOnlyList<DeckLinkPort>> EnumerateAsync(CancellationToken cancellationToken)
    {
        var modes = new[] { new VideoMode(1920, 1080, 25, 1, "uyvy422") };
        IReadOnlyList<DeckLinkPort> ports =
        [
            new("SIM-CARD-0-PORT-0", "Simulated DeckLink (1)", "DeckLink Quad Simulator", 0, 0, "SIM:00:00.0", modes, FriendlyName: "Studio Return 1", IdentityConfidence: "Simulation"),
            new("SIM-CARD-0-PORT-1", "Simulated DeckLink (2)", "DeckLink Quad Simulator", 0, 1, "SIM:00:00.0", modes, FriendlyName: "Studio Return 2", IdentityConfidence: "Simulation")
        ];
        return Task.FromResult(ports);
    }
}

public sealed class SimulationStreamProbe : IStreamProbe
{
    public Task<StreamProbeResult> ProbeAsync(Uri rtspUri, CancellationToken cancellationToken) =>
        Task.FromResult(new StreamProbeResult(true, true, new MediaProperties("h264", "aac", 1920, 1080, 25, 3_000_000, 48_000, 2, true), null, "Simulation probe succeeded.", true));
}
