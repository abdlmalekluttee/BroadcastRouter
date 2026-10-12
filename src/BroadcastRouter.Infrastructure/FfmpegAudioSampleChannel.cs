using System.IO.Pipes;

namespace BroadcastRouter.Infrastructure;

internal sealed class FfmpegAudioSampleChannel : IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly object _disposeGate = new();
    private readonly HashSet<object> _writers = [];
    private readonly List<NamedPipeServerStream> _pipes = [];
    private readonly CancellationTokenSource _stop = new();
    private readonly Action<FfmpegAudioSample?> _observe;
    private Task? _dispose;

    internal FfmpegAudioSampleChannel(Action<FfmpegAudioSample?> observe)
    {
        _observe = observe ?? throw new ArgumentNullException(nameof(observe));
        PipeName = "broadcast-router-media-" + Guid.NewGuid().ToString("N");
        try
        {
            // FFmpeg inspects and destroys the graph before constructing the
            // playback graph. Keep one spare listener ready for its next open.
            for (var i = 0; i < 2; i++)
                _pipes.Add(new(PipeName, PipeDirection.In, 2, PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly, 4096, 4096));
            Completion = Task.WhenAll(_pipes.Select(ReadConnectionsAsync));
        }
        catch
        {
            foreach (var pipe in _pipes) pipe.Dispose();
            _stop.Dispose();
            throw;
        }
    }

    internal string PipeName { get; }
    internal string Endpoint => @"\\.\pipe\" + PipeName;
    internal Task Completion { get; }

    private async Task ReadConnectionsAsync(NamedPipeServerStream pipe)
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                await pipe.WaitForConnectionAsync(_stop.Token).ConfigureAwait(false);
                var owner = new object();
                var parser = new FfmpegAudioSampleParser();
                try
                {
                    await MediaObservationPipe.ReadLinesAsync(pipe, text =>
                    {
                        var state = parser.Accept(text ?? "", out var sample);
                        lock (_gate)
                        {
                            if (_stop.IsCancellationRequested) return;
                            if (state == AudioSampleRecordState.Sample)
                            {
                                _writers.Add(owner);
                                // Never arbitrarily pick a winner when two
                                // reinitializing graphs still produce data.
                                _observe(_writers.Count == 1 ? sample : null);
                            }
                            else if (state == AudioSampleRecordState.Invalid) _observe(null);
                        }
                    }, _stop.Token).ConfigureAwait(false);
                }
                catch (IOException) { }
                finally
                {
                    lock (_gate)
                    {
                        var producedSamples = _writers.Remove(owner);
                        if (!_stop.IsCancellationRequested && (producedSamples || _writers.Count == 0)) _observe(null);
                    }
                }
                if (!_stop.IsCancellationRequested) pipe.Disconnect();
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        catch (ObjectDisposedException) when (_stop.IsCancellationRequested) { }
        catch (IOException) { if (!_stop.IsCancellationRequested) { lock (_gate) _observe(null); } }
    }

    public ValueTask DisposeAsync()
    {
        lock (_disposeGate) return new(_dispose ??= DisposeCoreAsync());
    }

    private async Task DisposeCoreAsync()
    {
        _stop.Cancel();
        foreach (var pipe in _pipes) pipe.Dispose();
        try { await Completion.ConfigureAwait(false); }
        finally { _stop.Dispose(); }
    }
}
