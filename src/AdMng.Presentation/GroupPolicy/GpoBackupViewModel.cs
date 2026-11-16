using System.Globalization;
using System.Text;
using AdMng.Core.Batch;
using AdMng.Core.GroupPolicy;
using AdMng.Core.GroupPolicy.Backup;
using AdMng.Core.GroupPolicy.Management;
using AdMng.Core.Model;
using AdMng.Presentation.Dialogs;
using AdMng.Presentation.Feedback;
using AdMng.Presentation.Infrastructure;
using AdMng.Presentation.Localization;
using AdMng.Presentation.Services;
using AdMng.Presentation.Shell;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AdMng.Presentation.GroupPolicy;

/// <summary>What <see cref="GpoBackupViewModel"/> does.</summary>
public enum GpoBackupMode
{
    /// <summary>"Kopia zapasowa…" of the given GPOs.</summary>
    Backup,

    /// <summary>"Usuń…": the backup offered before the delete ("Utwórz kopię zapasową przed usunięciem").</summary>
    BeforeDelete,

    /// <summary>"Przywróć z kopii zapasowej…".</summary>
    Restore,

    /// <summary>"Importuj ustawienia…" into the given GPO.</summary>
    Import,
}

/// <summary>A backup of the chosen folder.</summary>
public sealed class GpoBackupRowViewModel(GpoBackupEntry entry, string timeText, bool otherDomain)
{
    public GpoBackupEntry Entry { get; } = entry;

    public string Name => Entry.DisplayName;

    public string Domain => Entry.Domain;

    public string TimeText { get; } = timeText;

    public string Comment => Entry.Comment;

    /// <summary>The backup comes from another domain (restore is refused; import is possible).</summary>
    public bool IsOtherDomain { get; } = otherDomain;

    public string GuidText => GroupPolicyText.Guid(Entry.GpoId);
}

/// <summary>
/// Backup, restore and import of GPOs (design note docs/dev/k-computers-and-gpo.md 5.5, <c>GpoBackupWindow</c>). Backups
/// are written in GPMC's layout plus the <c>admng.json</c> sidecar ONLY into the folder the operator picks (CLAUDE.md rule 1:
/// an explicit export; the folder path may be remembered, never content); the backup runs as an activity of the activity
/// center and is read-only for the directory. Restore and import read a backup of the folder (untrusted input, checked by
/// <see cref="GpmcBackupFormat"/>), plan on the session's DC and show the change plan preview.
/// </summary>
/// <remarks>
/// <para>The window has one status line (<see cref="Status"/>; track V, stage V2b-3, design note
/// docs/dev/v-interface-renewal.md "V2b-3 as built"). Its operations, each with Stop: reading the backups of the folder
/// (restore, import), the backups themselves - "Backing up the GPOs: 1 of 3…" with the part done -, and making the plan of a
/// restore or an import until the change plan preview opens (that window says its own status). A backup that ended says
/// nothing - the window closes, the toast of the main window says it -; one that was stopped says how many GPOs were
/// written, one that failed says why. A folder that could not be read is the state of the window until it is read again
/// (Retry).</para>
/// <para>The read of the selected backup - its files, checked, for the details and the plan - is the operation of the list
/// and has a holder of its own (<see cref="DetailsStatus"/>), in front of the window's: it starts with every selection, also
/// while the window plans. A backup that could not be read is the state of that selection, with Retry. That a backup
/// comes from another domain is a note under the list, true for as long as it is selected.</para>
/// </remarks>
public sealed partial class GpoBackupViewModel : WindowViewModelBase, IHasPageStatus
{
    private readonly GroupPolicyServices _gp;
    private readonly SerialCancellation _details = new();
    private readonly SerialCancellation _work = new();
    private readonly SerialCancellation _listing = new();
    private IReadOnlyList<GpoBackupEntry> _entries = [];
    private GpoBackup? _selectedBackup;

    /// <summary>The folder was read at least once: the line has the count of backups as the summary.</summary>
    private bool _listed;

    /// <summary>Why the list is empty after a read of the folder that failed (null: nothing wrong).</summary>
    private PageStatus? _listState;

    public GpoBackupViewModel(GroupPolicyServices services, GpoBackupMode mode, IReadOnlyList<GpoInfo> gpos)
        : base((services ?? throw new ArgumentNullException(nameof(services))).Ui)
    {
        _gp = services;
        Mode = mode;
        Gpos = gpos ?? [];
        var settings = services.Settings.Current.GroupPolicy;
        Folder = settings.LastBackupFolder ?? string.Empty;
        Comment = settings.BackupCommentTemplate is { } template ? SafeFormat(template, services.Time.GetLocalNow()) : string.Empty;
        BackupFirst = settings.BackupBeforeDelete;
        Status = new PageStatusHolder(Localizer, Services.Dispatcher, Services.Lists.StatusLog);
        DetailsStatus = new PageStatusHolder(Localizer, Services.Dispatcher, Services.Lists.StatusLog);
        if (ListsBackups)
        {
            // The line says what the read of the selected backup says whenever the window itself has nothing more important.
            Status.Front = DetailsStatus;
        }

        UpdateLasting();
    }

    /// <summary>
    /// The status of the window - what its one status line shows: the running operation (with Stop), how it ended, at
    /// rest how many backups the folder holds, and that the session of the window ended.
    /// </summary>
    public PageStatusHolder Status { get; }

    /// <summary>
    /// The status of the selected backup (restore, import): its read, with Stop, and why it could not be read. In front of
    /// <see cref="Status"/> while the backups of the folder are listed.
    /// </summary>
    public PageStatusHolder DetailsStatus { get; }

    public GpoBackupMode Mode { get; }

    /// <summary>The GPOs to back up (Backup, BeforeDelete) or the import target (Import).</summary>
    public IReadOnlyList<GpoInfo> Gpos { get; }

    public override string WindowTitle => Mode switch
    {
        GpoBackupMode.BeforeDelete => Localizer.Format(L.GroupPolicy.BackupBeforeDeleteTitle, Gpos.FirstOrDefault()?.DisplayName ?? string.Empty),
        GpoBackupMode.Restore => Localizer[L.GroupPolicy.RestoreWindowTitle],
        GpoBackupMode.Import => Localizer.Format(L.GroupPolicy.ImportWindowTitle, Gpos.FirstOrDefault()?.DisplayName ?? string.Empty),
        _ => Localizer[L.GroupPolicy.BackupWindowTitle],
    };

    public bool IsBackup => Mode is GpoBackupMode.Backup or GpoBackupMode.BeforeDelete;

    public bool IsBeforeDelete => Mode == GpoBackupMode.BeforeDelete;

    public bool IsRestore => Mode == GpoBackupMode.Restore;

    public bool ListsBackups => Mode is GpoBackupMode.Restore or GpoBackupMode.Import;

    /// <summary>"Stacje robocze – Zasady haseł, Serwery – Zabezpieczenia".</summary>
    public string GposText => string.Join(", ", Gpos.Select(g => g.DisplayName));

    /// <summary>"Zgodna z GPMC – do potwierdzenia w pilotażu" (design note 5.5).</summary>
    public string CompatibilityNote => Localizer[L.GroupPolicy.BackupCompatibilityNote];

    /// <summary>The label of the main button ("Utwórz kopię", "Dalej", "Przywróć…", "Importuj…").</summary>
    public string RunText => Localizer[Mode switch
    {
        GpoBackupMode.BeforeDelete => L.GroupPolicy.BackupContinue,
        GpoBackupMode.Restore => L.GroupPolicy.RestoreRun,
        GpoBackupMode.Import => L.GroupPolicy.ImportRun,
        _ => L.GroupPolicy.BackupRun,
    }];

    /// <summary>The folder the operator chose (a local or UNC path).</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RunCommand))]
    public partial string Folder { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PowerShell))]
    public partial string Comment { get; set; }

    /// <summary>Before a delete: create the backup first (remembered as the default).</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RunCommand))]
    public partial bool BackupFirst { get; set; }

    [ObservableProperty]
    public partial IReadOnlyList<GpoBackupRowViewModel> Backups { get; private set; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RunCommand))]
    public partial GpoBackupRowViewModel? SelectedBackup { get; set; }

    /// <summary>Details of the selected backup (files, links, sidecar) once it was read.</summary>
    [ObservableProperty]
    public partial string? BackupDetails { get; private set; }

    /// <summary>Restore: also restore the links from the sidecar (off by default; a GPMC restore has none).</summary>
    [ObservableProperty]
    public partial bool RestoreLinks { get; set; }

    /// <summary>"Przywróć też połączenia (2)".</summary>
    [ObservableProperty]
    public partial string RestoreLinksText { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial bool CanRestoreLinks { get; private set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RunCommand), nameof(BrowseCommand), nameof(CloseCommand))]
    public partial bool IsBusy { get; private set; }

    /// <summary>What the last operation said, as plain text: what the status line says (kept for callers that read it).</summary>
    [ObservableProperty]
    public partial string? StatusText { get; private set; }

    /// <summary>Why the last operation failed, as plain text: what the status line says (kept for callers that read it).</summary>
    [ObservableProperty]
    public partial string? Error { get; private set; }

    /// <summary>
    /// The selected backup comes from another domain (restore only: it is refused, import is possible) - a note under the
    /// list, true for as long as that backup is selected; null otherwise.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasOtherDomain))]
    public partial string? OtherDomainText { get; private set; }

    public bool HasOtherDomain => OtherDomainText is not null;

    /// <summary>Something was written to the directory or SYSVOL (restore, import).</summary>
    public bool ChangedDirectory { get; private set; }

    /// <summary>The equivalent of a backup (<c>Backup-GPO</c>), shown and copyable in the window.</summary>
    public string PowerShell
    {
        get
        {
            if (!IsBackup)
            {
                return string.Empty;
            }

            var server = Session?.Info.Server ?? string.Empty;
            var domain = Session?.Info.DomainDnsName ?? string.Empty;
            var sb = new StringBuilder();
            sb.Append("#Requires -Modules GroupPolicy").AppendLine();
            foreach (var gpo in Gpos)
            {
                sb.Append("Backup-GPO -Guid ").Append(PowerShellExporter.Quote(gpo.Id.ToString("D")))
                    .Append(" -Path ").Append(PowerShellExporter.Quote(string.IsNullOrWhiteSpace(Folder) ? "<folder>" : Folder))
                    .Append(" -Comment ").Append(PowerShellExporter.Quote(Comment ?? string.Empty))
                    .Append(" -Domain ").Append(PowerShellExporter.Quote(domain))
                    .Append(" -Server ").Append(PowerShellExporter.Quote(server)).AppendLine();
            }

            return sb.ToString();
        }
    }

    public override async Task InitializeAsync()
    {
        if (ListsBackups && !string.IsNullOrWhiteSpace(Folder))
        {
            await LoadBackupsAsync().ConfigureAwait(true);
        }
    }

    partial void OnFolderChanged(string value) => OnPropertyChanged(nameof(PowerShell));

    partial void OnSelectedBackupChanged(GpoBackupRowViewModel? value) => ReadSelectedAsync(value).Forget();

    [RelayCommand(CanExecute = nameof(IsIdle))]
    private async Task BrowseAsync()
    {
        var folder = await _gp.Dialogs.PickFolderAsync(Localizer[L.GroupPolicy.PickFolderTitle], string.IsNullOrWhiteSpace(Folder) ? null : Folder).ConfigureAwait(true);
        if (folder is null)
        {
            return;
        }

        Folder = folder;
        if (ListsBackups)
        {
            await LoadBackupsAsync().ConfigureAwait(true);
        }
    }

    /// <summary>
    /// Lists the backups of <see cref="Folder"/> (restore, import) - the operation of the window: its line says "Reading the
    /// backups in the folder…" with Stop (a folder on a share that does not answer is waited for off the UI thread, and
    /// Stop returns control). The list is the result: the line then shows its count. A folder that could not be read leaves
    /// an empty list - the state of the window until it is read again (Retry); a read that was stopped leaves the list as
    /// it was, and its message can be closed. Closing the window and the end of its session end the read without a word.
    /// </summary>
    [RelayCommand]
    private async Task LoadBackupsAsync()
    {
        Error = null;
        var folder = Folder?.Trim();
        var latest = _listing.Next();
        if (string.IsNullOrEmpty(folder))
        {
            Backups = [];
            _listed = false;
            _listState = null;
            UpdateLasting();
            UpdateSummary();
            return;
        }

        using var reading = Status.Begin(Localizer[L.GpoBackupStatus.ReadingFolder], new PageStatusLog(L.GpoBackupStatus.ReadingFolder));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(latest, reading.Token);
        var token = linked.Token;

        // What the last read left is over: this one decides.
        _listState = null;
        UpdateLasting();
        try
        {
            _entries = await Task.Run(() => GpmcBackupFormat.List(folder)).WaitAsync(token).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            // Only a read the operator stopped says so: the list is as it was.
            if (reading.IsCancelling)
            {
                var retry = Localizer[L.Common.Retry];
                reading.CompleteAttention(
                    Localizer.Format(L.GpoBackupStatus.ListStopped, retry),
                    new PageStatusLog(L.GpoBackupStatus.ListStopped),
                    new PageStatusAction(retry, LoadBackupsCommand));
            }

            return;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or System.Security.SecurityException)
        {
            _entries = [];
            Error = Localizer[L.GroupPolicy.FolderFailed];
            var code = ResultCode.Of(ex);
            _listState = PageStatus.Error(
                PageStatusText.Failure(Localizer[L.GpoBackupStatus.ListFailed], Error, Localizer[L.Feedback.RetryNext], code),
                new PageStatusLog(L.GpoBackupStatus.ListFailed).WithCode(code),
                new PageStatusAction(Localizer[L.Common.Retry], LoadBackupsCommand),
                canClose: false);
        }

        _listed = true;
        var domain = Session?.Info.DomainDnsName;
        Backups = [.. _entries.Select(e => new GpoBackupRowViewModel(e, GroupPolicyText.Date(Localizer, e.BackupTime, TimeZoneInfo.Local),
            domain is not null && !string.Equals(e.Domain, domain, StringComparison.OrdinalIgnoreCase)))];
        SelectedBackup = Mode == GpoBackupMode.Import ? Backups.FirstOrDefault() : Backups.FirstOrDefault(b => b.Entry.GpoId == Gpos.FirstOrDefault()?.Id) ?? Backups.FirstOrDefault();
        StatusText = Backups.Count == 0 ? Localizer[L.GroupPolicy.NoBackups] : Localizer.Format(L.GroupPolicy.BackupsFound, Backups.Count);
        UpdateLasting();
        UpdateSummary();
        await RememberFolderAsync(folder).ConfigureAwait(true);
    }

    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task RunAsync()
    {
        Error = null;
        switch (Mode)
        {
            case GpoBackupMode.BeforeDelete when !BackupFirst:
                await _gp.Settings.UpdateAsync(s => s.GroupPolicy.BackupBeforeDelete = false).ConfigureAwait(true);
                RequestClose(true);
                break;
            case GpoBackupMode.Backup or GpoBackupMode.BeforeDelete:
                if (await BackupAsync().ConfigureAwait(true))
                {
                    if (Mode == GpoBackupMode.BeforeDelete)
                    {
                        await _gp.Settings.UpdateAsync(s => s.GroupPolicy.BackupBeforeDelete = true).ConfigureAwait(true);
                    }

                    RequestClose(true);
                }

                break;
            default:
                await RestoreOrImportAsync().ConfigureAwait(true);
                break;
        }
    }

    private bool CanRun() => !IsBusy && !IsSessionEnded && Mode switch
    {
        GpoBackupMode.BeforeDelete => !BackupFirst || !string.IsNullOrWhiteSpace(Folder),
        GpoBackupMode.Backup => Gpos.Count > 0 && !string.IsNullOrWhiteSpace(Folder),
        _ => SelectedBackup is not null && _selectedBackup is not null && !string.IsNullOrWhiteSpace(Folder),
    };

    private bool IsIdle() => !IsBusy;

    [RelayCommand]
    private void CopyPowerShell() => ClipboardCopy.Text(Services.Clipboard, Services.Notifications, PowerShell, Localizer[L.Batch.PowerShellCopied]);

    [RelayCommand(CanExecute = nameof(IsIdle))]
    private void Close() => RequestClose(ChangedDirectory);

    protected override Task<bool> CanCloseCoreAsync() => Task.FromResult(!IsBusy);

    protected override void OnCultureChanged()
    {
        base.OnCultureChanged();
        OnPropertyChanged(nameof(RunText));
        OnPropertyChanged(nameof(CompatibilityNote));

        // The window's own sentences in the new language (how an operation ended stays as it was said).
        UpdateLasting();
        UpdateSummary();
    }

    protected override void OnSessionEnded()
    {
        // The line says for good that the session ended; what still runs ends without a word of its own (the state
        // first, so the line goes straight from "working" to it).
        Error = SessionEndedMessage;
        UpdateLasting();
        _work.Cancel();
        _listing.Cancel();
        Status.Operation?.Dispose();
        DetailsStatus.Operation?.Dispose();
        RunCommand.NotifyCanExecuteChanged();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _details.Cancel();
            _work.Cancel();
            _listing.Cancel();

            // What still runs says nothing any more.
            Status.Operation?.Dispose();
            Status.Front = null;
            DetailsStatus.Operation?.Dispose();
        }

        base.Dispose(disposing);
    }

    /// <summary>
    /// The state of the window that lasts, the most important first: its session ended, or it was opened without one (an
    /// error that cannot be closed - nothing can be backed up, restored or imported), else why the list is empty after a
    /// folder that could not be read.
    /// </summary>
    private void UpdateLasting() => Status.SetLasting(
        SessionEndedStatus ?? (Session is null ? PageStatus.Error(NoSessionMessage, new PageStatusLog(L.Errors.NotConnected), canClose: false) : _listState));

    /// <summary>The summary: how many backups the folder holds (nothing before it was read once, and in a mode that lists none).</summary>
    private void UpdateSummary() => Status.SetIdle(!ListsBackups || !_listed ? null
        : Backups.Count == 0 ? Localizer[L.GpoBackupStatus.NoneFound]
        : Localizer.Format(L.GpoBackupStatus.Found, Backups.Count));

    /// <summary>
    /// Writes one backup per GPO into the folder (activity center); true when every backup was written. The backup is the
    /// operation of the window: "Backing up the GPOs: 1 of 3…" with the part done and Stop (the activity of the activity
    /// center is stopped with it). It says nothing when it ended well - the window closes and the toast says it -; stopped,
    /// it says how many GPOs were written; failed, why.
    /// </summary>
    private async Task<bool> BackupAsync()
    {
        var session = Session;
        var folder = Folder.Trim();
        if (session is null)
        {
            // The line says so for good.
            Error = NoSessionMessage;
            return false;
        }

        IsBusy = true;
        StatusText = Localizer[L.GroupPolicy.BackupRunning];
        var total = Gpos.Count;
        using var backingUp = Status.Begin(
            Localizer[L.GpoBackupStatus.BackingUp], new PageStatusLog(L.GpoBackupStatus.BackingUp, 0, total), PageProgress.Of(0, total),
            writes: OperationWrites.Other);

        // The operator's Stop, closing the window and the end of its session all end the backup.
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(_work.Next(), backingUp.Token);
        var token = linked.Token;
        var progressOnScreen = Services.Dispatcher.CreateProgress<BatchProgress>(p => ReportBackup(backingUp, p, total));
        Exception? firstFailure = null;
        try
        {
            await Task.Run(() => System.IO.Directory.CreateDirectory(folder), token).ConfigureAwait(true);
            var access = await _gp.OpenSysvolAsync(session, token).ConfigureAwait(true);
            if (access.Connection is not { } sysvol)
            {
                Error = _gp.SysvolText(access.Reason ?? SysvolUnavailableReason.Failed, session.Info.Server);
                backingUp.CompleteAttention(
                    PageStatusText.Failure(Localizer[L.GpoBackupStatus.NotBackedUp], Error, Localizer[L.Feedback.RetryNext]),
                    new PageStatusLog(L.GroupPolicy.SysvolFailedShort),
                    new PageStatusAction(Localizer[L.Common.Retry], RunCommand));
                return false;
            }

            await using (sysvol.ConfigureAwait(true))
            {
                var gpos = Gpos;
                var comment = Comment ?? string.Empty;
                var planner = _gp.Planner;
                var time = _gp.Time;
                var withDirectorySettings = 0;
                var run = Services.Activities.Start(Localizer.Format(L.GroupPolicy.BackupActivity, gpos.Count), gpos.Count, async (progress, activityToken) =>
                {
                    using var linkedActivity = CancellationTokenSource.CreateLinkedTokenSource(token, activityToken);
                    var started = time.GetUtcNow();
                    var results = new List<ObjectResult>();
                    var cancelled = false;
                    foreach (var gpo in gpos)
                    {
                        if (linkedActivity.IsCancellationRequested)
                        {
                            cancelled = true;
                            break;
                        }

                        var objectPlan = new ObjectPlan { DistinguishedName = gpo.Dn, DisplayName = gpo.DisplayName, Kind = ObjectKind.GroupPolicyContainer, Changes = [] };
                        var begin = time.GetUtcNow();
                        ObjectResultStatus status;
                        try
                        {
                            var backup = await planner.ReadBackupAsync(session, sysvol, gpo.Id, comment, linkedActivity.Token).ConfigureAwait(false);
                            await GpmcBackupFormat.WriteAsync(folder, backup, AppVersion.Display, linkedActivity.Token).ConfigureAwait(false);
                            status = ObjectResultStatus.Succeeded;
                            if (backup.DirectoryObjectCount > 0)
                            {
                                withDirectorySettings++;
                            }
                        }
                        catch (OperationCanceledException)
                        {
                            cancelled = true;
                            break;
                        }
                        catch (Exception ex) when (GroupPolicyText.IsExpected(ex))
                        {
                            firstFailure ??= ex;
                            status = ObjectResultStatus.Failed;
                        }

                        var result = new ObjectResult(objectPlan, status, [], gpo.Dn, begin, time.GetUtcNow());
                        results.Add(result);
                        var reported = new BatchProgress(results.Count, gpos.Count, results.Count(r => r.Status == ObjectResultStatus.Failed), result);
                        progress.Report(reported);
                        progressOnScreen.Report(reported);
                    }

                    return new BatchResult(new ChangePlan { Objects = [], Title = "Backup-GPO", Server = session.Info.Server }, results, session.Info.Server,
                        session.Info.BoundIdentity, started, time.GetUtcNow(), cancelled);
                });
                var outcome = await run.Execution.ConfigureAwait(true);
                var failed = outcome.FailedCount;
                await RememberFolderAsync(folder).ConfigureAwait(true);
                if (backingUp.IsEnded)
                {
                    return false;
                }

                if (outcome.WasCancelled)
                {
                    // Stopped in the line, in the list of background operations or by closing the window.
                    StatusText = Localizer[L.Activity.Cancelled];
                    var again = RunText;
                    backingUp.CompleteAttention(
                        Localizer.Format(L.GpoBackupStatus.BackupStopped, outcome.SucceededCount, gpos.Count, again),
                        new PageStatusLog(L.GpoBackupStatus.BackupStopped, outcome.SucceededCount, gpos.Count),
                        new PageStatusAction(again, RunCommand));
                    return false;
                }

                if (failed > 0)
                {
                    Error = Localizer.Format(L.GroupPolicy.BackupFailed, failed, gpos.Count);
                    var reason = firstFailure is null ? null : GroupPolicyText.Error(Localizer, Errors, firstFailure);
                    var code = ResultCode.Of(firstFailure);
                    backingUp.CompleteError(
                        PageStatusText.Failure(Localizer.Format(L.GpoBackupStatus.BackupFailed, failed, gpos.Count), reason, null, code),
                        new PageStatusLog(L.GpoBackupStatus.BackupFailed, failed, gpos.Count).WithCode(code));
                    return false;
                }

                StatusText = Localizer.Format(L.GroupPolicy.BackupDone, gpos.Count, folder);
                if (withDirectorySettings > 0)
                {
                    // Software installation, wireless and wired network policies live in the directory, not in the backup.
                    StatusText += " " + Localizer.Format(L.GroupPolicy.BackupWithoutDirectorySettings, withDirectorySettings);
                    Services.Notifications.Show(NotificationSeverity.Warning, StatusText);
                }
                else
                {
                    Services.Notifications.Show(NotificationSeverity.Success, StatusText);
                }

                // The window closes: what the operator learns is the toast, as before.
                return true;
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // Only a backup the operator stopped says so (nothing was written before the first GPO).
            if (backingUp.IsCancelling)
            {
                var again = RunText;
                StatusText = Localizer[L.Activity.Cancelled];
                backingUp.CompleteAttention(
                    Localizer.Format(L.GpoBackupStatus.BackupStopped, 0, total, again),
                    new PageStatusLog(L.GpoBackupStatus.BackupStopped, 0, total),
                    new PageStatusAction(again, RunCommand));
            }

            return false;
        }
        catch (Exception ex) when (GroupPolicyText.IsExpected(ex))
        {
            Error = GroupPolicyText.Error(Localizer, Errors, ex);
            var code = ResultCode.Of(ex);
            backingUp.CompleteError(
                PageStatusText.Failure(Localizer[L.GpoBackupStatus.NotBackedUp], Error, null, code),
                new PageStatusLog(L.GpoBackupStatus.NotBackedUp).WithCode(code));
            return false;
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>How far the backup is: "Backing up the GPOs: 1 of 3…" with the part done in the ring.</summary>
    private void ReportBackup(OperationScope backingUp, BatchProgress progress, int total) => backingUp.Report(
        Localizer.Format(L.GpoBackupStatus.BackingUpProgress, PageStatusText.Progress(Localizer, progress.Completed, total)),
        PageProgress.Of(progress.Completed, total),
        new PageStatusLog(L.GpoBackupStatus.BackingUp, progress.Completed, total));

    /// <summary>
    /// Plans the restore (or import) of the selected backup and shows the preview. Making the plan is the operation of the
    /// window - "Restore “…”: preparing the changes…" with Stop - until the change plan preview opens: that window says its
    /// own status, and the line is the window's again. When nothing comes of it - SYSVOL is not available, the plan could not
    /// be made, the operator stopped it - the line says why, with Retry, which makes the plan again. Stop must hold: a
    /// plan that was made although Stop was pressed is not previewed.
    /// </summary>
    private async Task RestoreOrImportAsync()
    {
        if (_selectedBackup is not { } backup || Session is not { } session)
        {
            // The line says that the session ended or that there is none; a backup that is still being read is waited for.
            Error ??= NoSessionMessage;
            return;
        }

        var folder = Folder.Trim();
        var title = Mode == GpoBackupMode.Import
            ? Localizer.Format(L.GroupPolicy.TitleImport, Gpos.FirstOrDefault()?.DisplayName ?? string.Empty)
            : Localizer.Format(L.GroupPolicy.TitleRestore, backup.DisplayName);
        IsBusy = true;
        using var preparing = Status.Begin(Localizer.Format(L.GroupPolicyStatus.Preparing, title), new PageStatusLog(L.GroupPolicyStatus.Preparing));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(preparing.Token, _gp.Lifetime);
        var token = linked.Token;
        var retry = new PageStatusAction(Localizer[L.Common.Retry], RunCommand);
        ChangePlan plan;
        try
        {
            var access = await _gp.OpenSysvolAsync(session, token).ConfigureAwait(true);
            if (access.Connection is not { } sysvol)
            {
                Error = _gp.SysvolText(access.Reason ?? SysvolUnavailableReason.Failed, session.Info.Server);
                preparing.CompleteAttention(NotChanged(title, Error, retryable: true), new PageStatusLog(L.GroupPolicy.SysvolFailedShort), retry);
                return;
            }

            await using (sysvol.ConfigureAwait(true))
            {
                plan = Mode == GpoBackupMode.Import
                    ? await _gp.Planner.PlanImportAsync(session, sysvol, Gpos[0].Id, backup, folder, token).ConfigureAwait(true)
                    : await _gp.Planner.PlanRestoreAsync(session, sysvol, backup, folder, RestoreLinks, token).ConfigureAwait(true);
            }

            if (preparing.IsCancelling)
            {
                // Stop must hold: a planner that finished although Stop was pressed does not lead to the preview.
                PlanningStopped(preparing, title, retry);
                return;
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // Only a plan the operator stopped says so: nothing was changed.
            if (preparing.IsCancelling)
            {
                PlanningStopped(preparing, title, retry);
            }

            return;
        }
        catch (Exception ex) when (GroupPolicyText.IsExpected(ex))
        {
            Error = GroupPolicyText.Error(Localizer, Errors, ex);
            var code = ResultCode.Of(ex);
            preparing.CompleteError(NotChanged(title, Error, retryable: true, code), new PageStatusLog(L.GroupPolicyStatus.NotChanged).WithCode(code), retry);
            return;
        }
        finally
        {
            IsBusy = false;
        }

        // The plan is made: the line is the window's again while the preview - which says its own status - is open.
        preparing.Dispose();
        if (await _gp.PreviewAsync(plan, title, session).ConfigureAwait(true))
        {
            ChangedDirectory = true;
            RequestClose(true);
        }
    }

    /// <summary>"Restore “…” – nothing was changed: why. Press Retry to try again. (code)".</summary>
    private string NotChanged(string title, string reason, bool retryable, ResultCode code = default) =>
        PageStatusText.Failure(Localizer.Format(L.GroupPolicyStatus.NotChanged, title), reason, retryable ? Localizer[L.Feedback.RetryNext] : null, code);

    /// <summary>The operator stopped the plan: nothing was changed, and Retry makes the plan again.</summary>
    private void PlanningStopped(OperationScope preparing, string title, PageStatusAction retry) => preparing.CompleteAttention(
        NotChanged(title, Localizer[L.GroupPolicyStatus.PreparingStopped], retryable: true),
        new PageStatusLog(L.GroupPolicyStatus.PreparingStopped),
        retry);

    /// <summary>
    /// Reads the selected backup (its files into memory, checked) for the details and the plan - the operation of the list,
    /// in <see cref="DetailsStatus"/>: "Reading the backup of “…”…" with Stop. It says nothing when it ended well; stopped
    /// or failed it is the state of that selection - the backup cannot be restored yet - with Retry, until another backup is
    /// selected or it is read again. The previous selection's read ends without a word.
    /// </summary>
    private async Task ReadSelectedAsync(GpoBackupRowViewModel? row)
    {
        var latest = _details.Next();
        _selectedBackup = null;
        BackupDetails = null;
        OtherDomainText = null;
        CanRestoreLinks = false;
        RestoreLinks = false;
        RestoreLinksText = Localizer.Format(L.GroupPolicy.RestoreLinks, 0);
        RunCommand.NotifyCanExecuteChanged();

        // What the last selection left is over: this one decides.
        DetailsStatus.SetLasting(null);
        if (row is null)
        {
            DetailsStatus.Operation?.Dispose();
            return;
        }

        var folder = Folder.Trim();
        using var reading = DetailsStatus.Begin(Localizer.Format(L.GpoBackupStatus.ReadingBackup, row.Name), new PageStatusLog(L.GpoBackupStatus.ReadingBackup));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(latest, reading.Token);
        var token = linked.Token;
        try
        {
            // Off the UI thread: the synchronous part (File.Exists, XML loads of up to 16 MB) may sit on a slow share, and
            // Stop (or another selection) must not wait for it.
            var backup = await Task.Run(() => GpmcBackupFormat.ReadAsync(folder, row.Entry.BackupId, token)).WaitAsync(token).ConfigureAwait(true);
            token.ThrowIfCancellationRequested();

            _selectedBackup = backup;
            CanRestoreLinks = Mode == GpoBackupMode.Restore && backup.Links.Count > 0;
            RestoreLinksText = Localizer.Format(L.GroupPolicy.RestoreLinks, backup.Links.Count);
            BackupDetails = Localizer.Format(L.GroupPolicy.BackupDetails, backup.Files.Count,
                backup.Files.Values.Sum(f => (long)f.Length).ToString("N0", Localizer.Culture),
                Localizer[backup.HasSidecar ? L.GroupPolicy.BackupWithSidecar : L.GroupPolicy.BackupGpmcOnly]);
            OtherDomainText = row.IsOtherDomain && Mode == GpoBackupMode.Restore ? Localizer.Format(L.GroupPolicy.BackupOtherDomain, row.Domain) : null;
            if (Error is not null && !IsBusy)
            {
                Error = null;
            }
        }
        catch (OperationCanceledException)
        {
            // Another backup was selected - or the operator pressed Stop, which says so.
            if (reading.IsCancelling)
            {
                var retry = Localizer[L.Common.Retry];
                DetailsStatus.SetLasting(PageStatus.Attention(
                    Localizer.Format(L.GpoBackupStatus.BackupReadStopped, retry),
                    new PageStatusLog(L.GpoBackupStatus.BackupReadStopped),
                    new PageStatusAction(retry, RetryBackupCommand)));
            }
        }
        catch (Exception ex) when (GroupPolicyText.IsExpected(ex))
        {
            if (!token.IsCancellationRequested)
            {
                Error = GroupPolicyText.Error(Localizer, Errors, ex);
                var code = ResultCode.Of(ex);
                DetailsStatus.SetLasting(PageStatus.Error(
                    PageStatusText.Failure(Localizer.Format(L.GpoBackupStatus.BackupReadFailed, row.Name), Error, Localizer[L.Feedback.RetryNext], code),
                    new PageStatusLog(L.GpoBackupStatus.BackupReadFailed).WithCode(code),
                    new PageStatusAction(Localizer[L.Common.Retry], RetryBackupCommand),
                    canClose: false));
            }
        }
        finally
        {
            RunCommand.NotifyCanExecuteChanged();
        }
    }

    /// <summary>Reads the selected backup again (the Retry of the line).</summary>
    [RelayCommand]
    private Task RetryBackupAsync() => ReadSelectedAsync(SelectedBackup);

    private Task RememberFolderAsync(string folder) => _gp.Settings.UpdateAsync(s => s.GroupPolicy.LastBackupFolder = folder);

    private static string SafeFormat(string template, DateTimeOffset now)
    {
        try
        {
            return string.Format(CultureInfo.CurrentCulture, template, now.ToString("d", CultureInfo.CurrentCulture));
        }
        catch (FormatException)
        {
            return template;
        }
    }
}
