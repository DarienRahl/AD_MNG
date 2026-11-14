namespace AdMng.Core.Pki;

public static partial class PkiEscAnalyzer
{
    /// <summary>
    /// ESC9 (<see cref="PkiFindingCodes.NoSecurityExtensionClientAuth"/>): <c>CT_FLAG_NO_SECURITY_EXTENSION</c> (enrollment flag
    /// <c>0x80000</c>), client authentication, enrollable; approval lowers it. Broad → High, other → Medium. How the DCs enforce
    /// strong mapping is P4/K1; P1 reports the template.
    /// </summary>
    private static void Esc9(Run run)
    {
        foreach (var facts in run.Evaluated.Where(t => t.IsEnrollable && t.Template.NoSecurityExtension))
        {
            var (holds, differs) = OnEitherEku(facts.Template, PkiFlags.IsClientAuth);
            if (holds)
            {
                run.AddEnrollment(facts, PkiFindingCodes.NoSecurityExtensionClientAuth,
                    ClassSeverity(facts.EnrollClass, PkiFindingSeverity.High, PkiFindingSeverity.Medium),
                    differs ? PkiFindingNotes.EkuSourcesDiffer : PkiFindingNotes.None);
            }
        }
    }
}
