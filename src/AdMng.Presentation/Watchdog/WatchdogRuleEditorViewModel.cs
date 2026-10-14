using System.Collections.ObjectModel;
using System.Globalization;
using AdMng.Core.ActiveDirectory;
using AdMng.Core.Directory;
using AdMng.Core.Model;
using AdMng.Presentation.Infrastructure;
using AdMng.Presentation.Localization;
using AdMng.Watchdog.Contracts.Api;
using AdMng.Watchdog.Contracts.Configuration;
using AdMng.Watchdog.Contracts.Events;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AdMng.Presentation.Watchdog;

/// <summary>
/// Inline editor of one rule: every <see cref="RuleSettings"/> field – kinds (catalog grouped by category), object kinds,
/// scope DNs (container picker with an AD session, else text checked with DistinguishedName.TryParse), attributes, tags,
/// group filter, actor exclusions (SIDs), severities, delivery (immediate, recipient groups, channels, digest, hours,
/// aggregation, rate, repeat suppression) and exclusions – plus the preview of what the rule would have matched. Every
/// edit goes to the shared <see cref="WatchdogConfigSession"/>; the service's validation issues are shown per field.
/// </summary>
public sealed partial class WatchdogRuleEditorViewModel : ObservableObject
{
    private readonly WatchdogServices _services;
    private readonly WatchdogConfigSession _session;
    private bool _loading;

    public WatchdogRuleEditorViewModel(WatchdogServices services, WatchdogConfigSession session, int index)
    {
        _services = services ?? throw new ArgumentNullException(nameof(services));
        _session = session ?? throw new ArgumentNullException(nameof(session));
        Index = index;
        var configuration = session.Working ?? throw new InvalidOperationException("The configuration is not loaded.");
        Original = configuration.Rules[index];
        var localizer = services.Localizer;
        Severities = [new(null, localizer[L.Watchdog.RuleNoSeverity]), .. Enum.GetValues<Severity>().Select(s => new Option<Severity?>(s, localizer.SeverityName(s)))];
        HoursOptions = [.. Enum.GetValues<DeliveryHours>().Select(h => new Option<DeliveryHours>(h, localizer[WatchdogDisplay.DeliveryHoursKey(h)]))];
        GroupFilters = [.. Enum.GetValues<GroupFilter>().Select(g => new Option<GroupFilter>(g, localizer[WatchdogDisplay.GroupFilterKey(g)]))];
        Load(Original, configuration);
        RefreshErrors();
    }

    /// <summary>The rule was edited (the list row follows).</summary>
    public event EventHandler<RuleSettings>? RuleChanged;

    /// <summary>Position of the rule in <c>configuration.rules</c>.</summary>
    public int Index { get; }

    /// <summary>The rule as it was when the editor opened.</summary>
    public RuleSettings Original { get; }

    public bool CanEdit => _session.CanEdit;

    /// <summary>The id of a built-in rule is fixed (new versions of the service merge by id).</summary>
    public bool CanEditId => CanEdit && !Original.BuiltIn;

    /// <summary>A locked rule (health) cannot be disabled and its kinds cannot change.</summary>
    public bool CanEditKinds => CanEdit && !Original.Locked;

    public bool CanDisable => CanEdit && !Original.Locked;

    public string? ReadOnlyReason => _session.ReadOnlyReason;

    /// <summary>Messages of the service for this rule's fields (<c>Errors[name]</c>, <c>Errors[delivery.channelIds]</c>…).</summary>
    public WatchdogFieldErrors Errors { get; } = new();

    [ObservableProperty]
    public partial string Id { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Name { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Description { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsEnabled { get; set; }

    public ObservableCollection<WatchdogOptionGroup<WatchEventKind>> KindGroups { get; } = [];

    public ObservableCollection<WatchdogCheckOption<WatchObjectKind>> ObjectKinds { get; } = [];

    /// <summary>One DN per line; empty = whole domain.</summary>
    [ObservableProperty]
    public partial string IncludeSubtrees { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string ExcludeSubtrees { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string? ScopeError { get; private set; }

    /// <summary>LDAP names, comma or line separated.</summary>
    [ObservableProperty]
    public partial string Attributes { get; set; } = string.Empty;

    public ObservableCollection<WatchdogCheckOption<string>> RequiredTags { get; } = [];

    public ObservableCollection<WatchdogCheckOption<string>> AnyTags { get; } = [];

    public IReadOnlyList<Option<GroupFilter>> GroupFilters { get; }

    [ObservableProperty]
    public partial Option<GroupFilter> Groups { get; set; }

    [ObservableProperty]
    public partial string GroupDns { get; set; } = string.Empty;

    /// <summary>Actor SIDs whose changes the rule ignores, one per line.</summary>
    [ObservableProperty]
    public partial string ExcludeActors { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string? ActorsError { get; private set; }

    [ObservableProperty]
    public partial bool ExcludeSelfChanges { get; set; }

    public IReadOnlyList<Option<Severity?>> Severities { get; }

    [ObservableProperty]
    public partial Option<Severity?> MinSeverity { get; set; }

    [ObservableProperty]
    public partial Option<Severity?> SeverityOverride { get; set; }

    [ObservableProperty]
    public partial bool Immediate { get; set; }

    public ObservableCollection<WatchdogCheckOption<string>> RecipientGroups { get; } = [];

    public ObservableCollection<WatchdogCheckOption<string>> Channels { get; } = [];

    [ObservableProperty]
    public partial bool AllEnabledChannels { get; set; }

    [ObservableProperty]
    public partial bool IncludeInDigest { get; set; }

    public IReadOnlyList<Option<DeliveryHours>> HoursOptions { get; }

    [ObservableProperty]
    public partial Option<DeliveryHours> Hours { get; set; }

    /// <summary>Minutes; empty = the global default.</summary>
    [ObservableProperty]
    public partial string AggregationMinutes { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string MaxMessagesPerHour { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string RepeatSuppressionMinutes { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string? NumbersError { get; private set; }

    [ObservableProperty]
    public partial bool NotifyManager { get; set; }

    public ObservableCollection<WatchdogExclusionViewModel> Exclusions { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RemoveExclusionCommand))]
    public partial WatchdogExclusionViewModel? SelectedExclusion { get; set; }

    /// <summary>"2 wyjątki" (Viewers see only the count).</summary>
    public string ExclusionsText => _services.Localizer.Format(L.Watchdog.RulesExclusionsCount, Exclusions.Count);

    /// <summary>A directory session is open: the container and object pickers are available.</summary>
    public bool CanPick => CanEdit && _services.Sessions.Current is not null;

    /// <summary>The caller's account (<c>CreatedBy</c> of new exclusions).</summary>
    public string? Account { get; set; }

    // ---- Preview ----------------------------------------------------------------------------------------------

    /// <summary>Choices of <see cref="PreviewDays"/>.</summary>
    public IReadOnlyList<int> PreviewDayOptions { get; } = [1, 7, 14, 30, 90];

    /// <summary>Days of stored events the preview scans (1–90).</summary>
    [ObservableProperty]
    public partial int PreviewDays { get; set; } = 7;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(PreviewCommand))]
    public partial bool IsPreviewing { get; private set; }

    [ObservableProperty]
    public partial RulePreviewResult? PreviewResult { get; private set; }

    /// <summary>"12 zdarzeń, ok. 4 wiadomości natychmiastowe".</summary>
    [ObservableProperty]
    public partial string PreviewText { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial string? PreviewError { get; private set; }

    public ObservableCollection<string> PreviewDaysList { get; } = [];

    public ObservableCollection<WatchdogEventRowViewModel> PreviewSamples { get; } = [];

    /// <summary>The rule built from the editor's fields.</summary>
    public RuleSettings Build()
    {
        var kinds = KindGroups.SelectMany(g => g.Options).Where(o => o.IsChecked).Select(o => o.Value).ToList();
        return Original with
        {
            Id = Id.Trim(),
            Name = Name.Trim(),
            Description = string.IsNullOrWhiteSpace(Description) ? null : Description.Trim(),
            Enabled = IsEnabled,
            Kinds = Original.Locked ? Original.Kinds : kinds,
            ObjectKinds = [.. ObjectKinds.Where(o => o.IsChecked).Select(o => o.Value)],
            Scope = new RuleScope { IncludeSubtrees = WatchdogDn.Lines(IncludeSubtrees), ExcludeSubtrees = WatchdogDn.Lines(ExcludeSubtrees) },
            Attributes = [.. Attributes.Split([',', ';', '\r', '\n', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)],
            RequiredTags = [.. RequiredTags.Where(t => t.IsChecked).Select(t => t.Value)],
            AnyTags = [.. AnyTags.Where(t => t.IsChecked).Select(t => t.Value)],
            Groups = Groups.Value,
            GroupDns = WatchdogDn.Lines(GroupDns),
            MinSeverity = MinSeverity.Value,
            Severity = SeverityOverride.Value,
            Actors = new ActorFilter { ExcludeActors = WatchdogDn.Lines(ExcludeActors), ExcludeSelfChanges = ExcludeSelfChanges },
            Delivery = new RuleDelivery
            {
                Immediate = Immediate,
                RecipientGroupIds = [.. RecipientGroups.Where(g => g.IsChecked).Select(g => g.Value)],
                ChannelIds = [.. Channels.Where(c => c.IsChecked).Select(c => c.Value)],
                AllEnabledChannels = AllEnabledChannels,
                IncludeInDigest = IncludeInDigest,
                Hours = Hours.Value,
                AggregationWindow = ParseMinutes(AggregationMinutes),
                MaxMessagesPerHour = ParseInt(MaxMessagesPerHour),
                RepeatSuppression = ParseMinutes(RepeatSuppressionMinutes),
                NotifyManager = NotifyManager,
            },
            Exclusions = [.. Exclusions.Select(e => e.ToExclusion())],
        };
    }

    /// <summary>Shows the service's validation issues of this rule next to its fields.</summary>
    public void RefreshErrors() => Errors.Set(_session.IssuesFor($"rules[{Index}]."), $"rules[{Index}].");

    /// <summary>Adds an exclusion prefilled with a suggested actor ("Dodaj wyjątek").</summary>
    public WatchdogExclusionViewModel AddExclusion(string? actorSid = null, string? actorName = null, string? reason = null)
    {
        var exclusion = new WatchdogExclusionViewModel(
            _services,
            new RuleExclusion
            {
                Id = NextExclusionId(),
                Reason = reason ?? string.Empty,
                CreatedBy = Account ?? string.Empty,
                CreatedAt = _services.Time.GetUtcNow(),
            },
            CanEdit);
        if (actorSid is not null)
        {
            exclusion.SetActor(actorSid, actorName);
        }

        exclusion.Changed += OnExclusionChanged;
        Exclusions.Add(exclusion);
        SelectedExclusion = exclusion;
        OnPropertyChanged(nameof(ExclusionsText));
        Commit();
        return exclusion;
    }

    /// <summary>An AD session was opened or closed (pickers of the rule and its exclusions).</summary>
    public void NotifySessionChanged()
    {
        OnPropertyChanged(nameof(CanPick));
        foreach (var exclusion in Exclusions)
        {
            exclusion.NotifySessionChanged();
        }
    }

    [RelayCommand]
    private void NewExclusion() => AddExclusion();

    [RelayCommand(CanExecute = nameof(HasSelectedExclusion))]
    private void RemoveExclusion()
    {
        if (SelectedExclusion is not { } exclusion)
        {
            return;
        }

        exclusion.Changed -= OnExclusionChanged;
        Exclusions.Remove(exclusion);
        SelectedExclusion = Exclusions.FirstOrDefault();
        OnPropertyChanged(nameof(ExclusionsText));
        Commit();
    }

    private bool HasSelectedExclusion() => CanEdit && SelectedExclusion is { IsRedacted: false };

    /// <summary>Adds a container chosen in the AD session to the included subtrees.</summary>
    [RelayCommand]
    private Task BrowseIncludeSubtreeAsync() => BrowseSubtreeAsync(dn => IncludeSubtrees = AppendLine(IncludeSubtrees, dn));

    [RelayCommand]
    private Task BrowseExcludeSubtreeAsync() => BrowseSubtreeAsync(dn => ExcludeSubtrees = AppendLine(ExcludeSubtrees, dn));

    /// <summary>Adds the SID of a user, computer or group chosen in the AD session to the excluded actors.</summary>
    [RelayCommand]
    private async Task PickExcludedActorAsync()
    {
        if (_services.Sessions.Current is not { } session)
        {
            return;
        }

        var picked = await _services.Dialogs.PickObjectsAsync(session, [ObjectKind.User, ObjectKind.Computer, ObjectKind.Group], multiSelect: true).ConfigureAwait(true);
        foreach (var dn in picked)
        {
            try
            {
                var obj = await Task.Run(() => session.GetAsync(dn, [Attr.ObjectSid], CancellationToken.None)).ConfigureAwait(true);
                if (obj?.ObjectSid is { } sid && !WatchdogDn.Lines(ExcludeActors).Contains(sid, StringComparer.OrdinalIgnoreCase))
                {
                    ExcludeActors = AppendLine(ExcludeActors, sid);
                }
            }
            catch (DirectoryException ex)
            {
                ActorsError = _services.Ui.Errors.Translate(ex);
            }
        }
    }

    /// <summary>What the (unsaved) rule would have matched in the last <see cref="PreviewDays"/> days.</summary>
    [RelayCommand(CanExecute = nameof(CanPreview))]
    private async Task PreviewAsync()
    {
        IsPreviewing = true;
        PreviewError = null;
        try
        {
            var result = await _services.Client.PreviewRuleAsync(
                new RulePreviewRequest { Rule = Build(), Days = Math.Clamp(PreviewDays, 1, 90) },
                CancellationToken.None).ConfigureAwait(true);
            ShowPreview(result);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            PreviewError = _services.Connection.Describe(ex);
        }
        finally
        {
            IsPreviewing = false;
        }
    }

    private bool CanPreview() => !IsPreviewing && _session.IsAdmin;

    partial void OnIdChanged(string value) => Commit();

    partial void OnNameChanged(string value) => Commit();

    partial void OnDescriptionChanged(string value) => Commit();

    partial void OnIsEnabledChanged(bool value) => Commit();

    partial void OnIncludeSubtreesChanged(string value) => Commit();

    partial void OnExcludeSubtreesChanged(string value) => Commit();

    partial void OnAttributesChanged(string value) => Commit();

    partial void OnGroupsChanged(Option<GroupFilter> value) => Commit();

    partial void OnGroupDnsChanged(string value) => Commit();

    partial void OnExcludeActorsChanged(string value) => Commit();

    partial void OnExcludeSelfChangesChanged(bool value) => Commit();

    partial void OnMinSeverityChanged(Option<Severity?> value) => Commit();

    partial void OnSeverityOverrideChanged(Option<Severity?> value) => Commit();

    partial void OnImmediateChanged(bool value) => Commit();

    partial void OnAllEnabledChannelsChanged(bool value) => Commit();

    partial void OnIncludeInDigestChanged(bool value) => Commit();

    partial void OnHoursChanged(Option<DeliveryHours> value) => Commit();

    partial void OnAggregationMinutesChanged(string value) => Commit();

    partial void OnMaxMessagesPerHourChanged(string value) => Commit();

    partial void OnRepeatSuppressionMinutesChanged(string value) => Commit();

    partial void OnNotifyManagerChanged(bool value) => Commit();

    private static string AppendLine(string text, string line) => string.IsNullOrWhiteSpace(text) ? line : text.TrimEnd() + Environment.NewLine + line;

    private static TimeSpan? ParseMinutes(string text) =>
        int.TryParse(text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var minutes) ? TimeSpan.FromMinutes(minutes) : null;

    private static int? ParseInt(string text) =>
        int.TryParse(text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var value) ? value : null;

    private static string Minutes(TimeSpan? value) =>
        value is { } span ? ((int)span.TotalMinutes).ToString(CultureInfo.InvariantCulture) : string.Empty;

    private async Task BrowseSubtreeAsync(Action<string> add)
    {
        if (_services.Sessions.Current is not { } session)
        {
            return;
        }

        var picked = await _services.Dialogs.PickContainerAsync(session, session.Info.DefaultNamingContext).ConfigureAwait(true);
        if (picked is not null)
        {
            add(picked);
        }
    }

    private void Load(RuleSettings rule, WatchdogConfiguration configuration)
    {
        _loading = true;
        try
        {
            var localizer = _services.Localizer;
            Id = rule.Id;
            Name = rule.Name;
            Description = rule.Description ?? string.Empty;
            IsEnabled = rule.Enabled;
            KindGroups.Clear();
            foreach (var group in EventKindCatalog.All.Where(i => i.Kind != WatchEventKind.Test).GroupBy(i => i.Category).OrderBy(g => g.Key))
            {
                KindGroups.Add(new WatchdogOptionGroup<WatchEventKind>(
                    localizer.CategoryName(group.Key),
                    [.. group.Select(i => new WatchdogCheckOption<WatchEventKind>(i.Kind, localizer.KindName(i.Kind), rule.Kinds.Contains(i.Kind), OnOptionChanged))]));
            }

            Fill(ObjectKinds, Enum.GetValues<WatchObjectKind>().Where(k => k != WatchObjectKind.Unknown), localizer.WatchObjectKindName, rule.ObjectKinds);
            IncludeSubtrees = string.Join(Environment.NewLine, rule.Scope.IncludeSubtrees);
            ExcludeSubtrees = string.Join(Environment.NewLine, rule.Scope.ExcludeSubtrees);
            Attributes = string.Join(", ", rule.Attributes);
            Fill(RequiredTags, WatchdogDisplay.Tags, localizer.TagName, rule.RequiredTags);
            Fill(AnyTags, WatchdogDisplay.Tags, localizer.TagName, rule.AnyTags);
            Groups = GroupFilters.First(g => g.Value == rule.Groups);
            GroupDns = string.Join(Environment.NewLine, rule.GroupDns);
            ExcludeActors = string.Join(Environment.NewLine, rule.Actors.ExcludeActors);
            ExcludeSelfChanges = rule.Actors.ExcludeSelfChanges;
            MinSeverity = Severities.First(s => s.Value == rule.MinSeverity);
            SeverityOverride = Severities.First(s => s.Value == rule.Severity);
            Immediate = rule.Delivery.Immediate;
            Fill(RecipientGroups, configuration.RecipientGroups.Select(g => g.Id), id => configuration.RecipientGroups.First(g => g.Id == id).Name, rule.Delivery.RecipientGroupIds);
            AddMissing(RecipientGroups, rule.Delivery.RecipientGroupIds);
            Fill(Channels, configuration.Channels.Select(c => c.Id), id => configuration.Channels.First(c => c.Id == id).Name, rule.Delivery.ChannelIds);
            AddMissing(Channels, rule.Delivery.ChannelIds);
            AllEnabledChannels = rule.Delivery.AllEnabledChannels;
            IncludeInDigest = rule.Delivery.IncludeInDigest;
            Hours = HoursOptions.First(h => h.Value == rule.Delivery.Hours);
            AggregationMinutes = Minutes(rule.Delivery.AggregationWindow);
            MaxMessagesPerHour = rule.Delivery.MaxMessagesPerHour?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
            RepeatSuppressionMinutes = Minutes(rule.Delivery.RepeatSuppression);
            NotifyManager = rule.Delivery.NotifyManager;
            Exclusions.Clear();
            foreach (var exclusion in rule.Exclusions)
            {
                var editor = new WatchdogExclusionViewModel(_services, exclusion, CanEdit);
                editor.Changed += OnExclusionChanged;
                Exclusions.Add(editor);
            }

            SelectedExclusion = Exclusions.FirstOrDefault();
            ValidateLocally();
        }
        finally
        {
            _loading = false;
        }
    }

    private void Fill<T>(ObservableCollection<WatchdogCheckOption<T>> target, IEnumerable<T> values, Func<T, string> text, IReadOnlyList<T> selected)
    {
        target.Clear();
        foreach (var value in values)
        {
            target.Add(new WatchdogCheckOption<T>(value, text(value), selected.Contains(value), OnOptionChanged));
        }
    }

    /// <summary>References to groups/channels that no longer exist stay visible (the service reports unknownReference).</summary>
    private void AddMissing(ObservableCollection<WatchdogCheckOption<string>> target, IReadOnlyList<string> selected)
    {
        foreach (var id in selected.Where(id => target.All(o => !string.Equals(o.Value, id, StringComparison.Ordinal))))
        {
            target.Add(new WatchdogCheckOption<string>(id, id, true, OnOptionChanged));
        }
    }

    private void ShowPreview(RulePreviewResult result)
    {
        var localizer = _services.Localizer;
        PreviewResult = result;
        PreviewText = localizer.Format(L.Watchdog.RulePreviewSummary, result.MatchedEvents, result.EstimatedImmediateMessages)
            + (result.ScanLimitReached ? " " + localizer[L.Watchdog.RulePreviewLimit] : string.Empty);
        PreviewDaysList.Clear();
        foreach (var day in result.PerDay)
        {
            PreviewDaysList.Add(localizer.Format(L.Watchdog.RulePreviewDay, day.Day.ToString("d", localizer.Culture), day.Events, day.Messages));
        }

        PreviewSamples.Clear();
        foreach (var sample in result.Samples)
        {
            PreviewSamples.Add(new WatchdogEventRowViewModel(sample, localizer, _services.TimeZone));
        }
    }

    private void OnOptionChanged<T>(WatchdogCheckOption<T> option) => Commit();

    private void OnExclusionChanged(object? sender, EventArgs e) => Commit();

    private void ValidateLocally()
    {
        var localizer = _services.Localizer;
        var invalidDns = WatchdogDn.InvalidLines(IncludeSubtrees).Concat(WatchdogDn.InvalidLines(ExcludeSubtrees)).Concat(WatchdogDn.InvalidLines(GroupDns)).ToList();
        ScopeError = invalidDns.Count == 0 ? null : $"{localizer[WatchdogDisplay.ValidationKey("invalidDn")]} {string.Join("; ", invalidDns)}";
        var invalidSids = WatchdogDn.Lines(ExcludeActors).Where(s => !WatchdogSid.IsValid(s)).ToList();
        ActorsError = invalidSids.Count == 0 ? null : $"{localizer[WatchdogDisplay.ValidationKey("invalidSid")]} {string.Join("; ", invalidSids)}";
        var badNumbers = new[] { AggregationMinutes, MaxMessagesPerHour, RepeatSuppressionMinutes }
            .Any(t => t.Trim().Length > 0 && !int.TryParse(t.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out _));
        NumbersError = badNumbers ? localizer[WatchdogDisplay.ValidationKey("range")] : null;
    }

    private string NextExclusionId()
    {
        var ids = Exclusions.Select(e => e.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        for (var i = 1; ; i++)
        {
            var id = string.Create(CultureInfo.InvariantCulture, $"wyjatek-{i}");
            if (!ids.Contains(id))
            {
                return id;
            }
        }
    }

    /// <summary>Writes the edited rule to the working configuration.</summary>
    private void Commit()
    {
        if (_loading || !CanEdit)
        {
            return;
        }

        ValidateLocally();
        var rule = Build();
        _session.Update(c => c with { Rules = [.. c.Rules.Select((r, i) => i == Index ? rule : r)] });
        RuleChanged?.Invoke(this, rule);
    }
}
