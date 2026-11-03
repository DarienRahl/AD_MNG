using AdMng.Core.GroupPolicy.Diagnostics;
using AdMng.Presentation.Localization;

namespace AdMng.Presentation.GroupPolicy.Diagnostics;

/// <summary>
/// The resource keys and plain texts of the GPO diagnostics (K6, design note docs/dev/k-computers-and-gpo.md 20.2): per
/// finding code a title, a message (formatted with the code's <see cref="GpoFinding.Arguments"/> in the order of
/// <see cref="GpoFindingCodes"/>), an explanation (why it matters) and a recommendation (what to do); the names of the
/// severities, areas, rule groups, read stages and repairs. Every key exists in English and Polish (the localization
/// tests enumerate <see cref="AllKeys"/>).
/// </summary>
public static class GpoFindingText
{
    /// <summary>
    /// The number of <see cref="GpoFinding.Arguments"/> of each code, as the XML comments of <see cref="GpoFindingCodes"/>
    /// list them; the message of the code takes exactly these placeholders.
    /// </summary>
    public static IReadOnlyDictionary<string, int> ArgumentCounts { get; } = new Dictionary<string, int>(StringComparer.Ordinal)
    {
        [GpoFindingCodes.GpoNotLinked] = 0,
        [GpoFindingCodes.GpoLinksAllDisabled] = 1,
        [GpoFindingCodes.LinkToMissingGpo] = 1,
        [GpoFindingCodes.LinkMalformed] = 2,
        [GpoFindingCodes.DuplicateLink] = 1,
        [GpoFindingCodes.LinkedAllSettingsDisabled] = 0,
        [GpoFindingCodes.PartDisabledWithSettings] = 2,
        [GpoFindingCodes.PartEnabledWithoutSettings] = 1,
        [GpoFindingCodes.EmptyGpoLinked] = 0,
        [GpoFindingCodes.UserSettingsWithoutUsers] = 0,
        [GpoFindingCodes.ComputerSettingsWithoutComputers] = 0,
        [GpoFindingCodes.AccountPolicyBelowDomain] = 0,
        [GpoFindingCodes.LoopbackWithoutComputers] = 1,
        [GpoFindingCodes.NoApplyRight] = 0,
        [GpoFindingCodes.Ms16072ComputersCannotRead] = 0,
        [GpoFindingCodes.WmiFilterMissing] = 1,
        [GpoFindingCodes.WmiFilterUnused] = 2,
        [GpoFindingCodes.DenyInSecurityFiltering] = 1,
        [GpoFindingCodes.OrphanedTrustee] = 1,
        [GpoFindingCodes.BroadEditRights] = 1,
        [GpoFindingCodes.VersionMismatch] = 4,
        [GpoFindingCodes.SysvolFolderMissing] = 1,
        [GpoFindingCodes.FolderSecurityMismatch] = 0,
        [GpoFindingCodes.ExtensionNamesMissing] = 1,
        [GpoFindingCodes.UnreadableFile] = 1,
        [GpoFindingCodes.Leftovers] = 1,
        [GpoFindingCodes.PathRedirected] = 1,
        [GpoFindingCodes.RestrictedAndPreferenceSameGroup] = 1,
        [GpoFindingCodes.RestrictedAdministratorsNoAdmins] = 0,
        [GpoFindingCodes.LegacyAndAdvancedAudit] = 0,
        [GpoFindingCodes.PasswordLengthNeedsRelax] = 1,
        [GpoFindingCodes.TypedPrincipalName] = 1,
        [GpoFindingCodes.UnresolvedPrincipal] = 1,
        [GpoFindingCodes.DenyLogonBroadGroup] = 1,
        [GpoFindingCodes.ConflictingSetting] = 4,
        [GpoFindingCodes.RedundantSetting] = 3,
        [GpoFindingCodes.GppPassword] = 1,
        [GpoFindingCodes.DangerousRightBroadGroup] = 1,
        [GpoFindingCodes.BroadGroupInPrivilegedLocalGroup] = 2,
        [GpoFindingCodes.LmCompatibilityWeak] = 1,
        [GpoFindingCodes.LmHashStored] = 0,
        [GpoFindingCodes.SmbSigningNotRequired] = 0,
        [GpoFindingCodes.LdapSigningNotRequired] = 1,
        [GpoFindingCodes.AnonymousAccessAllowed] = 1,
        [GpoFindingCodes.ReversibleEncryption] = 0,
        [GpoFindingCodes.GuestEnabled] = 0,
        [GpoFindingCodes.UacDisabled] = 0,
        [GpoFindingCodes.PlainTextSmbPassword] = 0,
        [GpoFindingCodes.WeakDomainPasswordPolicy] = 2,
    };

    /// <summary>The severities from the most serious down (the order of the summary chips).</summary>
    public static IReadOnlyList<GpoFindingSeverity> Severities { get; } =
        [GpoFindingSeverity.Critical, GpoFindingSeverity.High, GpoFindingSeverity.Medium, GpoFindingSeverity.Low, GpoFindingSeverity.Info];

    public static string TitleKey(string code) => "GpoFinding." + code + ".Title";

    public static string MessageKey(string code) => "GpoFinding." + code + ".Message";

    public static string ExplanationKey(string code) => "GpoFinding." + code + ".Explanation";

    public static string RecommendationKey(string code) => "GpoFinding." + code + ".Recommendation";

    /// <summary>The severity as the list's column shows it ("Wysoka").</summary>
    public static string SeverityKey(GpoFindingSeverity severity) => "GpoDiag.Severity." + severity;

    /// <summary>A summary chip ("Wysokie: {0}").</summary>
    public static string ChipKey(GpoFindingSeverity severity) => "GpoDiag.Chip." + severity;

    public static string AreaKey(GpoFindingArea area) => "GpoDiag.Area." + area;

    public static string GroupKey(GpoRuleGroup group) => "GpoDiag.Group." + group;

    /// <summary>The progress text of a read stage ({0} done, {1} total).</summary>
    public static string StageKey(GpoDiagnosticsStage stage) => "GpoDiag.Stage." + stage;

    /// <summary>What a repair does ("Wyrównaj wersje"); <c>SetStatus</c>, <c>GrantRead</c> and <c>RemoveTrustee</c> take {0}.</summary>
    public static string FixKey(GpoQuickFix fix) => FixKey((fix ?? throw new ArgumentNullException(nameof(fix))).GetType().Name);

    public static string FixKey(string fixKind) => "GpoDiag.FixKind." + fixKind;

    /// <summary>The kinds of repair (the nested records of <see cref="GpoQuickFix"/>).</summary>
    public static IReadOnlyList<string> FixKinds { get; } =
        [.. typeof(GpoQuickFix).GetNestedTypes().Where(t => t.IsSubclassOf(typeof(GpoQuickFix))).Select(t => t.Name).Order(StringComparer.Ordinal)];

    /// <summary>Every dynamic key of the diagnostics (both languages must have them).</summary>
    public static IEnumerable<string> AllKeys()
    {
        foreach (var definition in GpoFindingCodes.All)
        {
            yield return TitleKey(definition.Code);
            yield return MessageKey(definition.Code);
            yield return ExplanationKey(definition.Code);
            yield return RecommendationKey(definition.Code);
        }

        foreach (var severity in Enum.GetValues<GpoFindingSeverity>())
        {
            yield return SeverityKey(severity);
            yield return ChipKey(severity);
        }

        foreach (var area in Enum.GetValues<GpoFindingArea>())
        {
            yield return AreaKey(area);
        }

        foreach (var group in Enum.GetValues<GpoRuleGroup>())
        {
            yield return GroupKey(group);
        }

        foreach (var stage in Enum.GetValues<GpoDiagnosticsStage>())
        {
            yield return StageKey(stage);
        }

        foreach (var kind in FixKinds)
        {
            yield return FixKey(kind);
        }
    }

    public static string Severity(ILocalizer localizer, GpoFindingSeverity severity) =>
        (localizer ?? throw new ArgumentNullException(nameof(localizer)))[SeverityKey(severity)];

    public static string Area(ILocalizer localizer, GpoFindingArea area) =>
        (localizer ?? throw new ArgumentNullException(nameof(localizer)))[AreaKey(area)];

    public static string Group(ILocalizer localizer, GpoRuleGroup group) =>
        (localizer ?? throw new ArgumentNullException(nameof(localizer)))[GroupKey(group)];

    /// <summary>"Odczyt folderów SYSVOL: 3 z 8".</summary>
    public static string Stage(ILocalizer localizer, GpoDiagnosticsProgress progress)
    {
        ArgumentNullException.ThrowIfNull(localizer);
        ArgumentNullException.ThrowIfNull(progress);
        return localizer.Format(StageKey(progress.Stage), progress.Done, progress.Total);
    }

    /// <summary>"Wyrównaj wersje", "Nadaj uprawnienie „Odczyt” dla: Użytkownicy uwierzytelnieni".</summary>
    /// <param name="localizer">The UI language.</param>
    /// <param name="fix">The repair.</param>
    /// <param name="nameOf">The display name of a SID (a trustee of <c>GrantRead</c>/<c>RemoveTrustee</c>).</param>
    public static string Fix(ILocalizer localizer, GpoQuickFix fix, Func<string, string> nameOf)
    {
        ArgumentNullException.ThrowIfNull(localizer);
        ArgumentNullException.ThrowIfNull(fix);
        ArgumentNullException.ThrowIfNull(nameOf);
        return fix switch
        {
            GpoQuickFix.SetStatus status => localizer.Format(FixKey(fix), GroupPolicyText.Status(localizer, status.Status)),
            GpoQuickFix.GrantRead grant => localizer.Format(FixKey(fix), nameOf(grant.Sid)),
            GpoQuickFix.RemoveTrustee remove => localizer.Format(FixKey(fix), nameOf(remove.Sid)),
            _ => localizer[FixKey(fix)],
        };
    }

    /// <summary>The name of a GPO part in a message ("użytkownika"/"komputera"), or the argument as given.</summary>
    internal static string Part(ILocalizer localizer, string part) => part switch
    {
        "User" => localizer[L.GpoDiagnostics.PartUser],
        "Computer" => localizer[L.GpoDiagnostics.PartComputer],
        _ => part,
    };

    /// <summary>The text of a folder read status in the "not read" note.</summary>
    internal static string FolderStatus(ILocalizer localizer, GpoFolderReadStatus? status) => localizer[status switch
    {
        GpoFolderReadStatus.AccessDenied => L.GpoDiagnostics.FolderAccessDenied,
        GpoFolderReadStatus.TooLarge => L.GpoDiagnostics.FolderTooLarge,
        _ => L.GpoDiagnostics.FolderFailed,
    }];
}
