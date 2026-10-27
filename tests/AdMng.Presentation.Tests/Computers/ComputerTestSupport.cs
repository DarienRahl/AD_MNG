using System.Globalization;
using AdMng.Core.Computers;
using AdMng.Core.Computers.Remote;
using AdMng.Core.Directory;
using AdMng.Core.Model;
using AdMng.Presentation.Computers;
using AdMng.Presentation.Dialogs;
using AdMng.Presentation.Services;
using AdMng.Presentation.Tests.AccountReview;
using AdMng.Presentation.Tests.Dialogs;
using AdMng.Presentation.Tests.TestDoubles;
using AdMng.Presentation.Views;
using AdMng.Testing;

namespace AdMng.Presentation.Tests.Computers;

/// <summary>
/// The computer inventory sample on the B2 harness (clock: 27 October 2026, time zone UTC) with the computer windows'
/// services: the fake remote client, resolver, ping and tool launcher, and a dialog service that also implements
/// <see cref="IComputerDialogs"/>.
/// </summary>
internal sealed class ComputerScenario
{
    private ComputerScenario(B2Harness harness)
    {
        Harness = harness;
        harness.Time.SetUtcNow(ComputerInventorySample.Now);
        Sample = harness.Builder.AddComputerInventorySample(harness.Company);
        Dialogs = new ComputerAwareDialogs(harness.Dialogs);
        Services = new DialogServices(
            harness.Sessions, harness.Localizer, harness.Dispatcher, harness.Settings, harness.Errors, harness.Changes, harness.Time,
            Dialogs, harness.Files, harness.Clipboard, harness.Notifications, harness.Coordinator, harness.Activities, harness.Lists, harness.Regional);
        Actions = new ComputerActions(Services, harness.Planner, Launcher, Ping, Resolver);
        Factory = new ComputerViewModelFactory(
            Services, harness.Engines, harness.Views, new WindowsReleaseCatalog(), Actions, harness.AuditLog, harness.PowerShell,
            new ServerConnector(harness), Resolver, Remote, Ping);
        Dialogs.Factory = Factory;
    }

    public B2Harness Harness { get; }

    public ComputerInventorySample Sample { get; }

    public SampleCompany Company => Harness.Company;

    public ComputerAwareDialogs Dialogs { get; }

    public DialogServices Services { get; }

    public FakeRemoteComputerClient Remote { get; } = new();

    public FakeHostAddressResolver Resolver { get; } = new();

    public FakePingService Ping { get; } = new();

    public FakeLocalToolLauncher Launcher { get; } = new();

    public ComputerActions Actions { get; }

    public ComputerViewModelFactory Factory { get; }

    public static async Task<ComputerScenario> CreateAsync(string culture = "en-US") =>
        new(await B2Harness.CreateAsync(CultureInfo.GetCultureInfo(culture)));

    /// <summary>Connected through a session of dc01 (a reconnect closes only that session, not the shared directory).</summary>
    public static async Task<ComputerScenario> CreateOnDomainControllerAsync() => new(await B2Harness.CreateOnDomainControllerAsync());

    /// <summary>A review with every report, run over the whole domain.</summary>
    public async Task<ComputerReviewViewModel> RunAsync(Action<ComputerReviewViewModel>? arrange = null)
    {
        var review = Factory.CreateReview();
        await review.InitializeAsync();
        arrange?.Invoke(review);
        await review.RunCommand.ExecuteAsync(null);
        return review;
    }

    /// <summary>The names in the list of a report's tile (the releases report: <paramref name="release"/>).</summary>
    public static IReadOnlyList<string> Names(ComputerReviewViewModel review, ComputerReport report, string? release = null)
    {
        review.SelectedTile = Assert.Single(review.Tiles, t => t.Report == report && t.Release == release);
        return [.. review.Items.Select(r => r.Name).Order(StringComparer.Ordinal)];
    }

    public static ComputerReviewRowViewModel Row(ComputerReviewViewModel review, ComputerReport report, string name)
    {
        review.SelectedTile = Assert.Single(review.Tiles, t => t.Report == report && t.Release is null);
        return Assert.Single(review.Items, r => r.Name == name);
    }
}

/// <summary>The scripted dialogs plus <see cref="IComputerDialogs"/> (what the WPF dialog service implements).</summary>
internal sealed class ComputerAwareDialogs(ScriptedDialogService inner) : IDialogService, IComputerDialogs
{
    public ScriptedDialogService Inner { get; } = inner;

    public ComputerViewModelFactory? Factory { get; set; }

    public List<string> RemoteWindows { get; } = [];

    public int ReviewsShown { get; private set; }

    public List<(ComputerAction Action, IReadOnlyList<string> Dns)> ActionsRun { get; } = [];

    /// <summary>Plays the restart dialog (null: Cancel).</summary>
    public Func<RestartComputerViewModel, bool>? RestartHandler { get; set; }

    public Task ShowComputerReviewAsync()
    {
        ReviewsShown++;
        return Task.CompletedTask;
    }

    public Task ShowRemoteActionsAsync(string distinguishedName)
    {
        RemoteWindows.Add(distinguishedName);
        return Task.CompletedTask;
    }

    public Task<RestartRequest?> AskRestartAsync(RemoteTarget target)
    {
        var dialog = Factory!.CreateRestart(target);
        return Task.FromResult(RestartHandler?.Invoke(dialog) == true ? dialog.Request : null);
    }

    public Task RunComputerActionAsync(ComputerAction action, IReadOnlyList<string> distinguishedNames)
    {
        ActionsRun.Add((action, distinguishedNames));
        return Factory?.Actions.RunAsync(action, distinguishedNames) ?? Task.CompletedTask;
    }

    public ComputerActions Actions => Factory!.Actions;

    public Task ShowInfoAsync(string title, string message) => Inner.ShowInfoAsync(title, message);

    public Task ShowErrorAsync(string title, string message) => Inner.ShowErrorAsync(title, message);

    public Task<bool> ConfirmAsync(string title, string message, bool destructive) => Inner.ConfirmAsync(title, message, destructive);

    public Task<bool> ConfirmTypedAsync(string title, string message, string expectedText) => Inner.ConfirmTypedAsync(title, message, expectedText);

    public Task<string?> PickContainerAsync(IDirectorySession session, string? startDn) => Inner.PickContainerAsync(session, startDn);

    public Task<IReadOnlyList<string>> PickObjectsAsync(IDirectorySession session, IReadOnlyList<ObjectKind> kinds, bool multiSelect) =>
        Inner.PickObjectsAsync(session, kinds, multiSelect);

    public Task ShowPropertiesAsync(string distinguishedName) => Inner.ShowPropertiesAsync(distinguishedName);

    public Task ShowBatchEditorAsync(BatchEditorRequest request) => Inner.ShowBatchEditorAsync(request);

    public Task ShowResetPasswordAsync(IReadOnlyList<string> distinguishedNames) => Inner.ShowResetPasswordAsync(distinguishedNames);

    public Task<string?> ShowNewObjectAsync(ObjectKind kind, string parentDn) => Inner.ShowNewObjectAsync(kind, parentDn);

    public Task ShowFindAsync(string? startDn) => Inner.ShowFindAsync(startDn);

    public Task ShowSessionLogAsync() => Inner.ShowSessionLogAsync();

    public Task ShowRecycleBinAsync(Guid? select = null) => Inner.ShowRecycleBinAsync(select);

    public Task ShowAccountReviewAsync() => Inner.ShowAccountReviewAsync();

    public Task ShowSettingsAsync() => Inner.ShowSettingsAsync();

    public Task ShowConnectAsync() => Inner.ShowConnectAsync();

    public Task<bool> ShowChangeDomainControllerAsync() => Inner.ShowChangeDomainControllerAsync();

    public Task<bool> ShowSavedViewEditorAsync(SavedViewEditorViewModel editor) => Inner.ShowSavedViewEditorAsync(editor);

    public Task<bool> ShowFilterEditorAsync(AdMng.Presentation.Filters.FilterEditorViewModel editor) => Inner.ShowFilterEditorAsync(editor);

    public Task ShowSavedFiltersAsync() => Inner.ShowSavedFiltersAsync();

    public Task<bool> ShowColumnChooserAsync(AdMng.Presentation.Lists.ColumnChooserViewModel chooser) => Inner.ShowColumnChooserAsync(chooser);

    public Task<bool> ShowLogonHoursAsync(LogonHoursViewModel editor) => Inner.ShowLogonHoursAsync(editor);

    public Task<bool> ShowWatchdogCertificateTrustAsync(AdMng.Presentation.Watchdog.WatchdogCertificateTrustViewModel dialog) => Inner.ShowWatchdogCertificateTrustAsync(dialog);
}
