namespace AdMng.Core.Pki;

public static partial class PkiEscAnalyzer
{
    /// <summary>
    /// ESC1 (<see cref="PkiFindingCodes.EnrolleeSuppliesSubjectClientAuth"/>): the enrollee supplies the subject, the template
    /// allows client authentication and it is enrollable; approval suppresses it. Broad → Critical, other → Medium.
    /// </summary>
    private static void Esc1(Run run)
    {
        foreach (var facts in run.Evaluated.Where(t => t.IsEnrollable && t.Template.EnrolleeSuppliesSubject))
        {
            var (holds, differs) = OnEitherEku(facts.Template, PkiFlags.IsClientAuth);
            if (holds)
            {
                run.AddEnrollment(facts, PkiFindingCodes.EnrolleeSuppliesSubjectClientAuth,
                    ClassSeverity(facts.EnrollClass, PkiFindingSeverity.Critical, PkiFindingSeverity.Medium),
                    differs ? PkiFindingNotes.EkuSourcesDiffer : PkiFindingNotes.None);
            }
        }
    }
}
