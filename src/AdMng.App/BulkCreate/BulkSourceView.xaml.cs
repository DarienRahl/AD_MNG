using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using AdMng.App.Controls;
using AdMng.App.Localization;
using AdMng.Presentation.BulkCreate;
using AdMng.Presentation.Localization;

namespace AdMng.App.BulkCreate;

/// <summary>
/// Step 1 Dane: paste, file, typed rows, header choice, warnings (docs/dev/phase3-bulk-create.md 2.6). DataContext:
/// <see cref="BulkSourceViewModel"/>. The grid's columns follow <see cref="BulkSourceViewModel.Columns"/>: the Excel row, then
/// one text column per table column bound to the row indexer (two-way; password-like columns read-only, shown and copied as
/// the mask).
/// </summary>
public partial class BulkSourceView : UserControl
{
    private BulkSourceViewModel? _viewModel;

    public BulkSourceView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => Attach(DataContext as BulkSourceViewModel);
        Loaded += (_, _) => Attach(DataContext as BulkSourceViewModel);
        Unloaded += (_, _) => Attach(null);
    }

    private void Attach(BulkSourceViewModel? viewModel)
    {
        if (ReferenceEquals(_viewModel, viewModel))
        {
            return;
        }

        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        }

        _viewModel = viewModel;
        if (viewModel is not null)
        {
            viewModel.PropertyChanged += OnViewModelPropertyChanged;
        }

        BuildColumns();
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(BulkSourceViewModel.Columns) or null or "")
        {
            BuildColumns();
        }
    }

    /// <summary>The Excel row plus one column per table column (bound to <c>[i]</c> of <see cref="SourceRowViewModel"/>).</summary>
    private void BuildColumns()
    {
        SourceGrid.Columns.Clear();
        if (_viewModel is not { } viewModel)
        {
            return;
        }

        var rowColumn = new DataGridTextColumn
        {
            Binding = new Binding(nameof(SourceRowViewModel.ExcelRow)) { Mode = BindingMode.OneWay },
            IsReadOnly = true,
            Width = new DataGridLength(56),
        };
        BindingOperations.SetBinding(rowColumn, DataGridColumn.HeaderProperty, new TrBinding(L.BulkCreate.ExcelRowColumn));
        SourceGrid.Columns.Add(rowColumn);
        foreach (var column in viewModel.Columns)
        {
            var binding = new Binding($"[{column.Index}]")
            {
                Mode = column.IsMasked ? BindingMode.OneWay : BindingMode.TwoWay,
                UpdateSourceTrigger = UpdateSourceTrigger.LostFocus,
            };
            var gridColumn = new DataGridTextColumn
            {
                Header = HeaderFor(column),
                Binding = binding,
                IsReadOnly = column.IsReadOnly,
                Width = new DataGridLength(column.IsMasked ? 110 : 150),
                MaxWidth = 420,
            };
            if (column.IsMasked)
            {
                // The copy of a masked cell is the mask, never the cell (rule 2).
                gridColumn.ClipboardContentBinding = new Binding { Source = BulkSourceViewModel.Mask, Mode = BindingMode.OneTime };
            }

            SourceGrid.Columns.Add(gridColumn);
        }
    }

    private StackPanel HeaderFor(SourceColumnViewModel column)
    {
        var header = new StackPanel { Orientation = Orientation.Horizontal };
        if (column.IsMasked)
        {
            var glyph = new TextBlock
            {
                Margin = new Thickness(0, 0, 6, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Text = Glyphs.Lock,
                Style = TryFindResource("Ad.GlyphText") as Style,
            };
            BindingOperations.SetBinding(glyph, ToolTipProperty, new TrBinding(L.BulkCreate.MaskedColumnToolTip));
            header.Children.Add(glyph);
        }

        header.Children.Add(new TextBlock
        {
            Text = column.Name,
            FontWeight = FontWeights.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxWidth = 380,
            ToolTip = column.Name,
        });
        if (!string.Equals(column.Name, column.Letter, StringComparison.Ordinal))
        {
            header.Children.Add(new TextBlock
            {
                Margin = new Thickness(6, 0, 0, 0),
                Text = column.Letter,
                Opacity = 0.6,
            });
        }

        return header;
    }
}
