using System.Globalization;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using AdMng.Core.ActiveDirectory;
using AdMng.Core.Directory;
using AdMng.Core.Ldap;
using AdMng.Directory;
using AdMng.Watchdog.Contracts.Api;
using AdMng.Watchdog.Contracts.Configuration;
using AdMng.Watchdog.Contracts.Events;
using AdMng.Watchdog.Core.Detection;
using AdMng.Watchdog.Core.Notifications;
using AdMng.Watchdog.IntegrationTests.Infrastructure;
using AdMng.Watchdog.Keytab;
using AdMng.Watchdog.Keytab.Native;
using AdMng.Watchdog.Service.Directory;
using AdMng.Watchdog.Service.Hosting;
using AdMng.Watchdog.Service.Identity;
using AdMng.Watchdog.Testing.Scenarios;
using AdMng.Watchdog.Testing.Stores;
using Microsoft.Extensions.Hosting.Internal;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace AdMng.Watchdog.IntegrationTests.Identity;

/// <summary>
/// Milestone B2 on the Samba lab (Linux design 4.4–4.7, 5.1, 21): <c>admng-watchdog-keytab</c> sets the password of a
/// fresh computer account from a one-time password and rotates it (kvno + 1, previous kvno kept), while an LDAP session
/// over SASL GSSAPI (as the account, from <c>KRB5_CLIENT_KTNAME</c>) and a Negotiate client against the keytab acceptor
/// (<c>KRB5_KTNAME</c>) keep working without a restart; the service's own-account monitor reports the health codes of
/// 4.6/4.7 for injected faults. Every account and group lives in the run's own OU.
/// </summary>
/// <remarks>
/// Needs the lab (<c>ADMNG_TEST_LDAP_*</c>) and the keytab environment in the test process – libkrb5, libldap and Cyrus
/// SASL read it natively: <c>eval "$(tools/testlab/watchdog/keytab-lab.sh env)"</c> (an empty <c>MEMORY:</c> ccache, so
/// the account authenticates from the keytab the tests write).
/// </remarks>
[SupportedOSPlatform("linux")]
public sealed class KeytabLabTests(LabFixture lab)
{
    private const string SkipReason = "The keytab lab environment is not set - eval \"$(tools/testlab/watchdog/keytab-lab.sh env)\" (tools/testlab/watchdog/README.md).";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string KeytabPath => StartupChecks.KeytabPath(Environment.GetEnvironmentVariable("KRB5_CLIENT_KTNAME"));

    private static string Krb5Config => Environment.GetEnvironmentVariable("KRB5_CONFIG")!;

    private static string Server => Environment.GetEnvironmentVariable("ADMNG_TEST_GSSAPI_SERVER")!;

    private static string Realm => WatchdogLab.DomainDns.ToUpperInvariant();

    [Fact]
    public async Task InitAndRotate_KeepTheLdapSessionAndNegotiateClientsWorking()
    {
        RequireKeytabLab();
        using var state = new TempDirectory();
        var otp = OneTimePassword();
        var account = await CreateAccountAsync(otp);

        // init: the one-time password is replaced by a random one; the keytab starts with the account principal.
        var (exit, output) = RunTool(state, ["init", "--password-stdin", "--principal", account.Principal, "--fqdn", account.Fqdn], otp);
        Assert.True(exit == 0, output);
        var kvno = await DirectoryKvnoAsync(account);
        var status = KeytabToolStatus.Read(state.StatusFile)!;
        Assert.Equal(KeytabStatusResults.Ok, status.Result);
        Assert.Equal(kvno, status.Kvno);
        var entries = KeytabFile.Read(KeytabPath);
        Assert.Equal(account.Principal, entries[0].Principal);
        Assert.Equal(6, entries.Count);
        Assert.All(entries, e => Assert.Equal((uint)kvno, e.Kvno));
        Assert.Equal(["aes256-cts-hmac-sha1-96", "aes128-cts-hmac-sha1-96"], entries.Select(e => e.EnctypeName).Distinct());
        Assert.DoesNotContain(otp, output, StringComparison.Ordinal);

        // LDAP over SASL GSSAPI as the account (first keytab entry), sealed; Negotiate against the keytab acceptor.
        await using var before = await ConnectKerberosAsync();
        Assert.True(before.Info.SecurityStrengthFactor >= 56, $"SSF {before.Info.SecurityStrengthFactor}");
        Assert.Contains(account.Name, before.Info.BoundIdentity, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(account.Principal, NativeKerberos.Negotiate(account.HttpSpn), StringComparer.OrdinalIgnoreCase);

        // rotate: kvno + 1 in AD and in the keytab, the previous kvno kept, account entries first.
        (exit, output) = RunTool(state, ["rotate", "--if-older-than", "0"]);
        Assert.True(exit == 0, output);
        Assert.Equal(kvno + 1, await DirectoryKvnoAsync(account));
        var rotated = KeytabFile.Read(KeytabPath);
        Assert.Equal(12, rotated.Count);
        Assert.Equal([account.Principal, account.Principal, account.Principal, account.Principal], rotated.Take(4).Select(e => e.Principal));
        Assert.Equal([(uint)kvno + 1, (uint)kvno], rotated.Select(e => e.Kvno).Distinct());
        Assert.Equal(kvno + 1, KeytabToolStatus.Read(state.StatusFile)!.Kvno);

        // No restart: the open session keeps working, a new bind works, the cached ticket (previous kvno) and a fresh
        // ticket (new kvno, from an empty ccache of this thread) are both accepted by the acceptor.
        Assert.NotNull(await before.GetAsync(WatchdogLab.DomainDn, [Attr.DistinguishedName], Ct));
        await using (var after = await ConnectKerberosAsync())
        {
            Assert.True(after.Info.SecurityStrengthFactor >= 56);
            Assert.NotNull(await after.GetAsync(account.Dn, [Attr.ServicePrincipalName], Ct));
        }

        Assert.Equal(account.Principal, NativeKerberos.Negotiate(account.HttpSpn), StringComparer.OrdinalIgnoreCase);
        var fresh = NativeKerberos.WithThreadCcache("MEMORY:admng-fresh-" + Guid.NewGuid().ToString("N"), () => NativeKerberos.Negotiate(account.HttpSpn));
        Assert.Equal(account.Principal, fresh, StringComparer.OrdinalIgnoreCase);

        // verify: order, KDC, and a fresh ticket's kvno = the keytab's newest.
        (exit, output) = RunTool(state, ["verify"]);
        Assert.True(exit == 0, output);
        Assert.Contains(string.Create(CultureInfo.InvariantCulture, $"has kvno {kvno + 1} = the keytab's newest kvno"), output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FaultInjection_ProducesTheHealthCodesOfTheServiceAccount()
    {
        RequireKeytabLab();
        using var state = new TempDirectory();
        var otp = OneTimePassword();
        var account = await CreateAccountAsync(otp);
        var (exit, output) = RunTool(state, ["init", "--password-stdin", "--principal", account.Principal, "--fqdn", account.Fqdn], otp);
        Assert.True(exit == 0, output);

        var tier0 = await lab.Writer.CreateGroupAsync(account.Ou, "KT Tier0 " + account.Name, Ct);
        var tier0Sid = (await lab.Admin.GetAsync(tier0, [Attr.ObjectSid], Ct))!.ObjectSid!;
        var defaults = new PrivilegedGroupSettings();
        var configuration = new ScenarioConfigProvider(new WatchdogConfiguration
        {
            PrivilegedGroups = defaults with { CriticalSids = [.. defaults.CriticalSids, tier0Sid] },
        });
        var health = new HealthRegistry();

        // Healthy account; no Deny ACE for SELF yet (4.7 item 7). The session is sealed and reported in „Host”.
        await using (var monitor = MonitorHarness.Create(account, state, health, configuration))
        {
            await monitor.RunAsync();
            AssertCheck(health, HealthCheckCodes.ServiceAccountPrivileged, HealthState.Ok);
            AssertCheck(health, HealthCheckCodes.ServiceAccountKeytabStale, HealthState.Ok);
            AssertCheck(health, HealthCheckCodes.ServiceAccountKeytabOrder, HealthState.Ok);
            AssertCheck(health, HealthCheckCodes.ServiceAccountRotation, HealthState.Ok);
            AssertCheck(health, HealthCheckCodes.ServiceAccountPasswordAge, HealthState.Ok);
            AssertCheck(health, HealthCheckCodes.HostPrimaryKdcNotPdc, HealthState.Ok);
            AssertCheck(health, HealthCheckCodes.DirectoryNotSealed, HealthState.Ok);
            Assert.Equal([HealthCheckCodes.DenySelfAceMissing], AssertCheck(health, HealthCheckCodes.ServiceAccountDrift, HealthState.Warning).Args);
            Assert.True(health.Host?.LdapSsf >= 56, $"SSF {health.Host?.LdapSsf}");
            Assert.Equal(await DirectoryKvnoAsync(account), health.ServiceAccount!.KeytabKvno);
            Assert.Equal(account.Dn, health.ServiceAccount.AccountDn, StringComparer.OrdinalIgnoreCase);
            Assert.Equal(0, monitor.Terminator.ExitCode);
            Assert.Equal(WatchEventKind.HealthServiceAccount, (await monitor.Store.GetAsync(1, Ct))?.Kind);
            Assert.Equal(1, await monitor.Store.GetLatestIdAsync(Ct));

            // The Deny ACE for SELF clears the drift.
            var descriptor = (await lab.Admin.GetAsync(account.Dn, [Attr.NtSecurityDescriptor], Ct))!.GetBytes(Attr.NtSecurityDescriptor)!;
            await lab.Writer.ReplaceAsync(account.Dn, Attr.NtSecurityDescriptor,
                SecurityDescriptorBuilder.PrependAce(descriptor, SecurityDescriptorBuilder.Deny("S-1-5-10", ServiceAccountChecks.DenySelfMask)), Ct);
            await monitor.RunAsync();
            AssertCheck(health, HealthCheckCodes.ServiceAccountDrift, HealthState.Ok);
            Assert.True(health.ServiceAccount!.DenySelfAcePresent);

            // Delegation allowed again and RC4 re-enabled: drift with the attribute names.
            await lab.Writer.SetAccountControlAsync(account.Dn, UserAccountControl.WorkstationTrustAccount, Ct);
            await lab.Writer.ReplaceAsync(account.Dn, Attr.SupportedEncryptionTypes, "28", Ct);
            await monitor.RunAsync();
            Assert.Equal(["userAccountControl", "msDS-SupportedEncryptionTypes"], AssertCheck(health, HealthCheckCodes.ServiceAccountDrift, HealthState.Warning).Args);
            Assert.Equal(2, await monitor.Store.GetLatestIdAsync(Ct));    // one HealthServiceAccount event per new problem
            await lab.Writer.SetAccountControlAsync(account.Dn, UserAccountControl.WorkstationTrustAccount | UserAccountControl.NotDelegated, Ct);
            await lab.Writer.ReplaceAsync(account.Dn, Attr.SupportedEncryptionTypes, "24", Ct);
        }

        // Member of a privileged root: the first run stops the service with exit code 8; later runs report Error.
        await lab.Writer.AddMembersAsync(tier0, [account.Dn], Ct);
        await using (var monitor = MonitorHarness.Create(account, state, health, configuration))
        {
            await monitor.RunAsync();
            Assert.Equal(StartupExitCodes.PrivilegedServiceAccount, monitor.Terminator.ExitCode);
            AssertCheck(health, HealthCheckCodes.ServiceAccountPrivileged, HealthState.Error);
        }

        await lab.Writer.RemoveMembersAsync(tier0, [account.Dn], Ct);

        // Password age through the clock (32 / 35 days), clock skew against the DC's RootDSE currentTime.
        await using (var aged = MonitorHarness.Create(account, state, health, configuration, time: new OffsetTimeProvider(TimeSpan.FromDays(33))))
        {
            await aged.RunAsync();
            AssertCheck(health, HealthCheckCodes.ServiceAccountPasswordAge, HealthState.Warning);
            AssertCheck(health, HealthCheckCodes.ServiceAccountPrivileged, HealthState.Ok);
        }

        await using (var aged = MonitorHarness.Create(account, state, health, configuration, time: new OffsetTimeProvider(TimeSpan.FromDays(36))))
        {
            await aged.RunAsync();
            AssertCheck(health, HealthCheckCodes.ServiceAccountPasswordAge, HealthState.Error);
        }

        await using (var session = await ConnectKerberosAsync())
        {
            var dcTime = (await session.GetAsync(string.Empty, [Attr.CurrentTime], Ct))!.GetGeneralizedTime(Attr.CurrentTime)!.Value;
            var now = DateTimeOffset.UtcNow;
            Assert.Equal(HealthState.Ok, ClockSkewCheck.Evaluate(dcTime, now, Server).State);
            Assert.Equal(HealthState.Warning, ClockSkewCheck.Evaluate(dcTime, now + TimeSpan.FromSeconds(90), Server).State);
            Assert.Equal(HealthState.Error, ClockSkewCheck.Evaluate(dcTime, now - TimeSpan.FromMinutes(5), Server).State);
        }

        // kpasswd/KDC unreachable: changeFailed, nothing changed → rotation Warning.
        var unreachable = WriteKrb5Config(state, "unreachable.conf", kdcs: ["127.0.0.1:1"], primary: "127.0.0.1:1");
        var kvnoBefore = await DirectoryKvnoAsync(account);
        (exit, output) = RunTool(state, ["rotate", "--if-older-than", "0", "--krb5-config", unreachable]);
        Assert.Equal(2, exit);
        Assert.Equal(KeytabStatusResults.ChangeFailed, KeytabToolStatus.Read(state.StatusFile)!.Result);
        Assert.Equal(kvnoBefore, await DirectoryKvnoAsync(account));
        await using (var monitor = MonitorHarness.Create(account, state, health, configuration))
        {
            await monitor.RunAsync();
            Assert.Equal([KeytabStatusResults.ChangeFailed, "KRB5_KDC_UNREACH"], AssertCheck(health, HealthCheckCodes.ServiceAccountRotation, HealthState.Warning).Args);
            AssertCheck(health, HealthCheckCodes.ServiceAccountKeytabStale, HealthState.Ok);
        }

        // The keytab cannot be written after the change (4.6 "lost password"): keytabWriteFailed, stale keytab → Errors.
        var keytabBefore = File.ReadAllBytes(KeytabPath);
        (exit, output) = RunTool(state, ["rotate", "--if-older-than", "0"], fault: step =>
        {
            if (step == "written")
            {
                throw new IOException("Injected keytab write failure.");
            }
        });
        Assert.Equal(2, exit);
        Assert.Contains("Set-ADAccountPassword -Identity '" + account.Name + "' -Reset -NewPassword $otp", output, StringComparison.Ordinal);
        Assert.Equal(keytabBefore, File.ReadAllBytes(KeytabPath));
        Assert.Equal(kvnoBefore + 1, await DirectoryKvnoAsync(account));
        Assert.Equal(KeytabStatusResults.KeytabWriteFailed, KeytabToolStatus.Read(state.StatusFile)!.Result);
        await using (var monitor = MonitorHarness.Create(account, state, health, configuration))
        {
            await monitor.RunAsync();
            AssertCheck(health, HealthCheckCodes.ServiceAccountRotation, HealthState.Error);
            Assert.Equal(
                [kvnoBefore.ToString(CultureInfo.InvariantCulture), (kvnoBefore + 1).ToString(CultureInfo.InvariantCulture)],
                AssertCheck(health, HealthCheckCodes.ServiceAccountKeytabStale, HealthState.Error).Args);
        }

        // Recovery: a new one-time password set by the administrator, then init.
        var newOtp = OneTimePassword();
        await lab.Writer.ResetPasswordAsync(account.Dn, newOtp, Ct);
        (exit, output) = RunTool(state, ["init", "--password-stdin", "--principal", account.Principal, "--fqdn", account.Fqdn], newOtp);
        Assert.True(exit == 0, output);

        // A second KDC that has not replicated (unreachable): replicationPending is not an error.
        var twoKdcs = WriteKrb5Config(state, "two-kdcs.conf", kdcs: [Server, "127.0.0.1:1"], primary: Server);
        (exit, output) = RunTool(state, ["rotate", "--if-older-than", "0", "--krb5-config", twoKdcs]);
        Assert.True(exit == 0, output);
        Assert.Equal(KeytabStatusResults.ReplicationPending, KeytabToolStatus.Read(state.StatusFile)!.Result);
        await using (var monitor = MonitorHarness.Create(account, state, health, configuration))
        {
            await monitor.RunAsync();
            AssertCheck(health, HealthCheckCodes.ServiceAccountRotation, HealthState.Ok);
            AssertCheck(health, HealthCheckCodes.ServiceAccountKeytabStale, HealthState.Ok);
        }

        // A keytab that starts with the HTTP/ entries: verify fails, the monitor reports keytabOrder.
        ReorderKeytab(account);
        (exit, output) = RunTool(state, ["verify"]);
        Assert.Equal(2, exit);
        Assert.Equal("keytabOrder", KeytabToolStatus.Read(state.StatusFile)!.ErrorCode);
        await using (var monitor = MonitorHarness.Create(account, state, health, configuration))
        {
            await monitor.RunAsync();
            AssertCheck(health, HealthCheckCodes.ServiceAccountKeytabOrder, HealthState.Error);
            AssertCheck(health, HealthCheckCodes.ServiceAccountRotation, HealthState.Error);
        }

        // primary_kdc of krb5.conf is not the PDC emulator.
        var otherPrimary = WriteKrb5Config(state, "other-primary.conf", kdcs: [Server], primary: "dc99." + WatchdogLab.DomainDns);
        await using (var monitor = MonitorHarness.Create(account, state, health, configuration, krb5Config: otherPrimary))
        {
            await monitor.RunAsync();
            AssertCheck(health, HealthCheckCodes.HostPrimaryKdcNotPdc, HealthState.Warning);
        }

        // A bind without a SASL security layer is refused (minssf violation) → directory.notSealed; a sealed one clears it.
        await using (var monitor = MonitorHarness.Create(account, state, health, configuration))
        {
            LdapConnectionFactory.SaslSecurityLimitsForTests.Value = (0, 0);
            try
            {
                var error = await Assert.ThrowsAsync<DirectoryException>(async () => await monitor.Provider.GetSessionAsync(Ct));
                Assert.Equal(DirectoryErrorKind.StrongAuthRequired, error.Kind);
                AssertCheck(health, HealthCheckCodes.DirectoryNotSealed, HealthState.Error);
            }
            finally
            {
                LdapConnectionFactory.SaslSecurityLimitsForTests.Value = null;
            }

            await monitor.Provider.GetSessionAsync(Ct);
            AssertCheck(health, HealthCheckCodes.DirectoryNotSealed, HealthState.Ok);
        }
    }

    [Fact]
    public void PerKdcProfile_DoesNotFallBackToTheOtherKdcs()
    {
        RequireKeytabLab();
        using var state = new TempDirectory();
        using var config = KerberosConfig.Load(Krb5Config, Realm);
        using var context = Krb5Context.Open(config.ProfileFor("127.0.0.1:1"));
        using var client = context.ParseName("Administrator@" + Realm);
        using var password = new SecretBuffer(64);
        foreach (var b in Encoding.UTF8.GetBytes(WatchdogLab.AdminPassword))
        {
            password.Append(b);
        }

        var code = context.GetInitialCredentials(client, password, null, IntPtr.Zero, out _);

        Assert.Equal("KRB5_KDC_UNREACH", Krb5ErrorNames.Of(code));
    }

    // ---- helpers -----------------------------------------------------------------------------------------------------------

    private static void RequireKeytabLab()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "MIT Kerberos and SASL GSSAPI are the Linux path.");
        WatchdogLab.RequireLab();
        Assert.SkipWhen(
            string.IsNullOrEmpty(Environment.GetEnvironmentVariable("ADMNG_TEST_KEYTAB_DIR"))
            || string.IsNullOrEmpty(Environment.GetEnvironmentVariable("ADMNG_TEST_GSSAPI_SERVER"))
            || string.IsNullOrEmpty(Environment.GetEnvironmentVariable("KRB5_CLIENT_KTNAME"))
            || string.IsNullOrEmpty(Environment.GetEnvironmentVariable("KRB5_CONFIG"))
            || string.IsNullOrEmpty(Environment.GetEnvironmentVariable("LDAPCONF"))
            || Environment.GetEnvironmentVariable("KRB5CCNAME")?.StartsWith("MEMORY:", StringComparison.Ordinal) != true,
            SkipReason);

        // The process-wide MEMORY ccache may hold a ticket of an earlier test's account.
        NativeKerberos.DestroyDefaultCcache();
    }

    private async Task<TestAccount> CreateAccountAsync(string otp)
    {
        var ou = await lab.CreateTestOuAsync("Keytab", Ct);
        var name = "WD" + Convert.ToHexString(RandomNumberGenerator.GetBytes(3));
        var fqdn = name.ToLowerInvariant() + "." + WatchdogLab.DomainDns;
        var dn = DistinguishedName.Parse(ou).Child("CN", name).ToString();
        await lab.Admin.AddAsync(dn, new Dictionary<string, IReadOnlyList<object>>
        {
            [Attr.ObjectClass] = ["top", "person", "organizationalPerson", ObjectClasses.User, ObjectClasses.Computer],
            [Attr.SamAccountName] = [name + "$"],
            [Attr.UserAccountControl] = [Flags(UserAccountControl.WorkstationTrustAccount | UserAccountControl.AccountDisable | UserAccountControl.NotDelegated)],
            [Attr.DnsHostName] = [fqdn],
            [Attr.ServicePrincipalName] = ["HTTP/" + fqdn, "HOST/" + fqdn, "HOST/" + name],
            [Attr.SupportedEncryptionTypes] = ["24"],
        }, Ct);

        // Design 4.2: created disabled with the one-time password, enabled only then.
        await lab.Writer.ResetPasswordAsync(dn, otp, Ct);
        await lab.Writer.SetAccountControlAsync(dn, UserAccountControl.WorkstationTrustAccount | UserAccountControl.NotDelegated, Ct);
        return new TestAccount(ou, dn, name + "$", name + "$@" + Realm, fqdn);
    }

    private async Task<int> DirectoryKvnoAsync(TestAccount account) =>
        (await lab.Admin.GetAsync(account.Dn, ["msDS-KeyVersionNumber"], Ct))!.GetInt32("msDS-KeyVersionNumber")!.Value;

    private static async Task<IDirectorySession> ConnectKerberosAsync() =>
        await new LdapDirectoryConnector().ConnectAsync(new ConnectionOptions
        {
            Server = Server,
            Domain = WatchdogLab.DomainDns,
            Authentication = AuthenticationMode.CurrentUser,
            Security = TransportSecurity.SignAndSeal,
            RequestTimeout = TimeSpan.FromSeconds(60),
            ConnectTimeout = TimeSpan.FromSeconds(5),
        }, null, Ct);

    /// <summary>Runs the keytab tool in-process against the test's keytab, configuration and state folder.</summary>
    private static (int Exit, string Output) RunTool(TempDirectory state, string[] args, string? stdin = null, Action<string>? fault = null)
    {
        using var output = new StringWriter(CultureInfo.InvariantCulture);
        using var input = new MemoryStream(Encoding.UTF8.GetBytes((stdin ?? string.Empty) + "\n"));
        var host = new KeytabToolHost
        {
            Out = output,
            Error = output,
            Input = input,
            InputIsTerminal = false,
            ReplicationPollInterval = TimeSpan.FromSeconds(1),
            FaultPoint = fault,
        };
        var common = new List<string>(args);
        if (!args.Contains("--krb5-config"))
        {
            common.AddRange(["--krb5-config", Krb5Config]);
        }

        common.AddRange(["--keytab", KeytabPath, "--state-dir", state.Path]);
        if (args[0] is "init" or "rotate")
        {
            common.AddRange(["--replication-timeout", "0"]);
        }

        var exit = KeytabTool.Run(common, host, null);
        return (exit, output.ToString());
    }

    /// <summary>The keytab rewritten with the account's entries last (the wrong order of design 4.4).</summary>
    private static void ReorderKeytab(TestAccount account)
    {
        var temp = KeytabPath + ".reordered";
        File.WriteAllBytes(temp, [0x05, 0x02]);
        using (var context = Krb5Context.Open(Krb5Config))
        {
            var keytab = context.ResolveKeytab(temp);
            try
            {
                context.CopyEntries(KeytabPath, keytab, e => !string.Equals(e.Principal, account.Principal, StringComparison.OrdinalIgnoreCase));
                context.CopyEntries(KeytabPath, keytab, e => string.Equals(e.Principal, account.Principal, StringComparison.OrdinalIgnoreCase));
            }
            finally
            {
                context.CloseKeytab(keytab);
            }
        }

        File.Move(temp, KeytabPath, overwrite: true);
    }

    private static string WriteKrb5Config(TempDirectory state, string name, IReadOnlyList<string> kdcs, string primary)
    {
        var path = System.IO.Path.Combine(state.Path, name);
        var lines = new List<string>
        {
            "[libdefaults]",
            "    default_realm = " + Realm,
            "    dns_lookup_kdc = false",
            "    dns_lookup_realm = false",
            "    dns_canonicalize_hostname = false",
            "    rdns = false",
            "    permitted_enctypes = aes256-cts-hmac-sha1-96 aes128-cts-hmac-sha1-96",
            "[realms]",
            "    " + Realm + " = {",
        };
        lines.AddRange(kdcs.Select(k => "        kdc = " + k));
        lines.Add("        primary_kdc = " + primary);
        lines.Add("        kpasswd_server = " + primary);
        lines.Add("    }");
        File.WriteAllLines(path, lines);
        return path;
    }

    private static HealthCheckDto AssertCheck(HealthRegistry health, string code, HealthState state)
    {
        var check = health.Checks.SingleOrDefault(c => c.Code == code);
        Assert.True(check is not null, $"No health check {code}.");
        Assert.True(check.State == state, $"{code}: {check.State} ({check.Detail}; {string.Join(", ", check.Args)}), expected {state}.");
        return check;
    }

    private static string OneTimePassword() => "Otp-" + Convert.ToBase64String(RandomNumberGenerator.GetBytes(24)) + "-9a!";

    private static string Flags(UserAccountControl flags) => ((int)flags).ToString(CultureInfo.InvariantCulture);

    private sealed record TestAccount(string Ou, string Dn, string Name, string Principal, string Fqdn)
    {
        public string HttpSpn => "HTTP/" + Fqdn;
    }

    private sealed class TempDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Directory.CreateTempSubdirectory("admng-keytab-it-").FullName;

        public string StatusFile => System.IO.Path.Combine(Path, "status.json");

        public void Dispose() => System.IO.Directory.Delete(Path, recursive: true);
    }

    private sealed class OffsetTimeProvider(TimeSpan offset) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => base.GetUtcNow() + offset;
    }

    private sealed class RecordingTerminator : IProcessTerminator
    {
        public int ExitCode { get; private set; }

        public void Exit(int exitCode) => ExitCode = exitCode;
    }

    private sealed class StaticOptions<T>(T value) : IOptionsMonitor<T>
    {
        public T CurrentValue => value;

        public T Get(string? name) => value;

        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }

    /// <summary>The service's real directory provider (Directory.Servers, Kerberos sign and seal) and own-account monitor.</summary>
    private sealed class MonitorHarness : IAsyncDisposable
    {
        private MonitorHarness(ServiceDirectorySessionProvider provider, ServiceAccountMonitor job, RecordingTerminator terminator, InMemoryWatchdogStore store)
        {
            Provider = provider;
            Job = job;
            Terminator = terminator;
            Store = store;
        }

        public InMemoryWatchdogStore Store { get; }

        public ServiceDirectorySessionProvider Provider { get; }

        public ServiceAccountMonitor Job { get; }

        public RecordingTerminator Terminator { get; }

        public static MonitorHarness Create(
            TestAccount account, TempDirectory state, HealthRegistry health, ScenarioConfigProvider configuration, string? krb5Config = null, TimeProvider? time = null)
        {
            time ??= TimeProvider.System;
            var options = new StaticOptions<ServiceOptions>(new ServiceOptions
            {
                Directory = new DirectoryBootstrapOptions
                {
                    Domain = WatchdogLab.DomainDns,
                    Servers = [Server],
                    PreferPdc = true,
                    Authentication = AuthenticationMode.CurrentUser,
                    Security = TransportSecurity.SignAndSeal,
                },
                Kerberos = new KerberosBootstrapOptions { Realm = Realm, ServicePrincipal = account.Principal, KeytabStatusFile = state.StatusFile },
            });
            var factory = new DirectoryConnectionOptionsFactory(options, new HostingEnvironment { EnvironmentName = "Production" });
            var provider = new ServiceDirectorySessionProvider(
                new LdapDirectoryConnector(), factory, options, NullLogger<ServiceDirectorySessionProvider>.Instance, health, configuration, time);
            var terminator = new RecordingTerminator();
            var store = new InMemoryWatchdogStore(time);
            var job = new ServiceAccountMonitor(
                provider,
                options,
                configuration,
                health,
                store,
                new WatchdogSignals(time),
                terminator,
                new ServiceAccountPaths(KeytabPath, krb5Config ?? Krb5Config),
                time,
                NullLogger<ServiceAccountMonitor>.Instance);
            return new MonitorHarness(provider, job, terminator, store);
        }

        public async Task RunAsync()
        {
            var result = await Job.RunOnceAsync(Ct);
            Assert.False(result.Failed, result.FailureCode);
        }

        public ValueTask DisposeAsync() => Provider.DisposeAsync();
    }
}
