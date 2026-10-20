using System.Diagnostics;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Runtime.Versioning;
using System.Text;
using AdMng.Watchdog.Core.Attribution;
using AdMng.Watchdog.IntegrationTests.Infrastructure;
using AdMng.Watchdog.Testing.Fixtures.OpenWec;

namespace AdMng.Watchdog.IntegrationTests.Collector;

/// <summary>
/// Milestone B4 on the Samba lab (Linux design 7.2, 21): <c>openwecd</c> of the pinned OpenWEC commit, configured from the
/// package's <c>openwec.conf.toml</c> (keytab, SPN, no access log), accepts a Kerberos client of the realm with the keytab
/// that <c>admng-watchdog-keytab init</c> wrote for a fresh computer account – in WinRM's <c>Kerberos</c> scheme and in
/// SPNEGO (<c>Negotiate</c>, upstream #307), for tickets of the account's <c>HTTP/</c> and <c>host/</c> principals –
/// answering with its mutual-authentication token that completes the client's context – and refuses a request without a
/// ticket (401 with both challenges) and a ticket for another account's service. The subscription file comes from
/// <see cref="WecSubscription"/> and loads with the real CLI; no access-log file is written.
/// </summary>
/// <remarks>
/// Environment: the lab (<c>ADMNG_TEST_LDAP_*</c>), <c>eval "$(tools/testlab/watchdog/keytab-lab.sh env)"</c> and
/// <c>ADMNG_TEST_OPENWECD</c> = the path of <c>openwecd</c> (with <c>openwec</c> next to it) built from commit 86de07f
/// (<c>tools/packaging/build-openwec-deb.sh</c> or <c>cargo build --release --locked</c>). The account name is not in DNS:
/// the client asks the KDC for <c>HTTP/&lt;account fqdn&gt;</c> and connects to 127.0.0.1.
/// </remarks>
[SupportedOSPlatform("linux")]
[Collection(KeytabLabCollection.Name)]
public sealed class OpenWecLabTests(LabFixture lab)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Openwecd_AcceptsKerberosWithTheWatchdogKeytab()
    {
        KeytabLab.Require();
        var openwecd = Environment.GetEnvironmentVariable("ADMNG_TEST_OPENWECD");
        Assert.SkipWhen(string.IsNullOrEmpty(openwecd) || !File.Exists(openwecd), "ADMNG_TEST_OPENWECD (openwecd of OpenWEC 86de07f) is not set.");
        var openwec = Path.Combine(Path.GetDirectoryName(openwecd)!, "openwec");
        using var state = new KeytabTempDirectory();
        var account = await KeytabLab.CreateAndInitAsync(lab, state, Ct);
        var password = TestNames.Password();
        var user = await lab.Writer.CreateUserAsync(account.Ou, "Wanda", "Przekazująca", password, cancellationToken: Ct);

        var work = System.IO.Directory.CreateTempSubdirectory("admng-openwec-it-").FullName;
        try
        {
            var port = FreePort();
            var config = Path.Combine(work, "openwec.conf.toml");
            File.WriteAllText(config, Configuration(work, account.Fqdn, port));
            Run(openwec, ["-c", config, "db", "init"]);
            var subscription = Path.Combine(work, "admng-dc-security.toml");
            File.WriteAllText(subscription, WecSubscription.Toml(WecRawJson.SubscriptionUuid, [ "VM$@" + KeytabLab.Realm ], Path.Combine(work, "out") + "/{client}/security.jsonl"));
            Run(openwec, ["-c", config, "subscriptions", "load", "-y", subscription]);
            Assert.Contains("admng-dc-security", Run(openwec, ["-c", config, "subscriptions"]), StringComparison.Ordinal);

            using var server = Start(openwecd!, config);
            await WaitForPortAsync(port, server);
            var address = new Uri($"http://127.0.0.1:{port}");

            // No ticket: 401 with both challenges. (OpenWEC authenticates a TCP connection once – every attempt below uses
            // its own connection.)
            using (var http = Http(address))
            using (var anonymous = await http.PostAsync("/wsman/SubscriptionManager/WEC", Body(), Ct))
            {
                Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
                Assert.Equal(["Kerberos", "Negotiate"], anonymous.Headers.WwwAuthenticate.Select(h => h.Scheme).Order(StringComparer.Ordinal));
            }

            // A ticket for HTTP/<fqdn> of a realm user, as WinRM sends it ("Kerberos") and as SPNEGO ("Negotiate").
            // The server answers with its own token (mutual authentication with the keytab's key), which completes the
            // client's context – for the HTTP/ principal and for the host/ principal of the account (both are in the
            // keytab; MIT's acceptor takes any key of it).
            foreach (var (scheme, package, spn) in new[]
            {
                ("Kerberos", "Kerberos", account.HttpSpn), ("Negotiate", "Negotiate", account.HttpSpn), ("Kerberos", "Kerberos", "host/" + account.Fqdn),
            })
            {
                using var http = Http(address);
                using var client = Client(package, user.SamAccountName, password, spn);
                using var accepted = await PostWithTicketAsync(http, scheme, client);
                Assert.True(accepted.StatusCode != HttpStatusCode.Unauthorized, $"{scheme}: {accepted.StatusCode} {server.Log}");
                var answer = accepted.Headers.WwwAuthenticate.SingleOrDefault(h => h.Scheme == scheme);
                Assert.True(answer is not null, $"{scheme}: {accepted.StatusCode} {accepted.Headers} {server.Log}");
                client.GetOutgoingBlob(Convert.FromBase64String(answer.Parameter!), out var status);
                Assert.Equal(NegotiateAuthenticationStatusCode.Completed, status);
                Assert.True(client.IsMutuallyAuthenticated);
            }

            // A ticket for another account's service (the DC's ldap/) cannot be decrypted with the keytab.
            using (var http = Http(address))
            using (var otherClient = Client("Kerberos", user.SamAccountName, password, "ldap/" + KeytabLab.Server))
            using (var other = await PostWithTicketAsync(http, "Kerberos", otherClient))
            {
                Assert.Equal(HttpStatusCode.Unauthorized, other.StatusCode);
            }

            Assert.False(server.HasExited);
            Assert.Equal(["admng-dc-security.toml", "db.sqlite", "openwec.conf.toml"],
                System.IO.Directory.EnumerateFileSystemEntries(work).Select(Path.GetFileName).Where(n => !n!.StartsWith("db.sqlite-", StringComparison.Ordinal)).Order(StringComparer.Ordinal));
        }
        finally
        {
            System.IO.Directory.Delete(work, recursive: true);
        }
    }

    /// <summary>The shipped <c>openwec.conf.toml</c> (7.2) with the test's keytab, database, host name, SPN and port.</summary>
    private static string Configuration(string work, string fqdn, int port)
    {
        var shipped = File.ReadAllText(Path.Combine(RepositoryRoot(), "packaging", "admng-openwec", "etc", "openwec.conf.toml"));
        return shipped
            .Replace("/etc/admng-watchdog/admng-wd.keytab", KeytabLab.KeytabPath, StringComparison.Ordinal)
            .Replace("/var/lib/openwec/db/openwec.sqlite", Path.Combine(work, "db.sqlite"), StringComparison.Ordinal)
            .Replace("HTTP/admng-wd.contoso.com@CONTOSO.COM", $"HTTP/{fqdn}@{KeytabLab.Realm}", StringComparison.Ordinal)
            .Replace("admng-wd.contoso.com", fqdn, StringComparison.Ordinal)
            .Replace("listen_address = \"0.0.0.0\"", "listen_address = \"127.0.0.1\"", StringComparison.Ordinal)
            .Replace("listen_port = 5985", $"listen_port = {port}", StringComparison.Ordinal);
    }

    private static HttpClient Http(Uri address) =>
        new(new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.Zero, UseProxy = false }) { BaseAddress = address };

    private static NegotiateAuthentication Client(string package, string user, string password, string spn) => new(new NegotiateAuthenticationClientOptions
    {
        Package = package,
        Credential = new NetworkCredential(user, password, KeytabLab.Realm),
        TargetName = spn,
        RequiredProtectionLevel = ProtectionLevel.EncryptAndSign,
        RequireMutualAuthentication = true,
    });

    private static async Task<HttpResponseMessage> PostWithTicketAsync(HttpClient http, string scheme, NegotiateAuthentication client)
    {
        var token = client.GetOutgoingBlob(ReadOnlySpan<byte>.Empty, out var status);
        Assert.True(token is not null && status is NegotiateAuthenticationStatusCode.ContinueNeeded or NegotiateAuthenticationStatusCode.Completed,
            $"no ticket for {client.TargetName}: {status}");
        using var request = new HttpRequestMessage(HttpMethod.Post, "/wsman/SubscriptionManager/WEC") { Content = Body() };
        request.Headers.TryAddWithoutValidation("Authorization", scheme + " " + Convert.ToBase64String(token!));
        return await http.SendAsync(request, Ct);
    }

    private static ByteArrayContent Body()
    {
        var content = new ByteArrayContent([]);
        content.Headers.TryAddWithoutValidation("Content-Type", "multipart/encrypted;protocol=\"application/HTTP-Kerberos-session-encrypted\";boundary=\"Encrypted Boundary\"");
        return content;
    }

    private static string Run(string file, string[] args)
    {
        var start = new ProcessStartInfo(file, args) { RedirectStandardOutput = true, RedirectStandardError = true };
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, $"{Path.GetFileName(file)} {string.Join(' ', args)}: {error}");
        return output;
    }

    private static DisposableProcess Start(string openwecd, string config)
    {
        var start = new ProcessStartInfo(openwecd, ["-c", config]) { RedirectStandardOutput = true, RedirectStandardError = true };
        start.Environment["KRB5_CONFIG"] = KeytabLab.Krb5Config;
        start.Environment["KRB5RCACHEDIR"] = Path.GetTempPath();
        var process = Process.Start(start)!;
        var log = new StringBuilder();
        process.ErrorDataReceived += (_, e) => { lock (log) { log.AppendLine(e.Data); } };
        process.OutputDataReceived += (_, e) => { lock (log) { log.AppendLine(e.Data); } };
        process.BeginErrorReadLine();
        process.BeginOutputReadLine();
        return new DisposableProcess(process, log);
    }

    private static async Task WaitForPortAsync(int port, DisposableProcess server)
    {
        for (var i = 0; i < 100; i++)
        {
            Assert.False(server.HasExited, "openwecd exited: " + server.Log);
            try
            {
                using var socket = new TcpClient();
                await socket.ConnectAsync(IPAddress.Loopback, port, Ct);
                return;
            }
            catch (SocketException)
            {
                await Task.Delay(100, Ct);
            }
        }

        Assert.Fail("openwecd does not listen: " + server.Log);
    }

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private static string RepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "AdMng.slnx")))
            {
                return directory.FullName;
            }
        }

        throw new InvalidOperationException("AdMng.slnx not found");
    }

    private sealed class DisposableProcess(Process process, StringBuilder log) : IDisposable
    {
        public bool HasExited => process.HasExited;

        public string Log
        {
            get
            {
                lock (log)
                {
                    return log.ToString();
                }
            }
        }

        public void Dispose()
        {
            if (!process.HasExited)
            {
                process.Kill();
                process.WaitForExit();
            }

            process.Dispose();
        }
    }
}
