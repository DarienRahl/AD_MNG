using AdMng.Core.Pki;
using AdMng.Core.Pki.Tls;
using AdMng.Presentation.Localization;

namespace AdMng.Presentation.Pki;

/// <summary>
/// The dynamic resource keys of the enum families more than one P1 wave shows (docs/dev/p-certificate-services.md 2.8):
/// <c>Pki.Class.&lt;PrincipalClass&gt;</c>, <c>PkiTpl.Right.&lt;TemplateRight&gt;</c>, <c>Cert.Mapping.&lt;MappingStrength&gt;</c>,
/// <c>Pki.Tls.&lt;TlsProbeOutcome&gt;</c>, <c>Pki.Kdc.&lt;KdcReadinessState&gt;</c>, <c>Pki.Health.&lt;PkiHealthStatus&gt;</c>,
/// <c>PkiEsc.Note.&lt;flag&gt;</c> (one per flag of <see cref="PkiFindingNotes"/>), <c>PkiEsc.Severity.&lt;PkiFindingSeverity&gt;</c>,
/// <c>PkiEsc.Chip.&lt;PkiFindingSeverity&gt;</c> (takes {0}: the count), <c>PkiEsc.Area.&lt;PkiFindingArea&gt;</c>, and – moved
/// here from their single-wave owners because a parallel wave shows them too – <c>Pki.Notice.&lt;PkiReadNoticeCode&gt;</c>
/// (W5's overview, W6's review) and <c>Cert.MappingKind.&lt;AltSecurityIdentityKind&gt;</c> (W7's name mappings, W6's
/// readiness report and ESC14). They live in the contract block of both resx files; the single-wave families are owned by
/// the class of their wave.
/// </summary>
public static class PkiTextKeys
{
    /// <summary>The flags of <see cref="PkiFindingNotes"/> (without <see cref="PkiFindingNotes.None"/>), in declaration order.</summary>
    public static IReadOnlyList<PkiFindingNotes> NoteFlags { get; } =
        [.. Enum.GetValues<PkiFindingNotes>().Where(n => n != PkiFindingNotes.None)];

    public static string ClassKey(PrincipalClass principalClass) => "Pki.Class." + principalClass;

    public static string RightKey(TemplateRight right) => "PkiTpl.Right." + right;

    public static string MappingKey(MappingStrength strength) => "Cert.Mapping." + strength;

    public static string TlsKey(TlsProbeOutcome outcome) => "Pki.Tls." + outcome;

    public static string KdcKey(KdcReadinessState state) => "Pki.Kdc." + state;

    public static string HealthKey(PkiHealthStatus status) => "Pki.Health." + status;

    /// <summary>The key of one flag; <paramref name="note"/> must be a single flag (not <see cref="PkiFindingNotes.None"/>, not a combination).</summary>
    public static string NoteKey(PkiFindingNotes note) => NoteFlags.Contains(note)
        ? "PkiEsc.Note." + note
        : throw new ArgumentOutOfRangeException(nameof(note), "Not a single note flag.");

    /// <summary>The severity as a list column shows it (<i>Krytyczna</i>).</summary>
    public static string SeverityKey(PkiFindingSeverity severity) => "PkiEsc.Severity." + severity;

    /// <summary>A summary chip (<i>Krytyczne: {0}</i>).</summary>
    public static string ChipKey(PkiFindingSeverity severity) => "PkiEsc.Chip." + severity;

    public static string AreaKey(PkiFindingArea area) => "PkiEsc.Area." + area;

    /// <summary>A notice of a PKI read (<see cref="PkiReadNotice.Code"/>; the snapshot's and the ESC review's notices).</summary>
    public static string NoticeKey(PkiReadNoticeCode code) => "Pki.Notice." + code;

    /// <summary>The kind of an explicit mapping (<see cref="AltSecurityIdentity.Kind"/>; <i>Wystawca i podmiot</i>).</summary>
    public static string MappingKindKey(AltSecurityIdentityKind kind) => "Cert.MappingKind." + kind;

    /// <summary>The texts of the flags set in <paramref name="notes"/>, in declaration order.</summary>
    public static IReadOnlyList<string> Notes(ILocalizer localizer, PkiFindingNotes notes)
    {
        ArgumentNullException.ThrowIfNull(localizer);
        return [.. NoteFlags.Where(n => notes.HasFlag(n)).Select(n => localizer[NoteKey(n)])];
    }

    /// <summary>Every key of the families (both languages must have them).</summary>
    public static IEnumerable<string> AllKeys()
    {
        foreach (var value in Enum.GetValues<PrincipalClass>())
        {
            yield return ClassKey(value);
        }

        foreach (var value in Enum.GetValues<TemplateRight>())
        {
            yield return RightKey(value);
        }

        foreach (var value in Enum.GetValues<MappingStrength>())
        {
            yield return MappingKey(value);
        }

        foreach (var value in Enum.GetValues<TlsProbeOutcome>())
        {
            yield return TlsKey(value);
        }

        foreach (var value in Enum.GetValues<KdcReadinessState>())
        {
            yield return KdcKey(value);
        }

        foreach (var value in Enum.GetValues<PkiHealthStatus>())
        {
            yield return HealthKey(value);
        }

        foreach (var value in NoteFlags)
        {
            yield return NoteKey(value);
        }

        foreach (var value in Enum.GetValues<PkiFindingSeverity>())
        {
            yield return SeverityKey(value);
            yield return ChipKey(value);
        }

        foreach (var value in Enum.GetValues<PkiFindingArea>())
        {
            yield return AreaKey(value);
        }

        foreach (var value in Enum.GetValues<PkiReadNoticeCode>())
        {
            yield return NoticeKey(value);
        }

        foreach (var value in Enum.GetValues<AltSecurityIdentityKind>())
        {
            yield return MappingKindKey(value);
        }
    }
}
