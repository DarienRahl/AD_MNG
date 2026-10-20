using AdMng.Core.Directory;
using AdMng.Watchdog.Service.Hosting;
using AdMng.Watchdog.Service.Tests.Infrastructure;

namespace AdMng.Watchdog.Service.Tests.Hosting;

/// <summary>The libldap configuration start-up check (stage B design 5.1, 5.2, 11 item 5 – exit 2).</summary>
public sealed class LdapConfigCheckTests : IDisposable
{
    private readonly string _directory = System.IO.Directory.CreateTempSubdirectory("admng-ldapconf-").FullName;

    public void Dispose() => System.IO.Directory.Delete(_directory, recursive: true);

    [Fact]
    public void ShippedLdapConf_EnforcesSealing_AndItsLdapsVariantPassesForTls()
    {
        var shipped = Write("ldap.conf", RepositoryFiles.ReadAllText("packaging/admng-watchdog/etc/ldap.conf"));
        Assert.Null(LdapConfigCheck.Check(TransportSecurity.SignAndSeal, Inputs(shipped)));
        Assert.NotNull(LdapConfigCheck.Check(TransportSecurity.Tls, Inputs(shipped)));

        var ldaps = Write("ldaps.conf", "SASL_NOCANON on\nSASL_SECPROPS minssf=0,maxssf=0,noanonymous,noplain\nTLS_CACERT /etc/admng-watchdog/tls/dc-ca.pem\n");
        Assert.Null(LdapConfigCheck.Check(TransportSecurity.Tls, Inputs(ldaps)));
        Assert.NotNull(LdapConfigCheck.Check(TransportSecurity.SignAndSeal, Inputs(ldaps)));
    }

    [Theory]
    [InlineData("SASL_SECPROPS noanonymous,noplain\n")]
    [InlineData("SASL_SECPROPS minssf=1,noanonymous\n")]
    [InlineData("SASL_SECPROPS minssf=56\nSASL_SECPROPS minssf=0\n")]       // the last line wins in libldap
    [InlineData("#SASL_SECPROPS minssf=56\n")]
    public void WeakOrMissingMinssf_IsRefused(string content) =>
        Assert.Contains("minssf=56", LdapConfigCheck.Check(TransportSecurity.SignAndSeal, Inputs(Write("weak.conf", content))), StringComparison.Ordinal);

    [Fact]
    public void EnvironmentOverridesAndPerUserFiles_AreRefused()
    {
        var good = Write("ldap.conf", "SASL_SECPROPS minssf=56\n");

        Assert.NotNull(LdapConfigCheck.Check(TransportSecurity.SignAndSeal, Inputs(good) with { SaslSecPropsVariable = "minssf=0" }));
        Assert.Null(LdapConfigCheck.Check(TransportSecurity.SignAndSeal, Inputs(good) with { SaslSecPropsVariable = "minssf=128" }));
        Assert.NotNull(LdapConfigCheck.Check(TransportSecurity.SignAndSeal, Inputs(good) with { NoInit = "1" }));
        Assert.NotNull(LdapConfigCheck.Check(TransportSecurity.SignAndSeal, Inputs(good) with { LdapRc = "myrc" }));
        Assert.NotNull(LdapConfigCheck.Check(TransportSecurity.SignAndSeal, Inputs(null)));
        Assert.NotNull(LdapConfigCheck.Check(TransportSecurity.SignAndSeal, Inputs(Path.Combine(_directory, "missing.conf"))));
        Assert.NotNull(LdapConfigCheck.Check(TransportSecurity.None, Inputs(good)));

        Write(".ldaprc", "SASL_SECPROPS minssf=0\n");
        Assert.Contains(".ldaprc", LdapConfigCheck.Check(TransportSecurity.SignAndSeal, Inputs(good) with { Home = _directory }), StringComparison.Ordinal);
    }

    private LdapConfigCheck.Inputs Inputs(string? ldapConf) => new() { LdapConf = ldapConf, Home = "/nonexistent-home", WorkingDirectory = "/nonexistent-cwd" };

    private string Write(string name, string content)
    {
        var path = Path.Combine(_directory, name);
        File.WriteAllText(path, content);
        return path;
    }
}
