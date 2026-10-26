using System.Globalization;
using System.Net;
using AdMng.Core.Audit;
using AdMng.Core.Batch;
using AdMng.Core.Csv;
using AdMng.Core.Directory;
using AdMng.Core.Formatting;
using AdMng.Core.Security;
using AdMng.Core.Templates;
using AdMng.Presentation.BatchEditing;
using AdMng.Presentation.Changes;
using AdMng.Presentation.Dialogs;
using AdMng.Presentation.Errors;
using AdMng.Presentation.Lists;
using AdMng.Presentation.Localization;
using AdMng.Presentation.Navigation;
using AdMng.Presentation.Services;
using AdMng.Presentation.Session;
using AdMng.Presentation.Settings;
using AdMng.Presentation.Shell;
using AdMng.Presentation.Tests.TestDoubles;
using AdMng.Presentation.Views;
using AdMng.Testing;
using ManualTimeProvider = AdMng.Testing.ManualTimeProvider;

namespace AdMng.Presentation.Tests.Dialogs;

/// <summary>
/// The real presentation services and the REAL Core engines (planner, executor, rollback, CSV, templates, formatter)
/// over <see cref="FakeDirectorySession"/> with the Polish sample company - view models are tested end to end.
/// UI services are scripted doubles.
/// </summary>
internal sealed class B2Harness
{
    private B2Harness(CultureInfo culture, CultureInfo regional)
    {
        Builder = new FakeDirectoryBuilder(timeProvider: Time);
        Company = Builder.AddSampleCompany();
        Localizer = new ResxLocalizer(culture);
        Regional = new SystemRegionalSettings(regional, regional.TextInfo.ANSICodePage);
        Errors = new ErrorTranslator(Localizer);
        ConnectToServer = (_, server, _) => Task.FromResult<IDirectorySession>(new FakeDomainControllerSession(Session, server));
        Sessions = new SessionManager(
            new SessionConnector(() => NextSession ?? Session, (current, server, ct) => ConnectToServer(current, server, ct)),
            new DelegateAttributeFormatterFactory(s => new AttributeFormatter(s)),
            Localizer);
        Lists = new ObjectListServices(Sessions, Localizer, Dispatcher, Settings, Errors, Changes, Time);
        Activities = new ActivityCenterViewModel(Dispatcher, Localizer, Errors, Time, Sessions);
        var templates = new TemplateEngine();
        Planner = new ChangePlanner(templates, new PasswordGenerator(), Time);
        Executor = new BatchExecutor(AuditLog, Time);
        PowerShell = new PowerShellHistory(new PowerShellExporter());
        Coordinator = new ChangeCoordinator(Sessions, Planner, Executor, Dialogs, Notifications, Settings, Activities, Changes, Errors, Localizer, PowerShell);
        Services = new DialogServices(Sessions, Localizer, Dispatcher, Settings, Errors, Changes, Time, Dialogs, Files, Clipboard, Notifications, Coordinator, Activities, Lists, Regional);
        var csv = new CsvCodec();
        Engines = new BatchEngines(Planner, new CsvImportPlanner(Time), new RollbackPlanner(), new PowerShellExporter(), csv, templates, new BatchReportExporter(csv));
        Views = new ViewLibrary(Settings);
        Factory = new DialogViewModelFactory(Services, Engines, Views, AuditLog, new AuditLogExporter(csv), PowerShell, Theme);
    }

    public ManualTimeProvider Time { get; } = new();

    public FakeDirectoryBuilder Builder { get; }

    public FakeDirectorySession Session => Builder.Session;

    /// <summary>What the next connect returns (default: <see cref="Session"/>), e.g. a session of another identity.</summary>
    public IDirectorySession? NextSession { get; set; }

    /// <summary>
    /// "Change domain controller": what the connector opens for (current session, server). Default: a
    /// <see cref="FakeDomainControllerSession"/> of that server over the same in-memory directory.
    /// </summary>
    public Func<IDirectorySession, string, CancellationToken, Task<IDirectorySession>> ConnectToServer { get; set; }

    public SampleCompany Company { get; }

    public ResxLocalizer Localizer { get; }

    /// <summary>Regional settings of the "machine" (by default those of the UI culture).</summary>
    public SystemRegionalSettings Regional { get; }

    public ErrorTranslator Errors { get; }

    public SessionManager Sessions { get; }

    public SynchronousUiDispatcher Dispatcher { get; } = new();

    public InMemorySettingsStore Settings { get; } = new();

    public DirectoryChangeNotifier Changes { get; } = new();

    public ObjectListServices Lists { get; }

    public ActivityCenterViewModel Activities { get; }

    public InMemoryAuditLog AuditLog { get; } = new();

    public ChangePlanner Planner { get; }

    public BatchExecutor Executor { get; }

    public PowerShellHistory PowerShell { get; }

    public ChangeCoordinator Coordinator { get; }

    public ScriptedDialogService Dialogs { get; } = new();

    public RecordingNotificationService Notifications { get; } = new();

    public FakeFileDialogService Files { get; } = new();

    public FakeClipboard Clipboard { get; } = new();

    public FakeThemeService Theme { get; } = new();

    public DialogServices Services { get; }

    public BatchEngines Engines { get; }

    public ViewLibrary Views { get; }

    public DialogViewModelFactory Factory { get; }

    /// <param name="culture">UI language (default en-US).</param>
    /// <param name="regional">Regional format of the system (default: the UI culture).</param>
    public static async Task<B2Harness> CreateAsync(CultureInfo? culture = null, CultureInfo? regional = null)
    {
        culture ??= CultureInfo.GetCultureInfo("en-US");
        var harness = new B2Harness(culture, regional ?? culture);
        await harness.Sessions.ConnectAsync(new ConnectionOptions { Domain = FakeDirectoryBuilder.DefaultDomain }, null, CancellationToken.None);
        return harness;
    }

    /// <summary>
    /// Connected to a <see cref="FakeDomainControllerSession"/> of <paramref name="server"/> instead of the directory itself, so
    /// that "Change domain controller" can close it without closing the shared in-memory directory.
    /// </summary>
    public static async Task<B2Harness> CreateOnDomainControllerAsync(string server = "dc01.contoso.test", CultureInfo? culture = null)
    {
        culture ??= CultureInfo.GetCultureInfo("en-US");
        var harness = new B2Harness(culture, culture);
        harness.NextSession = new FakeDomainControllerSession(harness.Session, server);
        await harness.Sessions.ConnectAsync(new ConnectionOptions { Domain = FakeDirectoryBuilder.DefaultDomain }, null, CancellationToken.None);
        harness.NextSession = null;
        return harness;
    }

    /// <summary>The main window's view model over the same services (real engines, fake directory).</summary>
    public ShellViewModel CreateShell() => new(
        Lists,
        new NavigationTreeViewModel(Sessions, Settings, Localizer, Dispatcher, Errors, Views, Changes),
        Activities,
        Coordinator,
        Views,
        Dialogs,
        Files,
        Clipboard,
        Notifications,
        new CsvCodec(),
        Regional);

    /// <summary>
    /// The operator disconnects and connects again - to <paramref name="next"/>, a new session (e.g. another identity
    /// with the same sample company).
    /// </summary>
    public async Task ReconnectAsync(IDirectorySession next)
    {
        await Sessions.DisconnectAsync();
        NextSession = next;
        await Sessions.ConnectAsync(new ConnectionOptions { Domain = FakeDirectoryBuilder.DefaultDomain }, null, CancellationToken.None);
    }

    /// <summary>Values of an attribute as stored in the fake directory.</summary>
    public IReadOnlyList<string> Values(string dn, string attribute) => Session.GetValues(dn, attribute);

    public string? Value(string dn, string attribute) => Session.GetValues(dn, attribute).FirstOrDefault();

    /// <summary>Waits until a condition holds (background work started fire-and-forget by view models).</summary>
    public static async Task WaitUntilAsync(Func<bool> condition, int timeoutMs = 5000)
    {
        var started = Environment.TickCount64;
        while (!condition())
        {
            if (Environment.TickCount64 - started > timeoutMs)
            {
                throw new TimeoutException("The condition was not met in time.");
            }

            await Task.Delay(10, TestContext.Current.CancellationToken);
        }
    }

    private sealed class SessionConnector(
        Func<IDirectorySession> session,
        Func<IDirectorySession, string, CancellationToken, Task<IDirectorySession>> connectToServer) : IDirectoryConnector
    {
        public string? GetComputerDomain() => FakeDirectoryBuilder.DefaultDomain;

        public Task<IReadOnlyList<DomainControllerInfo>> FindDomainControllersAsync(string domain, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<DomainControllerInfo>>([]);

        public Task<IDirectorySession> ConnectAsync(ConnectionOptions options, NetworkCredential? credential, CancellationToken cancellationToken) =>
            Task.FromResult(session());

        public Task<IDirectorySession> ConnectToServerAsync(IDirectorySession current, string server, CancellationToken cancellationToken) =>
            connectToServer(current, server, cancellationToken);
    }
}

/// <summary>Theme service double recording the applied preference.</summary>
internal sealed class FakeThemeService : IThemeService
{
    public ThemePreference? Applied { get; private set; }

    public void Apply(ThemePreference preference) => Applied = preference;
}

/// <summary>Password box double for views that hand passwords over through <see cref="IPasswordInput"/>.</summary>
internal sealed class TestPasswordInput(string password) : IPasswordInput
{
    public string Password { get; set; } = password;

    public bool Cleared { get; private set; }

    public string GetPassword() => Password;

    public void Clear()
    {
        Cleared = true;
        Password = string.Empty;
    }
}
