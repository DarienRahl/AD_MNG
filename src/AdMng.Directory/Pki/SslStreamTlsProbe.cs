using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using AdMng.Core.Computers.Remote;
using AdMng.Core.Pki.Tls;

namespace AdMng.Directory.Pki;

/// <summary>
/// The TLS handshake probe of a DC's LDAPS (636) and GC-LDAPS (3269) certificate (docs/dev/p-certificate-services.md 2.6,
/// decision 1): TCP plus a client <see cref="SslStream"/> handshake – no LDAP bind, no client certificate, no application
/// data – that copies the certificates the server presented (leaf first) and closes the stream right after. Every server
/// certificate is accepted (the probe judges nothing; <c>DcCertificateEvaluation</c> does, from the AD roots), the platform's
/// own chain build can download nothing (<c>DisableCertificateDownloads</c>, <c>RevocationMode = NoCheck</c>, and an empty
/// custom root store, so no system root store and no automatic root update either – decision 1), and nothing is logged or
/// written (rule 1). Stateless and thread-safe: one instance for the application.
/// </summary>
public sealed class SslStreamTlsProbe : IDomainControllerTlsProbe
{
    /// <summary>LDAP over TLS.</summary>
    public const int LdapsPort = 636;

    /// <summary>The global catalog over TLS.</summary>
    public const int GlobalCatalogLdapsPort = 3269;

    /// <summary>The timeout of decision 1 (callers pass it; the probe uses whatever it gets).</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(5);

    /// <summary>
    /// The TCP port actually connected for a validated port (tests: the in-process TLS server listens on an ephemeral port,
    /// while the probe's own port check still admits only 636 and 3269).
    /// </summary>
    internal Func<int, int> ConnectPort { get; init; } = static port => port;

    /// <inheritdoc />
    /// <remarks>
    /// <see cref="TlsProbeOutcome.InvalidHost"/> without any traffic for a name <see cref="HostNames.IsValid"/> refuses (IP
    /// addresses included – SNI and the name check need a name) or a port other than 636 / 3269. A refused connection is
    /// <see cref="TlsProbeOutcome.PortClosed"/>, a name the resolver does not know <see cref="TlsProbeOutcome.NameNotResolved"/>,
    /// an unreachable host or the probe's own <paramref name="timeout"/> <see cref="TlsProbeOutcome.Timeout"/>, a handshake the
    /// server breaks off (AD DS without an LDAPS certificate resets it; the exception type depends on the platform)
    /// <see cref="TlsProbeOutcome.HandshakeFailed"/>. The caller's <paramref name="cancellationToken"/> propagates as
    /// <see cref="OperationCanceledException"/>.
    /// </remarks>
    public async Task<TlsProbeResult> ProbeAsync(string host, int port, TimeSpan timeout, CancellationToken cancellationToken)
    {
        if (timeout <= TimeSpan.Zero && timeout != Timeout.InfiniteTimeSpan)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout), "The timeout must be positive.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (!HostNames.IsValid(host) || port is not (LdapsPort or GlobalCatalogLdapsPort))
        {
            return Result(host ?? string.Empty, port, TlsProbeOutcome.InvalidHost);
        }

        var name = HostNames.Normalize(host);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (timeout != Timeout.InfiniteTimeSpan)
        {
            deadline.CancelAfter(timeout);
        }

        using var client = new TcpClient();
        try
        {
            await client.ConnectAsync(name, ConnectPort(port), deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Result(name, port, TlsProbeOutcome.Timeout);
        }
        catch (SocketException ex)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Result(name, port, ConnectOutcome(ex.SocketErrorCode));
        }

        var capture = new Capture();
        try
        {
            await using var stream = new SslStream(client.GetStream(), leaveInnerStreamOpen: false);
            await stream.AuthenticateAsClientAsync(Options(name, capture), deadline.Token).ConfigureAwait(false);
            if (capture.Chain.Count == 0)
            {
                // A completed handshake always delivers the server certificate to the callback; without it nothing was seen.
                return Result(name, port, TlsProbeOutcome.HandshakeFailed);
            }

            return new TlsProbeResult(name, port, TlsProbeOutcome.Ok, stream.SslProtocol, capture.Chain, capture.PolicyErrors);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Result(name, port, TlsProbeOutcome.Timeout);
        }
        catch (Exception ex) when (ex is AuthenticationException or IOException or SocketException or ObjectDisposedException)
        {
            // The deadline closes the socket under a pending read on some platforms: that is still the probe's timeout.
            cancellationToken.ThrowIfCancellationRequested();
            return Result(name, port, deadline.IsCancellationRequested ? TlsProbeOutcome.Timeout : TlsProbeOutcome.HandshakeFailed);
        }
    }

    /// <summary>The outcome of a failed TCP connect.</summary>
    internal static TlsProbeOutcome ConnectOutcome(SocketError error) => error switch
    {
        SocketError.HostNotFound or SocketError.NoData or SocketError.TryAgain => TlsProbeOutcome.NameNotResolved,
        SocketError.TimedOut or SocketError.HostUnreachable or SocketError.NetworkUnreachable or SocketError.HostDown
            or SocketError.NetworkDown => TlsProbeOutcome.Timeout,
        _ => TlsProbeOutcome.PortClosed,
    };

    private static TlsProbeResult Result(string host, int port, TlsProbeOutcome outcome) => new(host, port, outcome, null, [], null);

    /// <summary>The handshake options of one probe (internal for the tests).</summary>
    internal static SslClientAuthenticationOptions Options(string name, Capture capture) => new()
    {
        TargetHost = name,

        // AD LDAPS sends a CertificateRequest: no certificate is offered (an empty collection and a selection that picks none).
        ClientCertificates = [],
        LocalCertificateSelectionCallback = static (_, _, _, _, _) => null!,
        CertificateRevocationCheckMode = X509RevocationMode.NoCheck,

        // A fresh policy per probe (the handshake adds the presented certificates to its ExtraStore): the platform chain
        // build inside the handshake can fetch no AIA / CRL / OCSP URL, and its roots are an empty custom store (decision 1:
        // CustomRootTrust) – the system store is never consulted, so Windows' automatic root update never fetches a root or
        // CTL for an untrusted (e.g. self-signed) DC certificate. The policy errors are informational only.
        CertificateChainPolicy = new X509ChainPolicy
        {
            DisableCertificateDownloads = true,
            RevocationMode = X509RevocationMode.NoCheck,
            TrustMode = X509ChainTrustMode.CustomRootTrust,
        },
        RemoteCertificateValidationCallback = capture.Accept,
    };

    /// <summary>What the validation callback saw: the presented certificates (copied – the callback's objects are disposed after it) and the policy errors.</summary>
    internal sealed class Capture
    {
        public List<byte[]> Chain { get; } = [];

        public SslPolicyErrors? PolicyErrors { get; private set; }

        /// <summary>
        /// Copies the leaf and the presented intermediates from <c>chain.ChainPolicy.ExtraStore</c> (where the platform puts
        /// what the server sent – <c>ChainElements</c> is the locally built chain), records the policy errors and accepts the
        /// certificate: returning false would turn every presented certificate into a failed handshake, and the stream is
        /// closed right after without a byte of application data.
        /// </summary>
        public bool Accept(object sender, X509Certificate? certificate, X509Chain? chain, SslPolicyErrors errors)
        {
            PolicyErrors = errors;
            Chain.Clear();
            if (certificate is null)
            {
                return true;
            }

            var leaf = certificate.GetRawCertData();
            Chain.Add(leaf);
            if (chain is not null)
            {
                foreach (var presented in chain.ChainPolicy.ExtraStore)
                {
                    var der = presented.RawDataMemory.ToArray();
                    if (!Chain.Any(c => c.AsSpan().SequenceEqual(der)))
                    {
                        Chain.Add(der);
                    }
                }
            }

            return true;
        }
    }
}
