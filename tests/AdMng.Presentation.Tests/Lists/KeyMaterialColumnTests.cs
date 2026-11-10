using System.Globalization;
using AdMng.Core.ActiveDirectory;
using AdMng.Core.Formatting;
using AdMng.Core.Schema;
using AdMng.Presentation.Lists;
using AdMng.Presentation.Localization;
using AdMng.Presentation.Session;
using AdMng.Presentation.Tests.TestDoubles;

namespace AdMng.Presentation.Tests.Lists;

/// <summary>
/// Key material (P1 decision 7, <see cref="SecretAttributes.IsNeverShown"/>) is never offered as a list column, and a
/// column that lists it anyway (a saved view) shows, copies and exports only the mask.
/// </summary>
public sealed class KeyMaterialColumnTests
{
    private const string RoamingCredentials = "msPKIAccountCredentials";
    private const string DpapiMasterKeys = "msPKIDPAPIMasterKeys";

    [Fact]
    public void TheColumnChooser_OffersNoKeyMaterial_ButTheOtherAttributes()
    {
        var schema = new SchemaCatalog(
            [
                new AttributeDefinition { LdapName = "cn", Syntax = AttributeSyntax.UnicodeString, IsSingleValued = true },
                new AttributeDefinition { LdapName = "department", Syntax = AttributeSyntax.UnicodeString, IsSingleValued = true },
                new AttributeDefinition { LdapName = RoamingCredentials, Syntax = AttributeSyntax.DnWithBinary },
                new AttributeDefinition { LdapName = DpapiMasterKeys, Syntax = AttributeSyntax.DnWithBinary },
            ],
            [
                new ClassDefinition { LdapName = "top", MustContain = ["objectClass"], MayContain = ["cn"] },
                new ClassDefinition { LdapName = "user", SubClassOf = "top", MayContain = ["department", RoamingCredentials, DpapiMasterKeys] },
            ],
            new Dictionary<string, IReadOnlyDictionary<string, string>>());
        var localizer = new ResxLocalizer(CultureInfo.GetCultureInfo("en-US"));

        var chooser = new ColumnChooserViewModel(localizer, schema, ["name"], ColumnSets.Container);

        var offered = chooser.Available.Select(c => c.Attribute).ToList();
        Assert.Contains(Attr.Department, offered, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain(RoamingCredentials, offered, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain(DpapiMasterKeys, offered, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void ACellOfKeyMaterial_ShowsTheMask_AndSoDoesItsExportFormatting()
    {
        var localizer = new ResxLocalizer(CultureInfo.GetCultureInfo("pl-PL"));
        var context = new RowContext(localizer, new AttributeFormatter(TestSchema.Create()), DomainPolicy.Unknown, DateTimeOffset.UnixEpoch);
        var user = TestDirectory.WithAttribute(
            TestDirectory.User(TestDirectory.Lukasz, "lzolc", department: "IT"),
            RoamingCredentials,
            "B:8:01020304:CN=Łukasz Żółć,OU=IT,OU=Firma,DC=contoso,DC=test");

        var row = new ObjectRowViewModel(user, context);

        Assert.Equal(SecretAttributes.Mask, row[RoamingCredentials]);
        Assert.Equal(SecretAttributes.Mask, row.FormatUncached(RoamingCredentials));
        Assert.Equal(string.Empty, row[DpapiMasterKeys]);
        Assert.Equal("IT", row[Attr.Department]);
    }
}
