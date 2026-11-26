using System.Collections.ObjectModel;
using System.Text.Json;
using AdMng.Presentation.Feedback;
using AdMng.Presentation.Localization;
using AdMng.Presentation.Services;
using AdMng.Watchdog.Client;
using AdMng.Watchdog.Contracts;
using AdMng.Watchdog.Contracts.Api;
using AdMng.Watchdog.Contracts.Configuration;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AdMng.Presentation.Watchdog;

/// <summary>
/// The runtime configuration being edited, shared by the Rules, Channels and Digest sub-tabs: the loaded
/// <see cref="ConfigEnvelope"/>, a working copy the editors change, the dirty flag (serialized JSON compared – records
/// with lists compare by reference), validation issues by field, save with optimistic concurrency (412 → "changed by
/// another administrator – reload") and revert. Secret values typed into the editors live only in the working copy and
/// are gone after a save or revert (the service returns IsSet/Hint only). Viewers get a redacted, read-only copy.
/// </summary>
/// <remarks>
/// What the configuration says in the status line (track V, stage V2b-4, unit 4): its version is the summary, a read, a
/// validation and a save are its operations (a read and a validation can be stopped, a save is a write and cannot), and what
/// lasts is computed in one place (<see cref="UpdateLasting"/>): the configuration could not be read or the read was stopped
/// while none is loaded, and the conflict of a save (someone else saved first). The holder is in front of the four tabs that
/// edit the configuration. The log copy of every message is a key, counts and a result code - never a value of the
/// configuration (CLAUDE.md rule 2: no secret reaches a text, a log copy or a <c>ToString()</c>).
/// </remarks>
public sealed partial class WatchdogConfigSession : ObservableObject
{
    private readonly WatchdogServices _services;
    private string _loadedJson = string.Empty;
    private Problem? _problem;
    private bool _stopped;

    public WatchdogConfigSession(WatchdogServices services)
    {
        _services = services ?? throw new ArgumentNullException(nameof(services));
        Status = services.CreateStatus();
    }

    /// <summary>What the configuration says in the status line: its version, its reads and saves, the states that last.</summary>
    public PageStatusHolder Status { get; }

    /// <summary>The working copy was replaced (loaded, saved, reverted): editors rebuild from it.</summary>
    public event EventHandler? Reloaded;

    /// <summary>The working copy changed through <see cref="Update"/>.</summary>
    public event EventHandler? Changed;

    /// <summary>The issues changed (editors refresh their field messages).</summary>
    public event EventHandler? IssuesChanged;

    /// <summary>A service call failed.</summary>
    public event EventHandler<Exception>? Failed;

    /// <summary>The configuration as loaded or last saved.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsLoaded), nameof(IsRedacted), nameof(VersionText))]
    public partial ConfigEnvelope? Envelope { get; private set; }

    /// <summary>The configuration the editors work on.</summary>
    public WatchdogConfiguration? Working { get; private set; }

    public bool IsLoaded => Envelope is not null;

    /// <summary>Viewer copy without exclusions, addresses and identities – never saved.</summary>
    public bool IsRedacted => Envelope?.IsRedacted == true;

    /// <summary>The caller has the Admin role (set by the tab from <c>GET /me</c>).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanEdit), nameof(ReadOnlyReason), nameof(IsReadOnly))]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand), nameof(ValidateCommand))]
    public partial bool IsAdmin { get; set; }

    /// <summary>Editors are enabled: Admin, loaded, not redacted.</summary>
    public bool CanEdit => IsAdmin && IsLoaded && !IsRedacted;

    /// <summary>A configuration is shown but cannot be edited (Viewer, redacted copy): the views say why.</summary>
    public bool IsReadOnly => IsLoaded && !CanEdit;

    /// <summary>Tool tip of the disabled editors ("Wymaga roli administratora Watchdog").</summary>
    public string? ReadOnlyReason => IsAdmin ? null : _services.Localizer[L.Watchdog.RequiresAdmin];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand), nameof(RevertCommand))]
    public partial bool IsDirty { get; private set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand), nameof(RevertCommand), nameof(ValidateCommand), nameof(ReloadCommand))]
    public partial bool IsBusy { get; private set; }

    /// <summary>Inline error of the last load or save.</summary>
    [ObservableProperty]
    public partial string? Error { get; private set; }

    /// <summary>Inline result of the last save or validation ("Zapisano wersję 8").</summary>
    [ObservableProperty]
    public partial string? Message { get; private set; }

    /// <summary>The last save was refused because someone else saved first (412).</summary>
    [ObservableProperty]
    public partial bool HasConflict { get; private set; }

    partial void OnHasConflictChanged(bool value) => UpdateLasting();

    partial void OnEnvelopeChanged(ConfigEnvelope? value) => UpdateIdle();

    /// <summary>"Wersja 7 · 14.10.2026 12:00 · CONTOSO\jan.kowalski".</summary>
    public string VersionText => Envelope is { } envelope
        ? _services.Localizer.Format(L.Watchdog.ConfigVersion, envelope.Version, _services.Localizer.FormatTime(envelope.UpdatedAt, _services.TimeZone), envelope.UpdatedBy ?? string.Empty)
        : string.Empty;

    public ObservableCollection<WatchdogIssueViewModel> Issues { get; } = [];

    public bool HasErrors => Issues.Any(i => i.IsError);

    /// <summary>The current load (tests await it).</summary>
    public Task Loading { get; private set; } = Task.CompletedTask;

    /// <summary>Loads the configuration (discarding unsaved changes). Never throws.</summary>
    public Task LoadAsync()
    {
        Loading = LoadCoreAsync();
        return Loading;
    }

    /// <summary>Changes the working copy (editors call this on every edit).</summary>
    public void Update(Func<WatchdogConfiguration, WatchdogConfiguration> change)
    {
        ArgumentNullException.ThrowIfNull(change);
        if (Working is not { } working || !CanEdit)
        {
            return;
        }

        Working = change(working);
        IsDirty = !string.Equals(Serialize(Working), _loadedJson, StringComparison.Ordinal);
        HasConflict = false;
        Message = null;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Issues under <paramref name="prefix"/> with their texts (for <see cref="WatchdogFieldErrors"/>).</summary>
    public IEnumerable<(ConfigIssue Issue, string Text)> IssuesFor(string prefix) =>
        Issues.Where(i => i.Path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).Select(i => (i.Issue, i.Text));

    /// <summary>Validates the working copy on the service (no side effects). Returns true when it has no errors.</summary>
    public async Task<bool> ValidateAsync()
    {
        if (Working is not { } working || !CanEdit)
        {
            return false;
        }

        var localizer = _services.Localizer;
        using var scope = Status.Begin(localizer[L.WatchdogConfigStatus.Validating], new PageStatusLog(L.WatchdogConfigStatus.Validating));
        IsBusy = true;
        Error = null;
        try
        {
            var result = await _services.Client.ValidateConfigAsync(working, scope.Token).ConfigureAwait(true);
            SetIssues(result.Issues);
            Message = result.IsValid ? localizer[L.Watchdog.ConfigValid] : null;
            if (!scope.IsEnded)
            {
                if (result.IsValid)
                {
                    scope.CompleteDone(localizer[L.Watchdog.ConfigValid], new PageStatusLog(L.Watchdog.ConfigValid));
                }
                else
                {
                    var errors = result.Issues.Count(i => i.Severity == IssueSeverity.Error);
                    scope.CompleteAttention(
                        localizer[L.WatchdogConfigStatus.Invalid],
                        new PageStatusLog(L.WatchdogConfigStatus.Invalid, errors, result.Issues.Count - errors));
                }
            }

            return result.IsValid;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException && scope.IsCancelling)
        {
            // The operator's Stop (a newer operation or the end of the tab end it without a word).
            var validate = localizer[L.WatchdogUi.Validate];
            scope.CompleteAttention(
                localizer.Format(L.WatchdogConfigStatus.ValidateStopped, validate),
                new PageStatusLog(L.WatchdogConfigStatus.ValidateStopped),
                new PageStatusAction(validate, ValidateCommand));
            return false;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException && scope.IsEnded)
        {
            return false;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            ReportFailure(ex);
            scope.CompleteError(
                PageStatusText.Failure(localizer[L.WatchdogConfigStatus.ValidateFailed], Error, localizer[L.Feedback.RetryNext], ResultCode.Of(ex)),
                new PageStatusLog(L.WatchdogConfigStatus.ValidateFailed).WithCode(ResultCode.Of(ex)),
                new PageStatusAction(localizer[L.Common.Retry], ValidateCommand));
            return false;
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanValidate))]
    private Task Validate() => ValidateAsync();

    private bool CanValidate() => CanEdit && !IsBusy;

    /// <summary>Saves the working copy (PUT with If-Match). Returns true when saved. A redacted copy is never sent.</summary>
    public async Task<bool> SaveAsync()
    {
        if (Working is not { } working || Envelope is not { } envelope || !CanEdit)
        {
            return false;
        }

        // A write: no Stop. Its token is never cancelled by a scope that was replaced or ended.
        using var scope = Status.Begin(_services.Localizer[L.WatchdogConfigStatus.Saving], new PageStatusLog(L.WatchdogConfigStatus.Saving), canCancel: false, writes: OperationWrites.Other);
        IsBusy = true;
        Error = null;
        Message = null;
        HasConflict = false;
        try
        {
            var saved = await _services.Client.SaveConfigAsync(envelope with { Configuration = working }, CancellationToken.None).ConfigureAwait(true);
            SetLoaded(saved);
            SetIssues([]);
            Message = _services.Localizer.Format(L.Watchdog.ConfigSaved, saved.Version);
            _services.Ui.Notifications.Show(NotificationSeverity.Success, Message);
            scope.CompleteDone(Message, new PageStatusLog(L.Watchdog.ConfigSaved, saved.Version));
            return true;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            ReportSaveFailure(scope, ex);
            return false;
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanSave))]
    private Task Save() => SaveAsync();

    /// <summary>
    /// Saves <paramref name="change"/> alone (PUT with If-Match): it is applied to the configuration as last loaded or
    /// saved, so unsaved edits of the working copy (and typed secrets) are not sent with it. They stay unsaved, with the
    /// same change applied, on top of the new version – a later save is a normal save of that version. Used by actions
    /// whose confirmation names only their own change ("Zakończ tryb wdrożenia"). Returns true when saved.
    /// </summary>
    public async Task<bool> SaveChangeAsync(Func<WatchdogConfiguration, WatchdogConfiguration> change)
    {
        ArgumentNullException.ThrowIfNull(change);
        if (Working is null || Envelope is not { } envelope || !CanEdit)
        {
            return false;
        }

        using var scope = Status.Begin(_services.Localizer[L.WatchdogConfigStatus.Saving], new PageStatusLog(L.WatchdogConfigStatus.Saving), canCancel: false, writes: OperationWrites.Other);
        IsBusy = true;
        Error = null;
        Message = null;
        HasConflict = false;
        try
        {
            var saved = await _services.Client.SaveConfigAsync(envelope with { Configuration = change(envelope.Configuration) }, CancellationToken.None).ConfigureAwait(true);

            // The working copy as it is now (edits made while the request ran included); unchanged → the saved version.
            var pending = Working is { } working && !string.Equals(Serialize(working), _loadedJson, StringComparison.Ordinal) ? change(working) : null;
            SetLoaded(saved, pending);
            if (pending is null)
            {
                SetIssues([]);
            }

            Message = _services.Localizer.Format(L.Watchdog.ConfigSaved, saved.Version);
            _services.Ui.Notifications.Show(NotificationSeverity.Success, Message);
            scope.CompleteDone(Message, new PageStatusLog(L.Watchdog.ConfigSaved, saved.Version));
            return true;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            ReportSaveFailure(scope, ex);
            return false;
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// A save that did not happen: the service refused the values (the findings are listed - nothing was saved), someone else
    /// saved first (the state that lasts, <see cref="UpdateLasting"/>) or the call failed.
    /// </summary>
    private void ReportSaveFailure(OperationScope scope, Exception ex)
    {
        var localizer = _services.Localizer;
        if (ex is WatchdogClientException { Kind: WatchdogClientErrorKind.Validation } validation)
        {
            SetIssues(validation.Issues);
            Error = localizer[L.Watchdog.ConfigInvalid];
            scope.CompleteAttention(Error, new PageStatusLog(L.Watchdog.ConfigInvalid, validation.Issues.Count));
        }
        else if (ex is WatchdogClientException { Kind: WatchdogClientErrorKind.VersionConflict })
        {
            // The state of the configuration, said until it is read again; the save itself ends without a word.
            HasConflict = true;
            Error = localizer[L.Watchdog.ErrorVersionConflict];
            scope.Complete(null);
        }
        else
        {
            ReportFailure(ex);
            scope.CompleteError(
                PageStatusText.Failure(localizer[L.WatchdogConfigStatus.SaveFailed], Error, null, ResultCode.Of(ex)),
                new PageStatusLog(L.WatchdogConfigStatus.SaveFailed).WithCode(ResultCode.Of(ex)));
        }
    }

    private bool CanSave() => CanEdit && IsDirty && !IsBusy;

    /// <summary>Discards the unsaved changes (and typed secrets).</summary>
    [RelayCommand(CanExecute = nameof(CanRevert))]
    public void Revert()
    {
        if (Envelope is { } envelope)
        {
            SetLoaded(envelope);
            SetIssues([]);
            Error = null;
            Message = null;
            HasConflict = false;
        }
    }

    private bool CanRevert() => IsDirty && !IsBusy;

    [RelayCommand(CanExecute = nameof(CanReload))]
    private Task ReloadAsync() => LoadAsync();

    private bool CanReload() => !IsBusy;

    /// <summary>Texts after a language switch.</summary>
    public void Relocalize()
    {
        SetIssues([.. Issues.Select(i => i.Issue)]);
        OnPropertyChanged(nameof(VersionText));
        OnPropertyChanged(nameof(ReadOnlyReason));
        UpdateIdle();
        UpdateLasting();
    }

    /// <summary>The tab was closed: what still runs ends without a word.</summary>
    public void Dispose() => Status.Operation?.Dispose();

    private async Task LoadCoreAsync()
    {
        var localizer = _services.Localizer;
        using var scope = Status.Begin(localizer[L.WatchdogConfigStatus.Loading], new PageStatusLog(L.WatchdogConfigStatus.Loading));
        IsBusy = true;
        Error = null;
        Message = null;
        HasConflict = false;
        _problem = null;
        _stopped = false;
        UpdateLasting();
        try
        {
            var envelope = await _services.Client.GetConfigAsync(scope.Token).ConfigureAwait(true);
            if (scope.IsEnded)
            {
                // Replaced by a newer read, or the tab was closed: the newer one owns the page.
                return;
            }

            SetLoaded(envelope);
            SetIssues([]);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException && scope.IsCancelling)
        {
            // The operator's Stop: what the tabs show may be older. With nothing loaded the tabs are empty - that is the
            // state of the configuration; with a configuration loaded it is a result that can be closed.
            if (IsLoaded)
            {
                var reload = localizer[L.WatchdogUi.Reload];
                scope.CompleteAttention(
                    localizer.Format(L.WatchdogConfigStatus.Stopped, reload),
                    new PageStatusLog(L.WatchdogConfigStatus.Stopped),
                    new PageStatusAction(reload, ReloadCommand));
            }
            else
            {
                _stopped = true;
                UpdateLasting();
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException && scope.IsEnded)
        {
            // Replaced or closed: nothing to say.
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            ReportFailure(ex);
            if (IsLoaded)
            {
                scope.CompleteError(
                    PageStatusText.Failure(localizer[L.WatchdogConfigStatus.ReadFailed], Error, localizer[L.Feedback.RetryNext], ResultCode.Of(ex)),
                    new PageStatusLog(L.WatchdogConfigStatus.ReadFailed).WithCode(ResultCode.Of(ex)),
                    new PageStatusAction(localizer[L.Common.Retry], ReloadCommand));
            }
            else
            {
                _problem = new Problem(Error ?? string.Empty, ResultCode.Of(ex));
                UpdateLasting();
            }
        }
        finally
        {
            if (!scope.IsEnded)
            {
                IsBusy = false;
            }
            else if (Status.Operation is null)
            {
                IsBusy = false;
            }
        }
    }

    /// <param name="envelope">The configuration as loaded or saved.</param>
    /// <param name="pending">Unsaved edits to keep as the working copy on top of <paramref name="envelope"/> (null: none).</param>
    private void SetLoaded(ConfigEnvelope envelope, WatchdogConfiguration? pending = null)
    {
        _problem = null;
        _stopped = false;
        Envelope = envelope;
        _loadedJson = Serialize(envelope.Configuration);
        Working = pending ?? envelope.Configuration;
        IsDirty = pending is not null && !string.Equals(Serialize(pending), _loadedJson, StringComparison.Ordinal);
        OnPropertyChanged(nameof(CanEdit));
        OnPropertyChanged(nameof(IsReadOnly));
        OnPropertyChanged(nameof(Working));
        SaveCommand.NotifyCanExecuteChanged();
        ValidateCommand.NotifyCanExecuteChanged();
        Reloaded?.Invoke(this, EventArgs.Empty);
    }

    private void SetIssues(IReadOnlyList<ConfigIssue> issues)
    {
        Issues.Clear();
        foreach (var issue in issues.OrderBy(i => i.Severity).ThenBy(i => i.Path, StringComparer.Ordinal))
        {
            Issues.Add(new WatchdogIssueViewModel(issue, _services.Localizer));
        }

        OnPropertyChanged(nameof(HasErrors));
        IssuesChanged?.Invoke(this, EventArgs.Empty);
    }

    private void ReportFailure(Exception ex)
    {
        Error = _services.Connection.Describe(ex);
        Failed?.Invoke(this, ex);
    }

    /// <summary>The summary: the version of the configuration, who saved it and when.</summary>
    private void UpdateIdle() => Status.SetIdle(VersionText);

    /// <summary>
    /// Says the state of the configuration that lasts, the most important first: no configuration could be read (with Retry),
    /// the read was stopped before one was loaded (with Reload), someone else saved first (with Reload). Computed anew
    /// whenever a condition changes, so it follows the language.
    /// </summary>
    private void UpdateLasting() => Status.SetLasting(LastingState());

    private PageStatus? LastingState()
    {
        var localizer = _services.Localizer;
        if (_problem is { } problem)
        {
            return PageStatus.Error(
                PageStatusText.Failure(localizer[L.WatchdogConfigStatus.ReadFailed], problem.Text, localizer[L.Feedback.RetryNext], problem.Code),
                new PageStatusLog(L.WatchdogConfigStatus.ReadFailed).WithCode(problem.Code),
                new PageStatusAction(localizer[L.Common.Retry], ReloadCommand));
        }

        if (_stopped)
        {
            var reload = localizer[L.WatchdogUi.Reload];
            return PageStatus.Attention(
                localizer.Format(L.WatchdogConfigStatus.Stopped, reload),
                new PageStatusLog(L.WatchdogConfigStatus.Stopped),
                new PageStatusAction(reload, ReloadCommand));
        }

        if (HasConflict)
        {
            return PageStatus.Attention(
                localizer[L.Watchdog.ErrorVersionConflict],
                new PageStatusLog(L.Watchdog.ErrorVersionConflict),
                new PageStatusAction(localizer[L.WatchdogUi.Reload], ReloadCommand));
        }

        return null;
    }

    /// <summary>Why the configuration could not be read: the sentence on screen and the result code of the failed call.</summary>
    private sealed record Problem(string Text, ResultCode Code)
    {
        /// <summary>The code only - never the text.</summary>
        public override string ToString() => $"{nameof(Problem)}({Code})";
    }

    private static string Serialize(WatchdogConfiguration configuration) => JsonSerializer.Serialize(configuration, WatchdogJson.Options);
}
