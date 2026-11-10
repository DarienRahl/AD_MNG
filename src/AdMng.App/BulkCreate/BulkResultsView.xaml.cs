using System.Windows.Controls;
using AdMng.App.Services;
using AdMng.Presentation.BatchEditing;

namespace AdMng.App.BulkCreate;

/// <summary>
/// The created accounts with their initial passwords in the batch editor's results (docs/dev/phase3-bulk-create.md 2.7).
/// DataContext: <see cref="BatchEditorViewModel"/> in create mode (B4). The retry's password boxes are handed to the wizard
/// as <see cref="Presentation.Services.IPasswordInput"/> (never bound) and taken back when the view goes.
/// </summary>
public partial class BulkResultsView : UserControl
{
    private Presentation.BulkCreate.BulkCreateViewModel? _wizard;

    public BulkResultsView()
    {
        InitializeComponent();
        DataContextChanged += (_, e) => Attach((e.NewValue as BatchEditorViewModel)?.BulkCreate);
        Loaded += (_, _) => Attach((DataContext as BatchEditorViewModel)?.BulkCreate);
        Unloaded += (_, _) => Attach(null);
    }

    private void Attach(Presentation.BulkCreate.BulkCreateViewModel? wizard)
    {
        if (ReferenceEquals(wizard, _wizard))
        {
            return;
        }

        if (_wizard is not null)
        {
            _wizard.RetryPasswordInput = null;
            _wizard.RetryConfirmPasswordInput = null;
        }

        _wizard = wizard;
        if (wizard is not null)
        {
            wizard.RetryPasswordInput = new PasswordBoxInput(RetryPasswordBox);
            wizard.RetryConfirmPasswordInput = new PasswordBoxInput(RetryConfirmPasswordBox);
        }
        else
        {
            RetryPasswordBox.Clear();
            RetryConfirmPasswordBox.Clear();
        }
    }
}
