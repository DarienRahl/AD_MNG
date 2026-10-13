using System.Text;
using AdMng.Core.ActiveDirectory;
using AdMng.Core.Directory;
using AdMng.Core.Model;
using AdMng.Core.Schema;

namespace AdMng.Directory.IntegrationTests.Unit;

public sealed class EntryReaderAndRootDseTests
{
    private static readonly SchemaCatalog Schema = new(
        [
            new AttributeDefinition { LdapName = "department", Syntax = AttributeSyntax.UnicodeString },
            new AttributeDefinition { LdapName = "member", Syntax = AttributeSyntax.DistinguishedName },
            new AttributeDefinition { LdapName = "msDS-Custom-Blob", Syntax = AttributeSyntax.OctetString },
        ],
        []);

    [Fact]
    public void TextAttributes_AreDecodedAsUtf8()
    {
        var values = EntryReader.Decode("department", [Encoding.UTF8.GetBytes("Księgowość"), Encoding.UTF8.GetBytes("Zarząd")], Schema);

        Assert.Equal("department", values.Name);
        Assert.Equal(["Księgowość", "Zarząd"], values.Strings);
        Assert.All(values, v => Assert.IsType<string>(v));
    }

    [Theory]
    [InlineData("objectGUID")]
    [InlineData("objectSid")]
    [InlineData("msDS-Custom-Blob")]
    [InlineData("msDS-Custom-Blob;range=0-*")]
    public void BinaryAttributes_StayBytes(string name)
    {
        var raw = new byte[] { 0x01, 0xFF, 0x00, 0x80 };

        var values = EntryReader.Decode(name, [raw], Schema);

        Assert.Equal(raw, Assert.IsType<byte[]>(Assert.Single(values)));
    }

    [Fact]
    public void RangedTextAttributes_KeepTheirNameAndDecode()
    {
        var values = EntryReader.Decode("member;range=0-1", [Encoding.UTF8.GetBytes("CN=a,DC=x"), Encoding.UTF8.GetBytes("CN=b,DC=x")], Schema);
        var entry = new DirectoryObject("CN=g,DC=x", [values]);

        Assert.Equal("member;range=0-1", values.Name);
        Assert.Equal(["CN=a,DC=x", "CN=b,DC=x"], entry.GetStrings("member"));
    }

    [Fact]
    public void RootDse_IsParsed()
    {
        var entry = new DirectoryObject(string.Empty,
        [
            new AttributeValues(Attr.DefaultNamingContext, ["DC=contoso,DC=com"]),
            new AttributeValues(Attr.ConfigurationNamingContext, ["CN=Configuration,DC=contoso,DC=com"]),
            new AttributeValues(Attr.SchemaNamingContext, ["CN=Schema,CN=Configuration,DC=contoso,DC=com"]),
            new AttributeValues(Attr.RootDomainNamingContext, ["DC=contoso,DC=com"]),
            new AttributeValues(Attr.DnsHostNameRootDse, ["dc01.contoso.com"]),
            new AttributeValues(Attr.SupportedControl, [LdapControls.PagedResults, LdapControls.VirtualListView]),
            new AttributeValues(RootDse.SupportedExtension, [RootDse.WhoAmIOid]),
            new AttributeValues(Attr.DomainFunctionality, ["10"]),
            new AttributeValues(Attr.ForestFunctionality, ["7"]),
            new AttributeValues(Attr.IsGlobalCatalogReady, ["TRUE"]),
        ]);

        var rootDse = RootDse.Parse(entry);

        Assert.Equal("DC=contoso,DC=com", rootDse.DefaultNamingContext);
        Assert.Equal("dc01.contoso.com", rootDse.DnsHostName);
        Assert.Contains(LdapControls.VirtualListView, rootDse.SupportedControls);
        Assert.Contains(RootDse.WhoAmIOid, rootDse.SupportedExtensions);
        Assert.Equal(10, rootDse.DomainFunctionality);
        Assert.Equal(7, rootDse.ForestFunctionality);
        Assert.True(rootDse.IsGlobalCatalogReady);
        Assert.Contains(Attr.SchemaNamingContext, RootDse.Attributes);
    }

    [Fact]
    public void RootDse_WithoutNamingContexts_IsNotADomainController()
    {
        var entry = new DirectoryObject(string.Empty, [new AttributeValues(Attr.DnsHostNameRootDse, ["ldap.example.com"])]);

        var error = Assert.Throws<DirectoryException>(() => RootDse.Parse(entry));

        Assert.Equal(DirectoryErrorKind.UnwillingToPerform, error.Kind);
    }
}
