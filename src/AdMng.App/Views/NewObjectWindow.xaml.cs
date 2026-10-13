using System.Windows;
using System.Windows.Controls;
using AdMng.App.Infrastructure;
using AdMng.App.Localization;
using AdMng.App.Services;
using AdMng.Core.ActiveDirectory;
using AdMng.Core.Model;
using AdMng.Presentation.Dialogs;
using AdMng.Presentation.Icons;
using AdMng.Presentation.Localization;

namespace AdMng.App.Views;

/// <summary>
/// "New Object" (modal). The object kind does not change while the window is open, so the kind-specific sections
/// are set up once here; the password boxes are handed to the view model as <see cref="PasswordBoxInput"/> adapters.
/// </summary>
public partial class NewObjectWindow : Window, IDialogResultWindow
{
    private readonly WindowViewModelHost _host;

    public NewObjectWindow(NewObjectViewModel viewModel)
    {
        ViewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        DataContext = viewModel;
        InitializeComponent();
        UiLanguage.ApplyTo(this);

        // Passwords are read from the boxes only when the object is created (never bound as strings).
        viewModel.PasswordInput = new PasswordBoxInput(PasswordBox);
        viewModel.ConfirmPasswordInput = new PasswordBoxInput(ConfirmPasswordBox);

        KindIcon.Icon = ObjectIcons.For(viewModel.Kind, disabled: false, locked: false);
        var samLabel = viewModel.Kind switch
        {
            ObjectKind.Group => L.Labels.GroupNamePre2000,
            ObjectKind.Computer => L.Labels.ComputerNamePre2000,
            _ => DisplayNames.AttributeKey(Attr.SamAccountName),
        };
        SamLabel.SetBinding(TextBlock.TextProperty, new TrBinding(samLabel));
        SamSection.Visibility = viewModel.Kind is ObjectKind.User or ObjectKind.Group or ObjectKind.Computer
            ? Visibility.Visible
            : Visibility.Collapsed;
        DescriptionSection.Visibility = viewModel.Kind is ObjectKind.Group or ObjectKind.OrganizationalUnit or ObjectKind.Computer
            ? Visibility.Visible
            : Visibility.Collapsed;

        _host = WindowViewModelHost.Attach(this, viewModel);
        Loaded += OnLoaded;
    }

    public NewObjectViewModel ViewModel { get; }

    public bool? Result => _host.Result;

    /// <summary>Completes when <see cref="NewObjectViewModel.InitializeAsync"/> finished (tests).</summary>
    internal Task Initialization => _host.Initialization;

    protected override void OnClosed(EventArgs e)
    {
        ViewModel.PasswordInput = null;
        ViewModel.ConfirmPasswordInput = null;
        PasswordBox.Clear();
        ConfirmPasswordBox.Clear();
        base.OnClosed(e);
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoaded;
        Control first = ViewModel.Kind switch
        {
            ObjectKind.User or ObjectKind.Contact => FirstNameBox,
            ObjectKind.Group => GroupNameBox,
            ObjectKind.Computer => ComputerNameBox,
            _ => OuNameBox,
        };
        first.Focus();
    }
}
