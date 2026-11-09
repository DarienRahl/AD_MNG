namespace AdMng.Core.Pki;

public static partial class PkiEscAnalyzer
{
    /// <summary>
    /// ESC17 (<see cref="PkiFindingCodes.EnrolleeSuppliesSubjectServerAuth"/>, Certipy's definition): the enrollee supplies the
    /// subject, the template allows server authentication (Server Authentication or Any Purpose / no EKU – Locksmith only the
    /// Server Authentication OID), enrollable; approval suppresses it. Broad → High, other → Medium.
    /// </summary>
    private static void Esc17(Run run)
    {
        foreach (var facts in run.Evaluated.Where(t => t.IsEnrollable && t.Template.EnrolleeSuppliesSubject))
        {
            var (holds, differs) = OnEitherEku(facts.Template, Eku.IsServerAuth);
            if (holds)
            {
                run.AddEnrollment(facts, PkiFindingCodes.EnrolleeSuppliesSubjectServerAuth,
                    ClassSeverity(facts.EnrollClass, PkiFindingSeverity.High, PkiFindingSeverity.Medium),
                    differs ? PkiFindingNotes.EkuSourcesDiffer : PkiFindingNotes.None);
            }
        }
    }
}
