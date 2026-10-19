using System.Collections.ObjectModel;
using System.Text;
using AdMng.Core.ActiveDirectory;
using AdMng.Core.Batch;
using AdMng.Core.Directory;
using AdMng.Core.Ldap;
using AdMng.Presentation.Changes;
using AdMng.Presentation.Infrastructure;
using AdMng.Presentation.Localization;
using AdMng.Presentation.Navigation;
using AdMng.Presentation.Services;
using AdMng.Watchdog.Contracts.Api;
using AdMng.Watchdog.Contracts.Events;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EventAttribution = AdMng.Watchdog.Contracts.Events.Attribution;

namespace AdMng.Presentation.Watchdog;

/// <summary>
/// Details of the selected event (<c>GET /events/{id}</c>): the changes (attribute display names from the session schema
/// when connected), the attribution (actor, source with origin and confidence, method, reason and remedy, evidence;
/// lockouts: "Źródło błędnych haseł"), related events and deliveries. Commands: acknowledge, open the object in AD_MNG
/// (current DN by objectGUID – the stored DN may be stale; a deleted object opens the Recycle Bin), unlock a still locked
/// account through the change pipeline (preview, CLAUDE.md rule 5) and copy the details.
/// </summary>
public sealed partial class WatchdogEventDetailsViewModel : ObservableObject, IDisposable
{
    private static readonly string[] LockAttributes =
        [Attr.ObjectClass, Attr.UserAccountControl, Attr.UserAccountControlComputed, Attr.LockoutTime, Attr.Name];

    private readonly WatchdogServices _services;
    private readonly SerialCancellation _loads = new();
    private string? _lockedDn;
    private bool _disposed;

    public WatchdogEventDetailsViewModel(WatchdogServices services)
    {
        _services = services ?? throw new ArgumentNullException(nameof(services));
    }

    /// <summary>The event was acknowledged here (the feed updates its row).</summary>
    public event EventHandler<WatchEvent>? Acknowledged;

    /// <summary>Open the live object (current DN) in the main window.</summary>
    public event EventHandler<string>? LocateRequested;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasEvent))]
    [NotifyCanExecuteChangedFor(nameof(AcknowledgeCommand), nameof(OpenInAdMngCommand), nameof(CopyDetailsCommand))]
    public partial WatchEvent? Event { get; private set; }

    public bool HasEvent => Event is not null;

    [ObservableProperty]
    public partial bool IsLoading { get; private set; }

    [ObservableProperty]
    public partial string? Error { get; private set; }

    /// <summary>Result of the last action (open in AD_MNG, unlock) shown inline.</summary>
    [ObservableProperty]
    public partial string? ActionMessage { get; private set; }

    [ObservableProperty]
    public partial string Title { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial string SeverityText { get; private set; } = string.Empty;

    public Severity Severity => Event?.Severity ?? Severity.Info;

    [ObservableProperty]
    public partial string TimeText { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial string ObjectName { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial string ObjectKindText { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial string ObjectPath { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial string ObjectDn { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial string PreviousDn { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial string ObjectGuidText { get; private set; } = string.Empty;

    /// <summary>"dc01.contoso.test · 12:29:48" – where and when the change originated.</summary>
    [ObservableProperty]
    public partial string OriginText { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial string TagsText { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial string RulesText { get; private set; } = string.Empty;

    /// <summary>"Potwierdzone 14.10.2026 12:31 przez CONTOSO\jan.kowalski: komentarz", empty when not acknowledged.</summary>
    [ObservableProperty]
    public partial string AcknowledgedText { get; private set; } = string.Empty;

    /// <summary>GroupMember*: the member; Privilege*: the privileged group.</summary>
    [ObservableProperty]
    public partial string MemberText { get; private set; } = string.Empty;

    /// <summary>Privilege*: "Łukasz Wiśniewski → GG_Administratorzy_Serwerów → Domain Admins".</summary>
    [ObservableProperty]
    public partial string ViaText { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial string ExpiryText { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial string BurstText { get; private set; } = string.Empty;

    public ObservableCollection<WatchdogChangeRowViewModel> Changes { get; } = [];

    // ---- Attribution ------------------------------------------------------------------------------------------

    [ObservableProperty]
    public partial string AttributionStatusText { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial string ActorText { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial string SourceText { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial string SourceConfidenceText { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial string ConfidenceText { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial string ReasonText { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial string RemedyText { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial string SourceReasonText { get; private set; } = string.Empty;

    public ObservableCollection<string> Candidates { get; } = [];

    public ObservableCollection<WatchdogEvidenceRowViewModel> Evidence { get; } = [];

    // ---- Lockout ----------------------------------------------------------------------------------------------

    [ObservableProperty]
    public partial bool HasLockout { get; private set; }

    [ObservableProperty]
    public partial string LockoutText { get; private set; } = string.Empty;

    public ObservableCollection<WatchdogLockoutSourceRowViewModel> LockoutSources { get; } = [];

    public ObservableCollection<WatchdogEventRowViewModel> RelatedEvents { get; } = [];

    public ObservableCollection<WatchdogDeliveryRowViewModel> Deliveries { get; } = [];

    [ObservableProperty]
    public partial string AcknowledgeComment { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AcknowledgeCommand), nameof(OpenInAdMngCommand), nameof(UnlockAccountCommand))]
    public partial bool IsBusy { get; private set; }

    /// <summary>The event's account is still locked out and an AD session to its domain is open.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(UnlockAccountCommand))]
    public partial bool CanUnlock { get; private set; }

    /// <summary>The current load (tests await it).</summary>
    public Task Loading { get; private set; } = Task.CompletedTask;

    /// <summary>Shows the event of <paramref name="row"/> (null clears the pane).</summary>
    public Task ShowAsync(WatchdogEventRowViewModel? row)
    {
        Loading = ShowCoreAsync(row?.Event);
        return Loading;
    }

    /// <summary>Replaces the shown event with a newer version (acknowledged in the feed).</summary>
    public void UpdateEvent(WatchEvent watchEvent)
    {
        ArgumentNullException.ThrowIfNull(watchEvent);
        if (Event?.Id == watchEvent.Id)
        {
            Event = watchEvent;
            Fill(watchEvent, null);
        }
    }

    /// <summary>Texts after a language switch.</summary>
    public void Relocalize()
    {
        if (Event is { } shown)
        {
            Fill(shown, null);
        }
    }

    /// <summary>An AD session was opened or closed: "Pokaż w AD Manager" and the unlock offer are re-evaluated.</summary>
    public void NotifySessionChanged()
    {
        OpenInAdMngCommand.NotifyCanExecuteChanged();
        if (Event is { } shown && !_disposed)
        {
            Loading = CheckLockAsync(shown, CancellationToken.None);
        }
    }

    public void Dispose()
    {
        _disposed = true;
        _loads.Cancel();
    }

    [RelayCommand(CanExecute = nameof(CanAcknowledge))]
    private async Task AcknowledgeAsync()
    {
        if (Event is not { } shown)
        {
            return;
        }

        var comment = string.IsNullOrWhiteSpace(AcknowledgeComment) ? null : AcknowledgeComment.Trim();
        IsBusy = true;
        try
        {
            await _services.Client.AcknowledgeAsync(shown.Id, new AcknowledgeRequest { Comment = comment }, CancellationToken.None).ConfigureAwait(true);
            var acknowledged = shown with
            {
                AcknowledgedAt = _services.Time.GetUtcNow(),
                AcknowledgedBy = Account,
                AcknowledgeComment = comment,
            };
            AcknowledgeComment = string.Empty;
            if (Event?.Id == shown.Id)
            {
                Event = acknowledged;
                Fill(acknowledged, null);
            }

            Acknowledged?.Invoke(this, acknowledged);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Error = _services.Connection.Describe(ex);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool CanAcknowledge() => !IsBusy && Event is { AcknowledgedAt: null };

    /// <summary>The caller's account (shown as the acknowledger until the next reload).</summary>
    public string? Account { get; set; }

    /// <summary>Finds the object by objectGUID in the open session and opens it; a deleted object opens the Recycle Bin.</summary>
    [RelayCommand(CanExecute = nameof(CanOpenInAdMng))]
    private async Task OpenInAdMngAsync()
    {
        if (Event?.Object.ObjectGuid is not { } guid || SessionForEvent() is not { } session)
        {
            return;
        }

        IsBusy = true;
        ActionMessage = null;
        try
        {
            var dn = await FindLiveDnAsync(session, guid, CancellationToken.None).ConfigureAwait(true);
            if (dn is not null)
            {
                LocateRequested?.Invoke(this, dn);
            }
            else
            {
                ActionMessage = _services.Localizer[L.Watchdog.DetailsObjectDeleted];
                await _services.Dialogs.ShowRecycleBinAsync(guid).ConfigureAwait(true);
            }
        }
        catch (DirectoryException ex)
        {
            ActionMessage = _services.Ui.Errors.Translate(ex);
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Opening needs the object's GUID and an AD session to the event's domain.</summary>
    public bool CanOpenInAdMng() => !IsBusy && Event?.Object.ObjectGuid is not null && SessionForEvent() is not null;

    /// <summary>Unlocks the still locked account of a lockout event: planned and previewed like every write.</summary>
    [RelayCommand(CanExecute = nameof(CanUnlockAccount))]
    private async Task UnlockAccountAsync()
    {
        if (_lockedDn is not { } dn)
        {
            return;
        }

        IsBusy = true;
        try
        {
            var title = _services.Localizer[L.Commands.Unlock];
            var outcome = await _services.Ui.Coordinator.ApplyAsync(
                new ChangeRequest([dn], [new UnlockAccountOperation()], title) { ForcePreview = true }).ConfigureAwait(true);
            if (outcome is ChangeOutcome.Executed or ChangeOutcome.OpenedInEditor)
            {
                ActionMessage = outcome == ChangeOutcome.Executed ? _services.Localizer[L.Watchdog.DetailsUnlocked] : null;
                await CheckLockAsync(Event, CancellationToken.None).ConfigureAwait(true);
            }
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool CanUnlockAccount() => CanUnlock && !IsBusy;

    [RelayCommand(CanExecute = nameof(HasEvent))]
    private void CopyDetails()
    {
        if (Event is null)
        {
            return;
        }

        var localizer = _services.Localizer;
        var text = new StringBuilder();
        text.AppendLine($"{Title} ({SeverityText})");
        text.AppendLine($"{localizer[L.Watchdog.ColumnTime]}: {TimeText}");
        text.AppendLine($"{localizer[L.Watchdog.ColumnObject]}: {ObjectName} ({ObjectKindText})");
        text.AppendLine($"{localizer[L.Details.DistinguishedName]}: {ObjectDn}");
        if (PreviousDn.Length > 0)
        {
            text.AppendLine($"{localizer[L.Watchdog.DetailsPreviousDn]}: {PreviousDn}");
        }

        text.AppendLine($"{localizer[L.Watchdog.ColumnActor]}: {ActorText}");
        if (SourceText.Length > 0)
        {
            text.AppendLine($"{localizer[L.Watchdog.ColumnSource]}: {SourceText}");
        }

        foreach (var change in Changes)
        {
            text.AppendLine($"{change.Attribute}: {change.OldText} → {change.NewText} {change.ChangeText}".TrimEnd());
        }

        _services.Ui.Clipboard.SetText(text.ToString().TrimEnd());
        _services.Ui.Notifications.Show(NotificationSeverity.Info, localizer[L.Fields.Copied]);
    }

    private async Task ShowCoreAsync(WatchEvent? summary)
    {
        var token = _loads.Next();
        Error = null;
        ActionMessage = null;
        CanUnlock = false;
        _lockedDn = null;
        if (summary is null)
        {
            Event = null;
            Clear();
            return;
        }

        Event = summary;
        Fill(summary, null);
        IsLoading = true;
        try
        {
            var details = await _services.Client.GetEventAsync(summary.Id, token).ConfigureAwait(true);
            token.ThrowIfCancellationRequested();
            Event = details.Event;
            Fill(details.Event, details);
            await CheckLockAsync(details.Event, token).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException && token.IsCancellationRequested)
        {
            // Another event was selected (OperationCanceledException or a WatchdogClientException of kind Cancelled).
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Error = _services.Connection.Describe(ex);
        }
        finally
        {
            if (_loads.IsCurrent(token))
            {
                IsLoading = false;
            }

            OpenInAdMngCommand.NotifyCanExecuteChanged();
        }
    }

    /// <summary>For an AccountLockedOut event: is the account (found by GUID in the open session) still locked?</summary>
    private async Task CheckLockAsync(WatchEvent? watchEvent, CancellationToken token)
    {
        CanUnlock = false;
        _lockedDn = null;
        if (_disposed || watchEvent is not { Kind: WatchEventKind.AccountLockedOut, Object.ObjectGuid: { } guid } || SessionForEvent() is not { } session)
        {
            return;
        }

        try
        {
            var dn = await FindLiveDnAsync(session, guid, token).ConfigureAwait(true);
            if (dn is null)
            {
                return;
            }

            var obj = await Task.Run(() => session.GetAsync(dn, LockAttributes, token), token).ConfigureAwait(true);
            if (obj is { IsLockedOut: true } && Event?.Id == watchEvent.Id)
            {
                _lockedDn = dn;
                CanUnlock = true;
            }
        }
        catch (DirectoryException)
        {
            // Unknown lock state: no unlock offered.
        }
    }

    private static async Task<string?> FindLiveDnAsync(IDirectorySession session, Guid guid, CancellationToken token)
    {
        var search = new DirectorySearch
        {
            BaseDn = session.Info.DefaultNamingContext,
            Filter = LdapFilter.EqualGuid(Attr.ObjectGuid, guid),
            Attributes = [Attr.Name],
            SizeLimit = 1,
        };
        return await Task.Run(
            async () =>
            {
                await foreach (var found in session.SearchAsync(search, token).ConfigureAwait(false))
                {
                    return found.DistinguishedName;
                }

                return null;
            },
            token).ConfigureAwait(true);
    }

    /// <summary>The open AD session when it is connected to the event's domain (or the event names no domain).</summary>
    private IDirectorySession? SessionForEvent()
    {
        var session = _services.Sessions.Current;
        var domain = Event?.Domain;
        return session is not null && (string.IsNullOrEmpty(domain) || string.Equals(domain, session.Info.DomainDnsName, StringComparison.OrdinalIgnoreCase))
            ? session
            : null;
    }

    private void Fill(WatchEvent e, EventDetails? details)
    {
        var localizer = _services.Localizer;
        var zone = _services.TimeZone;
        Title = localizer.KindName(e.Kind);
        SeverityText = localizer.SeverityName(e.Severity);
        OnPropertyChanged(nameof(Severity));
        TimeText = localizer.FormatTimeWithSeconds(e.DetectedAt, zone);
        ObjectName = WatchdogEventRowViewModel.NameOf(e.Object, localizer);
        ObjectKindText = localizer.WatchObjectKindName(e.Object.Kind);
        ObjectDn = e.Object.Dn;
        ObjectPath = string.IsNullOrEmpty(e.Object.Dn) ? string.Empty : AddressResolver.ToDisplayPath(e.PreviousDn is { Length: > 0 } previous && e.Kind == WatchEventKind.ObjectDeleted ? previous : e.Object.Dn);
        PreviousDn = e.PreviousDn ?? string.Empty;
        ObjectGuidText = e.Object.ObjectGuid?.ToString("D") ?? string.Empty;
        OriginText = string.Join(" · ", new[] { e.OriginatingDc, localizer.FormatTimeWithSeconds(e.OriginatingTime, zone) }.Where(s => !string.IsNullOrEmpty(s)));
        TagsText = string.Join(", ", e.Tags.Select(localizer.TagName));
        RulesText = string.Join(", ", e.MatchedRules);
        AcknowledgedText = e.AcknowledgedAt is { } at
            ? localizer.Format(L.Watchdog.DetailsAcknowledgedBy, localizer.FormatTime(at, zone), e.AcknowledgedBy ?? string.Empty)
                + (string.IsNullOrEmpty(e.AcknowledgeComment) ? string.Empty : ": " + e.AcknowledgeComment)
            : string.Empty;
        MemberText = e.Member is { } member ? WatchdogEventRowViewModel.NameOf(member, localizer) : string.Empty;
        ViaText = string.Join(" → ", e.Via.Select(v => WatchdogEventRowViewModel.NameOf(v, localizer)));
        ExpiryText = e.Expiry is { } expiry
            ? localizer.Format(L.Watchdog.DetailsExpiry, localizer[WatchdogDisplay.ExpiryKindKey(expiry.Kind)], expiry.LastValidDay.ToString("d", localizer.Culture), expiry.DaysLeft)
            : string.Empty;
        BurstText = e.Burst is { } burst
            ? localizer.Format(L.Watchdog.DetailsBurst, burst.Count, (int)burst.Window.TotalMinutes, string.Join(", ", burst.Objects.Select(o => WatchdogEventRowViewModel.NameOf(o, localizer))))
            : string.Empty;

        var schema = _services.Sessions.Current?.Schema;
        var useSchema = e.Object.Kind != WatchObjectKind.Watchdog;
        Replace(Changes, e.Changes.Select(c => WatchdogChangeRowViewModel.Create(c, localizer, schema, useSchema)));
        FillAttribution(e.Attribution ?? EventAttribution.Pending);
        FillLockout(e.Lockout);
        if (details is not null)
        {
            Replace(RelatedEvents, details.RelatedEvents.Select(r => new WatchdogEventRowViewModel(r, localizer, zone)));
            Replace(Deliveries, details.Deliveries.Select(d => new WatchdogDeliveryRowViewModel(d, localizer, zone)));
        }

        AcknowledgeCommand.NotifyCanExecuteChanged();
    }

    private void FillAttribution(EventAttribution attribution)
    {
        var localizer = _services.Localizer;
        AttributionStatusText = localizer.AttributionStatusName(attribution.Status);
        ActorText = attribution.Actor is { } actor ? WatchdogAttributionText.PrincipalDetails(actor) : WatchdogAttributionText.Actor(localizer, attribution);
        SourceText = WatchdogAttributionText.Source(localizer, attribution);
        SourceConfidenceText = SourceText.Length == 0 ? string.Empty : localizer.ConfidenceName(attribution.SourceConfidence);
        ConfidenceText = attribution.Status == AttributionStatus.NotApplicable ? string.Empty : localizer.ConfidenceName(attribution.Confidence);
        ReasonText = localizer.ReasonText(attribution.Reason);
        RemedyText = localizer.RemedyText(attribution.Reason) ?? string.Empty;
        SourceReasonText = string.IsNullOrEmpty(attribution.SourceReason)
            ? string.Empty
            : $"{localizer.ReasonText(attribution.SourceReason)} – {localizer.RemedyText(attribution.SourceReason)}";
        Replace(Candidates, attribution.Candidates.Select(WatchdogAttributionText.PrincipalDetails));
        Replace(Evidence, attribution.Evidence.Select(r => new WatchdogEvidenceRowViewModel(r, localizer.FormatTimeWithSeconds(r.TimeCreated, _services.TimeZone))));
    }

    private void FillLockout(LockoutInfo? lockout)
    {
        var localizer = _services.Localizer;
        HasLockout = lockout is not null;
        LockoutText = lockout is null
            ? string.Empty
            : localizer.Format(
                L.Watchdog.DetailsLockout,
                localizer.FormatTimeWithSeconds(lockout.LockedAt, _services.TimeZone),
                lockout.LockedUntil is { } until ? localizer.FormatTimeWithSeconds(until, _services.TimeZone) : localizer[L.Watchdog.DetailsLockedUntilUnlocked],
                lockout.LockoutsToday,
                lockout.CallerComputer ?? string.Empty);
        Replace(LockoutSources, lockout?.Sources.Select(s => new WatchdogLockoutSourceRowViewModel(s)) ?? []);
    }

    private void Clear()
    {
        Title = SeverityText = TimeText = ObjectName = ObjectKindText = ObjectPath = ObjectDn = PreviousDn = ObjectGuidText = string.Empty;
        OriginText = TagsText = RulesText = AcknowledgedText = MemberText = ViaText = ExpiryText = BurstText = string.Empty;
        AttributionStatusText = ActorText = SourceText = SourceConfidenceText = ConfidenceText = ReasonText = RemedyText = SourceReasonText = string.Empty;
        LockoutText = string.Empty;
        HasLockout = false;
        Changes.Clear();
        Candidates.Clear();
        Evidence.Clear();
        LockoutSources.Clear();
        RelatedEvents.Clear();
        Deliveries.Clear();
    }

    private static void Replace<T>(ObservableCollection<T> target, IEnumerable<T> items)
    {
        target.Clear();
        foreach (var item in items)
        {
            target.Add(item);
        }
    }
}
