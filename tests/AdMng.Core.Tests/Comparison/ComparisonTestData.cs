using AdMng.Core.ActiveDirectory;
using AdMng.Core.Comparison;
using AdMng.Core.Model;
using AdMng.Core.Schema;
using AdMng.Testing;

namespace AdMng.Core.Tests.Comparison;

/// <summary>Snapshots of objects in contoso.test (side A) and fabrikam.test (side B) for the pure comparison tests.</summary>
internal static class ComparisonTestData
{
    public const string Contoso = "contoso.test";
    public const string Fabrikam = "fabrikam.test";
    public const string ContosoNc = "DC=contoso,DC=test";
    public const string FabrikamNc = "DC=fabrikam,DC=test";

    public static readonly ISchemaCatalog TestSchema = FakeSchema.Create();

    /// <summary>The fake schema with changed or extra attribute definitions (e.g. an attribute confidential only here).</summary>
    public static SchemaCatalog SchemaWith(Func<AttributeDefinition, AttributeDefinition?>? change = null, params AttributeDefinition[] extra)
    {
        var attributes = TestSchema.Attributes
            .Select(a => change is null ? a : change(a))
            .Where(a => a is not null)
            .Select(a => a!)
            .Concat(extra);
        return new SchemaCatalog(attributes, TestSchema.Classes);
    }

    public static ObjectSnapshot User(string domain, string dn, params (string Name, object[] Values)[] attributes) =>
        Snapshot(domain, TestSchema, dn, ["person", "organizationalPerson", ObjectClasses.User], attributes);

    public static ObjectSnapshot Snapshot(
        string domain,
        ISchemaCatalog schema,
        string dn,
        IReadOnlyList<string> objectClasses,
        params (string Name, object[] Values)[] attributes)
    {
        var values = new List<AttributeValues>
        {
            new(Attr.ObjectClass, ["top", .. objectClasses]),
        };
        values.AddRange(attributes.Select(a => new AttributeValues(a.Name, a.Values)));
        return new ObjectSnapshot(new DirectoryObject(dn, values), FakeDirectorySession.CreateInfo(domain), schema);
    }

    public static string LukaszIn(string namingContext) => $"CN=Łukasz Wiśniewski,OU=IT,OU=Pracownicy,OU=Firma,{namingContext}";

    public static string JanIn(string namingContext) => $"CN=Jan Kowalski,OU=IT,OU=Pracownicy,OU=Firma,{namingContext}";

    public static AttributeComparison Row(IReadOnlyList<AttributeComparison> rows, string name) =>
        Assert.Single(rows, r => string.Equals(r.Name, name, StringComparison.OrdinalIgnoreCase));
}
