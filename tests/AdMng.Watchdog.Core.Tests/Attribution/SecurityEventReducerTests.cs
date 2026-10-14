using AdMng.Core.Ldap;
using AdMng.Core.Schema;
using AdMng.Testing;
using AdMng.Watchdog.Contracts.Configuration;
using AdMng.Watchdog.Core.Attribution;
using AdMng.Watchdog.Core.Model;
using AdMng.Watchdog.Core.Tests.Rules.Fakes;
using AdMng.Watchdog.Testing.Fixtures.SecurityEvents;

namespace AdMng.Watchdog.Core.Tests.Attribution;

public class SecurityEventReducerTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 14, 6, 0, 0, TimeSpan.Zero);
    private static readonly ISchemaCatalog Schema = FakeSchema.Create("CN=Schema,CN=Configuration,DC=contoso,DC=test");
    private static readonly IEnumerable<(string, string?)> Admin = SecurityEventXml.Subject("S-1-5-21-1-2-3-1104", "adm.jkowalska", "CONTOSO", "0x5a3f1");
    private readonly SecurityEventParser _parser = new();

    private static SecurityEventReduction Reduction(ISchemaCatalog? schema = null, params string[] excluded) => new()
    {
        Schema = schema,
        WatchedAttributes = new AdMng.Watchdog.Core.Detection.WatchedAttributeCatalog(new FakeConfigProvider(new WatchdogConfiguration
        {
            Directory = new DirectorySettings { AdditionalWatchedAttributes = ["extensionAttribute1"], IgnoredAttributes = ["scriptPath"] },
        })),
        ExcludedSubtrees = [.. excluded.Select(DistinguishedName.Parse)],
    };

    private SecurityEvent Parse(string xml) => _parser.Parse(xml, "dc01.contoso.test")!.Event;

    private static string Modified(string attribute, string? value, string dn = "CN=Łukasz Wiśniewski,OU=Kraków,DC=contoso,DC=test", string objectClass = "user") =>
        SecurityEventXml.DirectoryModified(10, At, "dc01.contoso.test", Guid.NewGuid(), dn, objectClass, attribute, value, added: true, Admin);

    [Fact]
    public void KeepsOnlyTheRetainedFields()
    {
        var reduced = SecurityEventReducer.Reduce(Parse(SecurityEventFixtures.DirectoryObjectModified5136), Reduction(Schema))!;

        Assert.Null(reduced.Get("DSName"));
        Assert.Null(reduced.Get("DSType"));
        Assert.Null(reduced.Get("AttributeSyntaxOID"));
        Assert.Equal("userAccountControl", reduced.Get("AttributeLDAPDisplayName"));
        Assert.Equal("512", reduced.Get("AttributeValue"));
        Assert.Equal("0x32004", reduced.SubjectLogonId);
        Assert.NotNull(reduced.ObjectGuid);
    }

    [Theory]
    [InlineData("userAccountControl", "512", "512")]
    [InlineData("extensionAttribute1", "Dział Księgowości", "Dział Księgowości")]
    [InlineData("department", "Kadry", "Kadry")]              // in the effective user set of 9.2
    [InlineData("carLicense", "KR 12345", null)]               // in no watched set
    [InlineData("scriptPath", "logon.bat", null)]
    [InlineData("msDS-KeyCredentialLink", "B:828:00020000…", null)]
    [InlineData("unicodePwd", "\"Hasło-2026\"", SecretAttributes.Mask)]
    [InlineData("ms-Mcs-AdmPwd", "Zażółć-1", SecretAttributes.Mask)]
    public void AttributeValues_OnlyForWatchedNonSecretAttributes(string attribute, string value, string? expected)
    {
        var reduced = SecurityEventReducer.Reduce(Parse(Modified(attribute, value)), Reduction(Schema))!;

        Assert.Equal(expected, reduced.Get("AttributeValue"));
        Assert.Equal(attribute, reduced.Get("AttributeLDAPDisplayName"));
        Assert.Equal("ValueAdded", reduced.Get("OperationType"));
    }

    [Fact]
    public void WithoutASchema_NoValueIsKept() =>
        Assert.Null(SecurityEventReducer.Reduce(Parse(Modified("userAccountControl", "512")), Reduction())!.Get("AttributeValue"));

    [Fact]
    public void SamFields_FollowTheSameRule_UacValuesAlwaysStay()
    {
        var xml = SecurityEventXml.Account(4738, 11, At, "dc01.contoso.test", "S-1-5-21-1-2-3-1500", "lwisniewski", Admin,
            ("ScriptPath", "logon.bat"), ("DisplayName", "Łukasz Wiśniewski"), ("OldUacValue", "0x10"), ("NewUacValue", "0x11"), ("UserParameters", "x"));

        var reduced = SecurityEventReducer.Reduce(Parse(xml), Reduction(Schema))!;

        Assert.Null(reduced.Get("ScriptPath"));           // ignored attribute
        Assert.Null(reduced.Get("UserParameters"));       // not retained
        Assert.Equal("Łukasz Wiśniewski", reduced.Get("DisplayName"));
        Assert.Equal("0x10", reduced.Get("OldUacValue"));
        Assert.Equal("0x11", reduced.Get("NewUacValue"));
    }

    [Fact]
    public void DirectoryChangesUnderExcludedSubtrees_AreDropped()
    {
        var reduction = Reduction(Schema, "OU=Archiwum,DC=contoso,DC=test");

        Assert.Null(SecurityEventReducer.Reduce(Parse(Modified("description", "x", "CN=Stare konto,OU=Archiwum,DC=contoso,DC=test")), reduction));
        Assert.NotNull(SecurityEventReducer.Reduce(Parse(Modified("description", "x")), reduction));
    }

    [Fact]
    public void MovesOutOfAnExcludedSubtree_AreKept()
    {
        var reduction = Reduction(Schema, "OU=Archiwum,DC=contoso,DC=test");
        string Move(string from, string to) => SecurityEventXml.Create(5139, 12, At, "dc01.contoso.test",
            [("OldObjectDN", from), ("NewObjectDN", to), ("ObjectGUID", "{4FE80A66-5F93-4F73-B215-68678058E613}"), ("ObjectClass", "user"), .. Admin]);

        Assert.NotNull(SecurityEventReducer.Reduce(Parse(Move("CN=a,OU=Archiwum,DC=contoso,DC=test", "CN=a,OU=Kraków,DC=contoso,DC=test")), reduction));
        Assert.Null(SecurityEventReducer.Reduce(Parse(Move("CN=a,OU=Archiwum,DC=contoso,DC=test", "CN=b,OU=Archiwum,DC=contoso,DC=test")), reduction));
    }

    [Theory]
    [InlineData("msFVE-RecoveryInformation")]
    [InlineData("dnsNode")]
    [InlineData("printQueue")]
    public void DirectoryChangesOfUnwatchedClasses_AreDropped(string objectClass) =>
        Assert.Null(SecurityEventReducer.Reduce(Parse(Modified("description", "x", objectClass: objectClass)), Reduction(Schema)));

    [Theory]
    [InlineData("nTDSDSA")]
    [InlineData("pKICertificateTemplate")]
    [InlineData("groupPolicyContainer")]
    public void ConfigurationPkiAndPolicyClasses_AreKept(string objectClass) =>
        Assert.NotNull(SecurityEventReducer.Reduce(Parse(Modified("flags", "131680", objectClass: objectClass)), Reduction(Schema)));
}
