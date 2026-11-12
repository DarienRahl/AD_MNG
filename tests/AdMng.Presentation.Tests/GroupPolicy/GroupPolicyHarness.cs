using System.Globalization;
using AdMng.Core.Batch;
using AdMng.Core.GroupPolicy.Management;
using AdMng.Presentation.Changes;
using AdMng.Presentation.Dialogs;
using AdMng.Core.GroupPolicy.AdministrativeTemplates;
using AdMng.Presentation.Feedback;
using AdMng.Presentation.GroupPolicy;
using AdMng.Presentation.GroupPolicy.AdministrativeTemplates;
using AdMng.Presentation.GroupPolicy.SecuritySettings;
using AdMng.Presentation.Lists;
using AdMng.Presentation.Navigation;
using AdMng.Presentation.Shell;
using AdMng.Presentation.Tests.Dialogs;
using AdMng.Testing;
using Microsoft.Extensions.Logging;

namespace AdMng.Presentation.Tests.GroupPolicy;

/// <summary>
/// The Group Policy services of one connection window over the fake directory with the Polish GPO management sample
/// (<see cref="FakeDirectoryBuilder.AddGroupPolicyManagementSample"/>) and a writable <see cref="FakeSysvol"/>: the real
/// planner, executor (with the SYSVOL writer), rollback and PowerShell export; the windows are scripted
/// (<see cref="ScriptedGroupPolicyDialogs"/>).
/// </summary>
internal sealed class GroupPolicyHarness
{
    private GroupPolicyHarness(B2Harness b2, bool withWriter, ILogger<PageStatusHolder>? statusLog = null)
    {
        B2 = b2;
        Sample = b2.Builder.AddGroupPolicyManagementSample(b2.Company, Sysvol);
        Executor = new BatchExecutor(b2.AuditLog, b2.Time, null, Sysvol);
        Coordinator = new ChangeCoordinator(b2.Sessions, b2.Planner, Executor, b2.Dialogs, b2.Notifications, b2.Settings, b2.Activities, b2.Changes, b2.Errors,
            b2.Localizer, b2.PowerShell);
        // The status lines of the documents write their log copies to the logger of the list services.
        var lists = statusLog is null
            ? b2.Lists
            : new ObjectListServices(b2.Sessions, b2.Localizer, b2.Dispatcher, b2.Settings, b2.Errors, b2.Changes, b2.Time, statusLog);
        Ui = new DialogServices(b2.Sessions, b2.Localizer, b2.Dispatcher, b2.Settings, b2.Errors, b2.Changes, b2.Time, b2.Dialogs, b2.Files, b2.Clipboard,
            b2.Notifications, Coordinator, b2.Activities, lists, b2.Regional);
        Planner = new GroupPolicyPlanner(new GroupPolicyReader(), b2.Time);
        Definitions = new PolicyDefinitionsService(Local);
        Services = new GroupPolicyServices(Ui, b2.Engines, Planner, Windows, withWriter ? Sysvol : null, definitions: Definitions);
        Actions = new GroupPolicyActions(Services);
        Factory = new GroupPolicyViewModelFactory(Services, Actions, Definitions);
    }

    public B2Harness B2 { get; }

    public FakeSysvol Sysvol { get; } = new();

    public GroupPolicyManagementSample Sample { get; }

    public BatchExecutor Executor { get; }

    public ChangeCoordinator Coordinator { get; }

    public DialogServices Ui { get; }

    public GroupPolicyPlanner Planner { get; }

    public ScriptedGroupPolicyDialogs Windows { get; } = new();

    public GroupPolicyServices Services { get; }

    public GroupPolicyActions Actions { get; }

    public GroupPolicyViewModelFactory Factory { get; }

    /// <summary>The templates of "this computer" (none until a test sets <see cref="TestLocalPolicyDefinitions.Source"/>).</summary>
    public TestLocalPolicyDefinitions Local { get; } = new();

    public PolicyDefinitionsService Definitions { get; }

    /// <summary>Puts the sample templates (<see cref="SampleAdministrativeTemplates"/>) into the central store of SYSVOL.</summary>
    public void AddCentralStore(bool polish = true)
    {
        foreach (var (path, content) in SampleAdministrativeTemplates.Files(polish))
        {
            Sysvol.PutPolicyDefinition(path, content);
        }
    }

    public Task<AdministrativeTemplatesEditorViewModel> TemplatesAsync(Guid id) =>
        OpenAsync<AdministrativeTemplatesEditorViewModel>(new GroupPolicyDocumentRequest(GroupPolicyDocumentKind.AdministrativeTemplates) { GpoId = id });

    public Task<SecuritySettingsEditorViewModel> SecurityAsync(Guid id) =>
        OpenAsync<SecuritySettingsEditorViewModel>(new GroupPolicyDocumentRequest(GroupPolicyDocumentKind.SecuritySettings) { GpoId = id });

    public FakeDirectorySession Session => B2.Session;

    public SampleCompany Company => B2.Company;

    public string DomainDn => B2.Builder.DomainDn;

    public static Task<GroupPolicyHarness> CreateAsync(CultureInfo? culture = null) => CreateAsync(culture, withWriter: true);

    /// <param name="culture">UI language (default en-US).</param>
    /// <param name="withWriter">False: a host without SYSVOL writer (every SYSVOL write disabled as not supported).</param>
    public static async Task<GroupPolicyHarness> CreateAsync(CultureInfo? culture, bool withWriter) =>
        new(await B2Harness.CreateAsync(culture), withWriter);

    /// <summary>A harness whose documents write the log copies of their status lines to <paramref name="statusLog"/>.</summary>
    public static async Task<GroupPolicyHarness> CreateAsync(CultureInfo? culture, ILogger<PageStatusHolder> statusLog) =>
        new(await B2Harness.CreateAsync(culture), withWriter: true, statusLog);

    public string GpoDn(Guid id) => Core.GroupPolicy.GpLinkValue.GpoDn(id, DomainDn);

    public string? Value(string dn, string attribute) => Session.GetValues(dn, attribute).SingleOrDefault();

    /// <summary>A loaded document of the request.</summary>
    public async Task<T> OpenAsync<T>(GroupPolicyDocumentRequest request)
        where T : GroupPolicyDocumentBase
    {
        var document = (T)Factory.Create(request);
        await document.LoadAsync();
        return document;
    }

    public Task<GroupPolicyObjectsDocumentViewModel> ObjectsAsync() =>
        OpenAsync<GroupPolicyObjectsDocumentViewModel>(new GroupPolicyDocumentRequest(GroupPolicyDocumentKind.Objects));

    public Task<GroupPolicyObjectDocumentViewModel> GpoAsync(Guid id, GpoDocumentTab tab = GpoDocumentTab.Scope) =>
        OpenAsync<GroupPolicyObjectDocumentViewModel>(new GroupPolicyDocumentRequest(GroupPolicyDocumentKind.Object) { GpoId = id, Tab = tab });

    public Task<GroupPolicyScopeDocumentViewModel> ScopeAsync(string dn) =>
        OpenAsync<GroupPolicyScopeDocumentViewModel>(new GroupPolicyDocumentRequest(GroupPolicyDocumentKind.Scope) { ContainerDn = dn });

    public Task<WmiFiltersDocumentViewModel> WmiFiltersAsync() =>
        OpenAsync<WmiFiltersDocumentViewModel>(new GroupPolicyDocumentRequest(GroupPolicyDocumentKind.WmiFilters));

    /// <summary>The main window's shell with the Group Policy services in its tree.</summary>
    public ShellViewModel CreateShell() => new(
        B2.Lists,
        new NavigationTreeViewModel(B2.Sessions, B2.Settings, B2.Localizer, B2.Dispatcher, B2.Errors, B2.Views, B2.Changes, Factory),
        B2.Activities,
        Coordinator,
        B2.Views,
        B2.Dialogs,
        B2.Files,
        B2.Clipboard,
        B2.Notifications,
        new Core.Csv.CsvCodec(),
        B2.Regional);
}

/// <summary>The templates of "this computer" in the tests (none by default).</summary>
internal sealed class TestLocalPolicyDefinitions : ILocalPolicyDefinitions
{
    public string? Folder { get; set; }

    public IPolicyDefinitionSource? Source { get; set; }
}
