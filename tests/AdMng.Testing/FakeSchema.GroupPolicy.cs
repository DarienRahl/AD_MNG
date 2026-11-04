using AdMng.Core.Schema;

namespace AdMng.Testing;

/// <summary>
/// Schema additions for the GPO management (K2, design note docs/dev/k-computers-and-gpo.md 10): the remaining attributes
/// of a group policy container, the WMI filter class <c>msWMI-Som</c> with its attributes and <c>gPLink</c> /
/// <c>gPOptions</c> on sites. Adding definitions only relaxes the fake, so existing tests keep their behaviour.
/// </summary>
public static partial class FakeSchema
{
    private static readonly Dictionary<string, string[]> GroupPolicyMayContain = new(StringComparer.OrdinalIgnoreCase)
    {
        ["groupPolicyContainer"] =
        [
            "gPCFunctionalityVersion", "gPCMachineExtensionNames", "gPCUserExtensionNames", "gPCWQLFilter", "displayName",
        ],
        ["site"] = ["gPLink", "gPOptions", "description"],
    };

    private static List<AttributeDefinition> CreateGroupPolicyAttributes() =>
    [
        Def("gPCFunctionalityVersion", AttributeSyntax.Integer),
        Def("gPCMachineExtensionNames", AttributeSyntax.UnicodeString),
        Def("gPCUserExtensionNames", AttributeSyntax.UnicodeString),
        Def("gPCWQLFilter", AttributeSyntax.UnicodeString),
        Def("msWMI-ID", AttributeSyntax.UnicodeString),
        Def("msWMI-Name", AttributeSyntax.UnicodeString),
        Def("msWMI-Parm1", AttributeSyntax.UnicodeString),
        Def("msWMI-Parm2", AttributeSyntax.UnicodeString),
        Def("msWMI-Author", AttributeSyntax.UnicodeString),
        Def("msWMI-CreationDate", AttributeSyntax.UnicodeString),
        Def("msWMI-ChangeDate", AttributeSyntax.UnicodeString),
    ];

    private static void ExtendForGroupPolicy(List<ClassDefinition> classes, string schemaNc)
    {
        for (var i = 0; i < classes.Count; i++)
        {
            if (GroupPolicyMayContain.TryGetValue(classes[i].LdapName, out var extra))
            {
                classes[i] = classes[i] with { MayContain = [.. classes[i].MayContain.Concat(extra).Distinct(StringComparer.OrdinalIgnoreCase)] };
            }
        }

        classes.Add(Class("msWMI-Som", "top", schemaNc, "ms-WMI-Som",
        [
            "msWMI-ID", "msWMI-Name", "msWMI-Parm1", "msWMI-Parm2", "msWMI-Author", "msWMI-CreationDate", "msWMI-ChangeDate",
        ]));
    }
}
