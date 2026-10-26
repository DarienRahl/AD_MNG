using System.Runtime.Versioning;
using System.Text;
using AdMng.Watchdog.Contracts.Api;
using AdMng.Watchdog.Keytab;
using AdMng.Watchdog.Keytab.Native;
using AdMng.Watchdog.Service.Identity;

namespace AdMng.Watchdog.Service.Tests.Keytab;

/// <summary>The parts of <c>admng-watchdog-keytab</c> (stage B design 4.4) that need no KDC: command line, salt and
/// principals, the generated password, error names, and the status.json contract with the service. The Kerberos flows
/// run against the Samba lab (AdMng.Watchdog.IntegrationTests, KeytabLabTests).</summary>
[SupportedOSPlatform("linux")]
public sealed class KeytabToolUnitTests
{
    [Theory]
    [InlineData("30d", 30 * 24 * 3600)]
    [InlineData("12h", 12 * 3600)]
    [InlineData("15m", 15 * 60)]
    [InlineData("90s", 90)]
    [InlineData("0", 0)]
    public void Durations(string text, int seconds)
    {
        RequireLinux();
        Assert.Equal(TimeSpan.FromSeconds(seconds), KeytabArguments.ParseDuration(text));
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("-1d")]
    [InlineData("5w")]
    public void InvalidDurations_AreRefused(string text)
    {
        RequireLinux();
        Assert.Throws<ArgumentException>(() => KeytabArguments.ParseDuration(text));
    }

    [Fact]
    public void CommandLine_DefaultsAndPerCommandOptions()
    {
        RequireLinux();
        var init = KeytabArguments.Parse(["init", "--principal", "ADMNG-WD$@CONTOSO.COM", "--fqdn", "admng-wd.contoso.com", "--password-stdin"], null);
        Assert.Equal("ADMNG-WD$@CONTOSO.COM", init.Principal);
        Assert.True(init.PasswordStdin);
        Assert.Equal(KeytabArguments.DefaultKeytab, init.Keytab);
        Assert.Equal(KerberosConfig.DefaultPath, init.Krb5Config);
        Assert.Equal(KeytabArguments.DefaultStateDirectory, init.StateDirectory);
        Assert.Equal(TimeSpan.FromMinutes(15), init.ReplicationTimeout);

        var rotate = KeytabArguments.Parse(["rotate", "--if-older-than", "30d"], "/etc/admng-watchdog/krb5.conf");
        Assert.Equal(TimeSpan.FromDays(30), rotate.IfOlderThan);
        Assert.Equal("/etc/admng-watchdog/krb5.conf", rotate.Krb5Config);

        Assert.Throws<ArgumentException>(() => KeytabArguments.Parse(["rotate", "--fqdn", "x"], null));
        Assert.Throws<ArgumentException>(() => KeytabArguments.Parse(["init", "--principal"], null));
        Assert.Throws<ArgumentException>(() => KeytabArguments.Parse(["export"], null));
        Assert.Throws<ArgumentException>(() => KeytabArguments.Parse([], null));
    }

    [Fact]
    public void AccountPrincipal_SaltAndKeytabOrder()
    {
        RequireLinux();
        var account = AccountPrincipal.Parse("ADMNG-WD$@CONTOSO.COM");

        Assert.Equal("CONTOSO.COMhostadmng-wd.contoso.com", account.Salt);
        Assert.Equal("admng-wd.contoso.com", account.DefaultFqdn);
        Assert.Equal(
            ["ADMNG-WD$@CONTOSO.COM", "HTTP/admng-wd.contoso.com@CONTOSO.COM", "host/admng-wd.contoso.com@CONTOSO.COM"],
            account.PrincipalsFor("ADMNG-WD.contoso.com."));
        Assert.Throws<ArgumentException>(() => AccountPrincipal.Parse("ADMNG-WD@CONTOSO.COM"));
        Assert.Throws<ArgumentException>(() => AccountPrincipal.Parse("HTTP/admng-wd$@CONTOSO.COM"));
        Assert.Throws<ArgumentException>(() => AccountPrincipal.Parse("ADMNG-WD$"));
        Assert.Equal(
            "A Tier 0 administrator sets a new one-time password (Set-ADAccountPassword -Identity 'ADMNG-WD$' -Reset -NewPassword $otp, "
            + "$otp generated as in the instructions), then run: sudo admng-watchdog-keytab init",
            KeytabTool.RemedyText(account));
    }

    [Fact]
    public void GeneratedPassword_Is120PrintableAsciiCharacters_AndIsZeroedOnDispose()
    {
        RequireLinux();
        var password = PasswordGenerator.Generate();
        var other = PasswordGenerator.Generate();
        try
        {
            Assert.Equal(120, password.Length);
            Assert.All(password.Span.ToArray(), b => Assert.InRange(b, (byte)0x21, (byte)0x7E));
            Assert.True(PasswordGenerator.IsComplex(password.Span));
            Assert.False(password.Span.SequenceEqual(other.Span));
            Assert.Equal("SecretBuffer(redacted)", password.ToString());
        }
        finally
        {
            password.Dispose();
            other.Dispose();
        }

        Assert.Throws<ObjectDisposedException>(() => password.Span.Length);
        Assert.False(PasswordGenerator.IsComplex("abcdefgh1!"u8));
    }

    [Fact]
    public void ErrorNames()
    {
        RequireLinux();
        Assert.Equal("KRB5_KDC_UNREACH", Krb5ErrorNames.Of(Krb5ErrorNames.KdcUnreachable));
        Assert.Equal("KRB5KDC_ERR_PREAUTH_FAILED", Krb5ErrorNames.Of(Krb5ErrorNames.KdcErrPreauthFailed));
        Assert.Equal("KRB5KDC_ERR_CLIENT_REVOKED", Krb5ErrorNames.Of(Krb5ErrorNames.KdcErrClientRevoked));
        Assert.Equal("errno:111", Krb5ErrorNames.Of(111));
        Assert.Equal("krb5:-1765328000", Krb5ErrorNames.Of(-1765328000));
        Assert.Equal("KRB5_KPASSWD_SOFTERROR", Krb5ErrorNames.OfKpasswdResult(Krb5ErrorNames.KpasswdSoftError));
    }

    [Fact]
    public void StatusJson_WrittenByTheTool_IsReadByTheService()
    {
        RequireLinux();
        var directory = System.IO.Directory.CreateTempSubdirectory("admng-keytab-status-").FullName;
        try
        {
            var now = new DateTimeOffset(2026, 10, 20, 4, 20, 0, TimeSpan.Zero);
            new KeytabStatus
            {
                Principal = "ADMNG-WD$@CONTOSO.COM",
                Kvno = 5,
                LastChange = now,
                LastAttempt = now,
                Result = KeytabResults.ReplicationPending,
                ErrorCode = "KRB5KDC_ERR_PREAUTH_FAILED",
                NextDue = now.AddDays(30),
            }.Write(directory);

            var path = Path.Combine(directory, "status.json");
            var read = KeytabToolStatus.Read(path)!;
            Assert.Equal("ADMNG-WD$@CONTOSO.COM", read.Principal);
            Assert.Equal(5, read.Kvno);
            Assert.Equal(now, read.LastChange);
            Assert.Equal(KeytabStatusResults.ReplicationPending, read.Result);
            Assert.Equal("KRB5KDC_ERR_PREAUTH_FAILED", read.ErrorCode);
            Assert.Equal(now.AddDays(30), read.NextDue);
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead, File.GetUnixFileMode(path));
            Assert.Contains(KeytabResults.OtpActive, KeytabStatusResults.All);
        }
        finally
        {
            System.IO.Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Commands_WithoutAKdc_UsageAndStatusExitCodes()
    {
        RequireLinux();
        var state = System.IO.Directory.CreateTempSubdirectory("admng-keytab-cli-").FullName;
        try
        {
            Assert.Equal(KeytabTool.ExitOk, Run(["help"], out var help));
            Assert.Contains("rotate", help, StringComparison.Ordinal);
            Assert.Equal(KeytabTool.ExitUsage, Run(["export"], out _));
            Assert.Equal(KeytabTool.ExitUsage, Run(["init", "--state-dir", state], out var init));
            Assert.Contains("terminal", init, StringComparison.Ordinal);
            Assert.Equal(KeytabTool.ExitFailure, Run(["status", "--state-dir", state], out _));
            Assert.Equal(KeytabTool.ExitFailure, Run(["rotate", "--keytab", Path.Combine(state, "missing.keytab"), "--state-dir", state], out var rotate));
            Assert.Contains("init", rotate, StringComparison.Ordinal);
        }
        finally
        {
            System.IO.Directory.Delete(state, recursive: true);
        }
    }

    private static int Run(string[] args, out string output)
    {
        using var writer = new StringWriter();
        var exit = KeytabTool.Run(args, new KeytabToolHost { Out = writer, Error = writer, Input = new MemoryStream(Encoding.UTF8.GetBytes("x\n")) }, null);
        output = writer.ToString();
        return exit;
    }

    private static void RequireLinux() => Assert.SkipUnless(OperatingSystem.IsLinux(), "admng-watchdog-keytab runs on Linux only.");
}
