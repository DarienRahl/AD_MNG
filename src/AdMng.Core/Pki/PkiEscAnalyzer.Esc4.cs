namespace AdMng.Core.Pki;

public static partial class PkiEscAnalyzer
{
    /// <summary>The template rights of ESC4 in the order the message lists them.</summary>
    private static readonly TemplateRight[] Esc4Rights =
    [
        TemplateRight.FullControl, TemplateRight.Owner, TemplateRight.WriteDacl, TemplateRight.WriteOwner,
        TemplateRight.WriteAllProperties, TemplateRight.WriteSensitiveProperty, TemplateRight.WriteOtherProperty,
    ];

    /// <summary>The rights <see cref="TemplateRight.FullControl"/> implies (not listed again beside it).</summary>
    private static readonly TemplateRight[] ImpliedByFullControl = [TemplateRight.WriteDacl, TemplateRight.WriteOwner, TemplateRight.WriteAllProperties];

    /// <summary>
    /// ESC4 (<see cref="PkiFindingCodes.TemplateWriteable"/>): a broad or other trustee holds FullControl, Owner, WriteDacl,
    /// WriteOwner, WriteAllProperties or a write of a sensitive template attribute (broad → Critical, other → Medium; an object
    /// type nothing names counts as sensitive and adds <see cref="PkiFindingNotes.UnknownAttribute"/>), or only a write of
    /// another attribute (Low – Certipy flags only all-property writes, Locksmith every write but Enroll/AutoEnroll; the graded
    /// model finds both). One finding per template; approval never protects (the attacker rewrites the template). An
    /// unpublished template is one level lower.
    /// </summary>
    private static void Esc4(Run run)
    {
        foreach (var facts in run.Evaluated)
        {
            var writers = facts.Rights!.Trustees
                .Where(t => t.Class != PrincipalClass.Tier0 && Esc4Rights.Contains(t.Right))
                .ToList();
            if (writers.Count == 0)
            {
                continue;
            }

            var fullControl = writers.Where(t => t.Right == TemplateRight.FullControl).Select(t => t.Sid).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var shown = writers
                .Where(t => !(fullControl.Contains(t.Sid) && ImpliedByFullControl.Contains(t.Right)))
                .DistinctBy(t => (t.Sid.ToUpperInvariant(), t.Right))
                .OrderBy(t => Array.IndexOf(Esc4Rights, t.Right))
                .ToList();
            var severity = PkiFindingSeverity.Info;
            var notes = PkiFindingNotes.None;
            foreach (var writer in shown)
            {
                severity = Max(severity, writer.Right == TemplateRight.WriteOtherProperty
                    ? PkiFindingSeverity.Low
                    : ClassSeverity(writer.Class, PkiFindingSeverity.Critical, PkiFindingSeverity.Medium));
                if (writer.Right == TemplateRight.WriteSensitiveProperty && Guid.TryParse(writer.Attribute, out _))
                {
                    notes |= PkiFindingNotes.UnknownAttribute;
                }
            }

            var principals = shown
                .Select(t => new PkiPrincipal(t.Sid, string.IsNullOrWhiteSpace(t.Name) ? null : t.Name, t.Class, t.AccountCount, t.IsResolved, t.Right.ToString()))
                .ToList();
            var names = principals.Select(p => p.Name ?? p.Sid).Distinct(StringComparer.OrdinalIgnoreCase);
            var rights = shown.Select(t => t.Right).Distinct().OrderBy(r => Array.IndexOf(Esc4Rights, r)).Select(r => r.ToString());
            run.Add(Run.Publication(facts, new PkiFinding(
                PkiFindingCodes.TemplateWriteable,
                severity,
                PkiFindingArea.Template,
                facts.Template.ObjectGuid,
                [facts.Name, Join(names), Join(rights)])
            {
                ObjectDn = facts.Template.Dn,
                Principals = principals,
                Notes = notes,
                RelatedObjectIds = facts.PublishingCas,
            }));
        }
    }
}
