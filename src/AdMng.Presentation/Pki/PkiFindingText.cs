using AdMng.Core.Pki;
using AdMng.Presentation.Localization;

namespace AdMng.Presentation.Pki;

/// <summary>
/// The resource keys and plain texts of the AD CS security review (docs/dev/p-certificate-services.md 2.4, 2.8; modelled
/// on <c>GpoFindingText</c>): per finding code a title, a message (formatted with the code's <see cref="PkiFinding.Arguments"/>
/// in the order of <see cref="PkiFindingCodes"/>), an explanation (why it matters) and a recommendation (what to do). Every
/// key exists in English and Polish (the tests enumerate <see cref="AllKeys"/>); the names of the classes, rights, notes,
/// severities and areas are <see cref="PkiTextKeys"/>.
/// </summary>
public static class PkiFindingText
{
    /// <summary>
    /// The number of <see cref="PkiFinding.Arguments"/> of each code, as the XML comments of <see cref="PkiFindingCodes"/> list
    /// them; the message of the code takes exactly these placeholders (at most 8).
    /// </summary>
    public static IReadOnlyDictionary<string, int> ArgumentCounts { get; } = new Dictionary<string, int>(StringComparer.Ordinal)
    {
        [PkiFindingCodes.EnrolleeSuppliesSubjectClientAuth] = 3,
        [PkiFindingCodes.AnyPurposeOrNoEku] = 3,
        [PkiFindingCodes.EnrollmentAgentTemplate] = 3,
        [PkiFindingCodes.EnrollmentAgentTargetTemplate] = 3,
        [PkiFindingCodes.TemplateWriteable] = 3,
        [PkiFindingCodes.PkiObjectWriteable] = 3,
        [PkiFindingCodes.NoSecurityExtensionClientAuth] = 3,
        [PkiFindingCodes.IssuancePolicyLinkedToGroup] = 4,
        [PkiFindingCodes.WeakExplicitMapping] = 3,
        [PkiFindingCodes.SchemaV1EnrolleeSuppliesSubject] = 3,
        [PkiFindingCodes.EnrolleeSuppliesSubjectServerAuth] = 3,
    };

    /// <summary>The severities from the most serious down (the order of the summary chips).</summary>
    public static IReadOnlyList<PkiFindingSeverity> Severities { get; } =
        [PkiFindingSeverity.Critical, PkiFindingSeverity.High, PkiFindingSeverity.Medium, PkiFindingSeverity.Low, PkiFindingSeverity.Info];

    public static string TitleKey(string code) => "PkiFinding." + code + ".Title";

    public static string MessageKey(string code) => "PkiFinding." + code + ".Message";

    public static string ExplanationKey(string code) => "PkiFinding." + code + ".Explanation";

    public static string RecommendationKey(string code) => "PkiFinding." + code + ".Recommendation";

    /// <summary>Every finding text key (both languages must have them).</summary>
    public static IEnumerable<string> AllKeys()
    {
        foreach (var definition in PkiFindingCodes.All)
        {
            yield return TitleKey(definition.Code);
            yield return MessageKey(definition.Code);
            yield return ExplanationKey(definition.Code);
            yield return RecommendationKey(definition.Code);
        }
    }

    /// <summary>The finding's message with its arguments.</summary>
    public static string Message(ILocalizer localizer, PkiFinding finding)
    {
        ArgumentNullException.ThrowIfNull(localizer);
        ArgumentNullException.ThrowIfNull(finding);
        return localizer.Format(MessageKey(finding.Code), [.. finding.Arguments]);
    }

    public static string Severity(ILocalizer localizer, PkiFindingSeverity severity) =>
        (localizer ?? throw new ArgumentNullException(nameof(localizer)))[PkiTextKeys.SeverityKey(severity)];

    public static string Area(ILocalizer localizer, PkiFindingArea area) =>
        (localizer ?? throw new ArgumentNullException(nameof(localizer)))[PkiTextKeys.AreaKey(area)];
}
