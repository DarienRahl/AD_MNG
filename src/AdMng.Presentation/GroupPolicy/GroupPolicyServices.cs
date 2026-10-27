using AdMng.Core.Batch;
using AdMng.Core.Directory;
using AdMng.Core.GroupPolicy;
using AdMng.Core.GroupPolicy.Management;
using AdMng.Presentation.BatchEditing;
using AdMng.Presentation.Changes;
using AdMng.Presentation.Dialogs;
using AdMng.Presentation.Errors;
using AdMng.Presentation.Localization;
using AdMng.Presentation.Services;
using AdMng.Presentation.Session;
using AdMng.Presentation.Settings;
using AdMng.Presentation.Watchdog;

namespace AdMng.Presentation.GroupPolicy;

/// <summary>Whether SYSVOL of the session's DC can be written (an open connection) or why not.</summary>
/// <param name="Connection">Open connection (the caller disposes it); null when SYSVOL is unavailable.</param>
/// <param name="Reason">Why SYSVOL is unavailable (null when it is open).</param>
public sealed record SysvolAccess(ISysvolWriteConnection? Connection, SysvolUnavailableReason? Reason);

/// <summary>Which Group Policy document the shell should open (or select when it is open).</summary>
public enum GroupPolicyDocumentKind
{
    /// <summary>"Obiekty zasad grupy": every GPO of the domain.</summary>
    Objects,

    /// <summary>One GPO (<see cref="GroupPolicyDocumentRequest.GpoId"/>).</summary>
    Object,

    /// <summary>"Zasady grupy" of a domain, OU or site (<see cref="GroupPolicyDocumentRequest.ContainerDn"/>).</summary>
    Scope,

    /// <summary>"Filtry WMI".</summary>
    WmiFilters,
}

/// <summary>A request to open a Group Policy document in the connection window's shell.</summary>
/// <param name="Kind">The document.</param>
public sealed record GroupPolicyDocumentRequest(GroupPolicyDocumentKind Kind)
{
    /// <summary>The GPO of <see cref="GroupPolicyDocumentKind.Object"/>.</summary>
    public Guid? GpoId { get; init; }

    /// <summary>The domain head, OU or site of <see cref="GroupPolicyDocumentKind.Scope"/>.</summary>
    public string? ContainerDn { get; init; }

    /// <summary>The name shown until the document has loaded (GPO name, container name).</summary>
    public string? Name { get; init; }

    /// <summary>The tab of a GPO document to select ("Stan GPO" opens the Status tab).</summary>
    public GpoDocumentTab? Tab { get; init; }
}

/// <summary>
/// Services of the Group Policy documents and windows of one connection window (scoped, design note
/// docs/dev/k-computers-and-gpo.md 7): the window's UI services and change pipeline (<see cref="DialogServices"/>), the
/// pure planner and reader, the SYSVOL writer of the session's DC (rule 4), the Group Policy windows and the Watchdog
/// history launcher. <see cref="Changed"/> tells the open documents and the tree that a GPO operation was executed;
/// <see cref="DocumentRequested"/> asks the window's shell to open a document.
/// </summary>
public sealed class GroupPolicyServices(
    DialogServices ui,
    BatchEngines engines,
    GroupPolicyPlanner planner,
    IGroupPolicyDialogs dialogs,
    ISysvolFileWriter? writer = null,
    WatchdogObjectHistoryLauncher? history = null)
{
    /// <summary>Session, dialogs, files, clipboard, notifications and the change pipeline of the connection window.</summary>
    public DialogServices Ui { get; } = ui ?? throw new ArgumentNullException(nameof(ui));

    public BatchEngines Engines { get; } = engines ?? throw new ArgumentNullException(nameof(engines));

    public GroupPolicyPlanner Planner { get; } = planner ?? throw new ArgumentNullException(nameof(planner));

    public GroupPolicyReader Reader => Planner.Reader;

    public IGroupPolicyDialogs Dialogs { get; } = dialogs ?? throw new ArgumentNullException(nameof(dialogs));

    /// <summary>The SYSVOL writer (null in hosts without one: every SYSVOL write is then disabled as not supported).</summary>
    public ISysvolFileWriter? Writer { get; } = writer;

    /// <summary>"Historia w Watchdogu" (null when the host has no Watchdog).</summary>
    public WatchdogObjectHistoryLauncher? History { get; } = history;

    public ILocalizer Localizer => Ui.Localizer;

    public IUiDispatcher Dispatcher => Ui.Dispatcher;

    public ISessionManager Sessions => Ui.Sessions;

    public ISettingsStore Settings => Ui.Settings;

    public ErrorTranslator Errors => Ui.Errors;

    public TimeProvider Time => Ui.Time;

    /// <summary>The connection window's lifetime: reads stop when the window closes.</summary>
    public CancellationToken Lifetime => Ui.Connection?.Lifetime ?? CancellationToken.None;

    /// <summary>A GPO operation was executed in this connection window (documents and tree nodes refresh).</summary>
    public event EventHandler? Changed;

    /// <summary>A document should be opened (or selected) in the shell of this connection window.</summary>
    public event EventHandler<GroupPolicyDocumentRequest>? DocumentRequested;

    /// <summary>Raises <see cref="Changed"/>.</summary>
    public void NotifyChanged() => Changed?.Invoke(this, EventArgs.Empty);

    /// <summary>Asks the shell to open a document (<see cref="DocumentRequested"/>). False when no shell listens.</summary>
    public bool OpenDocument(GroupPolicyDocumentRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (DocumentRequested is not { } handler)
        {
            return false;
        }

        handler(this, request);
        return true;
    }

    /// <summary>Opens SYSVOL of the session's DC for writing; never throws for an unavailable share (the reason is returned).</summary>
    public async Task<SysvolAccess> OpenSysvolAsync(IDirectorySession session, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (Writer is null)
        {
            return new SysvolAccess(null, SysvolUnavailableReason.NotSupported);
        }

        try
        {
            return new SysvolAccess(await Writer.OpenForWriteAsync(session, cancellationToken).ConfigureAwait(true), null);
        }
        catch (SysvolUnavailableException ex)
        {
            return new SysvolAccess(null, ex.Reason);
        }
        catch (SysvolWriteException ex)
        {
            return new SysvolAccess(null, ex.Error switch
            {
                SysvolWriteError.AccessDenied => SysvolUnavailableReason.AccessDenied,
                SysvolWriteError.LogonFailure => SysvolUnavailableReason.LogonFailure,
                SysvolWriteError.Unreachable => SysvolUnavailableReason.Unreachable,
                _ => SysvolUnavailableReason.Failed,
            });
        }
    }

    /// <summary>The warning of the documents for an unavailable SYSVOL ("SYSVOL niedostępny: …").</summary>
    public string SysvolText(SysvolUnavailableReason reason, string server) => Localizer.Format(reason switch
    {
        SysvolUnavailableReason.NotSupported => L.GroupPolicy.SysvolNotSupported,
        SysvolUnavailableReason.DistinguishedNameLogon => L.GroupPolicy.SysvolDistinguishedNameLogon,
        SysvolUnavailableReason.LogonFailure => L.GroupPolicy.SysvolLogonFailure,
        SysvolUnavailableReason.AccessDenied => L.GroupPolicy.SysvolAccessDenied,
        SysvolUnavailableReason.Unreachable => L.GroupPolicy.SysvolUnreachable,
        _ => L.GroupPolicy.SysvolFailed,
    }, server);

    /// <summary>
    /// The PDC note ("changes are made on DC X, the PDC emulator is Y – GPMC uses the PDC by default"), or null when the
    /// session's DC is the PDC emulator or the role owner cannot be read. Never switches the DC (rule 4).
    /// </summary>
    public async Task<string?> PdcNoteAsync(IDirectorySession session, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        string? pdc;
        try
        {
            pdc = await Reader.ReadPdcEmulatorAsync(session, cancellationToken).ConfigureAwait(true);
        }
        catch (DirectoryException)
        {
            return null;
        }

        return pdc is null || SameHost(pdc, session.Info.Server) ? null : Localizer.Format(L.GroupPolicy.PdcNote, session.Info.Server, pdc);
    }

    /// <summary>
    /// Shows <paramref name="plan"/> in the change plan preview (every GPO operation, design note 3.3) and raises
    /// <see cref="Changed"/> when something was written. The preview executes only in <paramref name="session"/> (the one
    /// the plan was made in). True when the directory or SYSVOL changed.
    /// </summary>
    public async Task<bool> PreviewAsync(ChangePlan plan, string title, IDirectorySession session, string? confirmationText = null, string? confirmationMessage = null)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(session);
        var notes = new List<string>();
        if (await PdcNoteAsync(session, Lifetime).ConfigureAwait(true) is { } pdc)
        {
            notes.Add(pdc);
        }

        var preview = new ChangePlanPreviewViewModel(Ui, Engines, new ChangePlanPreviewRequest(plan, title)
        {
            Notes = notes,
            ConfirmationText = confirmationText,
            ConfirmationMessage = confirmationMessage,
        });
        bool changed;
        try
        {
            changed = await Dialogs.ShowPlanPreviewAsync(preview).ConfigureAwait(true) | preview.ChangedDirectory;
        }
        finally
        {
            // The window disposes it when it closes; disposing twice is safe (and needed when no window was shown).
            preview.Dispose();
        }

        if (changed)
        {
            NotifyChanged();
        }

        return changed;
    }

    /// <summary>True when two host names name the same DC (FQDN against the short name, case ignored).</summary>
    public static bool SameHost(string a, string b)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);
        var x = a.Trim().TrimEnd('.');
        var y = b.Trim().TrimEnd('.');
        if (string.Equals(x, y, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        static string Short(string host) => host.Contains('.', StringComparison.Ordinal) && !char.IsAsciiDigit(host[^1]) ? host[..host.IndexOf('.', StringComparison.Ordinal)] : host;
        return (!x.Contains('.', StringComparison.Ordinal) || !y.Contains('.', StringComparison.Ordinal))
            && string.Equals(Short(x), Short(y), StringComparison.OrdinalIgnoreCase);
    }
}
