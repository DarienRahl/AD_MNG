using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Threading;
using AdMng.Presentation.Shell;

namespace AdMng.App.GroupPolicy;

/// <summary>
/// A splitter that can be seen (K6, docs/dev/k-computers-and-gpo.md 20.3): a thin divider line in the middle of a wider
/// transparent grip, highlighted while the mouse is over it or it is dragged. Set <see cref="GridSplitter.ResizeDirection"/>
/// (<see cref="GridResizeDirection.Columns"/> draws a vertical line, <see cref="GridResizeDirection.Rows"/> a horizontal one).
/// </summary>
public sealed class PaneSplitter : GridSplitter
{
    private static readonly Lazy<ControlTemplate> ColumnTemplate = new(() => CreateTemplate(vertical: true));
    private static readonly Lazy<ControlTemplate> RowTemplate = new(() => CreateTemplate(vertical: false));

    public PaneSplitter()
    {
        Background = System.Windows.Media.Brushes.Transparent;
        Focusable = false;
        ResizeBehavior = GridResizeBehavior.PreviousAndNext;
        ResizeDirection = GridResizeDirection.Columns;
        Width = 8;
        HorizontalAlignment = HorizontalAlignment.Center;
        VerticalAlignment = VerticalAlignment.Stretch;
        Template = ColumnTemplate.Value;
    }

    protected override void OnPropertyChanged(DependencyPropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.Property == ResizeDirectionProperty)
        {
            var rows = ResizeDirection == GridResizeDirection.Rows;
            Template = rows ? RowTemplate.Value : ColumnTemplate.Value;
            if (rows)
            {
                ClearValue(WidthProperty);
                Height = 8;
                HorizontalAlignment = HorizontalAlignment.Stretch;
                VerticalAlignment = VerticalAlignment.Center;
            }
        }
    }

    private static ControlTemplate CreateTemplate(bool vertical)
    {
        var size = vertical ? "Width=\"1\" VerticalAlignment=\"Stretch\" HorizontalAlignment=\"Center\""
            : "Height=\"1\" HorizontalAlignment=\"Stretch\" VerticalAlignment=\"Center\"";
        var cursor = vertical ? "SizeWE" : "SizeNS";
        var thickness = vertical ? "Width" : "Height";
        var xaml = $$"""
            <ControlTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                             TargetType="GridSplitter">
              <Border Background="{TemplateBinding Background}" Cursor="{{cursor}}">
                <Rectangle x:Name="Line" {{size}} Fill="{DynamicResource DividerStrokeColorDefaultBrush}" />
              </Border>
              <ControlTemplate.Triggers>
                <Trigger Property="IsMouseOver" Value="True">
                  <Setter TargetName="Line" Property="Fill" Value="{DynamicResource AccentFillColorDefaultBrush}" />
                  <Setter TargetName="Line" Property="{{thickness}}" Value="2" />
                </Trigger>
                <Trigger Property="IsDragging" Value="True">
                  <Setter TargetName="Line" Property="Fill" Value="{DynamicResource AccentFillColorDefaultBrush}" />
                  <Setter TargetName="Line" Property="{{thickness}}" Value="2" />
                </Trigger>
              </ControlTemplate.Triggers>
            </ControlTemplate>
            """;
        return (ControlTemplate)XamlReader.Parse(xaml);
    }
}

/// <summary>
/// Small behaviours of the Group Policy views (K6, 20.3): the search box of an <see cref="ISearchableDocument"/> (Ctrl+F
/// focuses it, Esc clears it), lists that keep their selection in view, rows brought into view when "Edytuj to ustawienie"
/// points at them, and Enter in a list that moves the keyboard to the selected item's editor.
/// </summary>
public static class GroupPolicyUi
{
    /// <summary>The text box is the document's search box: Ctrl+F focuses it (selecting its text), Esc clears it.</summary>
    public static readonly DependencyProperty IsDocumentSearchProperty = DependencyProperty.RegisterAttached(
        "IsDocumentSearch", typeof(bool), typeof(GroupPolicyUi), new PropertyMetadata(false, OnIsDocumentSearchChanged));

    /// <summary>The list scrolls its selected item into view (also a selection made by the view model, and when it is shown).</summary>
    public static readonly DependencyProperty KeepsSelectionInViewProperty = DependencyProperty.RegisterAttached(
        "KeepsSelectionInView", typeof(bool), typeof(GroupPolicyUi), new PropertyMetadata(false, OnKeepsSelectionInViewChanged));

    /// <summary>The element is brought into view when the bound value becomes true.</summary>
    public static readonly DependencyProperty BringIntoViewWhenProperty = DependencyProperty.RegisterAttached(
        "BringIntoViewWhen", typeof(bool), typeof(GroupPolicyUi), new PropertyMetadata(false, OnBringIntoViewWhenChanged));

    /// <summary>Enter in the list moves the keyboard to the first control of this element (the selected item's editor).</summary>
    public static readonly DependencyProperty EnterFocusesProperty = DependencyProperty.RegisterAttached(
        "EnterFocuses", typeof(UIElement), typeof(GroupPolicyUi), new PropertyMetadata(null, OnEnterFocusesChanged));

    private static readonly DependencyProperty SearchSubscriptionProperty = DependencyProperty.RegisterAttached(
        "SearchSubscription", typeof(ISearchableDocument), typeof(GroupPolicyUi), new PropertyMetadata(null));

    /// <summary>The Ctrl+F handler of each search box (the same delegate subscribes and unsubscribes).</summary>
    private static readonly ConditionalWeakTable<TextBox, EventHandler> Handlers = new();

    public static bool GetIsDocumentSearch(DependencyObject element) => (bool)element.GetValue(IsDocumentSearchProperty);

    public static void SetIsDocumentSearch(DependencyObject element, bool value) => element.SetValue(IsDocumentSearchProperty, value);

    public static bool GetKeepsSelectionInView(DependencyObject element) => (bool)element.GetValue(KeepsSelectionInViewProperty);

    public static void SetKeepsSelectionInView(DependencyObject element, bool value) => element.SetValue(KeepsSelectionInViewProperty, value);

    public static bool GetBringIntoViewWhen(DependencyObject element) => (bool)element.GetValue(BringIntoViewWhenProperty);

    public static void SetBringIntoViewWhen(DependencyObject element, bool value) => element.SetValue(BringIntoViewWhenProperty, value);

    public static UIElement? GetEnterFocuses(DependencyObject element) => (UIElement?)element.GetValue(EnterFocusesProperty);

    public static void SetEnterFocuses(DependencyObject element, UIElement? value) => element.SetValue(EnterFocusesProperty, value);

    /// <summary>Shows or hides a column of a grid view (kept at <paramref name="index"/> when shown).</summary>
    public static void ShowColumn(GridView view, GridViewColumn column, bool show, int index)
    {
        ArgumentNullException.ThrowIfNull(view);
        ArgumentNullException.ThrowIfNull(column);
        var shown = view.Columns.Contains(column);
        if (show && !shown)
        {
            view.Columns.Insert(Math.Clamp(index, 0, view.Columns.Count), column);
        }
        else if (!show && shown)
        {
            view.Columns.Remove(column);
        }
    }

    // ---- Search box --------------------------------------------------------------------------------------------------

    private static void OnIsDocumentSearchChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TextBox box)
        {
            return;
        }

        box.Loaded -= OnSearchLoaded;
        box.Unloaded -= OnSearchUnloaded;
        box.DataContextChanged -= OnSearchDataContextChanged;
        box.PreviewKeyDown -= OnSearchKeyDown;
        Subscribe(box, null);
        if ((bool)e.NewValue)
        {
            box.Loaded += OnSearchLoaded;
            box.Unloaded += OnSearchUnloaded;
            box.DataContextChanged += OnSearchDataContextChanged;
            box.PreviewKeyDown += OnSearchKeyDown;
            if (box.IsLoaded)
            {
                Subscribe(box, box.DataContext as ISearchableDocument);
            }
        }
    }

    private static void OnSearchLoaded(object sender, RoutedEventArgs e)
    {
        var box = (TextBox)sender;
        Subscribe(box, box.DataContext as ISearchableDocument);
    }

    private static void OnSearchUnloaded(object sender, RoutedEventArgs e) => Subscribe((TextBox)sender, null);

    private static void OnSearchDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        var box = (TextBox)sender;
        if (box.IsLoaded)
        {
            Subscribe(box, e.NewValue as ISearchableDocument);
        }
    }

    /// <summary>One subscription per box: the document's Ctrl+F request focuses it (the handler holds the box only while loaded).</summary>
    private static void Subscribe(TextBox box, ISearchableDocument? document)
    {
        if (box.GetValue(SearchSubscriptionProperty) is ISearchableDocument old)
        {
            old.SearchFocusRequested -= Handler(box);
        }

        box.SetValue(SearchSubscriptionProperty, document);
        if (document is not null)
        {
            document.SearchFocusRequested += Handler(box);
        }
    }

    private static EventHandler Handler(TextBox box) => Handlers.GetValue(box, b => (_, _) => FocusLater(b));

    private static void FocusLater(TextBox box) =>
        box.Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
        {
            if (box.IsVisible && box.IsEnabled)
            {
                box.Focus();
                Keyboard.Focus(box);
                box.SelectAll();
            }
        });

    /// <summary>Esc clears a search box that holds text (an empty box lets Esc go on).</summary>
    private static void OnSearchKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape || sender is not TextBox { Text.Length: > 0 } box)
        {
            return;
        }

        if (box.DataContext is ISearchableDocument document)
        {
            document.SearchText = string.Empty;
        }

        box.Clear();
        box.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
        e.Handled = true;
    }

    // ---- Lists -------------------------------------------------------------------------------------------------------

    private static void OnKeepsSelectionInViewChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not ListBox list)
        {
            return;
        }

        list.SelectionChanged -= OnListSelectionChanged;
        list.IsVisibleChanged -= OnListVisibleChanged;
        if ((bool)e.NewValue)
        {
            list.SelectionChanged += OnListSelectionChanged;
            list.IsVisibleChanged += OnListVisibleChanged;
        }
    }

    private static void OnListSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ReferenceEquals(e.OriginalSource, sender))
        {
            ScrollLater((ListBox)sender);
        }
    }

    private static void OnListVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is true)
        {
            ScrollLater((ListBox)sender);
        }
    }

    private static void ScrollLater(ListBox list) =>
        list.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            if (list.SelectedItem is { } item && list.IsVisible)
            {
                list.ScrollIntoView(item);
            }
        });

    private static void OnBringIntoViewWhenChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is FrameworkElement element && e.NewValue is true)
        {
            element.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
            {
                if (element.IsVisible)
                {
                    element.BringIntoView();
                }
            });
        }
    }

    private static void OnEnterFocusesChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not UIElement list)
        {
            return;
        }

        list.KeyDown -= OnListKeyDown;
        if (e.NewValue is not null)
        {
            list.KeyDown += OnListKeyDown;
        }
    }

    /// <summary>Enter on a list item: the keyboard moves to the first control of the editor beside the list.</summary>
    private static void OnListKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || Keyboard.Modifiers != ModifierKeys.None || sender is not DependencyObject list || GetEnterFocuses(list) is not { } target)
        {
            return;
        }

        e.Handled = target.MoveFocus(new TraversalRequest(FocusNavigationDirection.First));
    }
}
