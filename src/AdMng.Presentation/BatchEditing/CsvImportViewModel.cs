using System.Collections.ObjectModel;
using AdMng.Core.ActiveDirectory;
using AdMng.Core.Batch;
using AdMng.Core.Csv;
using AdMng.Core.Directory;
using AdMng.Core.Model;
using AdMng.Presentation.Dialogs;
using AdMng.Presentation.Infrastructure;
using AdMng.Presentation.Localization;
using AdMng.Presentation.Navigation;
using AdMng.Presentation.Services;
using AdMng.Presentation.Session;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AdMng.Presentation.BatchEditing;

/// <summary>One CSV column and the attribute it is written to ("" = ignored).</summary>
public sealed partial class CsvColumnMappingViewModel : ObservableObject
{
    internal CsvColumnMappingViewModel(string column, string sample)
    {
        Column = column;
        Sample = sample;
    }

    /// <summary>Raised when <see cref="Attribute"/> changed.</summary>
    public event EventHandler? Changed;

    public string Column { get; }

    /// <summary>Value of the first data row (helps choosing the attribute).</summary>
    public string Sample { get; }

    /// <summary>LDAP name of the target attribute, empty = ignore the column.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsMapped))]
    public partial string Attribute { get; set; } = string.Empty;

    /// <summary>The column identifies the objects (it is not written).</summary>
    [ObservableProperty]
    public partial bool IsKey { get; internal set; }

    public bool IsMapped => !string.IsNullOrWhiteSpace(Attribute);

    partial void OnAttributeChanged(string value) => Changed?.Invoke(this, EventArgs.Empty);
}

/// <summary>A row of the CSV preview (first rows of the file).</summary>
/// <param name="RowNumber">1-based data row number (header excluded).</param>
/// <param name="Cells">Cell values in header order (WPF binds Cells[i]).</param>
public sealed record CsvPreviewRow(int RowNumber, IReadOnlyList<string> Cells);

/// <summary>
/// CSV import (bulk modify from a spreadsheet): pick a file (delimiter and encoding detected), preview the first
/// rows, choose the key column and the attribute it matches, map columns to attributes (auto-mapped by LDAP name,
/// display name or "Display Name [ldapName]"), and plan with <see cref="ICsvImportPlanner"/>. Hosted by
/// <see cref="BatchEditorViewModel"/> in CSV mode, which shows the same preview / execute / results steps.
/// </summary>
public sealed partial class CsvImportViewModel : ObservableObject
{
    /// <summary>Rows shown in the file preview.</summary>
    public const int PreviewRowCount = 20;

    /// <summary>Attributes the key column can be matched against (indexed and unique in practice).</summary>
    public static IReadOnlyList<string> KeyAttributeNames { get; } =
        [Attr.SamAccountName, Attr.UserPrincipalName, Attr.Mail, Attr.EmployeeId, Attr.DistinguishedName, Attr.ObjectGuid];

    /// <summary>Default columns of "Download template" when nothing is mapped yet.</summary>
    public static IReadOnlyList<string> DefaultTemplateAttributes { get; } =
    [
        Attr.GivenName, Attr.Sn, Attr.DisplayName, Attr.Title, Attr.Department, Attr.Company, Attr.Office,
        Attr.TelephoneNumber, Attr.Mobile, Attr.Mail, Attr.City, Attr.Manager,
    ];

    private readonly DialogServices _services;
    private readonly BatchEngines _engines;
    private readonly Func<IDirectorySession?> _session;

    /// <param name="services">Shared dialog services.</param>
    /// <param name="engines">CSV planner and codec.</param>
    /// <param name="session">
    /// The session to plan in: the batch editor passes the session its window was opened in (null once it ended).
    /// Default: the current session.
    /// </param>
    public CsvImportViewModel(DialogServices services, BatchEngines engines, Func<IDirectorySession?>? session = null)
    {
        _services = services ?? throw new ArgumentNullException(nameof(services));
        _engines = engines ?? throw new ArgumentNullException(nameof(engines));
        _session = session ?? (() => services.Sessions.Current);
        var localizer = services.Localizer;
        KeyAttributes = [.. KeyAttributeNames.Select(a => new Option<string>(a, AttributeText(a)))];
        KeyAttribute = KeyAttributes[0];
        EmptyCellOptions =
        [
            new(EmptyCellBehavior.Ignore, localizer[L.Csv.EmptyIgnore]),
            new(EmptyCellBehavior.Clear, localizer[L.Csv.EmptyClear]),
        ];
        EmptyCells = EmptyCellOptions[0];
        KindOptions =
        [
            new(null, localizer[L.Csv.AnyKind]),
            new(ObjectKind.User, localizer.ObjectKindName(ObjectKind.User)),
            new(ObjectKind.Contact, localizer.ObjectKindName(ObjectKind.Contact)),
            new(ObjectKind.Group, localizer.ObjectKindName(ObjectKind.Group)),
            new(ObjectKind.Computer, localizer.ObjectKindName(ObjectKind.Computer)),
        ];
        Kind = KindOptions[1];
        UpdateAttributeOptions();
    }

    /// <summary>Raised when the mapping became valid or invalid (the batch editor re-evaluates Preview).</summary>
    public event EventHandler? Changed;

    [ObservableProperty]
    public partial string? FilePath { get; private set; }

    public string FileName => FilePath is null ? string.Empty : System.IO.Path.GetFileName(FilePath);

    /// <summary>The loaded table (kept in memory while the window is open; the operator's own file).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RowCount), nameof(Headers), nameof(DelimiterText), nameof(IsLoaded))]
    public partial CsvTable? Table { get; private set; }

    public bool IsLoaded => Table is not null;

    public int RowCount => Table?.Rows.Count ?? 0;

    public IReadOnlyList<string> Headers => Table?.Headers ?? [];

    /// <summary>Detected delimiter for display (";", ",", "Tab").</summary>
    public string DelimiterText => Table?.Delimiter switch
    {
        null => string.Empty,
        '\t' => _services.Localizer[L.Csv.TabDelimiter],
        char c => c.ToString(),
    };

    /// <summary>First <see cref="PreviewRowCount"/> rows.</summary>
    public BulkObservableCollection<CsvPreviewRow> PreviewRows { get; } = [];

    /// <summary>Column → attribute mapping (one entry per CSV column).</summary>
    public ObservableCollection<CsvColumnMappingViewModel> Mappings { get; } = [];

    /// <summary>Attributes offered in the mapping combo boxes (writable, non-binary, for <see cref="Kind"/>).</summary>
    [ObservableProperty]
    public partial IReadOnlyList<AttributeOption> AttributeOptions { get; private set; } = [];

    /// <summary>CSV column identifying the objects.</summary>
    [ObservableProperty]
    public partial string? KeyColumn { get; set; }

    public IReadOnlyList<Option<string>> KeyAttributes { get; }

    /// <summary>Attribute the key column is matched against.</summary>
    [ObservableProperty]
    public partial Option<string> KeyAttribute { get; set; }

    public IReadOnlyList<Option<EmptyCellBehavior>> EmptyCellOptions { get; }

    [ObservableProperty]
    public partial Option<EmptyCellBehavior> EmptyCells { get; set; }

    public IReadOnlyList<Option<ObjectKind?>> KindOptions { get; }

    /// <summary>Restrict the lookup to one kind (recommended: avoids matching a contact instead of a user).</summary>
    [ObservableProperty]
    public partial Option<ObjectKind?> Kind { get; set; }

    /// <summary>Separator of multiple values in one cell (e.g. proxyAddresses "SMTP:a@x|smtp:b@x").</summary>
    [ObservableProperty]
    public partial string MultiValueSeparator { get; set; } = "|";

    /// <summary>Where keys are looked up (null = entire domain).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SearchBaseText))]
    public partial string? SearchBaseDn { get; set; }

    public string SearchBaseText => SearchBaseDn is null ? _services.Localizer[L.Csv.EntireDomain] : AddressResolver.ToDisplayPath(SearchBaseDn);

    /// <summary>Localized load or mapping problem.</summary>
    [ObservableProperty]
    public partial string? Error { get; private set; }

    [ObservableProperty]
    public partial bool IsLoading { get; private set; }

    /// <summary>A file is loaded, a key column is chosen and at least one other column is mapped.</summary>
    public bool IsValid => Table is not null && ValidationError() is null;

    /// <summary>Localized reason why the mapping cannot be planned yet (null when <see cref="IsValid"/>).</summary>
    public string? MappingError => Table is null ? null : ValidationError();

    [RelayCommand]
    private async Task BrowseAsync()
    {
        var path = await _services.Files.OpenFileAsync(
            _services.Localizer[L.Csv.OpenTitle],
            [new FileFilter(_services.Localizer[L.Files.CsvFilter], "*.csv"), new FileFilter(_services.Localizer[L.Csv.TextFilter], "*.txt;*.tsv")])
            .ConfigureAwait(true);
        if (path is not null)
        {
            await LoadFileAsync(path).ConfigureAwait(true);
        }
    }

    [RelayCommand]
    private async Task BrowseSearchBaseAsync()
    {
        var session = _session();
        if (session is null)
        {
            return;
        }

        var picked = await _services.Dialogs.PickContainerAsync(session, SearchBaseDn).ConfigureAwait(true);
        if (picked is not null)
        {
            SearchBaseDn = picked;
        }
    }

    [RelayCommand]
    private void ClearSearchBase() => SearchBaseDn = null;

    /// <summary>Writes a CSV header (key attribute + mapped or default attributes) for the operator to fill in.</summary>
    [RelayCommand]
    private async Task DownloadTemplateAsync()
    {
        var localizer = _services.Localizer;
        var mapped = Mappings.Where(m => m.IsMapped && !m.IsKey).Select(m => m.Attribute.Trim()).ToList();
        var headers = new List<string> { KeyAttribute.Value };
        headers.AddRange((mapped.Count > 0 ? mapped : DefaultTemplateAttributes).Where(a => !headers.Contains(a, StringComparer.OrdinalIgnoreCase)));
        var path = await _services.Files.SaveFileAsync(localizer[L.Csv.TemplateTitle], "import-template.csv", [new FileFilter(localizer[L.Files.CsvFilter], "*.csv")])
            .ConfigureAwait(true);
        if (path is null)
        {
            return;
        }

        try
        {
            var delimiter = CsvFiles.ListSeparator(_services.Regional.Culture);
            await CsvFiles.WriteAsync(path, writer => _engines.Csv.Write(writer, headers, [], delimiter, protectFormulas: true)).ConfigureAwait(true);
            _services.Notifications.Show(NotificationSeverity.Success, localizer.Format(L.Csv.TemplateSaved, System.IO.Path.GetFileName(path)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            await _services.Dialogs.ShowErrorAsync(localizer[L.Csv.TemplateTitle], _services.Errors.Translate(ex)).ConfigureAwait(true);
        }
    }

    /// <summary>
    /// Reads a CSV file (UTF-8, else the system's ANSI code page - Excel's "CSV (comma delimited)" - whatever the UI
    /// language; delimiter detected).
    /// </summary>
    public async Task LoadFileAsync(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        IsLoading = true;
        Error = null;
        try
        {
            var ansiCodePage = _services.Regional.AnsiCodePage;
            var csv = _engines.Csv;
            var table = await Task.Run(async () =>
            {
                var text = await CsvFiles.ReadTextAsync(path, ansiCodePage).ConfigureAwait(false);
                using var reader = new StringReader(text);
                return csv.Read(reader);
            }).ConfigureAwait(true);
            FilePath = path;
            OnPropertyChanged(nameof(FileName));
            Load(table);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Error = _services.Errors.Translate(ex);
        }
        catch (Exception ex) when (ex is FormatException or InvalidDataException)
        {
            Error = _services.Localizer[L.Csv.InvalidFile];
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>Shows a parsed table and auto-maps its columns.</summary>
    public void Load(CsvTable table)
    {
        ArgumentNullException.ThrowIfNull(table);
        if (table.Headers.Count == 0 || table.Headers.All(string.IsNullOrWhiteSpace))
        {
            Table = null;
            Error = _services.Localizer[L.Csv.NoHeader];
            return;
        }

        Table = table;
        Error = table.Rows.Count == 0 ? _services.Localizer[L.Csv.NoRows] : null;
        PreviewRows.ReplaceAll(table.Rows.Take(PreviewRowCount)
            .Select((row, index) => new CsvPreviewRow(index + 1, [.. table.Headers.Select((_, column) => table.Cell(index, column))])));
        foreach (var mapping in Mappings)
        {
            mapping.Changed -= OnMappingChanged;
        }

        Mappings.Clear();
        for (var i = 0; i < table.Headers.Count; i++)
        {
            var mapping = new CsvColumnMappingViewModel(table.Headers[i], table.Rows.Count > 0 ? table.Cell(0, i) : string.Empty)
            {
                Attribute = AutoMap(table.Headers[i]) ?? string.Empty,
            };
            mapping.Changed += OnMappingChanged;
            Mappings.Add(mapping);
        }

        KeyColumn = ChooseKeyColumn();
        RaiseChanged();
    }

    /// <summary>The mapping as the planner needs it.</summary>
    /// <exception cref="InvalidOperationException">No file loaded or the mapping is not valid.</exception>
    public CsvImportMapping BuildMapping()
    {
        if (ValidationError() is { } error)
        {
            throw new InvalidOperationException(error);
        }

        return new CsvImportMapping
        {
            KeyColumn = KeyColumn!,
            KeyAttribute = KeyAttribute.Value,
            Columns = Mappings
                .Where(m => m.IsMapped && !string.Equals(m.Column, KeyColumn, StringComparison.Ordinal))
                .ToDictionary(m => m.Column, m => AttributeName(m.Attribute), StringComparer.Ordinal),
            EmptyCells = EmptyCells.Value,
            SearchBaseDn = SearchBaseDn,
            Kind = Kind.Value,
            MultiValueSeparator = string.IsNullOrEmpty(MultiValueSeparator) ? "|" : MultiValueSeparator,
        };
    }

    /// <summary>
    /// Plans the import with <see cref="ICsvImportPlanner"/>. With <paramref name="onlyRows"/> (1-based data rows) only
    /// these rows are planned again (retry of failed rows); the plan keeps the original row numbers.
    /// </summary>
    public async Task<ChangePlan> PlanAsync(IProgress<int>? progress, CancellationToken cancellationToken, IReadOnlyCollection<int>? onlyRows = null)
    {
        var session = _session() ?? throw new SessionEndedException();
        var mapping = BuildMapping();
        var table = Table!;
        IReadOnlyList<int> rowNumbers = onlyRows is null
            ? [.. Enumerable.Range(1, table.Rows.Count)]
            : [.. onlyRows.Where(r => r >= 1 && r <= table.Rows.Count).Distinct().Order()];
        if (onlyRows is not null)
        {
            table = table with { Rows = [.. rowNumbers.Select(r => table.Rows[r - 1])] };
        }

        var options = new PlanningOptions { MaxDegreeOfParallelism = _services.Settings.Current.BatchMaxParallelism };
        var planner = _engines.CsvPlanner;
        var plan = await Task.Run(() => planner.PlanAsync(session, table, mapping, options, progress, cancellationToken), cancellationToken)
            .ConfigureAwait(true);
        if (onlyRows is null)
        {
            return plan;
        }

        return plan with
        {
            Objects = [.. plan.Objects.Select(o => o.SourceRow is { } row && row >= 1 && row <= rowNumbers.Count ? o with { SourceRow = rowNumbers[row - 1] } : o)],
        };
    }

    partial void OnKeyColumnChanged(string? value)
    {
        foreach (var mapping in Mappings)
        {
            mapping.IsKey = string.Equals(mapping.Column, value, StringComparison.Ordinal);
        }

        RaiseChanged();
    }

    partial void OnKeyAttributeChanged(Option<string> value) => RaiseChanged();

    partial void OnKindChanged(Option<ObjectKind?> value) => UpdateAttributeOptions();

    private void OnMappingChanged(object? sender, EventArgs e) => RaiseChanged();

    private void RaiseChanged()
    {
        OnPropertyChanged(nameof(IsValid));
        OnPropertyChanged(nameof(MappingError));
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private string? ValidationError()
    {
        if (Table is null)
        {
            return _services.Localizer[L.Csv.NoFile];
        }

        if (Table.Rows.Count == 0)
        {
            return _services.Localizer[L.Csv.NoRows];
        }

        if (string.IsNullOrEmpty(KeyColumn) || Table.IndexOf(KeyColumn) < 0)
        {
            return _services.Localizer[L.Csv.KeyColumnRequired];
        }

        var mapped = Mappings.Where(m => m.IsMapped && !string.Equals(m.Column, KeyColumn, StringComparison.Ordinal)).ToList();
        if (mapped.Count == 0)
        {
            return _services.Localizer[L.Csv.MappingRequired];
        }

        var duplicate = mapped.GroupBy(m => AttributeName(m.Attribute), StringComparer.OrdinalIgnoreCase).FirstOrDefault(g => g.Count() > 1);
        if (duplicate is not null)
        {
            return _services.Localizer.Format(L.Csv.DuplicateMapping, duplicate.Key);
        }

        var schema = _session()?.Schema;
        var unknown = mapped.FirstOrDefault(m => schema is { Attributes.Count: > 0 } && schema.FindAttribute(AttributeName(m.Attribute)) is null);
        return unknown is null ? null : _services.Localizer.Format(L.Csv.UnknownAttribute, unknown.Attribute);
    }

    private void UpdateAttributeOptions()
    {
        var schema = _session()?.Schema;
        if (schema is null)
        {
            AttributeOptions = [];
            return;
        }

        IReadOnlyList<ObjectKind> kinds = Kind.Value is { } kind ? [kind] : [ObjectKind.User, ObjectKind.Contact, ObjectKind.Group, ObjectKind.Computer];
        AttributeOptions = AttributeOption.ForKinds(schema, _services.Localizer, kinds);
    }

    /// <summary>"Display Name [ldapName]" → ldapName; a plain LDAP name stays as it is.</summary>
    private static string AttributeName(string text)
    {
        var value = text.Trim();
        var open = value.LastIndexOf('[');
        return open >= 0 && value.EndsWith(']') ? value[(open + 1)..^1].Trim() : value;
    }

    /// <summary>Maps a header to an attribute: LDAP name, "Display Name [ldapName]" or the localized display name.</summary>
    private string? AutoMap(string header)
    {
        var text = header.Trim();
        if (text.Length == 0)
        {
            return null;
        }

        var schema = _session()?.Schema;
        var bracketed = AttributeName(text);
        if (!string.Equals(bracketed, text, StringComparison.Ordinal) && (schema is null || schema.FindAttribute(bracketed) is not null))
        {
            return schema?.FindAttribute(bracketed)?.LdapName ?? bracketed;
        }

        if (schema?.FindAttribute(text) is { } definition)
        {
            return definition.LdapName;
        }

        if (KeyAttributeNames.FirstOrDefault(k => string.Equals(k, text, StringComparison.OrdinalIgnoreCase)) is { } key)
        {
            return key;
        }

        var byDisplayName = AttributeOptions.FirstOrDefault(o => string.Equals(o.DisplayName, text, StringComparison.CurrentCultureIgnoreCase)
            || TextFolding.Fold(o.DisplayName) == TextFolding.Fold(text));
        return byDisplayName?.LdapName;
    }

    /// <summary>The first column mapped to a key attribute (sAMAccountName preferred), else the first column.</summary>
    private string? ChooseKeyColumn()
    {
        foreach (var key in KeyAttributeNames)
        {
            var column = Mappings.FirstOrDefault(m => string.Equals(m.Attribute, key, StringComparison.OrdinalIgnoreCase));
            if (column is not null)
            {
                KeyAttribute = KeyAttributes.First(k => k.Value == key);
                column.Attribute = string.Empty;
                return column.Column;
            }
        }

        return Mappings.FirstOrDefault()?.Column;
    }

    private string AttributeText(string attribute)
    {
        var name = _services.Localizer.AttributeName(attribute, _session()?.Schema);
        return string.Equals(name, attribute, StringComparison.OrdinalIgnoreCase) ? attribute : $"{name} [{attribute}]";
    }
}
