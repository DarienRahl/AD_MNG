using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.Json;
using AdMng.Core.GroupPolicy;
using AdMng.Core.GroupPolicy.AdministrativeTemplates;
using AdMng.Core.GroupPolicy.Management;
using AdMng.Presentation.Dialogs;
using AdMng.Presentation.Feedback;
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
/// <para>The window has one status line (<see cref="Status"/>; track V, stage V2b-3, design note
/// docs/dev/v-interface-renewal.md "V2b-3 as built"). Loading a package - reading a file, a folder, the templates of this
/// computer or a download - is its operation, with Stop, and so is the update (the backup, then the plan up to the change
/// plan preview); the comparison with the central store is the next stage of a load and an operation of its own when an
/// option changes. At rest the line says what the comparison found (the summary: to write, unchanged, skipped). A package
/// that could not be loaded, or a backup that could not be written, is a result that can be closed - what was shown stays;
/// a comparison that was stopped or failed leaves no files to show, which is the state of the window until it is repeated
/// (Retry). A finished update says what was written, after the new comparison. What the package or the store is - its
/// signature, the files it skips, that the store does not exist yet - is true of the content for as long as it is shown and
/// stays a note. As the state that lasts: the session of the window ended, or it was opened without one.</para>
/// <para>Nothing of a download address, a file name or the content of a package reaches a log copy (CLAUDE.md rule 1): the
/// copies are message keys, counts and the kind of a failure.</para>
/// </remarks>
public sealed partial class CentralStoreUpdateViewModel : WindowViewModelBase, IHasPageStatus
{
    private readonly GroupPolicyServices _gp;
    private readonly PolicyDefinitionsService _definitions;
    private readonly PolicyTemplatePackageLoader _loader;
    private readonly SerialCancellation _work = new();
    private readonly SerialCancellation _compare = new();

    /// <summary>Which load (or update) is the latest: only that one ends "busy" - a stopped or cancelled one must too, a replaced one must not.</summary>
    private int _workId;
    private CentralStoreComparison? _comparison;

    /// <summary>Why there is no comparison to show after one that was stopped or failed (null: nothing wrong).</summary>
    private PageStatus? _compareState;

    public CentralStoreUpdateViewModel(GroupPolicyServices services, PolicyDefinitionsService definitions, PolicyTemplatePackageLoader loader)
        : base((services ?? throw new ArgumentNullException(nameof(services))).Ui)
    {
        _gp = services;
        _definitions = definitions ?? throw new ArgumentNullException(nameof(definitions));
        _loader = loader ?? throw new ArgumentNullException(nameof(loader));
        BackupFolder = services.Settings.Current.GroupPolicy.LastBackupFolder ?? string.Empty;
        Status = new PageStatusHolder(Localizer, Services.Dispatcher, Services.Lists.StatusLog);
        UpdateLasting();
    }

    /// <summary>
    /// The status of the window - what its one status line shows: the running load, comparison or update (with Stop), how
    /// it ended, at rest what the comparison found, and the state that lasts (no session, or no comparison to show).
    /// </summary>
    public PageStatusHolder Status { get; }

    public override string WindowTitle => Localizer[L.CentralStore.Title];

    /// <summary>"Magazyn centralny: \\dc1\SYSVOL\contoso.test\Policies\PolicyDefinitions".</summary>
    public string TargetText => _gp.Sessions.Current is { } session
        ? Localizer.Format(L.CentralStore.Target, $@"\\{session.Info.Server}\SYSVOL\{session.Info.DomainDnsName}\Policies\{SysvolPaths.PolicyDefinitionsFolder}")
        : Localizer[L.Errors.NotConnected];

    /// <summary>MSI, CAB and EXE packages can be unpacked here (Windows).</summary>
    public bool CanExtractInstallers => _loader.CanExtractInstallers;

    /// <summary>
    /// Writes the backup of the store (folder, DC, files, time) and returns where it is; replaced in tests - also the UI
    /// tests of the status line, which hold the backup to see the update stopped (hence public).
    /// </summary>
    public Func<string, string, IReadOnlyDictionary<string, byte[]>, DateTimeOffset, CancellationToken, Task<string>> WriteBackup { get; init; } =
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

    /// <summary>How a package is loaded, as the status line says it (nothing of it names a file or an address in the log copy).</summary>
    /// <param name="Busy">What runs ("Reading the templates…", "Downloading the templates of Microsoft Edge…").</param>
    /// <param name="BusyKey">The key of <paramref name="Busy"/>: its log copy.</param>
    /// <param name="FailedLead">What was tried when it failed, without a full stop.</param>
    /// <param name="FailedKey">The key of <paramref name="FailedLead"/>: its log copy.</param>
    /// <param name="Retry">The action that loads the same again (a download, the templates of this computer); none for a file or a folder that the operator picks.</param>
    private sealed record PackageLoad(string Busy, string BusyKey, string FailedLead, string FailedKey, PageStatusAction? Retry);

    [RelayCommand(CanExecute = nameof(IsIdle))]
    private async Task OpenFileAsync()
    {
        var file = await Services.Files.OpenFileAsync(Localizer[L.CentralStore.OpenFileTitle],
            [new FileFilter(Localizer[L.CentralStore.FileFilter], string.Join(';', PolicyTemplatePackageLoader.Extensions.Select(e => "*" + e)))]).ConfigureAwait(true);
        if (file is not null)
        {
            await LoadAsync(Reading(), token => _loader.LoadFileAsync(file, token)).ConfigureAwait(true);
        }
    }

    [RelayCommand(CanExecute = nameof(IsIdle))]
    private async Task OpenFolderAsync()
    {
        var folder = await _gp.Dialogs.PickFolderAsync(Localizer[L.CentralStore.OpenFolderTitle], null).ConfigureAwait(true);
        if (folder is not null)
        {
            await LoadAsync(Reading(), token => _loader.LoadFolderAsync(folder, token)).ConfigureAwait(true);
        }
    }

    /// <summary>"Szablony tego komputera": creates (or refreshes) the central store from %WINDIR%\PolicyDefinitions.</summary>
    [RelayCommand(CanExecute = nameof(IsIdle))]
    private Task UseLocalStoreAsync() => LoadAsync(Reading(new PageStatusAction(Localizer[L.Common.Retry], UseLocalStoreCommand)), _ => Task.FromResult(_loader.LocalStore()
        ?? throw new PolicyTemplatePackageException(PolicyTemplatePackageError.NotFound, "This computer has no templates.")));

    [RelayCommand(CanExecute = nameof(IsIdle))]
    private Task DownloadEdgeAsync() => DownloadAsync(TemplateVendor.MicrosoftEdge, DownloadEdgeCommand);

    [RelayCommand(CanExecute = nameof(IsIdle))]
    private Task DownloadChromeAsync() => DownloadAsync(TemplateVendor.GoogleChrome, DownloadChromeCommand);

    [RelayCommand(CanExecute = nameof(IsIdle))]
    private Task DownloadFirefoxAsync() => DownloadAsync(TemplateVendor.MozillaFirefox, DownloadFirefoxCommand);

    [RelayCommand]
    private async Task ChooseBackupFolderAsync()
    {
        if (await _gp.Dialogs.PickFolderAsync(Localizer[L.CentralStore.BackupFolderTitle], BackupFolder).ConfigureAwait(true) is { } folder)
        {
            BackupFolder = folder;
        }
    }

    private PackageLoad Reading(PageStatusAction? retry = null) => new(
        Localizer[L.CentralStore.Loading], L.CentralStore.Loading, Localizer[L.CentralStoreStatus.LoadFailed], L.CentralStoreStatus.LoadFailed, retry);

    private Task DownloadAsync(TemplateVendor vendor, IRelayCommand command)
    {
        var name = VendorName(vendor);
        var kind = new PackageLoad(
            Localizer.Format(L.CentralStore.Downloading, name),
            L.CentralStore.Downloading,
            Localizer.Format(L.CentralStoreStatus.DownloadFailed, name),
            L.CentralStoreStatus.DownloadFailed,
            new PageStatusAction(Localizer[L.Common.Retry], command));
        return LoadAsync(kind, token => _loader.DownloadAsync(vendor, token));
    }

    /// <summary>
    /// Loads a package - the operation of the window: its line says what is read or downloaded, with Stop, and then compares
    /// the package with the store (a stage of the same message). A package that was loaded says nothing here: the line has
    /// what the comparison found. One that could not be loaded is a result that can be closed - the package shown before
    /// stays; so is a load the operator stopped. Closing the window and the end of its session end the load without a word.
    /// </summary>
    private async Task LoadAsync(PackageLoad kind, Func<CancellationToken, Task<PolicyTemplatePackage>> load)
    {
        var latest = _work.Next();
        var id = ++_workId;
        if (IsComparing)
        {
            // A comparison of its own runs (an option was changed; the window is not busy, so it is no stage of a load or an
            // update): this load takes the line from it, and its files were cleared when it began. It ends now, whenever
            // its share answers - nothing it finds is shown -, and should the load bring no other package, the package shown
            // has no files: the state of the window, with Retry. A package that is loaded compares itself and ends the state.
            _compare.Cancel();
            IsComparing = false;
            var retry = Localizer[L.Common.Retry];
            _compareState = PageStatus.Attention(
                Localizer.Format(L.CentralStoreStatus.CompareInterrupted, retry),
                new PageStatusLog(L.CentralStoreStatus.CompareInterrupted),
                new PageStatusAction(retry, RetryCompareCommand),
                canClose: false);
            UpdateLasting();
        }

        IsBusy = true;
        BusyText = kind.Busy;
        Error = null;
        ResultText = null;
        using var loading = Status.Begin(kind.Busy, new PageStatusLog(kind.BusyKey));

        // The operator's Stop, "the latest wins" (another package) and closing the window all end the load.
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(latest, _gp.Lifetime, loading.Token);
        var token = linked.Token;
        try
        {
            var package = await Task.Run(() => load(token), token).ConfigureAwait(true);
            token.ThrowIfCancellationRequested(); // the window closed meanwhile, or Stop
            // The languages the store already has are chosen too: a replaced template's language files would otherwise stay
            // at the old revision there.
            var storeCultures = await StoreCulturesAsync(token).ConfigureAwait(true);
            token.ThrowIfCancellationRequested();
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

            // The comparison is the next stage of this load; what it found (or why it could not) it says itself.
            await CompareCoreAsync(loading).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            // Only a load the operator stopped says so: what was shown stays as it was.
            if (loading.IsCancelling)
            {
                loading.CompleteAttention(
                    Localizer[L.CentralStoreStatus.LoadStopped], new PageStatusLog(L.CentralStoreStatus.LoadStopped), kind.Retry);
            }
        }
        catch (PolicyTemplatePackageException ex)
        {
            Error = PackageError(ex.Error);
            LoadFailed(loading, kind, Error, default);
        }
        catch (JsonException ex)
        {
            // A release description that is not JSON (e.g. a proxy's block page answered with 200).
            Error = Localizer[L.CentralStore.ErrorDownload];
            LoadFailed(loading, kind, Error, ResultCode.Of(ex));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            Error = Localizer[L.CentralStore.ErrorExtraction];
            LoadFailed(loading, kind, Error, ResultCode.Of(ex));
        }
        finally
        {
            if (_workId == id)
            {
                IsBusy = false;
                BusyText = null;
            }
        }
    }

    /// <summary>The package could not be loaded: what was tried, why, and what to do - the same load again, or another package.</summary>
    private void LoadFailed(OperationScope loading, PackageLoad kind, string reason, ResultCode code) => loading.CompleteError(
        PageStatusText.Failure(
            kind.FailedLead, reason, Localizer[kind.Retry is null ? L.CentralStoreStatus.ChooseAnother : L.Feedback.RetryNext], code),
        new PageStatusLog(kind.FailedKey).WithCode(code),
        kind.Retry);

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
    /// current package. It is the operation of the window ("Comparing with the central store…", with Stop); the files and
    /// the summary are its result, and a comparison that was stopped or failed is the state of the window (Retry).
    /// </summary>
    public Task CompareAsync() => CompareCoreAsync(null);

    /// <param name="parent">The load or the update this comparison is the last stage of (its line says the stage), or null: an operation of its own.</param>
    private async Task CompareCoreAsync(OperationScope? parent)
    {
        var latest = _compare.Next();
        _comparison = null;
        Rows = [];
        SummaryText = string.Empty;
        StoreNote = null;

        // What the last comparison left is over: this one decides.
        _compareState = null;
        UpdateSummary();
        if (Package is not { } package || Session is not { } session)
        {
            IsComparing = false;
            UpdateLasting();
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
        UpdateLasting();
        using var own = parent is null
            ? Status.Begin(Localizer[L.CentralStore.Comparing], new PageStatusLog(L.CentralStore.Comparing))
            : null;
        var scope = parent ?? own!;

        // A stage of the load or of the update is not a new message: the key of the operation stays.
        parent?.Report(Localizer[L.CentralStore.Comparing]);
        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(latest, _gp.Lifetime, scope.Token);
            var access = await _gp.OpenSysvolAsync(session, linked.Token).ConfigureAwait(true);
            if (access.Connection is not { } sysvol)
            {
                if (IsLatest(latest, package))
                {
                    Error = _gp.SysvolText(access.Reason ?? SysvolUnavailableReason.Failed, session.Info.Server);
                    CompareFailed(Error, default);
                }

                return;
            }

            CentralStoreComparison comparison;
            await using (sysvol.ConfigureAwait(true))
            {
                comparison = await Task.Run(() => GroupPolicyPlanner.CompareCentralStoreAsync(sysvol, package, options, linked.Token), linked.Token)
                    .ConfigureAwait(true);
            }

            if (!IsLatest(latest, package))
            {
                return; // superseded by a newer comparison or another package
            }

            _comparison = comparison;
            Rows = [.. comparison.Files.Select(Row)];
            StoreNote = comparison.StoreExists ? null : Localizer[L.CentralStore.StoreMissing];
            SummaryText = BuildSummary();
            Error = null;
            UpdateSummary();
        }
        catch (OperationCanceledException)
        {
            // Superseded, or the window closed - or the operator stopped it: there are no files to show.
            if (scope.IsCancelling && IsLatest(latest, package))
            {
                var retry = Localizer[L.Common.Retry];
                _compareState = PageStatus.Attention(
                    Localizer.Format(L.CentralStoreStatus.CompareStopped, retry),
                    new PageStatusLog(L.CentralStoreStatus.CompareStopped),
                    new PageStatusAction(retry, RetryCompareCommand),
                    canClose: false);
            }
        }
        catch (Exception ex) when (GroupPolicyText.IsExpected(ex) || ex is InvalidDataException or JsonException)
        {
            if (IsLatest(latest, package))
            {
                // InvalidDataException: an entry of the archive cannot be decompressed (it is read only now, for the comparison).
                Error = ex is InvalidDataException or JsonException ? Localizer[L.CentralStore.ErrorExtraction] : GroupPolicyText.Error(Localizer, _gp.Errors, ex);
                CompareFailed(Error, ResultCode.Of(ex));
            }
        }
        finally
        {
            if (_compare.IsCurrent(latest))
            {
                IsComparing = false;
            }

            UpdateLasting();
            UpdateCommand.NotifyCanExecuteChanged();
        }
    }

    /// <summary>The comparison could not be made: the state of the window, with the command that compares again.</summary>
    private void CompareFailed(string reason, ResultCode code) => _compareState = PageStatus.Error(
        PageStatusText.Failure(Localizer[L.CentralStoreStatus.CompareFailed], reason, Localizer[L.Feedback.RetryNext], code),
        new PageStatusLog(L.CentralStoreStatus.CompareFailed).WithCode(code),
        new PageStatusAction(Localizer[L.Common.Retry], RetryCompareCommand),
        canClose: false);

    /// <summary>Compares again (the Retry of the line).</summary>
    [RelayCommand]
    private Task RetryCompareAsync() => CompareAsync();

    /// <summary>
    /// "Zaktualizuj…": the backup (when chosen), then the plan through the preview; afterwards the templates are read again.
    /// The update is the operation of the window - "Preparing the update of the central store…", then "Backing up…", with Stop
    /// - until the change plan preview opens (that window says its own status; Stop must hold: a plan that was made although
    /// Stop was pressed is not previewed). When the store was updated, the new comparison is the last stage and the line then
    /// says what was written; when only the backup was made, it says that. A backup that could not be written is an error.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanUpdate))]
    private async Task UpdateAsync()
    {
        if (_comparison is not { } comparison || Session is not { } session)
        {
            return;
        }

        var latest = _work.Next();
        _workId++;
        IsBusy = true;
        IsUpdating = true;
        Error = null;
        ResultText = null;
        var changed = false;
        var said = false;
        string? backupText = null;
        using var updating = Status.Begin(Localizer[L.CentralStore.Updating], new PageStatusLog(L.CentralStore.Updating), writes: OperationWrites.Other);
        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(latest, _gp.Lifetime, updating.Token);
            var token = linked.Token;
            if (MakeBackup && comparison.StoreExists)
            {
                BusyText = Localizer[L.CentralStore.BackingUp];
                updating.Report(BusyText);
                var folder = BackupFolder.Trim();
                var server = session.Info.Server;
                var files = comparison.StoreFiles;
                var time = _gp.Time.GetUtcNow();
                var write = WriteBackup;
                string written;
                try
                {
                    // Off the UI thread: the folder may be a slow or unreachable share (CLAUDE.md rule 6).
                    written = await Task.Run(() => write(folder, server, files, time, token), token).ConfigureAwait(true);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
                {
                    Error = Localizer[L.CentralStore.ErrorBackup];
                    var code = ResultCode.Of(ex);
                    updating.CompleteError(
                        PageStatusText.Failure(Localizer[L.CentralStoreStatus.NotUpdated], Error, null, code),
                        new PageStatusLog(L.CentralStoreStatus.NotUpdated).WithCode(code));
                    said = true;
                    return;
                }

                token.ThrowIfCancellationRequested(); // the window closed or the session ended meanwhile: nothing planned
                await _gp.Settings.UpdateAsync(s => s.GroupPolicy.LastBackupFolder = folder).ConfigureAwait(true);
                backupText = Localizer.Format(L.CentralStore.BackupWritten, written);
                ResultText = backupText;
            }

            if (!IsDisposed && Session is not null && ReferenceEquals(comparison, _comparison))
            {
                BusyText = Localizer[L.CentralStore.Updating];
                updating.Report(BusyText);
                var title = Localizer.Format(L.CentralStore.PlanTitle, session.Info.Server);
                var plan = _gp.Planner.PlanCentralStoreUpdate(session, comparison, title);
                if (updating.IsCancelling)
                {
                    // Stop must hold: a planner that finished although Stop was pressed does not lead to the preview.
                    UpdateStopped(updating);
                    said = true;
                }
                else if (plan.Objects.Count > 0)
                {
                    // The plan is made: the line is the window's again while the preview - which says its own status - is open.
                    updating.Dispose();
                    if (await _gp.PreviewAsync(plan, title, session).ConfigureAwait(true))
                    {
                        changed = true;
                        ChangedStore = true;
                        _definitions.Invalidate();
                        _gp.NotifyChanged();
                        ResultText = Localizer.Format(L.CentralStore.Updated, comparison.WrittenCount);
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Only an update the operator stopped says so: the store was not changed.
            if (updating.IsCancelling)
            {
                UpdateStopped(updating);
                said = true;
            }
        }
        finally
        {
            IsUpdating = false;
            BusyText = null;

            // The store was written: its new comparison is the last stage of the update, and the sources and the options
            // wait for it as they do for the comparison of a load (another comparison or a load would take the line from
            // it, and what the update wrote would never be said). The window may be closed meanwhile: nothing writes.
            IsBusy = changed && !IsDisposed;
        }

        if (IsDisposed)
        {
            return;
        }

        if (changed)
        {
            // The new comparison belongs to the update: the line says what was written when it ended - also when it was
            // stopped or failed, whose state (no files shown, Retry) is then under the result.
            try
            {
                using var after = Status.Begin(Localizer[L.CentralStore.Comparing], new PageStatusLog(L.CentralStore.Comparing));
                await CompareCoreAsync(after).ConfigureAwait(true);
                if (!after.IsEnded)
                {
                    var text = backupText is null ? ResultText! : $"{ResultText} {backupText}";
                    after.CompleteDone(text, new PageStatusLog(L.CentralStore.Updated, comparison.WrittenCount));
                }
            }
            finally
            {
                IsBusy = false;
            }
        }
        else if (backupText is not null && !said && !updating.IsCancelling && !IsSessionEnded)
        {
            // The backup was made and the plan was not executed (the preview was closed, or nothing had to change).
            Status.Show(PageStatus.Done(backupText, new PageStatusLog(L.CentralStore.BackupWritten)));
        }
    }

    /// <summary>The operator stopped the update before the preview: the store was not changed, and "Zaktualizuj…" starts it again.</summary>
    private void UpdateStopped(OperationScope updating)
    {
        var again = Localizer[L.CentralStore.Update];
        updating.CompleteAttention(
            Localizer.Format(L.CentralStoreStatus.UpdateStopped, again),
            new PageStatusLog(L.CentralStoreStatus.UpdateStopped),
            new PageStatusAction(again, UpdateCommand));
    }

    [RelayCommand(CanExecute = nameof(CanClose))]
    private void Close() => RequestClose(true);

    /// <summary>Not while the store is being updated (the backup, the plan and its preview); a load or download is cancelled by closing.</summary>
    protected override Task<bool> CanCloseCoreAsync() => Task.FromResult(!IsUpdating);

    protected override void OnCultureChanged()
    {
        base.OnCultureChanged();
        OnPropertyChanged(nameof(TargetText));
        OnPropertyChanged(nameof(PackageText));
        OnPropertyChanged(nameof(TrustText));
        OnPropertyChanged(nameof(PackageIssues));
        OnPropertyChanged(nameof(PackageIssuesTitle));
        OnPropertyChanged(nameof(ProgressText));

        // The window's own sentences in the new language (how an operation ended stays as it was said).
        SummaryText = BuildSummary();
        UpdateLasting();
        UpdateSummary();
    }

    protected override void Dispose(bool disposing)
    {
        _work.Cancel();
        _compare.Cancel();

        // What still runs says nothing any more.
        Status.Operation?.Dispose();
        base.Dispose(disposing);
    }

    protected override void OnSessionEnded()
    {
        // The line says for good that the session ended; what still runs ends without a word of its own (the state
        // first, so the line goes straight from "working" to it).
        UpdateLasting();
        _work.Cancel();
        _compare.Cancel();
        Status.Operation?.Dispose();
        IsComparing = false;
        UpdateCommand.NotifyCanExecuteChanged();
    }

    /// <summary>
    /// The state of the window that lasts, the most important first: its session ended, or it was opened without one (an
    /// error that cannot be closed - nothing can be compared or written), else why there are no files to show after a
    /// comparison that was stopped or failed.
    /// </summary>
    private void UpdateLasting() => Status.SetLasting(
        SessionEndedStatus ?? (Session is null ? PageStatus.Error(NoSessionMessage, new PageStatusLog(L.Errors.NotConnected), canClose: false) : _compareState));

    /// <summary>The summary: what the comparison found (nothing while there is none).</summary>
    private void UpdateSummary() => Status.SetIdle(_comparison is null ? null : SummaryText);

    /// <summary>"To write: 5, unchanged: 12, skipped: 1" for the files shown.</summary>
    private string BuildSummary() => _comparison is null
        ? string.Empty
        : Localizer.Format(L.CentralStore.Summary, Rows.Count(r => r.IsChange), Rows.Count(r => r.Status == CentralStoreFileStatus.Unchanged),
            Rows.Count(r => r.Status is CentralStoreFileStatus.NamespaceCollision or CentralStoreFileStatus.Unreadable or CentralStoreFileStatus.OutdatedLanguage));

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
