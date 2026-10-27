using System.Globalization;
using System.Text;
using AdMng.Core.Batch;
using AdMng.Core.GroupPolicy;
using AdMng.Core.GroupPolicy.Backup;
using AdMng.Core.GroupPolicy.Management;
using AdMng.Core.Model;
using AdMng.Presentation.Dialogs;
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
public sealed partial class GpoBackupViewModel : WindowViewModelBase
{
    private readonly GroupPolicyServices _gp;
    private readonly SerialCancellation _details = new();
    private readonly SerialCancellation _work = new();
    private IReadOnlyList<GpoBackupEntry> _entries = [];
    private GpoBackup? _selectedBackup;

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
    }

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

    [ObservableProperty]
    public partial string? StatusText { get; private set; }

    [ObservableProperty]
    public partial string? Error { get; private set; }

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

    /// <summary>Lists the backups of <see cref="Folder"/> (restore, import).</summary>
    [RelayCommand]
    private async Task LoadBackupsAsync()
    {
        Error = null;
        var folder = Folder?.Trim();
        if (string.IsNullOrEmpty(folder))
        {
            Backups = [];
            return;
        }

        try
        {
            _entries = await Task.Run(() => GpmcBackupFormat.List(folder)).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or System.Security.SecurityException)
        {
            _entries = [];
            Error = Localizer[L.GroupPolicy.FolderFailed];
        }

        var domain = Session?.Info.DomainDnsName;
        Backups = [.. _entries.Select(e => new GpoBackupRowViewModel(e, GroupPolicyText.Date(Localizer, e.BackupTime, TimeZoneInfo.Local),
            domain is not null && !string.Equals(e.Domain, domain, StringComparison.OrdinalIgnoreCase)))];
        SelectedBackup = Mode == GpoBackupMode.Import ? Backups.FirstOrDefault() : Backups.FirstOrDefault(b => b.Entry.GpoId == Gpos.FirstOrDefault()?.Id) ?? Backups.FirstOrDefault();
        StatusText = Backups.Count == 0 ? Localizer[L.GroupPolicy.NoBackups] : Localizer.Format(L.GroupPolicy.BackupsFound, Backups.Count);
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

    /// <summary>Cancels a running backup (the activity center cancels it too).</summary>
    [RelayCommand]
    private void CancelWork() => _work.Cancel();

    public override Task<bool> CanCloseAsync() => Task.FromResult(!IsBusy);

    protected override void OnCultureChanged()
    {
        base.OnCultureChanged();
        OnPropertyChanged(nameof(RunText));
        OnPropertyChanged(nameof(CompatibilityNote));
    }

    protected override void OnSessionEnded()
    {
        Error = SessionEndedMessage;
        RunCommand.NotifyCanExecuteChanged();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _details.Cancel();
            _work.Cancel();
        }

        base.Dispose(disposing);
    }

    /// <summary>Writes one backup per GPO into the folder (activity center); true when every backup was written.</summary>
    private async Task<bool> BackupAsync()
    {
        var session = Session;
        var folder = Folder.Trim();
        if (session is null)
        {
            Error = NoSessionMessage;
            return false;
        }

        IsBusy = true;
        StatusText = Localizer[L.GroupPolicy.BackupRunning];
        var token = _work.Next();
        try
        {
            await Task.Run(() => System.IO.Directory.CreateDirectory(folder), token).ConfigureAwait(true);
            var access = await _gp.OpenSysvolAsync(session, token).ConfigureAwait(true);
            if (access.Connection is not { } sysvol)
            {
                Error = _gp.SysvolText(access.Reason ?? SysvolUnavailableReason.Failed, session.Info.Server);
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
                    using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, activityToken);
                    var started = time.GetUtcNow();
                    var results = new List<ObjectResult>();
                    var cancelled = false;
                    foreach (var gpo in gpos)
                    {
                        if (linked.IsCancellationRequested)
                        {
                            cancelled = true;
                            break;
                        }

                        var objectPlan = new ObjectPlan { DistinguishedName = gpo.Dn, DisplayName = gpo.DisplayName, Kind = ObjectKind.GroupPolicyContainer, Changes = [] };
                        var begin = time.GetUtcNow();
                        ObjectResultStatus status;
                        try
                        {
                            var backup = await planner.ReadBackupAsync(session, sysvol, gpo.Id, comment, linked.Token).ConfigureAwait(false);
                            await GpmcBackupFormat.WriteAsync(folder, backup, AppVersion.Display, linked.Token).ConfigureAwait(false);
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
                            status = ObjectResultStatus.Failed;
                        }

                        var result = new ObjectResult(objectPlan, status, [], gpo.Dn, begin, time.GetUtcNow());
                        results.Add(result);
                        progress.Report(new BatchProgress(results.Count, gpos.Count, results.Count(r => r.Status == ObjectResultStatus.Failed), result));
                    }

                    return new BatchResult(new ChangePlan { Objects = [], Title = "Backup-GPO", Server = session.Info.Server }, results, session.Info.Server,
                        session.Info.BoundIdentity, started, time.GetUtcNow(), cancelled);
                });
                var outcome = await run.Execution.ConfigureAwait(true);
                var failed = outcome.FailedCount;
                await RememberFolderAsync(folder).ConfigureAwait(true);
                if (outcome.WasCancelled)
                {
                    StatusText = Localizer[L.Activity.Cancelled];
                    return false;
                }

                if (failed > 0)
                {
                    Error = Localizer.Format(L.GroupPolicy.BackupFailed, failed, gpos.Count);
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

                return true;
            }
        }
        catch (Exception ex) when (GroupPolicyText.IsExpected(ex))
        {
            Error = GroupPolicyText.Error(Localizer, Errors, ex);
            return false;
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Plans the restore (or import) of the selected backup and shows the preview.</summary>
    private async Task RestoreOrImportAsync()
    {
        if (_selectedBackup is not { } backup || Session is not { } session)
        {
            Error ??= NoSessionMessage;
            return;
        }

        var folder = Folder.Trim();
        var title = Mode == GpoBackupMode.Import
            ? Localizer.Format(L.GroupPolicy.TitleImport, Gpos.FirstOrDefault()?.DisplayName ?? string.Empty)
            : Localizer.Format(L.GroupPolicy.TitleRestore, backup.DisplayName);
        IsBusy = true;
        ChangePlan plan;
        try
        {
            var access = await _gp.OpenSysvolAsync(session, _gp.Lifetime).ConfigureAwait(true);
            if (access.Connection is not { } sysvol)
            {
                Error = _gp.SysvolText(access.Reason ?? SysvolUnavailableReason.Failed, session.Info.Server);
                return;
            }

            await using (sysvol.ConfigureAwait(true))
            {
                plan = Mode == GpoBackupMode.Import
                    ? await _gp.Planner.PlanImportAsync(session, sysvol, Gpos[0].Id, backup, folder, _gp.Lifetime).ConfigureAwait(true)
                    : await _gp.Planner.PlanRestoreAsync(session, sysvol, backup, folder, RestoreLinks, _gp.Lifetime).ConfigureAwait(true);
            }
        }
        catch (Exception ex) when (GroupPolicyText.IsExpected(ex))
        {
            Error = GroupPolicyText.Error(Localizer, Errors, ex);
            return;
        }
        finally
        {
            IsBusy = false;
        }

        if (await _gp.PreviewAsync(plan, title, session).ConfigureAwait(true))
        {
            ChangedDirectory = true;
            RequestClose(true);
        }
    }

    /// <summary>Reads the selected backup (its files into memory, checked) for the details and the plan.</summary>
    private async Task ReadSelectedAsync(GpoBackupRowViewModel? row)
    {
        var token = _details.Next();
        _selectedBackup = null;
        BackupDetails = null;
        CanRestoreLinks = false;
        RestoreLinks = false;
        RestoreLinksText = Localizer.Format(L.GroupPolicy.RestoreLinks, 0);
        RunCommand.NotifyCanExecuteChanged();
        if (row is null)
        {
            return;
        }

        var folder = Folder.Trim();
        try
        {
            var backup = await GpmcBackupFormat.ReadAsync(folder, row.Entry.BackupId, token).ConfigureAwait(true);
            if (token.IsCancellationRequested)
            {
                return;
            }

            _selectedBackup = backup;
            CanRestoreLinks = Mode == GpoBackupMode.Restore && backup.Links.Count > 0;
            RestoreLinksText = Localizer.Format(L.GroupPolicy.RestoreLinks, backup.Links.Count);
            BackupDetails = Localizer.Format(L.GroupPolicy.BackupDetails, backup.Files.Count,
                backup.Files.Values.Sum(f => (long)f.Length).ToString("N0", Localizer.Culture),
                Localizer[backup.HasSidecar ? L.GroupPolicy.BackupWithSidecar : L.GroupPolicy.BackupGpmcOnly]);
            if (row.IsOtherDomain && Mode == GpoBackupMode.Restore)
            {
                Error = Localizer.Format(L.GroupPolicy.BackupOtherDomain, row.Domain);
            }
            else if (Error is not null && !IsBusy)
            {
                Error = null;
            }
        }
        catch (OperationCanceledException)
        {
            // Another backup was selected.
        }
        catch (Exception ex) when (GroupPolicyText.IsExpected(ex))
        {
            if (!token.IsCancellationRequested)
            {
                Error = GroupPolicyText.Error(Localizer, Errors, ex);
            }
        }
        finally
        {
            RunCommand.NotifyCanExecuteChanged();
        }
    }

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
