using System.Collections;
using System.Globalization;
using AdMng.Core.GroupPolicy.Management;

namespace AdMng.Core.GroupPolicy.Diagnostics;

/// <summary>How much "Sprawdź zasady grupy" reads (design note docs/dev/k-computers-and-gpo.md 20.2).</summary>
public enum GpoDiagnosticsDepth
{
    /// <summary>LDAP (GPOs, links, WMI filters) and the <c>GPT.INI</c> of each GPO.</summary>
    Quick,

    /// <summary>Quick plus the SYSVOL folders, the folder ACLs, the object counts, the SID resolution and the conflicts.</summary>
    Full,
}

/// <summary>What to check: one GPO or the whole domain, quickly or fully.</summary>
/// <param name="GpoId">One GPO (its links, its folder), or null for every GPO of the session's domain.</param>
/// <param name="Depth">Quick or full.</param>
public sealed record GpoDiagnosticsRequest(Guid? GpoId = null, GpoDiagnosticsDepth Depth = GpoDiagnosticsDepth.Quick);

/// <summary>The step a diagnostics read is at.</summary>
public enum GpoDiagnosticsStage
{
    /// <summary>The GPOs, the linked containers and the WMI filters.</summary>
    Directory,

    /// <summary>The <c>GPT.INI</c> versions.</summary>
    Versions,

    /// <summary>The SYSVOL folders and their ACLs (<see cref="GpoDiagnosticsProgress.Done"/> of <see cref="GpoDiagnosticsProgress.Total"/> folders).</summary>
    Folders,

    /// <summary>The users and computers below the linked containers.</summary>
    Objects,

    /// <summary>The domain SIDs of the permissions, rights and groups.</summary>
    Principals,

    /// <summary>The read is complete.</summary>
    Completed,
}

/// <summary>Progress of <see cref="GroupPolicyDiagnosticsReader.ReadAsync"/>.</summary>
/// <param name="Stage">The current step.</param>
/// <param name="Done">Items of the step done (folders, objects, SIDs).</param>
/// <param name="Total">Items of the step (0 when not known in advance).</param>
public sealed record GpoDiagnosticsProgress(GpoDiagnosticsStage Stage, int Done = 0, int Total = 0);

/// <summary>How serious a finding is.</summary>
public enum GpoFindingSeverity
{
    Info,
    Low,
    Medium,
    High,
    Critical,
}

/// <summary>What a finding is about.</summary>
public enum GpoFindingArea
{
    /// <summary>Links of GPOs (gPLink) and the GPO status as far as links are concerned.</summary>
    Links,

    /// <summary>Which objects a GPO reaches (parts enabled, users and computers below the links, loopback, account policy).</summary>
    Scope,

    /// <summary>Security filtering, WMI filters and the GPO's permissions.</summary>
    Filtering,

    /// <summary>AD and GPT.INI versions.</summary>
    Versions,

    /// <summary>The SYSVOL folder: existence, path, ACL, extension names, unreadable files, leftovers.</summary>
    Folder,

    /// <summary>Logical errors in the settings of a GPO.</summary>
    Content,

    /// <summary>Several GPOs reaching one container set one setting.</summary>
    Conflict,

    /// <summary>Weak security settings and dangerous permissions.</summary>
    Security,
}

/// <summary>The data a group of rules needs; a group whose data was not read is reported as not evaluated.</summary>
public enum GpoRuleGroup
{
    /// <summary>LDAP only (always evaluated).</summary>
    Directory,

    /// <summary>The <c>GPT.INI</c> versions (Quick and Full, needs SYSVOL).</summary>
    SysvolVersions,

    /// <summary>The SYSVOL folders: their files, ACLs and settings (Full, needs a SYSVOL connection that lists folders).</summary>
    Folders,

    /// <summary>The users and computers below the links (Full).</summary>
    ObjectCounts,

    /// <summary>The domain SIDs of permissions, rights and groups resolved (Full).</summary>
    Principals,

    /// <summary>The settings of the GPOs reaching each linked container compared (Full, needs the folders).</summary>
    Conflicts,
}

/// <summary>
/// One finding of <see cref="GroupPolicyDiagnostics.Analyze"/>. Directory data (names, DNs, SIDs, values) lives in the
/// properties and <see cref="Arguments"/> for the window that shows it, in memory only (CLAUDE.md rule 1);
/// <see cref="ToString"/> carries none of it.
/// </summary>
/// <param name="Code">One of <see cref="GpoFindingCodes"/>.</param>
/// <param name="Severity">How serious it is.</param>
/// <param name="Area">What it is about.</param>
/// <param name="GpoId">The GPO (for a conflict: the winning GPO); null for a container or a WMI filter.</param>
/// <param name="ContainerDn">The domain head, OU or site the finding is about (links, conflicts).</param>
/// <param name="SettingKey">The normalized setting key (<c>Computer/Security/System Access/MinimumPasswordLength</c>, <c>File/&lt;path&gt;</c> …).</param>
/// <param name="Arguments">The message arguments of the code (documented per code in <see cref="GpoFindingCodes"/>).</param>
/// <param name="Fix">The repair through the change plan, null when there is none.</param>
public sealed record GpoFinding(
    string Code,
    GpoFindingSeverity Severity,
    GpoFindingArea Area,
    Guid? GpoId,
    string? ContainerDn,
    string? SettingKey,
    IReadOnlyList<string> Arguments,
    GpoQuickFix? Fix)
{
    /// <summary>The name of <see cref="GpoId"/> (its GUID in braces when it does not exist).</summary>
    public string? GpoName { get; init; }

    /// <summary>The other GPOs of the finding (a conflict's losers, a redundant setting's other sources), in precedence order.</summary>
    public IReadOnlyList<Guid> RelatedGpoIds { get; init; } = [];

    /// <summary>For diagnostics: the code, severity, area and GPO GUID only - never names, DNs, keys or values.</summary>
    public override string ToString() => string.Create(CultureInfo.InvariantCulture,
        $"{Code} {Severity} {Area}{(GpoId is { } id ? " " + SysvolPaths.FolderName(id) : string.Empty)}");
}

/// <summary>
/// A repair a finding offers; each record maps to one planner call of <see cref="GroupPolicyPlanner"/> whose plan the
/// operator previews (CLAUDE.md rule 5). <see cref="ToString"/> names the kind only (no DNs or SIDs).
/// </summary>
public abstract record GpoQuickFix
{
    private GpoQuickFix()
    {
    }

    /// <summary>For diagnostics: the kind of repair only.</summary>
    public sealed override string ToString() => GetType().Name;

    /// <summary>Remove the link (<see cref="GroupPolicyPlanner.PlanUnlinkAsync"/>).</summary>
    public sealed record Unlink(string ContainerDn, Guid GpoId) : GpoQuickFix;

    /// <summary>Enable the link (<see cref="GroupPolicyPlanner.PlanLinkOptionsAsync"/> with enabled = true).</summary>
    public sealed record EnableLink(string ContainerDn, Guid GpoId) : GpoQuickFix;

    /// <summary>
    /// Keep one link of a GPO linked several times in one container - the one with the highest precedence, with its options
    /// (<see cref="GroupPolicyPlanner.PlanRemoveDuplicateLinksAsync"/>; a plain unlink would remove every link).
    /// </summary>
    public sealed record RemoveDuplicateLinks(string ContainerDn, Guid GpoId) : GpoQuickFix;

    /// <summary>Set the GPO status (<see cref="GroupPolicyPlanner.PlanStatusAsync"/>).</summary>
    public sealed record SetStatus(Guid GpoId, GpoStatus Status) : GpoQuickFix;

    /// <summary>
    /// Give a trustee <i>Read</i> on the GPO - MS16-072: Authenticated Users (<see cref="GroupPolicyPlanner.PlanSetDelegationAsync"/>
    /// with <see cref="GpoPermissionLevel.Read"/>).
    /// </summary>
    public sealed record GrantRead(Guid GpoId, string Sid) : GpoQuickFix;

    /// <summary>Unlink the GPO's WMI filter (<see cref="GroupPolicyPlanner.PlanWmiFilterLinkAsync"/> with null).</summary>
    public sealed record ClearWmiFilter(Guid GpoId) : GpoQuickFix;

    /// <summary>Delete a WMI filter (<see cref="GroupPolicyPlanner.PlanDeleteWmiFilterAsync"/>).</summary>
    public sealed record DeleteWmiFilter(string Dn) : GpoQuickFix;

    /// <summary>Align AD and GPT.INI versions (<see cref="GroupPolicyPlanner.PlanAlignVersionsAsync"/>).</summary>
    public sealed record AlignVersions(Guid GpoId) : GpoQuickFix;

    /// <summary>Map the folder ACL from the GPO's permissions again (<see cref="GroupPolicyPlanner.PlanRepairFolderSecurityAsync"/>).</summary>
    public sealed record RepairFolderSecurity(Guid GpoId) : GpoQuickFix;

    /// <summary>Remove every explicit ACE of a trustee (<see cref="GroupPolicyPlanner.PlanRemoveDelegationAsync"/>).</summary>
    public sealed record RemoveTrustee(Guid GpoId, string Sid) : GpoQuickFix;

    /// <summary>Add the extension pairs of the files the folder holds (<see cref="GroupPolicyPlanner.PlanRepairExtensionNamesAsync"/>).</summary>
    public sealed record RepairExtensionNames(Guid GpoId) : GpoQuickFix;
}

/// <summary>
/// The findings of <see cref="GroupPolicyDiagnostics.Analyze"/> (most severe first) with what could not be checked: the
/// rule groups whose data the snapshot does not hold and the GPOs whose folder could not be read.
/// </summary>
public sealed class GpoDiagnosticsResult : IReadOnlyList<GpoFinding>
{
    private readonly IReadOnlyList<GpoFinding> _findings;

    public GpoDiagnosticsResult(IReadOnlyList<GpoFinding> findings, IReadOnlyList<GpoRuleGroup> notEvaluated, IReadOnlyList<Guid> foldersNotRead)
    {
        _findings = findings ?? throw new ArgumentNullException(nameof(findings));
        NotEvaluated = notEvaluated ?? throw new ArgumentNullException(nameof(notEvaluated));
        FoldersNotRead = foldersNotRead ?? throw new ArgumentNullException(nameof(foldersNotRead));
    }

    /// <summary>The rule groups that were not evaluated (Quick depth, no SYSVOL, a failed lookup).</summary>
    public IReadOnlyList<GpoRuleGroup> NotEvaluated { get; }

    /// <summary>GPOs in scope whose folder exists but could not be read (access denied, too large, failed): their folder and content rules were skipped.</summary>
    public IReadOnlyList<Guid> FoldersNotRead { get; }

    public int Count => _findings.Count;

    public GpoFinding this[int index] => _findings[index];

    public IEnumerator<GpoFinding> GetEnumerator() => _findings.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    /// <summary>For diagnostics: counts only.</summary>
    public override string ToString() => string.Create(CultureInfo.InvariantCulture,
        $"{_findings.Count} findings, {NotEvaluated.Count} rule groups not evaluated, {FoldersNotRead.Count} folders not read");
}
