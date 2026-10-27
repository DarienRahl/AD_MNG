namespace AdMng.Core.GroupPolicy;

/// <summary>Which side of a Restricted Groups entry (<c>[Group Membership]</c> of <c>GptTmpl.inf</c>) a line sets.</summary>
public enum RestrictedGroupRelation
{
    /// <summary><c>&lt;group&gt;__Members = …</c>: the group's members are exactly these (the highest-precedence GPO wins).</summary>
    Members,

    /// <summary><c>&lt;group&gt;__Memberof = …</c>: the group is added to these groups (the existing members stay).</summary>
    MemberOf,
}

/// <summary>One line of <c>[Group Membership]</c> (Restricted Groups, [MS-GPSB] 2.2.6).</summary>
/// <param name="Group">The group the line is about: a SID (<c>*S-1-5-32-544</c> → <c>S-1-5-32-544</c>) or a name as written.</param>
/// <param name="Relation">Members or MemberOf.</param>
/// <param name="Principals">The members, or the groups it becomes a member of: SIDs or names; empty = "no members".</param>
public sealed record RestrictedGroupEntry(string Group, RestrictedGroupRelation Relation, IReadOnlyList<string> Principals);

/// <summary>One line of <c>[Privilege Rights]</c> (User Rights Assignment, [MS-GPSB] 2.2.7).</summary>
/// <param name="Right">The constant name of the privilege or logon right, e.g. <c>SeDebugPrivilege</c>.</param>
/// <param name="Principals">Who holds it: SIDs or names; empty = nobody.</param>
public sealed record UserRightAssignment(string Right, IReadOnlyList<string> Principals);

/// <summary>
/// The parts of the security template <c>Machine/Microsoft/Windows NT/SecEdit/GptTmpl.inf</c> ([MS-GPSB] 2.2, UTF-16LE with
/// BOM) that grant rights on computers: <c>[Group Membership]</c> (Restricted Groups, both the Members and the MemberOf
/// form) and <c>[Privilege Rights]</c>. Shared by the privileged access review (U4c) and AdMng Watchdog (its
/// <c>GptTmplParser</c> takes the principal syntax and the Restricted Groups keys from here). A later duplicate key wins,
/// as in the security engine. Content is untrusted: decoding errors fail the file (<see cref="GpoParseException"/>).
/// </summary>
public sealed record SecurityTemplate
{
    /// <summary>The template's path in a GPO folder.</summary>
    public const string RelativePath = "Machine/Microsoft/Windows NT/SecEdit/GptTmpl.inf";

    /// <summary>The client-side extension that applies it (Security, in <c>gPCMachineExtensionNames</c>).</summary>
    public static readonly Guid ExtensionId = new("827D319E-6EAC-11D2-A4EA-00C04F79F83A");

    public IReadOnlyList<RestrictedGroupEntry> GroupMembership { get; init; } = [];

    public IReadOnlyList<UserRightAssignment> PrivilegeRights { get; init; } = [];

    /// <summary>Parses the template (the other sections are ignored).</summary>
    /// <exception cref="GpoParseException">The file is not valid text or has no section.</exception>
    public static SecurityTemplate Parse(ReadOnlySpan<byte> content)
    {
        var text = GpoText.DecodeUnicodeText(content);
        var sawSection = false;

        // Keyed, so that a later duplicate replaces the earlier one in O(1) (the file is untrusted and may be large); the
        // line number keeps the order of the last occurrence.
        var membership = new Dictionary<(string Group, RestrictedGroupRelation Relation), (int Line, RestrictedGroupEntry Entry)>(GroupKeyComparer.Instance);
        var rights = new Dictionary<string, (int Line, UserRightAssignment Right)>(StringComparer.OrdinalIgnoreCase);
        var number = 0;
        foreach (var line in IniReader.Read(text))
        {
            sawSection = true;
            number++;
            if (line.Value is null)
            {
                continue;
            }

            if (line.Section.Equals("Privilege Rights", StringComparison.OrdinalIgnoreCase))
            {
                if (line.Key.Length > 0)
                {
                    rights[line.Key] = (number, new UserRightAssignment(line.Key, Principals(line.Value)));
                }
            }
            else if (line.Section.Equals("Group Membership", StringComparison.OrdinalIgnoreCase)
                     && TrySplitGroupMembershipKey(line.Key, out var group, out var relation, out _)
                     && group.Length > 0
                     && relation is { } known)
            {
                membership[(group, known)] = (number, new RestrictedGroupEntry(group, known, Principals(line.Value)));
            }
        }

        if (!sawSection && text.Trim().Length > 0)
        {
            throw new GpoParseException("GptTmpl.inf has no section.");
        }

        return new SecurityTemplate
        {
            GroupMembership = [.. membership.Values.OrderBy(m => m.Line).Select(m => m.Entry)],
            PrivilegeRights = [.. rights.Values.OrderBy(r => r.Line).Select(r => r.Right)],
        };
    }

    /// <summary>The elements of a principal list: <c>*S-1-5-32-544</c> → <c>S-1-5-32-544</c>, names kept as written.</summary>
    public static IReadOnlyList<string> Principals(string? value) =>
        string.IsNullOrWhiteSpace(value) ? [] : [.. IniReader.SplitCommaList(value).Select(Principal).Where(p => p.Length > 0)];

    /// <summary>
    /// One principal: a leading <c>*</c> marks a SID (removed; a SID is upper-cased), anything else is a name kept as written
    /// (<c>CONTOSO\GG_Serwis</c>, <c>Administrators</c>).
    /// </summary>
    public static string Principal(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var text = value.Trim();
        if (text.StartsWith('*'))
        {
            text = text[1..].Trim();
        }

        return IsSid(text) ? text.ToUpperInvariant() : text;
    }

    /// <summary>A SID in SDDL form (<c>S-</c> followed by digits and dashes).</summary>
    public static bool IsSid(string? text) =>
        text is { Length: > 2 } && text.StartsWith("S-", StringComparison.OrdinalIgnoreCase) && text.Skip(2).All(c => char.IsAsciiDigit(c) || c == '-');

    /// <summary>
    /// Splits a <c>[Group Membership]</c> key <c>&lt;group&gt;__&lt;relation&gt;</c> at the last <c>__</c>: the group
    /// (<see cref="Principal"/>), the relation when it is <c>Members</c> or <c>Memberof</c> (case-insensitive; null for
    /// another word) and the relation as written.
    /// </summary>
    /// <returns>False when the key has no <c>__</c> with something before it.</returns>
    public static bool TrySplitGroupMembershipKey(string key, out string group, out RestrictedGroupRelation? relation, out string relationText)
    {
        ArgumentNullException.ThrowIfNull(key);
        group = string.Empty;
        relation = null;
        relationText = string.Empty;
        var separator = key.LastIndexOf("__", StringComparison.Ordinal);
        if (separator <= 0)
        {
            return false;
        }

        group = Principal(key[..separator]);
        relationText = key[(separator + 2)..];
        relation = relationText.Equals("Members", StringComparison.OrdinalIgnoreCase) ? RestrictedGroupRelation.Members
            : relationText.Equals("Memberof", StringComparison.OrdinalIgnoreCase) ? RestrictedGroupRelation.MemberOf
            : null;
        return true;
    }

    /// <summary>Restricted Groups entries are the same when the group (case-insensitive) and the relation are.</summary>
    private sealed class GroupKeyComparer : IEqualityComparer<(string Group, RestrictedGroupRelation Relation)>
    {
        public static GroupKeyComparer Instance { get; } = new();

        public bool Equals((string Group, RestrictedGroupRelation Relation) x, (string Group, RestrictedGroupRelation Relation) y) =>
            x.Relation == y.Relation && StringComparer.OrdinalIgnoreCase.Equals(x.Group, y.Group);

        public int GetHashCode((string Group, RestrictedGroupRelation Relation) obj) =>
            HashCode.Combine(StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Group), obj.Relation);
    }
}
