namespace AdMng.Presentation.Watchdog;

/// <summary>Sub-tabs of the Watchdog tab (the selection is remembered in the settings).</summary>
public enum WatchdogSubTab
{
    Events,
    Rules,
    Channels,
    Digest,
    Status,

    /// <summary>"Źródła i retencja" (stage B design 18 item 4); last, so the stored indexes of the others stay.</summary>
    Sources,
}
