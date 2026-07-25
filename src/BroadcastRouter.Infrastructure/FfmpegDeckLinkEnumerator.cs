using System.Security.Cryptography;
using System.Text;
using BroadcastRouter.Application;
using BroadcastRouter.Domain;

namespace BroadcastRouter.Infrastructure;

public sealed class FfmpegDeckLinkEnumerator(string ffmpegPath) : IDeckLinkEnumerator
{
    public async Task<IReadOnlyList<DeckLinkPort>> EnumerateAsync(CancellationToken cancellationToken)
    {
        var diagnostic = await FfmpegDiagnostics.InspectAsync(ffmpegPath, cancellationToken).ConfigureAwait(false);
        if (!diagnostic.HasDeckLinkOutput) return [];
        return diagnostic.OutputDevices.Select((device, index) => new DeckLinkPort(
            StableId: $"FFMPEG-NAME-{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(device.FfmpegAddress)))[..16]}",
            FfmpegName: device.FfmpegAddress,
            ModelName: device.DisplayName,
            CardIndex: index,
            SubdeviceIndex: 0,
            PciLocation: null,
            SupportedModes: [],
            IsAvailable: true,
            FriendlyName: device.DisplayName,
            IdentityConfidence: "FFmpeg name only — verify after driver/topology changes")).ToArray();
    }
}
