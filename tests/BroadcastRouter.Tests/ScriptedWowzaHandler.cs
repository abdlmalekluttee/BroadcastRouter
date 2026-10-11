using System.Collections.Concurrent;
using System.Net;
using System.Text;

namespace BroadcastRouter.Tests;

internal sealed record RestStep(HttpStatusCode Status, string Body, bool Timeout = false);

internal sealed class ScriptedWowzaHandler(params RestStep[] steps) : HttpMessageHandler
{
    private readonly ConcurrentQueue<RestStep> _steps = new(steps);
    private int _requests;
    public int Requests => Volatile.Read(ref _requests);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _requests);
        if (!_steps.TryDequeue(out var step)) throw new InvalidOperationException("Synthetic REST script exhausted");
        if (step.Timeout) await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        return new HttpResponseMessage(step.Status)
        {
            Content = new StringContent(step.Body, Encoding.UTF8, "application/json")
        };
    }

    public static RestStep Publisher(bool connected, long uptimeSeconds) => new(HttpStatusCode.OK,
        "{\"incomingStreams\":[{\"name\":\"synthetic.stream\",\"isConnected\":"
        + (connected ? "true" : "false") + ",\"uptime\":" + uptimeSeconds + "}]}");
}
