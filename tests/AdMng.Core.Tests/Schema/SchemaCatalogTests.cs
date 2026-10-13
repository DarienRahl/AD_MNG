using System.Globalization;
using AdMng.Core.Schema;

namespace AdMng.Core.Tests.Schema;

public class SchemaCatalogTests
{
    private static SchemaCatalog Create() => new(
        [
            new AttributeDefinition { LdapName = "cn", Syntax = AttributeSyntax.UnicodeString, IsSingleValued = true, RangeUpper = 64 },
            new AttributeDefinition { LdapName = "department", Syntax = AttributeSyntax.UnicodeString, IsSingleValued = true, RangeUpper = 64 },
            new AttributeDefinition { LdapName = "memberOf", Syntax = AttributeSyntax.DistinguishedName, LinkId = 3 },
            new AttributeDefinition { LdapName = "thumbnailPhoto", Syntax = AttributeSyntax.OctetString, IsSingleValued = true },
            new AttributeDefinition { LdapName = "allowedAttributesEffective", Syntax = AttributeSyntax.ObjectIdentifier, IsConstructed = true },
        ],
        [
            new ClassDefinition { LdapName = "top", MustContain = ["objectClass"], MayContain = ["memberOf"] },
            new ClassDefinition { LdapName = "person", SubClassOf = "top", MustContain = ["cn"], MayContain = ["sn"] },
            new ClassDefinition { LdapName = "organizationalPerson", SubClassOf = "person", MayContain = ["department"] },
            new ClassDefinition { LdapName = "user", SubClassOf = "organizationalPerson", AuxiliaryClasses = ["securityPrincipal"] },
            new ClassDefinition { LdapName = "securityPrincipal", Category = ClassCategory.Auxiliary, MustContain = ["objectSid"] },
        ],
        new Dictionary<string, IReadOnlyDictionary<string, string>>
        {
            ["pl"] = new Dictionary<string, string> { ["department"] = "Dział" },
            ["en"] = new Dictionary<string, string> { ["department"] = "Department", ["cn"] = "Common Name" },
        });

    [Fact]
    public void AllowedAttributes_IncludeInheritedAndAuxiliary()
    {
        var allowed = Create().GetAllowedAttributes(["user"]);

        Assert.Contains("department", allowed);
        Assert.Contains("cn", allowed);
        Assert.Contains("objectSid", allowed);
        Assert.Contains("memberOf", allowed);
        Assert.Equal(["cn", "objectClass", "objectSid"], Create().GetMandatoryAttributes(["user"]).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void AttributeFlags_AreDerived()
    {
        var schema = Create();

        Assert.True(schema.FindAttribute("memberOf")!.IsBackLink);
        Assert.True(schema.FindAttribute("memberOf")!.IsReadOnly);
        Assert.True(schema.FindAttribute("allowedAttributesEffective")!.IsReadOnly);
        Assert.False(schema.FindAttribute("department")!.IsReadOnly);
        Assert.True(schema.IsBinary("thumbnailPhoto"));
        Assert.True(schema.IsBinary("objectGUID"));
        Assert.False(schema.IsBinary("department"));
        Assert.NotNull(schema.FindAttribute("member;range=0-1499".Replace("member", "memberOf", StringComparison.Ordinal)));
    }

    [Fact]
    public void DisplayName_UsesCultureThenEnglishThenLdapName()
    {
        var schema = Create();

        Assert.Equal("Dział", schema.GetDisplayName("department", CultureInfo.GetCultureInfo("pl-PL")));
        Assert.Equal("Common Name", schema.GetDisplayName("cn", CultureInfo.GetCultureInfo("pl-PL")));
        Assert.Equal("sn", schema.GetDisplayName("sn", CultureInfo.GetCultureInfo("en-US")));
    }

    [Theory]
    [InlineData("2.5.5.12", 64, AttributeSyntax.UnicodeString)]
    [InlineData("2.5.5.16", 65, AttributeSyntax.LargeInteger)]
    [InlineData("2.5.5.9", 10, AttributeSyntax.Enumeration)]
    [InlineData("2.5.5.5", 22, AttributeSyntax.IA5String)]
    [InlineData("2.5.5.10", 4, AttributeSyntax.OctetString)]
    [InlineData("2.5.5.7", 127, AttributeSyntax.DnWithBinary)]
    [InlineData("9.9", 0, AttributeSyntax.Unknown)]
    public void MapSyntax_UsesAttributeAndOmSyntax(string syntax, int omSyntax, AttributeSyntax expected)
    {
        Assert.Equal(expected, AttributeDefinition.MapSyntax(syntax, omSyntax));
    }
}
