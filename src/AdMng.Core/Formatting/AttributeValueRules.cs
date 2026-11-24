using AdMng.Core.ActiveDirectory;
using AdMng.Core.Schema;

namespace AdMng.Core.Formatting;

/// <summary>
/// What a raw attribute value is, by the rules of the program's attribute view: a GUID, a SID, a FILETIME, a flag set, a time in one
/// of the directory's text forms. The one place of these decisions - the formatter (<see cref="AttributeFormatter"/>, what the
/// Attribute Editor and the lists show) and the converter of the PowerShell window's read commands (typed values) both use it, so an
/// attribute added here is treated the same in both (audit ps-commands-9: the converter kept its own copies).
/// </summary>
public static class AttributeValueRules
{
    private static readonly HashSet<string> Sids = new(StringComparer.OrdinalIgnoreCase)
    {
        Attr.ObjectSid, Attr.SidHistory, Attr.TokenGroups, "tokenGroupsGlobalAndUniversal", "tokenGroupsNoGCAcceptable",
        "securityIdentifier", "mS-DS-CreatorSID",
    };

    private static readonly HashSet<string> UserAccountControls = new(StringComparer.OrdinalIgnoreCase)
    {
        Attr.UserAccountControl, Attr.UserAccountControlComputed,
    };

    private static readonly HashSet<string> GeneralizedTimes = new(StringComparer.OrdinalIgnoreCase)
    {
        Attr.WhenCreated, Attr.WhenChanged, "dSCorePropagationData", "msDS-LastSuccessfulInteractiveLogonTime",
    };

    /// <summary>The attributes whose binary values are SIDs, besides those of the SID syntax.</summary>
    public static IReadOnlySet<string> SidAttributes => Sids;

    /// <summary>The attributes that hold the flags of <c>userAccountControl</c>.</summary>
    public static IReadOnlySet<string> UserAccountControlAttributes => UserAccountControls;

    /// <summary>Well-known GeneralizedTime attributes, used when the schema does not describe them.</summary>
    public static IReadOnlySet<string> GeneralizedTimeAttributes => GeneralizedTimes;

    /// <summary>A binary value that is a GUID: 16 bytes of an attribute whose name ends in "guid".</summary>
    public static bool IsGuid(string name, byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(bytes);
        return bytes.Length == 16 && name.EndsWith("guid", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>A binary value that is a SID: of a SID attribute, or of an attribute of the SID syntax.</summary>
    public static bool IsSid(string name, AttributeDefinition? definition) =>
        Sids.Contains(name) || definition?.Syntax == AttributeSyntax.Sid;

    /// <summary>A text value that is a FILETIME (<c>lastLogonTimestamp</c>, <c>pwdLastSet</c>, ...).</summary>
    public static bool IsFileTime(string name) => Attr.FileTimeAttributes.Contains(name);

    /// <summary>A text value that is the flag set of <c>userAccountControl</c>.</summary>
    public static bool IsUserAccountControl(string name) => UserAccountControls.Contains(name);

    /// <summary>A text value that is the flag set of <c>groupType</c>.</summary>
    public static bool IsGroupType(string name) => string.Equals(name, Attr.GroupType, StringComparison.OrdinalIgnoreCase);

    /// <summary>The syntax a text value is read in: the schema's, else GeneralizedTime for the well-known time attributes, else unknown.</summary>
    public static AttributeSyntax TextSyntax(string name, AttributeDefinition? definition) =>
        definition?.Syntax ?? (GeneralizedTimes.Contains(name) ? AttributeSyntax.GeneralizedTime : AttributeSyntax.Unknown);
}
