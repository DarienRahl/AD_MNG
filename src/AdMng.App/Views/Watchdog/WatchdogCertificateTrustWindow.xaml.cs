using System.Windows;
using AdMng.App.Infrastructure;
using AdMng.App.Services;
using AdMng.Presentation.Watchdog;

namespace AdMng.App.Views.Watchdog;

/// <summary>
/// "Potwierdź certyfikat usługi Watchdog" (modal, stage B design 18 item 1). <see cref="Result"/> is true only after
/// "Ufam temu certyfikatowi"; the keyboard focus starts on "Anuluj", so Enter does not trust a certificate by accident.
/// </summary>
public partial class WatchdogCertificateTrustWindow : Window, IDialogResultWindow
{
    public WatchdogCertificateTrustWindow(WatchdogCertificateTrustViewModel viewModel)
    {
        ViewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        InitializeComponent();
        UiLanguage.ApplyTo(this);
        DataContext = viewModel;
        Loaded += (_, _) => CancelButton.Focus();
    }

    public WatchdogCertificateTrustViewModel ViewModel { get; }

    public bool? Result { get; private set; }

    private void OnTrustClick(object sender, RoutedEventArgs e)
    {
        Result = true;
        Close();
    }

    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        Result = false;
        Close();
    }
}
