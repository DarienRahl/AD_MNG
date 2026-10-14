using AdMng.Core.ActiveDirectory;
using AdMng.Core.Directory;
using AdMng.Core.Model;
using AdMng.Presentation.Localization;
using AdMng.Watchdog.Contracts.Configuration;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AdMng.Presentation.Watchdog;

/// <summary>
/// One rule exclusion: object (picker or GUID), subtree, actor (picker resolving to a SID through the AD session, else SID
/// text – names are never stored), attribute, reason (required), expiry; <c>CreatedBy</c> is the caller's account. Every
/// edit is written to the rule (<see cref="Changed"/>); local checks are shown next to the fields, the service's 8.3
/// codes come through the rule editor's field errors.
/// </summary>
public sealed partial class WatchdogExclusionViewModel : ObservableObject
{
    /// <summary>Longest reason the service accepts.</summary>
    public const int MaxReasonLength = 500;

    private readonly WatchdogServices _services;
    private readonly bool _canEdit;
    private bool _loading;

    public WatchdogExclusionViewModel(WatchdogServices services, RuleExclusion exclusion, bool canEdit)
    {
        _services = services ?? throw new ArgumentNullException(nameof(services));
        ArgumentNullException.ThrowIfNull(exclusion);
        _canEdit = canEdit;
        Id = exclusion.Id;
        CreatedBy = exclusion.CreatedBy;
        CreatedAt = exclusion.CreatedAt;
        IsRedacted = exclusion.Id.StartsWith("redacted-", StringComparison.Ordinal);
        _loading = true;
        ObjectGuidText = exclusion.ObjectGuid?.ToString("D") ?? string.Empty;
        SubtreeDn = exclusion.SubtreeDn ?? string.Empty;
        Actor = exclusion.Actor ?? string.Empty;
        Attribute = exclusion.Attribute ?? string.Empty;
        Reason = exclusion.Reason;
        ExpiresAt = exclusion.ExpiresAt is { } expires ? TimeZoneInfo.ConvertTime(expires, services.TimeZone).Date : null;
        _loading = false;
        Validate();
    }

    /// <summary>The exclusion was edited.</summary>
    public event EventHandler? Changed;

    public string Id { get; }

    public string CreatedBy { get; }

    public DateTimeOffset CreatedAt { get; }

    /// <summary>A Viewer's placeholder (only the count of exclusions is visible).</summary>
    public bool IsRedacted { get; }

    public bool CanEdit => _canEdit && !IsRedacted;

    public string CreatedText => IsRedacted
        ? _services.Localizer[L.Watchdog.ExclusionRedacted]
        : _services.Localizer.Format(L.Watchdog.ExclusionCreated, _services.Localizer.FormatTime(CreatedAt, _services.TimeZone), CreatedBy);

    /// <summary>"Konto kiosku w recepcji – obiekt 4a8e…" for the list.</summary>
    public string Summary => IsRedacted ? _services.Localizer[L.Watchdog.ExclusionRedacted] : Reason;

    [ObservableProperty]
    public partial string ObjectGuidText { get; set; }

    /// <summary>Name of the object picked through the AD session (display only).</summary>
    [ObservableProperty]
    public partial string ObjectName { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial string SubtreeDn { get; set; }

    /// <summary>Actor SID.</summary>
    [ObservableProperty]
    public partial string Actor { get; set; }

    /// <summary>Name of the actor picked through the AD session (display only, never stored).</summary>
    [ObservableProperty]
    public partial string ActorName { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial string Attribute { get; set; }

    [ObservableProperty]
    public partial string Reason { get; set; }

    /// <summary>Local date after which the exclusion no longer applies.</summary>
    [ObservableProperty]
    public partial DateTime? ExpiresAt { get; set; }

    [ObservableProperty]
    public partial string? ObjectError { get; private set; }

    [ObservableProperty]
    public partial string? SubtreeError { get; private set; }

    [ObservableProperty]
    public partial string? ActorError { get; private set; }

    [ObservableProperty]
    public partial string? ReasonError { get; private set; }

    public bool IsValid => ObjectError is null && SubtreeError is null && ActorError is null && ReasonError is null;

    /// <summary>A directory session is open: the pickers are available.</summary>
    public bool CanPick => CanEdit && _services.Sessions.Current is not null;

    /// <summary>An AD session was opened or closed.</summary>
    public void NotifySessionChanged() => OnPropertyChanged(nameof(CanPick));

    /// <summary>The exclusion as stored in the rule (invalid GUID / SID texts are left out; the errors explain why).</summary>
    public RuleExclusion ToExclusion()
    {
        var zone = _services.TimeZone;
        DateTimeOffset? expires = null;
        if (ExpiresAt is { } date)
        {
            var endOfDay = DateTime.SpecifyKind(date.Date.AddDays(1), DateTimeKind.Unspecified);
            expires = new DateTimeOffset(endOfDay, zone.GetUtcOffset(endOfDay)).AddTicks(-1).ToUniversalTime();
        }

        return new RuleExclusion
        {
            Id = Id,
            ObjectGuid = Guid.TryParse(ObjectGuidText, out var guid) ? guid : null,
            SubtreeDn = Blank(SubtreeDn),
            Actor = WatchdogSid.IsValid(Actor.Trim()) ? Actor.Trim() : null,
            Attribute = Blank(Attribute),
            Reason = Reason.Trim(),
            ExpiresAt = expires,
            CreatedBy = CreatedBy,
            CreatedAt = CreatedAt,
        };
    }

    /// <summary>Prefills the actor (exclusion suggestions of the status).</summary>
    public void SetActor(string sid, string? name)
    {
        Actor = sid;
        ActorName = name ?? string.Empty;
    }

    [RelayCommand]
    private async Task PickObjectAsync()
    {
        if (_services.Sessions.Current is not { } session)
        {
            return;
        }

        var picked = await _services.Dialogs.PickObjectsAsync(
            session,
            [ObjectKind.User, ObjectKind.Computer, ObjectKind.Group, ObjectKind.Contact, ObjectKind.OrganizationalUnit],
            multiSelect: false).ConfigureAwait(true);
        if (picked is not [var dn])
        {
            return;
        }

        try
        {
            var obj = await Task.Run(() => session.GetAsync(dn, [Attr.ObjectGuid, Attr.Name], CancellationToken.None)).ConfigureAwait(true);
            if (obj?.ObjectGuid is { } guid)
            {
                ObjectGuidText = guid.ToString("D");
                ObjectName = obj.Name;
            }
        }
        catch (DirectoryException ex)
        {
            ObjectError = _services.Ui.Errors.Translate(ex);
        }
    }

    [RelayCommand]
    private async Task BrowseSubtreeAsync()
    {
        if (_services.Sessions.Current is not { } session)
        {
            return;
        }

        var picked = await _services.Dialogs.PickContainerAsync(session, Blank(SubtreeDn) ?? session.Info.DefaultNamingContext).ConfigureAwait(true);
        if (picked is not null)
        {
            SubtreeDn = picked;
        }
    }

    [RelayCommand]
    private async Task PickActorAsync()
    {
        if (_services.Sessions.Current is not { } session)
        {
            return;
        }

        var picked = await _services.Dialogs.PickObjectsAsync(session, [ObjectKind.User, ObjectKind.Computer, ObjectKind.Group], multiSelect: false).ConfigureAwait(true);
        if (picked is not [var dn])
        {
            return;
        }

        try
        {
            var obj = await Task.Run(() => session.GetAsync(dn, [Attr.ObjectSid, Attr.Name], CancellationToken.None)).ConfigureAwait(true);
            if (obj?.ObjectSid is { } sid)
            {
                SetActor(sid, obj.Name);
            }
        }
        catch (DirectoryException ex)
        {
            ActorError = _services.Ui.Errors.Translate(ex);
        }
    }

    partial void OnObjectGuidTextChanged(string value) => Edited();

    partial void OnSubtreeDnChanged(string value) => Edited();

    partial void OnActorChanged(string value) => Edited();

    partial void OnAttributeChanged(string value) => Edited();

    partial void OnReasonChanged(string value)
    {
        Edited();
        OnPropertyChanged(nameof(Summary));
    }

    partial void OnExpiresAtChanged(DateTime? value) => Edited();

    private static string? Blank(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();

    private void Edited()
    {
        if (_loading)
        {
            return;
        }

        Validate();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void Validate()
    {
        if (IsRedacted)
        {
            return;
        }

        var localizer = _services.Localizer;
        ObjectError = Blank(ObjectGuidText) is { } guid && !Guid.TryParse(guid, out _) ? localizer[L.Watchdog.ExclusionInvalidGuid] : null;
        SubtreeError = Blank(SubtreeDn) is { } subtree && !WatchdogDn.IsValid(subtree) ? localizer[WatchdogDisplay.ValidationKey("invalidDn")] : null;
        ActorError = Blank(Actor) is { } actor && !WatchdogSid.IsValid(actor) ? localizer[WatchdogDisplay.ValidationKey("invalidSid")] : null;
        ReasonError = Reason.Trim().Length switch
        {
            0 => localizer[WatchdogDisplay.ValidationKey("exclusionReason")],
            > MaxReasonLength => localizer[WatchdogDisplay.ValidationKey("range")],
            _ => null,
        };
        OnPropertyChanged(nameof(IsValid));
    }
}
