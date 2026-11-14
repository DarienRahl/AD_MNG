using AdMng.Core.GroupPolicy.Search;
using AdMng.Presentation.Localization;

namespace AdMng.Presentation.GroupPolicy.Copy;

/// <summary>
/// K7 (docs/dev/k-computers-and-gpo.md 21.8): why a unit cannot be copied or removed, in the window's language
/// (<c>GpoCopy.Refusal.&lt;GpoUnitRefusal&gt;</c>, a dynamic key family – tests enumerate <see cref="AllKeys"/>).
/// </summary>
public static class GpoUnitRefusalText
{
    public static string Key(GpoUnitRefusal refusal) => "GpoCopy.Refusal." + refusal;

    public static string Text(ILocalizer localizer, GpoUnitRefusal refusal)
    {
        ArgumentNullException.ThrowIfNull(localizer);
        return localizer[Key(refusal)];
    }

    public static IEnumerable<string> AllKeys() => Enum.GetValues<GpoUnitRefusal>().Select(Key);
}
