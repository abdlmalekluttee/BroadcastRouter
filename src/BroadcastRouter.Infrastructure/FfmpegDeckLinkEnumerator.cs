using BroadcastRouter.Application;
using BroadcastRouter.Domain;

namespace BroadcastRouter.Infrastructure;

public sealed class FfmpegDeckLinkEnumerator(
    IReadOnlyList<DeckLinkSink> outputDevices,
    string identityHelperPath) : IDeckLinkEnumerator
{
    public async Task<IReadOnlyList<DeckLinkPort>> EnumerateAsync(CancellationToken cancellationToken)
    {
        if (outputDevices.Count == 0) return [];
        var identityProbe = await DeckLinkIdentityProcessProbe
            .EnumerateAsync(identityHelperPath, cancellationToken)
            .ConfigureAwait(false);
        if (!identityProbe.Success)
            throw new InvalidOperationException(identityProbe.Error ?? "The isolated DeckLink identity query failed.");
        return DeckLinkIdentityResolver.Resolve(outputDevices, identityProbe.Identities);
    }
}
