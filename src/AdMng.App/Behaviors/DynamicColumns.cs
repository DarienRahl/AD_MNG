using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using AdMng.App.Infrastructure;
using AdMng.Core.ActiveDirectory;
using AdMng.Presentation.Lists;

namespace AdMng.App.Behaviors;

/// <summary>
/// Builds the <see cref="GridView"/> columns of a <see cref="ListView"/> from a list view model's
/// <see cref="ColumnViewModel"/> collection: visible columns in order, cells bound to the row indexer
/// (<c>row["department"]</c>, escaped), the name column with icon and badges, header text and sort indicator from the
/// column view model, widths two-way (a resized column is persisted by the view model). A header click executes
/// <see cref="SortCommandProperty"/> with the column. Templates come from the resources
/// <see cref="HeaderTemplateKey"/> and <see cref="NameCellTemplateKey"/> (Themes/Controls.xaml).
/// </summary>
public static class DynamicColumns
{
    /// <summary>Resource key of the column header template (DataContext: <see cref="ColumnViewModel"/>).</summary>
    public const string HeaderTemplateKey = "Ad.ColumnHeaderTemplate";

    /// <summary>Resource key of the name cell template (DataContext: <see cref="ObjectRowViewModel"/>).</summary>
    public const string NameCellTemplateKey = "Ad.NameCellTemplate";

    public static readonly DependencyProperty SourceProperty = DependencyProperty.RegisterAttached(
        "Source", typeof(ObservableCollection<ColumnViewModel>), typeof(DynamicColumns), new PropertyMetadata(null, OnSourceChanged));

    public static readonly DependencyProperty SortCommandProperty = DependencyProperty.RegisterAttached(
        "SortCommand", typeof(ICommand), typeof(DynamicColumns), new PropertyMetadata(null));

    private static readonly DependencyProperty BinderProperty = DependencyProperty.RegisterAttached(
        "Binder", typeof(ColumnBinder), typeof(DynamicColumns), new PropertyMetadata(null));

    public static ObservableCollection<ColumnViewModel>? GetSource(DependencyObject element)
    {
        ArgumentNullException.ThrowIfNull(element);
        return (ObservableCollection<ColumnViewModel>?)element.GetValue(SourceProperty);
    }

    public static void SetSource(DependencyObject element, ObservableCollection<ColumnViewModel>? value)
    {
        ArgumentNullException.ThrowIfNull(element);
        element.SetValue(SourceProperty, value);
    }

    public static ICommand? GetSortCommand(DependencyObject element)
    {
        ArgumentNullException.ThrowIfNull(element);
        return (ICommand?)element.GetValue(SortCommandProperty);
    }

    public static void SetSortCommand(DependencyObject element, ICommand? value)
    {
        ArgumentNullException.ThrowIfNull(element);
        element.SetValue(SortCommandProperty, value);
    }

    /// <summary>Default width of a column whose width was never changed (view model width 0).</summary>
    public static double DefaultWidth(string attribute) => attribute switch
    {
        _ when string.Equals(attribute, Attr.Name, StringComparison.OrdinalIgnoreCase) => 240,
        _ when string.Equals(attribute, ColumnSets.TypeColumn, StringComparison.OrdinalIgnoreCase) => 170,
        _ when string.Equals(attribute, ColumnSets.LocationColumn, StringComparison.OrdinalIgnoreCase) => 260,
        _ when string.Equals(attribute, Attr.Description, StringComparison.OrdinalIgnoreCase) => 320,
        _ => 160,
    };

    private static void OnSourceChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not ListView list)
        {
            return;
        }

        if (list.GetValue(BinderProperty) is not ColumnBinder binder)
        {
            binder = new ColumnBinder(list);
            list.SetValue(BinderProperty, binder);
        }

        binder.SetSource(e.NewValue as ObservableCollection<ColumnViewModel>);
    }

    /// <summary>Keeps the GridView in sync while the list view is loaded (no subscriptions outlive the view).</summary>
    private sealed class ColumnBinder
    {
        private readonly ListView _list;
        private readonly List<ColumnViewModel> _observed = [];
        private ObservableCollection<ColumnViewModel>? _source;
        private bool _attached;

        public ColumnBinder(ListView list)
        {
            _list = list;
            _list.Loaded += (_, _) => Attach();
            _list.Unloaded += (_, _) => Detach();
            _list.AddHandler(ButtonBase.ClickEvent, new RoutedEventHandler(OnHeaderClick));
        }

        public void SetSource(ObservableCollection<ColumnViewModel>? source)
        {
            Detach();
            _source = source;
            if (_list.IsLoaded)
            {
                Attach();
            }
            else
            {
                Rebuild();
            }
        }

        private void Attach()
        {
            if (_attached || _source is null)
            {
                return;
            }

            _attached = true;
            _source.CollectionChanged += OnCollectionChanged;
            Observe();
            Rebuild();
        }

        private void Detach()
        {
            if (!_attached)
            {
                return;
            }

            _attached = false;
            if (_source is not null)
            {
                _source.CollectionChanged -= OnCollectionChanged;
            }

            foreach (var column in _observed)
            {
                column.PropertyChanged -= OnColumnChanged;
            }

            _observed.Clear();
        }

        private void Observe()
        {
            foreach (var column in _observed)
            {
                column.PropertyChanged -= OnColumnChanged;
            }

            _observed.Clear();
            if (_source is null)
            {
                return;
            }

            foreach (var column in _source)
            {
                column.PropertyChanged += OnColumnChanged;
                _observed.Add(column);
            }
        }

        private void OnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            Observe();
            Rebuild();
        }

        private void OnColumnChanged(object? sender, PropertyChangedEventArgs e)
        {
            // Header and sort direction are bound in the header template, the width two-way.
            if (e.PropertyName is nameof(ColumnViewModel.IsVisible) or null or "")
            {
                Rebuild();
            }
        }

        private void Rebuild()
        {
            if (_list.View is not GridView view)
            {
                view = new GridView();
                _list.View = view;
            }

            foreach (var column in view.Columns)
            {
                BindingOperations.ClearAllBindings(column);
            }

            view.Columns.Clear();
            if (_source is null)
            {
                return;
            }

            var headerTemplate = _list.TryFindResource(HeaderTemplateKey) as DataTemplate;
            var nameTemplate = _list.TryFindResource(NameCellTemplateKey) as DataTemplate;
            foreach (var column in _source.Where(c => c.IsVisible))
            {
                view.Columns.Add(CreateColumn(column, headerTemplate, nameTemplate));
            }
        }

        private static GridViewColumn CreateColumn(ColumnViewModel column, DataTemplate? headerTemplate, DataTemplate? nameTemplate)
        {
            var result = new GridViewColumn { Header = column, HeaderTemplate = headerTemplate };
            if (nameTemplate is not null && string.Equals(column.Attribute, Attr.Name, StringComparison.OrdinalIgnoreCase))
            {
                result.CellTemplate = nameTemplate;
            }
            else
            {
                result.DisplayMemberBinding = new Binding(BindingPaths.Indexer(column.Attribute)) { Mode = BindingMode.OneWay };
            }

            BindingOperations.SetBinding(result, GridViewColumn.WidthProperty, new Binding(nameof(ColumnViewModel.Width))
            {
                Source = column,
                Mode = BindingMode.TwoWay,
                Converter = new WidthConverter(DefaultWidth(column.Attribute)),
            });
            if (headerTemplate is null)
            {
                result.Header = column.Header;
            }

            return result;
        }

        private void OnHeaderClick(object sender, RoutedEventArgs e)
        {
            if (e.OriginalSource is GridViewColumnHeader { Role: GridViewColumnHeaderRole.Normal, Column.Header: ColumnViewModel column }
                && GetSortCommand(_list) is { } command
                && command.CanExecute(column))
            {
                command.Execute(column);
                e.Handled = true;
            }
        }
    }

    /// <summary>View model width (0 = never resized) ↔ GridView column width.</summary>
    private sealed class WidthConverter(double defaultWidth) : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
            value is double width && width > 0 && !double.IsNaN(width) ? width : defaultWidth;

        // NaN = auto-size (double-click on the gripper): shown, but the stored width stays as it was.
        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
            value is double width && width > 0 && !double.IsNaN(width) ? Math.Round(width) : Binding.DoNothing;
    }
}
