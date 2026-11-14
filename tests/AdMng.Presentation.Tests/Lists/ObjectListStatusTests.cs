using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using AdMng.Core.Directory;
using AdMng.Presentation.Changes;
using AdMng.Presentation.Feedback;
using AdMng.Presentation.Icons;
using AdMng.Presentation.Lists;
using AdMng.Presentation.Localization;
using AdMng.Presentation.Shell;
using AdMng.Presentation.Tests.TestDoubles;
using AdMng.Testing;
using static AdMng.Presentation.Tests.TestDoubles.TestDirectory;

namespace AdMng.Presentation.Tests.Lists;

/// <summary>
/// The status line of the object list (track V, stage V2; the first page of docs/dev/v-interface-renewal.md 6.1): the
/// summary of what is loaded, a running load as an operation that can be stopped, a stopped load as "attention" with
/// Refresh, a failed one as "error" with Retry - and the status bar of the window, which no longer repeats it.
/// </summary>
public class ObjectListStatusTests
{
    private const string Bulk = "OU=Masowe,DC=contoso,DC=test";
    private static readonly CultureInfo English = CultureInfo.GetCultureInfo("en-US");
    private static readonly CultureInfo Polish = CultureInfo.GetCultureInfo("pl-PL");

    [Fact]
    public async Task ALoadedList_ShowsItsSummary_AndTheSelection()
    {
        var harness = await TestHarness.ConnectedAsync();
        var list = harness.Lists.CreateList();

        // Nothing loaded: the line says nothing.
        Assert.Same(PageStatus.Empty, list.Status.Current);

        await list.NavigateAsync(ListLocation.ForContainer(It));
        var time = TestHarness.Now.ToString("T", English);
        var status = list.Status.Current;
        Assert.Equal(PageStatusKind.Idle, status.Kind);
        Assert.Equal($"6 objects · 0.00 s · as of {time}", status.Message);
        Assert.Equal(list.StatusText, status.Message);
        Assert.False(list.Status.IsWorking);
        Assert.Null(list.Status.Operation);
        Assert.Null(status.Action);
        Assert.False(status.CanClose);

        // The selection joins the summary - one sentence in one place.
        list.SelectedItem = list.Items[0];
        Assert.Equal($"6 objects · 0.00 s · as of {time} · 1 selected", list.Status.Current.Message);
        list.SelectedItems = new ObservableCollection<object> { list.Items[0], list.Items[1], list.Items[2] };
        Assert.Equal($"6 objects · 0.00 s · as of {time} · 3 selected", list.Status.Current.Message);
        list.SelectedItems = null;
        list.SelectedItem = null;
        Assert.Equal($"6 objects · 0.00 s · as of {time}", list.Status.Current.Message);
        Assert.True(list.Status.Current.IsIdle);
    }

    [Fact]
    public async Task ALoad_IsTheOperationOfThePage_WithTheCountSoFar_AndEndsInTheSummary()
    {
        var harness = await TestHarness.ConnectedAsync(bulkUsers: 300);
        var list = harness.Lists.CreateList();
        var working = new List<string>();
        var kinds = new List<PageStatusKind>();
        list.Status.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != nameof(PageStatusHolder.Current))
            {
                return;
            }

            kinds.Add(list.Status.Current.Kind);
            if (list.Status.Current.IsWorking)
            {
                working.Add(list.Status.Current.Message);
            }
        };

        await list.NavigateAsync(ListLocation.ForContainer(Bulk));

        // The action and its scope from the first moment, then the count so far with every batch of rows.
        Assert.Equal(
            ["Reading “Masowe”…", "Reading “Masowe”: 50 objects so far…", "Reading “Masowe”: 300 objects so far…"],
            working);
        Assert.Equal(PageStatusKind.Working, kinds[0]);
        Assert.DoesNotContain(PageStatusKind.Done, kinds);
        Assert.DoesNotContain(PageStatusKind.Attention, kinds);
        Assert.DoesNotContain(PageStatusKind.Error, kinds);

        // The result of a load is the list itself: no "done", the summary again.
        var time = TestHarness.Now.ToString("T", English);
        Assert.Equal(PageStatusKind.Idle, list.Status.Current.Kind);
        Assert.Equal($"300 objects · 0.00 s · as of {time}", list.Status.Current.Message);
        Assert.False(list.Status.IsWorking);
        Assert.Null(list.Status.Operation);
        Assert.False(list.IsLoading);
    }

    [Fact]
    public async Task ARunningLoad_CanBeStoppedFromTheStatusLine_AndAnswersAtOnce()
    {
        var harness = await TestHarness.ConnectedAsync(bulkUsers: 300);
        var list = harness.Lists.CreateList();
        var gate = new TaskCompletionSource();
        harness.Directory.Hold = _ => gate.Task;

        var load = list.NavigateAsync(ListLocation.ForContainer(Bulk));

        // Working, with "Stop" as the action of the line.
        Assert.True(list.IsLoading);
        Assert.True(list.Status.IsWorking);
        Assert.False(list.Status.IsCancelling);
        Assert.Equal("Reading “Masowe”…", list.Status.Current.Message);
        Assert.True(Assert.NotNull(list.Status.Current.Progress).IsIndeterminate);
        var stop = Assert.IsType<PageStatusAction>(list.Status.Current.Action);
        Assert.Equal("Stop", stop.Label);
        Assert.Same(list.Status.CancelCommand, stop.Command);
        Assert.True(stop.Command.CanExecute(stop.Parameter));
        Assert.False(list.Status.CloseCommand.CanExecute(null));

        // Pressed: the line says "Stopping…" before the load noticed - whenever that is, here it may be inside the
        // call - and while it stops there is nothing left to press.
        var token = list.Status.Operation!.Token;
        var seen = Record(list);
        stop.Command.Execute(stop.Parameter);
        Assert.True(token.IsCancellationRequested);

        await load;
        Assert.Equal(2, seen.Count);
        Assert.Equal((PageStatusKind.Working, "Stopping…", false, false), seen[0]);
        Assert.Equal(PageStatusKind.Attention, seen[1].Kind);

        // Stopped before all results arrived: attention, what is there, and Refresh as the way on.
        var stopped = list.Status.Current;
        Assert.Equal(PageStatusKind.Attention, stopped.Kind);
        Assert.Equal(
            "Reading “Masowe” was stopped before all results arrived: 0 objects loaded. Press Refresh to read the whole list.",
            stopped.Message);
        Assert.True(stopped.CanClose);
        Assert.Equal("List.Status.Stopped (0)", stopped.Log.ToString());
        var refresh = Assert.IsType<PageStatusAction>(stopped.Action);
        Assert.Equal("Refresh", refresh.Label);
        Assert.Same(list.RefreshCommand, refresh.Command);
        Assert.True(list.Partial);
        Assert.False(list.IsLoading);
        Assert.False(list.Status.IsWorking);
        Assert.False(list.Status.IsCancelling);
        Assert.Null(list.Status.Operation);

        // Closed, the line still says that the list is incomplete.
        list.Status.CloseCommand.Execute(null);
        Assert.Equal(PageStatusKind.Idle, list.Status.Current.Kind);
        Assert.Equal("Stopped – 0 objects loaded (incomplete list)", list.Status.Current.Message);

        // Refresh reads the whole list.
        harness.Directory.Hold = null;
        await list.RefreshCommand.ExecuteAsync(null);
        Assert.Equal(300, list.Items.Count);
        Assert.False(list.Partial);
        Assert.Equal(PageStatusKind.Idle, list.Status.Current.Kind);
        Assert.StartsWith("300 objects", list.Status.Current.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CancelLoad_TheListsOwnCommand_GoesThroughTheSameOperation()
    {
        var harness = await TestHarness.ConnectedAsync(bulkUsers: 300);
        var list = harness.Lists.CreateList();
        var gate = new TaskCompletionSource();
        harness.Directory.Hold = _ => gate.Task;
        var load = list.NavigateAsync(ListLocation.ForContainer(Bulk));
        var token = Assert.IsType<OperationScope>(list.Status.Operation).Token;

        Assert.True(list.CancelLoadCommand.CanExecute(null));
        var seen = Record(list);
        list.CancelLoadCommand.Execute(null);
        Assert.True(token.IsCancellationRequested);

        await load;
        Assert.Equal([PageStatusKind.Working, PageStatusKind.Attention], seen.Select(status => status.Kind));
        Assert.Equal("Stopping…", seen[0].Message);
        Assert.Equal(PageStatusKind.Attention, list.Status.Current.Kind);
        Assert.False(list.CancelLoadCommand.CanExecute(null));
        Assert.False(list.Status.IsCancelling);
    }

    [Fact]
    public async Task AFailedLoad_IsAnErrorWithRetry_ThatStaysUntilTheListCouldBeRead()
    {
        var harness = await TestHarness.ConnectedAsync();
        var list = harness.Lists.CreateList();
        harness.Directory.FailSearch = search => search.BaseDn == It
            ? new DirectoryException(DirectoryErrorKind.ServerUnavailable, "81: Server Down, CN=Łukasz Żółć") { LdapResultCode = 81, DistinguishedName = Lukasz }
            : null;

        await list.NavigateAsync(ListLocation.ForContainer(It));

        // What we tried: why it failed. What to do next. (code)
        var failed = list.Status.Current;
        Assert.Equal(PageStatusKind.Error, failed.Kind);
        Assert.Equal("The domain controller is not available.", list.Error);
        Assert.Equal("Could not read “IT”: The domain controller is not available. Press Retry to try again. (LDAP 81)", failed.Message);
        Assert.Equal("List.Status.ReadFailed [LDAP 81]", failed.Log.ToString());
        Assert.False(failed.CanClose);
        Assert.False(list.Status.CloseCommand.CanExecute(null));
        Assert.False(list.Status.IsWorking);
        Assert.False(list.IsLoading);
        Assert.Empty(list.Items);
        var retry = Assert.IsType<PageStatusAction>(failed.Action);
        Assert.Equal("Retry", retry.Label);
        Assert.Same(list.RefreshCommand, retry.Command);

        // The close command does not take the only explanation of the empty list away.
        list.Status.CloseCommand.Execute(null);
        Assert.Same(failed, list.Status.Current);

        // Retry reads the list: the error is gone, the summary is back.
        await list.RefreshCommand.ExecuteAsync(null);
        Assert.Null(list.Error);
        Assert.Equal(6, list.Items.Count);
        Assert.Equal(PageStatusKind.Idle, list.Status.Current.Kind);
        Assert.StartsWith("6 objects", list.Status.Current.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NotConnected_IsAnErrorOfTheSameShape_WithoutACode()
    {
        var harness = TestHarness.Disconnected();
        var list = harness.Lists.CreateList();

        await list.NavigateAsync(ListLocation.ForContainer(It));

        var status = list.Status.Current;
        Assert.Equal(PageStatusKind.Error, status.Kind);
        Assert.Equal("Could not read “IT”: Not connected to a domain. Press Retry to try again.", status.Message);
        Assert.Equal("List.Status.ReadFailed", status.Log.ToString());
        Assert.Equal("Retry", status.Action!.Label);
        Assert.False(status.CanClose);
        Assert.False(list.Status.IsWorking);
        Assert.Null(list.Status.Operation);
    }

    [Fact]
    public async Task ADeletedLocation_IsALastingAttention_AlsoWhileALoadRuns()
    {
        var harness = await TestHarness.ConnectedAsync(bulkUsers: 300);
        var list = await harness.OpenContainerAsync(It);

        await list.HandleChangesAsync([new DirectoryChange(DirectoryChangeKind.Deleted, It)]);

        var gone = list.Status.Current;
        Assert.Equal(PageStatusKind.Attention, gone.Kind);
        Assert.Equal("This container no longer exists. Choose another container in the tree.", gone.Message);
        Assert.False(gone.CanClose);
        Assert.Null(gone.Action);
        Assert.Equal(L.List.LocationDeleted, gone.Log.Key);
        Assert.Equal("This container no longer exists.", list.Error);

        // Deleted while its load runs: the load is over, and what it says when it notices is ignored.
        var gate = new TaskCompletionSource();
        harness.Directory.Hold = _ => gate.Task;
        var load = list.NavigateAsync(ListLocation.ForContainer(Bulk));
        Assert.True(list.Status.IsWorking);
        var token = list.Status.Operation!.Token;

        await list.HandleChangesAsync([new DirectoryChange(DirectoryChangeKind.Deleted, Bulk)]);
        Assert.True(token.IsCancellationRequested);
        Assert.False(list.Status.IsWorking);
        Assert.Equal(PageStatusKind.Attention, list.Status.Current.Kind);
        var shown = list.Status.Current;
        await load;
        Assert.Same(shown, list.Status.Current);
        Assert.False(list.Status.IsCancelling);
    }

    [Fact]
    public async Task ANewLoad_ReplacesTheRunningOne_AndTheLineFollowsTheNewOne()
    {
        var harness = await TestHarness.ConnectedAsync(bulkUsers: 300);
        var list = harness.Lists.CreateList();
        var gate = new TaskCompletionSource();
        harness.Directory.Hold = search => search.BaseDn.StartsWith("OU=Masowe", StringComparison.Ordinal) ? gate.Task : null;

        var first = list.NavigateAsync(ListLocation.ForContainer(Bulk));
        var replaced = Assert.IsType<OperationScope>(list.Status.Operation);
        Assert.Equal("Reading “Masowe”…", list.Status.Current.Message);

        await list.NavigateAsync(ListLocation.ForContainer(It));
        gate.SetResult();
        await first;

        // The first load was not "stopped" by anybody: no attention, the summary of the second one.
        Assert.True(replaced.Token.IsCancellationRequested);
        Assert.True(replaced.IsEnded);
        Assert.Equal(PageStatusKind.Idle, list.Status.Current.Kind);
        Assert.StartsWith("6 objects", list.Status.Current.Message, StringComparison.Ordinal);
        Assert.False(list.Status.IsWorking);
        Assert.False(list.Partial);
    }

    [Fact]
    public async Task Dispose_EndsTheRunningLoad_WithoutAMessage()
    {
        var harness = await TestHarness.ConnectedAsync(bulkUsers: 300);
        var list = harness.Lists.CreateList();
        var gate = new TaskCompletionSource();
        harness.Directory.Hold = _ => gate.Task;
        var load = list.NavigateAsync(ListLocation.ForContainer(Bulk));
        var token = list.Status.Operation!.Token;

        list.Dispose();
        Assert.True(token.IsCancellationRequested);
        Assert.False(list.Status.IsWorking);
        Assert.Null(list.Status.Operation);

        await load;
        Assert.NotEqual(PageStatusKind.Attention, list.Status.Current.Kind);
        Assert.False(list.Status.IsWorking);
    }

    [Fact]
    public async Task Polish_UsesThePluralForms_AndTheSameShapes()
    {
        var harness = await TestHarness.ConnectedAsync(bulkUsers: 300, culture: Polish);
        var list = harness.Lists.CreateList();
        var working = new List<string>();
        list.Status.PropertyChanged += (_, _) =>
        {
            if (list.Status.Current.IsWorking && !working.Contains(list.Status.Current.Message))
            {
                working.Add(list.Status.Current.Message);
            }
        };

        await list.NavigateAsync(ListLocation.ForContainer(Bulk));
        Assert.Equal(
            ["Wczytywanie „Masowe”…", "Wczytywanie „Masowe”: na razie 50 obiektów…", "Wczytywanie „Masowe”: na razie 300 obiektów…"],
            working);
        Assert.StartsWith("Obiektów: 300", list.Status.Current.Message, StringComparison.Ordinal);

        // Stopped.
        var gate = new TaskCompletionSource();
        harness.Directory.Hold = _ => gate.Task;
        var load = list.RefreshAsync();
        Assert.Equal("Zatrzymaj", list.Status.Current.Action!.Label);
        list.CancelLoad();
        await load;
        Assert.Contains("Zatrzymywanie…", working);
        Assert.Equal(
            "Wczytywanie „Masowe” zatrzymano, zanim nadeszły wszystkie wyniki: wczytano 0 obiektów. Wybierz Odśwież, aby wczytać całą listę.",
            list.Status.Current.Message);
        Assert.Equal("Odśwież", list.Status.Current.Action!.Label);

        // Failed.
        harness.Directory.Hold = null;
        harness.Directory.FailSearch = search => search.BaseDn == Bulk
            ? new DirectoryException(DirectoryErrorKind.AccessDenied, "00000005: SecErr") { LdapResultCode = 50, Win32Error = 5 }
            : null;
        await list.RefreshAsync();
        Assert.Equal(PageStatusKind.Error, list.Status.Current.Kind);
        Assert.Equal(
            $"Nie udało się wczytać „Masowe”: {harness.Errors.Translate(DirectoryErrorKind.AccessDenied)} Wybierz Ponów, aby spróbować ponownie. (LDAP 50, 0x5)",
            list.Status.Current.Message);
        Assert.Equal("Ponów", list.Status.Current.Action!.Label);
    }

    [Fact]
    public async Task ALanguageSwitch_RewritesTheSummary()
    {
        var harness = await TestHarness.ConnectedAsync();
        var list = await harness.OpenContainerAsync(It);
        list.SelectedItem = list.Items[0];
        Assert.StartsWith("6 objects", list.Status.Current.Message, StringComparison.Ordinal);
        Assert.EndsWith("1 selected", list.Status.Current.Message, StringComparison.Ordinal);

        harness.Localizer.SetCulture(Polish);

        Assert.StartsWith("Obiektów: 6", list.Status.Current.Message, StringComparison.Ordinal);
        Assert.EndsWith("Zaznaczono: 1", list.Status.Current.Message, StringComparison.Ordinal);
        Assert.True(list.Status.Current.IsIdle);
    }

    [Fact]
    public async Task TheLogCopies_OfTheList_NameNoObject()
    {
        var harness = await TestHarness.ConnectedAsync(bulkUsers: 300);
        var logger = new ListLogger<PageStatusHolder>();
        var services = new ObjectListServices(harness.Sessions, harness.Localizer, harness.Dispatcher, harness.Settings, harness.Errors, harness.Changes, harness.Time, logger);
        var list = services.CreateList();

        // Read, stopped, failed - in a container with a Polish name, with a server message that names an account.
        await list.NavigateAsync(ListLocation.ForContainer(Ksiegowosc));
        var gate = new TaskCompletionSource();
        harness.Directory.Hold = _ => gate.Task;
        var load = list.RefreshAsync();
        list.CancelLoad();
        await load;
        harness.Directory.Hold = null;
        harness.Directory.FailSearch = search => search.BaseDn == Ksiegowosc
            ? new DirectoryException(DirectoryErrorKind.ServerUnavailable, "Server Down: CN=Zofia Ćwik") { LdapResultCode = 81, ServerMessage = "DSID-0C090308, CN=Zofia Ćwik" }
            : null;
        await list.RefreshAsync();
        Assert.Equal(PageStatusKind.Error, list.Status.Current.Kind);
        Assert.Contains("Księgowość", list.Status.Current.Message, StringComparison.Ordinal);

        Assert.Equal(
            [
                "Page status Working: List.Status.Reading () []",
                "Page status Working: List.Status.Reading () []",
                "Page status Working: Feedback.Stopping () []",
                "Page status Attention: List.Status.Stopped (0) []",
                "Page status Working: List.Status.Reading () []",
                "Page status Error: List.Status.ReadFailed () [LDAP 81]",
            ],
            logger.Entries.Select(entry => entry.Message));
        Assert.DoesNotContain("Księgowość", logger.AllText, StringComparison.Ordinal);
        Assert.DoesNotContain("Zofia", logger.AllText, StringComparison.Ordinal);
        Assert.DoesNotContain("DSID", logger.AllText, StringComparison.Ordinal);

        // Every key a list status logs is a resource key (a constant of L), in both languages.
        var polish = new ResxLocalizer(Polish);
        foreach (var key in new[] { L.ListStatus.Reading, L.ListStatus.Stopped, L.ListStatus.ReadFailed, L.Feedback.Stopping, L.List.LocationDeleted })
        {
            Assert.True(harness.Localizer.Contains(key), key);
            Assert.DoesNotContain("[", polish[key], StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task TheStatusBarOfTheWindow_SaysNothingAboutATabThatHasItsOwnStatusLine()
    {
        var harness = await TestHarness.ConnectedAsync();
        var shell = harness.CreateShell();
        await shell.InitializeAsync();
        var list = Assert.IsType<ObjectListViewModel>(shell.SelectedTab);
        Assert.IsAssignableFrom<IHasPageStatus>(list);
        var changes = new List<string?>();
        shell.PropertyChanged += (_, e) => changes.Add(e.PropertyName);

        // The list says it in its own line; the view model keeps the sentence for hosts without one.
        Assert.NotEqual(string.Empty, shell.StatusText);
        Assert.Equal(list.Status.Current.Message, shell.StatusText);
        Assert.Equal(string.Empty, shell.StatusBarText);
        list.SelectedItem = list.Items[0];
        Assert.Equal("1 selected", shell.SelectionText);
        Assert.Equal(string.Empty, shell.StatusBarSelectionText);
        Assert.EndsWith("· 1 selected", list.Status.Current.Message, StringComparison.Ordinal);
        Assert.Contains(nameof(ShellViewModel.StatusBarSelectionText), changes);

        // A tab without a status line of its own still speaks through the status bar.
        var other = new PlainDocument { StatusText = "Zdarzenia: 12", SelectionText = "Zaznaczono: 2" };
        shell.Documents.Add(other);
        changes.Clear();
        shell.SelectedTab = other;
        Assert.Equal("Zdarzenia: 12", shell.StatusBarText);
        Assert.Equal("Zaznaczono: 2", shell.StatusBarSelectionText);
        Assert.Contains(nameof(ShellViewModel.StatusBarText), changes);
        Assert.Contains(nameof(ShellViewModel.StatusBarSelectionText), changes);

        changes.Clear();
        other.StatusText = "Zdarzenia: 13";
        Assert.Equal("Zdarzenia: 13", shell.StatusBarText);
        Assert.Contains(nameof(ShellViewModel.StatusBarText), changes);

        // Back on the list: nothing again.
        changes.Clear();
        shell.SelectedTab = list;
        Assert.Equal(string.Empty, shell.StatusBarText);
        Assert.Equal(string.Empty, shell.StatusBarSelectionText);
        Assert.Contains(nameof(ShellViewModel.StatusBarText), changes);
    }

    /// <summary>
    /// Every status the line shows from now on: its kind and text, whether it offers an action and whether the running
    /// operation can (still) be stopped at that moment.
    /// </summary>
    private static List<(PageStatusKind Kind, string Message, bool HasAction, bool CanStop)> Record(ObjectListViewModel list)
    {
        var seen = new List<(PageStatusKind Kind, string Message, bool HasAction, bool CanStop)>();
        list.Status.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(PageStatusHolder.Current))
            {
                var status = list.Status.Current;
                seen.Add((status.Kind, status.Message, status.Action is not null, list.Status.CancelCommand.CanExecute(null)));
            }
        };
        return seen;
    }

    /// <summary>A tab that is not an object list and has no status line of its own.</summary>
    private sealed class PlainDocument : IShellDocument
    {
        private string _statusText = string.Empty;

        public event PropertyChangedEventHandler? PropertyChanged;

        public string Title => "Zdarzenia";

        public ObjectIcon Icon => ObjectIcon.Folder;

        public bool IsLoading => false;

        public string StatusText
        {
            get => _statusText;
            set
            {
                _statusText = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(StatusText)));
            }
        }

        public string SelectionText { get; init; } = string.Empty;

        public Task RefreshAsync() => Task.CompletedTask;

        public void Dispose()
        {
        }
    }
}
