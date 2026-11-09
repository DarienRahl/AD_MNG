using System.Formats.Asn1;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using AdMng.Core.Pki;

namespace AdMng.Presentation.Pki;

/// <summary>
/// The HTTP check of AIA / CDP / OCSP URLs (docs/dev/p-certificate-services.md 2.5, W5; modelled on
/// <c>PolicyTemplatePackageLoader</c>): only absolute <c>http</c>/<c>https</c> URLs are contacted (<c>file:</c>, UNC and
/// <c>ldap://host</c> are <see cref="PkiHttpOutcome.NotChecked"/> without a connection); default handler
/// <c>SocketsHttpHandler { AllowAutoRedirect = false, Credentials = null, ConnectTimeout = 5 s, SslOptions.CertificateChainPolicy
/// = { DisableCertificateDownloads = true, RevocationMode = NoCheck } }</c> with the system proxy's credentials only; at most 3
/// redirects (each target checked again), 15 s per URL including the body, 20 MB cap, 4 URLs in parallel. AIA bodies parse
/// as a DER or PEM certificate (or the first CA certificate of a PKCS #7 bundle), CDP bodies as a DER or PEM CRL; OCSP is
/// reachability only (any HTTP response is <see cref="PkiHttpOutcome.Ok"/> with its status code). Singleton; nothing kept –
/// the bodies are dropped after parsing (rule 1).
/// </summary>
/// <param name="handler">The HTTP handler factory (tests pass a fake; null: the default handler above).</param>
public sealed class PkiHealthProbe(Func<HttpMessageHandler>? handler = null) : IPkiHealthProbe
{
    /// <summary>At most this many redirects are followed (each target checked again).</summary>
    public const int MaxRedirects = 3;

    /// <summary>At most this many URLs at a time.</summary>
    public const int MaxParallel = 4;

    /// <summary>The handler factory of the tests, null for the default handler.</summary>
    public Func<HttpMessageHandler>? Handler { get; } = handler;

    /// <summary>The time per URL, redirects and body included (15 s).</summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>The largest body read (20 MB); a larger one is <see cref="PkiHttpOutcome.TooLarge"/>.</summary>
    public long MaxBodyBytes { get; init; } = 20L * 1024 * 1024;

    public async Task<IReadOnlyList<PkiHttpCheckResult>> CheckAsync(IReadOnlyList<PkiUrl> urls, IProgress<int>? progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(urls);
        cancellationToken.ThrowIfCancellationRequested();
        if (urls.Count == 0)
        {
            return [];
        }

        // No client-wide timeout: every URL has its own, which also covers the redirects and the body.
        using var client = new HttpClient((Handler ?? DefaultHandler)(), disposeHandler: true) { Timeout = System.Threading.Timeout.InfiniteTimeSpan };
        using var gate = new SemaphoreSlim(MaxParallel);
        var done = 0;
        var checks = urls.Select(async url =>
        {
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                return await CheckOneAsync(client, url, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                gate.Release();
                progress?.Report(Interlocked.Increment(ref done));
            }
        }).ToList();
        return await Task.WhenAll(checks).ConfigureAwait(false);
    }

    /// <summary>
    /// The default handler: no automatic redirects (the probe follows them itself), no credentials for the target (the system
    /// proxy gets the operator's, as an NTLM/Kerberos proxy answers 407 otherwise), no cookies, 5 s to connect, and an HTTPS
    /// chain policy that never downloads (decision 1).
    /// </summary>
    internal static SocketsHttpHandler DefaultHandler() => new()
    {
        AllowAutoRedirect = false,
        Credentials = null,
        PreAuthenticate = false,
        UseCookies = false,
        DefaultProxyCredentials = CredentialCache.DefaultCredentials,
        ConnectTimeout = TimeSpan.FromSeconds(5),
        AutomaticDecompression = DecompressionMethods.None,
        SslOptions = new SslClientAuthenticationOptions
        {
            CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
            CertificateChainPolicy = new X509ChainPolicy
            {
                DisableCertificateDownloads = true,
                RevocationMode = X509RevocationMode.NoCheck,
            },
        },
    };

    /// <summary>An absolute <c>http</c>/<c>https</c> URL with a host (the only kind ever contacted).</summary>
    internal static bool TryHttp(string? text, out Uri uri)
    {
        uri = null!;
        if (string.IsNullOrWhiteSpace(text) || !Uri.TryCreate(text.Trim(), UriKind.Absolute, out var parsed) || !IsHttp(parsed))
        {
            return false;
        }

        uri = parsed;
        return true;
    }

    private static bool IsHttp(Uri uri) =>
        (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps) && !uri.IsUnc && !uri.IsFile && uri.Host.Length > 0
        && string.IsNullOrEmpty(uri.UserInfo);

    private async Task<PkiHttpCheckResult> CheckOneAsync(HttpClient client, PkiUrl url, CancellationToken cancellationToken)
    {
        PkiHttpCheckResult Result(PkiHttpOutcome outcome, int? status = null, CertificateSummary? certificate = null, CrlSummary? crl = null) =>
            new(url.Url, url.Kind, outcome, status, certificate, crl);

        if (!TryHttp(url.Url, out var target))
        {
            return Result(PkiHttpOutcome.NotChecked);
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(Timeout);
        var token = timeout.Token;
        try
        {
            for (var redirects = 0; ; redirects++)
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, target);
                request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("*/*"));
                using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
                var status = (int)response.StatusCode;
                if (IsRedirect(response.StatusCode))
                {
                    if (redirects >= MaxRedirects)
                    {
                        return Result(PkiHttpOutcome.HttpError, status);
                    }

                    // Every target is checked again: a redirect to file:, UNC or ldap: is never followed.
                    if (response.Headers.Location is not { } location || !TryHttp(new Uri(target, location).ToString(), out var next))
                    {
                        return Result(PkiHttpOutcome.NotChecked, status);
                    }

                    target = next;
                    continue;
                }

                if (url.Kind == PkiUrlKind.Ocsp)
                {
                    // Reachability only in P1: any answer of the responder counts (it answers a bare GET with an error).
                    return Result(PkiHttpOutcome.Ok, status);
                }

                if (response.StatusCode == HttpStatusCode.NotFound)
                {
                    return Result(PkiHttpOutcome.NotFound, status);
                }

                if (!response.IsSuccessStatusCode)
                {
                    return Result(PkiHttpOutcome.HttpError, status);
                }

                if (response.Content.Headers.ContentLength is { } declared && declared > MaxBodyBytes)
                {
                    return Result(PkiHttpOutcome.TooLarge, status);
                }

                var body = await ReadBodyAsync(response, token).ConfigureAwait(false);
                if (body is null)
                {
                    return Result(PkiHttpOutcome.TooLarge, status);
                }

                if (url.Kind == PkiUrlKind.AiaCaIssuers)
                {
                    return ParseCertificate(body) is { } certificate
                        ? Result(PkiHttpOutcome.Ok, status, certificate: certificate)
                        : Result(PkiHttpOutcome.InvalidContent, status);
                }

                return ParseCrl(body) is { } crl
                    ? Result(PkiHttpOutcome.Ok, status, crl: crl)
                    : Result(PkiHttpOutcome.InvalidContent, status);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Result(PkiHttpOutcome.Timeout);
        }
        catch (HttpRequestException ex)
        {
            return Result(Classify(ex));
        }
        catch (IOException)
        {
            // The connection broke while the body was read.
            return Result(PkiHttpOutcome.ConnectFailed);
        }
        catch (UriFormatException)
        {
            return Result(PkiHttpOutcome.NotChecked);
        }
    }

    private static bool IsRedirect(HttpStatusCode status) => status is HttpStatusCode.MovedPermanently or HttpStatusCode.Found
        or HttpStatusCode.SeeOther or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect;

    /// <summary>The body up to <see cref="MaxBodyBytes"/>; null when it is larger.</summary>
    private async Task<byte[]?> ReadBodyAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + read > MaxBodyBytes)
            {
                return null;
            }

            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }

    private static PkiHttpOutcome Classify(HttpRequestException exception)
    {
        if (exception.InnerException is AuthenticationException || exception.HttpRequestError == HttpRequestError.SecureConnectionError)
        {
            return PkiHttpOutcome.TlsError;
        }

        if (exception.HttpRequestError == HttpRequestError.NameResolutionError
            || exception.InnerException is SocketException { SocketErrorCode: SocketError.HostNotFound or SocketError.NoData or SocketError.TryAgain })
        {
            return PkiHttpOutcome.NameNotResolved;
        }

        return PkiHttpOutcome.ConnectFailed;
    }

    /// <summary>
    /// A certificate as AD CS publishes it (<c>.crt</c>: DER, or Base64 PEM) or a PKCS #7 bundle (<c>.p7c</c>:
    /// <see cref="CertificateSummary.Of"/> takes one certificate only, so the bundle is unwrapped here – its first CA
    /// certificate, else its first one). Null when the body is none of these.
    /// </summary>
    internal static CertificateSummary? ParseCertificate(byte[] body)
    {
        if (CertificateSummary.Of(body) is { } der)
        {
            return der;
        }

        if (Pem(body, "CERTIFICATE") is { } pem && CertificateSummary.Of(pem) is { } fromPem)
        {
            return fromPem;
        }

        var bundle = Pkcs7Certificates(Pem(body, "PKCS7") ?? body);
        var summaries = bundle.Select(c => CertificateSummary.Of(c)).OfType<CertificateSummary>().ToList();
        return summaries.FirstOrDefault(s => s.IsCa) ?? summaries.FirstOrDefault();
    }

    /// <summary>A CRL in DER or PEM (<c>X509 CRL</c>); null when the body is neither.</summary>
    internal static CrlSummary? ParseCrl(byte[] body) =>
        CrlSummary.Of(body) ?? (Pem(body, "X509 CRL") is { } pem ? CrlSummary.Of(pem) : null);

    /// <summary>The DER of the first PEM block with <paramref name="label"/>, or null.</summary>
    private static byte[]? Pem(byte[] body, string label)
    {
        if (body.Length == 0 || body.Length > 64 * 1024 * 1024)
        {
            return null;
        }

        string text;
        try
        {
            text = Encoding.ASCII.GetString(body);
        }
        catch (ArgumentException)
        {
            return null;
        }

        try
        {
            return PemEncoding.TryFind(text, out var fields) && text[fields.Label].Equals(label, StringComparison.Ordinal)
                ? Convert.FromBase64String(text[fields.Base64Data])
                : null;
        }
        catch (FormatException)
        {
            return null;
        }
    }

    /// <summary>
    /// The certificates of a PKCS #7 <c>SignedData</c> (RFC 5652: <c>ContentInfo { contentType 1.2.840.113549.1.7.2,
    /// [0] SignedData { version, digestAlgorithms, encapContentInfo, certificates [0] IMPLICIT SET OF Certificate … } }</c>);
    /// empty when the body is not one.
    /// </summary>
    internal static IReadOnlyList<byte[]> Pkcs7Certificates(byte[] body)
    {
        const string SignedData = "1.2.840.113549.1.7.2";
        try
        {
            var reader = new AsnReader(body, AsnEncodingRules.BER);
            var contentInfo = reader.ReadSequence();
            if (contentInfo.ReadObjectIdentifier() != SignedData)
            {
                return [];
            }

            var explicitContent = contentInfo.ReadSequence(new Asn1Tag(TagClass.ContextSpecific, 0));
            var signedData = explicitContent.ReadSequence();
            _ = signedData.ReadInteger();
            _ = signedData.ReadSetOf();
            _ = signedData.ReadSequence();
            var certificatesTag = new Asn1Tag(TagClass.ContextSpecific, 0, isConstructed: true);
            if (!signedData.HasData || !signedData.PeekTag().HasSameClassAndValue(certificatesTag))
            {
                return [];
            }

            var set = signedData.ReadSetOf(certificatesTag);
            var certificates = new List<byte[]>();
            while (set.HasData)
            {
                var encoded = set.ReadEncodedValue();
                if (new Asn1Tag(UniversalTagNumber.Sequence, isConstructed: true).HasSameClassAndValue(Asn1Tag.Decode(encoded.Span, out _)))
                {
                    certificates.Add(encoded.ToArray());
                }
            }

            return certificates;
        }
        catch (AsnContentException)
        {
            return [];
        }
        catch (CryptographicException)
        {
            return [];
        }
    }
}
