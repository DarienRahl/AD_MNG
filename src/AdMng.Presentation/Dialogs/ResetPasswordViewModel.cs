using System.Globalization;
using AdMng.Core.Batch;
using AdMng.Core.Directory;
using AdMng.Core.Security;
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
public sealed partial class ResetPasswordViewModel : WindowViewModelBase
{
    private readonly IChangePlanner _planner;
    private readonly BatchReportExporter _reports;
    private readonly IReadOnlyList<string> _targets;
    private BatchResult? _result;
    private bool _passwordsExported;

    public ResetPasswordViewModel(DialogServices services, IChangePlanner planner, BatchReportExporter reports, IReadOnlyList<string> distinguishedNames)
        : base(services)
    {
        _planner = planner ?? throw new ArgumentNullException(nameof(planner));
        _reports = reports ?? throw new ArgumentNullException(nameof(reports));
        _targets = distinguishedNames ?? throw new ArgumentNullException(nameof(distinguishedNames));
        GeneratePassword = _targets.Count > 1;
        Targets.ReplaceAll(_targets.Select(dn => DirectoryItem.Unresolved(dn, Localizer)));
    }

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

    /// <summary>Localized validation or execution error.</summary>
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

    public override async Task InitializeAsync()
    {
        var session = Services.Sessions.Current;
        if (session is null)
        {
            Error = Localizer[L.Errors.NotConnected];
            return;
        }

        try
        {
            var found = await Task.Run(() => DirectoryLookup.ReadManyAsync(session, _targets, DirectoryItem.Attributes, CancellationToken.None))
                .ConfigureAwait(true);
            Targets.ReplaceAll(_targets.Select(dn => found.TryGetValue(DnKey.For(dn), out var obj) ? DirectoryItem.From(obj, Localizer) : DirectoryItem.Unresolved(dn, Localizer)));
            IsAnyLocked = found.Values.Any(o => o.IsLockedOut);
            OnPropertyChanged(nameof(WindowTitle));
        }
        catch (DirectoryException ex)
        {
            Error = Errors.Translate(ex);
        }
    }

    [RelayCommand(CanExecute = nameof(CanReset))]
    private async Task ResetAsync()
    {
        var session = Services.Sessions.Current;
        if (session is null)
        {
            Error = Localizer[L.Errors.NotConnected];
            return;
        }

        Error = null;
        ResetPasswordOperation operation;
        if (IsSingle && !GeneratePassword)
        {
            var password = PasswordInput?.GetPassword() ?? string.Empty;
            if (password.Length == 0)
            {
                Error = Localizer[L.Batch.PasswordRequired];
                return;
            }

            if (!string.Equals(password, ConfirmPasswordInput?.GetPassword() ?? string.Empty, StringComparison.Ordinal))
            {
                Error = Localizer[L.Batch.PasswordMismatch];
                return;
            }

            operation = new ResetPasswordOperation(PasswordMode.Fixed, password, MustChangeAtNextLogon, UnlockAccount);
        }
        else
        {
            if (GeneratedLength is < PasswordGenerator.MinimumLength or > PasswordGenerator.MaximumLength)
            {
                Error = Localizer.Format(L.Batch.PasswordLength, PasswordGenerator.MinimumLength, PasswordGenerator.MaximumLength);
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
            var plan = await Task.Run(() => _planner.PlanAsync(session, request, options, null, CancellationToken.None)).ConfigureAwait(true);
            if (plan.ExecutableCount == 0)
            {
                Error = DescribeBlocked(plan, session);
                return;
            }

            var count = plan.ExecutableCount;
            if (count > 1)
            {
                var expected = count.ToString(CultureInfo.InvariantCulture);
                var message = Localizer.Format(L.ResetPassword.ConfirmMany, count, expected);
                if (!await Services.Dialogs.ConfirmTypedAsync(WindowTitle, message, expected).ConfigureAwait(true))
                {
                    return;
                }
            }

            var result = await Services.Coordinator.ExecutePlanAsync(plan, title).ConfigureAwait(true);
            PasswordInput?.Clear();
            ConfirmPasswordInput?.Clear();
            ShowResults(result, operation.Mode == PasswordMode.GenerateUnique);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Error = Errors.Translate(ex);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool CanReset() => !IsBusy && !IsCompleted && _targets.Count > 0;

    [RelayCommand]
    private void CopyPassword(PasswordResultViewModel? row)
    {
        if (row?.Password is { } password)
        {
            row.WasHandedOut = true;
            Services.Clipboard.SetText(password);
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

        var includePasswords = result.Plan.ContainsPasswords
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

    partial void OnGeneratePasswordChanged(bool value)
    {
        if (!value && !IsSingle)
        {
            GeneratePassword = true;
        }
    }
}
