using System.Collections.ObjectModel;
using AdMng.Core.ActiveDirectory;
using AdMng.Core.Model;
using AdMng.Presentation.Infrastructure;
using AdMng.Presentation.Localization;
using AdMng.Presentation.Navigation;
using AdMng.Presentation.Settings;
using AdMng.Watchdog.Contracts.Api;
using AdMng.Watchdog.Contracts.Events;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AdMng.Presentation.Watchdog;

/// <summary>
/// Filters of the event feed: minimum severity, categories, kinds grouped by category, object kinds, subtree (container
/// picker with an AD session), tags, attribution status ("pokaż nieustalone"), actor (picker resolving a SID through the AD
/// session, else SID text – names are never used for filtering), text (debounced 300 ms), from/to, unacknowledged only,
/// one object (history launcher), and named presets saved in the settings (filters only, never event data).
/// </summary>
public sealed partial class WatchdogEventFilterViewModel : ObservableObject
{
    /// <summary>Debounce of the text filter.</summary>
    public static readonly TimeSpan TextDelay = TimeSpan.FromMilliseconds(300);

    private static readonly AttributionStatus[] Unresolved = [AttributionStatus.Unknown, AttributionStatus.Ambiguous];

    private readonly WatchdogServices _services;
    private readonly Debouncer _textDebouncer;
    private int _suspended;
    private bool _changedWhileSuspended;

    public WatchdogEventFilterViewModel(WatchdogServices services)
    {
        _services = services ?? throw new ArgumentNullException(nameof(services));
        _textDebouncer = new Debouncer(services.Time, TextDelay);
        Severities = CreateSeverities();
        MinSeverity = Severities[0];
        BuildOptions(EventKindCatalog.All);
        LoadPresets();
    }

    /// <summary>The filter changed (text: after the debounce delay).</summary>
    public event EventHandler? Changed;

    public IReadOnlyList<Option<Severity?>> Severities { get; private set; }

    [ObservableProperty]
    public partial Option<Severity?> MinSeverity { get; set; }

    public ObservableCollection<WatchdogCheckOption<EventCategory>> Categories { get; } = [];

    /// <summary>Event kinds grouped by category (the kind picker).</summary>
    public ObservableCollection<WatchdogOptionGroup<WatchEventKind>> KindGroups { get; } = [];

    public ObservableCollection<WatchdogCheckOption<WatchObjectKind>> ObjectKinds { get; } = [];

    public ObservableCollection<WatchdogCheckOption<string>> Tags { get; } = [];

    public ObservableCollection<WatchdogCheckOption<AttributionStatus>> AttributionStatuses { get; } = [];

    /// <summary>Text contained in name, logon name, DN, actor, workstation or IP address.</summary>
    [ObservableProperty]
    public partial string Text { get; set; } = string.Empty;

    /// <summary>Only objects in (or moved out of) this container.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SubtreeText))]
    [NotifyCanExecuteChangedFor(nameof(ClearSubtreeCommand))]
    public partial string? Subtree { get; set; }

    public string SubtreeText => Subtree is { Length: > 0 } dn ? AddressResolver.ToDisplayPath(dn) : _services.Localizer[L.Watchdog.FilterAnyLocation];

    /// <summary>SID of the actor (the only actor value that is sent to the service or saved in a preset).</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ClearActorCommand))]
    public partial string? ActorSid { get; set; }

    /// <summary>What the operator typed or picked: a SID, or the picked name (display only).</summary>
    [ObservableProperty]
    public partial string ActorText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string? ActorError { get; private set; }

    /// <summary>Local date from (inclusive).</summary>
    [ObservableProperty]
    public partial DateTime? From { get; set; }

    /// <summary>Local date to (inclusive, whole day).</summary>
    [ObservableProperty]
    public partial DateTime? To { get; set; }

    [ObservableProperty]
    public partial bool UnacknowledgedOnly { get; set; }

    /// <summary>"Pokaż nieustalone": only events whose actor could not be determined (Unknown, Ambiguous).</summary>
    [ObservableProperty]
    public partial bool UnresolvedOnly { get; set; }

    /// <summary>Only the history of one object (set by "Historia zmian (Watchdog)").</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasObject))]
    [NotifyCanExecuteChangedFor(nameof(ClearObjectCommand))]
    public partial Guid? ObjectGuid { get; set; }

    /// <summary>Name of the object of <see cref="ObjectGuid"/> ("Jan Kowalski"), or its GUID.</summary>
    [ObservableProperty]
    public partial string ObjectText { get; private set; } = string.Empty;

    public bool HasObject => ObjectGuid is not null;

    /// <summary>Only events matched by this rule.</summary>
    [ObservableProperty]
    public partial string? RuleId { get; set; }

    public ObservableCollection<WatchdogFilterPreset> Presets { get; } = [];

    /// <summary>Named filters exist: the view offers the "Zapisane filtry" list only then.</summary>
    public bool HasPresets => Presets.Count > 0;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(DeletePresetCommand))]
    public partial WatchdogFilterPreset? SelectedPreset { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SavePresetCommand))]
    public partial string PresetName { get; set; } = string.Empty;

    /// <summary>Number of active conditions (shown on the filter button).</summary>
    public int ActiveCount =>
        (MinSeverity.Value is null ? 0 : 1) + Categories.Count(c => c.IsChecked) + KindGroups.Sum(g => g.Options.Count(o => o.IsChecked))
        + ObjectKinds.Count(o => o.IsChecked) + Tags.Count(t => t.IsChecked) + AttributionStatuses.Count(a => a.IsChecked)
        + (UnresolvedOnly ? 1 : 0) + (string.IsNullOrWhiteSpace(Text) ? 0 : 1) + (Subtree is null ? 0 : 1) + (ActorSid is null ? 0 : 1)
        + (From is null ? 0 : 1) + (To is null ? 0 : 1) + (UnacknowledgedOnly ? 1 : 0) + (ObjectGuid is null ? 0 : 1) + (RuleId is null ? 0 : 1);

    public bool HasActiveFilter => ActiveCount > 0;

    /// <summary>A directory session is open: the subtree and actor pickers are available.</summary>
    public bool CanPick => _services.Sessions.Current is not null;

    /// <summary>An AD session was opened or closed.</summary>
    public void NotifySessionChanged() => OnPropertyChanged(nameof(CanPick));

    /// <summary>The filter as an <see cref="EventQuery"/> (no paging).</summary>
    public EventQuery ToQuery()
    {
        var zone = _services.TimeZone;
        var statuses = AttributionStatuses.Where(a => a.IsChecked).Select(a => a.Value);
        if (UnresolvedOnly)
        {
            statuses = statuses.Concat(Unresolved);
        }

        return new EventQuery
        {
            MinSeverity = MinSeverity.Value,
            Categories = [.. Categories.Where(c => c.IsChecked).Select(c => c.Value)],
            Kinds = [.. KindGroups.SelectMany(g => g.Options).Where(o => o.IsChecked).Select(o => o.Value)],
            ObjectKinds = [.. ObjectKinds.Where(o => o.IsChecked).Select(o => o.Value)],
            Tags = [.. Tags.Where(t => t.IsChecked).Select(t => t.Value)],
            AttributionStatuses = [.. statuses.Distinct()],
            Text = string.IsNullOrWhiteSpace(Text) ? null : Text.Trim(),
            Subtree = Subtree,
            ActorSid = ActorSid,
            From = From is { } from ? StartOfDay(from, zone) : null,
            To = To is { } to ? StartOfDay(to.AddDays(1), zone).AddTicks(-1) : null,
            UnacknowledgedOnly = UnacknowledgedOnly,
            ObjectGuid = ObjectGuid,
            RuleId = RuleId,
        };
    }

    /// <summary>Sets every condition from <paramref name="query"/> (presets); raises <see cref="Changed"/> once.</summary>
    public void Apply(EventQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);
        using (Suspend())
        {
            var zone = _services.TimeZone;
            MinSeverity = Severities.FirstOrDefault(s => s.Value == query.MinSeverity) ?? Severities[0];
            Check(Categories, query.Categories);
            Check([.. KindGroups.SelectMany(g => g.Options)], query.Kinds);
            Check(ObjectKinds, query.ObjectKinds);
            Check(Tags, query.Tags);
            var unresolved = Unresolved.All(query.AttributionStatuses.Contains);
            UnresolvedOnly = unresolved;
            Check(AttributionStatuses, unresolved ? [.. query.AttributionStatuses.Except(Unresolved)] : query.AttributionStatuses);
            Text = query.Text ?? string.Empty;
            Subtree = query.Subtree;
            ActorSid = query.ActorSid;
            ActorText = query.ActorSid ?? string.Empty;
            From = query.From is { } from ? TimeZoneInfo.ConvertTime(from, zone).Date : null;
            To = query.To is { } to ? TimeZoneInfo.ConvertTime(to, zone).Date : null;
            UnacknowledgedOnly = query.UnacknowledgedOnly;
            SetObject(query.ObjectGuid, null);
            RuleId = query.RuleId;
        }
    }

    /// <summary>Filters the feed to one object (history launcher); the other conditions are cleared.</summary>
    public void ShowObject(Guid objectGuid, string? name)
    {
        using (Suspend())
        {
            ClearCore();
            SetObject(objectGuid, name);
        }
    }

    /// <summary>The kinds of the service's catalog (from <c>GET /catalog</c>).</summary>
    public void SetCatalog(IReadOnlyList<EventKindInfo> catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        var selected = KindGroups.SelectMany(g => g.Options).Where(o => o.IsChecked).Select(o => o.Value).ToHashSet();
        using (Suspend())
        {
            BuildKindGroups(catalog, selected);
        }
    }

    /// <summary>Texts after a language switch (selections are kept).</summary>
    public void Relocalize()
    {
        var query = ToQuery();
        var name = ObjectText;
        using (Suspend())
        {
            Severities = CreateSeverities();
            OnPropertyChanged(nameof(Severities));
            BuildOptions([.. KindGroups.SelectMany(g => g.Options).Select(o => EventKindCatalog.Get(o.Value))]);
            Apply(query);
            SetObject(query.ObjectGuid, ObjectGuid is null ? null : name);
        }

        OnPropertyChanged(nameof(SubtreeText));
    }

    [RelayCommand]
    private void Clear()
    {
        using (Suspend())
        {
            ClearCore();
        }
    }

    [RelayCommand(CanExecute = nameof(HasObject))]
    private void ClearObject() => SetObject(null, null);

    [RelayCommand(CanExecute = nameof(HasSubtree))]
    private void ClearSubtree() => Subtree = null;

    private bool HasSubtree() => Subtree is not null;

    [RelayCommand(CanExecute = nameof(HasActor))]
    private void ClearActor()
    {
        using (Suspend())
        {
            ActorSid = null;
            ActorText = string.Empty;
            ActorError = null;
        }
    }

    private bool HasActor() => ActorSid is not null || ActorText.Length > 0;

    /// <summary>Picks a container through the AD session (the main window's container browser).</summary>
    [RelayCommand]
    private async Task BrowseSubtreeAsync()
    {
        if (_services.Sessions.Current is not { } session)
        {
            return;
        }

        var picked = await _services.Dialogs.PickContainerAsync(session, Subtree ?? session.Info.DefaultNamingContext).ConfigureAwait(true);
        if (picked is not null)
        {
            Subtree = picked;
        }
    }

    /// <summary>Picks a user, computer or group and filters by its SID (read through the AD session).</summary>
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
                using (Suspend())
                {
                    ActorSid = sid;
                    ActorText = obj.Name;
                    ActorError = null;
                }
            }
        }
        catch (Core.Directory.DirectoryException ex)
        {
            ActorError = _services.Ui.Errors.Translate(ex);
        }
    }

    [RelayCommand(CanExecute = nameof(CanSavePreset))]
    private async Task SavePresetAsync()
    {
        var name = PresetName.Trim();
        var preset = new WatchdogFilterPreset(name, WatchdogQueryText.ToPreset(ToQuery()));
        await _services.Settings.UpdateAsync(s =>
        {
            s.Watchdog.FilterPresets.RemoveAll(p => string.Equals(p.Name, name, StringComparison.CurrentCultureIgnoreCase));
            s.Watchdog.FilterPresets.Add(preset);
        }).ConfigureAwait(true);
        LoadPresets();
        PresetName = string.Empty;
        _services.Ui.Notifications.Show(Services.NotificationSeverity.Success, _services.Localizer.Format(L.Watchdog.PresetSaved, name));
    }

    private bool CanSavePreset() => !string.IsNullOrWhiteSpace(PresetName);

    [RelayCommand(CanExecute = nameof(HasSelectedPreset))]
    private async Task DeletePresetAsync()
    {
        if (SelectedPreset is not { } preset)
        {
            return;
        }

        await _services.Settings.UpdateAsync(s => s.Watchdog.FilterPresets.RemoveAll(p => string.Equals(p.Name, preset.Name, StringComparison.Ordinal))).ConfigureAwait(true);
        LoadPresets();
    }

    private bool HasSelectedPreset() => SelectedPreset is not null;

    partial void OnSelectedPresetChanged(WatchdogFilterPreset? value)
    {
        if (value is not null)
        {
            Apply(WatchdogQueryText.Parse(value.Query));
        }
    }

    partial void OnMinSeverityChanged(Option<Severity?> value) => RaiseChanged();

    partial void OnSubtreeChanged(string? value) => RaiseChanged();

    partial void OnActorSidChanged(string? value) => RaiseChanged();

    partial void OnFromChanged(DateTime? value) => RaiseChanged();

    partial void OnToChanged(DateTime? value) => RaiseChanged();

    partial void OnUnacknowledgedOnlyChanged(bool value) => RaiseChanged();

    partial void OnUnresolvedOnlyChanged(bool value) => RaiseChanged();

    partial void OnObjectGuidChanged(Guid? value) => RaiseChanged();

    partial void OnRuleIdChanged(string? value) => RaiseChanged();

    partial void OnTextChanged(string value)
    {
        if (_suspended > 0)
        {
            _changedWhileSuspended = true;
            return;
        }

        _textDebouncer.RunAsync(_ =>
        {
            RaiseChanged();
            return Task.CompletedTask;
        }).Forget();
    }

    /// <summary>A typed actor is used when it is a SID; anything else is an error (names are resolved only by the picker).</summary>
    partial void OnActorTextChanged(string value)
    {
        if (_suspended > 0)
        {
            return;
        }

        var text = value.Trim();
        if (text.Length == 0)
        {
            ActorError = null;
            ActorSid = null;
        }
        else if (WatchdogSid.IsValid(text))
        {
            ActorError = null;
            ActorSid = text;
        }
        else
        {
            ActorError = _services.Localizer[L.Watchdog.FilterActorSidOnly];
        }

        ClearActorCommand.NotifyCanExecuteChanged();
    }

    private static DateTimeOffset StartOfDay(DateTime date, TimeZoneInfo zone)
    {
        var local = DateTime.SpecifyKind(date.Date, DateTimeKind.Unspecified);
        return new DateTimeOffset(local, zone.GetUtcOffset(local));
    }

    private static void Check<T>(IEnumerable<WatchdogCheckOption<T>> options, IReadOnlyCollection<T> values)
    {
        foreach (var option in options)
        {
            option.IsChecked = values.Contains(option.Value);
        }
    }

    private List<Option<Severity?>> CreateSeverities() =>
    [
        new(null, _services.Localizer[L.Watchdog.FilterAnySeverity]),
        .. Enum.GetValues<Severity>().Select(s => new Option<Severity?>(s, _services.Localizer.Format(L.Watchdog.FilterMinSeverity, _services.Localizer.SeverityName(s)))),
    ];

    private void BuildOptions(IReadOnlyList<EventKindInfo> catalog)
    {
        var localizer = _services.Localizer;
        Categories.Clear();
        foreach (var category in Enum.GetValues<EventCategory>())
        {
            Categories.Add(new WatchdogCheckOption<EventCategory>(category, localizer.CategoryName(category), changed: OnOptionChanged));
        }

        ObjectKinds.Clear();
        foreach (var kind in Enum.GetValues<WatchObjectKind>().Where(k => k != WatchObjectKind.Unknown))
        {
            ObjectKinds.Add(new WatchdogCheckOption<WatchObjectKind>(kind, localizer.WatchObjectKindName(kind), changed: OnOptionChanged));
        }

        Tags.Clear();
        foreach (var tag in WatchdogDisplay.Tags)
        {
            Tags.Add(new WatchdogCheckOption<string>(tag, localizer.TagName(tag), changed: OnOptionChanged));
        }

        AttributionStatuses.Clear();
        foreach (var status in Enum.GetValues<AttributionStatus>())
        {
            AttributionStatuses.Add(new WatchdogCheckOption<AttributionStatus>(status, localizer.AttributionStatusName(status), changed: OnOptionChanged));
        }

        BuildKindGroups(catalog.Count == 0 ? EventKindCatalog.All : catalog, []);
    }

    private void BuildKindGroups(IReadOnlyList<EventKindInfo> catalog, HashSet<WatchEventKind> selected)
    {
        var localizer = _services.Localizer;
        KindGroups.Clear();
        foreach (var group in catalog.Where(i => i.Kind != WatchEventKind.Test).GroupBy(i => i.Category).OrderBy(g => g.Key))
        {
            KindGroups.Add(new WatchdogOptionGroup<WatchEventKind>(
                localizer.CategoryName(group.Key),
                [.. group.Select(i => new WatchdogCheckOption<WatchEventKind>(i.Kind, localizer.KindName(i.Kind), selected.Contains(i.Kind), OnOptionChanged))]));
        }
    }

    private void ClearCore()
    {
        MinSeverity = Severities[0];
        Check(Categories, []);
        Check(ObjectKinds, []);
        Check(Tags, []);
        Check(AttributionStatuses, []);
        Check([.. KindGroups.SelectMany(g => g.Options)], []);
        Text = string.Empty;
        Subtree = null;
        ActorSid = null;
        ActorText = string.Empty;
        ActorError = null;
        From = null;
        To = null;
        UnacknowledgedOnly = false;
        UnresolvedOnly = false;
        RuleId = null;
        SetObject(null, null);
    }

    private void SetObject(Guid? guid, string? name)
    {
        ObjectGuid = guid;
        ObjectText = guid is { } g ? (string.IsNullOrEmpty(name) ? g.ToString("D") : name) : string.Empty;
    }

    private void LoadPresets()
    {
        var selected = SelectedPreset?.Name;
        Presets.Clear();
        foreach (var preset in _services.Settings.Current.Watchdog.FilterPresets.OrderBy(p => p.Name, StringComparer.Create(_services.Localizer.Culture, true)))
        {
            Presets.Add(preset);
        }

        if (selected is not null && Presets.FirstOrDefault(p => p.Name == selected) is null)
        {
            SelectedPreset = null;
        }

        OnPropertyChanged(nameof(HasPresets));
    }

    private void OnOptionChanged<T>(WatchdogCheckOption<T> option) => RaiseChanged();

    private void RaiseChanged()
    {
        if (_suspended > 0)
        {
            _changedWhileSuspended = true;
            return;
        }

        OnPropertyChanged(nameof(ActiveCount));
        OnPropertyChanged(nameof(HasActiveFilter));
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private SuspendScope Suspend()
    {
        _suspended++;
        _textDebouncer.Cancel();
        return new SuspendScope(this);
    }

    private void Resume()
    {
        if (--_suspended == 0 && _changedWhileSuspended)
        {
            _changedWhileSuspended = false;
            RaiseChanged();
        }
    }

    /// <summary>Batches the change notifications of several condition changes into one <see cref="Changed"/>.</summary>
    private readonly struct SuspendScope(WatchdogEventFilterViewModel owner) : IDisposable
    {
        public void Dispose() => owner.Resume();
    }
}
