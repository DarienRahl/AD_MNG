using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.Json;
using AdMng.Core.GroupPolicy;
using AdMng.Core.GroupPolicy.AdministrativeTemplates;
using AdMng.Core.GroupPolicy.Management;
using AdMng.Presentation.Dialogs;
using AdMng.Presentation.Infrastructure;
using AdMng.Presentation.Localization;
using AdMng.Presentation.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AdMng.Presentation.GroupPolicy.AdministrativeTemplates;

/// <summary>A language of the package the operator can copy.</summary>
public sealed partial class CultureChoiceViewModel(string culture, string displayName, bool selected) : ObservableObject
{
    /// <summary>The culture folder of the package.</summary>
    public string Culture { get; } = culture;

    /// <summary>"polski (pl-PL)".</summary>
    public string DisplayName { get; } = displayName;

    [ObservableProperty]
    public partial bool IsSelected { get; set; } = selected;
}

/// <summary>A file of the update.</summary>
public sealed class CentralStoreRowViewModel(CentralStoreFileChange change, string statusText, string detailText)
{
    public CentralStoreFileChange Change { get; } = change;

    public string Path => Change.RelativePath;

    public CentralStoreFileStatus Status => Change.Status;

    /// <summary>"Nowy", "Zmieniony", "Bez zmian", "Kolizja przestrzeni nazw" …</summary>
    public string StatusText { get; } = statusText;

    /// <summary>"+3 / −1 ustawień", "jak msedge-old.admx" …</summary>
    public string DetailText { get; } = detailText;

    /// <summary>Written or deleted by the update.</summary>
    public bool IsChange => Change.IsWritten || Change.Status is CentralStoreFileStatus.Removed or CentralStoreFileStatus.Leftover;
}

/// <summary>
/// "Zaktualizuj szablony ADMX" (K3, design note docs/dev/k-computers-and-gpo.md 6, the sources decided with the operator):
/// a package from a file (ZIP, CAB, MSI, EXE, one ADMX with its language files), a folder, the templates of this computer,
/// or a download (Edge, Chrome, Firefox); its trust (signature, pinned address) and the files it skips; the languages to
/// copy; the comparison with the central store of the session's DC (new, changed, unchanged, colliding namespaces,
/// unreadable files, the policies a template adds or drops); a backup of the store to a folder the operator picks; then one
/// plan through the change plan preview. After an update the editors read the templates again.
/// </summary>
/// <remarks>
/// Comparisons are serialized: every change of the package, a language or "Zastąp stare szablony" starts a new one and
/// cancels the one running; only the result of the latest comparison of the current package is shown and updated, and
/// "Zaktualizuj…" waits for it. The backup runs off the UI thread while the window is busy; the window cannot be closed
/// during the update, and nothing is planned once it was closed.
/// </remarks>
public sealed partial class CentralStoreUpdateViewModel : WindowViewModelBase
{
    private readonly GroupPolicyServices _gp;
    private readonly PolicyDefinitionsService _definitions;
    private readonly PolicyTemplatePackageLoader _loader;
    private readonly SerialCancellation _work = new();
    private readonly SerialCancellation _compare = new();
    private CentralStoreComparison? _comparison;

    public CentralStoreUpdateViewModel(GroupPolicyServices services, PolicyDefinitionsService definitions, PolicyTemplatePackageLoader loader)
        : base((services ?? throw new ArgumentNullException(nameof(services))).Ui)
    {
        _gp = services;
        _definitions = definitions ?? throw new ArgumentNullException(nameof(definitions));
        _loader = loader ?? throw new ArgumentNullException(nameof(loader));
        BackupFolder = services.Settings.Current.GroupPolicy.LastBackupFolder ?? string.Empty;
    }

    public override string WindowTitle => Localizer[L.CentralStore.Title];

    /// <summary>"Magazyn centralny: \\dc1\SYSVOL\contoso.test\Policies\PolicyDefinitions".</summary>
    public string TargetText => _gp.Sessions.Current is { } session
        ? Localizer.Format(L.CentralStore.Target, $@"\\{session.Info.Server}\SYSVOL\{session.Info.DomainDnsName}\Policies\{SysvolPaths.PolicyDefinitionsFolder}")
        : Localizer[L.Errors.NotConnected];

    /// <summary>MSI, CAB and EXE packages can be unpacked here (Windows).</summary>
    public bool CanExtractInstallers => _loader.CanExtractInstallers;

    /// <summary>Writes the backup of the store (folder, DC, files, time); replaced in tests.</summary>
    internal Func<string, string, IReadOnlyDictionary<string, byte[]>, DateTimeOffset, CancellationToken, Task<string>> WriteBackup { get; init; } =
        CentralStoreBackup.WriteAsync;

    /// <summary>The loaded package (null: none yet).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPackage), nameof(PackageText), nameof(TrustText), nameof(IsUntrusted), nameof(PackageIssues), nameof(HasPackageIssues),
        nameof(PackageIssuesTitle))]
    [NotifyCanExecuteChangedFor(nameof(UpdateCommand))]
    public partial PolicyTemplatePackage? Package { get; private set; }

    public bool HasPackage => Package is not null;

    /// <summary>"policy_templates.zip – szablony: 2, języki: 54".</summary>
    public string PackageText => Package is { } package
        ? Localizer.Format(L.CentralStore.PackageSummary, package.Description, package.AdmxFiles.Count, package.Cultures.Count)
        : Localizer[L.CentralStore.NoPackage];

    /// <summary>"Podpisany: Microsoft Corporation" / "Pobrany z adresu producenta (dl.google.com) – producent nie podpisuje archiwum" …</summary>
    public string TrustText => Package?.Trust switch
    {
        PackageTrust.Signed => Localizer.Format(L.CentralStore.TrustSigned, Package.Signer ?? string.Empty),
        PackageTrust.PinnedDownload => Localizer.Format(L.CentralStore.TrustPinned, Package.Signer ?? string.Empty),
        PackageTrust.Unsigned => Localizer[L.CentralStore.TrustUnsigned],
        PackageTrust.NotChecked => Localizer[L.CentralStore.TrustNotChecked],
        _ => string.Empty,
    };

    /// <summary>The package is not vouched for by a signature or the vendor's address (shown as a warning).</summary>
    public bool IsUntrusted => Package is { Trust: PackageTrust.Unsigned or PackageTrust.NotChecked };

    /// <summary>
    /// The files of the package that are not used ("Archiwum/chrome.admx – szablon o tej nazwie znaleziono w paczce
    /// wcześniej …"): a second template of a name, a file too large. Shown so a skipped copy never goes unnoticed.
    /// </summary>
    public IReadOnlyList<string> PackageIssues => Package is { } package ? [.. package.Issues.Select(IssueText)] : [];

    public bool HasPackageIssues => Package is { Issues.Count: > 0 };

    /// <summary>"Pominięte pliki paczki (2)".</summary>
    public string PackageIssuesTitle => Localizer.Format(L.CentralStore.PackageIssues, Package?.Issues.Count ?? 0);

    public ObservableCollection<CultureChoiceViewModel> Cultures { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasRows))]
    public partial IReadOnlyList<CentralStoreRowViewModel> Rows { get; private set; } = [];

    public bool HasRows => Rows.Count > 0;

    /// <summary>"Do zapisania: 5, bez zmian: 12, pominięte: 1".</summary>
    [ObservableProperty]
    public partial string SummaryText { get; private set; } = string.Empty;

    /// <summary>"Magazyn centralny nie istnieje – aktualizacja go utworzy." (null when it exists or nothing was compared).</summary>
    [ObservableProperty]
    public partial string? StoreNote { get; private set; }

    /// <summary>Replace an old ADMX that declares the namespace of a new one (deleted with its language files).</summary>
    [ObservableProperty]
    public partial bool ReplaceCollidingFiles { get; set; }

    /// <summary>Copy the store to <see cref="BackupFolder"/> before the update (recommended).</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(UpdateCommand))]
    public partial bool MakeBackup { get; set; } = true;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(UpdateCommand))]
    public partial string BackupFolder { get; set; }

    /// <summary>A package is being loaded or downloaded, or the store is being updated: the sources and the options wait.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsWorking), nameof(ProgressText))]
    [NotifyCanExecuteChangedFor(nameof(UpdateCommand), nameof(OpenFileCommand), nameof(OpenFolderCommand), nameof(UseLocalStoreCommand),
        nameof(DownloadEdgeCommand), nameof(DownloadChromeCommand), nameof(DownloadFirefoxCommand))]
    public partial bool IsBusy { get; private set; }

    /// <summary>The package is being compared with the store: "Zaktualizuj…" waits for the result.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsWorking), nameof(ProgressText))]
    [NotifyCanExecuteChangedFor(nameof(UpdateCommand))]
    public partial bool IsComparing { get; private set; }

    /// <summary>The backup, the plan and the preview of "Zaktualizuj…" run: the window cannot be closed.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CloseCommand))]
    public partial bool IsUpdating { get; private set; }

    /// <summary>Something runs (the progress indicator).</summary>
    public bool IsWorking => IsBusy || IsComparing;

    /// <summary>"Pobieranie szablonów Microsoft Edge…" while busy.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ProgressText))]
    public partial string? BusyText { get; private set; }

    /// <summary>What runs now ("Pobieranie…", "Tworzenie kopii zapasowej…", "Porównywanie z magazynem centralnym…"), else null.</summary>
    public string? ProgressText => BusyText ?? (IsComparing ? Localizer[L.CentralStore.Comparing] : null);

    /// <summary>The last failure (localized), null when none.</summary>
    [ObservableProperty]
    public partial string? Error { get; private set; }

    /// <summary>The central store was changed through this window (the editors read the templates again).</summary>
    public bool ChangedStore { get; private set; }

    /// <summary>"Magazyn centralny zaktualizowany: zapisano 5 plików." after an update.</summary>
    [ObservableProperty]
    public partial string? ResultText { get; private set; }

    // ---- Sources -------------------------------------------------------------------------------------------------------

    [RelayCommand(CanExecute = nameof(IsIdle))]
    private async Task OpenFileAsync()
    {
        var file = await Services.Files.OpenFileAsync(Localizer[L.CentralStore.OpenFileTitle],
            [new FileFilter(Localizer[L.CentralStore.FileFilter], string.Join(';', PolicyTemplatePackageLoader.Extensions.Select(e => "*" + e)))]).ConfigureAwait(true);
        if (file is not null)
        {
            await LoadAsync(Localizer[L.CentralStore.Loading], token => _loader.LoadFileAsync(file, token)).ConfigureAwait(true);
        }
    }

    [RelayCommand(CanExecute = nameof(IsIdle))]
    private async Task OpenFolderAsync()
    {
        var folder = await _gp.Dialogs.PickFolderAsync(Localizer[L.CentralStore.OpenFolderTitle], null).ConfigureAwait(true);
        if (folder is not null)
        {
            await LoadAsync(Localizer[L.CentralStore.Loading], token => _loader.LoadFolderAsync(folder, token)).ConfigureAwait(true);
        }
    }

    /// <summary>"Szablony tego komputera": creates (or refreshes) the central store from %WINDIR%\PolicyDefinitions.</summary>
    [RelayCommand(CanExecute = nameof(IsIdle))]
    private Task UseLocalStoreAsync() => LoadAsync(Localizer[L.CentralStore.Loading], _ => Task.FromResult(_loader.LocalStore()
        ?? throw new PolicyTemplatePackageException(PolicyTemplatePackageError.NotFound, "This computer has no templates.")));

    [RelayCommand(CanExecute = nameof(IsIdle))]
    private Task DownloadEdgeAsync() => DownloadAsync(TemplateVendor.MicrosoftEdge);

    [RelayCommand(CanExecute = nameof(IsIdle))]
    private Task DownloadChromeAsync() => DownloadAsync(TemplateVendor.GoogleChrome);

    [RelayCommand(CanExecute = nameof(IsIdle))]
    private Task DownloadFirefoxAsync() => DownloadAsync(TemplateVendor.MozillaFirefox);

    [RelayCommand]
    private async Task ChooseBackupFolderAsync()
    {
        if (await _gp.Dialogs.PickFolderAsync(Localizer[L.CentralStore.BackupFolderTitle], BackupFolder).ConfigureAwait(true) is { } folder)
        {
            BackupFolder = folder;
        }
    }

    private Task DownloadAsync(TemplateVendor vendor) => LoadAsync(Localizer.Format(L.CentralStore.Downloading, VendorName(vendor)),
        token => _loader.DownloadAsync(vendor, token));

    private async Task LoadAsync(string busy, Func<CancellationToken, Task<PolicyTemplatePackage>> load)
    {
        var token = _work.Next();
        IsBusy = true;
        BusyText = busy;
        Error = null;
        ResultText = null;
        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, _gp.Lifetime);
            var package = await Task.Run(() => load(linked.Token), linked.Token).ConfigureAwait(true);
            linked.Token.ThrowIfCancellationRequested(); // the window closed meanwhile
            // The languages the store already has are chosen too: a replaced template's language files would otherwise stay
            // at the old revision there.
            var storeCultures = await StoreCulturesAsync(linked.Token).ConfigureAwait(true);
            linked.Token.ThrowIfCancellationRequested();
            Package = package;
            Cultures.Clear();
            var preferred = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { PolicyDefinitionLoader.DefaultCulture, Localizer.Culture.Name, Localizer.Culture.TwoLetterISOLanguageName };
            foreach (var culture in package.Cultures)
            {
                var inStore = storeCultures.Contains(GroupPolicyPlanner.TargetCulture(culture, storeCultures), StringComparer.OrdinalIgnoreCase);
                var choice = new CultureChoiceViewModel(culture, CultureName(culture), preferred.Contains(culture) || inStore);
                choice.PropertyChanged += (_, _) => CompareAsync().Forget();
                Cultures.Add(choice);
            }

            await CompareAsync().ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
        }
        catch (PolicyTemplatePackageException ex)
        {
            Error = PackageError(ex.Error);
        }
        catch (JsonException)
        {
            // A release description that is not JSON (e.g. a proxy's block page answered with 200).
            Error = Localizer[L.CentralStore.ErrorDownload];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            Error = Localizer[L.CentralStore.ErrorExtraction];
        }
        finally
        {
            if (!token.IsCancellationRequested)
            {
                IsBusy = false;
                BusyText = null;
            }
        }
    }

    /// <summary>The culture folders of the central store (none when it cannot be listed: the comparison says why).</summary>
    private async Task<IReadOnlyCollection<string>> StoreCulturesAsync(CancellationToken token)
    {
        if (Session is not { } session)
        {
            return [];
        }

        try
        {
            var access = await _gp.OpenSysvolAsync(session, token).ConfigureAwait(true);
            if (access.Connection is not { } sysvol)
            {
                return [];
            }

            await using (sysvol.ConfigureAwait(true))
            {
                var listing = await sysvol.ListPolicyDefinitionsAsync(GroupPolicyPlanner.MaxCentralStoreFiles, token).ConfigureAwait(true);
                return [.. (listing ?? []).Where(e => e.IsFolder && !e.RelativePath.Contains('/', StringComparison.Ordinal)).Select(e => e.RelativePath)];
            }
        }
        catch (Exception ex) when (GroupPolicyText.IsExpected(ex))
        {
            return [];
        }
    }

    partial void OnReplaceCollidingFilesChanged(bool value) => CompareAsync().Forget();

    // ---- Comparison and update -----------------------------------------------------------------------------------------

    /// <summary>
    /// Compares the package (chosen languages) with the central store of the session's DC. A newer comparison cancels this
    /// one; the previous result is cleared at once (so "Zaktualizuj…" never plans a result that no longer matches the
    /// options), and this result – or its failure, in <see cref="Error"/> – is shown only when it is still the latest for the
    /// current package.
    /// </summary>
    public async Task CompareAsync()
    {
        var token = _compare.Next();
        _comparison = null;
        Rows = [];
        SummaryText = string.Empty;
        StoreNote = null;
        if (Package is not { } package || Session is not { } session)
        {
            IsComparing = false;
            UpdateCommand.NotifyCanExecuteChanged();
            return;
        }

        var options = new CentralStoreUpdateOptions
        {
            Cultures = [.. Cultures.Where(c => c.IsSelected).Select(c => c.Culture)],
            ReplaceCollidingFiles = ReplaceCollidingFiles,
        };
        IsComparing = true;
        UpdateCommand.NotifyCanExecuteChanged();
        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, _gp.Lifetime);
            var access = await _gp.OpenSysvolAsync(session, linked.Token).ConfigureAwait(true);
            if (access.Connection is not { } sysvol)
            {
                if (IsLatest(token, package))
                {
                    Error = _gp.SysvolText(access.Reason ?? SysvolUnavailableReason.Failed, session.Info.Server);
                }

                return;
            }

            CentralStoreComparison comparison;
            await using (sysvol.ConfigureAwait(true))
            {
                comparison = await Task.Run(() => GroupPolicyPlanner.CompareCentralStoreAsync(sysvol, package, options, linked.Token), linked.Token)
                    .ConfigureAwait(true);
            }

            if (!IsLatest(token, package))
            {
                return; // superseded by a newer comparison or another package
            }

            _comparison = comparison;
            Rows = [.. comparison.Files.Select(Row)];
            StoreNote = comparison.StoreExists ? null : Localizer[L.CentralStore.StoreMissing];
            SummaryText = Localizer.Format(L.CentralStore.Summary, Rows.Count(r => r.IsChange), Rows.Count(r => r.Status == CentralStoreFileStatus.Unchanged),
                Rows.Count(r => r.Status is CentralStoreFileStatus.NamespaceCollision or CentralStoreFileStatus.Unreadable or CentralStoreFileStatus.OutdatedLanguage));
            Error = null;
        }
        catch (OperationCanceledException)
        {
            // Superseded, or the window closed.
        }
        catch (Exception ex) when (GroupPolicyText.IsExpected(ex) || ex is InvalidDataException or JsonException)
        {
            if (IsLatest(token, package))
            {
                // InvalidDataException: an entry of the archive cannot be decompressed (it is read only now, for the comparison).
                Error = ex is InvalidDataException or JsonException ? Localizer[L.CentralStore.ErrorExtraction] : GroupPolicyText.Error(Localizer, _gp.Errors, ex);
            }
        }
        finally
        {
            if (_compare.IsCurrent(token))
            {
                IsComparing = false;
            }

            UpdateCommand.NotifyCanExecuteChanged();
        }
    }

    /// <summary>"Zaktualizuj…": the backup (when chosen), then the plan through the preview; afterwards the templates are read again.</summary>
    [RelayCommand(CanExecute = nameof(CanUpdate))]
    private async Task UpdateAsync()
    {
        if (_comparison is not { } comparison || Session is not { } session)
        {
            return;
        }

        var token = _work.Next();
        IsBusy = true;
        IsUpdating = true;
        Error = null;
        ResultText = null;
        var changed = false;
        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, _gp.Lifetime);
            if (MakeBackup && comparison.StoreExists)
            {
                BusyText = Localizer[L.CentralStore.BackingUp];
                var folder = BackupFolder.Trim();
                var server = session.Info.Server;
                var files = comparison.StoreFiles;
                var time = _gp.Time.GetUtcNow();
                var write = WriteBackup;
                string written;
                try
                {
                    // Off the UI thread: the folder may be a slow or unreachable share (CLAUDE.md rule 6).
                    written = await Task.Run(() => write(folder, server, files, time, linked.Token), linked.Token).ConfigureAwait(true);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
                {
                    Error = Localizer[L.CentralStore.ErrorBackup];
                    return;
                }

                linked.Token.ThrowIfCancellationRequested(); // the window closed or the session ended meanwhile: nothing planned
                await _gp.Settings.UpdateAsync(s => s.GroupPolicy.LastBackupFolder = folder).ConfigureAwait(true);
                ResultText = Localizer.Format(L.CentralStore.BackupWritten, written);
            }

            if (IsDisposed || Session is null || !ReferenceEquals(comparison, _comparison))
            {
                return; // closed, the session ended, or the options changed meanwhile (the new comparison is shown)
            }

            BusyText = Localizer[L.CentralStore.Updating];
            var title = Localizer.Format(L.CentralStore.PlanTitle, session.Info.Server);
            var plan = _gp.Planner.PlanCentralStoreUpdate(session, comparison, title);
            if (plan.Objects.Count == 0)
            {
                return;
            }

            if (await _gp.PreviewAsync(plan, title, session).ConfigureAwait(true))
            {
                changed = true;
                ChangedStore = true;
                _definitions.Invalidate();
                _gp.NotifyChanged();
                ResultText = Localizer.Format(L.CentralStore.Updated, comparison.WrittenCount);
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            IsUpdating = false;
            IsBusy = false;
            BusyText = null;
        }

        if (changed && !IsDisposed)
        {
            await CompareAsync().ConfigureAwait(true);
        }
    }

    [RelayCommand(CanExecute = nameof(CanClose))]
    private void Close() => RequestClose(true);

    /// <summary>Not while the store is being updated (the backup, the plan and its preview); a load or download is cancelled by closing.</summary>
    public override Task<bool> CanCloseAsync() => Task.FromResult(!IsUpdating);

    protected override void OnCultureChanged()
    {
        base.OnCultureChanged();
        OnPropertyChanged(nameof(TargetText));
        OnPropertyChanged(nameof(PackageText));
        OnPropertyChanged(nameof(TrustText));
        OnPropertyChanged(nameof(PackageIssues));
        OnPropertyChanged(nameof(PackageIssuesTitle));
        OnPropertyChanged(nameof(ProgressText));
    }

    protected override void Dispose(bool disposing)
    {
        _work.Cancel();
        _compare.Cancel();
        base.Dispose(disposing);
    }

    protected override void OnSessionEnded()
    {
        _work.Cancel();
        _compare.Cancel();
        IsComparing = false;
        UpdateCommand.NotifyCanExecuteChanged();
    }

    private bool IsIdle() => !IsBusy;

    private bool CanClose() => !IsUpdating;

    private bool CanUpdate() => !IsBusy && !IsComparing && !IsSessionEnded && _comparison is { HasChanges: true } && Session is not null
        && (!MakeBackup || _comparison is { StoreExists: false } || BackupFolder.Trim().Length > 0);

    /// <summary>The comparison started with <paramref name="token"/> is still the latest one, for the package shown.</summary>
    private bool IsLatest(CancellationToken token, PolicyTemplatePackage package) => _compare.IsCurrent(token) && ReferenceEquals(Package, package);

    private CentralStoreRowViewModel Row(CentralStoreFileChange change)
    {
        var status = Localizer[change.Status switch
        {
            CentralStoreFileStatus.New => L.CentralStore.StatusNew,
            CentralStoreFileStatus.Changed => L.CentralStore.StatusChanged,
            CentralStoreFileStatus.Unchanged => L.CentralStore.StatusUnchanged,
            CentralStoreFileStatus.NamespaceCollision => L.CentralStore.StatusCollision,
            CentralStoreFileStatus.Unreadable => L.CentralStore.StatusUnreadable,
            CentralStoreFileStatus.Removed => L.CentralStore.StatusRemoved,
            CentralStoreFileStatus.OutdatedLanguage => L.CentralStore.StatusOutdated,
            _ => L.CentralStore.StatusLeftover,
        }];
        var detail = change.Status switch
        {
            CentralStoreFileStatus.NamespaceCollision => Localizer.Format(L.CentralStore.DetailCollision, change.CollidesWith ?? string.Empty, change.Namespace ?? string.Empty),
            CentralStoreFileStatus.Removed => Localizer.Format(L.CentralStore.DetailRemoved, change.CollidesWith ?? string.Empty),
            CentralStoreFileStatus.OutdatedLanguage => Localizer[L.CentralStore.DetailOutdated],
            _ when change.PoliciesAdded > 0 || change.PoliciesRemoved > 0 => Localizer.Format(L.CentralStore.DetailPolicies, change.PoliciesAdded, change.PoliciesRemoved),
            _ => string.Empty,
        };
        return new CentralStoreRowViewModel(change, status, detail);
    }

    /// <summary>
    /// "Archiwum/chrome.admx – szablon o tej nazwie znaleziono w paczce wcześniej – użyto tamtego pliku". The package gives
    /// an English, value-free reason; it is told in the UI language (an unknown reason as "pominięty").
    /// </summary>
    private string IssueText(PolicyTemplatePackageIssue issue)
    {
        var reason = issue.Reason;
        var key = reason.Contains("too large", StringComparison.OrdinalIgnoreCase) ? L.CentralStore.IssueTooLarge
            : reason.Contains("language file", StringComparison.OrdinalIgnoreCase) ? L.CentralStore.IssueDuplicateLanguage
            : reason.Contains("found earlier", StringComparison.OrdinalIgnoreCase) ? L.CentralStore.IssueDuplicateTemplate
            : L.CentralStore.IssueSkipped;
        return Localizer.Format(L.CentralStore.PackageIssue, issue.Path, Localizer[key]);
    }

    private string PackageError(PolicyTemplatePackageError error) => Localizer[error switch
    {
        PolicyTemplatePackageError.NotFound => L.CentralStore.ErrorNotFound,
        PolicyTemplatePackageError.Unsupported => L.CentralStore.ErrorUnsupported,
        PolicyTemplatePackageError.NoTemplates => L.CentralStore.ErrorNoTemplates,
        PolicyTemplatePackageError.WindowsOnly => L.CentralStore.ErrorWindowsOnly,
        PolicyTemplatePackageError.Unsigned => L.CentralStore.ErrorUnsigned,
        PolicyTemplatePackageError.DownloadFailed => L.CentralStore.ErrorDownload,
        PolicyTemplatePackageError.HashMismatch => L.CentralStore.ErrorHash,
        PolicyTemplatePackageError.TooLarge => L.CentralStore.ErrorTooLarge,
        _ => L.CentralStore.ErrorExtraction,
    }];

    private static string VendorName(TemplateVendor vendor) => vendor switch
    {
        TemplateVendor.MicrosoftEdge => "Microsoft Edge",
        TemplateVendor.GoogleChrome => "Google Chrome",
        _ => "Mozilla Firefox",
    };

    private string CultureName(string culture)
    {
        try
        {
            var info = CultureInfo.GetCultureInfo(culture);
            return string.Create(CultureInfo.InvariantCulture, $"{(Localizer.Culture.TwoLetterISOLanguageName == "pl" ? info.NativeName : info.EnglishName)} ({culture})");
        }
        catch (CultureNotFoundException)
        {
            return culture;
        }
    }
}
