using AdMng.Presentation.Icons;
using AdMng.Presentation.Localization;
using AdMng.Presentation.Navigation;
using AdMng.Watchdog.Contracts.Events;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AdMng.Presentation.Watchdog;

/// <summary>
/// One row of the event feed: local time, severity, kind, object name and canonical path, actor ("ustalanie…",
/// "nieustalony"), source (IP first, name with origin), lockout count ("5× dziś") and the acknowledged flag. Texts are
/// computed once per event version (the feed holds thousands of rows).
/// </summary>
public sealed partial class WatchdogEventRowViewModel : ObservableObject
{
    private readonly ILocalizer _localizer;
    private readonly TimeZoneInfo _zone;

    public WatchdogEventRowViewModel(WatchEvent watchEvent, ILocalizer localizer, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(watchEvent);
        _localizer = localizer ?? throw new ArgumentNullException(nameof(localizer));
        _zone = zone ?? throw new ArgumentNullException(nameof(zone));
        Event = watchEvent;
        Refresh();
    }

    /// <summary>The event as the service returned it (the latest version known to the feed).</summary>
    [ObservableProperty]
    public partial WatchEvent Event { get; private set; }

    public long Id => Event.Id;

    public Severity Severity => Event.Severity;

    public ObjectIcon Icon => WatchdogDisplay.IconFor(Event.Object.Kind);

    [ObservableProperty]
    public partial string TimeText { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial string SeverityText { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial string KindText { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial string ObjectName { get; private set; } = string.Empty;

    /// <summary>Canonical path of the object ("contoso.test/Firma/Pracownicy/IT/Jan Kowalski"); the last live one for deletions.</summary>
    [ObservableProperty]
    public partial string ObjectPath { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial string ActorText { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial string SourceText { get; private set; } = string.Empty;

    /// <summary>"5× dziś" for lockouts, empty otherwise.</summary>
    [ObservableProperty]
    public partial string LockoutText { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsAcknowledged { get; private set; }

    /// <summary>Attribution still pending (shown in italics).</summary>
    [ObservableProperty]
    public partial bool IsActorPending { get; private set; }

    /// <summary>Arrived through live polling (highlighted until the operator scrolls back to the top).</summary>
    [ObservableProperty]
    public partial bool IsNew { get; set; }

    /// <summary>Replaces the event with a newer version (acknowledged, late attribution).</summary>
    public void Update(WatchEvent watchEvent)
    {
        ArgumentNullException.ThrowIfNull(watchEvent);
        Event = watchEvent;
        Refresh();
    }

    /// <summary>Recomputes the texts (after a language switch).</summary>
    public void Refresh()
    {
        var e = Event;
        TimeText = _localizer.FormatTimeWithSeconds(e.DetectedAt, _zone);
        SeverityText = _localizer.SeverityName(e.Severity);
        KindText = _localizer.KindName(e.Kind);
        ObjectName = NameOf(e.Object, _localizer);
        var pathDn = e.Kind == WatchEventKind.ObjectDeleted && e.PreviousDn is { Length: > 0 } previous ? previous : e.Object.Dn;
        ObjectPath = string.IsNullOrEmpty(pathDn) ? string.Empty : AddressResolver.ToDisplayPath(pathDn);
        ActorText = WatchdogAttributionText.Actor(_localizer, e.Attribution);
        SourceText = WatchdogAttributionText.Source(_localizer, e.Attribution);
        LockoutText = e.Lockout is { } lockout ? _localizer.Format(L.Watchdog.LockoutsToday, lockout.LockoutsToday) : string.Empty;
        IsAcknowledged = e.AcknowledgedAt is not null;
        IsActorPending = e.Attribution?.Status == AttributionStatus.Pending;
        OnPropertyChanged(nameof(Severity));
        OnPropertyChanged(nameof(Icon));
    }

    /// <summary>Display name, else the RDN value, else the watchdog's own name.</summary>
    public static string NameOf(ObjectRef obj, ILocalizer localizer)
    {
        ArgumentNullException.ThrowIfNull(obj);
        ArgumentNullException.ThrowIfNull(localizer);
        if (obj.Kind == WatchObjectKind.Watchdog)
        {
            return localizer[L.Watchdog.Title];
        }

        if (!string.IsNullOrEmpty(obj.DisplayName))
        {
            return obj.DisplayName;
        }

        if (!string.IsNullOrEmpty(obj.Name))
        {
            return obj.Name;
        }

        return Core.Ldap.DistinguishedName.TryParse(obj.Dn, out var dn) && !dn.IsRoot ? dn.Name : obj.Dn;
    }
}
