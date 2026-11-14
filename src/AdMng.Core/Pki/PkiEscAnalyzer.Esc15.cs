namespace AdMng.Core.Pki;

public static partial class PkiEscAnalyzer
{
    /// <summary>
    /// ESC15 / EKUwu, CVE-2024-49019 (<see cref="PkiFindingCodes.SchemaV1EnrolleeSuppliesSubject"/>): a schema-1 template where
    /// the enrollee supplies the subject, enrollable – no EKU condition (the requester adds application policies). Medium
    /// whatever the class, with <see cref="PkiFindingNotes.PendingCaPatch"/> (an unpatched CA honours the request's
    /// application policies; the CA's patch level is P4); approval lowers it. Certipy reports this; Locksmith flags every
    /// enrollable schema-1 template, which the review does not (docs/CERTYFIKATY.md lists the difference).
    /// </summary>
    private static void Esc15(Run run)
    {
        foreach (var facts in run.Evaluated.Where(t => t.IsEnrollable && t.Template.SchemaVersion == 1 && t.Template.EnrolleeSuppliesSubject))
        {
            run.AddEnrollment(facts, PkiFindingCodes.SchemaV1EnrolleeSuppliesSubject, PkiFindingSeverity.Medium, PkiFindingNotes.PendingCaPatch);
        }
    }
}
