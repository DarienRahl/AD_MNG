using AdMng.Core.Correspondence.Import;
using AdMng.Presentation.Localization;

namespace AdMng.Presentation.Correspondence;

public static partial class CorrespondenceText
{
    /// <summary><c>Correspondence.Status.{kind}</c>: the base text of a row status ("wysłano", "błąd" …); none for <see cref="RecipientStatusKind.None"/>.</summary>
    public static string StatusKey(RecipientStatusKind kind) => "Correspondence.Status." + kind;

    /// <summary>The localized base text of a row status; "" for <see cref="RecipientStatusKind.None"/>.</summary>
    public static string StatusText(ILocalizer localizer, RecipientStatusKind kind)
    {
        ArgumentNullException.ThrowIfNull(localizer);
        return kind == RecipientStatusKind.None ? string.Empty : localizer[StatusKey(kind)];
    }

    /// <summary><c>Correspondence.Format.{format}</c>: the kind of source contacts were read from ("Excel", "Word" …).</summary>
    public static string FormatKey(ContactSourceFormat format) => "Correspondence.Format." + format;

    /// <summary>The Polish plural form of <paramref name="count"/>: one (1), few (2–4, 22–24 … but not 12–14) or many.</summary>
    internal static string Plural(ILocalizer localizer, int count, string one, string few, string many)
    {
        ArgumentNullException.ThrowIfNull(localizer);
        var key = count == 1 ? one : count % 10 is >= 2 and <= 4 && count % 100 is < 12 or > 14 ? few : many;
        return localizer.Format(key, count);
    }

    /// <summary>Dynamic keys of the recipients page (track recipients).</summary>
    private static partial IEnumerable<string> RecipientsKeys() =>
    [
        .. Enum.GetValues<RecipientStatusKind>().Where(k => k != RecipientStatusKind.None).Select(StatusKey),
        .. Enum.GetValues<ContactSourceFormat>().Select(FormatKey),
    ];
}
