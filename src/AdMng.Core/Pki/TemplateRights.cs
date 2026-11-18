using System.Collections.Frozen;
using System.Globalization;
using AdMng.Core.ActiveDirectory;
using AdMng.Core.PrivilegedAccess;

namespace AdMng.Core.Pki;

/// <summary>An atomic right on a certificate template (docs/dev/p-certificate-services.md 2.3; <c>PkiTpl.Right.&lt;TemplateRight&gt;</c>).</summary>
public enum TemplateRight
{
    /// <summary>Control access with the <c>Certificate-Enrollment</c> extended right (or no object type).</summary>
    Enroll,

    /// <summary>Control access with the <c>Certificate-AutoEnrollment</c> extended right (or no object type).</summary>
    AutoEnroll,

    /// <summary>Every right (also yields Enroll, AutoEnroll, WriteDacl, WriteOwner and WriteAllProperties).</summary>
    FullControl,

    /// <summary>WRITE_DAC.</summary>
    WriteDacl,

    /// <summary>WRITE_OWNER.</summary>
    WriteOwner,

    /// <summary>Write property without an object type (GENERIC_WRITE normalizes into it).</summary>
    WriteAllProperties,

    /// <summary>Write property on one of <see cref="PkiAttributes.SensitiveTemplateAttributes"/> (or an unknown object type).</summary>
    WriteSensitiveProperty,

    /// <summary>Write property on another named attribute.</summary>
    WriteOtherProperty,

    /// <summary>The owner (implicit READ_CONTROL | WRITE_DAC, unless an OWNER RIGHTS ACE replaces them).</summary>
    Owner,
}

/// <summary>The class of a principal for the ESC review (decision 4; <c>Pki.Class.&lt;PrincipalClass&gt;</c>).</summary>
public enum PrincipalClass
{
    /// <summary>Tier 0 or infrastructure: no finding.</summary>
    Tier0,

    /// <summary>Everyone, Authenticated Users, Domain Users …: High / Critical.</summary>
    Broad,

    /// <summary>Any other account or group: Medium.</summary>
    Other,
}

/// <summary>One atomic right one ACE grants or denies, in DACL order.</summary>
/// <param name="Sid">The trustee.</param>
/// <param name="Right">The right.</param>
/// <param name="IsDeny">A deny ACE.</param>
/// <param name="Attribute">The attribute of a write-property right (or the GUID of an unknown object type), else null.</param>
public sealed record TemplateAce(string Sid, TemplateRight Right, bool IsDeny, string? Attribute)
{
    /// <summary>Right and kind only (never the SID or attribute).</summary>
    public override string ToString() => $"{nameof(TemplateAce)}({(IsDeny ? "deny" : "allow")} {Right})";
}

/// <summary>The decoded ACL of a template.</summary>
/// <param name="Owner">The owner SID, or null.</param>
/// <param name="Aces">The atomic rights in DACL order.</param>
/// <param name="IsEvaluated">False when the descriptor was missing or unparseable (<i>nieoceniony</i>, never "safe").</param>
public sealed record TemplateAcl(string? Owner, IReadOnlyList<TemplateAce> Aces, bool IsEvaluated)
{
    /// <summary>
    /// The DACL holds an effective OWNER RIGHTS (S-1-3-4) ACE: the owner's implicit READ_CONTROL | WRITE_DAC are replaced by
    /// what those ACEs grant (as in Windows), so <see cref="TemplateRight.Owner"/> is not held.
    /// </summary>
    public bool HasOwnerRights { get; init; }

    /// <summary>Counts only.</summary>
    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"{nameof(TemplateAcl)}({Aces.Count} rights{(IsEvaluated ? string.Empty : ", not evaluated")})");
}

/// <summary>An account the UI may name (one of the first 20 of a right).</summary>
/// <param name="Dn">Its DN.</param>
/// <param name="Name">Its name.</param>
/// <param name="IsDisabled">Disabled (counted apart).</param>
public sealed record PkiAccountRef(string Dn, string Name, bool IsDisabled)
{
    /// <summary>Whether it is disabled only.</summary>
    public override string ToString() => $"{nameof(PkiAccountRef)}({(IsDisabled ? "disabled" : "enabled")})";
}

/// <summary>A trustee that holds a right on a template (trustee level first, account counts after the expansion).</summary>
/// <param name="Sid">The trustee.</param>
/// <param name="Name">Its name (empty until the graph is there).</param>
/// <param name="Class">Its class (decision 4).</param>
/// <param name="Right">The right.</param>
/// <param name="AccountCount">Enabled accounts that hold the right through it; -1 until expanded.</param>
/// <param name="DisabledCount">Disabled accounts; -1 until expanded.</param>
/// <param name="FirstAccounts">The first 20 accounts.</param>
/// <param name="IsResolved">False for a trustee of another domain the window cannot expand (count <i>nieznana</i>).</param>
/// <param name="Attribute">The attribute of a write-property right, else null.</param>
public sealed record TemplateTrustee(
    string Sid,
    string? Name,
    PrincipalClass Class,
    TemplateRight Right,
    int AccountCount,
    int DisabledCount,
    IReadOnlyList<PkiAccountRef> FirstAccounts,
    bool IsResolved,
    string? Attribute)
{
    /// <summary>Class, right and counts only (never the SID, name or attribute).</summary>
    public override string ToString() => string.Create(CultureInfo.InvariantCulture,
        $"{nameof(TemplateTrustee)}({Class} {Right}, {AccountCount} accounts, {DisabledCount} disabled{(IsResolved ? string.Empty : ", unresolved")})");
}

/// <summary>The account-level total of one right on one template (after the expansion).</summary>
/// <param name="Right">The right.</param>
/// <param name="HighestClass">The most dangerous class among the accounts' trustees, null when nobody holds it.</param>
/// <param name="AccountCount">Enabled accounts that hold it.</param>
/// <param name="DisabledCount">Disabled accounts that hold it.</param>
/// <param name="FirstAccounts">The first 20 accounts.</param>
public sealed record TemplateRightTotal(TemplateRight Right, PrincipalClass? HighestClass, int AccountCount, int DisabledCount, IReadOnlyList<PkiAccountRef> FirstAccounts)
{
    /// <summary>Right, class and counts only.</summary>
    public override string ToString() => string.Create(CultureInfo.InvariantCulture,
        $"{nameof(TemplateRightTotal)}({Right} {HighestClass}, {AccountCount} accounts, {DisabledCount} disabled)");
}

/// <summary>Who holds which right on one template.</summary>
/// <param name="TemplateCn">The template's cn.</param>
/// <param name="TemplateGuid">Its objectGUID.</param>
/// <param name="IsEvaluated">False when its descriptor is missing or unparseable.</param>
/// <param name="Trustees">The trustees per right.</param>
/// <param name="Totals">The account-level totals (empty until expanded).</param>
public sealed record TemplateRights(string TemplateCn, Guid TemplateGuid, bool IsEvaluated, IReadOnlyList<TemplateTrustee> Trustees, IReadOnlyList<TemplateRightTotal> Totals)
{
    /// <summary>The GUID and counts only.</summary>
    public override string ToString() => string.Create(CultureInfo.InvariantCulture,
        $"{nameof(TemplateRights)} {TemplateGuid:B} ({Trustees.Count} trustees, {Totals.Count} totals{(IsEvaluated ? string.Empty : ", not evaluated")})");
}

/// <summary>An account that carries <c>altSecurityIdentities</c> (ESC14 and the readiness report).</summary>
/// <param name="Dn">Its DN.</param>
/// <param name="Name">Its name.</param>
/// <param name="Sid">Its objectSid.</param>
/// <param name="IsDisabled">Disabled.</param>
/// <param name="IsTier0">Reaches Tier 0 (decision 4).</param>
/// <param name="Mappings">The parsed mappings.</param>
public sealed record PkiAccountFact(string Dn, string Name, string Sid, bool IsDisabled, bool IsTier0, IReadOnlyList<AltSecurityIdentity> Mappings)
{
    /// <summary>Flags and the count only.</summary>
    public override string ToString() => string.Create(CultureInfo.InvariantCulture,
        $"{nameof(PkiAccountFact)}({(IsTier0 ? "Tier 0" : "not Tier 0")}, {(IsDisabled ? "disabled" : "enabled")}, {Mappings.Count} mappings)");
}

/// <summary>
/// The result of the one account pass (2.3): per template the trustees with their account counts, the mapped accounts,
/// the graph and options it used. In memory only (rule 1).
/// </summary>
/// <param name="Templates">By template cn (OrdinalIgnoreCase).</param>
/// <param name="MappedAccounts">Every account that carries <c>altSecurityIdentities</c>.</param>
/// <param name="AccountsScanned">Accounts streamed.</param>
/// <param name="Groups">The group graph of the pass.</param>
/// <param name="Options">The options of the pass (domain SIDs, levels).</param>
/// <param name="Notices">What it observed (<see cref="PkiReadNoticeCode.TrusteeUnresolved"/>).</param>
public sealed record PkiEnrollmentExpansion(
    IReadOnlyDictionary<string, TemplateRights> Templates,
    IReadOnlyList<PkiAccountFact> MappedAccounts,
    int AccountsScanned,
    GroupGraph Groups,
    PrivilegedAccessOptions Options,
    IReadOnlyList<PkiReadNotice> Notices)
{
    /// <summary>
    /// The classifier of the pass with the Tier 0 accounts it found (<see cref="PrincipalClassifier.Tier0Accounts"/>): a user
    /// named directly in an ACE who reaches Tier 0 is <see cref="PrincipalClass.Tier0"/> (the ESC reader classifies with it).
    /// </summary>
    public PrincipalClassifier? Classifier { get; init; }

    /// <summary>The distinct memoisation keys (token ∩ the SIDs the ACEs name) the pass evaluated.</summary>
    public int DistinctTokens { get; init; }

    /// <summary>Counts only.</summary>
    public override string ToString() => string.Create(CultureInfo.InvariantCulture,
        $"{nameof(PkiEnrollmentExpansion)}({Templates.Count} templates, {MappedAccounts.Count} mapped accounts, {AccountsScanned} accounts scanned, {Notices.Count} notices)");
}

/// <summary>What <see cref="TemplateRightsEvaluator.ExpandAsync"/> needs (the caller reads the graph once with <c>GroupGraph.ReadAsync</c>).</summary>
/// <param name="Groups">The group graph.</param>
/// <param name="Types">The object-type GUIDs (<see cref="AclObjectTypes.ReadAsync"/>).</param>
/// <param name="Classifier">The principal classes (decision 4).</param>
/// <param name="Options">Domain SIDs, levels, page size.</param>
/// <param name="FirstAccounts">How many accounts per right are named (20).</param>
public sealed record PkiExpansionContext(GroupGraph Groups, AclObjectTypes Types, PrincipalClassifier Classifier, PrivilegedAccessOptions Options, int FirstAccounts = 20)
{
    /// <summary>The limit only.</summary>
    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"{nameof(PkiExpansionContext)}(first {FirstAccounts})");
}

/// <summary>Progress of the account pass (every 500 accounts).</summary>
/// <param name="Accounts">Accounts streamed so far.</param>
public sealed record PkiExpansionProgress(int Accounts)
{
    /// <summary>The count only.</summary>
    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"{Accounts} accounts");
}

/// <summary>
/// Decision 4 of docs/dev/p-certificate-services.md: classifies a SID as <see cref="PrincipalClass.Tier0"/> (the catalogue
/// Tier 0 groups and whatever reaches one through the graph, SYSTEM, S-1-5-9, PRINCIPAL SELF, CREATOR OWNER,
/// Administrators, the RIDs 498/500/512/516/517/518/519/521/526/527 of any forest domain, the CA computers),
/// <see cref="PrincipalClass.Broad"/> (Everyone, Authenticated Users, Anonymous, This Organization, Interactive, Network,
/// Users, Guests, the RIDs 513/514/515 of any domain) or <see cref="PrincipalClass.Other"/>. Without a graph it uses the
/// SID and RID rules only. Shared by 2.3, 2.4 and the ESC5/ESC14 trustees.
/// </summary>
public sealed class PrincipalClassifier
{
    /// <summary>SYSTEM, Enterprise Domain Controllers, PRINCIPAL SELF, CREATOR OWNER, Administrators.</summary>
    private static readonly FrozenSet<string> Tier0Sids =
        new[] { "S-1-5-18", "S-1-5-9", "S-1-5-10", "S-1-3-0", "S-1-5-32-544" }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>Everyone, Authenticated Users, Anonymous, This Organization, Interactive, Network, Users, Guests.</summary>
    private static readonly FrozenSet<string> BroadSids =
        new[] { "S-1-1-0", "S-1-5-11", "S-1-5-7", "S-1-5-15", "S-1-5-4", "S-1-5-2", "S-1-5-32-545", "S-1-5-32-546" }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The Tier 0 RIDs of any forest domain: Administrator, Domain Admins, Domain Controllers, Cert Publishers, Schema
    /// Admins, Enterprise Admins, Read-only DCs, Enterprise Read-only DCs, Key Admins, Enterprise Key Admins.
    /// </summary>
    private static readonly FrozenSet<uint> Tier0Rids = new[]
    {
        WellKnownRids.Administrator, WellKnownRids.DomainAdmins, WellKnownRids.DomainControllers, WellKnownRids.CertPublishers,
        WellKnownRids.SchemaAdmins, WellKnownRids.EnterpriseAdmins, WellKnownRids.ReadOnlyDomainControllers,
        WellKnownRids.EnterpriseReadOnlyDomainControllers, WellKnownRids.KeyAdmins, WellKnownRids.EnterpriseKeyAdmins,
    }.ToFrozenSet();

    /// <summary>Domain Users, Domain Guests, Domain Computers of any domain.</summary>
    private static readonly FrozenSet<uint> BroadRids = new[] { WellKnownRids.DomainUsers, WellKnownRids.DomainGuests, WellKnownRids.DomainComputers }.ToFrozenSet();

    private readonly HashSet<string> _caComputers;
    private HashSet<string> _tier0Accounts = new(StringComparer.OrdinalIgnoreCase);
    private readonly Lazy<HashSet<GroupNode>> _tier0Groups;

    /// <param name="domainSid">The window's domain SID.</param>
    /// <param name="rootDomainSid">The forest root's domain SID.</param>
    /// <param name="options">The privileged access options (catalogue levels).</param>
    /// <param name="groups">The group graph, null before the expansion read it.</param>
    /// <param name="caComputerSids">The SIDs of the enterprise CA computers (Tier 0).</param>
    public PrincipalClassifier(string? domainSid, string? rootDomainSid, PrivilegedAccessOptions options, GroupGraph? groups, IReadOnlyCollection<string> caComputerSids)
    {
        DomainSid = domainSid;
        RootDomainSid = rootDomainSid;
        Options = options ?? throw new ArgumentNullException(nameof(options));
        Groups = groups;
        CaComputerSids = caComputerSids ?? throw new ArgumentNullException(nameof(caComputerSids));
        _caComputers = new HashSet<string>(caComputerSids.Where(s => !string.IsNullOrEmpty(s)), StringComparer.OrdinalIgnoreCase);
        _tier0Groups = new Lazy<HashSet<GroupNode>>(ReachingTier0, LazyThreadSafetyMode.ExecutionAndPublication);
    }

    public string? DomainSid { get; }

    public string? RootDomainSid { get; }

    public PrivilegedAccessOptions Options { get; }

    public GroupGraph? Groups { get; }

    public IReadOnlyCollection<string> CaComputerSids { get; }

    /// <summary>
    /// Accounts the account pass found to reach Tier 0 (the graph holds groups only, so a user or computer named directly in
    /// an ACE is Tier 0 only through this list; <see cref="TemplateRightsEvaluator.ExpandAsync"/> fills it).
    /// </summary>
    public IReadOnlyCollection<string> Tier0Accounts
    {
        get => _tier0Accounts;
        init => _tier0Accounts = new HashSet<string>((value ?? []).Where(s => !string.IsNullOrEmpty(s)), StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>The class of <paramref name="sid"/>.</summary>
    public PrincipalClass Classify(string sid)
    {
        if (string.IsNullOrWhiteSpace(sid))
        {
            return PrincipalClass.Other;
        }

        if (Tier0Sids.Contains(sid))
        {
            return PrincipalClass.Tier0;
        }

        if (BroadSids.Contains(sid))
        {
            return PrincipalClass.Broad;
        }

        if (_caComputers.Contains(sid) || _tier0Accounts.Contains(sid))
        {
            return PrincipalClass.Tier0;
        }

        if (DomainRid(sid) is { } rid)
        {
            if (Tier0Rids.Contains(rid))
            {
                return PrincipalClass.Tier0;
            }

            if (BroadRids.Contains(rid))
            {
                return PrincipalClass.Broad;
            }
        }

        var node = Groups?.FindBySid(sid);
        if (IsCatalogueTier0(sid, node?.SamAccountName) || (node is not null && _tier0Groups.Value.Contains(node)))
        {
            return PrincipalClass.Tier0;
        }

        return PrincipalClass.Other;
    }

    /// <summary>The name of a group of the graph, or null (well-known principals and accounts have none here).</summary>
    internal string? NameOf(string sid) => Groups?.FindBySid(sid)?.Name;

    /// <summary>The RID of a SID of the form S-1-5-21-&lt;domain&gt;-&lt;RID&gt; (any domain), else null.</summary>
    internal static uint? DomainRid(string sid) =>
        sid.StartsWith("S-1-5-21-", StringComparison.OrdinalIgnoreCase)
        && AdConvert.GetRid(sid) is { } rid
        && PrivilegedGroupCatalog.IsDomainSid(sid[..sid.LastIndexOf('-')])
            ? rid
            : null;

    /// <summary>The SID belongs to another domain than the window's (it cannot be expanded here).</summary>
    internal bool IsOtherDomain(string sid) =>
        DomainSid is { } domain
        && DomainRid(sid) is not null
        && !string.Equals(sid[..sid.LastIndexOf('-')], domain, StringComparison.OrdinalIgnoreCase);

    private bool IsCatalogueTier0(string? sid, string? samAccountName) =>
        PrivilegedGroupCatalog.Match(sid, samAccountName, DomainSid, RootDomainSid) is { } definition && Options.LevelOf(definition) == PrivilegeLevel.Tier0;

    /// <summary>Tier 0 by the SID rules or the catalogue alone (the seeds of the graph closure).</summary>
    private bool IsDirectTier0(GroupNode node) =>
        node.Sid is { } sid
        && (Tier0Sids.Contains(sid) || _caComputers.Contains(sid) || (DomainRid(sid) is { } rid && Tier0Rids.Contains(rid)) || IsCatalogueTier0(sid, node.SamAccountName));

    /// <summary>Every group that is Tier 0 or reaches a Tier 0 group through memberOf (one downward pass over the graph).</summary>
    private HashSet<GroupNode> ReachingTier0()
    {
        var result = new HashSet<GroupNode>();
        if (Groups is not { } graph)
        {
            return result;
        }

        var children = new Dictionary<GroupNode, List<GroupNode>>();
        foreach (var node in graph.Nodes)
        {
            foreach (var parent in node.Parents)
            {
                if (!children.TryGetValue(parent, out var list))
                {
                    children[parent] = list = [];
                }

                list.Add(node);
            }
        }

        var queue = new Queue<GroupNode>();
        foreach (var node in graph.Nodes.Where(n => !n.IsImplicit && IsDirectTier0(n)))
        {
            if (result.Add(node))
            {
                queue.Enqueue(node);
            }
        }

        while (queue.Count > 0)
        {
            foreach (var child in children.GetValueOrDefault(queue.Dequeue()) ?? [])
            {
                if (!child.IsImplicit && result.Add(child))
                {
                    queue.Enqueue(child);
                }
            }
        }

        return result;
    }
}
