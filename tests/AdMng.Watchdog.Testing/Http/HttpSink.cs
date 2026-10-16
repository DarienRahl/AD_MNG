using System.Net;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace AdMng.Watchdog.Testing.Http;

/// <summary>
/// In-process HTTP(S) endpoint on loopback that records every request and answers with a scripted response (Teams and
/// webhook transport tests, end-to-end tests). Kestrel on 127.0.0.1 with an ephemeral port; HTTPS when a certificate is
/// given (e.g. <c>TestCertificates.Localhost</c>). Nothing is written to disk.
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
    private readonly X509Certificate2? _certificate;
    private readonly Lock _gate = new();
    private readonly List<HttpSinkRequest> _requests = [];
    private readonly List<(int Count, TaskCompletionSource Completion)> _waiters = [];
    private Func<HttpSinkRequest, HttpSinkResponse> _responder = _ => HttpSinkResponse.Accepted;
    private WebApplication? _app;
    private Uri? _baseAddress;

    /// <summary>A plain-HTTP sink.</summary>
    public HttpSink()
        : this(certificate: null)
    {
    }

    /// <summary>An HTTPS sink with <paramref name="certificate"/> (null = plain HTTP).</summary>
    public HttpSink(X509Certificate2? certificate)
    {
        _certificate = certificate;
    }

    /// <summary>Base address of the started sink, e.g. https://127.0.0.1:49152/.</summary>
    public Uri BaseAddress => _baseAddress ?? throw new InvalidOperationException("The sink is not started.");

    /// <summary>Requests received so far, in arrival order.</summary>
    public IReadOnlyList<HttpSinkRequest> Requests
    {
        get
        {
            lock (_gate)
            {
                return [.. _requests];
            }
        }
    }

    /// <summary>Starts listening on an ephemeral loopback port.</summary>
    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (_app is not null)
        {
            throw new InvalidOperationException("The sink is already started.");
        }

        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(kestrel => kestrel.Listen(IPAddress.Loopback, 0, listen =>
        {
            if (_certificate is not null)
            {
                listen.UseHttps(_certificate);
            }
        }));
        var app = builder.Build();
        app.Run(HandleAsync);
        await app.StartAsync(cancellationToken).ConfigureAwait(false);
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        _baseAddress = new Uri(address.EndsWith('/') ? address : address + "/");
        _app = app;
    }

    /// <summary>Answers every following request with <paramref name="responder"/> (default: 202 Accepted).</summary>
    public void Script(Func<HttpSinkRequest, HttpSinkResponse> responder)
    {
        ArgumentNullException.ThrowIfNull(responder);
        lock (_gate)
        {
            _responder = responder;
        }
    }

    /// <summary>Completes when at least <paramref name="count"/> requests have arrived.</summary>
    public Task WaitForRequestsAsync(int count, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            if (_requests.Count >= count)
            {
                return Task.CompletedTask;
            }

            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _waiters.Add((count, completion));
            return completion.Task.WaitAsync(cancellationToken);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_app is not null)
        {
            await _app.StopAsync(CancellationToken.None).ConfigureAwait(false);
            await _app.DisposeAsync().ConfigureAwait(false);
            _app = null;
        }
    }

    private async Task HandleAsync(HttpContext context)
    {
        using var body = new MemoryStream();
        await context.Request.Body.CopyToAsync(body, context.RequestAborted).ConfigureAwait(false);
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, values) in context.Request.Headers)
        {
            headers[name] = string.Join(", ", values.ToArray());
        }

        var request = new HttpSinkRequest
        {
            Method = context.Request.Method,
            PathAndQuery = context.Request.Path.Value + context.Request.QueryString.Value,
            Headers = headers,
            Body = body.ToArray(),
            ReceivedAt = DateTimeOffset.UtcNow,
        };
        Func<HttpSinkRequest, HttpSinkResponse> responder;
        List<TaskCompletionSource> completed = [];
        lock (_gate)
        {
            _requests.Add(request);
            responder = _responder;
            foreach (var waiter in _waiters.Where(w => _requests.Count >= w.Count).ToList())
            {
                completed.Add(waiter.Completion);
                _waiters.Remove(waiter);
            }
        }

        foreach (var completion in completed)
        {
            completion.TrySetResult();
        }

        var response = responder(request);
        if (response.HoldUntil is { } hold)
        {
            try
            {
                await hold.WaitAsync(context.RequestAborted).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;   // the client gave up (timeout test)
            }
        }

        context.Response.StatusCode = response.StatusCode;
        foreach (var (name, value) in response.Headers)
        {
            context.Response.Headers[name] = value;
        }

        if (response.Body is not null)
        {
            context.Response.ContentType = response.ContentType;
            await context.Response.WriteAsync(response.Body, context.RequestAborted).ConfigureAwait(false);
        }
    }
}
