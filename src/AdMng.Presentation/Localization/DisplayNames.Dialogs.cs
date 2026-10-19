using AdMng.Core.ActiveDirectory;
using AdMng.Core.Batch;
using AdMng.Core.Model;
using AdMng.Core.Schema;
using AdMng.Presentation.BatchEditing;
using AdMng.Presentation.Dialogs;

namespace AdMng.Presentation.Localization;

/// <summary>Key families of the property sheets, batch editor and dialogs (complete in both languages, see tests).</summary>
public static partial class DisplayNames
{
    /// <summary>
    /// Attributes shown on property pages that have a localized fallback name in addition to
    /// <see cref="CommonAttributes"/> (used when the forest has no display specifier for them).
    /// </summary>
    public static IReadOnlyList<string> PropertySheetAttributes { get; } =
    [
        Attr.OtherTelephone, Attr.Url, Attr.OtherHomePhone, Attr.OtherPager, Attr.OtherMobile, Attr.OtherFacsimileTelephoneNumber,
        Attr.OtherIpPhone, Attr.DirectReports, Attr.OperatingSystemServicePack, Attr.AllowedToDelegateTo, Attr.MailNickname,
        Attr.TargetAddress, Attr.HideFromAddressLists, Attr.UserWorkstations, Attr.SupportedEncryptionTypes, Attr.PrimaryGroupId,
        Attr.UsnCreated, Attr.UsnChanged, Attr.UserAccountControlComputed, Attr.ShowInAdvancedViewOnly, Attr.Initials,
        Attr.LogonHours,
    ];

    /// <summary>Object kinds with a plural name (object picker "Object Types").</summary>
    public static IReadOnlyList<ObjectKind> PluralKinds { get; } = Enum.GetValues<ObjectKind>();

    /// <summary>"Country.PL" = Poland / Polska (written to co by the country picker).</summary>
    public static string CountryKey(string code) => $"Country.{code.ToUpperInvariant()}";

    public static string SyntaxKey(AttributeSyntax syntax) => $"Syntax.{syntax}";

    public static string ChangeActionKey(ChangeAction action) => $"ChangeAction.{action}";

    public static string ObjectResultStatusKey(ObjectResultStatus status) => $"ResultStatus.{status}";

    public static string OperationTypeKey(OperationType type) => $"Operation.{type}";

    public static string OperationDescriptionKey(OperationType type) => $"OperationHint.{type}";

    public static string PreviewFilterKey(PreviewFilter filter) => $"PreviewFilter.{filter}";

    public static string ResultFilterKey(ResultFilter filter) => $"ResultFilter.{filter}";

    public static string FindKindKey(FindKind kind) => $"FindKind.{kind}";

    public static string FindOperatorKey(FindOperator op) => $"FindOperator.{op}";

    /// <summary>Plural object kind ("Users", "Groups") for the object picker.</summary>
    public static string KindPluralKey(ObjectKind kind) => $"KindPlural.{kind}";
}
