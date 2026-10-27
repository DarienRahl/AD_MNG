using System.Globalization;
using AdMng.Core.ActiveDirectory;
using AdMng.Core.Comparison;
using AdMng.Core.Directory;
using AdMng.Core.Ldap;
using AdMng.Core.Model;
using AdMng.Presentation.Infrastructure;
using AdMng.Presentation.Lists;

namespace AdMng.Presentation.Comparison;

/// <summary>The groups (or members) of one side, read for the membership tabs.</summary>
/// <param name="Entries">The entries with their kind (direct, primary, nested).</param>
/// <param name="IsTruncated">The list was capped at <see cref="ComparisonReader.MaxMembership"/>.</param>
/// <param name="Skipped">Values not listed: outside the side's domain partition, or foreign security principals.</param>
public sealed record MembershipRead(IReadOnlyList<MembershipEntry> Entries, bool IsTruncated, int Skipped)
{
    public static MembershipRead Empty { get; } = new([], false, 0);

    /// <summary>The DN of the primary group (its nested groups are read too), or null.</summary>
    public string? PrimaryGroupDn { get; init; }

    /// <summary>The count only (never names).</summary>
    public override string ToString() => $"MembershipRead({Entries.Count}, truncated {IsTruncated}, skipped {Skipped})";
}

/// <summary>
/// Reads one side of a comparison through a read-only reader (design note docs/dev/multi-window.md 8.4 and 8.7): the
/// object with all attributes (ranged ones completed), its direct, primary and nested groups, and the direct and nested
/// members of a group. In memory only; nothing is cached between runs.
/// </summary>
public static class ComparisonReader
{
    /// <summary>The most groups or members listed per side.</summary>
    public const int MaxMembership = 5000;

    /// <summary>What the object read requests: everything plus the constructed attributes of the attribute editor.</summary>
    public static IReadOnlyList<string> ObjectAttributes { get; } =
        ["*", Attr.CanonicalName, Attr.UserAccountControlComputed, Attr.PasswordExpiryComputed, Attr.MsDsPrincipalName];

    /// <summary>What is read of every group or member.</summary>
    public static IReadOnlyList<string> PrincipalAttributes { get; } =
        [Attr.Name, Attr.ObjectClass, Attr.SamAccountName, Attr.ObjectSid, Attr.GroupType, Attr.Mail];

    /// <summary>The object as a snapshot of its side, or null when it does not exist.</summary>
    public static async Task<ObjectSnapshot?> ReadAsync(IDirectoryReader reader, string distinguishedName, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentException.ThrowIfNullOrWhiteSpace(distinguishedName);
        var obj = await reader.GetAsync(distinguishedName, ObjectAttributes, cancellationToken).ConfigureAwait(false);
        if (obj is null)
        {
            return null;
        }

        obj = await RangedValues.CompleteAsync(reader, obj, cancellationToken).ConfigureAwait(false);
        return new ObjectSnapshot(obj, reader.Info, reader.Schema);
    }

    /// <summary>
    /// The groups the object is a direct member of (<c>memberOf</c>) plus its primary group (<c>primaryGroupID</c>, the
    /// group whose SID is the domain SID + RID). Groups outside the side's domain partition are counted, not listed.
    /// </summary>
    /// <param name="reader">The side's reader.</param>
    /// <param name="obj">The object (read with <see cref="ReadAsync"/>).</param>
    /// <param name="domainSid">The side's domain SID (policy), else derived from the object's SID.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    public static async Task<MembershipRead> ReadDirectMembershipAsync(
        IDirectoryReader reader,
        ObjectSnapshot obj,
        string? domainSid,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(obj);
        var memberOf = obj.Attributes.GetValueOrDefault(Attr.MemberOf)?.OfType<string>().ToList() ?? [];
        var truncated = memberOf.Count > MaxMembership;
        var groups = await DirectoryLookup.ReadManyAsync(reader, memberOf.Take(MaxMembership), PrincipalAttributes, cancellationToken).ConfigureAwait(false);
        var entries = groups.Values.Select(g => Entry(g, MembershipKind.Direct)).ToList();
        var skipped = memberOf.Take(MaxMembership).Count() - groups.Count;

        string? primaryDn = null;
        if (await ReadPrimaryGroupAsync(reader, obj, domainSid, cancellationToken).ConfigureAwait(false) is { } primary)
        {
            primaryDn = primary.DistinguishedName;
            entries.RemoveAll(e => DistinguishedName.AreEqual(e.DistinguishedName, primary.DistinguishedName));
            entries.Add(Entry(primary, MembershipKind.Primary));
        }

        return new MembershipRead(entries, truncated, Math.Max(0, skipped)) { PrimaryGroupDn = primaryDn };
    }

    /// <summary>
    /// The groups the object belongs to through other groups: one search for groups whose <c>member</c> chain reaches the
    /// object (<c>LDAP_MATCHING_RULE_IN_CHAIN</c>, as the "Member Of" page's nested list), plus the same search for its
    /// primary group (AD does not follow the primary group in <c>member</c>). Only groups not in
    /// <paramref name="direct"/> are returned, as <see cref="MembershipKind.Nested"/>.
    /// </summary>
    public static async Task<MembershipRead> ReadNestedMembershipAsync(
        IDirectoryReader reader,
        ObjectSnapshot obj,
        MembershipRead direct,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(obj);
        ArgumentNullException.ThrowIfNull(direct);
        var known = direct.Entries.Select(e => DnKey.For(e.DistinguishedName)).ToHashSet(StringComparer.Ordinal);
        var nested = new Dictionary<string, MembershipEntry>(StringComparer.Ordinal);
        var truncated = false;
        string?[] starts = [obj.DistinguishedName, direct.PrimaryGroupDn];
        foreach (var start in starts.OfType<string>())
        {
            var search = new DirectorySearch
            {
                BaseDn = reader.Info.DefaultNamingContext,
                Scope = SearchScope.Subtree,
                Filter = LdapFilter.And(ObjectKind.Group.ToLdapFilter(), LdapFilter.InChain(Attr.Member, start)),
                Attributes = PrincipalAttributes,
                SizeLimit = MaxMembership + 1,
            };
            await foreach (var group in reader.SearchAsync(search, cancellationToken).ConfigureAwait(false))
            {
                var key = DnKey.For(group.DistinguishedName);
                if (known.Contains(key))
                {
                    continue;
                }

                if (nested.Count >= MaxMembership)
                {
                    truncated = true;
                    break;
                }

                nested.TryAdd(key, Entry(group, MembershipKind.Nested));
            }
        }

        return new MembershipRead([.. nested.Values], truncated, 0);
    }

    /// <summary>
    /// The direct members of a group (<c>member</c>, ranged values completed), keyed by principal
    /// (<see cref="GroupKey.ForPrincipal"/>). Foreign security principals and members outside the side's domain partition
    /// are counted, not listed.
    /// </summary>
    public static async Task<MembershipRead> ReadMembersAsync(IDirectoryReader reader, ObjectSnapshot group, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(group);
        var member = group.Attributes.GetValueOrDefault(Attr.Member)?.OfType<string>().ToList() ?? [];
        var truncated = member.Count > MaxMembership;
        var listed = member.Take(MaxMembership).ToList();
        var found = await DirectoryLookup.ReadManyAsync(reader, listed, PrincipalAttributes, cancellationToken).ConfigureAwait(false);
        var entries = found.Values
            .Where(m => m.Kind != ObjectKind.ForeignSecurityPrincipal)
            .Select(m => MemberEntry(m, MembershipKind.Direct))
            .ToList();
        return new MembershipRead(entries, truncated, listed.Count - entries.Count);
    }

    /// <summary>
    /// The members of a group through other groups (<c>memberOf</c> chain): only those not in <paramref name="direct"/>,
    /// as <see cref="MembershipKind.Nested"/>.
    /// </summary>
    public static async Task<MembershipRead> ReadNestedMembersAsync(
        IDirectoryReader reader,
        ObjectSnapshot group,
        MembershipRead direct,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(group);
        ArgumentNullException.ThrowIfNull(direct);
        var known = direct.Entries.Select(e => DnKey.For(e.DistinguishedName)).ToHashSet(StringComparer.Ordinal);
        var search = new DirectorySearch
        {
            BaseDn = reader.Info.DefaultNamingContext,
            Scope = SearchScope.Subtree,
            Filter = LdapFilter.InChain(Attr.MemberOf, group.DistinguishedName),
            Attributes = PrincipalAttributes,
            SizeLimit = MaxMembership + 1,
        };
        var nested = new List<MembershipEntry>();
        var truncated = false;
        await foreach (var member in reader.SearchAsync(search, cancellationToken).ConfigureAwait(false))
        {
            if (member.Kind == ObjectKind.ForeignSecurityPrincipal || known.Contains(DnKey.For(member.DistinguishedName)))
            {
                continue;
            }

            if (nested.Count >= MaxMembership)
            {
                truncated = true;
                break;
            }

            nested.Add(MemberEntry(member, MembershipKind.Nested));
        }

        return new MembershipRead(nested, truncated, 0);
    }

    private static async Task<DirectoryObject?> ReadPrimaryGroupAsync(IDirectoryReader reader, ObjectSnapshot obj, string? domainSid, CancellationToken cancellationToken)
    {
        if (obj.Object.GetInt64(Attr.PrimaryGroupId) is not ({ } rid and > 0 and <= uint.MaxValue))
        {
            return null;
        }

        var prefix = domainSid;
        if (string.IsNullOrEmpty(prefix) && obj.ObjectSid is { } sid && sid.LastIndexOf('-') is var last and > 0)
        {
            prefix = sid[..last];
        }

        if (string.IsNullOrEmpty(prefix))
        {
            return null;
        }

        byte[] bytes;
        try
        {
            bytes = AdConvert.SidToBytes($"{prefix}-{rid.ToString(CultureInfo.InvariantCulture)}");
        }
        catch (Exception ex) when (ex is FormatException or OverflowException or ArgumentException)
        {
            return null;
        }

        var search = new DirectorySearch
        {
            BaseDn = reader.Info.DefaultNamingContext,
            Scope = SearchScope.Subtree,
            Filter = $"({Attr.ObjectSid}={LdapFilter.EscapeBinary(bytes)})",
            Attributes = PrincipalAttributes,
            SizeLimit = 1,
        };
        await foreach (var group in reader.SearchAsync(search, cancellationToken).ConfigureAwait(false))
        {
            return group;
        }

        return null;
    }

    private static MembershipEntry Entry(DirectoryObject group, MembershipKind kind) =>
        new(GroupKey.For(Sid(group), group.GetString(Attr.SamAccountName), group.Name), group.Name, group.DistinguishedName, kind);

    private static MembershipEntry MemberEntry(DirectoryObject member, MembershipKind kind) =>
        new(GroupKey.ForPrincipal(Sid(member), member.GetString(Attr.SamAccountName), member.GetString(Attr.Mail), member.Name),
            member.Name, member.DistinguishedName, kind);

    private static string? Sid(DirectoryObject obj)
    {
        try
        {
            return obj.ObjectSid;
        }
        catch (FormatException)
        {
            return null;
        }
    }
}
