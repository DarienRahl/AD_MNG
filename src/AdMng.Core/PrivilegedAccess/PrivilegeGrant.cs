using System.Collections.Concurrent;
using AdMng.Core.Directory;
using AdMng.Core.Filters;

namespace AdMng.Core.PrivilegedAccess;

/// <summary>
/// A right granted to a principal - a group or an account, by SID or DN. The evaluator attaches it to every account that
/// is the principal or reaches it through nested membership, with the path. U4a grants the catalogue groups and the
/// operator's marks; U4b grants ACL rights (principal = trustee of the ACE, scope = the container), U4c local rights
/// from GPOs (principal = member of the local group, scope = the computers of the linked OU).
/// </summary>
public sealed record PrivilegeGrant
{
    public required PrivilegeSource Source { get; init; }

    /// <summary>The kind of the finding of a member (<see cref="PrivilegeFindingKind.MarkedGroup"/> becomes
    /// <see cref="PrivilegeFindingKind.MarkedAccount"/> when the principal is the account itself).</summary>
    public required PrivilegeFindingKind Kind { get; init; }

    public required PrivilegeLevel Level { get; init; }

    public required PrivilegeScopeKind Scope { get; init; }

    public string? ScopeDn { get; init; }

    /// <summary>The name of <see cref="ScopeDn"/> when it is not its RDN value (the display name of a GPO).</summary>
    public string? ScopeName { get; init; }

    /// <summary>The principal's objectSid (preferred: survives renames and moves).</summary>
    public string? PrincipalSid { get; init; }

    /// <summary>The principal's DN (when no SID is known).</summary>
    public string? PrincipalDn { get; init; }

    public string? CatalogId { get; init; }

    public IReadOnlyList<string> Values { get; init; } = [];

    public string? Note { get; init; }

    /// <summary>U4c: the GPO, link and mechanism of a <see cref="PrivilegeFindingKind.LocalRight"/>.</summary>
    public GroupPolicyGrantDetails? GroupPolicy { get; init; }
}

/// <summary>A group or account the operator marked as privileged (settings: SID and/or DN, level, note - nothing else).</summary>
/// <param name="Sid">objectSid of the principal.</param>
/// <param name="Dn">DN of the principal (resolved, not portable).</param>
/// <param name="Level">The level its members (or the account) get.</param>
/// <param name="Note">Why it is privileged (shown with the finding).</param>
public sealed record PrivilegeMark(string? Sid, string? Dn, PrivilegeLevel Level, string? Note);

/// <summary>What a "mark as privileged" filter rule applies to.</summary>
public enum PrivilegeRuleTarget
{
    /// <summary>Accounts matching the filter are privileged themselves.</summary>
    Accounts,

    /// <summary>Groups matching the filter are privileged: their (nested) members get the finding.</summary>
    Groups,
}

/// <summary>A saved filter used as "mark as privileged with level X" (README U4, use (c) of the saved filters).</summary>
/// <param name="Name">Name of the saved filter (shown with the finding).</param>
/// <param name="Filter">The filter.</param>
/// <param name="Level">The level of the finding.</param>
/// <param name="Target">Accounts or groups.</param>
/// <param name="Note">The operator's note.</param>
public sealed record PrivilegeConditionRule(string Name, FilterDefinition Filter, PrivilegeLevel Level, PrivilegeRuleTarget Target, string? Note = null);

/// <summary>Everything a privileged access run is configured with (from the settings; nothing of it is directory data).</summary>
public sealed record PrivilegedAccessOptions
{
    /// <summary>SID of the session's domain (catalogue RIDs, primary groups). Without it only built-in SIDs are recognised.</summary>
    public string? DomainSid { get; init; }

    /// <summary>SID of the forest root domain; null = the forest-root RIDs match in any domain.</summary>
    public string? RootDomainSid { get; init; }

    /// <summary>Levels the operator set for catalogue groups (<see cref="PrivilegedGroupDefinition.Id"/> → level).</summary>
    public IReadOnlyDictionary<string, PrivilegeLevel> LevelOverrides { get; init; } = new Dictionary<string, PrivilegeLevel>();

    public IReadOnlyList<PrivilegeMark> Marks { get; init; } = [];

    public IReadOnlyList<PrivilegeConditionRule> Rules { get; init; } = [];

    /// <summary>Computer accounts (and domain controllers) are reviewed too.</summary>
    public bool IncludeComputers { get; init; } = true;

    /// <summary>Managed service accounts (MSA, gMSA, dMSA) are reviewed too.</summary>
    public bool IncludeServiceAccounts { get; init; } = true;

    public PeerOptions Peers { get; init; } = new();

    /// <summary>
    /// U4b: the ACL delegations source also reads the security descriptors of every user, group and computer (explicit
    /// ACEs on single objects); slower. Off: containers, GPOs and Tier 0 objects only.
    /// </summary>
    public bool DeepAclScan { get; init; }

    public int PageSize { get; init; } = 500;

    /// <summary>The level of a catalogue group: the operator's override or the default.</summary>
    public PrivilegeLevel LevelOf(PrivilegedGroupDefinition group)
    {
        ArgumentNullException.ThrowIfNull(group);
        return LevelOverrides.TryGetValue(group.Id, out var level) ? PrivilegeLevels.Normalize(level) : group.DefaultLevel;
    }
}

/// <summary>What a grant source needs (the graph is read before the sources run).</summary>
public sealed class PrivilegeScanContext
{
    private readonly ConcurrentQueue<PrivilegeSourceNotice> _notices = new();

    public required IDirectorySession Session { get; init; }

    public required GroupGraph Groups { get; init; }

    public required PrivilegedAccessOptions Options { get; init; }

    public required DateTimeOffset Now { get; init; }

    /// <summary>What the sources reported while collecting (in the order reported).</summary>
    public IReadOnlyList<PrivilegeSourceNotice> Notices => [.. _notices];

    /// <summary>
    /// A source tells the operator that part of its data could not be read or evaluated (the run goes on; the tab shows it).
    /// Thread-safe.
    /// </summary>
    public void Report(PrivilegeSourceNotice notice)
    {
        ArgumentNullException.ThrowIfNull(notice);
        _notices.Enqueue(notice);
    }
}

/// <summary>
/// Something a grant source could not read or evaluate (SYSVOL unavailable, unreadable GPO files, unresolved names): the
/// UI shows it with the resource <c>PrivilegedAccess.Notice.{Code}</c> formatted with <see cref="Arguments"/>. The
/// arguments are names of GPOs and counts - never file contents.
/// </summary>
/// <param name="Source">The source that reported it.</param>
/// <param name="Code">Stable code (resource key suffix), e.g. <c>GroupPolicy.SysvolUnavailable.LogonFailure</c>.</param>
/// <param name="Arguments">The format arguments.</param>
public sealed record PrivilegeSourceNotice(PrivilegeSource Source, string Code, IReadOnlyList<string> Arguments);

/// <summary>
/// A source of rights granted to principals (README U4: U4b ACL delegations, U4c local rights from GPOs). A source reads
/// what it needs once per run (no per-account round trips) and returns grants; the evaluator expands them to accounts.
/// </summary>
public interface IPrivilegeGrantSource
{
    PrivilegeSource Source { get; }

    /// <summary>Collects the grants of a run.</summary>
    Task<IReadOnlyList<PrivilegeGrant>> CollectAsync(PrivilegeScanContext context, CancellationToken cancellationToken);
}

/// <summary>Source 1: the groups of <see cref="PrivilegedGroupCatalog"/> found in the graph, with the operator's levels.</summary>
public sealed class BuiltInGroupGrantSource : IPrivilegeGrantSource
{
    public PrivilegeSource Source => PrivilegeSource.Groups;

    public Task<IReadOnlyList<PrivilegeGrant>> CollectAsync(PrivilegeScanContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var options = context.Options;
        var grants = new List<PrivilegeGrant>();
        foreach (var node in context.Groups.Nodes.Where(n => !n.IsImplicit))
        {
            if (PrivilegedGroupCatalog.Match(node.Sid, node.SamAccountName, options.DomainSid, options.RootDomainSid) is { } group)
            {
                grants.Add(new PrivilegeGrant
                {
                    Source = PrivilegeSource.Groups,
                    Kind = PrivilegeFindingKind.BuiltInGroup,
                    Level = options.LevelOf(group),
                    Scope = group.Scope,
                    PrincipalSid = node.Sid,
                    PrincipalDn = node.Dn,
                    CatalogId = group.Id,
                });
            }
        }

        return Task.FromResult<IReadOnlyList<PrivilegeGrant>>(grants);
    }
}

/// <summary>Source 1 (operator): the groups and accounts marked in the settings.</summary>
public sealed class MarkedPrincipalGrantSource : IPrivilegeGrantSource
{
    public PrivilegeSource Source => PrivilegeSource.OperatorRules;

    public Task<IReadOnlyList<PrivilegeGrant>> CollectAsync(PrivilegeScanContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        IReadOnlyList<PrivilegeGrant> grants = [.. context.Options.Marks
            .Where(m => !string.IsNullOrWhiteSpace(m.Sid) || !string.IsNullOrWhiteSpace(m.Dn))
            .Select(m => new PrivilegeGrant
            {
                Source = PrivilegeSource.OperatorRules,
                Kind = PrivilegeFindingKind.MarkedGroup,
                Level = PrivilegeLevels.Normalize(m.Level),
                Scope = PrivilegeScopeKind.Domain,
                PrincipalSid = string.IsNullOrWhiteSpace(m.Sid) ? null : m.Sid.Trim(),
                PrincipalDn = string.IsNullOrWhiteSpace(m.Dn) ? null : m.Dn.Trim(),
                Note = m.Note,
            })];
        return Task.FromResult(grants);
    }
}
