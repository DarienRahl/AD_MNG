using AdMng.Core.Pki;
using AdMng.Core.PrivilegedAccess;
using AdMng.Presentation.AccountReview;
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

    /// <summary>
    /// The finding's message with its arguments; the list arguments that hold enum names (the rights of
    /// <see cref="PkiFindingCodes.TemplateWriteable"/> and <see cref="PkiFindingCodes.PkiObjectWriteable"/>, the mapping kinds or
    /// rights of <see cref="PkiFindingCodes.WeakExplicitMapping"/>) are shown through <see cref="Argument"/>.
    /// </summary>
    public static string Message(ILocalizer localizer, PkiFinding finding)
    {
        ArgumentNullException.ThrowIfNull(localizer);
        ArgumentNullException.ThrowIfNull(finding);
        return localizer.Format(MessageKey(finding.Code), [.. finding.Arguments.Select((argument, index) => Argument(localizer, finding.Code, index, argument))]);
    }

    /// <summary>
    /// One argument as the message shows it: an enum-name list argument (<see cref="PkiFindingCodes"/>) has each name replaced by
    /// its resource – <c>PkiTpl.Right.*</c> (ESC4 {2}), <c>PrivilegedAccess.AclRight.*</c> (ESC5 {2}, ESC14 {1}) or
    /// <c>Cert.MappingKind.*</c> (ESC14 {1}) – joined with <see cref="PkiFindingCodes.ListSeparator"/>; a name of no family and
    /// every other argument (names, counts) stay as given.
    /// </summary>
    public static string Argument(ILocalizer localizer, string code, int index, string argument)
    {
        ArgumentNullException.ThrowIfNull(localizer);
        ArgumentNullException.ThrowIfNull(code);
        ArgumentNullException.ThrowIfNull(argument);
        return (code, index) switch
        {
            (PkiFindingCodes.TemplateWriteable, 2) => Names(argument, name =>
                TryName<TemplateRight>(name, out var right) ? localizer[PkiTextKeys.RightKey(right)] : null),
            (PkiFindingCodes.PkiObjectWriteable, 2) => Names(argument, name =>
                TryName<AclRight>(name, out var right) ? localizer[PrivilegedAccessText.AclRightKey(right)] : null),
            (PkiFindingCodes.WeakExplicitMapping, 1) => Names(argument, name =>
                TryName<AltSecurityIdentityKind>(name, out var kind) ? localizer[PkiTextKeys.MappingKindKey(kind)]
                : TryName<AclRight>(name, out var right) ? localizer[PrivilegedAccessText.AclRightKey(right)]
                : null),
            _ => argument,
        };
    }

    public static string Severity(ILocalizer localizer, PkiFindingSeverity severity) =>
        (localizer ?? throw new ArgumentNullException(nameof(localizer)))[PkiTextKeys.SeverityKey(severity)];

    public static string Area(ILocalizer localizer, PkiFindingArea area) =>
        (localizer ?? throw new ArgumentNullException(nameof(localizer)))[PkiTextKeys.AreaKey(area)];

    private static string Names(string argument, Func<string, string?> localize)
    {
        if (argument.Length == 0)
        {
            return argument;
        }

        var names = argument.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        return string.Join(PkiFindingCodes.ListSeparator, names.Select(name => localize(name) ?? name));
    }

    /// <summary>A declared name of <typeparamref name="T"/> (ordinal; never a number, which <see cref="Enum.TryParse{TEnum}(string, out TEnum)"/> also accepts).</summary>
    private static bool TryName<T>(string name, out T value)
        where T : struct, Enum
    {
        value = default;
        return Enum.GetNames<T>().Contains(name, StringComparer.Ordinal) && Enum.TryParse(name, ignoreCase: false, out value);
    }
}
