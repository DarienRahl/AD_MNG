using System.Collections.ObjectModel;
using AdMng.Core.ActiveDirectory;
using AdMng.Core.Schema;
using AdMng.Presentation.Infrastructure;
using AdMng.Presentation.Localization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AdMng.Presentation.Lists;

/// <summary>An attribute offered by the column chooser.</summary>
public sealed record ColumnChoice(string Attribute, string DisplayName)
{
    /// <summary>"Department [department]" - shows both names so LDAP-savvy operators find attributes quickly.</summary>
    public string Text => string.Equals(DisplayName, Attribute, StringComparison.OrdinalIgnoreCase) ? Attribute : $"{DisplayName} [{Attribute}]";
}

/// <summary>
/// "Add/Remove Columns" dialog: all non-binary attributes allowed for users, computers, groups and contacts (from
/// the schema) plus the Type and Location pseudo columns; the selection is ordered.
/// </summary>
public sealed partial class ColumnChooserViewModel : ObservableObject
{
    private static readonly string[] ObjectClassesForColumns =
        [ObjectClasses.User, ObjectClasses.InetOrgPerson, ObjectClasses.Computer, ObjectClasses.Group, ObjectClasses.Contact];

    private readonly List<ColumnChoice> _all;
    private readonly IReadOnlyList<string> _defaults;
    private readonly Dictionary<string, ColumnChoice> _byAttribute;

    public ColumnChooserViewModel(ILocalizer localizer, ISchemaCatalog? schema, IReadOnlyList<string> current, IReadOnlyList<string> defaults)
    {
        ArgumentNullException.ThrowIfNull(localizer);
        ArgumentNullException.ThrowIfNull(current);
        _defaults = defaults ?? throw new ArgumentNullException(nameof(defaults));

        var attributes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (schema is not null)
        {
            foreach (var attribute in schema.GetAllowedAttributes(ObjectClassesForColumns))
            {
                if (!schema.IsBinary(attribute))
                {
                    attributes.Add(attribute);
                }
            }
        }

        if (attributes.Count == 0)
        {
            attributes.UnionWith(DisplayNames.CommonAttributes.Where(a => schema is null || !schema.IsBinary(a)));
        }

        attributes.ExceptWith([Attr.ObjectGuid, Attr.ObjectSid, Attr.NtSecurityDescriptor]);
        attributes.UnionWith(current);
        attributes.UnionWith(defaults);

        string NameOf(string attribute) => attribute switch
        {
            ColumnSets.TypeColumn => localizer[L.Columns.Type],
            ColumnSets.LocationColumn => localizer[L.Columns.Location],
            Attr.Name => localizer[L.Columns.Name],
            _ => localizer.AttributeName(attribute, schema),
        };

        var comparer = StringComparer.Create(localizer.Culture, ignoreCase: true);
        _all = [.. attributes
            .Append(ColumnSets.TypeColumn)
            .Append(ColumnSets.LocationColumn)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(a => new ColumnChoice(a, NameOf(a)))
            .OrderBy(c => c.DisplayName, comparer)];
        _byAttribute = _all.ToDictionary(c => c.Attribute, StringComparer.OrdinalIgnoreCase);
        foreach (var attribute in current)
        {
            if (_byAttribute.TryGetValue(attribute, out var choice))
            {
                Selected.Add(choice);
            }
        }

        RefreshAvailable();
    }

    /// <summary>Attributes not selected that match <see cref="FilterText"/>.</summary>
    public ObservableCollection<ColumnChoice> Available { get; } = [];

    /// <summary>Selected columns in display order.</summary>
    public ObservableCollection<ColumnChoice> Selected { get; } = [];

    [ObservableProperty]
    public partial string FilterText { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AddCommand))]
    public partial ColumnChoice? SelectedAvailable { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RemoveCommand), nameof(MoveUpCommand), nameof(MoveDownCommand))]
    public partial ColumnChoice? SelectedColumn { get; set; }

    /// <summary>The chosen attributes in order (what the list applies).</summary>
    public IReadOnlyList<string> Result => [.. Selected.Select(c => c.Attribute)];

    [RelayCommand(CanExecute = nameof(CanAdd))]
    private void Add(ColumnChoice? choice)
    {
        choice ??= SelectedAvailable;
        if (choice is null || Selected.Contains(choice))
        {
            return;
        }

        Selected.Add(choice);
        RefreshAvailable();
    }

    private bool CanAdd(ColumnChoice? choice) => (choice ?? SelectedAvailable) is not null;

    [RelayCommand(CanExecute = nameof(CanRemove))]
    private void Remove(ColumnChoice? choice)
    {
        choice ??= SelectedColumn;
        if (choice is null || Selected.Count <= 1)
        {
            return;
        }

        Selected.Remove(choice);
        RefreshAvailable();
    }

    private bool CanRemove(ColumnChoice? choice) => (choice ?? SelectedColumn) is not null && Selected.Count > 1;

    [RelayCommand(CanExecute = nameof(CanMoveUp))]
    private void MoveUp() => Move(-1);

    private bool CanMoveUp() => SelectedColumn is not null && Selected.IndexOf(SelectedColumn) > 0;

    [RelayCommand(CanExecute = nameof(CanMoveDown))]
    private void MoveDown() => Move(1);

    private bool CanMoveDown() => SelectedColumn is not null && Selected.IndexOf(SelectedColumn) is var i && i >= 0 && i < Selected.Count - 1;

    [RelayCommand]
    private void ResetToDefault()
    {
        Selected.Clear();
        foreach (var attribute in _defaults)
        {
            if (_byAttribute.TryGetValue(attribute, out var choice))
            {
                Selected.Add(choice);
            }
        }

        RefreshAvailable();
    }

    partial void OnFilterTextChanged(string value) => RefreshAvailable();

    private void Move(int delta)
    {
        if (SelectedColumn is not { } column)
        {
            return;
        }

        var index = Selected.IndexOf(column);
        var target = index + delta;
        if (index < 0 || target < 0 || target >= Selected.Count)
        {
            return;
        }

        Selected.Move(index, target);
        SelectedColumn = column;
        MoveUpCommand.NotifyCanExecuteChanged();
        MoveDownCommand.NotifyCanExecuteChanged();
    }

    private void RefreshAvailable()
    {
        var selected = new HashSet<string>(Selected.Select(c => c.Attribute), StringComparer.OrdinalIgnoreCase);
        Available.Clear();
        foreach (var choice in _all)
        {
            if (!selected.Contains(choice.Attribute)
                && (TextFolding.Contains(choice.DisplayName, FilterText) || TextFolding.Contains(choice.Attribute, FilterText)))
            {
                Available.Add(choice);
            }
        }

        RemoveCommand.NotifyCanExecuteChanged();
    }
}
