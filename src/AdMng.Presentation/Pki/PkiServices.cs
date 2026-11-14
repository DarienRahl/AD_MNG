using AdMng.Core.Csv;
using AdMng.Core.Pki.Tls;
using AdMng.Presentation.Computers;
using AdMng.Presentation.Dialogs;
using AdMng.Presentation.Errors;
using AdMng.Presentation.Localization;
using AdMng.Presentation.Services;
using AdMng.Presentation.Session;
using AdMng.Presentation.Settings;

namespace AdMng.Presentation.Pki;

/// <summary>
/// Services of the PKI documents, node and property tabs of one connection window (scoped, docs/dev/p-certificate-services.md
/// 2.5): the window's UI services, the CSV codec of the exports, the window's snapshot service and the optional probes (null:
/// the LDAPS tab and the HTTP button say <i>niedostępne</i>). <see cref="DocumentRequested"/> asks the window's shell to open
/// a document; the CA-host navigation opens the CA's computer object by the DN the reader resolved (no lookup here).
/// </summary>
public sealed class PkiServices(
    DialogServices ui,
    ICsvCodec csv,
    IPkiSnapshotService snapshot,
    IDomainControllerTlsProbe? tlsProbe = null,
    IPkiHealthProbe? healthProbe = null)
{
    /// <summary>Session, dialogs, files, clipboard, notifications and the change pipeline of the connection window.</summary>
    public DialogServices Ui { get; } = ui ?? throw new ArgumentNullException(nameof(ui));

    /// <summary>The CSV codec of the exports (operator-chosen files only, <c>protectFormulas: true</c>).</summary>
    public ICsvCodec Csv { get; } = csv ?? throw new ArgumentNullException(nameof(csv));

    /// <summary>The window's PKI cache.</summary>
    public IPkiSnapshotService Snapshot { get; } = snapshot ?? throw new ArgumentNullException(nameof(snapshot));

    /// <summary>The DC LDAPS probe (null in hosts without one).</summary>
    public IDomainControllerTlsProbe? TlsProbe { get; } = tlsProbe;

    /// <summary>The HTTP check of AIA / CDP / OCSP URLs (null in hosts without one).</summary>
    public IPkiHealthProbe? HealthProbe { get; } = healthProbe;

    public ILocalizer Localizer => Ui.Localizer;

    public IUiDispatcher Dispatcher => Ui.Dispatcher;

    public ISessionManager Sessions => Ui.Sessions;

    public ISettingsStore Settings => Ui.Settings;

    public ErrorTranslator Errors => Ui.Errors;

    public TimeProvider Time => Ui.Time;

    /// <summary>The connection window's lifetime: reads stop when the window closes.</summary>
    public CancellationToken Lifetime => Ui.Connection?.Lifetime ?? CancellationToken.None;

    /// <summary>The CA host's remote actions (K1) can be opened in this window.</summary>
    public bool CanOpenRemoteActions => Ui.Dialogs is IComputerDialogs;

    /// <summary>A document should be opened (or selected) in the shell of this connection window.</summary>
    public event EventHandler<PkiDocumentRequest>? DocumentRequested;

    /// <summary>Asks the shell to open a document (<see cref="DocumentRequested"/>). False when no shell listens.</summary>
    public bool RequestDocument(PkiDocumentRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (DocumentRequested is not { } handler)
        {
            return false;
        }

        handler(this, request);
        return true;
    }

    /// <summary><i>Otwórz komputer</i>: the property sheet of the CA host's computer object (<c>PkiEnrollmentService.ComputerDn</c>).</summary>
    public Task OpenComputerAsync(string computerDn)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(computerDn);
        return Ui.Dialogs.ShowPropertiesAsync(computerDn);
    }

    /// <summary><i>Zdalne akcje</i> of the CA host (K1); nothing when the window has no computer dialogs (<see cref="CanOpenRemoteActions"/>).</summary>
    public Task OpenRemoteActionsAsync(string computerDn)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(computerDn);
        return Ui.Dialogs is IComputerDialogs computers ? computers.ShowRemoteActionsAsync(computerDn) : Task.CompletedTask;
    }
}
