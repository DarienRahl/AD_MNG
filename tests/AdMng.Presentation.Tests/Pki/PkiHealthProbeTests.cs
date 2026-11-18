using System.Net;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using AdMng.Core.Pki;
using AdMng.Presentation.Pki;
using AdMng.Testing.Pki;

namespace AdMng.Presentation.Tests.Pki;

/// <summary>
/// The HTTP check of decision 1 (docs/dev/p-certificate-services.md 2.5, 2.10) against a fake handler: other schemes are never
/// contacted, redirects are checked again and capped at 3, 20 MB cap, the timeout covers the body, no credentials, AIA / CDP
/// parsing (DER, PEM, PKCS #7), OCSP reachability, at most 4 at a time.
/// </summary>
public sealed class PkiHealthProbeTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static PkiUrl Aia(string url) => new(url, PkiUrlKind.AiaCaIssuers);

    private static PkiUrl Cdp(string url) => new(url, PkiUrlKind.Cdp);

    [Theory]
    [InlineData(@"file://\\srv-ca02.contoso.test\CertEnroll\Kontoso Wystawiający CA.crl")]
    [InlineData(@"\\srv-ca02.contoso.test\CertEnroll\ca.crl")]
    [InlineData("ldap://dc01.contoso.test/CN=Kontoso,CN=CDP,CN=Public%20Key%20Services?certificateRevocationList")]
    [InlineData("ldap:///CN=Kontoso,CN=CDP?certificateRevocationList")]
    [InlineData("ftp://pki.contoso.test/ca.crl")]
    [InlineData("/CertEnroll/ca.crl")]
    [InlineData("http://jan:hasło@pki.contoso.test/ca.crl")]
    [InlineData("")]
    public async Task OtherSchemes_AreNotChecked_WithoutARequest(string url)
    {
        var handler = new FakeHandler(_ => throw new InvalidOperationException("No request may be sent."));
        var probe = new PkiHealthProbe(() => handler);

        var result = Assert.Single(await probe.CheckAsync([Cdp(url)], null, Ct));

        Assert.Equal(PkiHttpOutcome.NotChecked, result.Outcome);
        Assert.Null(result.StatusCode);
        Assert.Equal(0, handler.Requests);
    }

    [Fact]
    public async Task AnAiaCertificate_InDerPemAndPkcs7_IsParsed()
    {
        var der = PkiSamples.IssuingCaDer;
        var pem = Encoding.ASCII.GetBytes(PkiSamples.IssuingCa.ExportCertificatePem());
        var bundle = new X509Certificate2Collection { PkiSamples.RootCa, PkiSamples.IssuingCa }.Export(X509ContentType.Pkcs7)!;
        var handler = new FakeHandler(request => request.RequestUri!.AbsolutePath switch
        {
            "/der.crt" => Ok(der),
            "/pem.crt" => Ok(pem),
            "/bundle.p7c" => Ok(bundle),
            _ => Ok("<html>nie certyfikat</html>"u8.ToArray()),
        });
        var probe = new PkiHealthProbe(() => handler);

        var results = await probe.CheckAsync(
            [Aia("http://pki.contoso.test/der.crt"), Aia("http://pki.contoso.test/pem.crt"), Aia("http://pki.contoso.test/bundle.p7c"), Aia("http://pki.contoso.test/x")],
            null, Ct);

        Assert.All(results.Take(3), r => Assert.Equal(PkiHttpOutcome.Ok, r.Outcome));
        Assert.Equal(PkiSamples.IssuingCa.Thumbprint, results[0].Certificate?.Thumbprint, ignoreCase: true);
        Assert.Equal(PkiSamples.IssuingCa.Thumbprint, results[1].Certificate?.Thumbprint, ignoreCase: true);
        Assert.True(results[2].Certificate?.IsCa);
        Assert.Equal(PkiHttpOutcome.InvalidContent, results[3].Outcome);
        Assert.Equal(200, results[3].StatusCode);
    }

    [Fact]
    public async Task ACdpCrl_IsParsed_InDerAndPem()
    {
        var crl = PkiSamples.IssuingCrlDer();
        var pem = Encoding.ASCII.GetBytes(PemEncodingText("X509 CRL", crl));
        var handler = new FakeHandler(request => Ok(request.RequestUri!.AbsolutePath == "/pem.crl" ? pem : crl));
        var probe = new PkiHealthProbe(() => handler);

        var results = await probe.CheckAsync([Cdp("http://pki.contoso.test/ca.crl"), Cdp("http://pki.contoso.test/pem.crl")], null, Ct);

        Assert.All(results, r => Assert.Equal(42, (int?)r.Crl?.CrlNumber));
        Assert.Null(results[0].Certificate);
    }

    [Fact]
    public async Task Redirects_AreFollowedAndCheckedAgain_AtMostThree()
    {
        var crl = PkiSamples.IssuingCrlDer();
        var handler = new FakeHandler(request => request.RequestUri!.AbsolutePath switch
        {
            "/one" => Redirect("http://pki2.contoso.test/two"),
            "/two" => Redirect("/three"),
            "/three" => Ok(crl),
            "/to-file" => Redirect(@"file://\\attacker.example\share\ca.crl"),
            "/to-ldap" => Redirect("ldap://dc02.contoso.test/CN=x"),
            _ => Redirect("http://pki.contoso.test/loop" + request.RequestUri!.AbsolutePath.Length),
        });
        var probe = new PkiHealthProbe(() => handler);

        var results = await probe.CheckAsync(
            [Cdp("http://pki.contoso.test/one"), Cdp("http://pki.contoso.test/to-file"), Cdp("http://pki.contoso.test/to-ldap"), Cdp("http://pki.contoso.test/loop")],
            null, Ct);

        Assert.Equal(PkiHttpOutcome.Ok, results[0].Outcome);
        Assert.Equal("http://pki.contoso.test/one", results[0].Url);
        Assert.Equal(PkiHttpOutcome.NotChecked, results[1].Outcome);
        Assert.Equal(PkiHttpOutcome.NotChecked, results[2].Outcome);
        Assert.Equal(PkiHttpOutcome.HttpError, results[3].Outcome);
        Assert.Equal(302, results[3].StatusCode);
        Assert.DoesNotContain(handler.Uris, u => u.Scheme is not ("http" or "https"));
        Assert.Equal(1 + PkiHealthProbe.MaxRedirects, handler.Uris.Count(u => u.AbsolutePath.StartsWith("/loop", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task ABodyOverTheCap_IsTooLarge_DeclaredOrStreamed()
    {
        var handler = new FakeHandler(request => request.RequestUri!.AbsolutePath == "/declared"
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new DeclaredLengthContent(64L * 1024 * 1024) }
            : Ok(new byte[3000]));
        var probe = new PkiHealthProbe(() => handler) { MaxBodyBytes = 2048 };

        var results = await probe.CheckAsync([Cdp("http://pki.contoso.test/declared"), Cdp("http://pki.contoso.test/streamed")], null, Ct);

        Assert.All(results, r => Assert.Equal(PkiHttpOutcome.TooLarge, r.Outcome));
        Assert.Equal(20L * 1024 * 1024, new PkiHealthProbe().MaxBodyBytes);
    }

    [Fact]
    public async Task TheTimeout_CoversTheBody()
    {
        var handler = new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new HangingStream()) });
        var probe = new PkiHealthProbe(() => handler) { Timeout = TimeSpan.FromMilliseconds(300) };

        var result = Assert.Single(await probe.CheckAsync([Cdp("http://pki.contoso.test/slow.crl")], null, Ct));

        Assert.Equal(PkiHttpOutcome.Timeout, result.Outcome);
        Assert.Equal(TimeSpan.FromSeconds(15), new PkiHealthProbe().Timeout);
    }

    [Fact]
    public async Task StatusCodes_AndOcsp_AreReported()
    {
        var handler = new FakeHandler(request => request.RequestUri!.AbsolutePath switch
        {
            "/missing.crl" => new HttpResponseMessage(HttpStatusCode.NotFound),
            "/broken.crl" => new HttpResponseMessage(HttpStatusCode.InternalServerError),
            _ => new HttpResponseMessage(HttpStatusCode.BadRequest),
        });
        var probe = new PkiHealthProbe(() => handler);

        var results = await probe.CheckAsync(
            [Cdp("http://pki.contoso.test/missing.crl"), Cdp("http://pki.contoso.test/broken.crl"), new PkiUrl("http://ocsp.contoso.test/ocsp", PkiUrlKind.Ocsp)],
            null, Ct);

        Assert.Equal((PkiHttpOutcome.NotFound, 404), (results[0].Outcome, results[0].StatusCode!.Value));
        Assert.Equal((PkiHttpOutcome.HttpError, 500), (results[1].Outcome, results[1].StatusCode!.Value));
        Assert.Equal((PkiHttpOutcome.Ok, 400), (results[2].Outcome, results[2].StatusCode!.Value));
        Assert.Null(results[2].Crl);
    }

    [Fact]
    public async Task NoCredentialsAreSent_AndTheDefaultHandlerNeitherRedirectsNorDownloads()
    {
        var handler = new FakeHandler(_ => Ok(PkiSamples.IssuingCrlDer()));
        var probe = new PkiHealthProbe(() => handler);

        await probe.CheckAsync([Cdp("https://pki.contoso.test/ca.crl")], null, Ct);

        var request = Assert.Single(handler.Headers);
        Assert.Null(request.Authorization);
        using var defaults = PkiHealthProbe.DefaultHandler();
        Assert.False(defaults.AllowAutoRedirect);
        Assert.Null(defaults.Credentials);
        Assert.False(defaults.PreAuthenticate);
        Assert.False(defaults.UseCookies);
        Assert.Equal(TimeSpan.FromSeconds(5), defaults.ConnectTimeout);
        Assert.True(defaults.SslOptions.CertificateChainPolicy!.DisableCertificateDownloads);
        Assert.Equal(X509RevocationMode.NoCheck, defaults.SslOptions.CertificateChainPolicy.RevocationMode);
    }

    [Fact]
    public async Task AtMostFourUrls_AreCheckedAtATime_AndProgressCountsThem()
    {
        var handler = new FakeHandler(_ => Ok(PkiSamples.IssuingCrlDer())) { Delay = TimeSpan.FromMilliseconds(50) };
        var probe = new PkiHealthProbe(() => handler);
        var done = new List<int>();

        var results = await probe.CheckAsync([.. Enumerable.Range(0, 12).Select(i => Cdp($"http://pki.contoso.test/{i}.crl"))], new SyncProgress(done), Ct);

        Assert.Equal(12, results.Count);
        Assert.Equal(Enumerable.Range(0, 12).Select(i => $"http://pki.contoso.test/{i}.crl"), results.Select(r => r.Url));
        Assert.InRange(handler.MaxConcurrency, 1, PkiHealthProbe.MaxParallel);

        // In the order they arrive: the report of one check never overtakes the report of another.
        Assert.Equal(Enumerable.Range(1, 12), done);
    }

    [Fact]
    public async Task ConnectionFailures_AreClassified_AndTheCallersCancellationPropagates()
    {
        var handler = new FakeHandler(request => request.RequestUri!.Host switch
        {
            "nieznany.contoso.test" => throw new HttpRequestException(HttpRequestError.NameResolutionError, "no such host"),
            "tls.contoso.test" => throw new HttpRequestException(HttpRequestError.SecureConnectionError, "handshake"),
            _ => throw new HttpRequestException(HttpRequestError.ConnectionError, "refused"),
        });
        var probe = new PkiHealthProbe(() => handler);

        var results = await probe.CheckAsync(
            [Cdp("http://nieznany.contoso.test/a.crl"), Cdp("https://tls.contoso.test/a.crl"), Cdp("http://zamkniety.contoso.test/a.crl")], null, Ct);

        Assert.Equal([PkiHttpOutcome.NameNotResolved, PkiHttpOutcome.TlsError, PkiHttpOutcome.ConnectFailed], results.Select(r => r.Outcome));
        using var cancel = new CancellationTokenSource();
        await cancel.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => probe.CheckAsync([Cdp("http://pki.contoso.test/a.crl")], null, cancel.Token));
        Assert.Empty(await probe.CheckAsync([], null, Ct));
    }

    [Fact]
    public async Task TheResults_PrintNoUrl()
    {
        var handler = new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound));
        var probe = new PkiHealthProbe(() => handler);

        var result = Assert.Single(await probe.CheckAsync([Cdp("http://pki.contoso.test/Kontoso Wystawiający CA.crl")], null, Ct));

        Assert.Equal("PkiHttpCheckResult(Cdp: NotFound 404)", result.ToString());
    }

    private static HttpResponseMessage Ok(byte[] body) => new(HttpStatusCode.OK) { Content = new ByteArrayContent(body) };

    private static HttpResponseMessage Redirect(string location)
    {
        var response = new HttpResponseMessage(HttpStatusCode.Found);
        response.Headers.Location = new Uri(location, UriKind.RelativeOrAbsolute);
        return response;
    }

    private static string PemEncodingText(string label, byte[] der) => new(System.Security.Cryptography.PemEncoding.Write(label, der));

    /// <summary>An in-process handler: answers by the request, counts requests and their concurrency.</summary>
    private sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> answer) : HttpMessageHandler
    {
        private readonly List<Uri> _uris = [];
        private readonly List<System.Net.Http.Headers.HttpRequestHeaders> _headers = [];
        private int _inFlight;
        private int _max;

        public TimeSpan Delay { get; init; }

        public int Requests => _uris.Count;

        public IReadOnlyList<Uri> Uris
        {
            get
            {
                lock (_uris)
                {
                    return [.. _uris];
                }
            }
        }

        public IReadOnlyList<System.Net.Http.Headers.HttpRequestHeaders> Headers => _headers;

        public int MaxConcurrency => Volatile.Read(ref _max);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            lock (_uris)
            {
                _uris.Add(request.RequestUri!);
                _headers.Add(request.Headers);
            }

            var now = Interlocked.Increment(ref _inFlight);
            int seen;
            while ((seen = Volatile.Read(ref _max)) < now && Interlocked.CompareExchange(ref _max, now, seen) != seen)
            {
            }

            try
            {
                if (Delay > TimeSpan.Zero)
                {
                    await Task.Delay(Delay, cancellationToken);
                }

                var response = answer(request);
                response.RequestMessage = request;
                return response;
            }
            finally
            {
                Interlocked.Decrement(ref _inFlight);
            }
        }
    }

    /// <summary>Declares a length it never sends (the probe must refuse before reading).</summary>
    private sealed class DeclaredLengthContent(long length) : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => throw new InvalidOperationException("The body must not be read.");

        protected override bool TryComputeLength(out long result)
        {
            result = length;
            return true;
        }
    }

    /// <summary>A body that never ends (until the read is cancelled).</summary>
    private sealed class HangingStream : Stream
    {
        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position { get => 0; set => throw new NotSupportedException(); }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return 0;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class SyncProgress(List<int> sink) : IProgress<int>
    {
        public void Report(int value)
        {
            lock (sink)
            {
                sink.Add(value);
            }
        }
    }
}
