using System.ComponentModel;
using AdMng.Core.Messaging;
using AdMng.Core.Messaging.Salutations;
using AdMng.Presentation.Infrastructure;
using AdMng.Presentation.Localization;

namespace AdMng.Presentation.UserMessages;

/// <summary>
/// Salutations in the message window (U1d): the "Zwroty" options of the template (titles, unknown gender), the gender
/// override of one message in the preview (re-plans that message from the accounts of the last read, kept in memory while
/// the window shows them – never in the settings) and the salutation options the attribute lists are built with.
/// </summary>
public sealed partial class UserMessageComposerViewModel
{
    private readonly Dictionary<string, Gender> _genderOverrides = new(StringComparer.OrdinalIgnoreCase);
    private UserMessagePlanRequest? _lastRequest;
    private IReadOnlyList<Option<UnknownGenderMode?>>? _unknownGenderChoices;
    private IReadOnlyList<Option<SalutationGenderChoice>>? _genderChoices;

    /// <summary>"Używaj tytułów" of the edited template.</summary>
    public bool UseTitles
    {
        get => _working.Salutation?.UseTitles ?? true;
        set
        {
            if (value != UseTitles)
            {
                UpdateSalutation(o => o with { UseTitles = value });
                OnPropertyChanged();
            }
        }
    }

    /// <summary>"Gdy płeć nieznana" choices of a template: from the settings, neutral, masculine, feminine.</summary>
    public IReadOnlyList<Option<UnknownGenderMode?>> UnknownGenderChoices => _unknownGenderChoices ??=
    [
        new(null, Localizer[L.Salutations.UnknownGenderFromSettings]),
        .. Enum.GetValues<UnknownGenderMode>().Select(m => new Option<UnknownGenderMode?>(m, Localizer[SalutationText.UnknownGenderKey(m)])),
    ];

    /// <summary>"Gdy płeć nieznana" of the edited template.</summary>
    public Option<UnknownGenderMode?> TemplateUnknownGender
    {
        get => UnknownGenderChoices.FirstOrDefault(c => c.Value == _working.Salutation?.UnknownGender) ?? UnknownGenderChoices[0];
        set
        {
            if (value is not null && value.Value != _working.Salutation?.UnknownGender)
            {
                UpdateSalutation(o => o with { UnknownGender = value.Value });
                OnPropertyChanged();
            }
        }
    }

    /// <summary>The gender choices of the preview's selector: automatic, Pan, Pani, neutral.</summary>
    public IReadOnlyList<Option<SalutationGenderChoice>> GenderChoices => _genderChoices ??=
        [.. Enum.GetValues<SalutationGenderChoice>().Select(c => new Option<SalutationGenderChoice>(c, Localizer[SalutationText.ChoiceKey(c)]))];

    /// <summary>Genders chosen in the preview of the current plan, by account DN (memory only).</summary>
    public IReadOnlyDictionary<string, Gender> GenderOverrides => _genderOverrides;

    /// <summary>The salutation options of the settings (the template's are merged by the planner).</summary>
    private SalutationOptions SalutationOptions => (Services.Settings.Current.UserMessages.Salutations ?? new()).ToOptions();

    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.PropertyName == nameof(SelectedTemplate))
        {
            // Another template was loaded: its salutation options are shown.
            base.OnPropertyChanged(new PropertyChangedEventArgs(nameof(UseTitles)));
            base.OnPropertyChanged(new PropertyChangedEventArgs(nameof(TemplateUnknownGender)));
        }
    }

    /// <summary>Keeps the request of the plan just shown (accounts of the fresh read) for overrides; earlier overrides end.</summary>
    private void KeepForReplan(UserMessagePlanRequest request)
    {
        _lastRequest = request;
        _genderOverrides.Clear();
        foreach (var item in _items)
        {
            item.PropertyChanged -= OnItemGenderChanged;
            item.PropertyChanged += OnItemGenderChanged;
        }
    }

    private void OnItemGenderChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(UserMessageItemViewModel.GenderChoice) && sender is UserMessageItemViewModel item)
        {
            ReplanWithGender(item);
        }
    }

    /// <summary>Re-plans one message with the gender chosen for it (the other messages and the settings stay as they are).</summary>
    private void ReplanWithGender(UserMessageItemViewModel item)
    {
        var index = _items.IndexOf(item);
        if (_lastRequest is not { } request || index < 0 || Stage != UserMessageStage.Preview)
        {
            return;
        }

        var dn = item.Planned.DistinguishedName;
        if (SalutationText.OverrideOf(item.GenderChoice) is { } chosen)
        {
            _genderOverrides[dn] = chosen;
        }
        else
        {
            _genderOverrides.Remove(dn);
        }

        var planned = _messages.Planner.Replan(
            request with { GenderOverrides = new Dictionary<string, Gender>(_genderOverrides, StringComparer.OrdinalIgnoreCase) },
            item.Planned.Index,
            item.Planned);
        var replacement = new UserMessageItemViewModel(planned, Localizer, Services.Time.LocalTimeZone)
        {
            GenderChoice = item.GenderChoice,
        };
        if (replacement.CanInclude && item.IsExcluded)
        {
            replacement.IsIncluded = false;
        }

        item.PropertyChanged -= OnItemChanged;
        item.PropertyChanged -= OnItemGenderChanged;
        replacement.PropertyChanged += OnItemChanged;
        replacement.PropertyChanged += OnItemGenderChanged;
        _items[index] = replacement;
        var visible = Items.IndexOf(item);
        if (visible >= 0)
        {
            Items[visible] = replacement;
        }

        if (ReferenceEquals(SelectedItem, item) || (visible >= 0 && SelectedItem is null))
        {
            SelectedItem = replacement;
        }

        RefreshCounts();
    }

    private void UpdateSalutation(Func<TemplateSalutationOptions, TemplateSalutationOptions> change) =>
        _working = _working with { Salutation = change(_working.Salutation ?? new TemplateSalutationOptions()).Normalized() };
}
