using System.Globalization;
using AdMng.Core.Batch;
using AdMng.Core.Directory;
using AdMng.Core.Security;
using AdMng.Presentation.Changes;
using AdMng.Presentation.Feedback;
using AdMng.Presentation.Infrastructure;
using AdMng.Presentation.Lists;
using AdMng.Presentation.Localization;
using AdMng.Presentation.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AdMng.Presentation.Dialogs;

/// <summary>Outcome of the reset of one account (generated password shown only on request).</summary>
public sealed partial class PasswordResultViewModel : ObservableObject
{
    private readonly string? _password;

    internal PasswordResultViewModel(string name, string dn, bool succeeded, string message, string? password)
    {
        Name = name;
        Dn = dn;
        Succeeded = succeeded;
        Message = message;
        _password = password;
    }

    public string Name { get; }

    public string Dn { get; }

    public bool Succeeded { get; }

    /// <summary>Localized error (or empty).</summary>
    public string Message { get; }

    /// <summary>A generated password is available for this account.</summary>
    public bool HasPassword => _password is not null;

    /// <summary>Show the password in clear text (operator's explicit choice per row).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayedPassword))]
    public partial bool IsRevealed { get; set; }

    /// <summary>The password when revealed, otherwise a mask. Never logged.</summary>
    public string DisplayedPassword => _password is null ? string.Empty : IsRevealed ? _password : "••••••••";

    internal string? Password => _password;

    /// <summary>The operator revealed or copied the password (closing the window then does not ask).</summary>
    internal bool WasHandedOut { get; set; }

    partial void OnIsRevealedChanged(bool value) => WasHandedOut |= value;

    /// <summary>Never print the password.</summary>
    public override string ToString() => $"{Name} succeeded={Succeeded}";
}

/// <summary>
/// "Reset Password" for one or many accounts. One account: new password + confirmation (PasswordBoxes handed in
/// through <see cref="IPasswordInput"/>, never bound) or a generated password; several accounts: unique generated
/// passwords. Options: must change at next logon (default on), unlock the account. Runs through the change
/// pipeline (plan → typed confirmation for many accounts → background execution); the generated passwords are
/// shown per account on request and can be exported explicitly.
/// </summary>
/// <remarks>
/// The window has one status line (<see cref="Status"/>; track V, stage V2b, design note
/// docs/dev/v-interface-renewal.md "V2b-2 as built"). The names of the accounts may still be read when the operator
/// already resets, so there are two holders: the list of accounts says that it is read and that the read was stopped or
/// failed (<see cref="TargetsStatus"/>, the page in front); the window says the reset - checking the accounts, then the
/// execution of the plan (<see cref="ExecutionStatus"/>) - how it ended, afterwards the counts of the result list, and -
/// as the state that lasts - that it has no session. A password, typed or generated, is never part of what the line
/// says or of its log copy: it is shown where it always was - in the result row, on request.
/// </remarks>
public sealed partial class ResetPasswordViewModel : WindowViewModelBase, IHasPageStatus
{
    /// <summary>How many different reasons the status line says when no password can be reset.</summary>
    private const int MaxSaidReasons = 3;

    private readonly IChangePlanner _planner;
    private readonly BatchReportExporter _reports;
    private readonly IReadOnlyList<string> _targets;

    /// <summary>Reads the names of the accounts again (the Retry of a read that was stopped or failed).</summary>
    private readonly IAsyncRelayCommand _readAgainCommand;
    private BatchResult? _result;
    private bool _passwordsExported;

    public ResetPasswordViewModel(DialogServices services, IChangePlanner planner, BatchReportExporter reports, IReadOnlyList<string> distinguishedNames)
        : base(services)
    {
        _planner = planner ?? throw new ArgumentNullException(nameof(planner));
        _reports = reports ?? throw new ArgumentNullException(nameof(reports));
        _targets = distinguishedNames ?? throw new ArgumentNullException(nameof(distinguishedNames));
        Status = new PageStatusHolder(Localizer, services.Dispatcher, services.Lists.StatusLog);
        TargetsStatus = new PageStatusHolder(Localizer, services.Dispatcher, services.Lists.StatusLog);
        Status.Front = TargetsStatus;
        _readAgainCommand = new AsyncRelayCommand(ReadTargetsAsync);
        GeneratePassword = _targets.Count > 1;
        Targets.ReplaceAll(_targets.Select(dn => DirectoryItem.Unresolved(dn, Localizer)));
        UpdateLasting();
    }

    /// <summary>
    /// The status of the window - what its one status line shows: what the list of accounts says while nothing else is
    /// said, the reset (checking, then executing - with Stop) and how it ended, the counts of the results, and that the
    /// window has no session.
    /// </summary>
    public PageStatusHolder Status { get; }

    /// <summary>The status of the list of accounts: reading their names (with Stop), and a read that was stopped or failed.</summary>
    public PageStatusHolder TargetsStatus { get; }

    public override string WindowTitle => IsSingle
        ? Localizer.Format(L.ResetPassword.WindowTitleOne, Targets.FirstOrDefault()?.Name ?? string.Empty)
        : Localizer.Format(L.ResetPassword.WindowTitleMany, _targets.Count);

    public BulkObservableCollection<DirectoryItem> Targets { get; } = [];

    public bool IsSingle => _targets.Count == 1;

    /// <summary>Password box of the new password (set by the view; read only when resetting).</summary>
    public IPasswordInput? PasswordInput { get; set; }

    /// <summary>Confirmation password box (set by the view).</summary>
    public IPasswordInput? ConfirmPasswordInput { get; set; }

    /// <summary>Generate a random password (always for several accounts).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanTypePassword))]
    public partial bool GeneratePassword { get; set; }

    /// <summary>The password boxes are enabled (one account, not generated).</summary>
    public bool CanTypePassword => IsSingle && !GeneratePassword;

    /// <summary>Several accounts always get unique generated passwords.</summary>
    public bool CanChooseMode => IsSingle;

    [ObservableProperty]
    public partial int GeneratedLength { get; set; } = PasswordGenerator.DefaultLength;

    [ObservableProperty]
    public partial bool MustChangeAtNextLogon { get; set; } = true;

    [ObservableProperty]
    public partial bool UnlockAccount { get; set; } = true;

    /// <summary>Some of the accounts are locked out (the unlock option is relevant).</summary>
    [ObservableProperty]
    public partial bool IsAnyLocked { get; private set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ResetCommand), nameof(CancelCommand))]
    public partial bool IsBusy { get; private set; }

    /// <summary>
    /// Localized validation or execution error - what the status line says, as plain text (kept for callers that read
    /// it).
    /// </summary>
    [ObservableProperty]
    public partial string? Error { get; private set; }

    /// <summary>The reset ran; <see cref="Results"/> are shown.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ResetCommand), nameof(ExportCommand))]
    public partial bool IsCompleted { get; private set; }

    public BulkObservableCollection<PasswordResultViewModel> Results { get; } = [];

    [ObservableProperty]
    public partial string ResultSummary { get; private set; } = string.Empty;

    /// <summary>Generated passwords that were neither shown, copied nor exported (closing asks first).</summary>
    public bool HasUnsavedGeneratedPasswords => !_passwordsExported && Results.Any(r => r.HasPassword && !r.WasHandedOut);

    /// <summary>Generated passwords exist only in this window: closing without taking them leaves unknown passwords.</summary>
    public override async Task<bool> CanCloseAsync() =>
        !IsBusy
        && (!HasUnsavedGeneratedPasswords
            || await Services.Dialogs.ConfirmAsync(WindowTitle, Localizer[L.Batch.DiscardPasswords], destructive: true).ConfigureAwait(true));

    public override Task InitializeAsync() => ReadTargetsAsync();

    /// <summary>
    /// Reads the names, the icons and the lockout state of the accounts - the operation of the list
    /// (<see cref="TargetsStatus"/>), with Stop. The list shows the names of the distinguished names until it is through,
    /// and keeps them when the read was stopped or failed: a result that can be closed, with Retry. Never throws.
    /// </summary>
    private async Task ReadTargetsAsync()
    {
        var session = Session;
        if (session is null)
        {
            Error = NoSessionMessage;
            UpdateLasting();
            return;
        }

        using var reading = TargetsStatus.Begin(
            Localizer.Format(L.ResetPasswordStatus.Reading, _targets.Count), new PageStatusLog(L.ResetPasswordStatus.Reading, _targets.Count));
        var token = reading.Token;
        try
        {
            var found = await Task.Run(() => DirectoryLookup.ReadManyAsync(session, _targets, DirectoryItem.Attributes, token), token)
                .ConfigureAwait(true);
            if (reading.IsEnded)
            {
                // The window was closed, or its session ended.
                return;
            }

            Targets.ReplaceAll(_targets.Select(dn => found.TryGetValue(DnKey.For(dn), out var obj) ? DirectoryItem.From(obj, Localizer) : DirectoryItem.Unresolved(dn, Localizer)));
            IsAnyLocked = found.Values.Any(o => o.IsLockedOut);
            OnPropertyChanged(nameof(WindowTitle));
        }
        catch (OperationCanceledException)
        {
            // Only a read the operator stopped says so.
            if (reading.IsCancelling)
            {
                reading.CompleteAttention(
                    Localizer[L.ResetPasswordStatus.ReadStopped],
                    new PageStatusLog(L.ResetPasswordStatus.ReadStopped),
                    new PageStatusAction(Localizer[L.Common.Retry], _readAgainCommand));
            }
        }
        catch (DirectoryException ex)
        {
            if (reading.IsEnded)
            {
                return;
            }

            Error = Errors.Translate(ex);
            var code = ResultCode.Of(ex);
            reading.CompleteError(
                PageStatusText.Failure(Localizer[L.ResetPasswordStatus.ReadFailed], Error, Localizer[L.Feedback.RetryNext], code),
                new PageStatusLog(L.ResetPasswordStatus.ReadFailed).WithCode(code),
                new PageStatusAction(Localizer[L.Common.Retry], _readAgainCommand));
        }
    }

    /// <summary>
    /// Resets the passwords through the change pipeline. The reset is the operation of the window: its status line
    /// says "Checking the accounts…" while the plan is made and then follows the execution of the plan
    /// (<see cref="ExecutionStatus"/>), both with Stop - the OK button is their Cancel as well - and says how it ended,
    /// with the domain. What was not started - a password is missing, no account can be reset - is "No password was
    /// reset: why". The password itself never leaves the plan.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanReset))]
    private async Task ResetAsync()
    {
        var session = Session;
        if (session is null)
        {
            Error = NoSessionMessage;
            UpdateLasting();
            return;
        }

        Error = null;
        ResetPasswordOperation operation;
        if (IsSingle && !GeneratePassword)
        {
            var password = PasswordInput?.GetPassword() ?? string.Empty;
            if (password.Length == 0)
            {
                NotReset(L.Batch.PasswordRequired, Localizer[L.Batch.PasswordRequired]);
                return;
            }

            if (!string.Equals(password, ConfirmPasswordInput?.GetPassword() ?? string.Empty, StringComparison.Ordinal))
            {
                NotReset(L.Batch.PasswordMismatch, Localizer[L.Batch.PasswordMismatch]);
                return;
            }

            operation = new ResetPasswordOperation(PasswordMode.Fixed, password, MustChangeAtNextLogon, UnlockAccount);
        }
        else
        {
            if (GeneratedLength is < PasswordGenerator.MinimumLength or > PasswordGenerator.MaximumLength)
            {
                NotReset(L.Batch.PasswordLength, Localizer.Format(L.Batch.PasswordLength, PasswordGenerator.MinimumLength, PasswordGenerator.MaximumLength));
                return;
            }

            operation = new ResetPasswordOperation(PasswordMode.GenerateUnique, null, MustChangeAtNextLogon, UnlockAccount, GeneratedLength);
        }

        IsBusy = true;
        try
        {
            var title = Localizer[L.Commands.ResetPassword].TrimEnd('.', '…');
            var request = new BatchRequest(_targets, [operation]) { Title = title };
            var options = new PlanningOptions { MaxDegreeOfParallelism = Services.Settings.Current.BatchMaxParallelism };

            // Checking - planning, which reads every account - is the first part: nothing is written yet, so Stop ends
            // it with "no password was reset".
            using var checking = Status.Begin(
                Localizer.Format(L.ResetPasswordStatus.Checking, _targets.Count), new PageStatusLog(L.ResetPasswordStatus.Checking, _targets.Count));
            var token = checking.Token;
            ChangePlan plan;
            try
            {
                plan = await Task.Run(() => _planner.PlanAsync(session, request, options, null, token), token).ConfigureAwait(true);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                // Only a check the operator stopped says so (the window that closes ends it without a word).
                if (checking.IsCancelling)
                {
                    CheckStopped(checking);
                }

                return;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                Error = Errors.Translate(ex);
                var code = ResultCode.Of(ex);
                checking.CompleteError(
                    PageStatusText.Failure(Localizer[L.ResetPasswordStatus.CheckFailed], Error, null, code),
                    new PageStatusLog(L.ResetPasswordStatus.CheckFailed).WithCode(code));
                return;
            }

            if (checking.IsEnded)
            {
                // The window was closed meanwhile: nothing is executed for a window that is gone.
                return;
            }

            if (checking.IsCancelling)
            {
                // Stop was pressed while the plan was made, and the planner finished all the same: the operator said
                // stop, so nothing is executed.
                CheckStopped(checking);
                return;
            }

            if (plan.ExecutableCount == 0)
            {
                Error = DescribeBlocked(plan, session);
                checking.CompleteAttention(
                    PageStatusText.Failure(Localizer[L.ResetPasswordStatus.NotReset], BlockedReasons(plan, session)),
                    new PageStatusLog(L.ResetPassword.NothingToReset));
                return;
            }

            var count = plan.ExecutableCount;
            if (count > 1)
            {
                // The check is over; the line is at rest while the confirmation is asked.
                checking.Dispose();
                var expected = count.ToString(CultureInfo.InvariantCulture);
                var message = Localizer.Format(L.ResetPassword.ConfirmMany, count, expected, Connections.ConfirmationText.Domain(session));
                if (!await Services.Dialogs.ConfirmTypedAsync(WindowTitle, message, expected).ConfigureAwait(true))
                {
                    return;
                }
            }

            // The reset itself is the execution of the plan: how far it is, Stop (which cancels the activity) and how it
            // ended - with the domain - are said like in every window that executes a plan. It takes the line from the
            // check without a pause in between.
            using var execution = ExecutionStatus.Begin(Status, Localizer, title, ConfirmationDomain, plan.Objects.Count);
            BatchResult result;
            try
            {
                result = await Services.Coordinator.ExecutePlanAsync(plan, title, session, execution.Follow).ConfigureAwait(true);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                Error = Errors.Translate(ex);
                execution.Fail(Error, ResultCode.Of(ex));
                return;
            }

            PasswordInput?.Clear();
            ConfirmPasswordInput?.Clear();
            ShowResults(result, operation.Mode == PasswordMode.GenerateUnique);

            // Where the reasons are - said only when the result list has one (an account that a Stop left half done has none).
            execution.Complete(result, Results.Any(r => !r.Succeeded && r.Message.Length > 0) ? Localizer[L.ChangeStatus.ReasonsNext] : null);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Neither the check nor the execution - they say themselves how they ended: the confirmation could not be
            // asked. Nothing was reset.
            Error = Errors.Translate(ex);
            var code = ResultCode.Of(ex);
            Status.Show(PageStatus.Error(
                PageStatusText.Failure(Localizer[L.ResetPasswordStatus.NotReset], Error, null, code),
                new PageStatusLog(L.ResetPasswordStatus.NotReset).WithCode(code)));
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool CanReset() => !IsBusy && !IsCompleted && _targets.Count > 0 && !IsSessionEnded;

    protected override void OnSessionEnded()
    {
        Error = SessionEndedMessage;

        // A read of the names that still runs ends without a word; a reset that runs still says how it ended. The line
        // says for good that the session of this window ended.
        TargetsStatus.Operation?.Dispose();
        UpdateLasting();
        ResetCommand.NotifyCanExecuteChanged();
    }

    /// <summary>The window's own sentence in the new language.</summary>
    protected override void OnCultureChanged()
    {
        base.OnCultureChanged();
        UpdateLasting();
    }

    /// <summary>
    /// The state of the window that lasts: its session ended, or it was opened without one. An error that cannot be
    /// closed - no password can be reset here.
    /// </summary>
    private void UpdateLasting() => Status.SetLasting(
        SessionEndedStatus ?? (Session is null ? PageStatus.Error(NoSessionMessage, new PageStatusLog(L.Errors.NotConnected), canClose: false) : null));

    /// <summary>"Checking the accounts was stopped: no password was reset. Press Retry to try again." - with Retry, which resets again.</summary>
    private void CheckStopped(OperationScope checking) => checking.CompleteAttention(
        PageStatusText.Failure(
            Localizer[L.ResetPasswordStatus.CheckStopped], Localizer[L.ResetPasswordStatus.CheckStoppedReason], Localizer[L.Feedback.RetryNext]),
        new PageStatusLog(L.ResetPasswordStatus.CheckStopped),
        new PageStatusAction(Localizer[L.Common.Retry], ResetCommand));

    /// <summary>
    /// Nothing was started: "No password was reset: The passwords do not match." - an attention the operator can close;
    /// the log copy is the key of the reason.
    /// </summary>
    private void NotReset(string reasonKey, string reason)
    {
        Error = reason;
        Status.Show(PageStatus.Attention(PageStatusText.Failure(Localizer[L.ResetPasswordStatus.NotReset], reason), new PageStatusLog(reasonKey)));
    }

    [RelayCommand]
    private void CopyPassword(PasswordResultViewModel? row)
    {
        // Kept out of clipboard history and cloud clipboard, cleared after a while. When the clipboard was busy the
        // password was not handed out (the clipboard service warns the operator).
        if (row?.Password is { } password && Services.Clipboard.SetSecret(password))
        {
            row.WasHandedOut = true;
            Services.Notifications.Show(NotificationSeverity.Info, Localizer[L.ResetPassword.PasswordCopied]);
        }
    }

    /// <summary>Exports the results including the generated passwords after an explicit warning.</summary>
    [RelayCommand(CanExecute = nameof(CanExport))]
    private async Task ExportAsync()
    {
        if (_result is not { } result)
        {
            return;
        }

        // Only the passwords of the resets that succeeded are exported: nothing to ask when none did.
        var includePasswords = BatchReportExporter.ContainsSecrets(result)
            && await Services.Dialogs.ConfirmAsync(Localizer[L.Batch.ExportPasswordsTitle], Localizer[L.Batch.ExportPasswordsWarning], destructive: true)
                .ConfigureAwait(true);
        var title = Localizer[L.Batch.ExportResults];
        var path = await Services.Files.SaveFileAsync(title, $"password-reset-{Services.Time.GetLocalNow():yyyyMMdd-HHmm}.csv", [new FileFilter(Localizer[L.Files.CsvFilter], "*.csv")])
            .ConfigureAwait(true);
        if (path is null)
        {
            return;
        }

        try
        {
            var delimiter = CsvFiles.ListSeparator(Services.Regional.Culture);
            await CsvFiles.WriteAsync(path, writer => _reports.Write(writer, result, includePasswords, delimiter)).ConfigureAwait(true);
            _passwordsExported |= includePasswords;
            Services.Notifications.Show(NotificationSeverity.Success, Localizer.Format(L.Batch.Exported, Path.GetFileName(path)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            await Services.Dialogs.ShowErrorAsync(title, Errors.Translate(ex)).ConfigureAwait(true);
        }
    }

    private bool CanExport() => IsCompleted;

    [RelayCommand(CanExecute = nameof(CanCancel))]
    private async Task CancelAsync()
    {
        if (await CanCloseAsync().ConfigureAwait(true))
        {
            RequestClose(IsCompleted);
        }
    }

    private bool CanCancel() => !IsBusy;

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            // Generated passwords live only as long as the window.
            Results.ReplaceAll([]);
            _result = null;

            // What still runs says nothing any more. A reset that is being executed goes on as an activity of the
            // connection: ending its operation does not cancel it.
            Status.Operation?.Dispose();
            Status.Front = null;
            TargetsStatus.Operation?.Dispose();
        }
    }

    private void ShowResults(BatchResult result, bool generated)
    {
        _result = result;
        Results.ReplaceAll(result.Objects.Select(o =>
        {
            var failed = o.Changes.FirstOrDefault(c => !c.Succeeded);
            var message = failed is not null ? Errors.TranslateFailure(failed.ErrorKind, failed.ErrorMessage)
                : o.Status == ObjectResultStatus.Skipped ? string.Join(" ", o.Plan.Issues.Concat(o.Plan.Changes.SelectMany(c => c.Issues))
                    .Where(i => i.Severity != IssueSeverity.Info).Select(i => Errors.TranslateIssue(i)))
                : string.Empty;
            var password = generated
                ? o.Changes.FirstOrDefault(c => c.Succeeded && c.Change.Action == ChangeAction.ResetPassword)?.Change.Password
                : null;
            return new PasswordResultViewModel(o.Plan.DisplayName, o.FinalDn, o.Status == ObjectResultStatus.Succeeded, message, password);
        }));
        ResultSummary = Localizer.Format(L.Batch.ResultSummary, result.SucceededCount, result.FailedCount, result.SkippedCount);

        // The counts of the result list are the summary of the window from now on (behind how the reset ended).
        Status.SetIdle(ResultSummary);
        IsCompleted = true;
        if (result.FailedCount == 0 && !generated)
        {
            Services.Notifications.Show(NotificationSeverity.Success, Localizer.Format(L.ResetPassword.Done, result.SucceededCount));
        }
    }

    private string DescribeBlocked(ChangePlan plan, IDirectorySession session)
    {
        var issues = plan.Objects
            .SelectMany(o => o.Issues.Concat(o.Changes.SelectMany(c => c.Issues)).Select(i => (o.DisplayName, Issue: i)))
            .Where(x => x.Issue.Severity != IssueSeverity.Info)
            .Select(x => $"{x.DisplayName}: {Errors.TranslateIssue(x.Issue, session.Schema)}")
            .Distinct(StringComparer.Ordinal)
            .Take(10);
        return string.Join(Environment.NewLine, [Localizer[L.ResetPassword.NothingToReset], .. issues]);
    }

    /// <summary>Why no account can be reset, for the status line: every reason once, three at most, as one flowing text.</summary>
    private string BlockedReasons(ChangePlan plan, IDirectorySession session) => string.Join(" ", plan.Objects
        .SelectMany(o => o.Issues.Concat(o.Changes.SelectMany(c => c.Issues)))
        .Where(i => i.Severity != IssueSeverity.Info)
        .Select(i => Errors.TranslateIssue(i, session.Schema))
        .Distinct(StringComparer.Ordinal)
        .Take(MaxSaidReasons));

    partial void OnGeneratePasswordChanged(bool value)
    {
        if (!value && !IsSingle)
        {
            GeneratePassword = true;
        }
    }
}
