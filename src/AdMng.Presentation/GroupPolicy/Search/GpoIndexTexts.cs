using AdMng.Core.GroupPolicy.Search;
using AdMng.Presentation.GroupPolicy.Settings;
using AdMng.Presentation.Localization;

namespace AdMng.Presentation.GroupPolicy.Search;

/// <summary>
/// K7 contract (docs/dev/k-computers-and-gpo.md 21.3) – implementation: W1. The GPMC texts of an index view: one
/// <see cref="GpoSettingPresenter"/> per entry, built once per view (on <c>Task.Run</c>), never per keystroke.
/// </summary>
public sealed class GpoIndexTexts(ILocalizer localizer, GpoIndexView view)
{
    public ILocalizer Localizer { get; } = localizer ?? throw new ArgumentNullException(nameof(localizer));

    public GpoIndexView View { get; } = view ?? throw new ArgumentNullException(nameof(view));

    /// <summary>The single row the presenter makes of the unit's settings.</summary>
    public GpoSettingText Describe(GpoIndexEntry entry, GpoSettingUnit unit) =>
        throw new NotImplementedException("K7 W1: GpoIndexTexts.Describe");

    /// <summary>DOMAIN\name of a SID (or the name as written).</summary>
    public string PrincipalName(string sidOrName) =>
        throw new NotImplementedException("K7 W1: GpoIndexTexts.PrincipalName");
}
