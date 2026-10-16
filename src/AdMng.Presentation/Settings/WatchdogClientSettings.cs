namespace AdMng.Presentation.Settings;

/// <summary>
/// The Watchdog tab's settings: the service address, an optional certificate pin, the last sub-tab and named event
/// filters. A URL, UI layout and filters only (like saved views) – never watchdog data (CLAUDE.md rule 1).
/// </summary>
public sealed class WatchdogClientSettings
{
    /// <summary>Most filter presets kept (further ones are dropped on load).</summary>
    public const int MaxFilterPresets = 50;

    /// <summary>https:// address of the watchdog service, e.g. https://admng-wd.contoso.com:8443; null = not configured.</summary>
    public string? ServiceUrl { get; set; }

    /// <summary>Optional SHA-256 (hex) of the service certificate, checked in addition to normal validation.</summary>
    public string? PinnedCertificateSha256 { get; set; }

    /// <summary>Sub-tab shown when the tab opens (a WatchdogSubTab name).</summary>
    public string? LastSubTab { get; set; }

    /// <summary>Named event filters (query strings without paging or long-poll keys).</summary>
    public List<WatchdogFilterPreset> FilterPresets { get; set; } = [];

    /// <summary>Repairs values from a hand-edited or older settings file.</summary>
    public void Normalize()
    {
        ServiceUrl = string.IsNullOrWhiteSpace(ServiceUrl) ? null : ServiceUrl.Trim();
        PinnedCertificateSha256 = string.IsNullOrWhiteSpace(PinnedCertificateSha256) ? null : PinnedCertificateSha256.Trim();
        FilterPresets ??= [];
        FilterPresets.RemoveAll(p => p is null || string.IsNullOrWhiteSpace(p.Name) || p.Query is null);
        if (FilterPresets.Count > MaxFilterPresets)
        {
            FilterPresets.RemoveRange(MaxFilterPresets, FilterPresets.Count - MaxFilterPresets);
        }
    }
}
