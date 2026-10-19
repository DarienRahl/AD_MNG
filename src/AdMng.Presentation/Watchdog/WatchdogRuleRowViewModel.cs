using AdMng.Presentation.Localization;
using AdMng.Watchdog.Contracts.Configuration;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AdMng.Presentation.Watchdog;

/// <summary>
/// One rule in the Rules list: enabled toggle, name, built-in/locked badge, kinds summary, severity override, delivery
/// summary, exclusions (only their count for Viewers), "wysłałaby N" during rollout and health warnings about the rule.
/// </summary>
public sealed partial class WatchdogRuleRowViewModel : ObservableObject
{
    private readonly ILocalizer _localizer;
    private readonly Action<WatchdogRuleRowViewModel, bool> _setEnabled;
    private bool _updating;

    public WatchdogRuleRowViewModel(RuleSettings rule, int index, ILocalizer localizer, bool canEdit, Action<WatchdogRuleRowViewModel, bool> setEnabled)
    {
        Rule = rule ?? throw new ArgumentNullException(nameof(rule));
        Index = index;
        _localizer = localizer ?? throw new ArgumentNullException(nameof(localizer));
        _setEnabled = setEnabled ?? throw new ArgumentNullException(nameof(setEnabled));
        CanEdit = canEdit;
        _updating = true;
        IsEnabled = rule.Enabled;
        _updating = false;
        Refresh();
    }

    public RuleSettings Rule { get; private set; }

    /// <summary>Position in <c>configuration.rules</c> (validation paths "rules[3]…").</summary>
    public int Index { get; }

    public string Id => Rule.Id;

    public bool IsBuiltIn => Rule.BuiltIn;

    public bool IsLocked => Rule.Locked;

    /// <summary>The configuration can be edited (Admin, not redacted).</summary>
    public bool CanEdit { get; }

    /// <summary>The enabled toggle is available (editable and not a locked rule).</summary>
    public bool CanToggle => CanEdit && !IsLocked;

    [ObservableProperty]
    public partial bool IsEnabled { get; set; }

    [ObservableProperty]
    public partial string Name { get; private set; } = string.Empty;

    /// <summary>Tool tip of the name (cut in a narrow list): the whole name and, below it, the rule's identifier.</summary>
    [ObservableProperty]
    public partial string NameToolTip { get; private set; } = string.Empty;

    /// <summary>"wbudowana" / "zablokowana" / empty.</summary>
    [ObservableProperty]
    public partial string BadgeText { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial string KindsText { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial string SeverityText { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial string DeliveryText { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial string ExclusionsText { get; private set; } = string.Empty;

    /// <summary>"wysłałaby 12 wiadomości" during rollout mode.</summary>
    [ObservableProperty]
    public partial string RolloutText { get; set; } = string.Empty;

    /// <summary>A health warning about this rule (e.g. actor conditions that cannot work), or empty.</summary>
    [ObservableProperty]
    public partial string WarningText { get; set; } = string.Empty;

    /// <summary>The service reported validation issues for this rule.</summary>
    [ObservableProperty]
    public partial bool HasIssues { get; set; }

    /// <summary>Takes a newer version of the rule (edited in the editor).</summary>
    public void Update(RuleSettings rule)
    {
        Rule = rule ?? throw new ArgumentNullException(nameof(rule));
        _updating = true;
        IsEnabled = rule.Enabled;
        _updating = false;
        Refresh();
        OnPropertyChanged(nameof(Id));
    }

    /// <summary>Recomputes the texts (language switch).</summary>
    public void Refresh()
    {
        var rule = Rule;
        Name = rule.Name;
        NameToolTip = rule.Name + "\n" + rule.Id;
        BadgeText = rule.Locked ? _localizer[L.Watchdog.RulesLocked] : rule.BuiltIn ? _localizer[L.Watchdog.RulesBuiltIn] : string.Empty;
        KindsText = rule.Kinds.Count switch
        {
            0 => _localizer[L.Watchdog.RulesAllKinds],
            <= 2 => string.Join(", ", rule.Kinds.Select(_localizer.KindName)),
            _ => _localizer.Format(L.Watchdog.RulesKindsCount, rule.Kinds.Count, _localizer.KindName(rule.Kinds[0])),
        };
        SeverityText = string.Join(
            " · ",
            new[]
            {
                rule.MinSeverity is { } min ? _localizer.Format(L.Watchdog.FilterMinSeverity, _localizer.SeverityName(min)) : null,
                rule.Severity is { } severity ? _localizer.Format(L.Watchdog.RulesSeverityOverride, _localizer.SeverityName(severity)) : null,
            }.Where(s => s is not null));
        DeliveryText = Delivery(rule.Delivery);
        ExclusionsText = rule.Exclusions.Count == 0 ? string.Empty : _localizer.Format(L.Watchdog.RulesExclusionsCount, rule.Exclusions.Count);
    }

    partial void OnIsEnabledChanged(bool value)
    {
        if (!_updating)
        {
            _setEnabled(this, value);
        }
    }

    private string Delivery(RuleDelivery delivery)
    {
        var targets = delivery.RecipientGroupIds.Concat(delivery.ChannelIds).ToList();
        if (delivery.AllEnabledChannels)
        {
            targets.Add(_localizer[L.Watchdog.RulesAllChannels]);
        }

        var immediate = delivery.Immediate && targets.Count > 0
            ? _localizer.Format(L.Watchdog.RulesImmediate, string.Join(", ", targets))
            : null;
        var digest = delivery.IncludeInDigest ? _localizer[L.Watchdog.RulesDigest] : null;
        return immediate is null && digest is null ? _localizer[L.Watchdog.RulesNoDelivery] : string.Join(" · ", new[] { immediate, digest }.Where(s => s is not null));
    }
}
