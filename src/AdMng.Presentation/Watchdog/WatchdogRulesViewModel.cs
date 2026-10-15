using System.Collections.ObjectModel;
using System.Globalization;
using AdMng.Presentation.Infrastructure;
using AdMng.Presentation.Localization;
using AdMng.Watchdog.Contracts.Api;
using AdMng.Watchdog.Contracts.Configuration;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AdMng.Presentation.Watchdog;

/// <summary>
/// The Rules sub-tab: rule rows with the inline editor of the selected rule; New / Duplicate / Delete (not built-in) /
/// Reset to default (built-in, from <c>GET /config/defaults</c>); exclusion suggestions of the status ("Dodaj wyjątek" →
/// the exclusion editor prefilled, saved through the normal PUT); the rollout banner with "Zakończ tryb wdrożenia" and
/// per rule "wysłałaby N" during rollout; Viewers see the redacted configuration read-only.
/// </summary>
public sealed partial class WatchdogRulesViewModel : ObservableObject, IDisposable
{
    private readonly WatchdogServices _services;
    private StatusDto? _status;
    private bool _disposed;

    public WatchdogRulesViewModel(WatchdogServices services, WatchdogConfigSession session)
    {
        _services = services ?? throw new ArgumentNullException(nameof(services));
        Session = session ?? throw new ArgumentNullException(nameof(session));
        Session.Reloaded += OnReloaded;
        Session.IssuesChanged += OnIssuesChanged;
        Session.PropertyChanged += OnSessionPropertyChanged;
    }

    public WatchdogConfigSession Session { get; }

    public ObservableCollection<WatchdogRuleRowViewModel> Rules { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(DuplicateRuleCommand), nameof(DeleteRuleCommand), nameof(ResetRuleCommand))]
    public partial WatchdogRuleRowViewModel? SelectedRule { get; set; }

    /// <summary>Editor of the selected rule (null when nothing is selected).</summary>
    [ObservableProperty]
    public partial WatchdogRuleEditorViewModel? Editor { get; private set; }

    public ObservableCollection<WatchdogSuggestionViewModel> Suggestions { get; } = [];

    /// <summary>End of rollout mode (from the status), or null.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsRolloutActive), nameof(RolloutBannerText))]
    [NotifyCanExecuteChangedFor(nameof(EndRolloutCommand))]
    public partial DateTimeOffset? RolloutUntil { get; private set; }

    public bool IsRolloutActive => RolloutUntil is not null;

    /// <summary>"Tryb wdrożenia do 21.10.2026 07:00: powiadomienia natychmiastowe tylko dla zdarzeń krytycznych…".</summary>
    public string RolloutBannerText => RolloutUntil is { } until
        ? _services.Localizer.Format(L.Watchdog.RolloutBanner, _services.Localizer.FormatTime(until, _services.TimeZone))
        : string.Empty;

    /// <summary>The caller's account (CreatedBy of new exclusions).</summary>
    public string? Account { get; set; }

    /// <summary>Takes the status: rollout banner, exclusion suggestions, health warnings about rules.</summary>
    public void ApplyStatus(StatusDto status)
    {
        ArgumentNullException.ThrowIfNull(status);
        var rolloutStarted = status.RolloutModeUntil is not null && RolloutUntil is null;
        _status = status;
        RolloutUntil = status.RolloutModeUntil;
        Suggestions.Clear();
        foreach (var suggestion in status.Suggestions)
        {
            Suggestions.Add(new WatchdogSuggestionViewModel(suggestion, _services.Localizer));
        }

        ApplyWarnings();
        if (rolloutStarted)
        {
            LoadRolloutCountsAsync().Forget();
        }
    }

    /// <summary>An AD session was opened or closed (the editor's pickers).</summary>
    public void NotifySessionChanged() => Editor?.NotifySessionChanged();

    /// <summary>Texts after a language switch.</summary>
    public void Relocalize()
    {
        Rebuild();
        if (_status is { } status)
        {
            ApplyStatus(status);
        }

        OnPropertyChanged(nameof(RolloutBannerText));
    }

    public void Dispose()
    {
        _disposed = true;
        Session.Reloaded -= OnReloaded;
        Session.IssuesChanged -= OnIssuesChanged;
        Session.PropertyChanged -= OnSessionPropertyChanged;
        DetachEditor();
    }

    /// <summary>Per rule "wysłałaby N wiadomości" since the baseline (rule previews, Admin only). Never throws.</summary>
    public async Task LoadRolloutCountsAsync()
    {
        if (!IsRolloutActive || !Session.IsAdmin || _status?.Directory.BaselineCompletedAt is not { } baseline)
        {
            return;
        }

        var days = Math.Clamp((int)Math.Ceiling((_services.Time.GetUtcNow() - baseline).TotalDays), 1, 90);
        foreach (var row in Rules.Where(r => r.Rule.Enabled).ToList())
        {
            if (_disposed)
            {
                return;
            }

            try
            {
                var result = await _services.Client.PreviewRuleAsync(new RulePreviewRequest { Rule = row.Rule, Days = days }, CancellationToken.None).ConfigureAwait(true);
                row.RolloutText = row.Rule.Delivery.Immediate
                    ? _services.Localizer.Format(L.Watchdog.RulesWouldSend, result.EstimatedImmediateMessages)
                    : string.Empty;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // The counts are informative; the rule list stays usable.
                return;
            }
        }
    }

    [RelayCommand(CanExecute = nameof(CanEdit))]
    private void NewRule()
    {
        var localizer = _services.Localizer;
        AddRule(new RuleSettings
        {
            Id = UniqueId("nowa-regula"),
            Name = localizer[L.Watchdog.RulesNewName],
            Delivery = new RuleDelivery { Immediate = false, IncludeInDigest = true },
        });
    }

    [RelayCommand(CanExecute = nameof(CanDuplicate))]
    private void DuplicateRule()
    {
        if (SelectedRule?.Rule is not { } rule)
        {
            return;
        }

        AddRule(rule with
        {
            Id = UniqueId(rule.Id + "-kopia"),
            Name = _services.Localizer.Format(L.Watchdog.RulesCopyName, rule.Name),
            BuiltIn = false,
            Locked = false,
            Exclusions = [.. rule.Exclusions.Select(e => e with { Id = e.Id })],
        });
    }

    private bool CanDuplicate() => CanEdit() && SelectedRule is not null;

    [RelayCommand(CanExecute = nameof(CanDelete))]
    private async Task DeleteRuleAsync()
    {
        if (SelectedRule is not { } row)
        {
            return;
        }

        var localizer = _services.Localizer;
        if (!await _services.Dialogs.ConfirmAsync(localizer[L.Watchdog.RulesDelete], localizer.Format(L.Watchdog.RulesDeleteConfirm, row.Name), destructive: true).ConfigureAwait(true))
        {
            return;
        }

        var index = row.Index;
        Session.Update(c => c with { Rules = [.. c.Rules.Where((_, i) => i != index)] });
        Rebuild(selectIndex: Math.Min(index, Rules.Count - 2));
    }

    private bool CanDelete() => CanEdit() && SelectedRule is { IsBuiltIn: false, IsLocked: false };

    /// <summary>Replaces a built-in rule with its shipped version (<c>GET /config/defaults</c>).</summary>
    [RelayCommand(CanExecute = nameof(CanReset))]
    private async Task ResetRuleAsync()
    {
        if (SelectedRule is not { } row)
        {
            return;
        }

        var localizer = _services.Localizer;
        if (!await _services.Dialogs.ConfirmAsync(localizer[L.Common.ResetToDefault], localizer.Format(L.Watchdog.RulesResetConfirm, row.Name), destructive: false).ConfigureAwait(true))
        {
            return;
        }

        try
        {
            var defaults = await _services.Client.GetDefaultConfigAsync(CancellationToken.None).ConfigureAwait(true);
            if (defaults.Rules.FirstOrDefault(r => string.Equals(r.Id, row.Id, StringComparison.OrdinalIgnoreCase)) is not { } shipped)
            {
                await _services.Dialogs.ShowErrorAsync(localizer[L.Common.ResetToDefault], localizer[L.Watchdog.RulesNoDefault]).ConfigureAwait(true);
                return;
            }

            var index = row.Index;
            Session.Update(c => c with { Rules = [.. c.Rules.Select((r, i) => i == index ? shipped : r)] });
            Rebuild(selectIndex: index);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            await _services.Dialogs.ShowErrorAsync(localizer[L.Common.ResetToDefault], _services.Connection.Describe(ex)).ConfigureAwait(true);
        }
    }

    private bool CanReset() => CanEdit() && SelectedRule is { IsBuiltIn: true };

    /// <summary>"Dodaj wyjątek": selects the suggestion's rule and adds an exclusion prefilled with the actor.</summary>
    [RelayCommand(CanExecute = nameof(CanEdit))]
    private void AddSuggestedExclusion(WatchdogSuggestionViewModel? suggestion)
    {
        if (suggestion is null || Rules.FirstOrDefault(r => string.Equals(r.Id, suggestion.Suggestion.RuleId, StringComparison.OrdinalIgnoreCase)) is not { } row)
        {
            return;
        }

        SelectedRule = row;
        Editor?.AddExclusion(suggestion.Suggestion.ActorSid, suggestion.Suggestion.ActorAccount, _services.Localizer.SuggestionText(suggestion.Suggestion.ReasonCode));
    }

    /// <summary>Ends rollout mode early (General.RolloutEnded = true) and saves the configuration.</summary>
    [RelayCommand(CanExecute = nameof(CanEndRollout))]
    private async Task EndRolloutAsync()
    {
        var localizer = _services.Localizer;
        if (!await _services.Dialogs.ConfirmAsync(localizer[L.Watchdog.RolloutEnd], localizer[L.Watchdog.RolloutEndConfirm], destructive: false).ConfigureAwait(true))
        {
            return;
        }

        Session.Update(c => c with { General = c.General with { RolloutEnded = true } });
        if (await Session.SaveAsync().ConfigureAwait(true))
        {
            RolloutUntil = null;
            foreach (var row in Rules)
            {
                row.RolloutText = string.Empty;
            }
        }
    }

    private bool CanEndRollout() => IsRolloutActive && CanEdit();

    private bool CanEdit() => Session.CanEdit;

    partial void OnSelectedRuleChanged(WatchdogRuleRowViewModel? value)
    {
        DetachEditor();
        if (value is null || Session.Working is null || value.Index >= Session.Working.Rules.Count)
        {
            Editor = null;
            return;
        }

        var editor = new WatchdogRuleEditorViewModel(_services, Session, value.Index) { Account = Account };
        editor.RuleChanged += OnRuleEdited;
        Editor = editor;
    }

    private void AddRule(RuleSettings rule)
    {
        Session.Update(c => c with { Rules = [.. c.Rules, rule] });
        Rebuild(selectIndex: Session.Working?.Rules.Count - 1 ?? 0);
    }

    private string UniqueId(string baseId)
    {
        var ids = Session.Working?.Rules.Select(r => r.Id).ToHashSet(StringComparer.OrdinalIgnoreCase) ?? [];
        var candidate = baseId.Length > 55 ? baseId[..55] : baseId;
        for (var i = 2; ids.Contains(candidate); i++)
        {
            candidate = string.Create(CultureInfo.InvariantCulture, $"{baseId}-{i}");
        }

        return candidate;
    }

    private void Rebuild(int? selectIndex = null)
    {
        var selectedId = SelectedRule?.Id;
        DetachEditor();
        Rules.Clear();
        if (Session.Working is { } working)
        {
            for (var i = 0; i < working.Rules.Count; i++)
            {
                Rules.Add(new WatchdogRuleRowViewModel(working.Rules[i], i, _services.Localizer, Session.CanEdit, OnRowToggled));
            }
        }

        ApplyWarnings();
        OnIssuesChanged(this, EventArgs.Empty);
        SelectedRule = selectIndex is { } index && index >= 0 && index < Rules.Count
            ? Rules[index]
            : Rules.FirstOrDefault(r => string.Equals(r.Id, selectedId, StringComparison.Ordinal)) ?? Rules.FirstOrDefault();
        NewRuleCommand.NotifyCanExecuteChanged();
        AddSuggestedExclusionCommand.NotifyCanExecuteChanged();
        EndRolloutCommand.NotifyCanExecuteChanged();
    }

    private void ApplyWarnings()
    {
        var checks = _status?.Checks.Where(c => c.Code == "attribution.actorConditionsUnreliable" && c.Args.Count > 0).ToList() ?? [];
        foreach (var row in Rules)
        {
            var warning = checks.FirstOrDefault(c => string.Equals(c.Args[0], row.Id, StringComparison.OrdinalIgnoreCase));
            row.WarningText = warning is null ? string.Empty : _services.Localizer.HealthText(warning.Code) + (warning.Args.Count > 1 ? $" ({warning.Args[1]})" : string.Empty);
        }
    }

    private void OnRowToggled(WatchdogRuleRowViewModel row, bool enabled)
    {
        var index = row.Index;
        Session.Update(c => c with { Rules = [.. c.Rules.Select((r, i) => i == index ? r with { Enabled = enabled } : r)] });
        if (Session.Working is { } working && index < working.Rules.Count)
        {
            row.Update(working.Rules[index]);
            if (Editor is { } editor && editor.Index == index)
            {
                editor.IsEnabled = enabled;
            }
        }
    }

    private void OnRuleEdited(object? sender, RuleSettings rule)
    {
        if (sender is WatchdogRuleEditorViewModel editor && editor.Index < Rules.Count)
        {
            Rules[editor.Index].Update(rule);
        }
    }

    private void OnReloaded(object? sender, EventArgs e) => Rebuild();

    private void OnIssuesChanged(object? sender, EventArgs e)
    {
        foreach (var row in Rules)
        {
            row.HasIssues = Session.IssuesFor($"rules[{row.Index}].").Any();
        }

        Editor?.RefreshErrors();
    }

    private void OnSessionPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(WatchdogConfigSession.IsAdmin))
        {
            Rebuild();
        }
    }

    private void DetachEditor()
    {
        if (Editor is { } editor)
        {
            editor.RuleChanged -= OnRuleEdited;
        }
    }
}
