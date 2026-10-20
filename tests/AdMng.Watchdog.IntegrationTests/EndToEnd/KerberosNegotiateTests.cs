using System.Net;
using System.Net.Http.Headers;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using AdMng.Core.ActiveDirectory;
using AdMng.Watchdog.Contracts;
using AdMng.Watchdog.Contracts.Api;
using AdMng.Watchdog.Core.Abstractions;
using AdMng.Watchdog.IntegrationTests.Infrastructure;
using AdMng.Watchdog.Keytab;
using AdMng.Watchdog.Keytab.Native;
using AdMng.Watchdog.Testing.Certificates;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AdMng.Watchdog.IntegrationTests.EndToEnd;

/// <summary>
/// Milestone B3 with real Kerberos on the Samba lab (Linux design 6.1–6.2, 21): the service runs with
/// <c>Authentication = Negotiate</c> on Kestrel, its acceptor keys come from <c>KRB5_KTNAME</c> – the keytab that
/// <c>admng-watchdog-keytab init</c> wrote for a fresh computer account of the run's OU – and it reads the directory over
/// SASL GSSAPI (sign and seal) as that account (<c>KRB5_CLIENT_KTNAME</c>). Callers authenticate with their passwords
/// (a ticket for <c>HTTP/&lt;account fqdn&gt;</c> from the KDC); their roles come only from the service's own lookup of
/// objectSid and tokenGroups: a Viewer (through a nested group) reads, an Admin reaches admin endpoints, a non-member
/// gets 403, a member of a <c>DeniedSids</c> group is refused although it is also an Admin. A ticket encrypted with a key
/// the keytab does not hold gives 401 (never 500), an NTLM-only client gets 401, and a loopback caller outside
/// <c>AllowedRemoteSubnets</c> gets 403 before authentication.
/// </summary>
/// <remarks>
/// Environment: the lab (<c>ADMNG_TEST_LDAP_*</c>) and <c>eval "$(tools/testlab/watchdog/keytab-lab.sh env)"</c> in the
/// test process. The account name is not in DNS, so the clients connect to 127.0.0.1 and send the account's FQDN as the
/// Host header – .NET then builds the SPN from the header without a DNS lookup.
/// </remarks>
[SupportedOSPlatform("linux")]
[Collection(KeytabLabCollection.Name)]
public sealed class KerberosNegotiateTests(LabFixture lab)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task RolesComeFromTokenGroups_AndGssFailuresAre401()
    {
        KeytabLab.Require();
        using var state = new KeytabTempDirectory();
        var account = await KeytabLab.CreateAndInitAsync(lab, state, Ct);

        var viewer = await lab.Writer.CreateUserAsync(account.Ou, "Łucja", "Przeglądająca", TestNames.Password(), cancellationToken: Ct);
        var admin = await lab.Writer.CreateUserAsync(account.Ou, "Jan", "Administrujący", TestNames.Password(), cancellationToken: Ct);
        var outsider = await lab.Writer.CreateUserAsync(account.Ou, "Paweł", "Postronny", TestNames.Password(), cancellationToken: Ct);
        var denied = await lab.Writer.CreateUserAsync(account.Ou, "Zofia", "Zablokowana", TestNames.Password(), cancellationToken: Ct);
        var team = await lab.Writer.CreateGroupAsync(account.Ou, "WD Zespół wsparcia", Ct, viewer.Dn);
        var viewers = await lab.Writer.CreateGroupAsync(account.Ou, "WD Podgląd", Ct, team);
        var admins = await lab.Writer.CreateGroupAsync(account.Ou, "WD Administratorzy", Ct, admin.Dn, denied.Dn);
        var blocked = await lab.Writer.CreateGroupAsync(account.Ou, "WD Zablokowani", Ct, denied.Dn);

        await using var service = new LabServiceFactory(new Dictionary<string, string?>(Settings("127.0.0.1/32"))
        {
            ["Watchdog:Authorization:ViewerSids:0"] = await SidAsync(viewers),
            ["Watchdog:Authorization:AdminSids:0"] = await SidAsync(admins),
            ["Watchdog:Authorization:DeniedSids:0"] = await SidAsync(blocked),
            ["Watchdog:Directory:Disabled"] = "false",
            ["Watchdog:Directory:Domain"] = WatchdogLab.DomainDns,
            ["Watchdog:Directory:Server"] = KeytabLab.Server,
            ["Watchdog:Directory:Servers:0"] = KeytabLab.Server,
            ["Watchdog:Directory:PreferPdc"] = "false",
            ["Watchdog:Directory:Authentication"] = "CurrentUser",
            ["Watchdog:Directory:Security"] = "SignAndSeal",
            ["Watchdog:Directory:AllowInsecureLab"] = "false",
        }, NoJobs);
        var address = service.Start();

        using (var client = Client(address, account.Fqdn, viewer))
        {
            var me = await MeAsync(client);
            Assert.Equal([WatchdogRole.Viewer], me.Roles);
            Assert.Equal("Kerberos", me.AuthenticationType);
            Assert.Equal(await SidAsync(viewer.Dn), me.Sid);
            Assert.Equal(viewer.SamAccountName + "@" + KeytabLab.Realm, me.Account, StringComparer.OrdinalIgnoreCase);
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(WatchdogRoutes.Status, Ct)).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync(WatchdogRoutes.ConfigAudit, Ct)).StatusCode);
        }

        using (var client = Client(address, account.Fqdn, admin))
        {
            Assert.Equal([WatchdogRole.Viewer, WatchdogRole.Admin], (await MeAsync(client)).Roles);
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(WatchdogRoutes.ConfigAudit, Ct)).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(WatchdogRoutes.Outbox, Ct)).StatusCode);
        }

        using (var client = Client(address, account.Fqdn, outsider))
        {
            Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync(WatchdogRoutes.Me, Ct)).StatusCode);
        }

        using (var client = Client(address, account.Fqdn, denied))
        {
            Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync(WatchdogRoutes.Me, Ct)).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync(WatchdogRoutes.ConfigAudit, Ct)).StatusCode);
        }

        // The KDC encrypts the ticket with the account's real key; the acceptor holds other keys for the same principals and
        // kvnos (a keytab that does not match AD): a GSS failure, answered with 401 and hint 2501 – never HTTP 500.
        var original = File.ReadAllBytes(KeytabLab.KeytabPath);
        try
        {
            ReplaceWithWrongKeys(KeytabLab.KeytabPath);
            using var client = Client(address, account.Fqdn, admin);
            using var response = await client.GetAsync(WatchdogRoutes.Me, Ct);
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
            Assert.Contains(service.Log.Lines, l => l.Contains("[2501]", StringComparison.Ordinal));
            Assert.DoesNotContain(service.Log.Lines, l => l.Contains("ExceptionHandler", StringComparison.Ordinal));   // no unhandled exception (500)
        }
        finally
        {
            File.WriteAllBytes(KeytabLab.KeytabPath, original);
        }

        using (var client = Client(address, account.Fqdn, admin))
        {
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(WatchdogRoutes.Me, Ct)).StatusCode);
        }
    }

    [Fact]
    public async Task NtlmOnlyClient_IsUnauthorized()
    {
        KeytabLab.Require();
        await using var service = new LabServiceFactory(Settings("127.0.0.1/32"), NoJobs);
        var address = service.Start();
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

    /// <summary>Design 4.3 item 1 / 6.1: a forged-name ticket used from the server itself never reaches the acceptor.</summary>
    [Fact]
    public async Task LoopbackCaller_OutsideAllowedRemoteSubnets_Is403BeforeAuthentication()
    {
        KeytabLab.Require();
        await using var service = new LabServiceFactory(Settings("10.0.20.0/24"), NoJobs);
        var address = service.Start();
        using var client = Client(new CredentialCache());

        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(address, WatchdogRoutes.Me));
        request.Headers.Authorization = new AuthenticationHeaderValue("Negotiate", "YIIBhwYGKwYBBQUCoIIBezCCAXegDTALBgkqhkiG9xIBAgI=");
        using var forged = await client.SendAsync(request, Ct);
        using var health = await client.GetAsync(new Uri(address, WatchdogRoutes.Health), Ct);

        Assert.Equal(HttpStatusCode.Forbidden, forged.StatusCode);
        Assert.False(forged.Headers.WwwAuthenticate.Count > 0);
        Assert.NotEqual(HttpStatusCode.Forbidden, health.StatusCode);
    }

    private static Dictionary<string, string?> Settings(string allowedSubnet) => new()
    {
        ["Watchdog:Authentication"] = "Negotiate",
        ["Watchdog:Hosting:Urls"] = "https://127.0.0.1:0",   // Kestrel binds no dynamic port to a host name
        ["Watchdog:Hosting:RequireKerberos"] = "true",
        ["Watchdog:Hosting:AllowedRemoteSubnets:0"] = allowedSubnet,
        ["Watchdog:Kerberos:Realm"] = KeytabLab.Realm,
        ["Watchdog:Directory:Disabled"] = "true",
    };

    /// <summary>Only the API: no detection baseline of the lab domain in the background.</summary>
    private static void NoJobs(IServiceCollection services) => services.RemoveAll<IWatchdogJob>();

    private async Task<string> SidAsync(string dn) => (await lab.Admin.GetAsync(dn, [Attr.ObjectSid], Ct))!.ObjectSid!;

    private static async Task<MeDto> MeAsync(HttpClient client)
    {
        using var response = await client.GetAsync(WatchdogRoutes.Me, Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return JsonSerializer.Deserialize<MeDto>(await response.Content.ReadAsStringAsync(Ct), WatchdogJson.Options)!;
    }

    /// <summary>A Negotiate client with the user's password (its own MEMORY credentials, not the process ccache) that
    /// connects to <paramref name="address"/> and names <paramref name="host"/> in the Host header (SPN HTTP/host).</summary>
    private static HttpClient Client(Uri address, string host, LabUser user)
    {
        var client = Client(new CredentialCache { { address, "Negotiate", new NetworkCredential(user.SamAccountName, user.Password, KeytabLab.Realm) } });
        client.BaseAddress = address;
        client.DefaultRequestHeaders.Host = host;
        return client;
    }

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

    /// <summary>The keytab rewritten with the same principals and kvnos but keys of a random password.</summary>
    private static void ReplaceWithWrongKeys(string path)
    {
        var temp = path + ".wrong";
        File.WriteAllBytes(temp, [0x05, 0x02]);
        using (var context = Krb5Context.Open(KeytabLab.Krb5Config))
        using (var password = new SecretBuffer(64))
        {
            foreach (var b in RandomNumberGenerator.GetBytes(32))
            {
                password.Append((byte)('a' + (b % 26)));
            }

            using var keys = context.DeriveKeys(password, KeytabLab.Realm + "hostwrong." + WatchdogLab.DomainDns);
            var keytab = context.ResolveKeytab(temp);
            try
            {
                foreach (var (principal, kvno) in context.ListKeytab(path).Select(e => (e.Principal, e.Kvno)).Distinct())
                {
                    using var name = context.ParseName(principal);
                    for (var i = 0; i < keys.Count; i++)
                    {
                        context.AddEntry(keytab, name, kvno, ref keys[i], DateTimeOffset.UtcNow);
                    }
                }
            }
            finally
            {
                context.CloseKeytab(keytab);
            }
        }

        File.Move(temp, path, overwrite: true);
    }
}
