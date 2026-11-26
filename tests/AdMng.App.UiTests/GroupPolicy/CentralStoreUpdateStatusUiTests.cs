using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using AdMng.App.Controls;
using AdMng.App.GroupPolicy;
using AdMng.App.Infrastructure;
using AdMng.App.Services;
using AdMng.App.UiTests.Infrastructure;
using AdMng.Presentation.Feedback;
using AdMng.Presentation.GroupPolicy;
using AdMng.Presentation.GroupPolicy.AdministrativeTemplates;
using AdMng.Presentation.Localization;
using AdMng.Presentation.Services;
using AdMng.Presentation.Session;
using AdMng.Testing;
using Microsoft.Extensions.DependencyInjection;
using static AdMng.App.UiTests.GroupPolicy.GroupPolicyStatusUiSupport;

namespace AdMng.App.UiTests.GroupPolicy;

/// <summary>
/// The status line of the window that updates the ADMX templates of the central store (track V, stage V2b-3;
/// docs/dev/v-interface-renewal.md "V2b-3 as built"), through the real window over the fake directory: one line at the top,
/// what the comparison found as its summary, a download with Stop - stopped, it offers the download again; failed, it says
/// why and offers Retry -, "Update…" as an action button that is also the Cancel of the update it started, and the missing
/// store as a note over the files. A slow vendor is a request the test holds (it ends when it is cancelled), a failing one an
/// answer of the fake handler; every state that is asserted lasts until the test changes it. How a package is compared and
/// written is covered by <see cref="GroupPolicyWindowTests"/>.
/// </summary>
[Collection(UiTestCollection.Name)]
public sealed class CentralStoreUpdateStatusUiTests
{
    [Fact]
    public Task TheWindow_HasOneStatusLine_AtTheTop_TheComparisonIsItsSummary_AndAMissingStoreIsANote() => WpfTestHost.RunAsync(async () =>
    {
        var files = new PreparedSaveDialog();
        await using var context = await UiTestContext.CreateAsync(configure: s => s.AddSingleton<IFileDialogService>(files));
        context.Directory.AddGroupPolicyManagementSample(context.Company, context.Sysvol);
        await context.ConnectAsync();
        using var folder = new TempFolder();
        var (window, update) = Open(context);
        await UiTestSupport.WaitForIdleAsync(rounds: 2);
        var line = window.PageStatus;

        // One status line, named PageStatus, over the status of the window, at the top - above the target and the sources -
        // of the fixed height, and empty: nothing was loaded.
        Assert.Same(line, Assert.Single(UiWait.Descendants<StatusLine>(window)));
        Assert.True(line.IsVisible);
        Assert.IsAssignableFrom<IHasPageStatus>(update);
        Assert.Same(update.Status.Current, line.Status);
        Assert.Same(update.Status.CloseCommand, line.CloseCommand);
        Assert.True(TopOf(line, window) < TopOf(window.TargetText, window), "the status line is not above the target");
        Assert.True(TopOf(window.TargetText, window) < TopOf(window.OpenFileButton, window), "the target is not above the sources");
        Assert.Equal((double)Application.Current.FindResource("Ad.Size.StatusLine"), line.ActualHeight);
        Assert.Equal(PageStatusKind.Idle, line.Kind);
        Assert.Equal(string.Empty, line.Message);
        Assert.False(line.HasAction);
        Assert.False(line.CanClose);

        // "Update…" is an action button, the default button, in the accent of the dialogs; Close is the cancel button.
        Assert.Equal(ActionButtonState.Idle, window.UpdateButton.State);
        Assert.Same(update.UpdateCommand, window.UpdateButton.Command);
        Assert.Same(update.Status.CancelCommand, window.UpdateButton.CancelCommand);
        Assert.True(window.UpdateButton.IsDefault);
        Assert.Same(Application.Current.FindResource("Ad.AccentDialogActionButton"), window.UpdateButton.Style);
        Assert.False(window.UpdateButton.IsEnabled);
        Assert.True(window.CloseButton.IsCancel);
        Assert.False(window.StoreNoteBar.IsVisible);

        // A package is chosen: the comparison says how many files are written - the line, not the footer.
        files.NextOpenPath = await ArchiveAsync(folder.Path);
        ControlHost.Press(window.OpenFileButton);
        await UiWait.UntilAsync(() => !update.Status.IsWorking && update.Rows.Count > 0, "the comparison");
        await UiTestSupport.WaitForIdleAsync(rounds: 2);
        Assert.Equal(PageStatusKind.Idle, line.Kind);
        Assert.Matches(@"^To write: \d+, unchanged: \d+, skipped: \d+$", line.Message);
        Assert.Equal(update.Status.Idle.Message, line.Message);

        // The store does not exist yet: a note over the files - true for as long as they are shown - and no status.
        Assert.True(window.StoreNoteBar.IsVisible);
        Assert.Same(Application.Current.FindResource("Ad.Note.Attention"), window.StoreNoteBar.Style);
        Assert.Equal("The domain has no central store yet: the update creates it.", window.StoreNoteText.Text);
        Assert.True(TopOf(window.StoreNoteBar, window) < TopOf(window.FileList, window), "the note is not above the files");
        AssertNoOldFeedback(window);
        context.AssertHealthy();
    });

    [Fact]
    public Task TheDownload_SaysDownloadingWithStop_AStoppedOneOffersItAgain_AndAFailedOneIsAnErrorWithRetry() => WpfTestHost.RunAsync(async () =>
    {
        await using var context = await UiTestContext.CreateAsync();
        context.Directory.AddGroupPolicyManagementSample(context.Company, context.Sysvol);
        await context.ConnectAsync();
        var handler = new VendorHandler { Hold = true };
        var (window, update) = Open(context, new PolicyTemplatePackageLoader(handler: () => handler));
        var line = window.PageStatus;
        var action = ControlHost.Part<Button>(line, "PART_ActionButton");
        var close = ControlHost.Part<Button>(line, "PART_CloseButton");
        var ring = ControlHost.Part<ProgressRing>(line, "PART_Ring");
        var contentTop = TopOf(window.TargetText, window);

        // The vendor does not answer: "downloading" lasts. The sources wait; the window can be closed.
        ControlHost.Press(window.EdgeButton);
        await UiWait.UntilAsync(() => handler.IsReached && line.Kind == PageStatusKind.Working, "the download is running");
        Assert.Equal("Working", line.Title);
        Assert.Equal("Downloading the templates of Microsoft Edge…", line.Message);
        Assert.True(ring.IsActive);
        Assert.Equal(Visibility.Visible, action.Visibility);
        Assert.Equal("Stop", Assert.IsType<string>(action.Content));
        Assert.Same(update.Status.CancelCommand, action.Command);
        Assert.Equal(Visibility.Collapsed, close.Visibility);
        Assert.False(window.EdgeButton.IsEnabled);
        Assert.False(window.OpenFileButton.IsEnabled);
        Assert.True(window.CloseButton.IsEnabled);
        AssertNoOldFeedback(window);
        window.UpdateLayout();
        Assert.Equal(contentTop, TopOf(window.TargetText, window));

        // Stop, pressed in the line: "Stopping…" at once, and the download that is on its way ends with it.
        ControlHost.Press(action);
        await UiWait.UntilAsync(() => update.Status.IsCancelling || line.Kind == PageStatusKind.Attention, "the download is stopping");
        await UiWait.UntilAsync(() => !update.IsBusy && line.Kind == PageStatusKind.Attention, "the download was stopped");
        await UiTestSupport.WaitForIdleAsync(rounds: 2);

        // Stopped: what was shown stays (nothing was), the sources are free, and the line offers the same download.
        Assert.Equal("Attention", line.Title);
        Assert.Equal("Loading the templates was stopped: the package shown was not changed.", line.Message);
        Assert.Equal(AppIcons.StatusWarning, line.IconKey);
        Assert.Equal(AutomationLiveSetting.Assertive, AutomationProperties.GetLiveSetting(line));
        Assert.False(ring.IsActive);
        Assert.Equal(Visibility.Visible, action.Visibility);
        Assert.Equal("Retry", Assert.IsType<string>(action.Content));
        Assert.Same(update.DownloadEdgeCommand, action.Command);
        Assert.Equal(Visibility.Visible, close.Visibility);
        Assert.True(window.EdgeButton.IsEnabled);
        window.UpdateLayout();
        Assert.Equal(contentTop + GrowthOf(line), TopOf(window.TargetText, window), 0.5);

        // Retry, with a vendor that answers with an error: what was tried, why, and Retry again - a result that can be closed.
        handler.Hold = false;
        await UiWait.UntilAsync(() => action.IsEnabled, "the action of the line can be pressed");
        ControlHost.Press(action);
        await UiWait.UntilAsync(() => !update.IsBusy && line.Kind == PageStatusKind.Error, "the download failed");
        await UiTestSupport.WaitForIdleAsync(rounds: 2);
        Assert.Equal("Error", line.Title);
        Assert.StartsWith("Could not download the templates of Microsoft Edge: ", line.Message, StringComparison.Ordinal);
        Assert.EndsWith(" Press Retry to try again.", line.Message, StringComparison.Ordinal);
        Assert.Equal(AppIcons.StatusError, line.IconKey);
        Assert.Equal(Visibility.Visible, action.Visibility);
        Assert.Equal("Retry", Assert.IsType<string>(action.Content));
        Assert.Equal(Visibility.Visible, close.Visibility);
        ControlHost.Press(close);
        await UiWait.UntilAsync(() => line.Kind == PageStatusKind.Idle, "the error was closed");
        Assert.Equal(string.Empty, line.Message);
        context.AssertHealthy();
    });

    [Fact]
    public Task TheUpdateButton_IsTheCancelOfTheUpdateItStarted_AStoppedUpdateOffersItAgain_AndTheEndOfTheSessionLasts() => WpfTestHost.RunAsync(async () =>
    {
        var files = new PreparedSaveDialog();
        await using var context = await UiTestContext.CreateAsync(configure: s => s.AddSingleton<IFileDialogService>(files));
        context.Directory.AddGroupPolicyManagementSample(context.Company, context.Sysvol);
        foreach (var (path, content) in SampleAdministrativeTemplates.Files(polish: false))
        {
            context.Sysvol.PutPolicyDefinition(path, content);
        }

        await context.ConnectAsync();
        using var folder = new TempFolder();
        files.NextOpenPath = await ArchiveAsync(folder.Path, newer: true);

        // The backup of the store waits for the test (until the update is stopped).
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gp = context.Services.GetRequiredService<GroupPolicyServices>();
        var factory = context.Services.GetRequiredService<GroupPolicyViewModelFactory>();
        var update = new CentralStoreUpdateViewModel(gp, factory.Definitions, factory.Loader)
        {
            BackupFolder = folder.Path,
            WriteBackup = async (path, server, backupFiles, time, token) =>
            {
                reached.TrySetResult();
                await gate.Task.WaitAsync(token);
                return path;
            },
        };
        var window = (CentralStoreUpdateWindow)context.Services.GetRequiredService<IWindowService>().CreateWindow(update);
        UiTestSupport.ShowOffScreen(window);
        var line = window.PageStatus;
        ControlHost.Press(window.OpenFileButton);
        await UiWait.UntilAsync(() => !update.Status.IsWorking && update.UpdateCommand.CanExecute(null), "the comparison");
        await UiTestSupport.WaitForIdleAsync(rounds: 2);
        var action = ControlHost.Part<Button>(line, "PART_ActionButton");
        var close = ControlHost.Part<Button>(line, "PART_CloseButton");
        var summary = line.Message;
        Assert.True(window.UpdateButton.IsEnabled);
        Assert.Equal(ActionButtonState.Idle, window.UpdateButton.State);
        var contentTop = TopOf(window.TargetText, window);

        ControlHost.Press(window.UpdateButton);
        await reached.Task.WaitAsync(TimeSpan.FromSeconds(30));
        await UiWait.UntilAsync(() => line.Kind == PageStatusKind.Working, "the update is running");

        // Working: the backup, with Stop in the line - and the button that started the update says "Cancel". Nothing moved;
        // the window cannot be closed meanwhile.
        Assert.Equal("Backing up the central store…", line.Message);
        Assert.Equal("Stop", Assert.IsType<string>(action.Content));
        Assert.Equal(ActionButtonState.Running, window.UpdateButton.State);
        Assert.True(window.UpdateButton.IsEnabled);
        Assert.False(window.CloseButton.IsEnabled);
        window.UpdateLayout();
        Assert.Equal(contentTop, TopOf(window.TargetText, window));

        // Cancel, pressed on the button that started the update: "Stopping…" and then the result.
        ControlHost.Press(window.UpdateButton);
        await UiWait.UntilAsync(() => !update.IsUpdating && line.Kind == PageStatusKind.Attention, "the update was stopped");
        await UiTestSupport.WaitForIdleAsync(rounds: 2);
        Assert.Equal("The update was stopped: the central store was not changed. Press “Update…” to try again.", line.Message);
        Assert.Equal("Update…", Assert.IsType<string>(action.Content));
        Assert.Same(update.UpdateCommand, action.Command);
        Assert.Equal(Visibility.Visible, close.Visibility);
        Assert.Equal(ActionButtonState.Idle, window.UpdateButton.State);
        Assert.True(window.CloseButton.IsEnabled);
        Assert.False(update.ChangedStore);
        Assert.Equal(SampleAdministrativeTemplates.Utf8(SampleAdministrativeTemplates.ContosoAdmx), context.Sysvol.GetPolicyDefinition("contoso.admx"));
        window.UpdateLayout();
        Assert.Equal(contentTop + GrowthOf(line), TopOf(window.TargetText, window), 0.5);

        // Closed, the line says what the comparison found again.
        ControlHost.Press(close);
        await UiWait.UntilAsync(() => line.Kind == PageStatusKind.Idle, "the result was closed");
        Assert.Equal(summary, line.Message);

        // The session of the window ends: an error that cannot be closed takes the line; "Update…" cannot be pressed.
        await context.Services.GetRequiredService<ISessionManager>().DisconnectAsync();
        await UiWait.UntilAsync(() => update.IsSessionEnded && line.Kind == PageStatusKind.Error, "the end of the session in the line");
        await UiTestSupport.WaitForIdleAsync(rounds: 2);
        Assert.Equal(context.Localizer()[L.WindowSession.Ended], line.Message);
        Assert.False(line.HasAction);
        Assert.False(line.CanClose);
        Assert.False(window.UpdateButton.IsEnabled);
        Assert.True(window.CloseButton.IsEnabled);
        gate.TrySetResult();
        context.AssertHealthy();
    });

    // ---- Helpers ------------------------------------------------------------------------------------------------

    /// <summary>The window, shown off-screen (<paramref name="loader"/>: the loader of the app unless a test brings its own).</summary>
    private static (CentralStoreUpdateWindow Window, CentralStoreUpdateViewModel Update) Open(UiTestContext context, PolicyTemplatePackageLoader? loader = null)
    {
        var gp = context.Services.GetRequiredService<GroupPolicyServices>();
        var factory = context.Services.GetRequiredService<GroupPolicyViewModelFactory>();
        var update = new CentralStoreUpdateViewModel(gp, factory.Definitions, loader ?? factory.Loader);
        var window = (CentralStoreUpdateWindow)context.Services.GetRequiredService<IWindowService>().CreateWindow(update);
        UiTestSupport.ShowOffScreen(window);
        return (window, update);
    }

    /// <summary>Writes an archive of the sample templates (newer: a higher revision of contoso.admx) and returns its path.</summary>
    private static async Task<string> ArchiveAsync(string folder, bool newer = false)
    {
        var admx = newer
            ? SampleAdministrativeTemplates.ContosoAdmx.Replace("revision=\"1.2\"", "revision=\"1.3\"", StringComparison.Ordinal)
            : SampleAdministrativeTemplates.ContosoAdmx;
        using var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            void Add(string path, string text)
            {
                using var entry = zip.CreateEntry(path).Open();
                entry.Write(SampleAdministrativeTemplates.Utf8(text));
            }

            Add("windows/admx/contoso.admx", admx);
            Add("windows/admx/en-US/contoso.adml", SampleAdministrativeTemplates.ContosoAdmlEn);
            Add("windows/admx/pl/contoso.adml", SampleAdministrativeTemplates.ContosoAdmlPl);
        }

        var path = Path.Combine(folder, "policy_templates.zip");
        await File.WriteAllBytesAsync(path, stream.ToArray());
        return path;
    }

    /// <summary>The old places of feedback are gone from the window: no progress bar of its own. The banners of before cannot come back - XamlSourceTests guards every view.</summary>
    private static void AssertNoOldFeedback(CentralStoreUpdateWindow window) => GroupPolicyStatusUiSupport.AssertNoOldFeedback(window);

    /// <summary>The vendor of a download: it does not answer while <see cref="Hold"/> (the request ends when it is cancelled), else it refuses.</summary>
    private sealed class VendorHandler : HttpMessageHandler
    {
        private int _reached;

        public bool Hold { get; set; }

        /// <summary>A request is being held.</summary>
        public bool IsReached => Volatile.Read(ref _reached) == 1;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (Hold)
            {
                Volatile.Write(ref _reached, 1);
                await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
            }

            return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) { RequestMessage = request };
        }
    }

    /// <summary>A folder that goes with the test.</summary>
    private sealed class TempFolder : IDisposable
    {
        public TempFolder()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "admng-v2b3-store-" + Guid.NewGuid().ToString("N"));
            System.IO.Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try
            {
                System.IO.Directory.Delete(Path, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}
