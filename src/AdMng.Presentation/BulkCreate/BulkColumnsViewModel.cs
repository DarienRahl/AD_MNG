using System.Collections.ObjectModel;
using AdMng.Core.Batch.Creation;
using AdMng.Core.Directory;
using AdMng.Core.Model;
using AdMng.Presentation.BatchEditing;
using AdMng.Presentation.Dialogs;
using AdMng.Presentation.Infrastructure;
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
/// <remarks>Contract stub: the option lists are built, the commands and outputs throw until B3 writes them.</remarks>
public sealed partial class BulkColumnsViewModel : ObservableObject, IDisposable
{
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

    /// <summary>Maps every column automatically (also when a table is loaded).</summary>
    [RelayCommand]
    private void AutoMap() => throw new NotImplementedException("F3 B3: BulkColumnsViewModel.AutoMap (2.6).");

    /// <summary>The mapping of every column (input of the planner).</summary>
    public IReadOnlyList<ColumnMapping> ToMappings() => throw new NotImplementedException("F3 B3: BulkColumnsViewModel.ToMappings (2.6).");

    /// <summary>The kind changed (attribute options, kind-dependent synonyms). Contract: records the kind.</summary>
    public void OnKindChanged(ObjectKind kind)
    {
        Kind = kind;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Unsubscribes from step 1 (B3). Contract: nothing to undo.</summary>
    public void Dispose()
    {
    }

    public override string ToString() => $"Bulk create columns: {Kind}, {Mappings.Count} columns";
}

/// <summary>The mapping of one column (2.6).</summary>
public sealed partial class BulkColumnMappingViewModel : ObservableObject
{
    /// <param name="column">The table column.</param>
    /// <param name="samples">The first three non-empty cells (masked for a password-like column).</param>
    /// <param name="isPasswordLike">A password-like header (2.1): forced to <see cref="ColumnTargetKind.Ignore"/>, cannot be mapped.</param>
    internal BulkColumnMappingViewModel(TableColumn column, IReadOnlyList<string> samples, bool isPasswordLike)
    {
        Column = column ?? throw new ArgumentNullException(nameof(column));
        Samples = samples ?? throw new ArgumentNullException(nameof(samples));
        IsPasswordLike = isPasswordLike;
    }

    public TableColumn Column { get; }

    /// <summary>The header, or the Excel letter without one.</summary>
    public string Name => Column.Name;

    public IReadOnlyList<string> Samples { get; }

    public bool IsPasswordLike { get; }

    [ObservableProperty]
    public partial ColumnTargetKind Target { get; set; } = ColumnTargetKind.Ignore;

    /// <summary>The LDAP name for <see cref="ColumnTargetKind.Attribute"/>.</summary>
    [ObservableProperty]
    public partial string? Attribute { get; set; }

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

    /// <summary>This column's mapping.</summary>
    public ColumnMapping ToMapping() => new(Column.Index, IsPasswordLike ? ColumnTargetKind.Ignore : Target, Attribute, Order, Case);

    /// <summary>Position and target – never the header or a sample.</summary>
    public override string ToString() => $"Column mapping {Column.Index} ({Column.Letter}): {Target}";
}
