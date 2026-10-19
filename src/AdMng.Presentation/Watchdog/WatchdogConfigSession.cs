using System.Collections.ObjectModel;
using System.Text.Json;
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
public sealed partial class WatchdogConfigSession : ObservableObject
{
    private readonly WatchdogServices _services;
    private string _loadedJson = string.Empty;

    public WatchdogConfigSession(WatchdogServices services)
    {
        _services = services ?? throw new ArgumentNullException(nameof(services));
    }

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

        IsBusy = true;
        Error = null;
        try
        {
            var result = await _services.Client.ValidateConfigAsync(working, CancellationToken.None).ConfigureAwait(true);
            SetIssues(result.Issues);
            Message = result.IsValid ? _services.Localizer[L.Watchdog.ConfigValid] : null;
            return result.IsValid;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            ReportFailure(ex);
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
            return true;
        }
        catch (WatchdogClientException ex) when (ex.Kind == WatchdogClientErrorKind.Validation)
        {
            SetIssues(ex.Issues);
            Error = _services.Localizer[L.Watchdog.ConfigInvalid];
            return false;
        }
        catch (WatchdogClientException ex) when (ex.Kind == WatchdogClientErrorKind.VersionConflict)
        {
            HasConflict = true;
            Error = _services.Localizer[L.Watchdog.ErrorVersionConflict];
            return false;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            ReportFailure(ex);
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
            return true;
        }
        catch (WatchdogClientException ex) when (ex.Kind == WatchdogClientErrorKind.Validation)
        {
            SetIssues(ex.Issues);
            Error = _services.Localizer[L.Watchdog.ConfigInvalid];
            return false;
        }
        catch (WatchdogClientException ex) when (ex.Kind == WatchdogClientErrorKind.VersionConflict)
        {
            HasConflict = true;
            Error = _services.Localizer[L.Watchdog.ErrorVersionConflict];
            return false;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            ReportFailure(ex);
            return false;
        }
        finally
        {
            IsBusy = false;
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
    }

    private async Task LoadCoreAsync()
    {
        IsBusy = true;
        Error = null;
        Message = null;
        HasConflict = false;
        try
        {
            var envelope = await _services.Client.GetConfigAsync(CancellationToken.None).ConfigureAwait(true);
            SetLoaded(envelope);
            SetIssues([]);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            ReportFailure(ex);
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <param name="envelope">The configuration as loaded or saved.</param>
    /// <param name="pending">Unsaved edits to keep as the working copy on top of <paramref name="envelope"/> (null: none).</param>
    private void SetLoaded(ConfigEnvelope envelope, WatchdogConfiguration? pending = null)
    {
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

    private static string Serialize(WatchdogConfiguration configuration) => JsonSerializer.Serialize(configuration, WatchdogJson.Options);
}
