using System.Collections.ObjectModel;
using AdMng.Core.GroupPolicy.Management;
using AdMng.Presentation.Dialogs;
using AdMng.Presentation.Localization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AdMng.Presentation.GroupPolicy;

/// <summary>One query of <see cref="WmiFilterEditorViewModel"/>.</summary>
public sealed partial class WmiQueryRowViewModel : ObservableObject
{
    public WmiQueryRowViewModel(string @namespace, string query)
    {
        Namespace = @namespace;
        Query = query;
    }

    [ObservableProperty]
    public partial string Namespace { get; set; }

    [ObservableProperty]
    public partial string Query { get; set; }

    /// <summary>The namespace and the query are filled.</summary>
    public bool IsComplete => !string.IsNullOrWhiteSpace(Namespace) && !string.IsNullOrWhiteSpace(Query);
}

/// <summary>What the WMI filter editor returns.</summary>
/// <param name="Name">msWMI-Name.</param>
/// <param name="Description">msWMI-Parm1 (null: empty).</param>
/// <param name="Queries">The queries (msWMI-Parm2).</param>
public sealed record WmiFilterEditorResult(string Name, string? Description, IReadOnlyList<WmiFilterQuery> Queries);

/// <summary>
/// A new or edited WMI filter (design note 5.2 <c>WmiFilterEditorWindow</c>): name, description and the WQL queries with
/// their namespaces (<c>root\CIMv2</c> by default). The caller plans the create or edit with <see cref="Result"/>.
/// </summary>
public sealed partial class WmiFilterEditorViewModel : WindowViewModelBase
{
    /// <summary>Longest filter name (msWMI-Name).</summary>
    public const int MaxNameLength = 255;

    private readonly WmiFilterInfo? _filter;

    public WmiFilterEditorViewModel(DialogServices services, WmiFilterInfo? filter)
        : base(services)
    {
        _filter = filter;
        Name = filter?.Name ?? string.Empty;
        Description = filter?.Description ?? string.Empty;
        foreach (var query in filter?.Queries ?? [])
        {
            Add(new WmiQueryRowViewModel(query.Namespace, query.Query));
        }

        if (Queries.Count == 0)
        {
            Add(new WmiQueryRowViewModel(WmiFilterQueries.DefaultNamespace, string.Empty));
        }

        Queries.CollectionChanged += (_, _) => Revalidate();
    }

    public override string WindowTitle => _filter is null ? Localizer[L.GroupPolicy.WmiNewWindowTitle] : Localizer.Format(L.GroupPolicy.WmiEditWindowTitle, _filter.Name);

    public bool IsNew => _filter is null;

    /// <summary>The filter's queries could not be parsed: saving replaces them with the queries shown here.</summary>
    public bool HasUnparsedQueries => _filter is { Queries.Count: 0, RawQueries: { Length: > 0 } };

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ValidationError), nameof(HasValidationError))]
    [NotifyCanExecuteChangedFor(nameof(OkCommand))]
    public partial string Name { get; set; }

    [ObservableProperty]
    public partial string Description { get; set; }

    public ObservableCollection<WmiQueryRowViewModel> Queries { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RemoveQueryCommand))]
    public partial WmiQueryRowViewModel? SelectedQuery { get; set; }

    /// <summary>Why the filter cannot be saved (null when it can).</summary>
    public string? ValidationError
    {
        get
        {
            var name = (Name ?? string.Empty).Trim();
            if (name.Length == 0)
            {
                return Localizer[L.GroupPolicy.NameRequired];
            }

            if (name.Length > MaxNameLength)
            {
                return Localizer.Format(L.GroupPolicy.NameTooLong, MaxNameLength);
            }

            return Queries.Count == 0 || Queries.Any(q => !q.IsComplete) ? Localizer[L.GroupPolicy.WmiQueryRequired] : null;
        }
    }

    public bool HasValidationError => ValidationError is not null;

    /// <summary>The filter to plan (set by OK).</summary>
    public WmiFilterEditorResult? Result { get; private set; }

    [RelayCommand]
    private void AddQuery()
    {
        var row = new WmiQueryRowViewModel(WmiFilterQueries.DefaultNamespace, string.Empty);
        Add(row);
        SelectedQuery = row;
    }

    [RelayCommand(CanExecute = nameof(CanRemoveQuery))]
    private void RemoveQuery()
    {
        if (SelectedQuery is { } row)
        {
            row.PropertyChanged -= OnQueryChanged;
            Queries.Remove(row);
            SelectedQuery = Queries.FirstOrDefault();
        }
    }

    private bool CanRemoveQuery() => SelectedQuery is not null && Queries.Count > 1;

    [RelayCommand(CanExecute = nameof(CanOk))]
    private void Ok()
    {
        Result = new WmiFilterEditorResult(
            Name.Trim(),
            string.IsNullOrWhiteSpace(Description) ? null : Description.Trim(),
            [.. Queries.Select(q => new WmiFilterQuery(q.Namespace.Trim(), q.Query.Trim()))]);
        RequestClose(true);
    }

    private bool CanOk() => ValidationError is null;

    [RelayCommand]
    private void Cancel() => RequestClose(false);

    protected override void OnCultureChanged()
    {
        base.OnCultureChanged();
        OnPropertyChanged(nameof(ValidationError));
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            foreach (var row in Queries)
            {
                row.PropertyChanged -= OnQueryChanged;
            }
        }

        base.Dispose(disposing);
    }

    private void Add(WmiQueryRowViewModel row)
    {
        row.PropertyChanged += OnQueryChanged;
        Queries.Add(row);
    }

    private void OnQueryChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e) => Revalidate();

    private void Revalidate()
    {
        OnPropertyChanged(nameof(ValidationError));
        OnPropertyChanged(nameof(HasValidationError));
        OkCommand.NotifyCanExecuteChanged();
        RemoveQueryCommand.NotifyCanExecuteChanged();
    }
}
