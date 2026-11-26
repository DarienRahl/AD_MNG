using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using AdMng.App.Services;
using AdMng.Presentation.BulkCreate;

namespace AdMng.App.BulkCreate;

/// <summary>
/// Step 3 Nazwy i opcje: profiles, rules with live samples, the Numeracja panel, account options, groups, advanced options
/// (docs/dev/phase3-bulk-create.md 2.6). DataContext: <see cref="BulkRulesViewModel"/>. The shared password boxes are handed to
/// the view model as <see cref="Presentation.Services.IPasswordInput"/> (never bound); <em>Wstaw pole</em> inserts into the
/// pattern box that had the focus last, at its caret.
/// </summary>
public partial class BulkRulesView : UserControl
{
    private BulkRulesViewModel? _viewModel;
    private TextBox? _activePatternBox;

    public BulkRulesView()
    {
        InitializeComponent();
        SharedPasswordBox.PasswordChanged += OnPasswordChanged;
        ConfirmPasswordBox.PasswordChanged += OnPasswordChanged;
        DataContextChanged += (_, _) => Attach(DataContext as BulkRulesViewModel);
        Loaded += (_, _) => Attach(DataContext as BulkRulesViewModel);
    }

    private void Attach(BulkRulesViewModel? viewModel)
    {
        if (ReferenceEquals(_viewModel, viewModel))
        {
            return;
        }

        _viewModel = viewModel;
        _activePatternBox = null;
        if (viewModel is not null)
        {
            viewModel.PasswordInput = new PasswordBoxInput(SharedPasswordBox);
            viewModel.ConfirmPasswordInput = new PasswordBoxInput(ConfirmPasswordBox);
        }
    }

    private void OnPasswordChanged(object sender, RoutedEventArgs e) => _viewModel?.NotifyPasswordChanged();

    /// <summary>The pattern box that gets the focus is where <em>Wstaw pole</em> inserts.</summary>
    private void OnPatternFocused(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (sender is TextBox { DataContext: AttributeRuleViewModel rule } box && _viewModel is { } viewModel)
        {
            _activePatternBox = box;
            viewModel.ActiveRule = rule;
            rule.CaretIndex = box.CaretIndex;
        }
    }

    private void OnPatternSelectionChanged(object sender, RoutedEventArgs e)
    {
        if (sender is TextBox { DataContext: AttributeRuleViewModel rule, IsKeyboardFocusWithin: true } box)
        {
            rule.CaretIndex = box.CaretIndex;
        }
    }

    /// <summary><em>Wstaw pole</em> opens its menu of fields, tokens and functions.</summary>
    private void OnInsertFieldClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { ContextMenu: { } menu } button)
        {
            menu.PlacementTarget = button;
            menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
            menu.IsOpen = true;
        }
    }

    /// <summary>After an insertion the pattern box gets the focus back with the caret after the inserted text.</summary>
    private void OnInsertFieldMenuClick(object sender, RoutedEventArgs e)
    {
        var box = _activePatternBox;
        if (box is null)
        {
            return;
        }

        Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
        {
            if (box.DataContext is AttributeRuleViewModel rule && box.IsLoaded)
            {
                box.Focus();
                box.CaretIndex = Math.Clamp(rule.CaretIndex, 0, box.Text.Length);
            }
        });
    }
}
