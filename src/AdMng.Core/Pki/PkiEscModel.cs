using System.Collections;
using System.Globalization;
using AdMng.Core.PrivilegedAccess;

namespace AdMng.Core.Pki;

/// <summary>How serious a finding is (<c>PkiEsc.Severity.&lt;PkiFindingSeverity&gt;</c>).</summary>
public enum PkiFindingSeverity
{
    Info,
    Low,
    Medium,
    High,
    Critical,
}

/// <summary>What a finding is about (<c>PkiEsc.Area.&lt;PkiFindingArea&gt;</c>).</summary>
public enum PkiFindingArea
{
    /// <summary>A certificate template.</summary>
    Template,

    /// <summary>An enterprise CA.</summary>
    CertificationAuthority,

    /// <summary>A PKI container, NTAuth, an OID object, a CA / AIA / CDP object or a CA computer.</summary>
    PkiObject,

    /// <summary>An account (explicit mappings, ESC14).</summary>
    Account,
}

/// <summary>The data a rule group needs; a group whose data was not read is reported as not evaluated.</summary>
public enum PkiRuleGroup
{
    /// <summary>The templates and the trustee-level rights (ESC1, 2, 3, 9, 13, 15, 17).</summary>
    Enrollment,

    /// <summary>The ACLs of the PKI objects (<c>CollectPublicKeyAsync</c>; ESC4, ESC5).</summary>
    AccessControl,

    /// <summary>The mapped accounts of the account pass and the Tier 0 account ACLs (ESC14).</summary>
    Mapping,
}

/// <summary>Notes a finding carries (<c>PkiEsc.Note.&lt;flag&gt;</c>, one resource per flag).</summary>
[Flags]
public enum PkiFindingNotes
{
    None = 0,

    /// <summary>No enterprise CA publishes the template: one level lower, exploitable only once published.</summary>
    NotPublished = 1,

    /// <summary>Assumes every CA grants Enroll to Authenticated Users (decision 3; the CA's own security is P4).</summary>
    AssumesCaEnroll = 2,

    /// <summary>Depends on the CA's November 2024 patch (ESC15, confirmed in P4).</summary>
    PendingCaPatch = 4,

    /// <summary>Manager approval or an authorized signature lowers the finding one level.</summary>
    ApprovalRequired = 8,

    /// <summary><c>pKIExtendedKeyUsage</c> and the application policies disagree.</summary>
    EkuSourcesDiffer = 16,

    /// <summary>A write right on an object type nothing names (counted sensitive).</summary>
    UnknownAttribute = 32,

    /// <summary>An ESC3 target template without an enrollment-agent template anybody can use.</summary>
    TargetOnly = 64,
}

/// <summary>How manager approval / an authorized signature affects a rule (2.4, the <i>Approval</i> column).</summary>
public enum PkiApprovalEffect
{
    /// <summary>No finding (Certipy and Locksmith agree).</summary>
    Suppresses,

    /// <summary>One level lower with <see cref="PkiFindingNotes.ApprovalRequired"/>.</summary>
    Lowers,

    /// <summary>The attacker rewrites the template or the account: approval does not protect.</summary>
    Never,
}

/// <summary>An account or group a finding names, with its count (README: <i>konta i grupy z liczbą kont</i>).</summary>
/// <param name="Sid">The trustee.</param>
/// <param name="Name">Its name, or null when unresolved.</param>
/// <param name="Class">Its class (decision 4).</param>
/// <param name="AccountCount">Accounts behind it (-1: not expanded / unknown).</param>
/// <param name="IsResolved">False for a trustee of another domain the window cannot expand.</param>
/// <param name="Right">The right it holds (a <see cref="TemplateRight"/> or <see cref="AclRight"/> name), or null.</param>
public sealed record PkiPrincipal(string Sid, string? Name, PrincipalClass Class, int AccountCount, bool IsResolved, string? Right)
{
    /// <summary>Class and count only (never the SID or name).</summary>
    public override string ToString() => string.Create(CultureInfo.InvariantCulture,
        $"{nameof(PkiPrincipal)}({Class}, {AccountCount} accounts{(IsResolved ? string.Empty : ", unresolved")})");
}

/// <summary>The group an OID object links (<c>msDS-OIDToGroupLink</c>) resolved against the graph (ESC13).</summary>
/// <param name="Dn">The group's DN.</param>
/// <param name="Name">Its name.</param>
/// <param name="Level">Its privilege level (Tier 0 → Critical).</param>
public sealed record PkiLinkedGroup(string Dn, string? Name, PrivilegeLevel Level)
{
    /// <summary>The level only.</summary>
    public override string ToString() => $"{nameof(PkiLinkedGroup)}({Level})";
}

/// <summary>
/// One finding of <see cref="PkiEscAnalyzer.Analyze"/> (2.4). Directory data lives in the properties and
/// <see cref="Arguments"/> for the window that shows it, in memory only (rule 1); <see cref="ToString"/> carries none of it.
/// </summary>
/// <param name="Code">One of <see cref="PkiFindingCodes"/>.</param>
/// <param name="Severity">Its severity after the approval / publication adjustments.</param>
/// <param name="Area">What it is about.</param>
/// <param name="ObjectId">The objectGUID of the template, CA, PKI object or account.</param>
/// <param name="Arguments">The message arguments of the code (documented per code in <see cref="PkiFindingCodes"/>).</param>
public sealed record PkiFinding(string Code, PkiFindingSeverity Severity, PkiFindingArea Area, Guid? ObjectId, IReadOnlyList<string> Arguments)
{
    /// <summary>The DN of the affected object (navigation: <i>Otwórz szablon/urząd</i>).</summary>
    public string? ObjectDn { get; init; }

    /// <summary>The accounts and groups with their counts.</summary>
    public IReadOnlyList<PkiPrincipal> Principals { get; init; } = [];

    /// <summary>The notes.</summary>
    public PkiFindingNotes Notes { get; init; }

    /// <summary>The CAs, OID objects and target templates of the finding.</summary>
    public IReadOnlyList<Guid> RelatedObjectIds { get; init; } = [];

    /// <summary>Code, severity, area and the object GUID in braces – never a name, DN, subject or argument.</summary>
    public override string ToString() =>
        $"{Code} {Severity} {Area}{(ObjectId is { } id ? " " + id.ToString("B") : string.Empty)}";
}

/// <summary>What the ESC analyzer evaluates (built by <see cref="PkiEscReader.ReadAsync"/>, or by hand in tests).</summary>
/// <param name="Templates">The templates.</param>
/// <param name="Rights">The trustee-level rights per template (the rules use the trustee level, as Certipy and Locksmith).</param>
/// <param name="EnterpriseCas">The enterprise CAs (published templates, NTAuth).</param>
/// <param name="Oids">The OID objects.</param>
/// <param name="LinkedGroups">The groups of the OID group links that resolved.</param>
/// <param name="AclGrants">The grants on PKI objects, CA computers and Tier 0 accounts.</param>
/// <param name="TrusteeClasses">The class of every grant trustee (SID → class).</param>
/// <param name="MappedAccounts">The accounts with <c>altSecurityIdentities</c>.</param>
/// <param name="CaGrantsAuthenticatedUsersEnroll">Decision 3: every CA grants Enroll to Authenticated Users.</param>
public sealed record PkiEscSnapshot(
    IReadOnlyList<CertificateTemplate> Templates,
    IReadOnlyList<TemplateRights> Rights,
    IReadOnlyList<PkiEnrollmentService> EnterpriseCas,
    IReadOnlyList<PkiOidObject> Oids,
    IReadOnlyList<PkiLinkedGroup> LinkedGroups,
    IReadOnlyList<PkiAclGrant> AclGrants,
    IReadOnlyDictionary<string, PrincipalClass> TrusteeClasses,
    IReadOnlyList<PkiAccountFact> MappedAccounts,
    bool CaGrantsAuthenticatedUsersEnroll)
{
    /// <summary>The reader's notices (unresolved trustees and group links).</summary>
    public IReadOnlyList<PkiReadNotice> Notices { get; init; } = [];

    /// <summary>Counts only.</summary>
    public override string ToString() => string.Create(CultureInfo.InvariantCulture,
        $"{nameof(PkiEscSnapshot)}({Templates.Count} templates, {EnterpriseCas.Count} CAs, {AclGrants.Count} grants, {MappedAccounts.Count} mapped accounts)");
}

/// <summary>Options of <see cref="PkiEscReader.ReadAsync"/>.</summary>
/// <param name="CaGrantsAuthenticatedUsersEnroll">Decision 3 (default true until P4 reads the CA's security).</param>
public sealed record PkiEscReadOptions(bool CaGrantsAuthenticatedUsersEnroll = true)
{
    /// <summary>The option only.</summary>
    public override string ToString() => $"{nameof(PkiEscReadOptions)}(CA enroll assumed {CaGrantsAuthenticatedUsersEnroll})";
}

/// <summary>The step the ESC read is at.</summary>
public enum PkiEscStage
{
    /// <summary>The PKI ACLs (<c>CollectPublicKeyAsync</c>).</summary>
    AccessControl,

    /// <summary>Classifying the trustees and resolving the OID group links.</summary>
    Principals,

    /// <summary>The read is complete.</summary>
    Completed,
}

/// <summary>Progress of <see cref="PkiEscReader.ReadAsync"/>.</summary>
/// <param name="Stage">The current step.</param>
/// <param name="Done">Items of the step done.</param>
public sealed record PkiEscProgress(PkiEscStage Stage, int Done = 0)
{
    /// <summary>Stage and count only.</summary>
    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"{Stage} {Done}");
}

/// <summary>The findings of <see cref="PkiEscAnalyzer.Analyze"/> (most severe first) with what could not be evaluated.</summary>
/// <param name="Findings">The findings.</param>
public sealed record PkiEscResult(IReadOnlyList<PkiFinding> Findings) : IReadOnlyList<PkiFinding>
{
    /// <summary>False when nothing could be evaluated (the PKI base was denied): a visible "not evaluated" state.</summary>
    public bool IsEvaluated { get; init; } = true;

    /// <summary>What the reads observed (templates without descriptor, unresolved trustees …).</summary>
    public IReadOnlyList<PkiReadNotice> Notices { get; init; } = [];

    /// <summary>The rule groups whose data was not read.</summary>
    public IReadOnlyList<PkiRuleGroup> NotEvaluated { get; init; } = [];

    public int Count => Findings.Count;

    public PkiFinding this[int index] => Findings[index];

    public IEnumerator<PkiFinding> GetEnumerator() => Findings.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    /// <summary>Counts only.</summary>
    public override string ToString() => string.Create(CultureInfo.InvariantCulture,
        $"{nameof(PkiEscResult)}({Findings.Count} findings, {NotEvaluated.Count} rule groups not evaluated{(IsEvaluated ? string.Empty : ", not evaluated")})");
}
