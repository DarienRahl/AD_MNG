using System.Collections.Specialized;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using AdMng.App.Infrastructure;
using AdMng.App.Services;
using AdMng.Presentation.PowerShell;

namespace AdMng.App.PowerShell;

/// <summary>
/// The PowerShell window of a connection (<see cref="PowerShellWindowViewModel"/>; track R, stage R1): the scrollback, the line editor
/// and the buttons. It owns one thing the view model cannot: the width of the output in characters (the engine formats tables for it)
/// and the follow of the end of the scrollback while the operator has not scrolled up. The window is closed with its connection
/// window; the runspace is disposed with the view model.
/// </summary>
public partial class PowerShellWindow : Window, IDialogResultWindow
{
    private readonly WindowViewModelLifecycle _lifecycle;
    private bool _following = true;
    private double _characterWidth;

    public PowerShellWindow(PowerShellWindowViewModel viewModel)
    {
        ViewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        InitializeComponent();
        UiLanguage.ApplyTo(this);
        DataContext = viewModel;
        _lifecycle = new WindowViewModelLifecycle(this, viewModel);
        ((INotifyCollectionChanged)viewModel.Output.Lines).CollectionChanged += OnLinesChanged;
        OutputList.AddHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler(OnOutputScrolled));
        OutputList.SizeChanged += (_, _) => TellTheWidth();
        PreviewKeyDown += OnPreviewKeyDown;
        viewModel.PropertyChanged += (_, e) =>
        {
            // A question of the command was asked, or answered: the keyboard goes to the editor either way.
            if (e.PropertyName is nameof(PowerShellWindowViewModel.PendingPrompt) or nameof(PowerShellWindowViewModel.IsReady))
            {
                Dispatcher.InvokeAsync(Editor.FocusInput, System.Windows.Threading.DispatcherPriority.Input);
            }
        };
        Loaded += (_, _) =>
        {
            TellTheWidth();
            Editor.FocusInput();
        };
        Closed += (_, _) => ((INotifyCollectionChanged)viewModel.Output.Lines).CollectionChanged -= OnLinesChanged;
    }

    public PowerShellWindowViewModel ViewModel { get; }

    public bool? Result => _lifecycle.Result;

    /// <summary>Completes when the view model finished starting the engine (it never throws).</summary>
    public Task Initialization => _lifecycle.Initialization;

    internal PowerShellEditor EditorControl => Editor;

    internal ListBox Output => OutputList;

    // ---- The scrollback ----------------------------------------------------------------------------------------------

    /// <summary>The columns the output has: the engine formats tables for this width (the glyph column, the padding and a scroll bar are not text).</summary>
    private void TellTheWidth()
    {
        if (_characterWidth <= 0)
        {
            var sample = new FormattedText(
                "0000000000",
                CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight,
                new Typeface((FontFamily)FindResource("Ad.Font.Mono"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal),
                (double)FindResource("Ad.FontSize.Body"),
                Brushes.Black,
                VisualTreeHelper.GetDpi(this).PixelsPerDip);
            _characterWidth = sample.WidthIncludingTrailingWhitespace / 10;
        }

        if (_characterWidth > 0 && OutputList.ActualWidth > 0)
        {
            var text = OutputList.ActualWidth - 16 - 20 - 20; // the padding of a line, the glyph column, the scroll bar
            ViewModel.SetOutputWidth((int)Math.Floor(text / _characterWidth));
        }
    }

    private void OnLinesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (_following && OutputList.Items.Count > 0)
        {
            // After the layout: the new lines have a place by then.
            Dispatcher.InvokeAsync(() => OutputList.ScrollIntoView(OutputList.Items[^1]), System.Windows.Threading.DispatcherPriority.Background);
        }
    }

    private void OnOutputScrolled(object sender, ScrollChangedEventArgs e)
    {
        // The operator at the end of the list follows it; one who scrolled up is left where he is.
        if (e.ExtentHeightChange == 0 && e.ViewportHeightChange == 0)
        {
            _following = e.VerticalOffset + e.ViewportHeight >= e.ExtentHeight - 0.5;
        }
    }

    // ---- Keys and the menu of the output ----------------------------------------------------------------------------------

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (!OutputList.IsKeyboardFocusWithin)
        {
            return;
        }

        var control = Keyboard.Modifiers.HasFlag(ModifierKeys.Control) && !Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
        if (e.Key == Key.C && control)
        {
            // Ctrl+C copies the selected lines; with nothing selected it stops the command, as at a console.
            if (OutputList.SelectedItems.Count > 0)
            {
                CopySelectedLines();
            }
            else
            {
                ViewModel.StopCommand.Execute(null);
            }

            e.Handled = true;
        }
        else if (e.Key is Key.Enter or Key.Return && Keyboard.Modifiers == ModifierKeys.None)
        {
            // Enter on a line with a distinguished name opens the first of them (a link is no tab stop).
            if (OutputList.SelectedItem is PowerShellOutputLine line && line.Runs.FirstOrDefault(run => run.IsLink).DistinguishedName is { } name)
            {
                ViewModel.OpenObjectCommand.Execute(name);
                e.Handled = true;
            }
        }
        else if (e.Key is Key.Escape)
        {
            Editor.FocusInput();
            e.Handled = true;
        }
    }

    private void CopySelectedLines() =>
        ViewModel.CopyLines(OutputList.SelectedItems.OfType<PowerShellOutputLine>().OrderBy(line => OutputList.Items.IndexOf(line)));

    private void OnCopyLinesClick(object sender, RoutedEventArgs e) => CopySelectedLines();

    private void OnSelectAllClick(object sender, RoutedEventArgs e) => OutputList.SelectAll();
}
