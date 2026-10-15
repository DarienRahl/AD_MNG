using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography.X509Certificates;
using AdMng.Watchdog.Contracts;
using AdMng.Watchdog.IntegrationTests.Infrastructure;
using AdMng.Watchdog.Testing.Certificates;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace AdMng.Watchdog.IntegrationTests.EndToEnd;

/// <summary>
/// Optional real Kerberos against the Samba KDC (spec 22.4, <c>ADMNG_TEST_KERBEROS=1</c> with the exports of
/// tools/testlab/watchdog/krb5-lab.sh): the service runs with <c>Authentication = Negotiate</c> on
/// loopback https and is called as <c>https://localhost:port</c> (SPN HTTP/localhost from the keytab), the client takes a ticket as <c>odczyt.test</c>
/// (kinit into the private cache). Linux proves authentication only: the identity is "Kerberos", the API answers 403
/// (no SID claims, so no role) – never 401 – and an NTLM-only client gets 401.
/// </summary>
public sealed class KerberosNegotiateTests
{
    private const string SkipReason = "Real Kerberos is optional: set ADMNG_TEST_KERBEROS=1 and the exports of tools/testlab/watchdog/krb5-lab.sh.";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task KerberosClient_IsAuthenticated_ButHasNoRoleOnLinux()
    {
        RequireKerberos();
        await KinitAsync();
        await using var service = new LabServiceFactory(Settings(), services => services.AddSingleton<IStartupFilter, IdentityProbeStartupFilter>());
        var address = Localhost(service.Start());
        using var client = Client(CredentialCache.DefaultCredentials);

        using var probe = await client.GetAsync(new Uri(address, IdentityProbeStartupFilter.Path), Ct);
        Assert.Equal(HttpStatusCode.OK, probe.StatusCode);
        Assert.Equal("Kerberos", await probe.Content.ReadAsStringAsync(Ct));

        using var me = await client.GetAsync(new Uri(address, WatchdogRoutes.Me), Ct);
        Assert.Equal(HttpStatusCode.Forbidden, me.StatusCode);
    }

    [Fact]
    public async Task NtlmOnlyClient_IsUnauthorized()
    {
        RequireKerberos();
        await using var service = new LabServiceFactory(Settings());
        var address = Localhost(service.Start());
        var me = new Uri(address, WatchdogRoutes.Me);
        var credentials = new CredentialCache { { address, "NTLM", new NetworkCredential("odczyt.test", WatchdogLab.ReadOnlyPassword, WatchdogLab.NetBiosName) } };
        using var client = Client(credentials);

        using var response = await client.GetAsync(me, Ct);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);

        // An NTLM negotiate message, as a raw NTLM header and wrapped in Negotiate: never accepted.
        foreach (var scheme in new[] { "NTLM", "Negotiate" })
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, me);
            request.Headers.Authorization = new AuthenticationHeaderValue(scheme, "TlRMTVNTUAABAAAAB4IIogAAAAAAAAAAAAAAAAAAAAAGAbEdAAAADw==");
            using var raw = await client.SendAsync(request, Ct);
            Assert.Equal(HttpStatusCode.Unauthorized, raw.StatusCode);
        }
    }

    private static void RequireKerberos()
    {
        WatchdogLab.RequireLab();
        Assert.SkipUnless(WatchdogLab.KerberosEnabled
            && Environment.GetEnvironmentVariable("KRB5_CONFIG") is { Length: > 0 }
            && Environment.GetEnvironmentVariable("KRB5_KTNAME") is { Length: > 0 }
            && Environment.GetEnvironmentVariable("KRB5CCNAME") is { Length: > 0 }, SkipReason);
    }

    private static Dictionary<string, string?> Settings() => new()
    {
        ["Watchdog:Authentication"] = "Negotiate",
        ["Watchdog:Hosting:Urls"] = "https://127.0.0.1:0",   // Kestrel binds no dynamic port to "localhost"
        ["Watchdog:Hosting:RequireKerberos"] = "true",
        ["Watchdog:Directory:Disabled"] = "true",
    };

    /// <summary>The same port under the name of the SPN (HTTP/localhost).</summary>
    private static Uri Localhost(Uri address) => new UriBuilder(address) { Host = "localhost" }.Uri;

    private static HttpClient Client(ICredentials credentials) => new(new SocketsHttpHandler
    {
        Credentials = credentials,
        AllowAutoRedirect = false,
        SslOptions =
        {
            RemoteCertificateValidationCallback = (_, certificate, _, _) =>
                certificate is X509Certificate2 presented && TestCertificates.Sha256(presented) == TestCertificates.Sha256(TestCertificates.Localhost),
        },
    })
    {
        Timeout = TimeSpan.FromSeconds(60),
    };

    /// <summary>A ticket for the read-only account in the private cache (KRB5CCNAME); the password goes through stdin.</summary>
    private static async Task KinitAsync()
    {
        var realm = WatchdogLab.DomainDns.ToUpperInvariant();
        var user = WatchdogLab.ReadOnlyUser.Split('@')[0];
        var start = new ProcessStartInfo("kinit", $"{user}@{realm}")
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        using var kinit = Process.Start(start) ?? throw new InvalidOperationException("kinit could not be started.");
        await kinit.StandardInput.WriteLineAsync(WatchdogLab.ReadOnlyPassword.AsMemory(), Ct);
        kinit.StandardInput.Close();
        await kinit.WaitForExitAsync(Ct);
        Assert.True(kinit.ExitCode == 0, "kinit failed: " + await kinit.StandardError.ReadToEndAsync(Ct));
    }
}
