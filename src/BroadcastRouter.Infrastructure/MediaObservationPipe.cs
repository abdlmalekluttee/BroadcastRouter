using System.Text;

namespace BroadcastRouter.Infrastructure;

internal static class MediaObservationPipe
{
    internal static async Task ReadLinesAsync(Stream stream, Action<string?> accept, CancellationToken cancellationToken)
    {
        var chunk = new byte[512];
        var record = new byte[256];
        var length = 0;
        var discard = false;
        while (true)
        {
            var count = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false);
            if (count == 0) return; // Incomplete final records are never accepted.
            for (var i = 0; i < count; i++)
            {
                var value = chunk[i];
                if (value == (byte)'\n')
                {
                    if (!discard) accept(Encoding.ASCII.GetString(record, 0, length).TrimEnd('\r'));
                    length = 0;
                    discard = false;
                }
                else if (!discard)
                {
                    if (length == record.Length || value > 127)
                    {
                        discard = true;
                        accept(null);
                    }
                    else record[length++] = value;
                }
            }
        }
    }
}
