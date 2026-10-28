using System.Windows;
using System.Windows.Controls;
using AdMng.App.Infrastructure;
using AdMng.App.Services;
using AdMng.Presentation.Correspondence;

namespace AdMng.App.Correspondence;

/// <summary>
/// A text prompt of Korespondencja (<see cref="TextPromptViewModel"/>, modal). The result is the view model's text; a
/// multiline prompt has a tall box with line breaks and wrapping.
/// </summary>
public partial class TextPromptWindow : Window, IDialogResultWindow
{
    /// <summary>The height of the box of a multiline prompt.</summary>
    internal const double MultilineHeight = 220;

    private readonly WindowViewModelLifecycle _lifecycle;

    public TextPromptWindow(TextPromptViewModel viewModel)
    {
        ViewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        InitializeComponent();
        UiLanguage.ApplyTo(this);
        DataContext = viewModel;
        if (viewModel.IsMultiline)
        {
            PromptTextBox.AcceptsReturn = true;
            PromptTextBox.TextWrapping = TextWrapping.Wrap;
            PromptTextBox.Height = MultilineHeight;
            PromptTextBox.VerticalContentAlignment = VerticalAlignment.Top;
            PromptTextBox.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        }

        _lifecycle = new WindowViewModelLifecycle(this, viewModel);
        Loaded += (_, _) =>
        {
            PromptTextBox.Focus();
            if (viewModel.IsMultiline)
            {
                PromptTextBox.CaretIndex = PromptTextBox.Text.Length;
            }
            else
            {
                PromptTextBox.SelectAll();
            }
        };
    }

    public TextPromptViewModel ViewModel { get; }

    public bool? Result => _lifecycle.Result;

    /// <summary>Completes when the view model was initialized (tests).</summary>
    public Task Initialization => _lifecycle.Initialization;
}
