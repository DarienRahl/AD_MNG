using System.Collections.Concurrent;
using System.Globalization;
using AdMng.Core.ActiveDirectory;
using AdMng.Core.Directory;
using AdMng.Core.GroupPolicy;
using AdMng.Core.Ldap;

namespace AdMng.Core.PrivilegedAccess;

/// <summary>Limits of the GPO source.</summary>
public sealed record GroupPolicyGrantSourceOptions
{
    /// <summary>Largest GptTmpl.inf / Groups.xml read (larger files are skipped and reported).</summary>
    public long MaxFileBytes { get; init; } = 4 * 1024 * 1024;

    /// <summary>Files read at the same time.</summary>
    public int MaxConcurrentReads { get; init; } = 4;

    /// <summary>Names per sAMAccountName search.</summary>
    public int NamesPerSearch { get; init; } = 50;
}

/// <summary>
/// U4c - local rights from GPOs (<see cref="PrivilegeSource.GroupPolicy"/>, design docs/dev/u4-privileged-access.md
/// section 6): reads the GPOs, links and computers from the session's DC (<see cref="GroupPolicyTopology"/>), the
/// GptTmpl.inf and Groups.xml of every GPO that applies to a computer from SYSVOL of the same DC
/// (<see cref="ISysvolFileReader"/>, in memory only), resolves the names to SIDs (one search per 50 names) and returns the
/// grants of <see cref="GroupPolicyRightsResolver"/>. What cannot be read is reported to the tab
/// (<see cref="PrivilegeScanContext.Report"/>) and the run goes on with the rest. Stateless: one instance serves every
/// window.
/// </summary>
public sealed class GroupPolicyGrantSource(ISysvolFileReader sysvol, GroupPolicyGrantSourceOptions? options = null) : IPrivilegeGrantSource
{
    /// <summary>Notice codes (resource keys <c>PrivilegedAccess.Notice.{code}</c>).</summary>
    public static class Notices
    {
        /// <summary>Prefix of <c>GroupPolicy.SysvolUnavailable.{reason}</c>; argument: the DC.</summary>
        public const string SysvolUnavailablePrefix = "GroupPolicy.SysvolUnavailable.";

        /// <summary>Files that could not be read (access denied, read error); arguments: count, GPO names.</summary>
        public const string FilesUnreadable = "GroupPolicy.FilesUnreadable";

        /// <summary>Files that are not valid or too large; arguments: count, GPO names.</summary>
        public const string FilesInvalid = "GroupPolicy.FilesInvalid";

        /// <summary>Names that are no account or group of the domain; arguments: count, names.</summary>
        public const string UnresolvedPrincipals = "GroupPolicy.UnresolvedPrincipals";

        /// <summary>Links to GPOs without a container in this domain; argument: count.</summary>
        public const string MissingPolicies = "GroupPolicy.MissingPolicies";

        /// <summary>gPCFileSysPath points elsewhere; arguments: count, GPO names.</summary>
        public const string PathRedirected = "GroupPolicy.PathRedirected";

        public static string SysvolUnavailable(SysvolUnavailableReason reason) => SysvolUnavailablePrefix + reason;
    }

    private const int MaxNamesInNotice = 10;

    private readonly ISysvolFileReader _sysvol = sysvol ?? throw new ArgumentNullException(nameof(sysvol));
    private readonly GroupPolicyGrantSourceOptions _options = options ?? new GroupPolicyGrantSourceOptions();

    public PrivilegeSource Source => PrivilegeSource.GroupPolicy;

    public async Task<IReadOnlyList<PrivilegeGrant>> CollectAsync(PrivilegeScanContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var session = context.Session;
        var topology = await GroupPolicyTopology.ReadAsync(session, context.Options.PageSize, cancellationToken).ConfigureAwait(false);

        // Only the GPOs that reach a computer are read from SYSVOL.
        var inheritance = new GroupPolicyInheritance(topology.DomainDn, topology.Containers);
        var needed = new HashSet<Guid>();
        foreach (var group in topology.Computers.GroupBy(c => (Parent: ParentOf(c.Dn), c.SiteDn)))
        {
            foreach (var applied in inheritance.Resolve(group.Key.Parent, group.Key.SiteDn))
            {
                if (topology.Policies.TryGetValue(applied.GpoId, out var policy) && !policy.ComputerSettingsDisabled)
                {
                    needed.Add(applied.GpoId);
                }
            }
        }

        if (needed.Count == 0)
        {
            return [];
        }

        var redirected = needed.Select(id => topology.Policies[id]).Where(p => p.IsPathRedirected(session.Info.DomainDnsName)).ToList();
        if (redirected.Count > 0)
        {
            context.Report(Notice(Notices.PathRedirected, redirected.Select(p => p.DisplayName)));
        }

        var settings = await ReadSettingsAsync(context, topology, needed, cancellationToken).ConfigureAwait(false);
        if (settings is null)
        {
            return [];
        }

        var domain = new GroupPolicyDomainNames(session.Info.DomainDnsName, session.Info.DomainNetBiosName);
        var sids = await ResolveNamesAsync(session, GroupPolicyRightsResolver.NamesToResolve(settings.Values, domain), context.Options.PageSize, cancellationToken)
            .ConfigureAwait(false);
        var resolver = new GroupPolicyRightsResolver(topology, settings, sids, context.Groups, context.Options, domain);
        var grants = resolver.Resolve();
        if (resolver.UnresolvedNames is { Count: > 0 } unresolved)
        {
            context.Report(Notice(Notices.UnresolvedPrincipals, unresolved));
        }

        if (resolver.MissingPolicies > 0)
        {
            context.Report(new PrivilegeSourceNotice(PrivilegeSource.GroupPolicy, Notices.MissingPolicies, [Count(resolver.MissingPolicies)]));
        }

        return grants;
    }

    /// <summary>GptTmpl.inf and Groups.xml of the GPOs; null when SYSVOL cannot be opened (reported).</summary>
    private async Task<Dictionary<Guid, GroupPolicyRightSettings>?> ReadSettingsAsync(
        PrivilegeScanContext context, GroupPolicyTopology topology, IReadOnlyCollection<Guid> gpos, CancellationToken cancellationToken)
    {
        ISysvolConnection connection;
        try
        {
            connection = await _sysvol.OpenAsync(context.Session, cancellationToken).ConfigureAwait(false);
        }
        catch (SysvolUnavailableException ex)
        {
            context.Report(new PrivilegeSourceNotice(PrivilegeSource.GroupPolicy, Notices.SysvolUnavailable(ex.Reason), [context.Session.Info.Server]));
            return null;
        }

        var settings = new ConcurrentDictionary<Guid, GroupPolicyRightSettings>();
        var unreadable = new ConcurrentBag<string>();
        var invalid = new ConcurrentBag<string>();
        await using (connection.ConfigureAwait(false))
        {
            await Parallel.ForEachAsync(
                gpos,
                new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, _options.MaxConcurrentReads), CancellationToken = cancellationToken },
                async (id, ct) =>
                {
                    var policy = topology.Policies[id];
                    SecurityTemplate? template = null;
                    IReadOnlyList<GppLocalGroup> groups = [];
                    if (policy.UsesExtension(SecurityTemplate.ExtensionId))
                    {
                        template = await ReadAsync(connection, policy, SecurityTemplate.RelativePath, bytes => SecurityTemplate.Parse(bytes), unreadable, invalid, ct)
                            .ConfigureAwait(false);
                    }

                    if (policy.UsesExtension(GppGroupsParser.ExtensionId))
                    {
                        groups = await ReadAsync(connection, policy, GppGroupsParser.RelativePath, bytes => GppGroupsParser.Parse(bytes), unreadable, invalid, ct)
                            .ConfigureAwait(false) ?? [];
                    }

                    settings[id] = new GroupPolicyRightSettings(template, groups);
                }).ConfigureAwait(false);
        }

        if (!unreadable.IsEmpty)
        {
            context.Report(Notice(Notices.FilesUnreadable, unreadable));
        }

        if (!invalid.IsEmpty)
        {
            context.Report(Notice(Notices.FilesInvalid, invalid));
        }

        return new Dictionary<Guid, GroupPolicyRightSettings>(settings);
    }

    private async Task<T?> ReadAsync<T>(
        ISysvolConnection connection,
        GroupPolicyObjectInfo policy,
        string path,
        Func<byte[], T> parse,
        ConcurrentBag<string> unreadable,
        ConcurrentBag<string> invalid,
        CancellationToken cancellationToken)
        where T : class
    {
        var file = await connection.ReadGpoFileAsync(policy.Id, path, _options.MaxFileBytes, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        switch (file.Status)
        {
            case SysvolFileStatus.Read:
                try
                {
                    return parse(file.Content!);
                }
                catch (GpoParseException)
                {
                    invalid.Add(policy.DisplayName);
                    return null;
                }

            case SysvolFileStatus.NotFound:
                return null;
            case SysvolFileStatus.TooLarge:
                invalid.Add(policy.DisplayName);
                return null;
            default:
                unreadable.Add(policy.DisplayName);
                return null;
        }
    }

    /// <summary>sAMAccountName → objectSid of the names, in searches of <see cref="GroupPolicyGrantSourceOptions.NamesPerSearch"/>.</summary>
    private async Task<IReadOnlyDictionary<string, string>> ResolveNamesAsync(
        IDirectorySession session, IReadOnlyList<string> names, int pageSize, CancellationToken cancellationToken)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var chunk in names.Chunk(Math.Max(1, _options.NamesPerSearch)))
        {
            await foreach (var principal in session.SearchAsync(new DirectorySearch
            {
                BaseDn = session.Info.DefaultNamingContext,
                Scope = SearchScope.Subtree,
                Filter = LdapFilter.And(
                    LdapFilter.Present(Attr.ObjectSid),
                    LdapFilter.Or(chunk.Select(n => LdapFilter.Equal(Attr.SamAccountName, n)))),
                Attributes = [Attr.SamAccountName, Attr.ObjectSid],
                PageSize = pageSize,
            }, cancellationToken).ConfigureAwait(false))
            {
                if (principal.GetString(Attr.SamAccountName) is { } sam && principal.ObjectSid is { } sid)
                {
                    result.TryAdd(sam, sid);
                }
            }
        }

        return result;
    }

    /// <summary>A notice with the number of distinct names and the first <see cref="MaxNamesInNotice"/> of them.</summary>
    private static PrivilegeSourceNotice Notice(string code, IEnumerable<string> names)
    {
        var distinct = names.Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToList();
        var shown = string.Join(", ", distinct.Take(MaxNamesInNotice)) + (distinct.Count > MaxNamesInNotice ? ", …" : string.Empty);
        return new PrivilegeSourceNotice(PrivilegeSource.GroupPolicy, code, [Count(distinct.Count), shown]);
    }

    private static string Count(int count) => count.ToString(CultureInfo.InvariantCulture);

    private static string ParentOf(string dn) =>
        DistinguishedName.TryParse(dn, out var parsed) && parsed.Parent is { } parent ? parent.ToString() : dn;
}
