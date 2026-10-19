using System.ComponentModel;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Input;
using System.Windows.Threading;
using AdMng.App.Infrastructure;
using AdMng.App.Services;
using AdMng.Presentation.Dialogs;

namespace AdMng.App.Views;

/// <summary>
/// The Logon Hours dialog (modal). Mouse: pressing on a cell starts a selection there (Shift extends it from the
/// anchor), moving with the button pressed extends it - the grid captures the mouse, so dragging past its edge selects
/// up to the edge. Keyboard on the grid: arrows move the focused cell (Shift extends), Home/End go to the first/last
/// hour of the day, Space switches the selection, Ctrl+A selects the week. The keyboard focus stays on the grid (the
/// headers are not focusable and hand the focus back to it), so a screen reader hears the selection line (a live
/// region) after every move.
/// </summary>
public partial class LogonHoursWindow : Window, IDialogResultWindow
{
    private bool _dragging;

    public LogonHoursWindow(LogonHoursViewModel viewModel)
    {
        ViewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        InitializeComponent();
        UiLanguage.ApplyTo(this);
        DataContext = viewModel;
        Loaded += (_, _) => HourGrid.Focus();
        viewModel.PropertyChanged += OnViewModelPropertyChanged;
        Closed += (_, _) => viewModel.PropertyChanged -= OnViewModelPropertyChanged;
    }

    public LogonHoursViewModel ViewModel { get; }

    public bool? Result { get; private set; }

    /// <summary>The cell under <paramref name="position"/> (relative to the grid), clamped to the grid.</summary>
    internal LogonHoursCellViewModel? CellAt(Point position)
    {
        var rows = ViewModel.Days.Count;
        if (rows == 0 || HourGrid.ActualWidth <= 0 || HourGrid.ActualHeight <= 0)
        {
            return null;
        }

        var row = Math.Clamp((int)(position.Y / (HourGrid.ActualHeight / rows)), 0, rows - 1);
        var hour = Math.Clamp((int)(position.X / (HourGrid.ActualWidth / 24)), 0, 23);
        return ViewModel.Days[row].Cells[hour];
    }

    /// <summary>Announces the new selection (after the binding updated the text).</summary>
    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(LogonHoursViewModel.SelectionText) && AutomationPeer.ListenerExists(AutomationEvents.LiveRegionChanged))
        {
            Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
                UIElementAutomationPeer.CreatePeerForElement(SelectionLine)?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged));
        }
    }

    /// <summary>A day, hour or corner header was clicked: the keyboard continues on the grid from the new selection.</summary>
    private void OnHeaderClick(object sender, RoutedEventArgs e) => HourGrid.Focus();

    private void OnOkClick(object sender, RoutedEventArgs e)
    {
        Result = true;
        Close();
    }

    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        Result = false;
        Close();
    }

    private void OnGridMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        HourGrid.Focus();
        if (CellAt(e.GetPosition(HourGrid)) is not { } cell)
        {
            return;
        }

        ViewModel.BeginSelection(cell, Keyboard.Modifiers.HasFlag(ModifierKeys.Shift));
        _dragging = HourGrid.CaptureMouse();
        e.Handled = true;
    }

    private void OnGridMouseMove(object sender, MouseEventArgs e)
    {
        if (_dragging && e.LeftButton == MouseButtonState.Pressed && CellAt(e.GetPosition(HourGrid)) is { } cell && !ReferenceEquals(cell, ViewModel.FocusedCell))
        {
            ViewModel.ExtendSelection(cell);
        }
    }

    private void OnGridMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_dragging)
        {
            _dragging = false;
            HourGrid.ReleaseMouseCapture();
            e.Handled = true;
        }
    }

    private void OnGridLostMouseCapture(object sender, MouseEventArgs e) => _dragging = false;

    private void OnGridKeyDown(object sender, KeyEventArgs e)
    {
        var shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
        switch (e.Key)
        {
            case Key.Left:
                ViewModel.MoveFocus(rows: 0, hours: -1, shift);
                break;
            case Key.Right:
                ViewModel.MoveFocus(rows: 0, hours: 1, shift);
                break;
            case Key.Up:
                ViewModel.MoveFocus(rows: -1, hours: 0, shift);
                break;
            case Key.Down:
                ViewModel.MoveFocus(rows: 1, hours: 0, shift);
                break;
            case Key.Home:
                ViewModel.MoveFocusToEdge(end: false, shift);
                break;
            case Key.End:
                ViewModel.MoveFocusToEdge(end: true, shift);
                break;
            case Key.Space:
                if (ViewModel.ToggleCommand.CanExecute(null))
                {
                    ViewModel.ToggleCommand.Execute(null);
                }

                break;
            case Key.A when Keyboard.Modifiers.HasFlag(ModifierKeys.Control):
                ViewModel.SelectAllCommand.Execute(null);
                break;
            default:
                return;
        }

        e.Handled = true;
    }
}
