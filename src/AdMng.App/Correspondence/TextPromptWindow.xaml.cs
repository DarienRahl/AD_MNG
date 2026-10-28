using System.Windows;
using AdMng.App.Infrastructure;
using AdMng.App.Services;
using AdMng.Presentation.Correspondence;

namespace AdMng.App.Correspondence;

/// <summary>A text prompt of Korespondencja (<see cref="TextPromptViewModel"/>, modal). The result is the view model's text.</summary>
public partial class TextPromptWindow : Window, IDialogResultWindow
{
    private readonly WindowViewModelLifecycle _lifecycle;

    public TextPromptWindow(TextPromptViewModel viewModel)
    {
        ViewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        InitializeComponent();
        UiLanguage.ApplyTo(this);
        DataContext = viewModel;
        _lifecycle = new WindowViewModelLifecycle(this, viewModel);
    }

    public TextPromptViewModel ViewModel { get; }

    public bool? Result => _lifecycle.Result;
}
