using System.Globalization;
using AdMng.Core.Directory;
using AdMng.Presentation.Changes;
using AdMng.Presentation.Errors;
using AdMng.Presentation.Lists;
using AdMng.Presentation.Localization;
using AdMng.Presentation.Navigation;
using AdMng.Presentation.Session;
using AdMng.Presentation.Shell;
using AdMng.Presentation.Views;

namespace AdMng.Presentation.Tests.TestDoubles;

/// <summary>Real presentation services wired to test doubles (in-memory directory, scripted UI services).</summary>
internal sealed class TestHarness
{
    public static readonly DateTimeOffset Now = new(2026, 10, 13, 12, 0, 0, TimeSpan.Zero);

    private TestHarness(int bulkUsers, CultureInfo culture)
    {
        Time = new ManualTimeProvider(Now);
        Directory = TestDirectory.Build(Now, bulkUsers);
        Connector = new FakeDirectoryConnector(Directory);
        Localizer = new ResxLocalizer(culture);
        Errors = new ErrorTranslator(Localizer);
        Sessions = new SessionManager(Connector, new DelegateAttributeFormatterFactory(_ => new PassThroughAttributeFormatter()), Localizer);
        Lists = new ObjectListServices(Sessions, Localizer, Dispatcher, Settings, Errors, Changes, Time);
        Activities = new ActivityCenterViewModel(Dispatcher, Localizer, Errors, Time);
        Views = new ViewLibrary(Settings);
        Coordinator = new ChangeCoordinator(Sessions, Planner, Executor, Dialogs, Notifications, Settings, Activities, Changes, Errors, Localizer);
    }

    public ManualTimeProvider Time { get; }

    public InMemoryDirectorySession Directory { get; }

    public FakeDirectoryConnector Connector { get; }

    public ResxLocalizer Localizer { get; }

    public SynchronousUiDispatcher Dispatcher { get; } = new();

    public InMemorySettingsStore Settings { get; } = new();

    public ErrorTranslator Errors { get; }

    public DirectoryChangeNotifier Changes { get; } = new();

    public SessionManager Sessions { get; }

    public ObjectListServices Lists { get; }

    public RecordingChangePlanner Planner { get; } = new();

    public RecordingBatchExecutor Executor { get; } = new();

    public ScriptedDialogService Dialogs { get; } = new();

    public RecordingNotificationService Notifications { get; } = new();

    public FakeClipboard Clipboard { get; } = new();

    public FakeFileDialogService Files { get; } = new();

    public ActivityCenterViewModel Activities { get; }

    public ViewLibrary Views { get; }

    public ChangeCoordinator Coordinator { get; }

    public static async Task<TestHarness> ConnectedAsync(int bulkUsers = 0, CultureInfo? culture = null)
    {
        var harness = new TestHarness(bulkUsers, culture ?? CultureInfo.GetCultureInfo("en-US"));
        await harness.Sessions.ConnectAsync(new ConnectionOptions { Domain = "contoso.test" }, null, CancellationToken.None);
        return harness;
    }

    public static TestHarness Disconnected() => new(0, CultureInfo.GetCultureInfo("en-US"));

    public NavigationTreeViewModel CreateTree() => new(Sessions, Settings, Localizer, Dispatcher, Errors, Views, Changes);

    public ShellViewModel CreateShell() =>
        new(Lists, CreateTree(), Activities, Coordinator, Views, Dialogs, Files, Clipboard, Notifications, new SimpleCsvCodec());

    /// <summary>A list showing a container, loaded.</summary>
    public async Task<ObjectListViewModel> OpenContainerAsync(string dn)
    {
        var list = Lists.CreateList();
        await list.NavigateAsync(ListLocation.ForContainer(dn));
        return list;
    }
}
