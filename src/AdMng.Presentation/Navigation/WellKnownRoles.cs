using AdMng.Core.Directory;
using AdMng.Presentation.Localization;

namespace AdMng.Presentation.Navigation;

/// <summary>Localized descriptions of the well-known containers ("Default container for new users and groups").</summary>
public static class WellKnownRoles
{
    /// <summary>Resource key of a role: <c>Tree.WellKnown.&lt;container&gt;</c> (both languages, see Strings*.resx).</summary>
    public static string Key(WellKnownContainer container) => "Tree.WellKnown." + container;

    /// <summary>The role description in the current language.</summary>
    public static string Text(ILocalizer localizer, WellKnownContainer container)
    {
        ArgumentNullException.ThrowIfNull(localizer);
        return localizer[Key(container)];
    }
}
