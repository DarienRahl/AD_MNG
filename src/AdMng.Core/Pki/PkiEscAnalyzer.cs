using System.Globalization;
using AdMng.Core.Ldap;

namespace AdMng.Core.Pki;

/// <summary>
/// The LDAP-only ESC rules (docs/dev/p-certificate-services.md 2.4: ESC1, 2, 3, 4, 5, 9, 13, 14, 15, 17), pure – split
/// like K6 into a reader and this analyzer; one partial per rule (<c>PkiEscAnalyzer.Esc*.cs</c>).
/// </summary>
/// <remarks>
/// <para><b>Enrollable</b> = a trustee of class <see cref="PrincipalClass.Broad"/> or <see cref="PrincipalClass.Other"/> holds
/// <see cref="TemplateRight.Enroll"/> (or <see cref="TemplateRight.FullControl"/>) at the trustee level
/// (<see cref="PkiEscSnapshot.Rights"/>; AutoEnroll alone does not enroll); the severity follows the highest such class. A
/// template whose rights were not evaluated gives no finding of its own and a <see cref="PkiReadNoticeCode.SecurityDescriptorUnreadable"/>
/// notice – never "safe".</para>
/// <para><b>EKU</b>: each predicate is evaluated on <see cref="CertificateTemplate.EffectiveEku"/> and on
/// <see cref="CertificateTemplate.Ekus"/>; the rule fires when either holds and carries
/// <see cref="PkiFindingNotes.EkuSourcesDiffer"/> when exactly one does. An empty set means Any Purpose (as Certipy).</para>
/// <para><b>Adjustments</b>, in this order: the approval effect of the code (<see cref="PkiFindingDefinition.Approval"/>:
/// suppress, or one level lower with <see cref="PkiFindingNotes.ApprovalRequired"/>), then one level lower with
/// <see cref="PkiFindingNotes.NotPublished"/> for a template no enterprise CA publishes (every template finding, ESC4 too);
/// never below <see cref="PkiFindingSeverity.Info"/>. Every enrollment finding carries <see cref="PkiFindingNotes.AssumesCaEnroll"/>
/// while <see cref="PkiEscSnapshot.CaGrantsAuthenticatedUsersEnroll"/> holds (decision 3).</para>
/// <para><b>Arguments</b> of the enrollment findings: the template's display name (else its cn), the enrolling principals
/// (names, else SIDs, joined with <see cref="PkiFindingCodes.ListSeparator"/>) and the number of their enabled accounts – the
/// sum of the principals' counts capped by the template's Enroll total, <c>N+</c> when some principal's count is unknown
/// (another domain), <c>?</c> when none is known.</para>
/// </remarks>
public static partial class PkiEscAnalyzer
{
    private const string UnknownCount = "?";

    /// <summary>Evaluates every rule on <paramref name="snapshot"/>; most severe first.</summary>
    public static PkiEscResult Analyze(PkiEscSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var run = new Run(snapshot);
        if (!run.Skips(PkiRuleGroup.Enrollment))
        {
            Esc1(run);
            Esc2(run);
            Esc3(run);
            Esc9(run);
            Esc13(run);
            Esc15(run);
            Esc17(run);

            // ESC4 reads the template rights (the snapshot's descriptors) like the enrollment rules, not the PKI ACL pass.
            Esc4(run);
        }

        if (!run.Skips(PkiRuleGroup.AccessControl))
        {
            Esc5(run);
        }

        if (!run.Skips(PkiRuleGroup.Mapping))
        {
            Esc14(run);
        }

        return run.Result();
    }

    /// <summary>One level lower (never below <see cref="PkiFindingSeverity.Info"/>).</summary>
    internal static PkiFindingSeverity Lower(PkiFindingSeverity severity) =>
        severity == PkiFindingSeverity.Info ? PkiFindingSeverity.Info : severity - 1;

    /// <summary>The severity of a principal class: <paramref name="broad"/> for Broad, <paramref name="other"/> for Other.</summary>
    private static PkiFindingSeverity ClassSeverity(PrincipalClass principalClass, PkiFindingSeverity broad, PkiFindingSeverity other) =>
        principalClass == PrincipalClass.Broad ? broad : other;

    /// <summary>The more dangerous of two classes (Broad &gt; Other &gt; Tier 0).</summary>
    private static PrincipalClass Worse(PrincipalClass a, PrincipalClass b) => Rank(a) >= Rank(b) ? a : b;

    private static int Rank(PrincipalClass principalClass) => principalClass switch
    {
        PrincipalClass.Broad => 2,
        PrincipalClass.Other => 1,
        _ => 0,
    };

    private static PkiFindingSeverity Max(PkiFindingSeverity a, PkiFindingSeverity b) => a >= b ? a : b;

    /// <summary>The value of the leaf RDN of a DN (the object's name when nothing better is known).</summary>
    private static string LeafName(string dn) => DistinguishedName.TryParse(dn, out var parsed) && !parsed.IsRoot ? parsed.Name : dn;

    private static string Join(IEnumerable<string> items) => string.Join(PkiFindingCodes.ListSeparator, items);

    /// <summary>A predicate on both EKU sources: whether it holds on either, and whether exactly one holds.</summary>
    private static (bool Holds, bool Differs) OnEitherEku(CertificateTemplate template, Func<IReadOnlyCollection<string>, bool> predicate)
    {
        var effective = predicate(template.EffectiveEku);
        var legacy = predicate(template.Ekus);
        return (effective || legacy, effective != legacy);
    }

    /// <summary>One template with what the rules need about it.</summary>
    private sealed class TemplateFacts
    {
        public required CertificateTemplate Template { get; init; }

        public required TemplateRights? Rights { get; init; }

        /// <summary>The descriptor was read and parsed.</summary>
        public required bool IsEvaluated { get; init; }

        /// <summary>The enterprise CAs that publish the template.</summary>
        public required IReadOnlyList<Guid> PublishingCas { get; init; }

        /// <summary>The broad or other trustees that can enroll (one per SID).</summary>
        public required IReadOnlyList<TemplateTrustee> Enrollers { get; init; }

        public bool IsPublished => PublishingCas.Count > 0;

        public bool IsEnrollable => Enrollers.Count > 0;

        /// <summary>The most dangerous enrolling class (Broad before Other); Tier 0 when nobody outside Tier 0 enrolls.</summary>
        public PrincipalClass EnrollClass => Enrollers.Aggregate(PrincipalClass.Tier0, (worst, t) => Worse(worst, t.Class));

        public string Name => string.IsNullOrWhiteSpace(Template.DisplayName) ? Template.Cn : Template.DisplayName;
    }

    /// <summary>The state of one analysis: the template facts, the findings and the notices.</summary>
    private sealed class Run
    {
        private readonly List<PkiFinding> _findings = [];
        private readonly List<PkiReadNotice> _notices = [];
        private readonly HashSet<PkiRuleGroup> _notEvaluated = [];

        public Run(PkiEscSnapshot snapshot)
        {
            Snapshot = snapshot;
            _notices.AddRange(snapshot.Notices);
            foreach (var group in snapshot.NotEvaluated)
            {
                _notEvaluated.Add(group);
            }

            IsEvaluated = !snapshot.Notices.Any(n => n.Code == PkiReadNoticeCode.BaseAccessDenied);
            if (!IsEvaluated)
            {
                _notEvaluated.Add(PkiRuleGroup.Enrollment);
                _notEvaluated.Add(PkiRuleGroup.AccessControl);
            }

            var byGuid = new Dictionary<Guid, TemplateRights>();
            var byCn = new Dictionary<string, TemplateRights>(StringComparer.OrdinalIgnoreCase);
            foreach (var rights in snapshot.Rights)
            {
                if (rights.TemplateGuid != Guid.Empty)
                {
                    byGuid.TryAdd(rights.TemplateGuid, rights);
                }

                byCn.TryAdd(rights.TemplateCn, rights);
            }

            var templates = new List<TemplateFacts>();
            foreach (var template in snapshot.Templates)
            {
                var rights = template.ObjectGuid != Guid.Empty && byGuid.TryGetValue(template.ObjectGuid, out var found) ? found
                    : byCn.GetValueOrDefault(template.Cn);
                var evaluated = rights is { IsEvaluated: true };
                if (!evaluated && !_notices.Any(n => n.Code == PkiReadNoticeCode.SecurityDescriptorUnreadable && n.ObjectGuid == template.ObjectGuid))
                {
                    _notices.Add(new PkiReadNotice(PkiReadNoticeCode.SecurityDescriptorUnreadable, template.Dn, template.ObjectGuid));
                }

                templates.Add(new TemplateFacts
                {
                    Template = template,
                    Rights = rights,
                    IsEvaluated = evaluated,
                    PublishingCas = [.. snapshot.EnterpriseCas
                        .Where(ca => ca.PublishedTemplates.Contains(template.Cn, StringComparer.OrdinalIgnoreCase))
                        .Select(ca => ca.ObjectGuid)],
                    Enrollers = evaluated ? EnrollersOf(rights!) : [],
                });
            }

            Templates = templates;
        }

        public PkiEscSnapshot Snapshot { get; }

        public bool IsEvaluated { get; }

        /// <summary>Every template with its facts (evaluated or not).</summary>
        public IReadOnlyList<TemplateFacts> Templates { get; }

        /// <summary>The templates whose rights were evaluated.</summary>
        public IEnumerable<TemplateFacts> Evaluated => Templates.Where(t => t.IsEvaluated);

        public bool Skips(PkiRuleGroup group) => _notEvaluated.Contains(group);

        public void Add(PkiFinding finding) => _findings.Add(finding);

        public IReadOnlyList<PkiFinding> FindingsOf(string code) => [.. _findings.Where(f => f.Code == code)];

        /// <summary>
        /// Adds an enrollment finding of <paramref name="code"/> on <paramref name="facts"/> at <paramref name="severity"/>
        /// after the approval effect of the code and the publication adjustment; false when approval suppresses it.
        /// </summary>
        public bool AddEnrollment(
            TemplateFacts facts,
            string code,
            PkiFindingSeverity severity,
            PkiFindingNotes notes,
            IEnumerable<Guid>? related = null,
            IEnumerable<string>? moreArguments = null)
        {
            var template = facts.Template;
            var approval = PkiFindingCodes.Find(code).Approval;
            var approved = template.RequiresManagerApproval || template.RequiresAuthorizedSignature;
            switch (approval)
            {
                case PkiApprovalEffect.Suppresses when approved:
                case PkiApprovalEffect.ManagerApprovalSuppresses when template.RequiresManagerApproval:
                    return false;
                case PkiApprovalEffect.Lowers when approved:
                    severity = Lower(severity);
                    notes |= PkiFindingNotes.ApprovalRequired;
                    break;
            }

            if (Snapshot.CaGrantsAuthenticatedUsersEnroll)
            {
                notes |= PkiFindingNotes.AssumesCaEnroll;
            }

            var principals = facts.Enrollers.Select(PrincipalOf).ToList();
            Add(Publication(facts, new PkiFinding(
                code,
                severity,
                PkiFindingArea.Template,
                template.ObjectGuid,
                [facts.Name, Join(principals.Select(p => p.Name ?? p.Sid)), AccountCount(facts), .. moreArguments ?? []])
            {
                ObjectDn = template.Dn,
                Principals = principals,
                Notes = notes,
                RelatedObjectIds = [.. facts.PublishingCas, .. (related ?? []).Where(id => id != template.ObjectGuid && !facts.PublishingCas.Contains(id)).Distinct()],
            }));
            return true;
        }

        /// <summary>One level lower with <see cref="PkiFindingNotes.NotPublished"/> when no enterprise CA publishes the template.</summary>
        public static PkiFinding Publication(TemplateFacts facts, PkiFinding finding) =>
            facts.IsPublished ? finding : finding with { Severity = Lower(finding.Severity), Notes = finding.Notes | PkiFindingNotes.NotPublished };

        /// <summary>The class of a grant trustee (an unclassified SID counts as Other – no false negative).</summary>
        public PrincipalClass ClassOf(string sid) =>
            Snapshot.TrusteeClasses.TryGetValue(sid, out var principalClass) ? principalClass : PrincipalClass.Other;

        /// <summary>A grant trustee as a principal: its resolved name and, when a template right names it, its account count.</summary>
        public PkiPrincipal GrantPrincipal(string sid, string right)
        {
            var name = Snapshot.TrusteeNames.TryGetValue(sid, out var resolved) && !string.IsNullOrWhiteSpace(resolved) ? resolved : null;
            var known = Snapshot.Rights.SelectMany(r => r.Trustees)
                .FirstOrDefault(t => string.Equals(t.Sid, sid, StringComparison.OrdinalIgnoreCase) && t.AccountCount >= 0);
            name ??= known is { Name: { Length: > 0 } knownName } ? knownName : null;
            var resolvedPrincipal = name is not null || !sid.StartsWith("S-1-5-21-", StringComparison.OrdinalIgnoreCase);
            return new PkiPrincipal(sid, name, ClassOf(sid), known?.AccountCount ?? -1, resolvedPrincipal, right);
        }

        public PkiEscResult Result()
        {
            var order = PkiFindingCodes.All.Select((d, i) => (d.Code, i)).ToDictionary(p => p.Code, p => p.i, StringComparer.Ordinal);
            var findings = _findings
                .OrderByDescending(f => f.Severity)
                .ThenBy(f => order[f.Code])
                .ThenBy(f => f.Arguments.Count > 0 ? f.Arguments[0] : string.Empty, StringComparer.InvariantCultureIgnoreCase)
                .ThenBy(f => f.ObjectDn, StringComparer.OrdinalIgnoreCase)
                .ToList();
            var notices = _notices
                .DistinctBy(n => (n.Code, n.ObjectGuid, n.ObjectDn?.ToUpperInvariant()))
                .ToList();
            return new PkiEscResult(findings)
            {
                IsEvaluated = IsEvaluated,
                Notices = notices,
                NotEvaluated = [.. Enum.GetValues<PkiRuleGroup>().Where(_notEvaluated.Contains)],
            };
        }

        private static PkiPrincipal PrincipalOf(TemplateTrustee trustee) =>
            new(trustee.Sid, string.IsNullOrWhiteSpace(trustee.Name) ? null : trustee.Name, trustee.Class, trustee.AccountCount, trustee.IsResolved, trustee.Right.ToString());

        /// <summary>The broad or other trustees holding Enroll (or FullControl), one per SID (Enroll preferred).</summary>
        private static List<TemplateTrustee> EnrollersOf(TemplateRights rights) =>
        [
            .. rights.Trustees
                .Where(t => t.Class != PrincipalClass.Tier0 && t.Right is TemplateRight.Enroll or TemplateRight.FullControl)
                .OrderBy(t => t.Right == TemplateRight.Enroll ? 0 : 1)
                .DistinctBy(t => t.Sid, StringComparer.OrdinalIgnoreCase),
        ];

        /// <summary>The number of enabled accounts behind the enrolling principals (see the class remarks).</summary>
        private static string AccountCount(TemplateFacts facts)
        {
            var known = facts.Enrollers.Where(t => t.IsResolved && t.AccountCount >= 0).ToList();
            if (known.Count == 0)
            {
                return UnknownCount;
            }

            long sum = known.Sum(t => (long)t.AccountCount);
            if (facts.Rights?.Totals.FirstOrDefault(t => t.Right == TemplateRight.Enroll) is { AccountCount: >= 0 } total)
            {
                sum = Math.Min(sum, total.AccountCount);
            }

            var text = sum.ToString(CultureInfo.InvariantCulture);
            return known.Count < facts.Enrollers.Count ? text + "+" : text;
        }
    }
}
