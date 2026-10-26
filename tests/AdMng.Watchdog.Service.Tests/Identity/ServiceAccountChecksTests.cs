using System.Buffers.Binary;
using System.Text;
using AdMng.Watchdog.Contracts.Api;
using AdMng.Watchdog.Core.Detection;
using AdMng.Watchdog.Service.Identity;

namespace AdMng.Watchdog.Service.Tests.Identity;

/// <summary>The own-account self-checks of stage B design 4.6/4.7 as pure evaluations, the managed keytab reader and the
/// krb5.conf reader.</summary>
public sealed class ServiceAccountChecksTests
{
    private const string Principal = "ADMNG-WD$@CONTOSO.COM";
    private static readonly DateTimeOffset Now = new(2026, 10, 20, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void HealthyAccount_ReportsEveryCheckOk()
    {
        var result = ServiceAccountChecks.Evaluate(Healthy(), Now);

        Assert.All(result.Checks, c => Assert.True(c.State == HealthState.Ok, $"{c.Code}: {c.State} {string.Join(",", c.Args)}"));
        Assert.Equal(
            [
                HealthCheckCodes.ServiceAccountPrivileged, HealthCheckCodes.ServiceAccountDrift, HealthCheckCodes.ServiceAccountPasswordAge,
                HealthCheckCodes.ServiceAccountKeytabStale, HealthCheckCodes.ServiceAccountKeytabOrder, HealthCheckCodes.ServiceAccountRotation,
            ],
            result.Checks.Select(c => c.Code));
        Assert.Equal(HealthState.Ok, result.Status.State);
        Assert.Equal(7, result.Status.KeytabKvno);
        Assert.Equal(7, result.Status.DirectoryKvno);
        Assert.True(result.Status.NotDelegated);
        Assert.True(result.Status.DenySelfAcePresent);
        Assert.True(result.Status.KeytabOrderValid);
        Assert.False(result.Status.Privileged);
        Assert.Equal(["aes256-cts-hmac-sha1-96", "aes128-cts-hmac-sha1-96"], result.Status.KeytabEncryptionTypes);
    }

    [Theory]
    [InlineData(KeytabStatusResults.Ok, HealthState.Ok)]
    [InlineData(KeytabStatusResults.ReplicationPending, HealthState.Ok)]
    [InlineData(KeytabStatusResults.ChangeFailed, HealthState.Warning)]
    [InlineData(KeytabStatusResults.KeytabWriteFailed, HealthState.Error)]
    [InlineData(KeytabStatusResults.OtpActive, HealthState.Error)]
    [InlineData(KeytabStatusResults.VerifyFailed, HealthState.Error)]
    public void RotationResult_MapsToTheHealthStateOfDesign46(string result, HealthState expected)
    {
        var facts = Healthy() with { ToolStatus = new KeytabToolStatus { Result = result, ErrorCode = "KRB5_KDC_UNREACH", Kvno = 7 } };

        var check = Check(ServiceAccountChecks.Evaluate(facts, Now), HealthCheckCodes.ServiceAccountRotation);

        Assert.Equal(expected, check.State);
        Assert.Equal([result, "KRB5_KDC_UNREACH"], check.Args);
    }

    [Fact]
    public void ChangeFailed_WithAPasswordOlderThan35Days_IsAnError()
    {
        var facts = Healthy() with
        {
            PasswordLastSet = Now - TimeSpan.FromDays(36),
            ToolStatus = new KeytabToolStatus { Result = KeytabStatusResults.ChangeFailed },
        };

        var result = ServiceAccountChecks.Evaluate(facts, Now);

        Assert.Equal(HealthState.Error, Check(result, HealthCheckCodes.ServiceAccountRotation).State);
        Assert.Equal(HealthState.Error, Check(result, HealthCheckCodes.ServiceAccountPasswordAge).State);
        Assert.Equal(["36"], Check(result, HealthCheckCodes.ServiceAccountPasswordAge).Args);
        Assert.Equal(HealthState.Warning, Check(ServiceAccountChecks.Evaluate(facts with { PasswordLastSet = Now - TimeSpan.FromDays(33) }, Now), HealthCheckCodes.ServiceAccountPasswordAge).State);
    }

    [Fact]
    public void MissingStatusFile_IsAWarning()
    {
        var check = Check(ServiceAccountChecks.Evaluate(Healthy() with { ToolStatus = null }, Now), HealthCheckCodes.ServiceAccountRotation);

        Assert.Equal(HealthState.Warning, check.State);
        Assert.Equal(["missing"], check.Args);
    }

    [Fact]
    public void KeytabBehindTheDirectory_IsStale_ExceptOneBehindWhileReplicationIsPending()
    {
        var stale = Healthy() with { KeyVersionNumber = 8 };

        Assert.Equal(HealthState.Error, Check(ServiceAccountChecks.Evaluate(stale, Now), HealthCheckCodes.ServiceAccountKeytabStale).State);
        Assert.Equal(["7", "8"], Check(ServiceAccountChecks.Evaluate(stale, Now), HealthCheckCodes.ServiceAccountKeytabStale).Args);
        var pending = stale with { ToolStatus = new KeytabToolStatus { Result = KeytabStatusResults.ReplicationPending } };
        Assert.Equal(HealthState.Ok, Check(ServiceAccountChecks.Evaluate(pending, Now), HealthCheckCodes.ServiceAccountKeytabStale).State);
        Assert.Equal(HealthState.Error, Check(ServiceAccountChecks.Evaluate(Healthy() with { Keytab = null, KeytabError = "accessDenied" }, Now), HealthCheckCodes.ServiceAccountKeytabStale).State);
    }

    [Fact]
    public void KeytabStartingWithTheHttpEntries_IsAnOrderError()
    {
        var reordered = Healthy().Keytab!.OrderBy(e => e.Principal.StartsWith("ADMNG", StringComparison.Ordinal)).ToList();

        var result = ServiceAccountChecks.Evaluate(Healthy() with { Keytab = reordered }, Now);

        Assert.Equal(HealthState.Error, Check(result, HealthCheckCodes.ServiceAccountKeytabOrder).State);
        Assert.False(result.Status.KeytabOrderValid);
    }

    [Fact]
    public void Drift_NamesTheAttributes()
    {
        var facts = Healthy() with
        {
            UserAccountControl = 0x1000 | ServiceAccountChecks.TrustedForDelegation,
            SupportedEncryptionTypes = 0x1C,
            ServicePrincipalNames = ["HTTP/admng-wd.contoso.com", "HOST/admng-wd.contoso.com", "HOST/ADMNG-WD", "cifs/admng-wd.contoso.com"],
            DnsHostName = "other.contoso.com",
            Dacl = [Ace(0x01, 0x10, ServiceAccountChecks.SelfSid, 0x2B)],     // inherited deny does not count
        };

        var check = Check(ServiceAccountChecks.Evaluate(facts, Now), HealthCheckCodes.ServiceAccountDrift);

        Assert.Equal(HealthState.Warning, check.State);
        Assert.Equal(["userAccountControl", "msDS-SupportedEncryptionTypes", "servicePrincipalName", "dNSHostName", HealthCheckCodes.DenySelfAceMissing], check.Args);
    }

    [Fact]
    public void RestrictedKrbHostSpns_AreHarmless_AndAWiderDenyMaskCounts()
    {
        var facts = Healthy() with
        {
            ServicePrincipalNames = [.. Healthy().ServicePrincipalNames, "RestrictedKrbHost/admng-wd.contoso.com"],
            Dacl = [Ace(0x01, 0x00, ServiceAccountChecks.SelfSid, 0x000F01FF & ~0x10000)],
        };

        Assert.Equal(HealthState.Ok, Check(ServiceAccountChecks.Evaluate(facts, Now), HealthCheckCodes.ServiceAccountDrift).State);
        Assert.False(ServiceAccountChecks.HasDenySelfAce([Ace(0x01, 0x00, ServiceAccountChecks.SelfSid, 0x28)]));
        Assert.False(ServiceAccountChecks.HasDenySelfAce([Ace(0x00, 0x00, ServiceAccountChecks.SelfSid, 0x2B)]));
    }

    [Fact]
    public void PrivilegedMembership_IsAnError_WithTheCountOnly()
    {
        var check = Check(ServiceAccountChecks.Evaluate(Healthy() with { PrivilegedSids = ["S-1-5-21-1-2-3-512"] }, Now), HealthCheckCodes.ServiceAccountPrivileged);

        Assert.Equal(HealthState.Error, check.State);
        Assert.Equal(["1"], check.Args);
    }

    [Theory]
    [InlineData("S-1-5-21-1-2-3-512", true)]
    [InlineData("S-1-5-21-9-9-9-519", true)]
    [InlineData("S-1-5-21-1-2-3-498", true)]
    [InlineData("S-1-5-32-551", true)]
    [InlineData("S-1-5-21-1-2-3-1104", true)]       // DnsAdmins by its SID
    [InlineData("S-1-5-21-1-2-3-515", false)]       // Domain Computers
    [InlineData("S-1-5-32-554", false)]             // Pre-Windows 2000 Compatible Access (lab)
    [InlineData("S-1-5-21-1-2-3-5120", false)]
    public void PrivilegedSids_AreTheGroupsOf47Item1(string sid, bool privileged) =>
        Assert.Equal(privileged, ServiceAccountMonitor.IsPrivileged(sid, "S-1-5-21-1-2-3-1104"));

    [Fact]
    public void KeytabFile_ListsPrincipalsKvnosAndEnctypes_InFileOrder()
    {
        var bytes = Keytab(
            Entry("ADMNG-WD$", null, 4, 18), Entry("ADMNG-WD$", null, 4, 17), Hole(30), Entry("HTTP", "admng-wd.contoso.com", 300, 18));

        var entries = KeytabFile.Parse(bytes);

        Assert.Equal(["ADMNG-WD$@CONTOSO.COM", "ADMNG-WD$@CONTOSO.COM", "HTTP/admng-wd.contoso.com@CONTOSO.COM"], entries.Select(e => e.Principal));
        Assert.Equal([4u, 4u, 300u], entries.Select(e => e.Kvno));
        Assert.Equal(["aes256-cts-hmac-sha1-96", "aes128-cts-hmac-sha1-96", "aes256-cts-hmac-sha1-96"], entries.Select(e => e.EnctypeName));
        Assert.Throws<InvalidDataException>(() => KeytabFile.Parse([0x05, 0x01]));
        Assert.Throws<InvalidDataException>(() => KeytabFile.Parse(bytes.AsSpan(0, bytes.Length - 3)));
    }

    [Fact]
    public void Krb5Conf_PrimaryKdcOfTheRealm()
    {
        string[] lines =
        [
            "[libdefaults]", "  default_realm = CONTOSO.COM", "[realms]", "  OTHER.COM = {", "    primary_kdc = dc9.other.com", "  }",
            "  CONTOSO.COM = {", "    kdc = DC01.contoso.com", "    kdc = DC02.contoso.com", "    primary_kdc = DC01.contoso.com:88", "  }",
        ];

        Assert.Equal("DC01.contoso.com", Krb5ConfigFile.PrimaryKdc(lines, "CONTOSO.COM"));
        Assert.Equal("dc9.other.com", Krb5ConfigFile.PrimaryKdc(lines, "OTHER.COM"));
        Assert.Null(Krb5ConfigFile.PrimaryKdc(lines, "MISSING.COM"));
        Assert.Equal("DC01.contoso.com", Krb5ConfigFile.PrimaryKdc(RepositoryLines("packaging/admng-watchdog/etc/krb5.conf"), "CONTOSO.COM"));
    }

    private static IEnumerable<string> RepositoryLines(string path) =>
        Infrastructure.RepositoryFiles.ReadAllText(path).Split('\n');

    private static HealthCheckDto Check(ServiceAccountEvaluation result, string code) => result.Checks.Single(c => c.Code == code);

    private static ServiceAccountFacts Healthy() => new()
    {
        Principal = Principal,
        AccountDn = "CN=ADMNG-WD,OU=Service Accounts,OU=Tier 0,OU=Admin,DC=contoso,DC=com",
        UserAccountControl = 0x1000 | ServiceAccountChecks.NotDelegated,
        SupportedEncryptionTypes = 0x18,
        ServicePrincipalNames = ["HTTP/admng-wd.contoso.com", "HOST/admng-wd.contoso.com", "HOST/ADMNG-WD"],
        DnsHostName = "admng-wd.contoso.com",
        PasswordLastSet = Now - TimeSpan.FromDays(3),
        KeyVersionNumber = 7,
        TokenGroups = ["S-1-5-21-1-2-3-515"],
        PrivilegedSids = [],
        Dacl = [Ace(0x01, 0x00, ServiceAccountChecks.SelfSid, ServiceAccountChecks.DenySelfMask), Ace(0x00, 0x00, "S-1-5-18", 0x000F01FF)],
        Keytab =
        [
            new(Principal, 7, 18), new(Principal, 7, 17), new(Principal, 6, 18), new(Principal, 6, 17),
            new("HTTP/admng-wd.contoso.com@CONTOSO.COM", 7, 18), new("HTTP/admng-wd.contoso.com@CONTOSO.COM", 7, 17),
            new("host/admng-wd.contoso.com@CONTOSO.COM", 7, 18), new("host/admng-wd.contoso.com@CONTOSO.COM", 7, 17),
        ],
        ToolStatus = new KeytabToolStatus { Principal = Principal, Kvno = 7, Result = KeytabStatusResults.Ok, LastChange = Now - TimeSpan.FromDays(3) },
    };

    private static SecurityDescriptorAce Ace(byte type, byte flags, string sid, int mask) =>
        new() { Type = type, Flags = flags, Sid = sid, Mask = mask };

    private static byte[] Keytab(params byte[][] records) => [0x05, 0x02, .. records.SelectMany(r => r)];

    private static byte[] Hole(int size)
    {
        var record = new byte[4 + size];
        BinaryPrimitives.WriteInt32BigEndian(record, -size);
        return record;
    }

    /// <summary>One 0x0502 record (realm CONTOSO.COM, 32 key bytes, 32-bit kvno when it does not fit 8 bits).</summary>
    private static byte[] Entry(string first, string? second, uint kvno, ushort enctype)
    {
        var body = new List<byte>();
        void U16(int v) => body.AddRange([(byte)(v >> 8), (byte)v]);
        void U32(uint v) => body.AddRange([(byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v]);
        void Counted(string s)
        {
            var b = Encoding.UTF8.GetBytes(s);
            U16(b.Length);
            body.AddRange(b);
        }

        U16(second is null ? 1 : 2);
        Counted("CONTOSO.COM");
        Counted(first);
        if (second is not null)
        {
            Counted(second);
        }

        U32(1);                       // KRB5_NT_PRINCIPAL
        U32(1_760_000_000);           // timestamp
        body.Add((byte)Math.Min(kvno, 255));
        U16(enctype);
        U16(32);
        body.AddRange(new byte[32]);
        U32(kvno);
        var record = new byte[4 + body.Count];
        BinaryPrimitives.WriteInt32BigEndian(record, body.Count);
        body.CopyTo(record, 4);
        return record;
    }
}
