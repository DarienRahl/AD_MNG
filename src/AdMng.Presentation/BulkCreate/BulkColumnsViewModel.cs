using System.Collections.ObjectModel;
using AdMng.Core.ActiveDirectory;
using AdMng.Core.Batch.Creation;
using AdMng.Core.Directory;
using AdMng.Core.Model;
using AdMng.Core.Schema;
using AdMng.Presentation.BatchEditing;
using AdMng.Presentation.Dialogs;
using AdMng.Presentation.Infrastructure;
using AdMng.Presentation.Localization;
using AdMng.Presentation.Settings;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AdMng.Presentation.BulkCreate;

/// <summary>
/// Step 2 <em>Kolumny</em> of the bulk creation wizard (docs/dev/phase3-bulk-create.md 2.6, owner B3): one row per column of
/// the table with its samples, target, attribute (any writable attribute of the class from the live schema, minus the
/// blocked ones), name order, case and the suggestion note; automatic mapping by the profile's remembered headers, the
/// header synonyms and content, the LDAP names and the localized display names. The output is <see cref="ToMappings"/> and
/// the country table of the <c>Country</c> target (<see cref="CountryTable"/>).
/// </summary>
/// <remarks>
/// A new table (<see cref="BulkSourceViewModel.TableReplaced"/>) is mapped again from scratch; an edit, an appended paste or
/// an added column keeps the choices of the existing columns and maps only the new ones. Automatic mapping never gives two
/// columns the same target (the second one is <em>Pomiń</em> with a note); the operator's own choices are validated
/// (<see cref="MappingError"/>).
/// </remarks>
public sealed partial class BulkColumnsViewModel : ObservableObject, IDisposable
{
    /// <summary>The non-empty cells <see cref="ColumnSynonyms.Suggest"/> looks at.</summary>
    internal const int SuggestionSamples = 20;

    /// <summary>The samples shown per column.</summary>
    internal const int ShownSamples = 3;

    private bool _validating;
    private bool _disposed;

    /// <param name="services">The dialog services of the window.</param>
    /// <param name="source">Step 1 (its <see cref="BulkSourceViewModel.Table"/> and change events).</param>
    /// <param name="session">The window's session (schema of the attribute combo), null once it ended.</param>
    /// <param name="kind">The kind of the created objects.</param>
    public BulkColumnsViewModel(DialogServices services, BulkSourceViewModel source, Func<IDirectorySession?> session, ObjectKind kind)
    {
        Services = services ?? throw new ArgumentNullException(nameof(services));
        Source = source ?? throw new ArgumentNullException(nameof(source));
        Session = session ?? throw new ArgumentNullException(nameof(session));
        Kind = kind;
        var localizer = services.Localizer;
        Targets = [.. Enum.GetValues<ColumnTargetKind>().Select(t => new Option<ColumnTargetKind>(t, localizer[BulkCreateTextKeys.TargetKey(t)]))];
        NameOrders = [.. Enum.GetValues<NameOrder>().Select(o => new Option<NameOrder>(o, localizer[BulkCreateTextKeys.NameOrderKey(o)]))];
        Cases = [.. Enum.GetValues<TextCase>().Select(c => new Option<TextCase>(c, localizer[BulkCreateTextKeys.CaseKey(c)]))];
        _schema = session()?.Schema;
        CountryTable = BulkCreateCountries.Build(localizer);
        RefreshKindLists();
        Source.TableReplaced += OnTableReplaced;
        Source.Changed += OnSourceChanged;
        localizer.CultureChanged += OnCultureChanged;
    }

    /// <summary>The mapping changed (valid or not).</summary>
    public event EventHandler? Changed;

    internal DialogServices Services { get; }

    internal BulkSourceViewModel Source { get; }

    internal Func<IDirectorySession?> Session { get; }

    /// <summary>The kind of the created objects (<see cref="OnKindChanged"/>).</summary>
    public ObjectKind Kind { get; private set; }

    /// <summary>One row per column of the table.</summary>
    public ObservableCollection<BulkColumnMappingViewModel> Mappings { get; } = [];

    /// <summary><em>Pomiń, Atrybut, Tylko we wzorcach, Imię i nazwisko, Menedżer (przełożony), Grupy, Kraj/region, Konto wygasa</em>.</summary>
    public IReadOnlyList<Option<ColumnTargetKind>> Targets { get; }

    /// <summary>The targets the kind can use (<see cref="Targets"/> without the person-only ones for groups and computers).</summary>
    [ObservableProperty]
    public partial IReadOnlyList<Option<ColumnTargetKind>> AvailableTargets { get; private set; } = [];

    public IReadOnlyList<Option<NameOrder>> NameOrders { get; }

    /// <summary><em>Wielkość liter</em>: <em>bez zmian / Jak W Nazwie / małe / WIELKIE</em>.</summary>
    public IReadOnlyList<Option<TextCase>> Cases { get; }

    /// <summary>The attributes a column can be mapped to (<c>AttributeOption.ForKinds</c> minus the blocked ones).</summary>
    [ObservableProperty]
    public partial IReadOnlyList<AttributeOption> AttributeOptions { get; private set; } = [];

    /// <summary>The countries of the <c>Country</c> target by folded name or code (<c>BulkCreateCountries.Build</c>).</summary>
    [ObservableProperty]
    public partial IReadOnlyDictionary<string, CountryValue> CountryTable { get; private set; } = new Dictionary<string, CountryValue>();

    /// <summary>The localized problem of the mapping (null when it can be used).</summary>
    [ObservableProperty]
    public partial string? MappingError { get; private set; }

    /// <summary>
    /// <em>Format międzynarodowy</em> of the phone columns (<see cref="BulkCreateRules.Phones"/>; kept in step with
    /// <see cref="BulkRulesViewModel.Phones"/>).
    /// </summary>
    [ObservableProperty]
    public partial bool InternationalPhones { get; set; }

    /// <summary>
    /// The remembered mappings of the selected naming profile (folded header or <c>#letter</c> → target), used by the
    /// automatic mapping; set by step 3 when a profile is applied.
    /// </summary>
    public IReadOnlyList<BulkCreateHeaderMapping> HeaderMappings { get; set; } = [];

    private ISchemaCatalog? _schema;

    /// <summary>Maps every column automatically (also when a table is loaded).</summary>
    [RelayCommand]
    private void AutoMap()
    {
        Rebuild(remap: true);
        RaiseChanged();
    }

    /// <summary><em>Format międzynarodowy</em> (a phone column with numbers such as <c>48600100200</c>).</summary>
    [RelayCommand]
    private void UseInternationalPhones() => InternationalPhones = true;

    /// <summary>The mapping of every column (input of the planner).</summary>
    public IReadOnlyList<ColumnMapping> ToMappings() => [.. Mappings.Select(m => m.ToMapping())];

    /// <summary>
    /// The remembered mappings of the current table for a profile (2.6 header rule): by folded header when the table has a
    /// header, by <c>#letter</c> without one; never for password-like headers or headers that look like data.
    /// </summary>
    public IReadOnlyList<BulkCreateHeaderMapping> ToHeaderMappings()
    {
        var table = Source.Table;
        var result = new List<BulkCreateHeaderMapping>();
        foreach (var mapping in Mappings)
        {
            if (mapping.IsPasswordLike || HeaderKey(table, mapping.Column) is not { } key || !BulkCreateSettings.IsStorableHeader(key))
            {
                continue;
            }

            result.Add(new BulkCreateHeaderMapping(key, mapping.Target, mapping.Target == ColumnTargetKind.Attribute ? mapping.Attribute : null, mapping.Order, mapping.Case));
        }

        return [.. result.DistinctBy(m => m.Header, StringComparer.Ordinal)];
    }

    /// <summary>The headers <see cref="ToHeaderMappings"/> would remember, as the operator sees them (<em>Zapisz</em> lists them).</summary>
    public IReadOnlyList<string> StorableHeaderNames()
    {
        var table = Source.Table;
        return [.. Mappings
            .Where(m => !m.IsPasswordLike && HeaderKey(table, m.Column) is { } key && BulkCreateSettings.IsStorableHeader(key))
            .Select(m => m.Name)];
    }

    /// <summary>The kind changed (attribute options, kind-dependent synonyms): the columns are mapped again.</summary>
    public void OnKindChanged(ObjectKind kind)
    {
        Kind = kind;
        RefreshKindLists();
        Rebuild(remap: true);
        RaiseChanged();
    }

    /// <summary>Unsubscribes from step 1 and drops the samples (rule 1).</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Source.TableReplaced -= OnTableReplaced;
        Source.Changed -= OnSourceChanged;
        Services.Localizer.CultureChanged -= OnCultureChanged;
        foreach (var mapping in Mappings)
        {
            mapping.PropertyChanged -= OnMappingPropertyChanged;
        }

        Mappings.Clear();
        Changed = null;
    }

    public override string ToString() => $"Bulk create columns: {Kind}, {Mappings.Count} columns";

    // ---- Attribute names ------------------------------------------------------------------------------------------

    /// <summary>The LDAP name of an attribute typed or picked in a combo (<em>Telefon komórkowy [mobile]</em>, <c>mobile</c>, the label).</summary>
    internal string? ResolveAttribute(string? text) => ResolveAttribute(text, AttributeOptions, _schema);

    internal static string? ResolveAttribute(string? text, IReadOnlyList<AttributeOption> options, ISchemaCatalog? schema)
    {
        var value = (text ?? string.Empty).Trim();
        if (value.Length == 0)
        {
            return null;
        }

        var open = value.LastIndexOf('[');
        if (open >= 0 && value.EndsWith(']'))
        {
            value = value[(open + 1)..^1].Trim();
        }

        var option = options.FirstOrDefault(o => string.Equals(o.LdapName, value, StringComparison.OrdinalIgnoreCase))
            ?? options.FirstOrDefault(o => string.Equals(o.Text, value, StringComparison.CurrentCultureIgnoreCase))
            ?? options.FirstOrDefault(o => string.Equals(TextFolding.Fold(o.DisplayName), TextFolding.Fold(value), StringComparison.Ordinal));
        if (option is not null)
        {
            return option.LdapName;
        }

        return schema?.FindAttribute(value)?.LdapName ?? value;
    }

    /// <summary>The label of an attribute as the combo shows it.</summary>
    internal string AttributeLabel(string attribute) =>
        AttributeOptions.FirstOrDefault(o => string.Equals(o.LdapName, attribute, StringComparison.OrdinalIgnoreCase))?.Text
        ?? new AttributeOption(attribute, Services.Localizer.AttributeName(attribute, _schema)).Text;

    /// <summary>
    /// The attributes a column or a rule of <paramref name="kind"/> can set: the writable attributes of the class
    /// (<see cref="AttributeOption.ForKinds"/>) and <c>cn</c>, minus the blocked ones; without a schema the attributes of the
    /// synonym table that the kind can have.
    /// </summary>
    internal static IReadOnlyList<AttributeOption> OptionsFor(ISchemaCatalog? schema, ILocalizer localizer, ObjectKind kind)
    {
        if (schema is null)
        {
            return [];
        }

        var options = AttributeOption.ForKinds(schema, localizer, [kind])
            .Where(o => !CreationValues.IsBlockedAttribute(o.LdapName, kind, schema))
            .ToList();
        if (options.Count == 0)
        {
            options.AddRange(ColumnSynonyms.Entries
                .Select(e => e.Target.Attribute)
                .OfType<string>()
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Where(a => !CreationValues.IsBlockedAttribute(a, kind, schema))
                .Select(a => new AttributeOption(a, localizer.AttributeName(a, schema))));
        }

        if (!options.Any(o => string.Equals(o.LdapName, Attr.Cn, StringComparison.OrdinalIgnoreCase)) && !CreationValues.IsBlockedAttribute(Attr.Cn, kind, schema))
        {
            options.Add(new AttributeOption(Attr.Cn, localizer.AttributeName(Attr.Cn, schema)));
        }

        var comparer = StringComparer.Create(localizer.Culture, ignoreCase: true);
        return [.. options.DistinctBy(o => o.LdapName, StringComparer.OrdinalIgnoreCase).OrderBy(o => o.DisplayName, comparer)];
    }

    // ---- Mapping --------------------------------------------------------------------------------------------------

    private void OnTableReplaced(object? sender, EventArgs e)
    {
        Rebuild(remap: true);
        RaiseChanged();
    }

    private void OnSourceChanged(object? sender, EventArgs e)
    {
        if (Rebuild(remap: false))
        {
            RaiseChanged();
        }
    }

    /// <summary>
    /// Brings <see cref="Mappings"/> in line with the table: with <paramref name="remap"/> every column is mapped again,
    /// otherwise existing columns keep their choices and only new ones are mapped. True when the mapping rows changed.
    /// </summary>
    private bool Rebuild(bool remap)
    {
        if (_disposed)
        {
            return false;
        }

        _schema ??= Session()?.Schema;
        if (AttributeOptions.Count == 0)
        {
            RefreshKindLists();
        }

        var table = Source.Table;
        var previous = remap ? [] : Mappings.ToDictionary(m => m.Column.Index);
        var changed = remap || Mappings.Count != table.Columns.Count;
        var rows = new List<BulkColumnMappingViewModel>(table.Columns.Count);
        foreach (var column in table.Columns)
        {
            if (previous.TryGetValue(column.Index, out var kept) && kept.Column == column)
            {
                kept.Samples = SamplesOf(table, column, kept.IsPasswordLike, ShownSamples);
                rows.Add(kept);
                continue;
            }

            changed = true;
            rows.Add(CreateMapping(table, column));
        }

        if (!changed)
        {
            Validate();
            return false;
        }

        _validating = true;
        try
        {
            var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var row in rows.Where(r => previous.ContainsValue(r)))
            {
                taken.Add(TargetKey(row) ?? string.Empty);
            }

            foreach (var row in rows.Where(r => !previous.ContainsValue(r)))
            {
                AutoMapColumn(table, row, taken);
            }
        }
        finally
        {
            _validating = false;
        }

        foreach (var mapping in Mappings)
        {
            mapping.PropertyChanged -= OnMappingPropertyChanged;
        }

        Mappings.Clear();
        foreach (var row in rows)
        {
            row.PropertyChanged += OnMappingPropertyChanged;
            Mappings.Add(row);
        }

        Validate();
        return true;
    }

    private BulkColumnMappingViewModel CreateMapping(InputTable table, TableColumn column)
    {
        var passwordLike = column.Header.Trim().Length > 0 && ColumnSynonyms.IsPasswordLike(column.Header);
        return new BulkColumnMappingViewModel(column, SamplesOf(table, column, passwordLike, ShownSamples), passwordLike) { Owner = this };
    }

    /// <summary>
    /// The automatic mapping of one column (2.6): the profile's remembered header (or <c>#letter</c>), the synonyms with the
    /// first 20 non-empty cells, the LDAP name, <em>Display [ldap]</em> or the localized display name; a table without a
    /// header: the profile's letter, then the content. A target another column already has becomes <em>Pomiń</em> with a note.
    /// </summary>
    private void AutoMapColumn(InputTable table, BulkColumnMappingViewModel row, HashSet<string> taken)
    {
        var localizer = Services.Localizer;
        var column = row.Column;
        row.Note = SuggestionNote.None;
        row.NoteText = null;
        if (row.IsPasswordLike)
        {
            row.SetTarget(ColumnTargetKind.Ignore, null, NameOrder.GivenNameFirst, TextCase.AsIs);
            row.NoteText = localizer[L.BulkCreate.PasswordColumnNote];
            return;
        }

        var header = column.Header.Trim();
        var samples = SamplesOf(table, column, masked: false, SuggestionSamples);
        ColumnSuggestion? suggestion = null;
        var key = HeaderKey(table, column);
        if (key is not null && HeaderMappings.FirstOrDefault(m => string.Equals(m.Header, key, StringComparison.Ordinal)) is { } remembered
            && IsUsable(remembered.Target, remembered.Attribute))
        {
            row.SetTarget(remembered.Target, remembered.Attribute, remembered.Order, remembered.Case);
            row.NoteText = localizer[L.BulkCreate.ProfileMappingNote];
        }
        else
        {
            if (header.Length > 0)
            {
                suggestion = ColumnSynonyms.Suggest(header, Kind, AttributeNames(), samples) ?? ByName(header);
            }
            else if (samples.Count > 0)
            {
                suggestion = ColumnSynonyms.SuggestFromContent(samples, Kind);
            }

            if (suggestion is { } found && IsUsable(found.Kind, found.Attribute))
            {
                row.SetTarget(found.Kind, found.Attribute, found.Order, TextCase.AsIs);
                row.Note = found.Note;
                row.NoteText = found.Note == SuggestionNote.None ? null : localizer[BulkCreateTextKeys.SuggestionNoteKey(found.Note)];
            }
            else
            {
                row.SetTarget(ColumnTargetKind.Ignore, null, NameOrder.GivenNameFirst, TextCase.AsIs);
                if (header.Length > 0 && ColumnSynonyms.IsPersonalIdentifier(header))
                {
                    row.NoteText = localizer[L.BulkCreate.PersonalIdentifierNote];
                }
            }
        }

        if (TargetKey(row) is { } target && !taken.Add(target))
        {
            row.SetTarget(ColumnTargetKind.Ignore, null, NameOrder.GivenNameFirst, TextCase.AsIs);
            row.Note = SuggestionNote.None;
            row.NoteText = localizer[L.BulkCreate.DuplicateTargetNote];
        }

        if (row.IsPhone && row.Note == SuggestionNote.None && samples.Any(s => s.Length >= 11 && s.StartsWith("48", StringComparison.Ordinal) && s.All(char.IsAsciiDigit)))
        {
            row.Note = SuggestionNote.PhonesWithoutPlus;
            row.NoteText = localizer[BulkCreateTextKeys.SuggestionNoteKey(SuggestionNote.PhonesWithoutPlus)];
        }
    }

    /// <summary>The LDAP name, <em>Display [ldap]</em> or the localized display name of an attribute the kind can set.</summary>
    private ColumnSuggestion? ByName(string header)
    {
        var bracket = header.LastIndexOf('[');
        var name = bracket >= 0 && header.EndsWith(']') ? header[(bracket + 1)..^1].Trim() : header;
        var folded = TextFolding.Fold(header);
        var option = AttributeOptions.FirstOrDefault(o => string.Equals(o.LdapName, name, StringComparison.OrdinalIgnoreCase))
            ?? AttributeOptions.FirstOrDefault(o => string.Equals(TextFolding.Fold(o.DisplayName), folded, StringComparison.Ordinal));
        return option is null ? null : new ColumnSuggestion(ColumnTargetKind.Attribute, option.LdapName);
    }

    private IReadOnlyCollection<string>? AttributeNames() =>
        AttributeOptions.Count == 0 ? null : [.. AttributeOptions.Select(o => o.LdapName)];

    /// <summary>The target can be used for the kind (an attribute only when not blocked).</summary>
    private bool IsUsable(ColumnTargetKind target, string? attribute) => target switch
    {
        ColumnTargetKind.Attribute => attribute is { Length: > 0 }
            && (_schema is null ? !CreationValues.IsNamedBlockedAttribute(attribute) : !CreationValues.IsBlockedAttribute(attribute, Kind, _schema)),
        _ => AvailableTargets.Any(t => t.Value == target),
    };

    /// <summary>The key two columns must not share (null: several columns may have it).</summary>
    private static string? TargetKey(BulkColumnMappingViewModel row) => row.Target switch
    {
        ColumnTargetKind.Ignore or ColumnTargetKind.PatternOnly => null,
        ColumnTargetKind.Attribute => row.Attribute is { Length: > 0 } attribute ? "a:" + attribute.ToLowerInvariant() : null,
        var target => "t:" + target,
    };

    /// <summary>The key a profile remembers the column by: the folded header, or <c>#letter</c> without a header.</summary>
    private static string? HeaderKey(InputTable table, TableColumn column)
    {
        if (table.HasHeader && column.Header.Trim().Length > 0)
        {
            var folded = ColumnSynonyms.Fold(column.Header);
            return folded.Length == 0 ? null : folded;
        }

        return table.HasHeader ? null : "#" + column.Letter;
    }

    /// <summary>The first <paramref name="count"/> non-empty cells of a column (the mask for a password-like column).</summary>
    private static IReadOnlyList<string> SamplesOf(InputTable table, TableColumn column, bool masked, int count)
    {
        var samples = new List<string>(count);
        foreach (var row in table.Rows)
        {
            var cell = row.Cell(column.Index);
            if (cell.Length == 0)
            {
                continue;
            }

            samples.Add(masked ? BulkSourceViewModel.Mask : cell);
            if (samples.Count == count)
            {
                break;
            }
        }

        return samples;
    }

    // ---- Validation ---------------------------------------------------------------------------------------------

    private void OnMappingPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (_validating || e.PropertyName is not (nameof(BulkColumnMappingViewModel.Target) or nameof(BulkColumnMappingViewModel.Attribute)
            or nameof(BulkColumnMappingViewModel.Order) or nameof(BulkColumnMappingViewModel.Case)))
        {
            return;
        }

        Validate();
        RaiseChanged();
    }

    private void Validate()
    {
        var localizer = Services.Localizer;
        foreach (var mapping in Mappings)
        {
            mapping.ShowsInternationalOffer = mapping.Note == SuggestionNote.PhonesWithoutPlus && mapping.IsPhone && !InternationalPhones;
        }

        if (Mappings.Count == 0)
        {
            MappingError = Source.Table.Rows.Count == 0 ? null : localizer[L.BulkCreate.NoColumns];
            return;
        }

        foreach (var mapping in Mappings.Where(m => m.Target == ColumnTargetKind.Attribute))
        {
            if (mapping.Attribute is not { Length: > 0 } attribute)
            {
                MappingError = localizer.Format(L.BulkCreate.ChooseAttribute, mapping.Name);
                return;
            }

            if (_schema is not null && CreationValues.IsBlockedAttribute(attribute, Kind, _schema))
            {
                MappingError = localizer.Format(L.BulkCreate.AttributeNotSettable, mapping.Name, attribute);
                return;
            }
        }

        foreach (var mapping in Mappings.Where(m => m.Target != ColumnTargetKind.Attribute && m.Target != ColumnTargetKind.Ignore))
        {
            if (!AvailableTargets.Any(t => t.Value == mapping.Target))
            {
                MappingError = localizer.Format(L.BulkCreate.TargetNotForKind, mapping.Name, localizer[BulkCreateTextKeys.TargetKey(mapping.Target)]);
                return;
            }
        }

        var duplicate = Mappings
            .Select(m => (Mapping: m, Key: TargetKey(m)))
            .Where(m => m.Key is not null)
            .GroupBy(m => m.Key, StringComparer.Ordinal)
            .FirstOrDefault(g => g.Count() > 1);
        if (duplicate is not null)
        {
            var columns = duplicate.Select(m => m.Mapping).ToList();
            MappingError = localizer.Format(L.BulkCreate.DuplicateTarget, columns[0].Name, columns[1].Name);
            return;
        }

        MappingError = Mappings.All(m => m.Target == ColumnTargetKind.Ignore) ? localizer[L.BulkCreate.NothingMapped] : null;
    }

    partial void OnInternationalPhonesChanged(bool value)
    {
        Validate();
        RaiseChanged();
    }

    private void RefreshKindLists()
    {
        var localizer = Services.Localizer;
        var personal = Kind is ObjectKind.User or ObjectKind.InetOrgPerson or ObjectKind.Contact;
        AvailableTargets = [.. Targets.Where(t => t.Value switch
        {
            ColumnTargetKind.FullName or ColumnTargetKind.Country => personal,
            ColumnTargetKind.ExpiresAt => Kind is ObjectKind.User or ObjectKind.InetOrgPerson,
            _ => true,
        })];
        AttributeOptions = OptionsFor(_schema, localizer, Kind);
    }

    private void RaiseChanged()
    {
        if (!_disposed)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    private void OnCultureChanged(object? sender, EventArgs e)
    {
        CountryTable = BulkCreateCountries.Build(Services.Localizer);
        RefreshKindLists();
        foreach (var mapping in Mappings)
        {
            mapping.RefreshTexts();
        }

        Validate();
    }
}

/// <summary>The mapping of one column (2.6).</summary>
public sealed partial class BulkColumnMappingViewModel : ObservableObject
{
    private bool _syncingAttribute;

    /// <param name="column">The table column.</param>
    /// <param name="samples">The first three non-empty cells (masked for a password-like column).</param>
    /// <param name="isPasswordLike">A password-like header (2.1): forced to <see cref="ColumnTargetKind.Ignore"/>, cannot be mapped.</param>
    internal BulkColumnMappingViewModel(TableColumn column, IReadOnlyList<string> samples, bool isPasswordLike)
    {
        Column = column ?? throw new ArgumentNullException(nameof(column));
        Samples = samples ?? throw new ArgumentNullException(nameof(samples));
        IsPasswordLike = isPasswordLike;
    }

    /// <summary>The step that owns the row (attribute names and labels).</summary>
    internal BulkColumnsViewModel? Owner { get; init; }

    public TableColumn Column { get; }

    /// <summary>The header, or the Excel letter without one.</summary>
    public string Name => Column.Name;

    /// <summary>The Excel letter (the table has a header: shown next to it).</summary>
    public string Letter => Column.Letter;

    /// <summary>The first three non-empty cells (the mask for a password-like column).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SamplesText))]
    public partial IReadOnlyList<string> Samples { get; internal set; }

    /// <summary>The samples on one line.</summary>
    public string SamplesText => string.Join(" · ", Samples);

    public bool IsPasswordLike { get; }

    /// <summary>The target can be changed (not for a password-like column).</summary>
    public bool CanMap => !IsPasswordLike;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsAttributeTarget), nameof(IsFullNameTarget), nameof(CanChooseCase), nameof(IsPhone))]
    public partial ColumnTargetKind Target { get; set; } = ColumnTargetKind.Ignore;

    /// <summary>The LDAP name for <see cref="ColumnTargetKind.Attribute"/>.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPhone))]
    public partial string? Attribute { get; set; }

    /// <summary>The attribute as the editable combo shows it (<em>Telefon komórkowy [mobile]</em>); typing resolves <see cref="Attribute"/>.</summary>
    [ObservableProperty]
    public partial string AttributeText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial NameOrder Order { get; set; } = NameOrder.GivenNameFirst;

    [ObservableProperty]
    public partial TextCase Case { get; set; } = TextCase.AsIs;

    /// <summary>Why the auto-mapping chose the target beyond the header.</summary>
    [ObservableProperty]
    public partial SuggestionNote Note { get; set; } = SuggestionNote.None;

    /// <summary>The localized note (<c>BulkCreate.SuggestionNote.&lt;note&gt;</c>) or another hint such as the second-column note.</summary>
    [ObservableProperty]
    public partial string? NoteText { get; set; }

    /// <summary>The phone column offers <em>Format międzynarodowy</em> (numbers without <c>+48</c>).</summary>
    [ObservableProperty]
    public partial bool ShowsInternationalOffer { get; internal set; }

    public bool IsAttributeTarget => Target == ColumnTargetKind.Attribute;

    public bool IsFullNameTarget => Target == ColumnTargetKind.FullName;

    /// <summary><em>Wielkość liter</em> is offered for text targets (an attribute, the full name).</summary>
    public bool CanChooseCase => Target is ColumnTargetKind.Attribute or ColumnTargetKind.FullName;

    /// <summary>The column feeds a phone attribute.</summary>
    public bool IsPhone => Target == ColumnTargetKind.Attribute && Attribute is { } attribute && CreationValues.IsPhoneAttribute(attribute);

    /// <summary>This column's mapping.</summary>
    public ColumnMapping ToMapping() => new(Column.Index, IsPasswordLike ? ColumnTargetKind.Ignore : Target, Target == ColumnTargetKind.Attribute ? Attribute : null, Order, Case);

    /// <summary>Position and target – never the header or a sample.</summary>
    public override string ToString() => $"Column mapping {Column.Index} ({Column.Letter}): {Target}";

    /// <summary>Sets the target without validating in between (automatic mapping, a remembered header).</summary>
    internal void SetTarget(ColumnTargetKind target, string? attribute, NameOrder order, TextCase textCase)
    {
        Target = IsPasswordLike ? ColumnTargetKind.Ignore : target;
        Attribute = Target == ColumnTargetKind.Attribute ? attribute : null;
        Order = order;
        Case = textCase;
    }

    internal void RefreshTexts() => SyncAttributeText();

    partial void OnTargetChanged(ColumnTargetKind value)
    {
        if (IsPasswordLike && value != ColumnTargetKind.Ignore)
        {
            Target = ColumnTargetKind.Ignore;
        }
    }

    partial void OnAttributeChanged(string? value) => SyncAttributeText();

    partial void OnAttributeTextChanged(string value)
    {
        if (_syncingAttribute || Owner is null)
        {
            return;
        }

        var resolved = Owner.ResolveAttribute(value);
        if (!string.Equals(resolved, Attribute, StringComparison.Ordinal))
        {
            _syncingAttribute = true;
            try
            {
                Attribute = resolved;
            }
            finally
            {
                _syncingAttribute = false;
            }
        }
    }

    private void SyncAttributeText()
    {
        if (_syncingAttribute)
        {
            return;
        }

        _syncingAttribute = true;
        try
        {
            AttributeText = Attribute is { Length: > 0 } attribute ? Owner?.AttributeLabel(attribute) ?? attribute : string.Empty;
        }
        finally
        {
            _syncingAttribute = false;
        }
    }
}
