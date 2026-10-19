using AdMng.Presentation.Localization;

namespace AdMng.Presentation.Connection;

/// <summary>One row of the "Change domain controller" list: the entry with its localized texts.</summary>
public sealed class DomainControllerItem
{
    public DomainControllerItem(DomainControllerEntry entry, ILocalizer localizer)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(localizer);
        Entry = entry;
        SiteText = entry.Site ?? string.Empty;
        InClientSiteText = entry.IsInClientSite ? localizer[L.Common.Yes] : string.Empty;
        var roles = new List<string>();
        if (entry.IsCurrent)
        {
            roles.Add(localizer[L.ChangeDc.RoleCurrent]);
        }

        if (entry.IsPdcEmulator)
        {
            roles.Add(localizer[L.ChangeDc.RolePdcEmulator]);
        }

        if (entry.IsReadOnly)
        {
            roles.Add(localizer[L.Connect.ReadOnlyDc]);
        }

        if (entry.IsGlobalCatalog)
        {
            roles.Add(localizer[L.Connect.GlobalCatalog]);
        }

        RolesText = string.Join(", ", roles);

        // Every column the row shows, also "your site" (the column only says "Yes").
        var yourSite = entry.IsInClientSite ? localizer[L.ChangeDc.ColumnYourSite] : string.Empty;
        AutomationName = string.Join(", ", new[] { entry.HostName, SiteText, yourSite, RolesText }.Where(t => t.Length > 0));
    }

    public DomainControllerEntry Entry { get; }

    public string HostName => Entry.HostName;

    /// <summary>Site name, empty when unknown.</summary>
    public string SiteText { get; }

    /// <summary>"Yes" when the DC is in this computer's site, else empty.</summary>
    public string InClientSiteText { get; }

    /// <summary>"Current, PDC emulator, Read-only, Global catalog" (the roles that apply).</summary>
    public string RolesText { get; }

    /// <summary>Screen reader text of the row: name, site, "your site" and the roles.</summary>
    public string AutomationName { get; }

    public bool IsCurrent => Entry.IsCurrent;

    public bool IsReadOnly => Entry.IsReadOnly;

    public override string ToString() => AutomationName;
}
