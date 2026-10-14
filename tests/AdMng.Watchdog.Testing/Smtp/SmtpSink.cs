using System.Buffers;
using System.Net;
using System.Security.Authentication;
using AdMng.Watchdog.Testing.Certificates;
using AdMng.Watchdog.Testing.Net;
using SmtpServer;
using SmtpServer.Authentication;
using SmtpServer.Protocol;
using SmtpServer.Storage;
using SmtpServiceProvider = SmtpServer.ComponentModel.ServiceProvider;
using SmtpServerHost = SmtpServer.SmtpServer;

namespace AdMng.Watchdog.Testing.Smtp;

/// <summary>
/// In-process SMTP server on 127.0.0.1 (SmtpServer package) for the e-mail transport tests: plain, STARTTLS or implicit
/// TLS, optional AUTH PLAIN/LOGIN (offered only after TLS), SIZE. It records every message and every AUTH attempt
/// (user names only), so a test can assert that no credential reached it. Messages stay in memory.
/// </summary>
public sealed class SmtpSink : IAsyncDisposable
{
    private readonly SmtpSinkOptions _options;
    private readonly Lock _gate = new();
    private readonly List<SmtpSinkMessage> _messages = [];
    private readonly List<string> _authenticationAttempts = [];
    private readonly List<(int Count, TaskCompletionSource Completion)> _waiters = [];
    private SmtpServerHost? _server;
    private CancellationTokenSource? _stop;
    private Task? _run;

    public SmtpSink(SmtpSinkOptions? options = null)
    {
        _options = options ?? new SmtpSinkOptions();
    }

    public string Host => "127.0.0.1";

    /// <summary>The listening port (after <see cref="StartAsync"/>).</summary>
    public int Port { get; private set; }

    public IReadOnlyList<SmtpSinkMessage> Messages
    {
        get
        {
            lock (_gate)
            {
                return [.. _messages];
            }
        }
    }

    /// <summary>User names of every AUTH attempt (successful or not).</summary>
    public IReadOnlyList<string> AuthenticationAttempts
    {
        get
        {
            lock (_gate)
            {
                return [.. _authenticationAttempts];
            }
        }
    }

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (_server is not null)
        {
            throw new InvalidOperationException("The sink is already started.");
        }

        Port = FreeTcpPort.Next();
        var certificate = _options.Security == SmtpSinkSecurity.Plain ? null : _options.Certificate ?? TestCertificates.Localhost;
        var builder = new SmtpServerOptionsBuilder()
            .ServerName("localhost")
            .Endpoint(endpoint =>
            {
                endpoint.Endpoint(new IPEndPoint(IPAddress.Loopback, Port))
                    .IsSecure(_options.Security == SmtpSinkSecurity.ImplicitTls)
                    .AuthenticationRequired(_options.RequireAuthentication)
                    .AllowUnsecureAuthentication(false)
                    .SupportedSslProtocols(SslProtocols.Tls12 | SslProtocols.Tls13);
                if (certificate is not null)
                {
                    endpoint.Certificate(certificate);
                }
            });
        if (_options.MaxMessageSize > 0)
        {
            builder.MaxMessageSize(_options.MaxMessageSize, MaxMessageSizeHandling.Strict);
        }

        var services = new SmtpServiceProvider();
        services.Add(new RecordingStore(this));
        services.Add(new RecordingAuthenticator(this));
        _server = new SmtpServerHost(builder.Build(), services);
        _stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _run = _server.StartAsync(_stop.Token);
        return Task.CompletedTask;
    }

    /// <summary>Completes when at least <paramref name="count"/> messages have arrived.</summary>
    public Task WaitForMessagesAsync(int count, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            if (_messages.Count >= count)
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
        if (_server is null)
        {
            return;
        }

        _server.Shutdown();
        await _stop!.CancelAsync().ConfigureAwait(false);
        try
        {
            await _run!.ConfigureAwait(false);
            await _server.ShutdownTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }

        _stop.Dispose();
        _server = null;
    }

    private void Record(SmtpSinkMessage message)
    {
        List<TaskCompletionSource> completed = [];
        lock (_gate)
        {
            _messages.Add(message);
            foreach (var waiter in _waiters.Where(w => _messages.Count >= w.Count).ToList())
            {
                completed.Add(waiter.Completion);
                _waiters.Remove(waiter);
            }
        }

        foreach (var completion in completed)
        {
            completion.TrySetResult();
        }
    }

    private bool Authenticate(string user, string password)
    {
        lock (_gate)
        {
            _authenticationAttempts.Add(user);
        }

        return _options.Accounts.TryGetValue(user, out var expected) && string.Equals(expected, password, StringComparison.Ordinal);
    }

    private sealed class RecordingStore(SmtpSink sink) : MessageStore
    {
        public override Task<SmtpResponse> SaveAsync(ISessionContext context, IMessageTransaction transaction, ReadOnlySequence<byte> buffer, CancellationToken cancellationToken)
        {
            sink.Record(new SmtpSinkMessage
            {
                MailFrom = transaction.From is { } from ? $"{from.User}@{from.Host}" : null,
                Recipients = [.. transaction.To.Select(t => $"{t.User}@{t.Host}")],
                Data = buffer.ToArray(),
                Secure = context.Pipe.IsSecure,
                AuthenticatedUser = context.Authentication.IsAuthenticated ? context.Authentication.User : null,
                ReceivedAt = DateTimeOffset.UtcNow,
            });
            return Task.FromResult(SmtpResponse.Ok);
        }
    }

    private sealed class RecordingAuthenticator(SmtpSink sink) : IUserAuthenticator
    {
        public Task<bool> AuthenticateAsync(ISessionContext context, string user, string password, CancellationToken cancellationToken) =>
            Task.FromResult(sink.Authenticate(user, password));
    }
}
