using BroadcastRouter.Domain;
using System.IO.Pipes;
using System.Text;

namespace BroadcastRouter.Infrastructure;

internal sealed class FfmpegMediaChannel : IAsyncDisposable
{
    private readonly NamedPipeServerStream _pipe;
    private readonly CancellationTokenSource _stop = new();
    private readonly object _disposeGate = new();
    private Task? _dispose;

    internal FfmpegMediaChannel(MediaSignalKind kind, Action<FfmpegMediaObservation?> observe)
    {
        ArgumentNullException.ThrowIfNull(observe);
        if (!Enum.IsDefined(kind)) throw new ArgumentOutOfRangeException(nameof(kind));
        PipeName = "broadcast-router-media-" + Guid.NewGuid().ToString("N");
        _pipe = new(PipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly, 4096, 4096);
        Completion = ReadAsync(kind, observe);
    }

    internal string PipeName { get; }
    internal string Endpoint => @"\\.\pipe\" + PipeName;
    internal Task Completion { get; }

    private async Task ReadAsync(MediaSignalKind kind, Action<FfmpegMediaObservation?> observe)
    {
        // Isolated channels avoid FFmpeg's independently buffered writers
        // concatenating audio/video records. A hostile/invalid line cannot grow
        // the buffer: discard it through the next LF, then resume framing.
        var chunk = new byte[512];
        var record = new byte[256];
        var length = 0;
        var discard = false;
        try
        {
            await _pipe.WaitForConnectionAsync(_stop.Token).ConfigureAwait(false);
            while (true)
            {
                var count = await _pipe.ReadAsync(chunk, _stop.Token).ConfigureAwait(false);
                if (count == 0) break;
                for (var i = 0; i < count; i++)
                {
                    var value = chunk[i];
                    if (value == (byte)'\n')
                    {
                        if (!discard)
                        {
                            var text = Encoding.ASCII.GetString(record, 0, length).TrimEnd('\r');
                            observe(FfmpegMediaStats.TryParse(kind, text, out var observation) ? observation : null);
                        }
                        length = 0;
                        discard = false;
                    }
                    else if (!discard)
                    {
                        if (length == record.Length || value > 127)
                        {
                            discard = true;
                            observe(null);
                        }
                        else record[length++] = value;
                    }
                }
            }
            // EOF or a transport fault invalidates instrumentation. It is not
            // failed-media evidence, even after valid observations earlier.
            if (!_stop.IsCancellationRequested) observe(null);
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        catch (ObjectDisposedException) when (_stop.IsCancellationRequested) { }
        catch (IOException) { if (!_stop.IsCancellationRequested) observe(null); }
    }

    public ValueTask DisposeAsync()
    {
        lock (_disposeGate) return new(_dispose ??= DisposeCoreAsync());
    }

    private async Task DisposeCoreAsync()
    {
        _stop.Cancel();
        _pipe.Dispose();
        try { await Completion.ConfigureAwait(false); }
        finally { _stop.Dispose(); }
    }
}
