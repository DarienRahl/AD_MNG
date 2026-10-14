using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text;

namespace AdMng.Watchdog.Testing.Smtp;

/// <summary>
/// A raw, scriptable SMTP server on 127.0.0.1 for the error and protocol paths a real server library hides (spec 22.2):
/// scripted replies per command (421/451/452, 535 5.7.139, 550 5.7.68, 552 …), XOAUTH2 (the AUTH payload is recorded),
/// servers without 8BITMIME. <see cref="Script"/> gets every command line (and <see cref="EndOfData"/> after a DATA body)
/// and returns a reply to override the default, or null. Everything stays in memory.
/// </summary>
public sealed class ScriptedSmtpServer : IAsyncDisposable
{
    /// <summary>Passed to the script after the DATA body was received.</summary>
    public const string EndOfData = "<END-OF-DATA>";

    private readonly ScriptedSmtpOptions _options;
    private readonly Lock _gate = new();
    private readonly List<string> _commands = [];
    private readonly List<byte[]> _messages = [];
    private readonly CancellationTokenSource _stop = new();
    private TcpListener? _listener;
    private Task? _accept;

    public ScriptedSmtpServer(ScriptedSmtpOptions? options = null)
    {
        _options = options ?? new ScriptedSmtpOptions();
    }

    /// <summary>Returns the reply for a command (e.g. "451 4.3.0 Try again later"), or null for the default reply.</summary>
    public Func<string, string?> Script { get; set; } = _ => null;

    public int Port { get; private set; }

    /// <summary>Every command line received, in order (AUTH lines include their payload).</summary>
    public IReadOnlyList<string> Commands
    {
        get
        {
            lock (_gate)
            {
                return [.. _commands];
            }
        }
    }

    /// <summary>Raw DATA bodies (dot-unstuffed) of the accepted messages.</summary>
    public IReadOnlyList<byte[]> Messages
    {
        get
        {
            lock (_gate)
            {
                return [.. _messages];
            }
        }
    }

    public Task StartAsync()
    {
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _accept = AcceptLoopAsync(_stop.Token);
        return Task.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync().ConfigureAwait(false);
        _listener?.Stop();
        if (_accept is not null)
        {
            try
            {
                await _accept.ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException)
            {
            }
        }

        _stop.Dispose();
    }

    private async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener!.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException)
            {
                return;
            }

            _ = Task.Run(() => ServeAsync(client, cancellationToken), cancellationToken);
        }
    }

    private async Task ServeAsync(TcpClient client, CancellationToken cancellationToken)
    {
        using (client)
        {
            Stream stream = client.GetStream();
            try
            {
                if (_options.ImplicitTlsCertificate is { } implicitCertificate)
                {
                    stream = await UpgradeAsync(stream, implicitCertificate, cancellationToken).ConfigureAwait(false);
                }

                await RunSessionAsync(stream, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or SocketException or AuthenticationException or OperationCanceledException or ObjectDisposedException)
            {
                // The client hung up or the test ended.
            }
            finally
            {
                await stream.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private async Task RunSessionAsync(Stream stream, CancellationToken cancellationToken)
    {
        var reader = new StreamReader(stream, Encoding.Latin1, false, 1024, leaveOpen: true);
        await WriteAsync(stream, "220 localhost ESMTP scripted", cancellationToken).ConfigureAwait(false);
        while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
        {
            lock (_gate)
            {
                _commands.Add(line);
            }

            var verb = line.Split(' ', 2)[0].ToUpperInvariant();
            if (Script(line) is { } scripted)
            {
                await WriteAsync(stream, scripted, cancellationToken).ConfigureAwait(false);
                if (verb == "QUIT" || scripted.StartsWith("421", StringComparison.Ordinal))
                {
                    return;
                }

                continue;
            }

            switch (verb)
            {
                case "EHLO":
                    await WriteAsync(stream, Ehlo(stream is SslStream), cancellationToken).ConfigureAwait(false);
                    break;
                case "STARTTLS" when _options.StartTlsCertificate is { } certificate && stream is not SslStream:
                    await WriteAsync(stream, "220 2.0.0 Ready to start TLS", cancellationToken).ConfigureAwait(false);
                    reader.Dispose();
                    stream = await UpgradeAsync(stream, certificate, cancellationToken).ConfigureAwait(false);
                    reader = new StreamReader(stream, Encoding.Latin1, false, 1024, leaveOpen: true);
                    break;
                case "AUTH":
                    if (line.Split(' ').Length < 3)
                    {
                        await WriteAsync(stream, "334 ", cancellationToken).ConfigureAwait(false);
                        var response = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                        lock (_gate)
                        {
                            _commands.Add(response ?? string.Empty);
                        }
                    }

                    await WriteAsync(stream, "235 2.7.0 Authentication successful", cancellationToken).ConfigureAwait(false);
                    break;
                case "MAIL":
                    await WriteAsync(stream, "250 2.1.0 Sender OK", cancellationToken).ConfigureAwait(false);
                    break;
                case "RCPT":
                    await WriteAsync(stream, "250 2.1.5 Recipient OK", cancellationToken).ConfigureAwait(false);
                    break;
                case "DATA":
                    await WriteAsync(stream, "354 Start mail input; end with <CRLF>.<CRLF>", cancellationToken).ConfigureAwait(false);
                    var body = await ReadDataAsync(reader, cancellationToken).ConfigureAwait(false);
                    if (Script(EndOfData) is { } dataReply)
                    {
                        await WriteAsync(stream, dataReply, cancellationToken).ConfigureAwait(false);
                    }
                    else
                    {
                        lock (_gate)
                        {
                            _messages.Add(body);
                        }

                        await WriteAsync(stream, "250 2.0.0 OK queued", cancellationToken).ConfigureAwait(false);
                    }

                    break;
                case "QUIT":
                    await WriteAsync(stream, "221 2.0.0 Bye", cancellationToken).ConfigureAwait(false);
                    return;
                default:
                    await WriteAsync(stream, "250 2.0.0 OK", cancellationToken).ConfigureAwait(false);
                    break;
            }
        }
    }

    private string Ehlo(bool secure)
    {
        var lines = new List<string> { "localhost" };
        lines.AddRange(_options.Extensions);
        if (_options.StartTlsCertificate is not null && !secure)
        {
            lines.Add("STARTTLS");
        }

        var builder = new StringBuilder();
        for (var i = 0; i < lines.Count; i++)
        {
            builder.Append("250").Append(i == lines.Count - 1 ? ' ' : '-').Append(lines[i]);
            if (i < lines.Count - 1)
            {
                builder.Append("\r\n");
            }
        }

        return builder.ToString();
    }

    private static async Task<byte[]> ReadDataAsync(StreamReader reader, CancellationToken cancellationToken)
    {
        var body = new StringBuilder();
        while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line && line != ".")
        {
            body.Append(line.StartsWith("..", StringComparison.Ordinal) ? line[1..] : line).Append("\r\n");
        }

        return Encoding.Latin1.GetBytes(body.ToString());
    }

    private static async Task<SslStream> UpgradeAsync(Stream stream, System.Security.Cryptography.X509Certificates.X509Certificate2 certificate, CancellationToken cancellationToken)
    {
        var ssl = new SslStream(stream, leaveInnerStreamOpen: false);
        await ssl.AuthenticateAsServerAsync(
            new SslServerAuthenticationOptions
            {
                ServerCertificate = certificate,
                EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
            },
            cancellationToken).ConfigureAwait(false);
        return ssl;
    }

    private static async Task WriteAsync(Stream stream, string reply, CancellationToken cancellationToken)
    {
        var bytes = Encoding.Latin1.GetBytes(reply + "\r\n");
        await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }
}
