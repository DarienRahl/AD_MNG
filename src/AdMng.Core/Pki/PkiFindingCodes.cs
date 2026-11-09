namespace AdMng.Core.Pki;

/// <summary>The ESC number, highest severity, area, rule group, approval effect and tool ids of a finding code.</summary>
/// <param name="Code">The code.</param>
/// <param name="Esc">The Certipy ESC number (<c>ESC1</c> …).</param>
/// <param name="Severity">The highest severity it can get.</param>
/// <param name="Area">Its area.</param>
/// <param name="Group">The data its rule needs.</param>
/// <param name="Approval">How manager approval / an authorized signature affects it.</param>
/// <param name="Locksmith">The <c>Technique</c> string of the Locksmith rule (commit 8b555bd), null when Locksmith has none.</param>
/// <param name="Mdi">The Microsoft Defender for Identity assessment id – null until a real one is confirmed (never invented).</param>
public sealed record PkiFindingDefinition(
    string Code,
    string Esc,
    PkiFindingSeverity Severity,
    PkiFindingArea Area,
    PkiRuleGroup Group,
    PkiApprovalEffect Approval,
    string? Locksmith,
    string? Mdi)
{
    /// <summary>The code, severity and area (constants of the catalogue).</summary>
    public override string ToString() => $"{Code} {Severity} {Area}";
}

/// <summary>
/// The codes of the AD CS security review (docs/dev/p-certificate-services.md 2.4) – stable ids the UI localizes
/// (<c>PkiFinding.&lt;Code&gt;.Title|Message|Explanation|Recommendation</c>, 2.8). The comment of each code lists its
/// <see cref="PkiFinding.Arguments"/> in order; the principals themselves are in <see cref="PkiFinding.Principals"/>.
/// All eleven are LDAP-only; ESC6/7/8/10/11/16 are P4 and ESC12 is out of scope.
/// </summary>
public static class PkiFindingCodes
{
    /// <summary>
    /// ESC1: the enrollee supplies the subject, the template allows client authentication and a broad or other trustee can
    /// enroll. Arguments: the template's name, the enrolling principals (comma separated), the number of accounts.
    /// </summary>
    public const string EnrolleeSuppliesSubjectClientAuth = nameof(EnrolleeSuppliesSubjectClientAuth);

    /// <summary>
    /// ESC2: Any Purpose or no EKU at all, enrollable. Arguments: the template's name, the enrolling principals, the number
    /// of accounts.
    /// </summary>
    public const string AnyPurposeOrNoEku = nameof(AnyPurposeOrNoEku);

    /// <summary>
    /// ESC3 (condition 1): an enrollment-agent template, enrollable (<see cref="PkiFinding.RelatedObjectIds"/>: the target
    /// templates). Arguments: the template's name, the enrolling principals, the number of accounts.
    /// </summary>
    public const string EnrollmentAgentTemplate = nameof(EnrollmentAgentTemplate);

    /// <summary>
    /// ESC3 (condition 2): a schema-2+ template that requires one enrollment-agent signature and allows client
    /// authentication, enrollable. Arguments: the template's name, the enrolling principals, the number of accounts.
    /// </summary>
    public const string EnrollmentAgentTargetTemplate = nameof(EnrollmentAgentTargetTemplate);

    /// <summary>
    /// ESC4: a broad or other trustee may change the template (full control, DACL, owner, all or sensitive properties).
    /// Arguments: the template's name, the principals, the rights (comma separated).
    /// </summary>
    public const string TemplateWriteable = nameof(TemplateWriteable);

    /// <summary>
    /// ESC5: a broad or other trustee may change a PKI container, NTAuth, an OID object, an enrollment service, a CA / AIA /
    /// CDP object or a CA computer. Arguments: the object's name, the principals, the rights (comma separated).
    /// </summary>
    public const string PkiObjectWriteable = nameof(PkiObjectWriteable);

    /// <summary>
    /// ESC9: no security extension (<c>CT_FLAG_NO_SECURITY_EXTENSION</c>), client authentication, enrollable. Arguments:
    /// the template's name, the enrolling principals, the number of accounts.
    /// </summary>
    public const string NoSecurityExtensionClientAuth = nameof(NoSecurityExtensionClientAuth);

    /// <summary>
    /// ESC13: an issuance policy of the template is linked to a group (<c>msDS-OIDToGroupLink</c>), client authentication,
    /// enrollable. Arguments: the template's name, the enrolling principals, the number of accounts, the linked group.
    /// </summary>
    public const string IssuancePolicyLinkedToGroup = nameof(IssuancePolicyLinkedToGroup);

    /// <summary>
    /// ESC14: a weak explicit mapping on an account (a Tier 0 account, or the aggregated count of the others), or a broad or
    /// other trustee may write <c>altSecurityIdentities</c> of a Tier 0 account. Arguments: the account's name (or the
    /// number of accounts of the aggregated finding), the mapping kinds or the rights, the principals (empty for a mapping).
    /// </summary>
    public const string WeakExplicitMapping = nameof(WeakExplicitMapping);

    /// <summary>
    /// ESC15 (CVE-2024-49019): a schema-1 template where the enrollee supplies the subject, enrollable. Arguments: the
    /// template's name, the enrolling principals, the number of accounts.
    /// </summary>
    public const string SchemaV1EnrolleeSuppliesSubject = nameof(SchemaV1EnrolleeSuppliesSubject);

    /// <summary>
    /// ESC17: the enrollee supplies the subject and the template allows server authentication, enrollable. Arguments: the
    /// template's name, the enrolling principals, the number of accounts.
    /// </summary>
    public const string EnrolleeSuppliesSubjectServerAuth = nameof(EnrolleeSuppliesSubjectServerAuth);

    /// <summary>Every code in the order of the design note's table.</summary>
    public static IReadOnlyList<PkiFindingDefinition> All { get; } =
    [
        new(EnrolleeSuppliesSubjectClientAuth, "ESC1", PkiFindingSeverity.Critical, PkiFindingArea.Template, PkiRuleGroup.Enrollment, PkiApprovalEffect.Suppresses, "ESC1", null),
        new(AnyPurposeOrNoEku, "ESC2", PkiFindingSeverity.Critical, PkiFindingArea.Template, PkiRuleGroup.Enrollment, PkiApprovalEffect.Suppresses, "ESC2", null),
        new(EnrollmentAgentTemplate, "ESC3", PkiFindingSeverity.Critical, PkiFindingArea.Template, PkiRuleGroup.Enrollment, PkiApprovalEffect.Suppresses, "ESC3", null),
        new(EnrollmentAgentTargetTemplate, "ESC3", PkiFindingSeverity.High, PkiFindingArea.Template, PkiRuleGroup.Enrollment, PkiApprovalEffect.Suppresses, "ESC3", null),
        new(TemplateWriteable, "ESC4", PkiFindingSeverity.Critical, PkiFindingArea.Template, PkiRuleGroup.AccessControl, PkiApprovalEffect.Never, "ESC4", null),
        new(PkiObjectWriteable, "ESC5", PkiFindingSeverity.Critical, PkiFindingArea.PkiObject, PkiRuleGroup.AccessControl, PkiApprovalEffect.Never, "ESC5", null),
        new(NoSecurityExtensionClientAuth, "ESC9", PkiFindingSeverity.High, PkiFindingArea.Template, PkiRuleGroup.Enrollment, PkiApprovalEffect.Lowers, "ESC9", null),
        new(IssuancePolicyLinkedToGroup, "ESC13", PkiFindingSeverity.Critical, PkiFindingArea.Template, PkiRuleGroup.Enrollment, PkiApprovalEffect.Lowers, "ESC13", null),
        new(WeakExplicitMapping, "ESC14", PkiFindingSeverity.Critical, PkiFindingArea.Account, PkiRuleGroup.Mapping, PkiApprovalEffect.Never, null, null),
        new(SchemaV1EnrolleeSuppliesSubject, "ESC15", PkiFindingSeverity.Medium, PkiFindingArea.Template, PkiRuleGroup.Enrollment, PkiApprovalEffect.Lowers, "ESC15/EKUwu", null),
        new(EnrolleeSuppliesSubjectServerAuth, "ESC17", PkiFindingSeverity.High, PkiFindingArea.Template, PkiRuleGroup.Enrollment, PkiApprovalEffect.Suppresses, "ESC17", null),
    ];

    private static readonly Dictionary<string, PkiFindingDefinition> ByCode = All.ToDictionary(d => d.Code, StringComparer.Ordinal);

    /// <summary>The definition of a code.</summary>
    /// <exception cref="KeyNotFoundException">Not a code of this class.</exception>
    public static PkiFindingDefinition Find(string code) =>
        ByCode.TryGetValue(code ?? throw new ArgumentNullException(nameof(code)), out var definition)
            ? definition
            : throw new KeyNotFoundException("Unknown PKI finding code.");
}
