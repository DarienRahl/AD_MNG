namespace AdMng.Core.Pki;

public static partial class PkiEscAnalyzer
{
    /// <summary>
    /// ESC3. Condition 1 (<see cref="PkiFindingCodes.EnrollmentAgentTemplate"/>): the template issues Certificate Request Agent
    /// certificates (or Any Purpose / no EKU) and is enrollable; approval suppresses it; broad → Critical, other → Medium; its
    /// <see cref="PkiFinding.RelatedObjectIds"/> are the publishing CAs and the published targets (the condition-2 templates
    /// and the schema-1 client-authentication templates anybody outside Tier 0 can enroll in without manager approval –
    /// Certipy's <i>ESC3 Target Template</i>; schema 1 accepts any agent signature).
    /// Condition 2 (<see cref="PkiFindingCodes.EnrollmentAgentTargetTemplate"/>): schema 2 or later, at least one authorized
    /// signature whose application policies hold Certificate Request Agent, client authentication, enrollable; only manager
    /// approval suppresses it (the signature is the condition). High when a broad principal can use a published agent
    /// template, Medium when only other principals can, else Low with <see cref="PkiFindingNotes.TargetOnly"/>. A schema-1
    /// target is never a finding of its own (it would flag the default <i>User</i> template).
    /// </summary>
    private static void Esc3(Run run)
    {
        var agents = new List<(TemplateFacts Facts, bool Differs)>();
        foreach (var facts in run.Evaluated.Where(t => t.IsEnrollable))
        {
            var (holds, differs) = OnEitherEku(facts.Template, Eku.IsEnrollmentAgent);
            if (holds)
            {
                agents.Add((facts, differs));
            }
        }

        var targets = new List<(TemplateFacts Facts, bool Differs)>();
        var schemaOneTargets = new List<TemplateFacts>();
        foreach (var facts in run.Evaluated.Where(t => t.IsEnrollable && !t.Template.RequiresManagerApproval))
        {
            var template = facts.Template;
            var (clientAuth, differs) = OnEitherEku(template, Eku.IsClientAuth);
            if (!clientAuth)
            {
                continue;
            }

            if (template.SchemaVersion >= 2 && template.RaSignatures >= 1
                && template.RaApplicationPolicies.Contains(PkiFlags.CertificateRequestAgent, StringComparer.Ordinal))
            {
                targets.Add((facts, differs));
            }
            else if (template.SchemaVersion == 1)
            {
                schemaOneTargets.Add(facts);
            }
        }

        var publishedTargets = targets.Select(t => t.Facts).Concat(schemaOneTargets).Where(t => t.IsPublished).Select(t => t.Template.ObjectGuid).ToList();
        var agentClass = PrincipalClass.Tier0;
        foreach (var (facts, differs) in agents)
        {
            var added = run.AddEnrollment(facts, PkiFindingCodes.EnrollmentAgentTemplate,
                ClassSeverity(facts.EnrollClass, PkiFindingSeverity.Critical, PkiFindingSeverity.Medium),
                differs ? PkiFindingNotes.EkuSourcesDiffer : PkiFindingNotes.None,
                publishedTargets);
            if (added && facts.IsPublished)
            {
                agentClass = Worse(agentClass, facts.EnrollClass);
            }
        }

        foreach (var (facts, differs) in targets)
        {
            var (severity, notes) = agentClass switch
            {
                PrincipalClass.Broad => (PkiFindingSeverity.High, PkiFindingNotes.None),
                PrincipalClass.Other => (PkiFindingSeverity.Medium, PkiFindingNotes.None),
                _ => (PkiFindingSeverity.Low, PkiFindingNotes.TargetOnly),
            };
            var related = run.FindingsOf(PkiFindingCodes.EnrollmentAgentTemplate).Select(f => f.ObjectId).OfType<Guid>();
            run.AddEnrollment(facts, PkiFindingCodes.EnrollmentAgentTargetTemplate, severity,
                notes | (differs ? PkiFindingNotes.EkuSourcesDiffer : PkiFindingNotes.None), related);
        }
    }
}
