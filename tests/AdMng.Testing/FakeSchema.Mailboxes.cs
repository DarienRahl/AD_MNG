using AdMng.Core.Correspondence.Mailboxes;
using AdMng.Core.Schema;

namespace AdMng.Testing;

/// <summary>
/// Schema additions for the mailbox owner queries (U5, design note docs/dev/u5-outlook-correspondence.md 10): the Exchange
/// mailbox type <c>msExchRecipientTypeDetails</c> (Large integer, single-valued) and the delegate links
/// <c>msExchDelegateListLink</c> (full access) and <c>publicDelegates</c> (send on behalf) – DN syntax, multi-valued –
/// allowed on users. In AD both DN attributes are forward links with back links; the fake keeps them as plain DN values.
/// Adding definitions only relaxes the fake, so existing tests keep their behaviour; <see cref="CreateWithoutMailboxes"/>
/// gives a schema of a directory without the Exchange extension.
/// </summary>
public static partial class FakeSchema
{
    /// <summary>The attributes of this extension.</summary>
    public static IReadOnlyList<string> MailboxAttributes { get; } =
        [MailboxTypes.RecipientTypeDetails, MailboxTypes.DelegateListLink, MailboxTypes.PublicDelegates];

    private static readonly Dictionary<string, string[]> MailboxesMayContain = new(StringComparer.OrdinalIgnoreCase)
    {
        ["user"] = [.. MailboxAttributes],
    };

    /// <summary><see cref="Create"/> without the attributes of the mailbox extension (a directory without Exchange).</summary>
    public static SchemaCatalog CreateWithoutMailboxes(string schemaNamingContext = DefaultSchemaNamingContext) =>
        CreateCore(schemaNamingContext, mailboxes: false);

    private static List<AttributeDefinition> CreateMailboxAttributes() =>
    [
        Def(MailboxTypes.RecipientTypeDetails, AttributeSyntax.LargeInteger),
        Def(MailboxTypes.DelegateListLink, AttributeSyntax.DistinguishedName, single: false),
        Def(MailboxTypes.PublicDelegates, AttributeSyntax.DistinguishedName, single: false),
    ];

    private static void ExtendForMailboxes(List<ClassDefinition> classes)
    {
        for (var i = 0; i < classes.Count; i++)
        {
            if (MailboxesMayContain.TryGetValue(classes[i].LdapName, out var extra))
            {
                classes[i] = classes[i] with { MayContain = [.. classes[i].MayContain.Concat(extra).Distinct(StringComparer.OrdinalIgnoreCase)] };
            }
        }
    }
}
