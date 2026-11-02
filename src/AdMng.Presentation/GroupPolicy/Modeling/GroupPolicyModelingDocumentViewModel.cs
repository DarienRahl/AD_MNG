using System.Net;
using AdMng.Core.ActiveDirectory;
using AdMng.Core.Computers;
using AdMng.Core.Computers.Remote;
using AdMng.Core.Csv;
using AdMng.Core.Directory;
using AdMng.Core.GroupPolicy;
using AdMng.Core.GroupPolicy.AdministrativeTemplates;
using AdMng.Core.GroupPolicy.Modeling;
using AdMng.Core.Ldap;
using AdMng.Core.Model;
using AdMng.Presentation.Computers;
using AdMng.Presentation.GroupPolicy.AdministrativeTemplates;
using AdMng.Presentation.Icons;
using AdMng.Presentation.Infrastructure;
using AdMng.Presentation.Lists;
using AdMng.Presentation.Localization;
using AdMng.Presentation.Navigation;
using AdMng.Presentation.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AdMng.Presentation.GroupPolicy.Modeling;

/// <summary>
/// "Co dostanie komputer" (K5, design note docs/dev/k-computers-and-gpo.md 18): the Group Policy model of a computer and/or
/// a user (an account, or a new one in a container), like GPMC's Group Policy Modeling – computed from the directory and
/// SYSVOL of the session's DC (rule 4) without contacting the computer. Inputs: computer, user, site (default: the
/// computer's site through K1's <see cref="ComputerSites"/>), the WMI filter assumption and the loopback choice;
/// "Przelicz" reads everything again and builds the model off the UI thread (rule 6). Per part the GPO list with each
/// outcome, the winning settings with "Dlaczego", the token's groups and the warnings. Read only – no plan, nothing
/// written; the model lives in memory while the tab is open, "Eksportuj do CSV…" is the only file, and only on request
/// (rule 1).
/// </summary>
public sealed partial class GroupPolicyModelingDocumentViewModel : GroupPolicyDocumentBase
{
    private readonly PolicyDefinitionsService _definitions;
    private readonly IHostAddressResolver? _resolver;
    private readonly ICsvCodec _csv;
    private readonly List<ModelExportRow> _export = [];
    private bool _siteExplicit;
    private bool _applyingSite;
    private int _inputVersion;
    private string _modelTitle = string.Empty;

    public GroupPolicyModelingDocumentViewModel(
        GroupPolicyServices services,
        GroupPolicyActions actions,
        PolicyDefinitionsService definitions,
        GroupPolicyDocumentRequest request,
        IHostAddressResolver? resolver = null,
        ICsvCodec? csv = null)
        : base(services, actions)
    {
        ArgumentNullException.ThrowIfNull(request);
        _definitions = definitions ?? throw new ArgumentNullException(nameof(definitions));
        _resolver = resolver;
        _csv = csv ?? new CsvCodec();
        (ComputerChoice, UserChoice) = Choices(request);
        ComputerPart = new ModelPartViewModel(Localizer, ModelingPart.Computer);
        UserPart = new ModelPartViewModel(Localizer, ModelingPart.User);
        WmiOptions = BuildWmiOptions();
        LoopbackOptions = BuildLoopbackOptions();
        Sites = [new ModelingSiteOption(null, Localizer[L.GroupPolicyModel.SiteUnknown])];
        _applyingSite = true;
        SelectedSite = Sites[0]; // not the operator's choice: the first calculation looks the computer's site up
        _applyingSite = false;
        SummaryText = Localizer[L.GroupPolicyModel.SummaryNone];
        ComputerPart.Clear(Localizer[L.GroupPolicyModel.PartNoComputer]);
        UserPart.Clear(Localizer[L.GroupPolicyModel.PartNoUser]);
    }

    public override string Title => TitleOf(ComputerChoice, UserChoice);

    private string TitleOf(ModelingTargetChoice computer, ModelingTargetChoice user) =>
        !computer.IsNone ? Localizer.Format(L.GroupPolicyModel.TitleComputer, TargetName(computer))
        : user.IsNone ? Localizer[L.GroupPolicyModel.TitleEmpty]
        : Localizer.Format(L.GroupPolicyModel.TitleUser, TargetName(user));

    public override ObjectIcon Icon => !ComputerChoice.IsNone ? ObjectIcon.Computer : !UserChoice.IsNone ? ObjectIcon.User : ObjectIcon.GroupPolicy;

    // ---- Inputs ------------------------------------------------------------------------------------------------------------

    /// <summary>The computer: an account, a container (a new computer there) or none.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Title), nameof(Icon), nameof(ComputerText))]
    public partial ModelingTargetChoice ComputerChoice { get; private set; }

    /// <summary>The user: an account, a container (a new user there) or none.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Title), nameof(Icon), nameof(UserText))]
    public partial ModelingTargetChoice UserChoice { get; private set; }

    /// <summary>"WAW-PC001 (contoso.test/Firma/Komputery)", "Nowy komputer w contoso.test/Firma/Komputery", "(brak)".</summary>
    public string ComputerText => TargetText(ComputerChoice, L.GroupPolicyModel.NewComputerIn);

    public string UserText => TargetText(UserChoice, L.GroupPolicyModel.NewUserIn);

    /// <summary>The forest's sites and "(nieznana)" first.</summary>
    [ObservableProperty]
    public partial IReadOnlyList<ModelingSiteOption> Sites { get; private set; }

    [ObservableProperty]
    public partial ModelingSiteOption? SelectedSite { get; set; }

    [ObservableProperty]
    public partial IReadOnlyList<WmiAssumptionOption> WmiOptions { get; private set; }

    /// <summary>What the model assumes for GPOs with a WMI filter (true by default, as GPMC).</summary>
    [ObservableProperty]
    public partial WmiFilterAssumption WmiFilters { get; set; } = WmiFilterAssumption.AssumeTrue;

    [ObservableProperty]
    public partial IReadOnlyList<LoopbackOption> LoopbackOptions { get; private set; }

    /// <summary>Loopback: from the computer's GPOs (default), off, merge or replace.</summary>
    [ObservableProperty]
    public partial LoopbackChoice Loopback { get; set; } = LoopbackChoice.FromSettings;

    /// <summary>The inputs changed after the last calculation ("kliknij Przelicz"); the export waits for a new one.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ExportCsvCommand))]
    public partial bool IsStale { get; private set; }

    // ---- Result ------------------------------------------------------------------------------------------------------------

    /// <summary>"Konfiguracja komputera".</summary>
    public ModelPartViewModel ComputerPart { get; }

    /// <summary>"Konfiguracja użytkownika".</summary>
    public ModelPartViewModel UserPart { get; }

    /// <summary>The last calculation produced a model.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ExportCsvCommand))]
    public partial bool HasModel { get; private set; }

    /// <summary>"Komputer: 3 GPO, 12 ustawień; Użytkownik: 1 GPO, 2 ustawienia".</summary>
    [ObservableProperty]
    public partial string SummaryText { get; private set; }

    /// <summary>"Pętla zwrotna: …" (empty without a model).</summary>
    [ObservableProperty]
    public partial string LoopbackText { get; private set; } = string.Empty;

    /// <summary>What the model could not use: a target that does not exist, SYSVOL unavailable, no target.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNotes))]
    public partial IReadOnlyList<string> Notes { get; private set; } = [];

    public bool HasNotes => Notes.Count > 0;

    /// <summary>True when the tab shows the model of the request's targets (the shell selects it instead of opening another).</summary>
    public bool Matches(GroupPolicyDocumentRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var (computer, user) = Choices(request);
        return computer.SameAs(ComputerChoice) && user.SameAs(UserChoice);
    }

    /// <summary>The inputs of a request: the named accounts, else a new computer and user in the container.</summary>
    internal static (ModelingTargetChoice Computer, ModelingTargetChoice User) Choices(GroupPolicyDocumentRequest request)
    {
        var container = request.ComputerDn is null && request.UserDn is null && request.ContainerDn is { Length: > 0 } dn ? dn : null;
        var computer = request.ComputerDn is { Length: > 0 } computerDn ? ModelingTargetChoice.Account(computerDn)
            : container is not null ? ModelingTargetChoice.Container(container) : ModelingTargetChoice.None;
        var user = request.UserDn is { Length: > 0 } userDn ? ModelingTargetChoice.Account(userDn)
            : container is not null ? ModelingTargetChoice.Container(container) : ModelingTargetChoice.None;
        return (computer, user);
    }

    partial void OnComputerChoiceChanged(ModelingTargetChoice value)
    {
        _siteExplicit = false;
        InputsChanged();
    }

    partial void OnUserChoiceChanged(ModelingTargetChoice value) => InputsChanged();

    partial void OnSelectedSiteChanged(ModelingSiteOption? value)
    {
        if (!_applyingSite)
        {
            _siteExplicit = true;
            InputsChanged();
        }
    }

    partial void OnWmiFiltersChanged(WmiFilterAssumption value) => InputsChanged();

    partial void OnLoopbackChanged(LoopbackChoice value) => InputsChanged();

    private void InputsChanged()
    {
        _inputVersion++;
        IsStale = true;
    }

    // ---- Commands ----------------------------------------------------------------------------------------------------------

    /// <summary>"Wybierz komputer…" (object picker).</summary>
    [RelayCommand(CanExecute = nameof(CanPick))]
    private async Task PickComputerAsync()
    {
        if (Gp.Sessions.Current is { } session
            && await Gp.Ui.Dialogs.PickObjectsAsync(session, [ObjectKind.Computer], multiSelect: false).ConfigureAwait(true) is [var dn, ..])
        {
            ComputerChoice = ModelingTargetChoice.Account(dn);
        }
    }

    /// <summary>"Kontener…": a new computer in the chosen OU or container.</summary>
    [RelayCommand(CanExecute = nameof(CanPick))]
    private async Task PickComputerContainerAsync()
    {
        if (Gp.Sessions.Current is { } session && await Gp.Ui.Dialogs.PickContainerAsync(session, StartDn(ComputerChoice)).ConfigureAwait(true) is { } dn)
        {
            ComputerChoice = ModelingTargetChoice.Container(dn);
        }
    }

    [RelayCommand]
    private void ClearComputer() => ComputerChoice = ModelingTargetChoice.None;

    /// <summary>"Wybierz użytkownika…".</summary>
    [RelayCommand(CanExecute = nameof(CanPick))]
    private async Task PickUserAsync()
    {
        if (Gp.Sessions.Current is { } session
            && await Gp.Ui.Dialogs.PickObjectsAsync(session, [ObjectKind.User, ObjectKind.InetOrgPerson], multiSelect: false).ConfigureAwait(true) is [var dn, ..])
        {
            UserChoice = ModelingTargetChoice.Account(dn);
        }
    }

    [RelayCommand(CanExecute = nameof(CanPick))]
    private async Task PickUserContainerAsync()
    {
        if (Gp.Sessions.Current is { } session && await Gp.Ui.Dialogs.PickContainerAsync(session, StartDn(UserChoice)).ConfigureAwait(true) is { } dn)
        {
            UserChoice = ModelingTargetChoice.Container(dn);
        }
    }

    [RelayCommand]
    private void ClearUser() => UserChoice = ModelingTargetChoice.None;

    /// <summary>"Przelicz": reads the directory and SYSVOL again and builds the model with the current inputs.</summary>
    [RelayCommand(CanExecute = nameof(CanPick))]
    private Task RecalculateAsync() => LoadAsync();

    /// <summary>"Eksportuj do CSV…": the GPO lists and the settings of both parts to a file the operator chooses.</summary>
    [RelayCommand(CanExecute = nameof(CanExport))]
    private async Task ExportCsvAsync()
    {
        if (!CanExport() || _export.Count == 0)
        {
            return;
        }

        var title = Localizer[L.GroupPolicyModel.ExportCsv];
        var path = await Gp.Ui.Files.SaveFileAsync(
            title,
            ListExportSnapshot.SuggestFileName(_modelTitle, Gp.Time.GetLocalNow()),
            [new FileFilter(Localizer[L.Files.CsvFilter], "*.csv")]).ConfigureAwait(true);
        if (path is null)
        {
            return;
        }

        var headers = new[]
        {
            L.GroupPolicyModel.ColumnPart, L.GroupPolicyModel.ColumnSection, L.GroupPolicy.ColumnPrecedence, L.GroupPolicy.ColumnGpo,
            L.GroupPolicy.ColumnLocation, L.GroupPolicy.ColumnEnforced, L.GroupPolicyModel.ColumnLinkOrder, L.GroupPolicyModel.ColumnOutcome,
            L.GroupPolicyModel.ColumnSetting, L.GroupPolicyModel.ColumnValue, L.GroupPolicyModel.ColumnOverridden, L.GroupPolicyModel.ColumnMarks,
        }.Select(k => Localizer[k]).ToList();
        var rows = _export.Select(r => r.Cells).ToList();
        var delimiter = CsvFiles.ListSeparator(Gp.Ui.Regional.Culture);
        try
        {
            await CsvFiles.WriteAsync(path, writer => _csv.Write(writer, headers, rows, delimiter, protectFormulas: true)).ConfigureAwait(true);
            Gp.Ui.Notifications.Show(NotificationSeverity.Success, Localizer.Format(L.Files.Exported, rows.Count, Path.GetFileName(path)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            await Gp.Ui.Dialogs.ShowErrorAsync(title, Gp.Errors.Translate(ex)).ConfigureAwait(true);
        }
    }

    private bool CanPick() => HasSession;

    /// <summary>A model of the current inputs is shown (not one of earlier inputs).</summary>
    private bool CanExport() => HasModel && !IsStale;

    protected override void RefreshCommands()
    {
        PickComputerCommand.NotifyCanExecuteChanged();
        PickComputerContainerCommand.NotifyCanExecuteChanged();
        PickUserCommand.NotifyCanExecuteChanged();
        PickUserContainerCommand.NotifyCanExecuteChanged();
        RecalculateCommand.NotifyCanExecuteChanged();
        ExportCsvCommand.NotifyCanExecuteChanged();
    }

    // ---- Loading -----------------------------------------------------------------------------------------------------------

    protected override async Task LoadCoreAsync(IDirectorySession session, ISysvolWriteConnection? sysvol, CancellationToken cancellationToken)
    {
        var request = new Calculation(ComputerChoice, UserChoice, _siteExplicit ? SelectedSite?.Dn : null, _siteExplicit,
            new ModelingOptions { WmiFilters = WmiFilters, Loopback = Loopback });
        var version = _inputVersion;
        if (request.Computer.IsNone && request.User.IsNone)
        {
            ShowNoModel([Localizer[L.GroupPolicyModel.NoteNoTargets]]);
            IsStale = version != _inputVersion;
            return;
        }

        try
        {
            var definitions = await _definitions.GetAsync(session, sysvol, Localizer.Culture, reload: false, cancellationToken).ConfigureAwait(true);
            var resolver = _resolver ?? NoAddresses.Instance;
            var localizer = Localizer;
            var result = await Task.Run(() => CalculateAsync(session, sysvol, request, definitions.Set, resolver, localizer, cancellationToken), cancellationToken)
                .ConfigureAwait(true);
            cancellationToken.ThrowIfCancellationRequested();
            Show(session, result, sysvol is null);
            _modelTitle = TitleOf(request.Computer, request.User);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // No model of earlier inputs stays on screen (or exportable) under the new title; the error is shown by the base.
            ShowNoModel([]);
            throw;
        }

        // Inputs changed while the model was computed: it is the model of the inputs captured above.
        IsStale = version != _inputVersion;
    }

    protected override void OnNotConnected() => ShowNoModel([]);

    protected override void OnCultureChanged()
    {
        WmiOptions = BuildWmiOptions();
        LoopbackOptions = BuildLoopbackOptions();
        OnPropertyChanged(nameof(ComputerText));
        OnPropertyChanged(nameof(UserText));
        base.OnCultureChanged();
    }

    private void Show(IDirectorySession session, CalculationResult result, bool sysvolUnavailable)
    {
        _applyingSite = true;
        try
        {
            Sites = [new ModelingSiteOption(null, Localizer[L.GroupPolicyModel.SiteUnknown]), .. result.Sites];
            SelectedSite = Sites.FirstOrDefault(s => s.Dn is not null && result.SiteDn is not null && DistinguishedName.AreEqual(s.Dn, result.SiteDn)) ?? Sites[0];
        }
        finally
        {
            _applyingSite = false;
        }

        var notes = new List<string>(result.Notes);
        if (sysvolUnavailable)
        {
            notes.Add(Localizer[L.GroupPolicyModel.NoteSysvol]);
        }

        if (result.Model is not { } model)
        {
            ShowNoModel(notes);
            return;
        }

        var text = new GroupPolicyModelText(Localizer, session.Info.DomainDnsName);
        _export.Clear();
        var summary = new List<string>();
        ShowPart(ComputerPart, model.Computer, result.ComputerTarget, result.Names, text, sysvolUnavailable, L.GroupPolicyModel.PartNoComputer, summary);
        ShowPart(UserPart, model.User, result.UserTarget, result.Names, text, sysvolUnavailable, L.GroupPolicyModel.PartNoUser, summary);
        LoopbackText = text.Loopback(model);
        Notes = notes;
        SummaryText = string.Join("; ", summary);
        LoadedText = SummaryText;
        HasModel = true;
        OnPropertyChanged(nameof(Title));
    }

    private void ShowPart(ModelPartViewModel view, ModeledPart? part, ModelingTarget? target, IReadOnlyDictionary<string, string> names,
        GroupPolicyModelText text, bool sysvolUnavailable, string unavailableKey, List<string> summary)
    {
        if (part is null || target is null)
        {
            view.Clear(Localizer[unavailableKey]);
            return;
        }

        var gpos = part.Gpos.Select(g => text.GpoRow(g, part.Part)).ToList();
        var settings = text.SettingRows(part);
        var warnings = part.Warnings
            .Where(w => !(sysvolUnavailable && w.Kind == ModelingWarningKind.ContentUnreadable))
            .Select(text.Warning)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        var compare = StringComparer.Create(Localizer.Culture, ignoreCase: true);
        var groups = target.Token.Select(sid => sid.Equals(GroupPolicySecurityFilter.ThisOrganizationSid, StringComparison.OrdinalIgnoreCase)
                ? Localizer[L.GroupPolicyModel.TrusteeThisOrganization]
                : TrusteeNames.NameOf(names, sid))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(compare)
            .ToList();
        view.Show(gpos, settings, warnings, groups);
        var partName = Localizer[part.Part == ModelingPart.Computer ? L.GroupPolicyModel.ComputerLabel : L.GroupPolicyModel.UserLabel];
        summary.Add(Localizer.Format(L.GroupPolicyModel.SummaryPart, partName,
            Localizer.Format(L.GroupPolicyModel.GpoCount, view.AppliedCount),
            Correspondence.CorrespondenceText.Plural(Localizer, settings.Count, L.GroupPolicyModel.SettingsOne, L.GroupPolicyModel.SettingsFew, L.GroupPolicyModel.SettingsMany)));

        var gpoSection = Localizer[L.GroupPolicyModel.SectionGpo];
        var settingSection = Localizer[L.GroupPolicyModel.SectionSetting];
        foreach (var gpo in gpos)
        {
            _export.Add(new ModelExportRow([partName, gpoSection, gpo.PrecedenceText, gpo.Name, gpo.LocationText, gpo.EnforcedText, gpo.LinkOrderText, gpo.OutcomeText,
                string.Empty, string.Empty, string.Empty, string.Empty]));
        }

        foreach (var setting in settings)
        {
            _export.Add(new ModelExportRow([partName, settingSection, string.Empty, setting.WinnerGpo, setting.Area, string.Empty, string.Empty, string.Empty,
                setting.Name, setting.Value, setting.OverriddenText, setting.MarksText]));
        }
    }

    private void ShowNoModel(IReadOnlyList<string> notes)
    {
        ComputerPart.Clear(Localizer[L.GroupPolicyModel.PartNoComputer]);
        UserPart.Clear(Localizer[L.GroupPolicyModel.PartNoUser]);
        _export.Clear();
        HasModel = false;
        LoopbackText = string.Empty;
        Notes = notes;
        SummaryText = Localizer[L.GroupPolicyModel.SummaryNone];
        LoadedText = SummaryText;
    }

    /// <summary>Everything the model needs, read on a background thread from the session's DC and its SYSVOL.</summary>
    private static async Task<CalculationResult> CalculateAsync(IDirectorySession session, ISysvolWriteConnection? sysvol, Calculation request,
        PolicyDefinitionSet definitions, IHostAddressResolver resolver, ILocalizer localizer, CancellationToken cancellationToken)
    {
        var directory = await GroupPolicyModelReader.ReadDirectoryAsync(session, cancellationToken).ConfigureAwait(false);
        SiteData topology;
        try
        {
            topology = await ComputerSites.ReadTopologyAsync(session, cancellationToken).ConfigureAwait(false);
        }
        catch (DirectoryException)
        {
            topology = new SiteData(SiteTopology.Empty, []);
        }

        var sitesDn = DistinguishedName.Parse(session.Info.ConfigurationNamingContext).Child("CN", "Sites");
        var sites = topology.Topology.Sites.Select(name => new ModelingSiteOption(sitesDn.Child("CN", name).ToString(), name)).ToList();

        var notes = new List<string>();
        var computer = await TargetAsync(session, request.Computer, ModelingPart.Computer, directory.DomainSid, localizer, notes, cancellationToken).ConfigureAwait(false);
        var user = await TargetAsync(session, request.User, ModelingPart.User, directory.DomainSid, localizer, notes, cancellationToken).ConfigureAwait(false);

        // The site: the operator's choice, else the computer account's (its DC server object, or DNS and the subnets).
        var siteDn = request.SiteExplicit ? request.SiteDn : null;
        if (!request.SiteExplicit && computer?.AccountDn is { } computerDn)
        {
            siteDn = await DefaultSiteAsync(session, computerDn, topology, sites, resolver, cancellationToken).ConfigureAwait(false);
        }

        if (computer is null && user is null)
        {
            return new CalculationResult(sites, siteDn, notes, null, null, null, new Dictionary<string, string>());
        }

        var input = new GroupPolicyModelInput
        {
            DomainDn = directory.DomainDn,
            Containers = directory.Containers,
            Gpos = directory.Gpos,
            Computer = computer,
            User = user,
            SiteDn = siteDn,
            Options = request.Options,
            Definitions = definitions,
        };
        IReadOnlyDictionary<Guid, ModelingGpoContent> contents = sysvol is null
            ? new Dictionary<Guid, ModelingGpoContent>()
            : await GroupPolicyModelReader.ReadContentsAsync(sysvol, GroupPolicyModeler.GposToRead(input), cancellationToken).ConfigureAwait(false);
        var model = GroupPolicyModeler.Build(input, contents);
        var sids = (computer?.Token ?? Enumerable.Empty<string>()).Concat(user?.Token ?? Enumerable.Empty<string>()).ToList();
        var names = await TrusteeNames.ResolveAsync(session, sids, localizer, cancellationToken).ConfigureAwait(false);
        return new CalculationResult(sites, siteDn, notes, model, computer, user, names);
    }

    /// <summary>The target of a part; a missing account or container is a note (null target), never a failure.</summary>
    private static async Task<ModelingTarget?> TargetAsync(IDirectorySession session, ModelingTargetChoice choice, ModelingPart part, string? domainSid,
        ILocalizer localizer, List<string> notes, CancellationToken cancellationToken)
    {
        if (choice.IsNone)
        {
            return null;
        }

        if (choice.Kind == ModelingTargetKind.Account)
        {
            try
            {
                return await GroupPolicyModelReader.ReadAccountAsync(session, choice.Dn!, domainSid, cancellationToken).ConfigureAwait(false);
            }
            catch (DirectoryException ex) when (ex.Kind == DirectoryErrorKind.NoSuchObject)
            {
                notes.Add(localizer.Format(L.GroupPolicyModel.NoteAccountMissing, AddressResolver.ToDisplayPath(choice.Dn!)));
                return null;
            }
        }

        DirectoryObject? container;
        try
        {
            container = await session.GetAsync(choice.Dn!, [Attr.ObjectClass], cancellationToken).ConfigureAwait(false);
        }
        catch (DirectoryException ex) when (ex.Kind == DirectoryErrorKind.NoSuchObject)
        {
            container = null;
        }

        if (container is null)
        {
            notes.Add(localizer.Format(L.GroupPolicyModel.NoteContainerMissing, AddressResolver.ToDisplayPath(choice.Dn!)));
            return null;
        }

        return GroupPolicyModelReader.ContainerTarget(container.DistinguishedName, part, domainSid);
    }

    /// <summary>The site of a computer account through K1's resolution (null when it cannot be told).</summary>
    private static async Task<string?> DefaultSiteAsync(IDirectorySession session, string computerDn, SiteData topology, IReadOnlyList<ModelingSiteOption> sites,
        IHostAddressResolver resolver, CancellationToken cancellationToken)
    {
        var account = await session.GetAsync(computerDn, ComputerFacts.Attributes(session.Schema), cancellationToken).ConfigureAwait(false);
        if (account is null)
        {
            return null;
        }

        var resolved = await ComputerSites.ResolveAsync(resolver, topology, [ComputerFacts.From(account)], cancellationToken).ConfigureAwait(false);
        var name = resolved.Values.FirstOrDefault()?.Site;
        return name is null ? null : sites.FirstOrDefault(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase))?.Dn;
    }

    // ---- Helpers -----------------------------------------------------------------------------------------------------------

    private string TargetText(ModelingTargetChoice choice, string newInKey) => choice switch
    {
        { IsNone: true } => Localizer[L.GroupPolicyModel.NoTarget],
        { Kind: ModelingTargetKind.Container } => Localizer.Format(newInKey, AddressResolver.ToDisplayPath(choice.Dn!)),
        _ => DistinguishedName.TryParse(choice.Dn!, out var dn) && dn.Parent is { } parent
            ? dn.Name + " (" + AddressResolver.ToDisplayPath(parent.ToString()) + ")"
            : choice.Dn!,
    };

    private string TargetName(ModelingTargetChoice choice)
    {
        if (Gp.Sessions.Current is { } session && DistinguishedName.AreEqual(choice.Dn!, session.Info.DefaultNamingContext))
        {
            return session.Info.DomainDnsName;
        }

        return DistinguishedName.TryParse(choice.Dn!, out var dn) && !dn.IsRoot ? dn.Name : choice.Dn!;
    }

    private static string? StartDn(ModelingTargetChoice choice) => choice switch
    {
        { IsNone: true } => null,
        { Kind: ModelingTargetKind.Container } => choice.Dn,
        _ => DistinguishedName.TryParse(choice.Dn!, out var dn) ? dn.Parent?.ToString() : null,
    };

    private List<WmiAssumptionOption> BuildWmiOptions() =>
    [
        new(WmiFilterAssumption.AssumeTrue, Localizer[L.GroupPolicyModel.WmiTrue]),
        new(WmiFilterAssumption.AssumeFalse, Localizer[L.GroupPolicyModel.WmiFalse]),
    ];

    private List<LoopbackOption> BuildLoopbackOptions() =>
    [
        new(LoopbackChoice.FromSettings, Localizer[L.GroupPolicyModel.LoopbackFromSettings]),
        new(LoopbackChoice.Off, Localizer[L.GroupPolicyModel.LoopbackOff]),
        new(LoopbackChoice.Merge, Localizer[L.GroupPolicyModel.LoopbackMerge]),
        new(LoopbackChoice.Replace, Localizer[L.GroupPolicyModel.LoopbackReplace]),
    ];

    /// <summary>The inputs of one calculation (taken on the UI thread).</summary>
    private sealed record Calculation(ModelingTargetChoice Computer, ModelingTargetChoice User, string? SiteDn, bool SiteExplicit, ModelingOptions Options);

    /// <summary>What a calculation read and built (in memory only).</summary>
    private sealed record CalculationResult(
        IReadOnlyList<ModelingSiteOption> Sites,
        string? SiteDn,
        IReadOnlyList<string> Notes,
        GroupPolicyModel? Model,
        ModelingTarget? ComputerTarget,
        ModelingTarget? UserTarget,
        IReadOnlyDictionary<string, string> Names);

    /// <summary>A row of the CSV export.</summary>
    private sealed record ModelExportRow(IReadOnlyList<string> Cells);

    /// <summary>No resolver in this host: only a DC's site (its server object) is known.</summary>
    private sealed class NoAddresses : IHostAddressResolver
    {
        public static NoAddresses Instance { get; } = new();

        public Task<IReadOnlyList<IPAddress>> ResolveAsync(string host, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<IPAddress>>([]);
    }
}
