using System.Globalization;
using System.Runtime.CompilerServices;
using AdMng.Core.Directory;
using AdMng.Core.Model;
using AdMng.Core.Schema;
using AdMng.Directory.IntegrationTests.Infrastructure;

namespace AdMng.Directory.IntegrationTests.Unit;

public sealed class SchemaLoaderTests
{
    private const string SchemaNc = "CN=Schema,CN=Configuration,DC=contoso,DC=com";
    private const string ConfigNc = "CN=Configuration,DC=contoso,DC=com";
    private static readonly Guid DepartmentGuid = new("bf96794f-0de6-11d0-a285-00aa003049e2");
    private static readonly Guid PersonalInformation = new("77b5b886-944a-11d1-aebd-0000f80367c1");

    [Fact]
    public void AttributeSchema_IsMapped()
    {
        var definition = SchemaLoader.ParseAttribute(Entry(
            "CN=Department," + SchemaNc,
            ("lDAPDisplayName", ["department"]),
            ("cn", ["Department"]),
            ("attributeID", ["1.2.840.113556.1.2.141"]),
            ("attributeSyntax", ["2.5.5.12"]),
            ("oMSyntax", ["64"]),
            ("isSingleValued", ["TRUE"]),
            ("rangeLower", ["1"]),
            ("rangeUpper", ["64"]),
            ("systemOnly", ["FALSE"]),
            ("systemFlags", ["16"]),
            ("searchFlags", ["17"]),
            ("isMemberOfPartialAttributeSet", ["TRUE"]),
            ("schemaIDGUID", [DepartmentGuid.ToByteArray()]),
            ("attributeSecurityGUID", [PersonalInformation.ToByteArray()])));

        Assert.NotNull(definition);
        Assert.Equal("department", definition.LdapName);
        Assert.Equal("Department", definition.Cn);
        Assert.Equal("1.2.840.113556.1.2.141", definition.AttributeId);
        Assert.Equal(AttributeSyntax.UnicodeString, definition.Syntax);
        Assert.True(definition.IsSingleValued);
        Assert.Equal(1, definition.RangeLower);
        Assert.Equal(64, definition.RangeUpper);
        Assert.False(definition.IsSystemOnly);
        Assert.False(definition.IsConstructed);
        Assert.True(definition.IsIndexed);
        Assert.True(definition.IsInGlobalCatalog);
        Assert.Null(definition.LinkId);
        Assert.Equal(DepartmentGuid, definition.SchemaIdGuid);
        Assert.Equal(PersonalInformation, definition.PropertySetGuid);
        Assert.False(definition.IsConfidential);
    }

    [Fact]
    public void ConfidentialAttributes_AreRecognised()
    {
        // Legacy LAPS: searchFlags 904 = 0x388 (confidential 0x80, never audited 0x100, RODC filtered 0x200, copy 0x8).
        var laps = SchemaLoader.ParseAttribute(Entry(
            "CN=ms-Mcs-AdmPwd," + SchemaNc,
            ("lDAPDisplayName", ["ms-Mcs-AdmPwd"]),
            ("attributeSyntax", ["2.5.5.5"]),
            ("oMSyntax", ["19"]),
            ("searchFlags", ["904"])));
        var custom = SchemaLoader.ParseAttribute(Entry(
            "CN=contoso-Pin," + SchemaNc,
            ("lDAPDisplayName", ["contoso-Pin"]),
            ("attributeSyntax", ["2.5.5.12"]),
            ("oMSyntax", ["64"]),
            ("searchFlags", ["128"])));

        Assert.True(laps!.IsConfidential);
        Assert.False(laps.IsIndexed);
        Assert.True(custom!.IsConfidential);
        Assert.True(SecretAttributes.IsSecret("contoso-Pin", new SchemaCatalog([custom], [])));
        Assert.False(SecretAttributes.IsSecret("contoso-Pin"));
    }

    [Fact]
    public void ConstructedAndLinkedAttributes_AreRecognised()
    {
        var constructed = SchemaLoader.ParseAttribute(Entry(
            "CN=Allowed-Attributes-Effective," + SchemaNc,
            ("lDAPDisplayName", ["allowedAttributesEffective"]),
            ("attributeSyntax", ["2.5.5.2"]),
            ("oMSyntax", ["6"]),
            ("systemOnly", ["TRUE"]),
            ("systemFlags", ["134217748"])));
        var memberOf = SchemaLoader.ParseAttribute(Entry(
            "CN=Is-Member-Of-DL," + SchemaNc,
            ("lDAPDisplayName", ["memberOf"]),
            ("attributeSyntax", ["2.5.5.1"]),
            ("oMSyntax", ["127"]),
            ("linkID", ["3"])));

        Assert.True(constructed!.IsConstructed);
        Assert.True(constructed.IsReadOnly);
        Assert.Equal(AttributeSyntax.ObjectIdentifier, constructed.Syntax);
        Assert.Equal(Guid.Empty, constructed.SchemaIdGuid);
        Assert.Equal(3, memberOf!.LinkId);
        Assert.True(memberOf.IsBackLink);
    }

    [Fact]
    public void EntriesWithoutLdapDisplayName_AreSkipped()
    {
        Assert.Null(SchemaLoader.ParseAttribute(Entry("CN=X," + SchemaNc, ("cn", ["X"]))));
        Assert.Null(SchemaLoader.ParseClass(Entry("CN=Y," + SchemaNc, ("cn", ["Y"]))));
    }

    [Fact]
    public void ClassSchema_MergesSystemAndUserLists()
    {
        var user = SchemaLoader.ParseClass(Entry(
            "CN=User," + SchemaNc,
            ("lDAPDisplayName", ["user"]),
            ("governsID", ["1.2.840.113556.1.5.9"]),
            ("subClassOf", ["organizationalPerson"]),
            ("objectClassCategory", ["1"]),
            ("auxiliaryClass", ["shadowAccount"]),
            ("systemAuxiliaryClass", ["securityPrincipal", "mailRecipient"]),
            ("systemMustContain", ["objectSid"]),
            ("mayContain", ["msDS-Custom"]),
            ("systemMayContain", ["userAccountControl", "pwdLastSet"]),
            ("systemPossSuperiors", ["organizationalUnit", "container", "builtinDomain"]),
            ("possSuperiors", ["domainDNS"]),
            ("defaultObjectCategory", ["CN=Person," + SchemaNc])));

        Assert.NotNull(user);
        Assert.Equal("organizationalPerson", user.SubClassOf);
        Assert.Equal(ClassCategory.Structural, user.Category);
        Assert.Equal(["shadowAccount", "securityPrincipal", "mailRecipient"], user.AuxiliaryClasses);
        Assert.Equal(["objectSid"], user.MustContain);
        Assert.Equal(["msDS-Custom", "userAccountControl", "pwdLastSet"], user.MayContain);
        Assert.Equal(4, user.PossibleSuperiors.Count);
        Assert.Equal("CN=Person," + SchemaNc, user.DefaultObjectCategory);
    }

    [Fact]
    public void UnknownClassCategory_FallsBackToStructural()
    {
        var odd = SchemaLoader.ParseClass(Entry("CN=Odd," + SchemaNc, ("lDAPDisplayName", ["odd"]), ("objectClassCategory", ["9"])));
        var auxiliary = SchemaLoader.ParseClass(Entry("CN=Aux," + SchemaNc, ("lDAPDisplayName", ["aux"]), ("objectClassCategory", ["3"])));

        Assert.Equal(ClassCategory.Structural, odd!.Category);
        Assert.Equal(ClassCategory.Auxiliary, auxiliary!.Category);
    }

    [Theory]
    [InlineData("department,Department", "department", "Department")]
    [InlineData(" title , Job Title ", "title", "Job Title")]
    [InlineData("info,Notes, remarks", "info", "Notes, remarks")]
    public void DisplayNameValues_AreParsed(string value, string attribute, string displayName)
    {
        Assert.True(SchemaLoader.TryParseDisplayName(value, out var a, out var d));
        Assert.Equal(attribute, a);
        Assert.Equal(displayName, d);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("department")]
    [InlineData(",Department")]
    [InlineData("department, ")]
    public void InvalidDisplayNameValues_AreSkipped(string? value)
    {
        Assert.False(SchemaLoader.TryParseDisplayName(value, out _, out _));
    }

    [Fact]
    public void DisplayNames_PreferClassSpecificSpecifiers()
    {
        var merged = SchemaLoader.MergeDisplayNames(
        [
            ("default-Display", ["department,Department (default)", "description,Description"]),
            ("contact-Display", ["department,Department (contact)"]),
            ("user-Display", ["department,Department", "garbage"]),
            ("unknown-Display", ["description,Ignored", "wWWHomePage,Web Page"]),
        ]);

        Assert.Equal("Department", merged["department"]);
        Assert.Equal("Description", merged["description"]);
        Assert.Equal("Web Page", merged["wwwhomepage"]);
    }

    [Fact]
    public async Task LoadAsync_BuildsTheCatalog()
    {
        var requests = new List<DirectorySearch>();
        var catalog = await SchemaLoader.LoadAsync(
            (search, ct) => Respond(search, requests, polish: true, ct),
            SchemaNc,
            ConfigNc,
            new CapturingLoggerFactory().CreateLogger("test"),
            TimeProvider.System,
            TestContext.Current.CancellationToken);

        Assert.Equal(AttributeSyntax.UnicodeString, catalog.FindAttribute("department")?.Syntax);
        Assert.Equal("person", catalog.FindClass("user")?.SubClassOf);
        Assert.Equal("Dział", catalog.GetDisplayName("department", CultureInfo.GetCultureInfo("pl-PL")));
        Assert.Equal("Department", catalog.GetDisplayName("department", CultureInfo.GetCultureInfo("en-US")));
        Assert.Equal("Department", catalog.GetDisplayName("department", CultureInfo.GetCultureInfo("de-DE")));

        var attributeSearch = Assert.Single(requests, r => r.Filter == "(objectClass=attributeSchema)");
        Assert.Equal(SchemaNc, attributeSearch.BaseDn);
        Assert.Equal(SearchScope.OneLevel, attributeSearch.Scope);
        Assert.Contains("schemaIDGUID", attributeSearch.Attributes);
        Assert.Contains(requests, r => r.BaseDn == "CN=409,CN=DisplaySpecifiers," + ConfigNc);
    }

    [Fact]
    public async Task LoadAsync_ToleratesAMissingLocale()
    {
        var catalog = await SchemaLoader.LoadAsync(
            (search, ct) => Respond(search, [], polish: false, ct),
            SchemaNc,
            ConfigNc,
            new CapturingLoggerFactory().CreateLogger("test"),
            TimeProvider.System,
            TestContext.Current.CancellationToken);

        Assert.Equal("Department", catalog.GetDisplayName("department", CultureInfo.GetCultureInfo("pl-PL")));
    }

    [Fact]
    public async Task LoadAsync_WithoutAnyDisplaySpecifiers_StillLoads()
    {
        var catalog = await SchemaLoader.LoadAsync(
            (search, ct) => search.BaseDn.Contains("DisplaySpecifiers", StringComparison.Ordinal)
                ? Throw(new DirectoryException(DirectoryErrorKind.AccessDenied, "denied"))
                : Respond(search, [], polish: true, ct),
            SchemaNc,
            ConfigNc,
            new CapturingLoggerFactory().CreateLogger("test"),
            TimeProvider.System,
            TestContext.Current.CancellationToken);

        Assert.NotNull(catalog.FindAttribute("department"));
        Assert.Equal("department", catalog.GetDisplayName("department", CultureInfo.GetCultureInfo("en-US")));
    }

    [Fact]
    public async Task LoadAsync_PropagatesSchemaFailures()
    {
        var error = await Assert.ThrowsAsync<DirectoryException>(() => SchemaLoader.LoadAsync(
            (search, _) => Throw(new DirectoryException(DirectoryErrorKind.ServerUnavailable, "down")),
            SchemaNc,
            ConfigNc,
            new CapturingLoggerFactory().CreateLogger("test"),
            TimeProvider.System,
            TestContext.Current.CancellationToken));

        Assert.Equal(DirectoryErrorKind.ServerUnavailable, error.Kind);
    }

    private static async IAsyncEnumerable<DirectoryObject> Respond(
        DirectorySearch search,
        List<DirectorySearch> requests,
        bool polish,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        lock (requests)
        {
            requests.Add(search);
        }

        await Task.Yield();
        cancellationToken.ThrowIfCancellationRequested();
        if (search.Filter == "(objectClass=attributeSchema)")
        {
            yield return Entry(
                "CN=Department," + SchemaNc,
                ("lDAPDisplayName", ["department"]),
                ("attributeSyntax", ["2.5.5.12"]),
                ("oMSyntax", ["64"]),
                ("schemaIDGUID", [DepartmentGuid.ToByteArray()]));
        }
        else if (search.Filter == "(objectClass=classSchema)")
        {
            yield return Entry("CN=User," + SchemaNc, ("lDAPDisplayName", ["user"]), ("subClassOf", ["person"]));
        }
        else if (search.BaseDn.StartsWith("CN=409,", StringComparison.Ordinal))
        {
            yield return Entry(
                "CN=user-Display," + search.BaseDn,
                ("cn", ["user-Display"]),
                ("attributeDisplayNames", ["department,Department"]));
        }
        else if (polish && search.BaseDn.StartsWith("CN=415,", StringComparison.Ordinal))
        {
            yield return Entry(
                "CN=user-Display," + search.BaseDn,
                ("cn", ["user-Display"]),
                ("attributeDisplayNames", ["department,Dział"]));
        }
        else
        {
            throw new DirectoryException(DirectoryErrorKind.NoSuchObject, "no such object");
        }
    }

    private static async IAsyncEnumerable<DirectoryObject> Throw(Exception error)
    {
        await Task.Yield();
        if (error is not null)
        {
            throw error;
        }

        yield break;
    }

    private static DirectoryObject Entry(string dn, params (string Name, object[] Values)[] attributes) =>
        new(dn, attributes.Select(a => new AttributeValues(a.Name, a.Values)));
}
