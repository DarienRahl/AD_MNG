using AdMng.Core.Schema;

namespace AdMng.Testing;

/// <summary>
/// Schema additions for the bulk creation wizard (F3, docs/dev/phase3-bulk-create.md 2.13) – <b>opt-in</b> through
/// <c>FakeSchema.Create(schemaNamingContext, bulkCreate: true)</c>, so the attribute lists other tests and screenshots pin
/// do not change: <c>extensionAttribute1–15</c>, <c>ipPhone</c>, <c>pager</c>, <c>otherMobile</c>, <c>otherHomePhone</c>,
/// <c>postalAddress</c>, <c>middleName</c> and the custom <c>contosoBadgeNumber</c> on users (contacts get the phones and
/// extension attributes too), the back link <c>msDS-PSOApplied</c> of <c>msDS-PSOAppliesTo</c>, the forest reads of the
/// planner – <c>crossRef</c> with <c>dnsRoot</c> and <c>nCName</c>, <c>uPNSuffixes</c> on the partitions container – and the
/// RID set (<c>rIDSetReferences</c>, <c>rIDAllocationPool</c>, <c>rIDNextRID</c>). <c>msDS-LastKnownRDN</c> and
/// <c>lastKnownParent</c> are in the base schema already.
/// </summary>
public static partial class FakeSchema
{
    /// <summary>The custom attribute of the sample schema (a schema extension of the company, on users).</summary>
    public const string CustomBadgeAttribute = "contosoBadgeNumber";

    private static readonly string[] ExtensionAttributes = [.. Enumerable.Range(1, 15).Select(i => $"extensionAttribute{i}")];

    private static readonly Dictionary<string, string[]> BulkCreateMayContain = new(StringComparer.OrdinalIgnoreCase)
    {
        ["top"] = ["msDS-PSOApplied"],
        ["organizationalPerson"] = ["ipPhone", "pager", "otherMobile", "otherHomePhone", "postalAddress", "middleName"],
        ["user"] = [.. ExtensionAttributes, CustomBadgeAttribute, "rIDSetReferences"],
        ["contact"] = [.. ExtensionAttributes],
        ["group"] = [.. ExtensionAttributes],
        ["crossRefContainer"] = ["uPNSuffixes"],
    };

    private static List<AttributeDefinition> CreateBulkCreateAttributes() =>
    [
        .. ExtensionAttributes.Select(name => Def(name, AttributeSyntax.UnicodeString, lower: 1, upper: 1024)),
        Def("ipPhone", AttributeSyntax.UnicodeString, lower: 1, upper: 64),
        Def("pager", AttributeSyntax.UnicodeString, lower: 1, upper: 64),
        Def("otherMobile", AttributeSyntax.UnicodeString, single: false, lower: 1, upper: 64),
        Def("otherHomePhone", AttributeSyntax.UnicodeString, single: false, lower: 1, upper: 64),
        Def("postalAddress", AttributeSyntax.UnicodeString, single: false, lower: 1, upper: 4096),
        Def("middleName", AttributeSyntax.UnicodeString, lower: 0, upper: 64),
        Def(CustomBadgeAttribute, AttributeSyntax.UnicodeString, lower: 1, upper: 32),
        Def("msDS-PSOApplied", AttributeSyntax.DistinguishedName, single: false, linkId: 2119),
        Def("uPNSuffixes", AttributeSyntax.UnicodeString, single: false),
        Def("dnsRoot", AttributeSyntax.UnicodeString, single: false, lower: 1, upper: 255),
        Def("nCName", AttributeSyntax.DistinguishedName),
        Def("rIDSetReferences", AttributeSyntax.DistinguishedName, single: false, systemOnly: true),
        Def("rIDAllocationPool", AttributeSyntax.LargeInteger, systemOnly: true),
        Def("rIDNextRID", AttributeSyntax.Integer, systemOnly: true),
    ];

    private static void ExtendForBulkCreate(List<ClassDefinition> classes, string schemaNc)
    {
        if (!classes.Any(c => string.Equals(c.LdapName, "crossRef", StringComparison.OrdinalIgnoreCase)))
        {
            classes.Add(Class("crossRef", "top", schemaNc, "Cross-Ref", ["dnsRoot", "nCName", "systemFlags"]));
        }

        if (!classes.Any(c => string.Equals(c.LdapName, "rIDSet", StringComparison.OrdinalIgnoreCase)))
        {
            classes.Add(Class("rIDSet", "top", schemaNc, "RID-Set", ["rIDAllocationPool", "rIDNextRID"]));
        }

        for (var i = 0; i < classes.Count; i++)
        {
            if (BulkCreateMayContain.TryGetValue(classes[i].LdapName, out var extra))
            {
                classes[i] = classes[i] with { MayContain = [.. classes[i].MayContain.Concat(extra).Distinct(StringComparer.OrdinalIgnoreCase)] };
            }
        }
    }
}
