using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Markup;
using System.Windows.Media;
using AdMng.Presentation.BulkCreate;

namespace AdMng.App.BulkCreate;

/// <summary>
/// Step 4 Sprawdzenie: the check grid (docs/dev/phase3-bulk-create.md 2.7). DataContext: <see cref="BulkCheckViewModel"/> (B4).
/// The value columns are generated from <see cref="BulkCheckViewModel.Columns"/> (no WPF types in Presentation) and bound by
/// index to <c>Cells[i]</c>; a cell commits its text on Enter or when the editor closes (never on Escape), the open editor is
/// marked <see cref="CheckCellViewModel.IsEditing"/> so a recompute leaves its text alone, and an ambiguous manager offers its
/// candidates in the editor's drop-down.
/// </summary>
public partial class BulkCheckView : UserControl
{
    /// <summary>The fixed columns before the generated ones (include check box, status glyph).</summary>
    private const int FixedColumns = 2;

    private BulkCheckViewModel? _viewModel;
    private CheckCellViewModel? _editing;

    public BulkCheckView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        Unloaded += (_, _) => Attach(null);
        Loaded += (_, _) => Attach(DataContext as BulkCheckViewModel);
    }

    /// <summary>The generated columns (tests and screenshots check them).</summary>
    internal IReadOnlyList<DataGridColumn> ValueColumns => [.. CheckGrid.Columns.Skip(FixedColumns)];

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e) => Attach(e.NewValue as BulkCheckViewModel);

    private void Attach(BulkCheckViewModel? viewModel)
    {
        if (ReferenceEquals(viewModel, _viewModel))
        {
            return;
        }

        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged -= OnViewModelChanged;
        }

        _viewModel = viewModel;
        if (viewModel is not null)
        {
            viewModel.PropertyChanged += OnViewModelChanged;
        }

        RebuildColumns();
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(BulkCheckViewModel.Columns))
        {
            RebuildColumns();
        }
    }

    /// <summary>One column per descriptor after the fixed ones; Nr, Imię and Nazwisko frozen with the fixed columns.</summary>
    private void RebuildColumns()
    {
        while (CheckGrid.Columns.Count > FixedColumns)
        {
            CheckGrid.Columns.RemoveAt(CheckGrid.Columns.Count - 1);
        }

        var descriptors = _viewModel?.Columns ?? [];
        for (var index = 0; index < descriptors.Count; index++)
        {
            CheckGrid.Columns.Add(CreateColumn(descriptors[index], index));
        }

        CheckGrid.FrozenColumnCount = FixedColumns + descriptors.TakeWhile(d => d.IsFrozen).Count();
    }

    private static DataGridTemplateColumn CreateColumn(CheckColumnDescriptor descriptor, int index)
    {
        var path = $"Cells[{index.ToString(CultureInfo.InvariantCulture)}]";
        var column = new DataGridTemplateColumn
        {
            Header = descriptor.Header,
            IsReadOnly = !descriptor.IsEditable,
            Width = descriptor.Key switch
            {
                BulkCheckViewModel.NumberKey => new DataGridLength(48),
                BulkCheckViewModel.NotesKey => new DataGridLength(260),
                BulkCheckViewModel.GroupsKey => new DataGridLength(70),
                _ => new DataGridLength(150),
            },
            CellTemplate = ParseTemplate(CellXaml(path)),
            ClipboardContentBinding = new Binding(path + ".Value") { Mode = BindingMode.OneWay },
            SortMemberPath = null,
        };
        if (descriptor.IsEditable)
        {
            column.CellEditingTemplate = ParseTemplate(descriptor.Key == BulkCheckViewModel.ManagerKey ? ManagerEditorXaml(path) : EditorXaml(path));
        }

        return column;
    }

    private static DataTemplate ParseTemplate(string body) =>
        (DataTemplate)XamlReader.Parse(
            "<DataTemplate xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" "
            + "xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\">" + body + "</DataTemplate>");

    /// <summary>The value; edited cells in the accent colour, adjusted ones (disambiguated, sanitized, cut) in italics; the reason as tool tip.</summary>
    private static string CellXaml(string path) =>
        $"<TextBlock x:Name=\"Value\" Margin=\"4,0\" VerticalAlignment=\"Center\" TextTrimming=\"CharacterEllipsis\" "
        + $"Text=\"{{Binding {path}.Value, Mode=OneWay}}\" ToolTip=\"{{Binding {path}.ToolTip, Mode=OneWay}}\" />"
        + "<DataTemplate.Triggers>"
        + $"<DataTrigger Binding=\"{{Binding {path}.IsEdited, Mode=OneWay}}\" Value=\"True\">"
        + "<Setter TargetName=\"Value\" Property=\"Foreground\" Value=\"{DynamicResource AccentTextFillColorPrimaryBrush}\" />"
        + "<Setter TargetName=\"Value\" Property=\"FontWeight\" Value=\"SemiBold\" />"
        + "</DataTrigger>"
        + $"<DataTrigger Binding=\"{{Binding {path}.IsAdjusted, Mode=OneWay}}\" Value=\"True\">"
        + "<Setter TargetName=\"Value\" Property=\"FontStyle\" Value=\"Italic\" />"
        + "</DataTrigger>"
        + "</DataTemplate.Triggers>";

    private static string EditorXaml(string path) =>
        $"<TextBox x:Name=\"Editor\" Margin=\"0\" Padding=\"4,0\" BorderThickness=\"0\" "
        + $"Text=\"{{Binding {path}.EditText, Mode=TwoWay, UpdateSourceTrigger=Explicit}}\" />";

    /// <summary>The manager's editor: the text, and over it the candidates of an ambiguous manager.</summary>
    private static string ManagerEditorXaml(string path) =>
        "<Grid>"
        + $"<TextBox x:Name=\"Editor\" Padding=\"4,0\" BorderThickness=\"0\" Text=\"{{Binding {path}.EditText, Mode=TwoWay, UpdateSourceTrigger=Explicit}}\" />"
        + "<ComboBox x:Name=\"Candidates\" DisplayMemberPath=\"Text\" SelectedValuePath=\"Value\" Visibility=\"Collapsed\" "
        + $"ItemsSource=\"{{Binding {path}.Candidates, Mode=OneWay}}\" SelectedValue=\"{{Binding {path}.ChosenCandidate, Mode=TwoWay, UpdateSourceTrigger=Explicit}}\" />"
        + "</Grid>"
        + "<DataTemplate.Triggers>"
        + $"<DataTrigger Binding=\"{{Binding {path}.HasCandidates, Mode=OneWay}}\" Value=\"True\">"
        + "<Setter TargetName=\"Candidates\" Property=\"Visibility\" Value=\"Visible\" />"
        + "</DataTrigger>"
        + "</DataTemplate.Triggers>";

    // ------------------------------------------------------------------ editing

    /// <summary>A cell of a created row or of a read-only column does not open its editor.</summary>
    private void OnBeginningEdit(object? sender, DataGridBeginningEditEventArgs e)
    {
        if (CellOf(e.Row.Item, e.Column) is { } cell)
        {
            e.Cancel = !cell.IsEditable;
        }
    }

    private void OnPreparingCellForEdit(object? sender, DataGridPreparingCellForEditEventArgs e)
    {
        _editing = CellOf(e.Row.Item, e.Column);
        if (_editing is not null)
        {
            _editing.IsEditing = true;
        }

        if (FindChild<TextBox>(e.EditingElement, "Editor") is { } editor && FindChild<ComboBox>(e.EditingElement, "Candidates") is not { Visibility: Visibility.Visible })
        {
            editor.Focus();
            editor.SelectAll();
        }
    }

    /// <summary>Enter (or leaving the cell) commits the text; Escape drops it.</summary>
    private void OnCellEditEnding(object? sender, DataGridCellEditEndingEventArgs e)
    {
        var cell = _editing;
        _editing = null;
        if (cell is not null)
        {
            cell.IsEditing = false;
        }

        if (e.EditAction != DataGridEditAction.Commit)
        {
            return;
        }

        if (FindChild<ComboBox>(e.EditingElement, "Candidates") is { Visibility: Visibility.Visible, SelectedValue: not null } candidates)
        {
            candidates.GetBindingExpression(System.Windows.Controls.Primitives.Selector.SelectedValueProperty)?.UpdateSource();
            return;
        }

        FindChild<TextBox>(e.EditingElement, "Editor")?.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
    }

    /// <summary>Next error (F8) scrolls its row into view.</summary>
    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (CheckGrid.SelectedItem is { } item)
        {
            CheckGrid.ScrollIntoView(item);
        }
    }

    /// <summary>The cell menu: <em>Przywróć wartość wyliczoną</em> for an edited cell.</summary>
    private void OnContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        var cell = CheckGrid.CurrentCell is { Item: { } item, Column: { } column } ? CellOf(item, column) : null;
        RevertCellItem.Command = _viewModel?.RevertCellCommand;
        RevertCellItem.CommandParameter = cell;
        RevertCellItem.IsEnabled = cell is { IsEdited: true };
    }

    private CheckCellViewModel? CellOf(object item, DataGridColumn column)
    {
        var index = CheckGrid.Columns.IndexOf(column) - FixedColumns;
        return item is CheckRowViewModel row && index >= 0 && index < row.Cells.Count ? row.Cells[index] : null;
    }

    private static T? FindChild<T>(DependencyObject? parent, string name)
        where T : FrameworkElement
    {
        if (parent is null)
        {
            return null;
        }

        if (parent is T match && match.Name == name)
        {
            return match;
        }

        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            if (FindChild<T>(VisualTreeHelper.GetChild(parent, i), name) is { } found)
            {
                return found;
            }
        }

        return null;
    }
}
