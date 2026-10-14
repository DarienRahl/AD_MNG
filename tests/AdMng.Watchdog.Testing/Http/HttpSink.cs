namespace AdMng.Watchdog.Testing.Http;

/// <summary>
/// In-process HTTP(S) endpoint on loopback that records every request and answers with a scripted response (Teams and
/// webhook transport tests, end-to-end tests). Scaffold shell with the final public API – implemented by W-service.
/// </summary>
/// <example>
/// <code>
/// await using var sink = new HttpSink();
/// sink.Script(request => request.Headers.ContainsKey("webhook-signature") ? HttpSinkResponse.Accepted : HttpSinkResponse.Status(401));
/// await sink.StartAsync(ct);
/// // … send to sink.BaseAddress …
/// var received = Assert.Single(sink.Requests);
/// </code>
/// </example>
public sealed class HttpSink : IAsyncDisposable
{
    /// <summary>Base address of the started sink, e.g. https://127.0.0.1:49152/.</summary>
    public Uri BaseAddress => throw new NotImplementedException();

    /// <summary>Requests received so far, in arrival order.</summary>
    public IReadOnlyList<HttpSinkRequest> Requests => throw new NotImplementedException();

    /// <summary>Starts listening on an ephemeral loopback port.</summary>
    public Task StartAsync(CancellationToken cancellationToken = default) => throw new NotImplementedException();

    /// <summary>Answers every following request with <paramref name="responder"/> (default: 202 Accepted).</summary>
    public void Script(Func<HttpSinkRequest, HttpSinkResponse> responder) => throw new NotImplementedException();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
