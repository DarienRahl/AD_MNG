using System.ComponentModel;
using System.Globalization;
using AdMng.Core.ActiveDirectory;
using AdMng.Core.Batch;
using AdMng.Core.Directory;
using AdMng.Core.Model;
using AdMng.Presentation.Changes;
using AdMng.Presentation.Feedback;
using AdMng.Presentation.Localization;
using AdMng.Presentation.PropertySheets;
using AdMng.Testing;

namespace AdMng.Presentation.Tests.Dialogs;

/// <summary>
/// The status line of the property sheet (track V, stage V2b; docs/dev/v-interface-renewal.md 6.8 and "V2b-1 as
/// built"): one line for the window and its pages. The window says where and when the object was read, a read as an
/// operation that can be stopped, Apply and how it ended, and its states that last (could not be read, deleted, read
/// stopped, out of date, session ended); a page says its summary, its own read and that it could not be read - in the
/// line of the window while it is the selected tab.
/// </summary>
public class PropertySheetStatusTests
{
    /// <summary>Longer than any wait of a test: a read that is not cancelled fails the test instead of ending by itself.</summary>
    private static readonly TimeSpan SlowDomainController = TimeSpan.FromSeconds(30);

    private static readonly CultureInfo English = CultureInfo.GetCultureInfo("en-US");
    private static readonly CultureInfo Polish = CultureInfo.GetCultureInfo("pl-PL");

    // ---- The summary --------------------------------------------------------------------------------------------

    [Fact]
    public async Task TheSheet_SaysWhereAndWhenTheObjectWasRead_AndThePageInFrontSaysItsOwnSummary()
    {
        var (sheet, harness) = await OpenAsync(c => c.JanKowalski);
        var time = harness.Time.GetLocalNow().ToString("T", English);
        var summary = $"Read from {harness.Session.Info.Server} · as of {time}";

        // One status holder for the window; the first page (fields only) has nothing to say itself.
        Assert.IsAssignableFrom<IHasPageStatus>(sheet);
        Assert.Same(sheet.SelectedPage!.Status, sheet.Status.Front);
        Assert.Equal(PageStatusKind.Idle, sheet.Status.Current.Kind);
        Assert.Equal(summary, sheet.Status.Current.Message);
        Assert.False(sheet.Status.IsWorking);
        Assert.Null(sheet.Status.Operation);
        Assert.Null(sheet.Status.Lasting);
        Assert.Null(sheet.Status.Current.Action);
        Assert.False(sheet.Status.CloseCommand.CanExecute(null));
        Assert.Equal(string.Empty, sheet.Status.Current.Log.Key);

        // "Member Of" in front: its count is the summary of the line - said once, there.
        var memberOf = Page<MemberOfPageViewModel>(sheet);
        sheet.SelectedPage = memberOf;
        Assert.Same(memberOf.Status, sheet.Status.Front);
        Assert.Equal(memberOf.CountText, sheet.Status.Current.Message);
        Assert.StartsWith("Groups: ", sheet.Status.Current.Message, StringComparison.Ordinal);
        Assert.True(sheet.Status.Current.IsIdle);

        // A staged change is part of the count at once.
        var groups = memberOf.Items.Count;
        memberOf.SelectedItem = memberOf.Items.First(i => !i.IsPrimary);
        memberOf.RemoveCommand.Execute(null);
        Assert.Equal($"Groups: {groups - 1}", sheet.Status.Current.Message);
        memberOf.Revert();
        Assert.Equal($"Groups: {groups}", sheet.Status.Current.Message);

        // Back on a page of fields: the summary of the window again.
        sheet.SelectedPage = sheet.Pages[0];
        Assert.Equal(summary, sheet.Status.Current.Message);
    }

    [Fact]
    public async Task TheSummaries_ArePolish_AndFollowTheLanguage()
    {
        var (sheet, harness) = await OpenAsync(c => c.GroupIt, Polish);
        var time = harness.Time.GetLocalNow();

        Assert.Equal($"Odczytano z {harness.Session.Info.Server} · stan na {time.ToString("T", Polish)}", sheet.Status.Current.Message);
        sheet.SelectedPage = Page<MembersPageViewModel>(sheet);
        Assert.StartsWith("Członkowie: ", sheet.Status.Current.Message, StringComparison.Ordinal);

        // The window says its own sentence in the new language at once.
        sheet.SelectedPage = sheet.Pages[0];
        harness.Localizer.SetCulture(English);
        Assert.Equal($"Read from {harness.Session.Info.Server} · as of {time.ToString("T", English)}", sheet.Status.Current.Message);
    }

    // ---- Reading the object -------------------------------------------------------------------------------------

    [Fact]
    public async Task ARead_IsTheOperationOfTheWindow_AndEndsInTheSummary()
    {
        var (sheet, _) = await OpenAsync(c => c.JanKowalski);
        var seen = Record(sheet.Status);

        await sheet.RefreshCommand.ExecuteAsync(null);

        // Working with the name of the object from the first moment; the result of a read is the sheet itself.
        Assert.Equal((PageStatusKind.Working, "Reading “Jan Kowalski”…"), seen[0]);
        Assert.Equal(PageStatusKind.Idle, seen[^1].Kind);
        Assert.DoesNotContain(seen, status => status.Kind is PageStatusKind.Done or PageStatusKind.Attention or PageStatusKind.Error);
        Assert.StartsWith("Read from ", sheet.Status.Current.Message, StringComparison.Ordinal);
        Assert.False(sheet.Status.IsWorking);
        Assert.False(sheet.IsLoading);
    }

    [Fact]
    public async Task ARunningRead_CanBeStoppedInTheStatusLine_AndStopReturnsControl()
    {
        var (sheet, harness) = await OpenAsync(c => c.JanKowalski);
        harness.Session.Latency = SlowDomainController;
        Task load;
        List<(PageStatusKind Kind, string Message)> seen;
        try
        {
            load = sheet.LoadAsync();

            // Working, with Stop as the action of the line - and nothing to close.
            Assert.True(sheet.IsLoading);
            Assert.True(sheet.Status.IsWorking);
            Assert.False(sheet.Status.IsCancelling);
            Assert.Equal("Reading “Jan Kowalski”…", sheet.Status.Current.Message);
            Assert.Equal("PropertySheet.Status.Reading", sheet.Status.Current.Log.ToString());
            var stop = Assert.IsType<PageStatusAction>(sheet.Status.Current.Action);
            Assert.Equal("Stop", stop.Label);
            Assert.Same(sheet.Status.CancelCommand, stop.Command);
            Assert.False(sheet.Status.CloseCommand.CanExecute(null));
            Assert.False(sheet.RefreshCommand.CanExecute(null));

            // Pressed: "Stopping…" at once - whenever the read notices, here it may be inside the call.
            seen = Record(sheet.Status);
            stop.Command.Execute(stop.Parameter);
            await load.WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken);
        }
        finally
        {
            harness.Session.Latency = TimeSpan.Zero;
        }

        Assert.Equal((PageStatusKind.Working, "Stopping…"), seen[0]);
        Assert.Equal(PageStatusKind.Attention, seen[^1].Kind);
        Assert.Equal(2, seen.Count);

        // The read ended as an attention: what the window may lack, and Refresh as the way on. It is a state of the
        // window - it cannot be closed, it goes when the object was read.
        var stopped = sheet.Status.Current;
        Assert.Equal(PageStatusKind.Attention, stopped.Kind);
        Assert.Equal(
            "Reading “Jan Kowalski” was stopped: the window may not show everything. Press Refresh to read the object again.",
            stopped.Message);
        Assert.Equal("PropertySheet.Status.Stopped", stopped.Log.ToString());
        Assert.False(stopped.CanClose);
        Assert.Same(sheet.Status.Lasting, stopped);
        var refresh = Assert.IsType<PageStatusAction>(stopped.Action);
        Assert.Equal("Refresh", refresh.Label);
        Assert.Same(sheet.RefreshCommand, refresh.Command);
        Assert.False(sheet.IsLoading);
        Assert.False(sheet.Status.IsWorking);
        Assert.False(sheet.Status.IsCancelling);
        Assert.Null(sheet.LoadError);
        Assert.True(sheet.RefreshCommand.CanExecute(null));

        // Refresh reads the object: the attention is gone.
        await sheet.RefreshCommand.ExecuteAsync(null);
        Assert.Null(sheet.Status.Lasting);
        Assert.True(sheet.Status.Current.IsIdle);
        Assert.StartsWith("Read from ", sheet.Status.Current.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AFailedRead_IsAnErrorThatLasts_WithRetry_WhateverPageIsInFront()
    {
        var (sheet, harness) = await OpenAsync(c => c.JanKowalski);
        var jan = harness.Company.JanKowalski;
        harness.Session.FailNext(FakeOperationKind.Get, jan, DirectoryErrorKind.ServerUnavailable);

        await sheet.LoadAsync();

        // What we tried: why it failed. What to do next. (code)
        var reason = Assert.IsType<string>(sheet.LoadError);
        var failed = sheet.Status.Current;
        Assert.Equal(PageStatusKind.Error, failed.Kind);
        Assert.Equal(PageStatusText.Failure("Could not read “Jan Kowalski”", reason, "Press Retry to try again.", ResultCode.Ldap(52)), failed.Message);
        Assert.EndsWith(" Press Retry to try again. (LDAP 52)", failed.Message, StringComparison.Ordinal);
        Assert.Equal("PropertySheet.Status.ReadFailed [LDAP 52]", failed.Log.ToString());
        Assert.False(failed.CanClose);
        Assert.Same(sheet.Status.Lasting, failed);
        var retry = Assert.IsType<PageStatusAction>(failed.Action);
        Assert.Equal("Retry", retry.Label);
        Assert.Same(sheet.RefreshCommand, retry.Command);
        Assert.False(sheet.IsLoading);
        Assert.False(sheet.Status.IsWorking);

        // The close command does not take the explanation away, and no page covers it: what the pages show is the
        // previous read.
        sheet.Status.CloseCommand.Execute(null);
        Assert.Same(failed, sheet.Status.Current);
        sheet.SelectedPage = Page<MemberOfPageViewModel>(sheet);
        Assert.Same(failed, sheet.Status.Current);

        // Retry reads the object: the page in front says its summary.
        await sheet.RefreshCommand.ExecuteAsync(null);
        Assert.Null(sheet.LoadError);
        Assert.Null(sheet.Status.Lasting);
        Assert.StartsWith("Groups: ", sheet.Status.Current.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnObjectThatIsGone_SaysSo_AsReadOrAsDeletedWhileTheWindowWasOpen()
    {
        // Not there when the sheet is opened.
        var harness = await B2Harness.CreateAsync();
        var missing = harness.Factory.CreatePropertySheet("CN=Nie Ma,OU=IT,OU=Pracownicy,OU=Firma,DC=contoso,DC=test");
        await missing.InitializeAsync();

        Assert.True(missing.IsDeleted);
        Assert.Equal(PageStatusKind.Error, missing.Status.Current.Kind);
        Assert.Equal("Could not read “Nie Ma”: The object no longer exists. Close this window.", missing.Status.Current.Message);
        Assert.Equal("PropertySheet.Status.ReadFailed", missing.Status.Current.Log.ToString());
        Assert.False(missing.Status.Current.CanClose);

        // Deleted elsewhere in the application while the sheet is open.
        var ou = harness.Builder.AddOu("Tymczasowe", harness.Company.EmployeesOu);
        var user = harness.Builder.AddUser("Zofia", "Żółkiewska", ou);
        var sheet = harness.Factory.CreatePropertySheet(user);
        await sheet.InitializeAsync();
        Assert.True(sheet.Status.Current.IsIdle);

        harness.Changes.Publish([new DirectoryChange(DirectoryChangeKind.Deleted, ou, ObjectKind: ObjectKind.OrganizationalUnit)]);
        await B2Harness.WaitUntilAsync(() => sheet.IsDeleted);

        var deleted = sheet.Status.Current;
        Assert.Equal(PageStatusKind.Error, deleted.Kind);
        Assert.Equal("“Zofia Żółkiewska” was deleted while this window was open. Close this window.", deleted.Message);
        Assert.Equal("PropertySheet.Status.Deleted", deleted.Log.ToString());
        Assert.Null(deleted.Action);
        Assert.False(deleted.CanClose);
        Assert.Null(sheet.LoadError);
    }

    // ---- Out of date --------------------------------------------------------------------------------------------

    [Fact]
    public async Task ASheetWithPendingChanges_ThatWasChangedElsewhere_SaysSoUntilItIsRefreshed()
    {
        var (sheet, harness) = await OpenAsync(c => c.JanKowalski);
        var jan = harness.Company.JanKowalski;
        Text(sheet, L.Pages.General, Attr.Initials).Value = "K";

        await harness.Coordinator.ApplyAsync(
            new ChangeRequest([jan], [new SetAttributeOperation(Attr.Title, "Kierownik")], "test"), TestContext.Current.CancellationToken);

        Assert.True(sheet.IsOutdated);
        var outdated = sheet.Status.Current;
        Assert.Equal(PageStatusKind.Attention, outdated.Kind);
        Assert.Equal(harness.Localizer[L.PropertySheet.Outdated], outdated.Message);
        Assert.Equal("PropertySheet.Outdated", outdated.Log.ToString());
        Assert.False(outdated.CanClose);
        var refresh = Assert.IsType<PageStatusAction>(outdated.Action);
        Assert.Equal("Refresh", refresh.Label);
        Assert.Same(sheet.RefreshCommand, refresh.Command);

        // An Apply that fails covers it with its result - and closed, the sheet still says that it is out of date.
        harness.Session.FailNext(FakeOperationKind.Modify, jan, DirectoryErrorKind.AccessDenied);
        Assert.False(await sheet.ApplyChangesAsync());
        Assert.Equal(PageStatusKind.Error, sheet.Status.Current.Kind);
        Assert.True(sheet.Status.CloseCommand.CanExecute(null));
        sheet.Status.CloseCommand.Execute(null);
        Assert.Equal(PageStatusKind.Attention, sheet.Status.Current.Kind);
        Assert.Equal(outdated.Message, sheet.Status.Current.Message);

        // Refresh (the operator agrees to lose the pending change) reads the current values: the attention is gone.
        harness.Dialogs.ConfirmAnswers.Enqueue(true);
        await RunAsync(refresh);
        Assert.False(sheet.IsOutdated);
        Assert.Null(sheet.Status.Lasting);
        Assert.True(sheet.Status.Current.IsIdle);
    }

    [Fact]
    public async Task DecliningToOverwriteAValueChangedOnTheServer_LeavesTheOutOfDateState_AndNoSecondSentence()
    {
        var (sheet, harness) = await OpenAsync(c => c.JanKowalski);
        harness.Session.SetValues(harness.Company.JanKowalski, Attr.Title, "Zmienione przez kogoś");
        Text(sheet, L.Pages.Organization, Attr.Title).Value = "Mój tytuł";
        var seen = Record(sheet.Status);

        harness.Dialogs.ConfirmAnswers.Enqueue(false);
        Assert.False(await sheet.ApplyChangesAsync());

        // Saving…, then the state of the window - the same text is not said a second time as a result.
        Assert.Equal(
            [(PageStatusKind.Working, "Saving the changes to “Jan Kowalski”…"), (PageStatusKind.Attention, harness.Localizer[L.PropertySheet.Outdated])],
            seen);
        Assert.Equal(harness.Localizer[L.PropertySheet.Outdated], sheet.ApplyError);
        Assert.False(sheet.Status.Current.CanClose);
        Assert.Same(sheet.Status.Lasting, sheet.Status.Current);
        Assert.False(sheet.IsApplying);
    }

    // ---- Apply --------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Apply_IsTheOperationOfTheWindow_AndSaysWhatWasSavedAndWhereTheReportIs()
    {
        var (sheet, harness) = await OpenAsync(c => c.JanKowalski);
        Text(sheet, L.Pages.Organization, Attr.Title).Value = "Kierownik działu IT";
        var seen = Record(sheet.Status);
        PageStatus? whileSaving = null;
        sheet.Status.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(PageStatusHolder.Current) && sheet.Status.Current.IsWorking)
            {
                whileSaving = sheet.Status.Current;
            }
        };

        Assert.True(await sheet.ApplyChangesAsync());

        // One operation from the press to the result: the read that follows the write is part of it.
        Assert.Equal(
            [
                (PageStatusKind.Working, "Saving the changes to “Jan Kowalski”…"),
                (PageStatusKind.Done, "The changes to “Jan Kowalski” were saved in contoso.test. The report is in the list of background operations."),
            ],
            seen);

        // Saving cannot be stopped from the line: no action, and the cancel command has nothing to cancel.
        var saving = Assert.IsType<PageStatus>(whileSaving);
        Assert.Null(saving.Action);
        Assert.False(saving.CanClose);
        Assert.Equal("PropertySheet.Status.Saving", saving.Log.ToString());

        var done = sheet.Status.Current;
        Assert.Equal(PageStatusKind.Done, done.Kind);
        Assert.Equal("PropertySheet.Status.Saved", done.Log.ToString());
        Assert.True(done.CanClose);
        Assert.Null(sheet.ApplyError);
        Assert.False(sheet.Status.IsWorking);
        Assert.False(sheet.IsApplying);
        Assert.Single(harness.Activities.Activities);
        Assert.Equal("Kierownik działu IT", harness.Value(harness.Company.JanKowalski, Attr.Title));

        // The result stays when another page comes to the front and is not lost when a page finishes its own read…
        var editor = Page<AttributeEditorPageViewModel>(sheet);
        sheet.SelectedPage = editor;
        await B2Harness.WaitUntilAsync(() => !editor.IsLoading && editor.Rows.Count > 0);
        Assert.Same(done, sheet.Status.Current);

        // …and closed, the line says the summary of the window, read after the write.
        sheet.Status.CloseCommand.Execute(null);
        Assert.True(sheet.Status.Current.IsIdle);
        Assert.StartsWith("Read from ", sheet.Status.Current.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Apply_InPolish_NamesTheObjectAndTheDomain()
    {
        var (sheet, _) = await OpenAsync(c => c.LukaszWisniewski, Polish);
        Text(sheet, L.Pages.General, Attr.Description).Value = "Zażółć gęślą jaźń";
        var seen = Record(sheet.Status);

        Assert.True(await sheet.ApplyChangesAsync());

        Assert.Equal(
            [
                (PageStatusKind.Working, "Zapisywanie zmian obiektu „Łukasz Wiśniewski”…"),
                (PageStatusKind.Done, "Zmiany obiektu „Łukasz Wiśniewski” zapisano w domenie contoso.test. Raport jest na liście operacji w tle."),
            ],
            seen);

        // The log copy has the key only - never the name.
        Assert.Equal("PropertySheet.Status.Saved", sheet.Status.Current.Log.ToString());
        Assert.DoesNotContain("Wiśniewski", sheet.Status.Current.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnApplyThePlannerRefuses_IsAnAttention_NothingWasSaved()
    {
        var (sheet, harness) = await OpenAsync(c => c.JanKowalski);
        var sam = Text(sheet, L.Pages.Account, Attr.SamAccountName);
        sam.Value = "lukasz.wisniewski";

        Assert.False(await sheet.ApplyChangesAsync());

        // What was skipped, why, and what to do - an attention, not an error: nothing went wrong.
        var blocked = sheet.Status.Current;
        Assert.Equal(PageStatusKind.Attention, blocked.Kind);
        Assert.Equal("Nothing was saved: The changes cannot be applied - see the highlighted values.", blocked.Message);
        Assert.Equal("PropertySheet.ApplyBlocked", blocked.Log.ToString());
        Assert.True(blocked.CanClose);
        Assert.Equal(harness.Localizer[L.PropertySheet.ApplyBlocked], sheet.ApplyError);
        Assert.True(sam.IsIssueError);
        Assert.Empty(harness.Session.Writes);

        // OK with a value that is not valid: the same shape, without a plan.
        sam.Value = "jan.kowalski";
        Text(sheet, L.Pages.General, Attr.GivenName).Value = new string('x', 65);
        Assert.True(sheet.HasErrors);
        await sheet.OkCommand.ExecuteAsync(null);
        Assert.Equal(PageStatusKind.Attention, sheet.Status.Current.Kind);
        Assert.Equal("Nothing was saved: Correct the highlighted values before applying.", sheet.Status.Current.Message);
        Assert.Equal("PropertySheet.FixErrors", sheet.Status.Current.Log.ToString());
    }

    [Fact]
    public async Task AFailedWrite_IsAnError_WithTheReasonAndWhatToDo()
    {
        var (sheet, harness) = await OpenAsync(c => c.JanKowalski);
        harness.Session.FailNext(FakeOperationKind.Modify, harness.Company.JanKowalski, DirectoryErrorKind.AccessDenied);
        Text(sheet, L.Pages.Organization, Attr.Title).Value = "Dyrektor";
        var seen = Record(sheet.Status);

        Assert.False(await sheet.ApplyChangesAsync());

        var denied = harness.Errors.Translate(DirectoryErrorKind.AccessDenied);
        var failed = sheet.Status.Current;
        Assert.Equal([PageStatusKind.Working, PageStatusKind.Error], seen.Select(status => status.Kind));
        Assert.Equal(
            PageStatusText.Failure(
                "Could not save the changes to “Jan Kowalski”", denied, "Correct the values and press Apply again.", ResultCode.Kind(DirectoryErrorKind.AccessDenied)),
            failed.Message);
        Assert.EndsWith(" Correct the values and press Apply again. (AccessDenied)", failed.Message, StringComparison.Ordinal);
        Assert.Equal("PropertySheet.Status.SaveFailed [AccessDenied]", failed.Log.ToString());
        Assert.True(failed.CanClose);
        Assert.Contains(denied, sheet.ApplyError, StringComparison.Ordinal);
        Assert.True(sheet.HasChanges);
        Assert.False(sheet.Status.IsWorking);

        // The next Apply replaces the error with its own result.
        Assert.True(await sheet.ApplyChangesAsync());
        Assert.Equal(PageStatusKind.Done, sheet.Status.Current.Kind);
    }

    // ---- The pages ----------------------------------------------------------------------------------------------

    [Fact]
    public async Task ATabThatCouldNotBeReadWithTheObject_SaysSoWhenItIsSelected_WithRetry()
    {
        var (sheet, harness) = await OpenAsync(c => c.GroupIt);
        var members = Page<MembersPageViewModel>(sheet);
        Assert.True(members.Items.Count > 0);
        var count = members.CountText;
        harness.Session.FailNext(FakeOperationKind.GetAllValues, harness.Company.GroupIt, DirectoryErrorKind.ServerUnavailable);

        await sheet.LoadAsync();

        // The object was read; one page was not. The page in front has nothing to do with it: the line says nothing of it.
        Assert.Null(sheet.LoadError);
        var reason = Assert.IsType<string>(members.Error);
        Assert.Null(sheet.Status.Lasting);
        Assert.True(sheet.Status.Current.IsIdle);
        Assert.StartsWith("Read from ", sheet.Status.Current.Message, StringComparison.Ordinal);

        // Selected, the tab says why it is empty - for as long as it is.
        sheet.SelectedPage = members;
        var failed = sheet.Status.Current;
        Assert.Same(members.Status.Current, failed);
        Assert.Equal(PageStatusKind.Error, failed.Kind);
        Assert.Equal(
            PageStatusText.Failure("Could not read the “Members” tab", reason, "Press Retry to try again.", ResultCode.Ldap(52)),
            failed.Message);
        Assert.Equal("PropertySheet.Status.PageReadFailed [LDAP 52]", failed.Log.ToString());
        Assert.False(failed.CanClose);
        Assert.False(sheet.Status.CloseCommand.CanExecute(null));
        var retry = Assert.IsType<PageStatusAction>(failed.Action);
        Assert.Equal("Retry", retry.Label);
        Assert.Same(sheet.RefreshCommand, retry.Command);
        Assert.Empty(members.Items);

        // Another tab: gone. Back: there again.
        sheet.SelectedPage = sheet.Pages[0];
        Assert.True(sheet.Status.Current.IsIdle);
        sheet.SelectedPage = members;
        Assert.Same(failed, sheet.Status.Current);

        // Retry reads the object and with it the page: its summary.
        await RunAsync(retry);
        Assert.Null(members.Error);
        Assert.Null(members.Status.Lasting);
        Assert.Equal(count, sheet.Status.Current.Message);
        Assert.True(sheet.Status.Current.IsIdle);
    }

    [Fact]
    public async Task TheNestedList_IsAPartOfItsPage_ItsReadCanBeStoppedWhereItWasStarted()
    {
        var (sheet, harness) = await OpenAsync(c => c.JanKowalski);
        var page = Page<MemberOfPageViewModel>(sheet);
        sheet.SelectedPage = page;
        var direct = sheet.Status.Current.Message;
        Assert.StartsWith("Groups: ", direct, StringComparison.Ordinal);

        // The inner tab "All (nested)" is shown: its list is the part in front, and it starts to read.
        page.SelectedListIndex = MembershipPageViewModel.NestedListIndex;
        Assert.Same(page.NestedStatus, page.Status.Front);
        harness.Session.Latency = SlowDomainController;
        Task load;
        List<(PageStatusKind Kind, string Message)> seen;
        try
        {
            load = page.LoadNestedCommand.ExecuteAsync(null);

            // The line of the window says what the list reads, with Stop; the operation is the list's, not the window's.
            Assert.True(page.IsNestedLoading);
            Assert.True(page.NestedStatus.IsWorking);
            Assert.False(page.Status.IsWorking);
            Assert.False(sheet.Status.IsWorking);
            Assert.Equal(PageStatusKind.Working, sheet.Status.Current.Kind);
            Assert.Equal("Reading the “All (nested)” list…", sheet.Status.Current.Message);
            Assert.Equal("PropertySheet.Status.ListReading", sheet.Status.Current.Log.ToString());
            var stop = Assert.IsType<PageStatusAction>(sheet.Status.Current.Action);
            Assert.Same(page.NestedStatus.CancelCommand, stop.Command);
            Assert.True(page.NestedStatus.CancelCommand.CanExecute(null));

            // Cancel where it was started (the button binds this command) - "Stopping…" at once.
            seen = Record(sheet.Status);
            page.NestedStatus.CancelCommand.Execute(null);
            await load.WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken);
        }
        finally
        {
            harness.Session.Latency = TimeSpan.Zero;
        }

        Assert.Equal((PageStatusKind.Working, "Stopping…"), seen[0]);
        Assert.Equal(PageStatusKind.Attention, seen[^1].Kind);
        var stopped = sheet.Status.Current;
        Assert.Equal("Reading the “All (nested)” list was stopped before all results arrived. Press Refresh to read it again.", stopped.Message);
        Assert.Equal("PropertySheet.Status.ListStopped", stopped.Log.ToString());
        Assert.True(stopped.CanClose);
        var refresh = Assert.IsType<PageStatusAction>(stopped.Action);
        Assert.Equal("Refresh", refresh.Label);
        Assert.Same(page.LoadNestedCommand, refresh.Command);
        Assert.False(page.IsNestedLoading);
        Assert.False(page.IsNestedLoaded);
        Assert.False(page.NestedStatus.IsWorking);
        Assert.False(page.NestedStatus.IsCancelling);
        Assert.True(page.LoadNestedCommand.CanExecute(null));

        // The direct list in front: the line speaks about that list - and about the nested one again when it is shown.
        page.SelectedListIndex = 0;
        Assert.Null(page.Status.Front);
        Assert.Equal(direct, sheet.Status.Current.Message);
        page.SelectedListIndex = MembershipPageViewModel.NestedListIndex;
        Assert.Same(stopped, sheet.Status.Current);

        // Refresh, from the line, reads the list: its count is its summary.
        await RunAsync(refresh);
        Assert.True(page.IsNestedLoaded);
        Assert.Equal(["DL_Wszyscy", "GG_IT"], page.NestedItems.Select(i => i.Name));
        Assert.True(sheet.Status.Current.IsIdle);
        Assert.Equal("2 objects", sheet.Status.Current.Message);
        page.SelectedListIndex = 0;
        Assert.Equal(direct, sheet.Status.Current.Message);
    }

    [Fact]
    public async Task ANestedListThatCouldNotBeRead_SaysSoWhileItIsShown_UntilItCouldBeRead()
    {
        var (sheet, harness) = await OpenAsync(c => c.JanKowalski, Polish);
        var page = Page<MemberOfPageViewModel>(sheet);
        sheet.SelectedPage = page;
        page.SelectedListIndex = MembershipPageViewModel.NestedListIndex;
        var fail = true;
        harness.Session.BeforeOperation = operation =>
        {
            // LDAP_MATCHING_RULE_IN_CHAIN: the "All (nested)" query.
            if (fail && operation.Kind == FakeOperationKind.Search && operation.Search?.Filter.Contains("1.2.840.113556.1.4.1941", StringComparison.Ordinal) == true)
            {
                throw new DirectoryException(DirectoryErrorKind.ServerUnavailable, "81: Server Down, CN=Łukasz Żółć") { LdapResultCode = 81 };
            }
        };

        await page.LoadNestedCommand.ExecuteAsync(null);

        var reason = Assert.IsType<string>(page.NestedError);
        var failed = sheet.Status.Current;
        Assert.Equal(PageStatusKind.Error, failed.Kind);
        Assert.Equal(
            PageStatusText.Failure("Nie udało się wczytać listy „Wszystkie (zagnieżdżone)”", reason, "Wybierz Ponów, aby spróbować ponownie.", ResultCode.Ldap(81)),
            failed.Message);
        Assert.Equal("PropertySheet.Status.ListReadFailed [LDAP 81]", failed.Log.ToString());
        Assert.DoesNotContain("Żółć", failed.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("Żółć", failed.ToString(), StringComparison.Ordinal);
        Assert.False(failed.CanClose);
        Assert.Same(page.NestedStatus.Lasting, failed);
        Assert.False(page.IsNestedLoading);
        var retry = Assert.IsType<PageStatusAction>(failed.Action);
        Assert.Equal("Ponów", retry.Label);
        Assert.Same(page.LoadNestedCommand, retry.Command);

        // Not while the direct list is shown; again when the nested one is.
        page.SelectedListIndex = 0;
        Assert.StartsWith("Grupy: ", sheet.Status.Current.Message, StringComparison.Ordinal);
        page.SelectedListIndex = MembershipPageViewModel.NestedListIndex;
        Assert.Same(failed, sheet.Status.Current);

        // Retry reads the list.
        fail = false;
        await RunAsync(retry);
        Assert.Null(page.NestedError);
        Assert.Null(page.NestedStatus.Lasting);
        Assert.Equal("2 obiekty", sheet.Status.Current.Message);
    }

    [Fact]
    public async Task ATabThatReadsWhenItIsShown_SaysSoInTheLine_AndAReadThatFailedStaysItsState()
    {
        var (sheet, harness) = await OpenAsync(c => c.JanKowalski);
        var jan = harness.Company.JanKowalski;
        var editor = Page<AttributeEditorPageViewModel>(sheet);

        // Shown for the first time on a slow domain controller: the tab reads, the line says so, with Stop.
        harness.Session.Latency = SlowDomainController;
        List<(PageStatusKind Kind, string Message)> seen;
        try
        {
            sheet.SelectedPage = editor;
            Assert.True(editor.IsLoading);
            Assert.True(editor.Status.IsWorking);
            Assert.False(sheet.Status.IsWorking);
            Assert.Equal("Reading the “Attribute Editor” tab…", sheet.Status.Current.Message);
            Assert.Equal("PropertySheet.Status.PageReading", sheet.Status.Current.Log.ToString());
            var stop = Assert.IsType<PageStatusAction>(sheet.Status.Current.Action);

            seen = Record(sheet.Status);
            stop.Command.Execute(stop.Parameter);
            await B2Harness.WaitUntilAsync(() => !editor.IsLoading);
        }
        finally
        {
            harness.Session.Latency = TimeSpan.Zero;
        }

        Assert.Equal((PageStatusKind.Working, "Stopping…"), seen[0]);
        var stopped = sheet.Status.Current;
        Assert.Equal(PageStatusKind.Attention, stopped.Kind);
        Assert.Equal("Reading the “Attribute Editor” tab was stopped: it may not show everything. Press Refresh to read it again.", stopped.Message);
        Assert.Equal("PropertySheet.Status.PageStopped", stopped.Log.ToString());
        Assert.True(stopped.CanClose);
        var refresh = Assert.IsType<PageStatusAction>(stopped.Action);
        Assert.Same(editor.RefreshCommand, refresh.Command);
        Assert.False(editor.Status.IsWorking);
        Assert.Empty(editor.Rows);

        // Refresh of the tab fails: an error that is the state of the tab, with Retry.
        harness.Session.FailNext(FakeOperationKind.Get, jan, DirectoryErrorKind.AccessDenied);
        await RunAsync(refresh);
        var reason = Assert.IsType<string>(editor.Error);
        var failed = sheet.Status.Current;
        Assert.Equal(PageStatusKind.Error, failed.Kind);
        Assert.StartsWith($"Could not read the “Attribute Editor” tab: {reason}", failed.Message, StringComparison.Ordinal);
        Assert.Contains(" Press Retry to try again. (LDAP ", failed.Message, StringComparison.Ordinal);
        Assert.StartsWith("PropertySheet.Status.PageReadFailed [LDAP ", failed.Log.ToString(), StringComparison.Ordinal);
        Assert.False(failed.CanClose);
        Assert.Same(editor.RefreshCommand, Assert.IsType<PageStatusAction>(failed.Action).Command);
        Assert.False(editor.IsLoading);

        // It says so only while it is in front…
        sheet.SelectedPage = sheet.Pages[0];
        Assert.True(sheet.Status.Current.IsIdle);
        sheet.SelectedPage = editor;
        Assert.Same(failed, sheet.Status.Current);

        // …and until it could be read.
        await editor.RefreshCommand.ExecuteAsync(null);
        Assert.Null(editor.Error);
        Assert.Null(editor.Status.Lasting);
        Assert.True(editor.Rows.Count > 0);
        Assert.True(sheet.Status.Current.IsIdle);
        Assert.StartsWith("Read from ", sheet.Status.Current.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ARefreshOfTheSheet_KeepsTheLineForTheWindow_AlsoWhileATabReadsWithIt()
    {
        var (sheet, _) = await OpenAsync(c => c.JanKowalski);
        var editor = Page<AttributeEditorPageViewModel>(sheet);
        sheet.SelectedPage = editor;
        await B2Harness.WaitUntilAsync(() => !editor.IsLoading && editor.Rows.Count > 0);
        var seen = Record(sheet.Status);

        await sheet.RefreshCommand.ExecuteAsync(null);
        await B2Harness.WaitUntilAsync(() => !editor.IsLoading && !sheet.IsLoading);

        // The tab in front reads again with the object, but while the window reads the line is the window's.
        Assert.Equal((PageStatusKind.Working, "Reading “Jan Kowalski”…"), seen[0]);
        Assert.DoesNotContain(seen, status => status.Message.Contains("Attribute Editor", StringComparison.Ordinal));
        Assert.True(sheet.Status.Current.IsIdle);
    }

    [Fact]
    public async Task TheCheckOnAllDomainControllers_OfTheAccountTab_IsAnOperationOfThatPage()
    {
        var (sheet, harness) = await OpenAsync(c => c.JanKowalski);
        var account = Assert.IsType<FieldsPageViewModel>(Assert.Single(sheet.Pages, p => p.Key == L.Pages.Account));
        var state = Assert.Single(account.Fields.OfType<AccountStatusFieldViewModel>());
        sheet.SelectedPage = account;
        Assert.StartsWith("Read from ", sheet.Status.Current.Message, StringComparison.Ordinal);

        harness.Session.Latency = SlowDomainController;
        Task check;
        List<(PageStatusKind Kind, string Message)> seen;
        try
        {
            check = state.CheckDomainControllersCommand.ExecuteAsync(null);

            // The line says what is asked, with Stop; the place of the answers stays empty until they are there.
            Assert.True(state.IsCheckingDomainControllers);
            Assert.True(account.Status.IsWorking);
            Assert.False(sheet.Status.IsWorking);
            Assert.Equal(PageStatusKind.Working, sheet.Status.Current.Kind);
            Assert.Equal("Checking the account on all domain controllers…", sheet.Status.Current.Message);
            Assert.Equal("AccountStatus.Status.Checking", sheet.Status.Current.Log.ToString());
            Assert.Equal(string.Empty, state.DomainControllersText);
            var stop = Assert.IsType<PageStatusAction>(sheet.Status.Current.Action);

            seen = Record(sheet.Status);
            stop.Command.Execute(stop.Parameter);
            await check.WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken);
        }
        finally
        {
            harness.Session.Latency = TimeSpan.Zero;
        }

        Assert.Equal((PageStatusKind.Working, "Stopping…"), seen[0]);
        var stopped = sheet.Status.Current;
        Assert.Equal(PageStatusKind.Attention, stopped.Kind);
        Assert.Equal("Checking the account on all domain controllers was stopped. Press “Check on all DCs” to check again.", stopped.Message);
        Assert.Equal("AccountStatus.Status.Stopped", stopped.Log.ToString());
        Assert.True(stopped.CanClose);
        Assert.False(state.IsCheckingDomainControllers);
        Assert.Equal(string.Empty, state.DomainControllersText);
        Assert.True(state.CheckDomainControllersCommand.CanExecute(null));

        // The check again: the answers are the content of the field, the operation ends without a message.
        await state.CheckDomainControllersCommand.ExecuteAsync(null);
        Assert.NotEqual(string.Empty, state.DomainControllersText);
        Assert.False(account.Status.IsWorking);
        Assert.True(sheet.Status.Current.IsIdle);

        // A fresh read of the object supersedes a running check without a word in the line. (The first call of the
        // check is held until the object was read again; then it meets its cancelled token.)
        using var held = new SemaphoreSlim(0);
        using var release = new ManualResetEventSlim();
        var hold = 1;
        harness.Session.BeforeOperation = _ =>
        {
            if (Interlocked.Exchange(ref hold, 0) == 1)
            {
                held.Release();
                release.Wait(TestContext.Current.CancellationToken);
            }
        };
        try
        {
            check = state.CheckDomainControllersCommand.ExecuteAsync(null);
            Assert.True(await held.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
            Assert.True(account.Status.IsWorking);
            seen = Record(sheet.Status);

            await sheet.LoadAsync();
            harness.Session.Latency = SlowDomainController;
            release.Set();
            await check.WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken);
        }
        finally
        {
            release.Set();
            harness.Session.BeforeOperation = null;
            harness.Session.Latency = TimeSpan.Zero;
        }

        Assert.False(account.Status.IsWorking);
        Assert.False(state.IsCheckingDomainControllers);
        Assert.True(sheet.Status.Current.IsIdle);
        Assert.Equal(string.Empty, state.DomainControllersText);
        Assert.DoesNotContain(seen, status => status.Kind is PageStatusKind.Attention or PageStatusKind.Error or PageStatusKind.Done);
    }

    // ---- The session of the window ------------------------------------------------------------------------------

    [Fact]
    public async Task WhenTheSessionOfTheWindowEnded_TheLineSaysSoForGood()
    {
        var (sheet, harness) = await OpenAsync(c => c.JanKowalski, Polish);
        var editor = Page<AttributeEditorPageViewModel>(sheet);
        Text(sheet, L.Pages.General, Attr.Initials).Value = "K";

        await harness.Sessions.DisconnectAsync();

        var ended = Assert.IsType<string>(sheet.SessionEndedMessage);
        var status = sheet.Status.Current;
        Assert.Equal(PageStatusKind.Error, status.Kind);
        Assert.Equal(ended, status.Message);
        Assert.Equal("WindowSession.Ended", status.Log.ToString());
        Assert.Null(status.Action);
        Assert.False(status.CanClose);
        Assert.Equal(ended, sheet.LoadError);
        Assert.False(sheet.IsLoading);
        Assert.False(sheet.Status.IsWorking);

        // Whatever page comes to the front and whatever it tries: nothing here can be used any more.
        sheet.SelectedPage = editor;
        await B2Harness.WaitUntilAsync(() => !editor.IsLoading);
        Assert.Same(status, sheet.Status.Current);

        // An Apply is refused, said as its result, and closed the line says again that the session ended.
        Assert.False(await sheet.ApplyChangesAsync());
        Assert.Equal(PageStatusKind.Error, sheet.Status.Current.Kind);
        Assert.StartsWith("Nie udało się zapisać zmian obiektu „Jan Kowalski”: ", sheet.Status.Current.Message, StringComparison.Ordinal);
        sheet.Status.CloseCommand.Execute(null);
        Assert.Same(status, sheet.Status.Current);
    }

    [Fact]
    public async Task ClosingTheSheet_EndsTheReadsOfTheWindowAndOfItsPages_WithoutAWord()
    {
        var (sheet, harness) = await OpenAsync(c => c.JanKowalski);
        var memberOf = Page<MemberOfPageViewModel>(sheet);
        var editor = Page<AttributeEditorPageViewModel>(sheet);
        harness.Session.Latency = SlowDomainController;
        try
        {
            var nested = memberOf.LoadNestedCommand.ExecuteAsync(null);
            sheet.SelectedPage = editor;
            var nestedRead = Assert.IsType<OperationScope>(memberOf.NestedStatus.Operation).Token;
            var editorRead = Assert.IsType<OperationScope>(editor.Status.Operation).Token;
            Assert.False(nestedRead.IsCancellationRequested);

            // The window is closed (the host disposes the view model).
            sheet.Dispose();

            Assert.True(nestedRead.IsCancellationRequested);
            Assert.True(editorRead.IsCancellationRequested);
            await nested.WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken);
            await B2Harness.WaitUntilAsync(() => !editor.IsLoading);
        }
        finally
        {
            harness.Session.Latency = TimeSpan.Zero;
        }

        // Ended, not "stopped by the operator": nobody is told anything.
        Assert.False(memberOf.NestedStatus.IsWorking);
        Assert.False(editor.Status.IsWorking);
        Assert.True(memberOf.NestedStatus.Current.IsIdle);
        Assert.True(editor.Status.Current.IsIdle);
        Assert.Null(sheet.Status.Front);
        Assert.False(sheet.Status.IsWorking);
    }

    // ---- Helpers ------------------------------------------------------------------------------------------------

    private static async Task<(PropertySheetViewModel Sheet, B2Harness Harness)> OpenAsync(Func<SampleCompany, string> target, CultureInfo? culture = null)
    {
        var harness = await B2Harness.CreateAsync(culture);
        var sheet = harness.Factory.CreatePropertySheet(target(harness.Company));
        await sheet.InitializeAsync();
        Assert.Null(sheet.LoadError);
        return (sheet, harness);
    }

    private static T Page<T>(PropertySheetViewModel sheet)
        where T : PropertyPageViewModel => Assert.Single(sheet.Pages.OfType<T>());

    private static FieldViewModel Text(PropertySheetViewModel sheet, string key, string attribute) =>
        Assert.Single(
            Assert.IsType<FieldsPageViewModel>(Assert.Single(sheet.Pages, p => p.Key == key)).Fields.OfType<FieldViewModel>(),
            f => f.Attribute == attribute);

    /// <summary>Presses the action of the line and waits for what it started.</summary>
    private static Task RunAsync(PageStatusAction action) =>
        Assert.IsAssignableFrom<CommunityToolkit.Mvvm.Input.IAsyncRelayCommand>(action.Command).ExecuteAsync(action.Parameter);

    /// <summary>What the line shows from now on: the kind and the text of every change.</summary>
    private static List<(PageStatusKind Kind, string Message)> Record(PageStatusHolder status)
    {
        var seen = new List<(PageStatusKind Kind, string Message)>();
        PropertyChangedEventHandler handler = (_, e) =>
        {
            if (e.PropertyName == nameof(PageStatusHolder.Current))
            {
                lock (seen)
                {
                    seen.Add((status.Current.Kind, status.Current.Message));
                }
            }
        };
        status.PropertyChanged += handler;
        return seen;
    }
}
