using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using AdMng.Core.Messaging;
using AdMng.Core.Messaging.Salutations;
using AdMng.Presentation.Infrastructure;
using AdMng.Presentation.Localization;
using AdMng.Presentation.Settings;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AdMng.Presentation.UserMessages;

/// <summary>A row of the operator's given-name list.</summary>
public sealed partial class GivenNameRowViewModel : ObservableObject
{
    [ObservableProperty]
    public partial string Name { get; set; } = string.Empty;

    [ObservableProperty]
    public partial Gender Gender { get; set; } = Gender.Feminine;
}

/// <summary>A row of the title table (variants comma-separated).</summary>
public sealed partial class TitleRowViewModel : ObservableObject
{
    [ObservableProperty]
    public partial string Title { get; set; } = string.Empty;

    /// <summary>"prof, profesor, prof. zw." – separated by commas or semicolons.</summary>
    [ObservableProperty]
    public partial string Variants { get; set; } = string.Empty;

    [ObservableProperty]
    public partial int Rank { get; set; }

    [ObservableProperty]
    public partial string PolishMasculine { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string PolishFeminine { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string English { get; set; } = string.Empty;

    public static TitleRowViewModel From(TitleRule rule) => new()
    {
        Title = rule.Title,
        Variants = string.Join(", ", rule.Variants ?? []),
        Rank = rule.Rank,
        PolishMasculine = rule.PolishMasculine ?? string.Empty,
        PolishFeminine = rule.PolishFeminine ?? string.Empty,
        English = rule.English ?? string.Empty,
    };

    public TitleRule ToRule() => new()
    {
        Title = Title.Trim(),
        Variants = [.. Variants.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)],
        Rank = Rank,
        PolishMasculine = Clean(PolishMasculine),
        PolishFeminine = Clean(PolishFeminine),
        English = Clean(English),
    };

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

/// <summary>
/// The "Zwroty grzecznościowe" section of the settings tab "Wiadomości e-mail" (U1d): title and gender attributes, the
/// values of the gender attribute, neutral texts, what an unknown gender renders as, the given-name list, the title table
/// with "Przywróć domyślne" and the "Sprawdź" box (typed values → title, gender with its source, PL and EN salutation –
/// pure, no directory access).
/// </summary>
public sealed partial class SalutationSettingsViewModel : ObservableObject
{
    private readonly ILocalizer _localizer;
    private readonly bool _ready;
    private bool _refreshing;

    public SalutationSettingsViewModel(ILocalizer localizer, SalutationSettings current)
    {
        _localizer = localizer ?? throw new ArgumentNullException(nameof(localizer));
        ArgumentNullException.ThrowIfNull(current);
        UnknownGenderModes = [.. Enum.GetValues<UnknownGenderMode>().Select(m => new Option<UnknownGenderMode>(m, localizer[SalutationText.UnknownGenderKey(m)]))];
        Genders = [new(Gender.Masculine, localizer[SalutationText.GenderKey(Gender.Masculine)]), new(Gender.Feminine, localizer[SalutationText.GenderKey(Gender.Feminine)])];
        TitleAttribute = current.TitleAttribute ?? string.Empty;
        TitleFromDisplayName = current.TitleFromDisplayName;
        GenderAttribute = current.GenderAttribute ?? string.Empty;
        MasculineValuesText = string.Join(", ", current.MasculineValues ?? []);
        FeminineValuesText = string.Join(", ", current.FeminineValues ?? []);
        NeutralPolish = current.NeutralPolish ?? SalutationData.DefaultNeutralPolish;
        NeutralEnglish = current.NeutralEnglish ?? SalutationData.DefaultNeutralEnglish;
        UnknownGender = UnknownGenderModes.FirstOrDefault(m => m.Value == current.UnknownGender) ?? UnknownGenderModes[0];
        GivenNames = [.. (current.GivenNames ?? []).Select(n => new GivenNameRowViewModel { Name = n.Name, Gender = n.Gender })];
        Titles = [.. current.EffectiveTitles.Select(TitleRowViewModel.From)];
        Watch(GivenNames);
        Watch(Titles);
        _ready = true;
        RefreshCheck();
    }

    public IReadOnlyList<Option<UnknownGenderMode>> UnknownGenderModes { get; }

    /// <summary>The genders of the given-name list (masculine, feminine).</summary>
    public IReadOnlyList<Option<Gender>> Genders { get; }

    /// <summary>"{name} = imię i nazwisko" with the placeholder filled in.</summary>
    public string NeutralHint => _localizer.Format(L.Salutations.NeutralHint, SalutationData.NamePlaceholder);

    [ObservableProperty]
    public partial string TitleAttribute { get; set; }

    [ObservableProperty]
    public partial bool TitleFromDisplayName { get; set; }

    [ObservableProperty]
    public partial string GenderAttribute { get; set; }

    /// <summary>Values meaning masculine, comma-separated.</summary>
    [ObservableProperty]
    public partial string MasculineValuesText { get; set; }

    /// <summary>Values meaning feminine, comma-separated.</summary>
    [ObservableProperty]
    public partial string FeminineValuesText { get; set; }

    [ObservableProperty]
    public partial string NeutralPolish { get; set; }

    [ObservableProperty]
    public partial string NeutralEnglish { get; set; }

    [ObservableProperty]
    public partial Option<UnknownGenderMode> UnknownGender { get; set; }

    public ObservableCollection<GivenNameRowViewModel> GivenNames { get; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RemoveGivenNameCommand))]
    public partial GivenNameRowViewModel? SelectedGivenName { get; set; }

    public ObservableCollection<TitleRowViewModel> Titles { get; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RemoveTitleCommand))]
    public partial TitleRowViewModel? SelectedTitle { get; set; }

    // ---- "Sprawdź" -----------------------------------------------------------------------------------------------

    [ObservableProperty]
    public partial string CheckTitle { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string CheckDisplayName { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string CheckGivenName { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string CheckSurname { get; set; } = string.Empty;

    /// <summary>"Tytuł: prof. dr hab. (z atrybutu tytułu)".</summary>
    [ObservableProperty]
    public partial string CheckTitleResult { get; private set; } = string.Empty;

    /// <summary>"Płeć: mężczyzna (Pan) (z imienia – reguła · Jan · zgadnięte)".</summary>
    [ObservableProperty]
    public partial string CheckGenderResult { get; private set; } = string.Empty;

    /// <summary>The Polish salutation of the typed values.</summary>
    [ObservableProperty]
    public partial string CheckPolish { get; private set; } = string.Empty;

    /// <summary>The English salutation of the typed values.</summary>
    [ObservableProperty]
    public partial string CheckEnglish { get; private set; } = string.Empty;

    /// <summary>Localized validation error of the section, or null.</summary>
    public string? Error
    {
        get
        {
            string? Attribute(string label, string value) =>
                value.Trim().Length > 0 && !SalutationOptions.IsUsableAttribute(value.Trim()) ? _localizer.Format(L.Salutations.InvalidAttribute, _localizer[label]) : null;
            return Attribute(L.Salutations.TitleAttribute, TitleAttribute)
                ?? Attribute(L.Salutations.GenderAttribute, GenderAttribute)
                ?? (Titles.Any(t => t.Rank is < 0 or > TitleTable.MaxRank) ? _localizer.Format(L.Salutations.InvalidRank, 0, TitleTable.MaxRank) : null);
        }
    }

    [RelayCommand]
    private void AddGivenName()
    {
        var row = new GivenNameRowViewModel();
        GivenNames.Add(row);
        SelectedGivenName = row;
    }

    [RelayCommand(CanExecute = nameof(HasSelectedGivenName))]
    private void RemoveGivenName()
    {
        if (SelectedGivenName is { } row)
        {
            GivenNames.Remove(row);
        }
    }

    private bool HasSelectedGivenName() => SelectedGivenName is not null;

    [RelayCommand]
    private void AddTitle()
    {
        var row = new TitleRowViewModel { Rank = 50 };
        Titles.Add(row);
        SelectedTitle = row;
    }

    [RelayCommand(CanExecute = nameof(HasSelectedTitle))]
    private void RemoveTitle()
    {
        if (SelectedTitle is { } row)
        {
            Titles.Remove(row);
        }
    }

    private bool HasSelectedTitle() => SelectedTitle is not null;

    /// <summary>"Przywróć domyślne": the built-in title table (saved as "defaults", so later improvements apply).</summary>
    [RelayCommand]
    private void ResetTitles()
    {
        Titles.Clear();
        foreach (var rule in SalutationData.DefaultTitles)
        {
            Titles.Add(TitleRowViewModel.From(rule));
        }
    }

    /// <summary>The settings of the typed values, normalized (the title table null when it equals the defaults).</summary>
    public SalutationSettings ToSettings()
    {
        var settings = new SalutationSettings
        {
            TitleAttribute = TitleAttribute.Trim(),
            TitleFromDisplayName = TitleFromDisplayName,
            GenderAttribute = GenderAttribute.Trim(),
            MasculineValues = Split(MasculineValuesText),
            FeminineValues = Split(FeminineValuesText),
            GivenNames = [.. GivenNames.Select(r => new GivenNameGender(r.Name, r.Gender))],
            Titles = [.. Titles.Select(r => r.ToRule())],
            NeutralPolish = Neutral(NeutralPolish, SalutationData.DefaultNeutralPolish),
            NeutralEnglish = Neutral(NeutralEnglish, SalutationData.DefaultNeutralEnglish),
            UnknownGender = UnknownGender.Value,
        };
        settings.Normalize();
        return settings;
    }

    /// <summary>Writes the values into <paramref name="target"/> (called by Save of the settings window).</summary>
    public void Apply(UserMessageSettings target)
    {
        ArgumentNullException.ThrowIfNull(target);
        target.Salutations = ToSettings();
    }

    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (_ready && !_refreshing && e.PropertyName is not (nameof(CheckTitleResult) or nameof(CheckGenderResult) or nameof(CheckPolish) or nameof(CheckEnglish)
            or nameof(SelectedGivenName) or nameof(SelectedTitle) or nameof(Error)))
        {
            base.OnPropertyChanged(new PropertyChangedEventArgs(nameof(Error)));
            RefreshCheck();
        }
    }

    /// <summary>Recomputes the "Sprawdź" box from the typed values (the unsaved settings).</summary>
    private void RefreshCheck()
    {
        if (!_ready || _refreshing)
        {
            return;
        }

        _refreshing = true;
        try
        {
            var options = ToSettings().ToOptions();
            var person = SalutationPerson.Of(
                Clean(CheckTitle),
                Clean(CheckDisplayName) ?? $"{CheckGivenName} {CheckSurname}".Trim(),
                Clean(CheckGivenName),
                Clean(CheckSurname),
                null,
                options);
            var title = person.Title;
            CheckTitleResult = _localizer.Format(
                L.Salutations.CheckTitleResult,
                title.HasTitle ? title.Text : _localizer[L.Salutations.NoTitle],
                _localizer[SalutationText.SourceKey(title.Source)]);
            var gender = person.Gender;
            var (effective, isDefault) = SalutationResolver.EffectiveGender(gender, options);
            var source = new DerivedFieldValue(
                MessageSalutations.GenderField,
                effective.ToString(),
                isDefault || gender.IsConflict ? DerivedValueSource.Fallback : gender.Source,
                gender.IsGuess || isDefault,
                gender.Detail);
            CheckGenderResult = _localizer.Format(L.Salutations.CheckGenderResult, SalutationText.Value(_localizer, source), SalutationText.Source(_localizer, source));
            CheckPolish = SalutationResolver.Salutation(person, MessageLanguage.Polish, SalutationVariant.Full, options).Text;
            CheckEnglish = SalutationResolver.Salutation(person, MessageLanguage.English, SalutationVariant.Full, options).Text;
        }
        finally
        {
            _refreshing = false;
        }
    }

    private void Watch<T>(ObservableCollection<T> rows)
        where T : INotifyPropertyChanged
    {
        foreach (var row in rows)
        {
            row.PropertyChanged += OnRowChanged;
        }

        rows.CollectionChanged += (_, e) =>
        {
            foreach (var row in e.OldItems?.OfType<T>() ?? [])
            {
                row.PropertyChanged -= OnRowChanged;
            }

            foreach (var row in e.NewItems?.OfType<T>() ?? [])
            {
                row.PropertyChanged += OnRowChanged;
            }

            if (e.Action == NotifyCollectionChangedAction.Reset)
            {
                foreach (var row in rows)
                {
                    row.PropertyChanged -= OnRowChanged;
                    row.PropertyChanged += OnRowChanged;
                }
            }

            OnRowChanged(this, new PropertyChangedEventArgs(null));
        };
    }

    private void OnRowChanged(object? sender, PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(new PropertyChangedEventArgs(nameof(Error)));
        RefreshCheck();
    }

    private static List<string> Split(string? text) =>
        [.. (text ?? string.Empty).Split([',', ';', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];

    /// <summary>The typed neutral text; null when empty or the default (so later improvements of the default apply).</summary>
    private static string? Neutral(string? text, string defaultText)
    {
        var clean = Clean(text);
        return clean is null || string.Equals(clean, defaultText, StringComparison.Ordinal) ? null : clean;
    }

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
