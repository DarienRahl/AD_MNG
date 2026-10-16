using System.Net;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using AdMng.Watchdog.Contracts.Configuration;
using AdMng.Watchdog.Core.Model;
using AdMng.Watchdog.Service.Hosting;
using AdMng.Watchdog.Service.Notifications;
using Microsoft.Identity.Client;

namespace AdMng.Watchdog.Service.Tests.Notifications;

/// <summary>The MSAL confidential client: fixed authority per cloud, cached tokens, failures classified (spec 15.6–15.7).</summary>
public sealed class MsalEntraTokenClientTests
{
    private const string TenantId = "6f1d3c2a-8b4e-4f5a-9c7d-2e1f0a3b4c5d";
    private const string ClientId = "0a1b2c3d-4e5f-4a6b-8c7d-9e0f1a2b3c4d";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(EntraCloud.Public, "login.microsoftonline.com", "https://outlook.office365.com/.default")]
    [InlineData(EntraCloud.UsGovernment, "login.microsoftonline.us", "https://outlook.office365.us/.default")]
    [InlineData(EntraCloud.China, "login.partner.microsoftonline.cn", "https://partner.outlook.cn/.default")]
    public async Task ClientSecret_GoesToTheCloudsFixedAuthority(EntraCloud cloud, string authorityHost, string scope)
    {
        var handler = new TokenEndpoint();
        var client = new MsalEntraTokenClient(new NoCertificates(), new MsalHttpClients(handler));

        var token = await client.AcquireTokenAsync(Request(cloud, EntraCloudInfo.SmtpScope(cloud)), Ct);

        Assert.Equal(TokenEndpoint.AccessToken, token);
        var tokenRequest = Assert.Single(handler.Requests, r => r.Uri.AbsolutePath.EndsWith("/oauth2/v2.0/token", StringComparison.Ordinal));
        Assert.Equal(authorityHost, tokenRequest.Uri.Host);
        Assert.StartsWith($"/{TenantId}/", tokenRequest.Uri.AbsolutePath, StringComparison.Ordinal);
        Assert.Contains("grant_type=client_credentials", tokenRequest.Body, StringComparison.Ordinal);
        Assert.Contains("client_secret=" + Uri.EscapeDataString("sekret-ąę"), tokenRequest.Body, StringComparison.Ordinal);
        Assert.Contains("scope=" + Uri.EscapeDataString(scope), tokenRequest.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Tokens_AreCached_PerApplication()
    {
        var handler = new TokenEndpoint();
        var client = new MsalEntraTokenClient(new NoCertificates(), new MsalHttpClients(handler));

        await client.AcquireTokenAsync(Request(EntraCloud.Public, EntraCloudInfo.SmtpScope(EntraCloud.Public)), Ct);
        await client.AcquireTokenAsync(Request(EntraCloud.Public, EntraCloudInfo.SmtpScope(EntraCloud.Public)), Ct);

        Assert.Single(handler.Requests, r => r.Uri.AbsolutePath.EndsWith("/token", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RejectedClient_IsAnAuthenticationFailure_AndAnOutageIsTransient()
    {
        var rejected = new TokenEndpoint { TokenStatus = HttpStatusCode.Unauthorized };
        var outage = new TokenEndpoint { TokenStatus = HttpStatusCode.ServiceUnavailable };

        var auth = await Failure(rejected);
        var transient = await Failure(outage);

        Assert.False(auth.IsTransient);
        Assert.Equal("auth.token.401", auth.FailureCode);
        Assert.True(transient.IsTransient);
        Assert.StartsWith("auth.token", transient.FailureCode, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MissingCertificate_Fails_WithoutARequest()
    {
        var handler = new TokenEndpoint();
        var client = new MsalEntraTokenClient(new NoCertificates(), new MsalHttpClients(handler));

        var error = await Record.ExceptionAsync(() => client.AcquireTokenAsync(
            Request(EntraCloud.Public, EntraCloudInfo.SmtpScope(EntraCloud.Public)) with { CertificateThumbprint = "AB CD EF", ClientSecret = null },
            Ct));

        Assert.NotNull(error);
        Assert.Equal("auth.token", AccessTokenResult.Failed(error).FailureCode);
        Assert.Empty(handler.Requests);
    }

    private static async Task<AccessTokenResult> Failure(TokenEndpoint handler)
    {
        var client = new MsalEntraTokenClient(new NoCertificates(), new MsalHttpClients(handler));
        var error = await Record.ExceptionAsync(() => client.AcquireTokenAsync(Request(EntraCloud.Public, EntraCloudInfo.SmtpScope(EntraCloud.Public)), Ct));
        Assert.IsAssignableFrom<MsalException>(error);
        return AccessTokenResult.Failed(error!);
    }

    private static EntraTokenRequest Request(EntraCloud cloud, string scope) => new()
    {
        Cloud = cloud,
        TenantId = TenantId,
        ClientId = ClientId,
        Scope = scope,
        ClientSecret = new SecretValue("sekret-ąę"),
    };

    private sealed class NoCertificates : ICertificateStore
    {
        public IReadOnlyList<X509Certificate2> GetCertificates(StoreName storeName, StoreLocation storeLocation) => [];
    }

    private sealed class MsalHttpClients(HttpMessageHandler handler) : IMsalHttpClientFactory
    {
        public HttpClient GetHttpClient() => new(handler, disposeHandler: false);
    }

    /// <summary>Answers instance discovery, OpenID metadata and the token endpoint of any authority host.</summary>
    private sealed class TokenEndpoint : HttpMessageHandler
    {
        public const string AccessToken = "eyJ0eXAiOiJKV1QiLCJhbGciOiJSUzI1NiJ9.eyJhdWQiOiJodHRwczovL291dGxvb2sub2ZmaWNlMzY1LmNvbSJ9.c2ln";

        private readonly Lock _gate = new();
        private readonly List<(Uri Uri, string Body)> _requests = [];

        public HttpStatusCode TokenStatus { get; init; } = HttpStatusCode.OK;

        public IReadOnlyList<(Uri Uri, string Body)> Requests
        {
            get
            {
                lock (_gate)
                {
                    return [.. _requests];
                }
            }
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
            lock (_gate)
            {
                _requests.Add((request.RequestUri!, body));
            }

            var host = request.RequestUri!.Host;
            var path = request.RequestUri.AbsolutePath;
            if (path.Contains("/discovery/instance", StringComparison.Ordinal))
            {
                return Json(HttpStatusCode.OK, $$"""
                    {"tenant_discovery_endpoint":"https://{{host}}/{{TenantId}}/v2.0/.well-known/openid-configuration","api-version":"1.1",
                     "metadata":[{"preferred_network":"{{host}}","preferred_cache":"{{host}}","aliases":["{{host}}"]}]}
                    """);
            }

            if (path.EndsWith("/.well-known/openid-configuration", StringComparison.Ordinal))
            {
                return Json(HttpStatusCode.OK, $$"""
                    {"token_endpoint":"https://{{host}}/{{TenantId}}/oauth2/v2.0/token",
                     "authorization_endpoint":"https://{{host}}/{{TenantId}}/oauth2/v2.0/authorize",
                     "issuer":"https://{{host}}/{{TenantId}}/v2.0"}
                    """);
            }

            return TokenStatus == HttpStatusCode.OK
                ? Json(HttpStatusCode.OK, $$"""{"token_type":"Bearer","expires_in":3599,"ext_expires_in":3599,"access_token":"{{AccessToken}}"}""")
                : Json(TokenStatus, """{"error":"invalid_client","error_description":"AADSTS7000215: Invalid client secret provided.","error_codes":[7000215]}""");
        }

        private static HttpResponseMessage Json(HttpStatusCode status, string json) => new(status)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
    }
}
