using AdMng.Core.Directory;
using AdMng.Core.GroupPolicy;
using AdMng.Core.GroupPolicy.Diagnostics;
using AdMng.Presentation.Feedback;
using AdMng.Presentation.GroupPolicy.Diagnostics;
using AdMng.Presentation.Infrastructure;
using AdMng.Presentation.Localization;
using AdMng.Presentation.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AdMng.Presentation.GroupPolicy;

/// <summary>
/// The "Stan" tab of a GPO (K6, design note docs/dev/k-computers-and-gpo.md 20.2): the GPO diagnostics engine for this GPO
/// - Full (its folder, the folder ACL, its settings, the objects below its links, the SIDs, the GPOs it meets) when SYSVOL
/// of the session's DC is available, Quick otherwise - with the same rows and repairs as "Diagnostyka zasad grupy", and a
/// one-line result per area (<see cref="GroupPolicyObjectDocumentViewModel.Checks"/>). The check runs while the tab is shown:
/// with the document's load when it opens on "Stan", otherwise when the tab is selected (and again after every reload).
/// "Wyrównaj wersje" and "Napraw uprawnienia folderu" follow its findings. Results live in memory only (rule 1).
/// </summary>
/// <remarks>
/// <b>What the tab says</b> (track V, stage V2b-3): the tab has a status of its own, <see cref="HealthStatus"/>, which
/// is in front of the document's while the tab is shown - the one status line of the document then says it. A check
/// the tab starts (it was selected, "Sprawdź ponownie") is the operation of the tab, with its stages and Stop; a check
/// that is a step of the document's load belongs to that load and reports its stages there. At rest the tab says when
/// the GPO was checked and how many findings there are; a check that failed is its state until the GPO is checked
/// again (with Retry), a check the operator stopped a result. What a check could not evaluate
/// (<see cref="HealthNotes"/>) is a note above the findings: it is about how complete they are.
/// </remarks>
public sealed partial class GroupPolicyObjectDocumentViewModel
{
    private readonly SerialCancellation _healthLoad = new();

    /// <summary>The findings shown are of the last load of the document.</summary>
    private bool _healthCurrent;

    /// <summary>"Stan" was selected while a load ran (possibly past its check): the check runs when the load ends.</summary>
    private bool _healthPending;

    private GpoQuickFixRunner? _healthFixes;
    private HealthCheck? _health;

    /// <summary>
    /// What the "Stan" tab says: its check, how it ended and - at rest - when the GPO was checked. In front of the
    /// document's status (<see cref="GroupPolicyDocumentBase.Status"/>) while the tab is shown.
    /// </summary>
    public PageStatusHolder HealthStatus { get; }

    /// <summary>The findings of the GPO, most severe first.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNoHealthFindings))]
    public partial IReadOnlyList<GpoFindingRowViewModel> HealthFindings { get; private set; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelectedHealthFinding))]
    [NotifyCanExecuteChangedFor(nameof(FixHealthFindingCommand), nameof(EditHealthSettingCommand))]
    public partial GpoFindingRowViewModel? SelectedHealthFinding { get; set; }

    /// <summary>A finding is selected: the card shows its details (else the hint to select one).</summary>
    public bool HasSelectedHealthFinding => SelectedHealthFinding is not null;

    /// <summary>The engine reads the GPO (the tab shows <see cref="HealthStatusText"/> as its progress).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNoHealthFindings))]
    [NotifyCanExecuteChangedFor(nameof(CheckHealthCommand), nameof(FixHealthFindingCommand))]
    public partial bool IsCheckingHealth { get; private set; }

    /// <summary>
    /// "Pełne sprawdzenie, 3.11.2026 14:32 – 2 uwagi" or the progress of the running check. The status line says both
    /// (<see cref="HealthStatus"/>); the text is kept for the code that reads it.
    /// </summary>
    [ObservableProperty]
    public partial string HealthStatusText { get; private set; } = string.Empty;

    /// <summary>
    /// What the check could not evaluate (SYSVOL unavailable, folders that were not read): how complete the findings
    /// below are. A check that failed altogether is no note - the status line says it.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasHealthNotes), nameof(HealthNote))]
    public partial IReadOnlyList<string> HealthNotes { get; private set; } = [];

    public bool HasHealthNotes => HealthNotes.Count > 0;

    /// <summary><see cref="HealthNotes"/> as the text of the note above the findings, one per line (null: no note).</summary>
    public string? HealthNote => HealthNotes.Count == 0 ? null : string.Join(Environment.NewLine, HealthNotes);

    /// <summary>"Nie wykryto problemów w tym obiekcie GPO." is shown.</summary>
    public bool HasNoHealthFindings => _health is not null && !IsCheckingHealth && HealthFindings.Count == 0;

    /// <summary>
    /// Called at the end of a load: the findings of the previous load are no longer current; the engine runs now when the
    /// "Stan" tab is shown (on the load's session and SYSVOL connection), else when the tab is selected.
    /// </summary>
    private async Task LoadHealthAsync(IDirectorySession session, ISysvolWriteConnection? sysvol, CancellationToken cancellationToken)
    {
        _healthCurrent = false;
        _healthPending = false;
        _healthLoad.Cancel();
        if (SelectedTab == GpoDocumentTab.Status)
        {
            // A step of the load: no operation of its own - its stages are reported by the load of the document.
            await CheckHealthCoreAsync(session, sysvol, SysvolWarning, null, cancellationToken).ConfigureAwait(true);
        }
    }

    /// <summary>
    /// The tab switched to "Stan": the findings of this load are read when they are not yet - at once, or when the running
    /// load ends (it may be past its own check: disposing SYSVOL, reading the PDC note).
    /// </summary>
    partial void OnSelectedTabChanged(GpoDocumentTab oldValue, GpoDocumentTab newValue)
    {
        // The tab shown is in front before it is activated: what its check says is said by the line of the document.
        Status.Front = newValue == GpoDocumentTab.Status ? HealthStatus : null;
        if (newValue == GpoDocumentTab.Status && !_healthCurrent && !IsCheckingHealth && Gpo is not null)
        {
            if (IsLoading)
            {
                _healthPending = true;
            }
            else
            {
                RefreshHealthAsync().Forget();
            }
        }
    }

    /// <summary>A load started or ended: the commands follow it, and a check "Stan" asked for meanwhile runs now.</summary>
    protected override void OnLoadingChanged()
    {
        if (!IsLoading && _healthPending)
        {
            _healthPending = false;
            if (SelectedTab == GpoDocumentTab.Status && !_healthCurrent && !IsCheckingHealth && Gpo is not null && !IsDisposed)
            {
                RefreshHealthAsync().Forget();
            }
        }

        RefreshCommands();
    }

    /// <summary>"Sprawdź ponownie".</summary>
    [RelayCommand(CanExecute = nameof(CanCheckHealth))]
    private Task CheckHealthAsync() => RefreshHealthAsync();

    private bool CanCheckHealth() => !IsLoading && !IsCheckingHealth && HasSession && Gpo is not null;

    /// <summary>"Napraw…" of the selected finding (the document reloads, and the tab checks again, after the change).</summary>
    [RelayCommand(CanExecute = nameof(CanFixHealthFinding))]
    private Task FixHealthFindingAsync()
    {
        if (SelectedHealthFinding is not { Finding.Fix: { } fix } row || _health is not { } health)
        {
            return Task.CompletedTask;
        }

        _healthFixes ??= new GpoQuickFixRunner(Actions);
        var names = new GpoFindingRows(Localizer, health.Snapshot, health.Names);
        return RunAsync(() => _healthFixes.RunAsync(fix, GpoQuickFixRunner.SubjectOf(row), names.NameOf));
    }

    private bool CanFixHealthFinding() => SelectedHealthFinding is { HasFix: true } && !IsCheckingHealth && !IsLoading && HasSession;

    /// <summary>"Edytuj to ustawienie" of the selected finding.</summary>
    [RelayCommand(CanExecute = nameof(CanEditHealthSetting))]
    private void EditHealthSetting()
    {
        if (SelectedHealthFinding is { CanEditSetting: true, Focus: { } focus } && Gpo is { } gpo)
        {
            Actions.EditSetting(gpo.Id, gpo.DisplayName, focus);
        }
    }

    private bool CanEditHealthSetting() => SelectedHealthFinding is { CanEditSetting: true };

    /// <summary>
    /// The engine on the window's current session with SYSVOL opened for it (the tab was selected, "Sprawdź ponownie"):
    /// the operation of the tab - "Checking the GPO…", then its stages, with Stop.
    /// </summary>
    private async Task RefreshHealthAsync()
    {
        var token = _healthLoad.Next();
        if (Gp.Sessions.Current is not { } session || IsDisposed)
        {
            return;
        }

        using var checking = HealthStatus.Begin(Localizer[L.GpoDiagnostics.HealthChecking], new PageStatusLog(L.GpoDiagnostics.HealthChecking));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, Gp.Lifetime, checking.Token);
        try
        {
            var access = await Gp.OpenSysvolAsync(session, linked.Token).ConfigureAwait(true);
            var warning = access.Connection is null ? Gp.SysvolText(access.Reason ?? SysvolUnavailableReason.Failed, session.Info.Server) : null;
            try
            {
                await CheckHealthCoreAsync(session, access.Connection, warning, checking, linked.Token).ConfigureAwait(true);
            }
            finally
            {
                if (access.Connection is { } connection)
                {
                    await connection.DisposeAsync().ConfigureAwait(true);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // A newer check, a reload or the window closed: nothing to say. The operator's Stop: the tab keeps what the
            // check before found, and says that it was not updated.
            if (checking.IsCancelling)
            {
                var again = Localizer[L.GpoDiagnostics.CheckAgain];
                checking.CompleteAttention(
                    Localizer.Format(L.GroupPolicyStatus.HealthStopped, again),
                    new PageStatusLog(L.GroupPolicyStatus.HealthStopped),
                    new PageStatusAction(again, CheckHealthCommand));
            }
        }
        finally
        {
            RefreshCommands();
        }
    }

    /// <summary>
    /// Reads and analyses the GPO; a failure is the state of the tab (never the whole document's error).
    /// <paramref name="sysvolWarning"/> says why <paramref name="sysvol"/> is null (the SYSVOL open this check made or shares).
    /// <paramref name="checking"/> is the operation of the tab when the tab started the check, null when the check is a
    /// step of the document's load: the stages are then reported by that load.
    /// </summary>
    private async Task CheckHealthCoreAsync(IDirectorySession session, ISysvolWriteConnection? sysvol, string? sysvolWarning, OperationScope? checking,
        CancellationToken cancellationToken)
    {
        var depth = sysvol is null ? GpoDiagnosticsDepth.Quick : GpoDiagnosticsDepth.Full;
        var keep = SelectedHealthFinding?.Key;
        IsCheckingHealth = true;
        HealthStatusText = Localizer[L.GpoDiagnostics.HealthChecking];
        HealthStatus.SetLasting(null);
        try
        {
            var progress = Gp.Dispatcher.CreateProgress<GpoDiagnosticsProgress>(p =>
            {
                if (IsCheckingHealth)
                {
                    var stage = GpoFindingText.Stage(Localizer, p);
                    HealthStatusText = stage;
                    if (checking is not null)
                    {
                        checking.Report(stage, log: new PageStatusLog(L.GpoDiagnostics.HealthChecking, p.Done, p.Total));
                    }
                    else
                    {
                        LoadOperation?.Report(stage);
                    }
                }
            });
            var request = new GpoDiagnosticsRequest(GpoId, depth);
            var (snapshot, result) = await Task.Run(async () =>
            {
                var read = await GroupPolicyDiagnosticsReader.ReadAsync(session, sysvol, request, progress, cancellationToken).ConfigureAwait(false);
                return (read, GroupPolicyDiagnostics.Analyze(read));
            }, cancellationToken).ConfigureAwait(true);
            var names = await Task.Run(() => GpoFindingRows.ResolveNamesAsync(session, result, Localizer, cancellationToken), cancellationToken)
                .ConfigureAwait(true);
            cancellationToken.ThrowIfCancellationRequested();
            _health = new HealthCheck(snapshot, result, names, depth, Gp.Time.GetLocalNow(), sysvol is null ? sysvolWarning : null);
            ShowHealth(keep);
            _healthCurrent = true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException && GroupPolicyText.IsExpected(ex))
        {
            _health = null;
            HealthFindings = [];
            Checks = [];
            HasVersionMismatch = false;
            HasFolderAclMismatch = false;
            HealthNotes = [];
            HealthStatusText = string.Empty;

            // The tab is empty because of it: its state until the GPO is checked again (shown when the running
            // operation - the tab's or the document's - is over).
            var code = ResultCode.Of(ex);
            HealthStatus.SetIdle(null);
            HealthStatus.SetLasting(PageStatus.Error(
                PageStatusText.Failure(Localizer[L.GroupPolicyStatus.HealthFailed], GroupPolicyText.Error(Localizer, Gp.Errors, ex), Localizer[L.Feedback.RetryNext], code),
                new PageStatusLog(L.GroupPolicyStatus.HealthFailed).WithCode(code),
                new PageStatusAction(Localizer[L.Common.Retry], CheckHealthCommand)));
        }
        finally
        {
            IsCheckingHealth = false;
        }
    }

    /// <summary>Shows the last check in the UI language (also after a language change).</summary>
    private void ShowHealth(string? keepKey)
    {
        if (_health is not { } health)
        {
            return;
        }

        var rows = new GpoFindingRows(Localizer, health.Snapshot, health.Names);
        HealthFindings = rows.Rows(health.Result);
        SelectedHealthFinding = keepKey is null ? null : HealthFindings.FirstOrDefault(r => string.Equals(r.Key, keepKey, StringComparison.Ordinal));
        HasVersionMismatch = health.Result.Any(NeedsVersionAlignment);
        HasFolderAclMismatch = health.Result.Any(f => f.Code == GpoFindingCodes.FolderSecurityMismatch);
        Checks = AreaChecks(health);
        HealthNotes = health.Result.NotEvaluated.Contains(GpoRuleGroup.SysvolVersions) && health.SysvolWarning is { } warning
            ? [warning]
            : health.Result.FoldersNotRead.Count > 0 ? [Localizer.Format(L.GpoDiagnostics.NoteFoldersNotRead,
                rows.GpoName(GpoId, null) + " (" + GpoFindingText.FolderStatus(Localizer, health.Snapshot.Folders.GetValueOrDefault(GpoId)?.Status) + ")")]
            : [];
        var depth = Localizer[health.Depth == GpoDiagnosticsDepth.Full ? L.GpoDiagnostics.DepthFull : L.GpoDiagnostics.DepthQuick];
        HealthStatusText = Localizer.Format(L.GpoDiagnostics.LastRun, depth, health.At.ToString("g", Localizer.Culture)) + " – "
            + Correspondence.CorrespondenceText.Plural(Localizer, HealthFindings.Count, L.GpoDiagnostics.FindingsOne, L.GpoDiagnostics.FindingsFew,
                L.GpoDiagnostics.FindingsMany);
        HealthStatus.SetIdle(HealthStatusText);
        OnPropertyChanged(nameof(HasNoHealthFindings));
        RefreshCommands();
    }

    /// <summary>
    /// "Wyrównaj wersje" repairs it: AD and GPT.INI differ, or GPT.INI is missing in an existing folder (the alignment writes
    /// it again from the AD version). A GPT.INI that cannot be parsed is not offered: the GPO transaction refuses to replace it.
    /// </summary>
    private static bool NeedsVersionAlignment(GpoFinding finding) =>
        finding.Code == GpoFindingCodes.VersionMismatch
        || (finding.Code == GpoFindingCodes.SysvolFolderMissing && finding.Arguments is ["GptIni", ..]);

    /// <summary>One line per area: "Bez uwag", "Uwagi: 2, najpoważniejsza: wysoka", or why it was not checked.</summary>
    private List<GpoCheckRowViewModel> AreaChecks(HealthCheck health)
    {
        var checks = new List<GpoCheckRowViewModel>();
        foreach (var area in Enum.GetValues<GpoFindingArea>())
        {
            var name = GpoFindingText.Area(Localizer, area);
            var groups = GpoFindingCodes.All.Where(d => d.Area == area).Select(d => d.Group).Distinct().ToList();
            var skipped = groups.Where(health.Result.NotEvaluated.Contains).ToList();
            var reason = Localizer[health.SysvolWarning is not null && skipped.Any(g => g is GpoRuleGroup.SysvolVersions or GpoRuleGroup.Folders or GpoRuleGroup.Conflicts)
                ? L.GpoDiagnostics.ReasonSysvol
                : health.Depth == GpoDiagnosticsDepth.Quick ? L.GpoDiagnostics.ReasonQuickOnly : L.GpoDiagnostics.ReasonFailed];
            if (skipped.Count == groups.Count)
            {
                checks.Add(new GpoCheckRowViewModel(name, Localizer.Format(L.GpoDiagnostics.AreaNotChecked, reason), false));
                continue;
            }

            var findings = health.Result.Where(f => f.Area == area).ToList();
            var ok = findings.All(f => f.Severity == GpoFindingSeverity.Info);
            var detail = findings.Count == 0 ? Localizer[L.GpoDiagnostics.AreaOk]
                : ok ? Localizer.Format(L.GpoDiagnostics.AreaInfo, findings.Count)
                : Localizer.Format(L.GpoDiagnostics.AreaFindings, findings.Count,
                    GpoFindingText.Severity(Localizer, findings.Max(f => f.Severity)).ToLower(Localizer.Culture));
            checks.Add(new GpoCheckRowViewModel(name, skipped.Count == 0 ? detail : Localizer.Format(L.GpoDiagnostics.AreaPartly, detail, reason), ok));
        }

        return checks;
    }

    /// <summary>The findings are cleared when the window is not connected or the GPO is gone.</summary>
    private void ClearHealth()
    {
        _healthLoad.Cancel();
        _health = null;
        _healthCurrent = false;
        HealthFindings = [];
        HealthNotes = [];
        HealthStatusText = string.Empty;

        // Nothing of the tab is left to talk about: its state, its summary and what it said last go with the findings.
        HealthStatus.SetLasting(null);
        HealthStatus.SetIdle(null);
        HealthStatus.Clear();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _healthLoad.Cancel();
            HealthStatus.Operation?.Dispose();
            _health = null;
        }

        base.Dispose(disposing);
    }

    /// <summary>One check of this GPO (in memory only).</summary>
    private sealed record HealthCheck(
        GpoDiagnosticsSnapshot Snapshot,
        GpoDiagnosticsResult Result,
        IReadOnlyDictionary<string, string> Names,
        GpoDiagnosticsDepth Depth,
        DateTimeOffset At,
        string? SysvolWarning)
    {
        /// <summary>For diagnostics: counts only.</summary>
        public override string ToString() => $"{Depth}: {Result}";
    }
}
