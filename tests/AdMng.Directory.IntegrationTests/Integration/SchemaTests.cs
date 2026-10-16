using System.Globalization;
using AdMng.Core.ActiveDirectory;
using AdMng.Core.Schema;
using AdMng.Directory.IntegrationTests.Infrastructure;

namespace AdMng.Directory.IntegrationTests.Integration;

public sealed class SchemaTests(LabFixture lab)
{
    [Fact]
    public void Schema_ContainsAttributeDefinitions()
    {
        LabSettings.RequireLab();
        var schema = lab.Admin.Schema;

        var department = schema.FindAttribute(Attr.Department);
        Assert.NotNull(department);
        Assert.Equal(AttributeSyntax.UnicodeString, department.Syntax);
        Assert.True(department.IsSingleValued);
        Assert.Equal(64, department.RangeUpper);
        Assert.NotEqual(Guid.Empty, department.SchemaIdGuid);
        Assert.Equal(new Guid("bf96794f-0de6-11d0-a285-00aa003049e2"), department.SchemaIdGuid);

        var member = schema.FindAttribute(Attr.Member);
        Assert.NotNull(member);
        Assert.Equal(AttributeSyntax.DistinguishedName, member.Syntax);
        Assert.False(member.IsSingleValued);
        Assert.Equal(2, member.LinkId);

        var memberOf = schema.FindAttribute(Attr.MemberOf);
        Assert.NotNull(memberOf);
        Assert.True(memberOf.IsBackLink);
        Assert.True(memberOf.IsReadOnly);

        Assert.True(schema.FindAttribute(Attr.AllowedAttributesEffective)?.IsConstructed);
        Assert.True(schema.FindAttribute(Attr.SamAccountName)?.IsIndexed);
        Assert.Equal(AttributeSyntax.LargeInteger, schema.FindAttribute(Attr.PwdLastSet)?.Syntax);
        Assert.True(schema.IsBinary(Attr.ObjectSid));
        Assert.True(schema.IsBinary("thumbnailPhoto"));
        Assert.False(schema.IsBinary(Attr.DisplayName));
    }

    [Fact]
    public void Schema_ContainsClassHierarchy()
    {
        LabSettings.RequireLab();
        var schema = lab.Admin.Schema;

        var user = schema.FindClass(ObjectClasses.User);
        Assert.NotNull(user);
        Assert.Equal("organizationalPerson", user.SubClassOf);
        Assert.Equal(ClassCategory.Structural, user.Category);
        Assert.Contains("securityPrincipal", user.AuxiliaryClasses, StringComparer.OrdinalIgnoreCase);

        var allowed = schema.GetAllowedAttributes([ObjectClasses.User]);
        Assert.Contains(Attr.Department, allowed, StringComparer.OrdinalIgnoreCase);
        Assert.Contains(Attr.SamAccountName, allowed, StringComparer.OrdinalIgnoreCase);
        Assert.Contains(Attr.ObjectSid, allowed, StringComparer.OrdinalIgnoreCase);
        Assert.Contains(Attr.Cn, schema.GetMandatoryAttributes([ObjectClasses.User]), StringComparer.OrdinalIgnoreCase);

        var ou = schema.FindClass(ObjectClasses.OrganizationalUnit);
        Assert.NotNull(ou);
        Assert.Contains("domainDNS", ou.PossibleSuperiors, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void DisplayNames_ComeFromDisplaySpecifiers()
    {
        LabSettings.RequireLab();
        var schema = lab.Admin.Schema;
        var english = CultureInfo.GetCultureInfo("en-US");

        Assert.Equal("Display Name", schema.GetDisplayName(Attr.DisplayName, english));
        Assert.Equal("Department", schema.GetDisplayName(Attr.Department, english));
        Assert.Equal("Job Title", schema.GetDisplayName(Attr.Title, english));

        // Polish specifiers when the DC has them, English otherwise - never the raw LDAP name for these.
        Assert.NotEqual(Attr.Title, schema.GetDisplayName(Attr.Title, CultureInfo.GetCultureInfo("pl-PL")));

        // Unknown attributes fall back to the LDAP name.
        Assert.Equal("noSuchAttributeXyz", schema.GetDisplayName("noSuchAttributeXyz", english));
    }
}
