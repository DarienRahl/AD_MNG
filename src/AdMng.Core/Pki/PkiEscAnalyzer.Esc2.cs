namespace AdMng.Core.Pki;

public static partial class PkiEscAnalyzer
{
    /// <summary>
    /// ESC2 (<see cref="PkiFindingCodes.AnyPurposeOrNoEku"/>): Any Purpose or no EKU at all (a template is not exempt because
    /// it issues CA certificates – Certipy reports <c>SubCA</c> too when others can enroll), enrollable; approval suppresses
    /// it. Broad → Critical, other → Medium.
    /// </summary>
    private static void Esc2(Run run)
    {
        foreach (var facts in run.Evaluated.Where(t => t.IsEnrollable))
        {
            var (holds, differs) = OnEitherEku(facts.Template, PkiFlags.IsAnyPurpose);
            if (holds)
            {
                run.AddEnrollment(facts, PkiFindingCodes.AnyPurposeOrNoEku,
                    ClassSeverity(facts.EnrollClass, PkiFindingSeverity.Critical, PkiFindingSeverity.Medium),
                    differs ? PkiFindingNotes.EkuSourcesDiffer : PkiFindingNotes.None);
            }
        }
    }
}
