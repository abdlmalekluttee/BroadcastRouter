using BroadcastRouter.Application;
using BroadcastRouter.Domain;

namespace BroadcastRouter.Infrastructure;

public sealed class FfmpegDeckLinkEnumerator(string ffmpegPath) : IDeckLinkEnumerator
{
    public async Task<IReadOnlyList<DeckLinkPort>> EnumerateAsync(CancellationToken cancellationToken)
    {
        var diagnostic = await FfmpegDiagnostics.InspectAsync(ffmpegPath, cancellationToken).ConfigureAwait(false);
        if (!diagnostic.HasDeckLinkOutput) return [];
        return DeckLinkIdentityResolver.Resolve(diagnostic.OutputDevices, DeckLinkSdkIdentityEnumerator.Enumerate());
    }
}
