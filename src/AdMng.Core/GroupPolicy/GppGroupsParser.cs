namespace AdMng.Core.GroupPolicy;

/// <summary>The action of a Group Policy Preferences item ([MS-GPPREF] 2.2.1.5: C, R, U, D).</summary>
public enum GppItemAction
{
    /// <summary>"C": create the group when it is missing, then apply the member changes.</summary>
    Create,

    /// <summary>"R": delete and recreate the group (its current members are gone), then add the members.</summary>
    Replace,

    /// <summary>"U" (the default): change the existing group - remove all users/groups when asked, then the member changes.</summary>
    Update,

    /// <summary>"D": delete the group (a built-in group cannot be deleted; its GPP members are gone either way).</summary>
    Delete,
}

/// <summary>What a member line of a GPP group does.</summary>
public enum GppMemberAction
{
    Add,
    Remove,
}

/// <summary>One member of a GPP local group item.</summary>
/// <param name="Name">The name as written (<c>CONTOSO\GG_Serwis</c>); may be empty when only the SID is known.</param>
/// <param name="Sid">The SID the editor resolved (preferred), null when it was typed without resolving.</param>
/// <param name="Action">ADD or REMOVE.</param>
public sealed record GppGroupMember(string? Name, string? Sid, GppMemberAction Action);

/// <summary>
/// One local group item of <c>Machine/Preferences/Groups/Groups.xml</c> (Group Policy Preferences, Local Users and Groups).
/// </summary>
public sealed record GppLocalGroup
{
    /// <summary>The item's uid (stable id of the editor).</summary>
    public string? Uid { get; init; }

    /// <summary>The item's display name (<c>Administrators (built-in)</c>).</summary>
    public string? Name { get; init; }

    /// <summary>The group's SID (<c>groupSid</c>; set for built-in groups by the editor), null when not given.</summary>
    public string? GroupSid { get; init; }

    /// <summary>The group's name (<c>groupName</c>, e.g. <c>Administrators (built-in)</c>).</summary>
    public string? GroupName { get; init; }

    public GppItemAction Action { get; init; } = GppItemAction.Update;

    /// <summary>"Delete all member users" before the member changes.</summary>
    public bool DeleteAllUsers { get; init; }

    /// <summary>"Delete all member groups" before the member changes.</summary>
    public bool DeleteAllGroups { get; init; }

    /// <summary>The item (or a collection it is in) is disabled in the editor: the client skips it.</summary>
    public bool Disabled { get; init; }

    /// <summary>
    /// Item-level targeting: the item or a collection it is in has <c>Filters</c> - whether it applies depends on the
    /// computer (name, OS, group, IP range …), so the result is conditional.
    /// </summary>
    public bool HasItemLevelTargeting { get; init; }

    public IReadOnlyList<GppGroupMember> Members { get; init; } = [];
}

/// <summary>
/// Reads the local group items of a GPP <c>Groups.xml</c> ([MS-GPPREF] 2.2.1.5.1): every <c>Group</c> element with a
/// <c>Properties</c> child, also inside <c>Collection</c> elements, in document order (the order the client applies them).
/// Local users (<c>User</c> items) are not groups and are left out. Loaded with the hardened <see cref="GpoXml"/> (no DTD,
/// no external entities, nesting and size limits); content is untrusted.
/// </summary>
public static class GppGroupsParser
{
    /// <summary>The file's path in a GPO folder.</summary>
    public const string RelativePath = "Machine/Preferences/Groups/Groups.xml";

    /// <summary>The client-side extension that applies it (Preference CSE GUID Local Users and Groups).</summary>
    public static readonly Guid ExtensionId = new("17D89FEC-5C44-4972-B12D-241CAEF74509");

    /// <exception cref="GpoParseException">The file is not well-formed XML or uses a prohibited feature.</exception>
    public static IReadOnlyList<GppLocalGroup> Parse(ReadOnlySpan<byte> content)
    {
        var document = GpoXml.Load(content);
        var result = new List<GppLocalGroup>();
        foreach (var item in document.Root!.DescendantsAndSelf().Where(e => e.Name.LocalName.Equals("Group", StringComparison.OrdinalIgnoreCase)))
        {
            if (GpoXml.Child(item, "Properties") is not { } properties)
            {
                continue;
            }

            var members = new List<GppGroupMember>();
            if (GpoXml.Child(properties, "Members") is { } list)
            {
                foreach (var member in list.Elements().Where(e => e.Name.LocalName.Equals("Member", StringComparison.OrdinalIgnoreCase)))
                {
                    var name = Text(GpoXml.Attribute(member, "name"));
                    var sid = Text(GpoXml.Attribute(member, "sid"));
                    if (name is null && sid is null)
                    {
                        continue;
                    }

                    var action = string.Equals(GpoXml.Attribute(member, "action")?.Trim(), "REMOVE", StringComparison.OrdinalIgnoreCase)
                        ? GppMemberAction.Remove
                        : GppMemberAction.Add;
                    members.Add(new GppGroupMember(name, sid is not null && SecurityTemplate.IsSid(sid) ? sid.ToUpperInvariant() : null, action));
                }
            }

            var groupSid = Text(GpoXml.Attribute(properties, "groupSid"));
            result.Add(new GppLocalGroup
            {
                Uid = Text(GpoXml.Attribute(item, "uid")),
                Name = Text(GpoXml.Attribute(item, "name")),
                GroupSid = groupSid is not null && SecurityTemplate.IsSid(groupSid) ? groupSid.ToUpperInvariant() : null,
                GroupName = Text(GpoXml.Attribute(properties, "groupName")),
                Action = Action(GpoXml.Attribute(properties, "action")),
                DeleteAllUsers = Flag(GpoXml.Attribute(properties, "deleteAllUsers")),
                DeleteAllGroups = Flag(GpoXml.Attribute(properties, "deleteAllGroups")),
                Disabled = item.AncestorsAndSelf().Any(e => Flag(GpoXml.Attribute(e, "disabled"))),
                HasItemLevelTargeting = item.AncestorsAndSelf().Any(e => GpoXml.Child(e, "Filters") is { HasElements: true }),
                Members = members,
            });
        }

        return result;
    }

    private static GppItemAction Action(string? value) => value?.Trim().ToUpperInvariant() switch
    {
        "C" => GppItemAction.Create,
        "R" => GppItemAction.Replace,
        "D" => GppItemAction.Delete,
        _ => GppItemAction.Update,
    };

    private static bool Flag(string? value) => value?.Trim() is "1" || string.Equals(value?.Trim(), "true", StringComparison.OrdinalIgnoreCase);

    private static string? Text(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
