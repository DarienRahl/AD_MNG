using System.Collections.ObjectModel;
using System.Globalization;
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
/// a package from a file (ZIP, CAB, MSI, EXE, ADMX), a folder, the templates of this computer, or a download (Edge, Chrome,
/// Firefox); its trust (signature, pinned address); the languages to copy; the comparison with the central store of the
/// session's DC (new, changed, unchanged, colliding namespaces, unreadable files, the policies a template adds or drops);
/// a backup of the store to a folder the operator picks; then one plan through the change plan preview. After an update
/// the editors read the templates again.
/// </summary>
public sealed partial class CentralStoreUpdateViewModel : WindowViewModelBase
{
    private readonly GroupPolicyServices _gp;
    private readonly PolicyDefinitionsService _definitions;
    private readonly PolicyTemplatePackageLoader _loader;
    private readonly SerialCancellation _work = new();
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

    /// <summary>The loaded package (null: none yet).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPackage), nameof(PackageText), nameof(TrustText), nameof(IsUntrusted))]
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

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(UpdateCommand), nameof(OpenFileCommand), nameof(OpenFolderCommand), nameof(UseLocalStoreCommand),
        nameof(DownloadEdgeCommand), nameof(DownloadChromeCommand), nameof(DownloadFirefoxCommand))]
    public partial bool IsBusy { get; private set; }

    /// <summary>"Pobieranie szablonów Microsoft Edge…" while busy.</summary>
    [ObservableProperty]
    public partial string? BusyText { get; private set; }

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
            Package = package;
            Cultures.Clear();
            var preferred = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { PolicyDefinitionLoader.DefaultCulture, Localizer.Culture.Name, Localizer.Culture.TwoLetterISOLanguageName };
            foreach (var culture in package.Cultures)
            {
                var choice = new CultureChoiceViewModel(culture, CultureName(culture), preferred.Contains(culture));
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
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
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

    partial void OnReplaceCollidingFilesChanged(bool value) => CompareAsync().Forget();

    // ---- Comparison and update -----------------------------------------------------------------------------------------

    /// <summary>Compares the package (chosen languages) with the central store of the session's DC.</summary>
    public async Task CompareAsync()
    {
        if (Package is not { } package || Session is not { } session)
        {
            return;
        }

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
                var options = new CentralStoreUpdateOptions
                {
                    Cultures = [.. Cultures.Where(c => c.IsSelected).Select(c => c.Culture)],
                    ReplaceCollidingFiles = ReplaceCollidingFiles,
                };
                _comparison = await Task.Run(() => GroupPolicyPlanner.CompareCentralStoreAsync(sysvol, package, options, _gp.Lifetime), _gp.Lifetime).ConfigureAwait(true);
            }

            Rows = [.. _comparison.Files.Select(Row)];
            StoreNote = _comparison.StoreExists ? null : Localizer[L.CentralStore.StoreMissing];
            SummaryText = Localizer.Format(L.CentralStore.Summary, Rows.Count(r => r.IsChange), Rows.Count(r => r.Status == CentralStoreFileStatus.Unchanged),
                Rows.Count(r => r.Status is CentralStoreFileStatus.NamespaceCollision or CentralStoreFileStatus.Unreadable));
        }
        catch (Exception ex) when (GroupPolicyText.IsExpected(ex))
        {
            Error = GroupPolicyText.Error(Localizer, _gp.Errors, ex);
        }
        finally
        {
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

        Error = null;
        if (MakeBackup && comparison.StoreExists)
        {
            try
            {
                var written = await CentralStoreBackup.WriteAsync(BackupFolder, session.Info.Server, comparison.StoreFiles, _gp.Time.GetUtcNow(), _gp.Lifetime)
                    .ConfigureAwait(true);
                await _gp.Settings.UpdateAsync(s => s.GroupPolicy.LastBackupFolder = BackupFolder).ConfigureAwait(true);
                ResultText = Localizer.Format(L.CentralStore.BackupWritten, written);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                Error = Localizer[L.CentralStore.ErrorBackup];
                return;
            }
        }

        var title = Localizer.Format(L.CentralStore.PlanTitle, session.Info.Server);
        var plan = _gp.Planner.PlanCentralStoreUpdate(session, comparison, title);
        if (plan.Objects.Count == 0)
        {
            return;
        }

        if (await _gp.PreviewAsync(plan, title, session).ConfigureAwait(true))
        {
            ChangedStore = true;
            _definitions.Invalidate();
            _gp.NotifyChanged();
            ResultText = Localizer.Format(L.CentralStore.Updated, comparison.WrittenCount);
            await CompareAsync().ConfigureAwait(true);
        }
    }

    [RelayCommand]
    private void Close() => RequestClose(true);

    protected override void Dispose(bool disposing)
    {
        _work.Cancel();
        base.Dispose(disposing);
    }

    protected override void OnSessionEnded()
    {
        _work.Cancel();
        UpdateCommand.NotifyCanExecuteChanged();
    }

    private bool IsIdle() => !IsBusy;

    private bool CanUpdate() => !IsBusy && !IsSessionEnded && _comparison is { HasChanges: true } && Session is not null
        && (!MakeBackup || _comparison is { StoreExists: false } || BackupFolder.Trim().Length > 0);

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
            _ => L.CentralStore.StatusLeftover,
        }];
        var detail = change.Status switch
        {
            CentralStoreFileStatus.NamespaceCollision => Localizer.Format(L.CentralStore.DetailCollision, change.CollidesWith ?? string.Empty, change.Namespace ?? string.Empty),
            CentralStoreFileStatus.Removed => Localizer.Format(L.CentralStore.DetailRemoved, change.CollidesWith ?? string.Empty),
            _ when change.PoliciesAdded > 0 || change.PoliciesRemoved > 0 => Localizer.Format(L.CentralStore.DetailPolicies, change.PoliciesAdded, change.PoliciesRemoved),
            _ => string.Empty,
        };
        return new CentralStoreRowViewModel(change, status, detail);
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
