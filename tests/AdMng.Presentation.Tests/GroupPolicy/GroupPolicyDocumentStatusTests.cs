using System.ComponentModel;
using System.Globalization;
using AdMng.Core.ActiveDirectory;
using AdMng.Core.Directory;
using AdMng.Core.GroupPolicy;
using AdMng.Presentation.Feedback;
using AdMng.Presentation.GroupPolicy;
using AdMng.Presentation.Icons;
using AdMng.Presentation.Tests.Dialogs;
using AdMng.Testing;

namespace AdMng.Presentation.Tests.GroupPolicy;

/// <summary>
/// The status line of the Group Policy documents (track V, stage V2b-3; docs/dev/v-interface-renewal.md "V2b-3 as built",
/// <i>Group Policy documents</i>): every document - a tab of the main window - says in its own line what it read (the
/// summary, with the selection), that it is being read (with Stop), and - as the state that lasts - why its content is
/// not there: not connected, a read that failed or was stopped, a GPO or a container that is gone. An operation a
/// document starts says in the same line that its plan is being made and why nothing came of it; the tree and the shell
/// keep the dialog and the toast. That SYSVOL is not available, and that the session is not on the PDC emulator, are
/// notes: no status. What the documents read and how they change the directory is covered, unchanged, by
/// <see cref="GroupPolicyDocumentTests"/> and <see cref="GroupPolicyOperationTests"/>.
/// </summary>
public sealed class GroupPolicyDocumentStatusTests
{
    private static readonly CultureInfo Polish = CultureInfo.GetCultureInfo("pl-PL");

    /// <summary>How long a held call waits for the test at most (a failing test must not keep a pool thread for good).</summary>
    private static readonly TimeSpan HoldLimit = TimeSpan.FromSeconds(60);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // ---- The summary ------------------------------------------------------------------------------------------------

    [Fact]
    public async Task TheLine_SaysWhatWasRead_WithTheSelection_InBothLanguages()
    {
        var h = await GroupPolicyHarness.CreateAsync();
        using var objects = await h.ObjectsAsync();
        Assert.IsAssignableFrom<IHasPageStatus>(objects);

        // At rest: what was read.
        Assert.Equal(PageStatusKind.Idle, objects.Status.Current.Kind);
        Assert.Equal("7 group policy objects, 1 with different versions in AD and SYSVOL", objects.LoadedText);
        Assert.Equal(objects.LoadedText, objects.Status.Current.Message);
        Assert.False(objects.Status.IsWorking);
        Assert.Null(objects.Status.Lasting);
        Assert.Null(objects.Status.Current.Action);
        Assert.False(objects.Status.CloseCommand.CanExecute(null));

        // The selection joins the summary; the search says how many of the GPOs are listed.
        objects.Selection = [objects.Rows[0], objects.Rows[1]];
        Assert.Equal("2 selected", objects.SelectionText);
        Assert.Equal($"{objects.LoadedText} · 2 selected", objects.Status.Current.Message);
        objects.Selection = [];
        objects.SearchText = "Zasady haseł";
        Assert.Single(objects.Rows);
        Assert.Equal(objects.LoadedText, objects.Status.Current.Message);
        Assert.Contains("1", objects.Status.Current.Message, StringComparison.Ordinal);
        Assert.Contains("7", objects.Status.Current.Message, StringComparison.Ordinal);
        objects.SearchText = string.Empty;
        objects.Selection = [objects.Rows[0], objects.Rows[1]];

        // Both follow the language (the document reads itself again in the new one).
        h.B2.Localizer.SetCulture(Polish);
        await B2Harness.WaitUntilAsync(() => !objects.IsLoading && objects.LoadedText.StartsWith("Obiekty", StringComparison.Ordinal)
            && objects.Status.Current.Kind == PageStatusKind.Idle);
        Assert.Equal("Obiekty zasad grupy: 7, z różnymi wersjami w AD i SYSVOL: 1 · Zaznaczono: 2", objects.Status.Current.Message);
    }

    [Theory]
    [InlineData("en-US", "1 WMI filters")]
    [InlineData("pl-PL", "Filtry WMI: 1")]
    public async Task EveryDocument_HasItsSummaryInItsLine(string culture, string filters)
    {
        var h = await GroupPolicyHarness.CreateAsync(CultureInfo.GetCultureInfo(culture));
        using var wmi = await h.WmiFiltersAsync();
        using var scope = await h.ScopeAsync(h.Company.ComputersOu);
        using var gpo = await h.GpoAsync(h.Sample.WorkstationPasswords);

        Assert.Equal(filters, wmi.Status.Current.Message);
        foreach (var document in new GroupPolicyDocumentBase[] { wmi, scope, gpo })
        {
            Assert.Equal(PageStatusKind.Idle, document.Status.Current.Kind);
            Assert.NotEqual(string.Empty, document.LoadedText);
            Assert.Equal(document.LoadedText, document.Status.Current.Message);
            Assert.Equal(document.StatusText, document.Status.Current.Message);
            Assert.Null(document.Status.Lasting);
            Assert.False(document.Status.IsWorking);
        }
    }

    [Fact]
    public async Task TheStatusBarOfTheMainWindow_SaysNothingAboutAGroupPolicyTab()
    {
        var h = await GroupPolicyHarness.CreateAsync();
        using var shell = h.CreateShell();
        await shell.InitializeAsync();

        // The documents of this unit: the tab has a line of its own with the summary; the view model keeps its sentence
        // for hosts without one.
        GroupPolicyDocumentRequest[] requests =
        [
            new(GroupPolicyDocumentKind.Objects),
            new(GroupPolicyDocumentKind.WmiFilters),
            new(GroupPolicyDocumentKind.Scope) { ContainerDn = h.Company.ComputersOu },
            new(GroupPolicyDocumentKind.Object) { GpoId = h.Sample.WorkstationPasswords },
        ];
        foreach (var request in requests)
        {
            var document = Assert.IsAssignableFrom<GroupPolicyDocumentBase>(shell.OpenGroupPolicyDocument(request));
            await B2Harness.WaitUntilAsync(() => !document.IsLoading && document.Status.Current.Kind == PageStatusKind.Idle && document.LoadedText.Length > 0);
            Assert.Same(document, shell.SelectedTab);
            Assert.IsAssignableFrom<IHasPageStatus>(document);
            Assert.NotEqual(string.Empty, shell.StatusText);
            Assert.Equal(shell.StatusText, document.Status.Current.Message);
            Assert.Equal(string.Empty, shell.StatusBarText);
            Assert.Equal(string.Empty, shell.StatusBarSelectionText);
        }

        // The same holds for every other Group Policy document: the holder is the base class's.
        GroupPolicyDocumentRequest[] others =
        [
            new(GroupPolicyDocumentKind.AdministrativeTemplates) { GpoId = h.Sample.WorkstationPasswords },
            new(GroupPolicyDocumentKind.SecuritySettings) { GpoId = h.Sample.WorkstationPasswords },
            new(GroupPolicyDocumentKind.Modeling) { ContainerDn = h.Company.ComputersOu },
        ];
        foreach (var request in others)
        {
            var document = Assert.IsAssignableFrom<GroupPolicyDocumentBase>(shell.OpenGroupPolicyDocument(request));
            await B2Harness.WaitUntilAsync(() => !document.IsLoading);
            Assert.Same(document, shell.SelectedTab);
            Assert.IsAssignableFrom<IHasPageStatus>(document);
            Assert.Equal(string.Empty, shell.StatusBarText);
            Assert.Equal(string.Empty, shell.StatusBarSelectionText);
        }
    }

    // ---- Reading ----------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("en-US", "Reading “Group Policy Objects”…", "Stop", "Stopping…",
        "Reading “Group Policy Objects” was stopped: the tab may not show everything. Press Refresh to read it again.", "Refresh")]
    [InlineData("pl-PL", "Wczytywanie „Obiekty zasad grupy”…", "Zatrzymaj", "Zatrzymywanie…",
        "Wczytywanie „Obiekty zasad grupy” zatrzymano: karta może nie pokazywać wszystkiego. Wybierz Odśwież, aby wczytać ją ponownie.", "Odśwież")]
    public async Task Reading_IsTheOperationOfTheDocument_AndStopReturnsControl(
        string culture, string reading, string stopLabel, string stopping, string stoppedText, string refreshLabel)
    {
        var h = await GroupPolicyHarness.CreateAsync(CultureInfo.GetCultureInfo(culture));
        using var objects = (GroupPolicyObjectsDocumentViewModel)h.Factory.Create(new GroupPolicyDocumentRequest(GroupPolicyDocumentKind.Objects));
        var seen = Record(objects.Status);
        Task loading;
        using (var held = new HeldCall(h.Session))
        {
            loading = objects.LoadAsync();
            await held.Reached.WaitAsync(HoldLimit, Ct);

            // Working: what is read - with Stop, and nothing to close.
            Assert.True(objects.IsLoading);
            Assert.True(objects.Status.IsWorking);
            var working = objects.Status.Current;
            Assert.Equal(PageStatusKind.Working, working.Kind);
            Assert.Equal(reading, working.Message);
            Assert.Equal("GroupPolicy.Status.Reading", working.Log.ToString());
            Assert.False(working.CanClose);
            var stop = Assert.IsType<PageStatusAction>(working.Action);
            Assert.Equal(stopLabel, stop.Label);
            Assert.Same(objects.Status.CancelCommand, stop.Command);

            // Pressed: "Stopping…" at once - the read that is on its way to the domain controller still has to return.
            stop.Command.Execute(stop.Parameter);
            Assert.True(objects.Status.IsCancelling);
            Assert.Equal((PageStatusKind.Working, stopping), (objects.Status.Current.Kind, objects.Status.Current.Message));
            Assert.Null(objects.Status.Current.Action);
            Assert.True(objects.IsLoading);
        }

        await loading.WaitAsync(HoldLimit, Ct);

        // Stopped: the state of the document until it is read again - what it may lack, and Refresh. It cannot be closed.
        Assert.False(objects.IsLoading);
        Assert.False(objects.Status.IsWorking);
        var stopped = objects.Status.Current;
        Assert.Equal(PageStatusKind.Attention, stopped.Kind);
        Assert.Equal(stoppedText, stopped.Message);
        Assert.Equal("GroupPolicy.Status.Stopped", stopped.Log.ToString());
        Assert.False(stopped.CanClose);
        Assert.Same(stopped, objects.Status.Lasting);
        Assert.Null(objects.Error);
        var refresh = Assert.IsType<PageStatusAction>(stopped.Action);
        Assert.Equal(refreshLabel, refresh.Label);
        Assert.Same(objects.RefreshCommand, refresh.Command);
        Assert.Equal(
            [(PageStatusKind.Working, reading), (PageStatusKind.Working, stopping), (PageStatusKind.Attention, stoppedText)],
            Snapshot(seen));

        // Refresh reads the document: the summary, and "stopped" is gone with the read.
        await objects.RefreshCommand.ExecuteAsync(null);
        Assert.Equal(7, objects.Rows.Count);
        Assert.Null(objects.Status.Lasting);
        Assert.Equal((PageStatusKind.Idle, objects.LoadedText), (objects.Status.Current.Kind, objects.Status.Current.Message));
    }

    [Theory]
    [InlineData("en-US", "Could not read “Group Policy Objects”: ", " Press Retry to try again. (ServerUnavailable)", "Retry")]
    [InlineData("pl-PL", "Nie udało się wczytać „Obiekty zasad grupy”: ", " Wybierz Ponów, aby spróbować ponownie. (ServerUnavailable)", "Ponów")]
    public async Task AReadThatFails_IsTheStateOfTheDocument_WithRetry(string culture, string lead, string tail, string retryLabel)
    {
        var h = await GroupPolicyHarness.CreateAsync(CultureInfo.GetCultureInfo(culture));
        using var objects = (GroupPolicyObjectsDocumentViewModel)h.Factory.Create(new GroupPolicyDocumentRequest(GroupPolicyDocumentKind.Objects));
        var fail = 1;
        h.Session.BeforeOperation = _ =>
        {
            if (Volatile.Read(ref fail) == 1)
            {
                throw new DirectoryException(DirectoryErrorKind.ServerUnavailable, "the domain controller does not answer (test)");
            }
        };
        var seen = Record(objects.Status);

        await objects.LoadAsync();

        // The document is empty and the line says why: what was tried, the reason, what to do, the code - with Retry. It
        // lasts: it is the only explanation of the empty list.
        Assert.False(objects.IsLoading);
        Assert.Empty(objects.Rows);
        Assert.NotNull(objects.Error);
        var failed = objects.Status.Current;
        Assert.Equal(PageStatusKind.Error, failed.Kind);
        Assert.Equal(lead + objects.Error + tail, failed.Message);
        Assert.Equal("GroupPolicy.Status.ReadFailed [ServerUnavailable]", failed.Log.ToString());
        Assert.False(failed.CanClose);
        Assert.Same(failed, objects.Status.Lasting);
        var retry = Assert.IsType<PageStatusAction>(failed.Action);
        Assert.Equal(retryLabel, retry.Label);
        Assert.Same(objects.RefreshCommand, retry.Command);
        Assert.Equal([PageStatusKind.Working, PageStatusKind.Error], Snapshot(seen).Select(s => s.Kind));
        Assert.DoesNotContain("does not answer", failed.Log.ToString(), StringComparison.Ordinal);

        // Retry, from the line, reads the document.
        Volatile.Write(ref fail, 0);
        retry.Command.Execute(retry.Parameter);
        await B2Harness.WaitUntilAsync(() => !objects.IsLoading && objects.Rows.Count == 7);
        Assert.Null(objects.Error);
        Assert.Null(objects.Status.Lasting);
        Assert.Equal((PageStatusKind.Idle, objects.LoadedText), (objects.Status.Current.Kind, objects.Status.Current.Message));
    }

    [Fact]
    public async Task ANewerLoad_ReplacesTheRunningOne_WithoutAWord()
    {
        var h = await GroupPolicyHarness.CreateAsync();
        using var objects = (GroupPolicyObjectsDocumentViewModel)h.Factory.Create(new GroupPolicyDocumentRequest(GroupPolicyDocumentKind.Objects));
        var seen = Record(objects.Status);
        Task first;
        using (var held = new HeldCall(h.Session))
        {
            first = objects.LoadAsync();
            await held.Reached.WaitAsync(HoldLimit, Ct);

            // F5 while the document is read: the newer read takes the document and the line; it is not held.
            await objects.RefreshCommand.ExecuteAsync(null);
            Assert.False(objects.IsLoading);
            Assert.Equal(7, objects.Rows.Count);
            Assert.Equal((PageStatusKind.Idle, objects.LoadedText), (objects.Status.Current.Kind, objects.Status.Current.Message));
        }

        // The read that was replaced returns: it says nothing and touches nothing.
        await first.WaitAsync(HoldLimit, Ct);
        Assert.False(objects.IsLoading);
        Assert.Null(objects.Status.Lasting);
        Assert.Equal((PageStatusKind.Idle, objects.LoadedText), (objects.Status.Current.Kind, objects.Status.Current.Message));
        Assert.DoesNotContain(Snapshot(seen), s => s.Kind is PageStatusKind.Attention or PageStatusKind.Error or PageStatusKind.Done);
    }

    // ---- The state that lasts ---------------------------------------------------------------------------------------

    [Fact]
    public async Task NotConnected_IsTheStateOfTheDocument_AndBusyEndsWithTheSession()
    {
        var h = await GroupPolicyHarness.CreateAsync();
        using var objects = await h.ObjectsAsync();
        var seen = Record(objects.Status);
        Task reading;
        using (var held = new HeldCall(h.Session))
        {
            reading = objects.RefreshCommand.ExecuteAsync(null);
            await held.Reached.WaitAsync(HoldLimit, Ct);
            Assert.True(objects.IsLoading);

            // The session ends while the document is read: the line goes from "working" straight to the state, and
            // nothing is "busy" any more - although the read has not returned yet.
            await h.B2.Sessions.DisconnectAsync();
            await B2Harness.WaitUntilAsync(() => objects.Status.Current.Kind == PageStatusKind.Error);
            Assert.False(objects.IsLoading);
            Assert.False(objects.Status.IsWorking);
            Assert.Empty(objects.Rows);
            Assert.False(objects.NewCommand.CanExecute(null));
        }

        await reading.WaitAsync(HoldLimit, Ct);
        var state = objects.Status.Current;
        Assert.Equal("Not connected to a domain.", state.Message);
        Assert.Equal(objects.Error, state.Message);
        Assert.Equal("Errors.NotConnected", state.Log.ToString());
        Assert.False(state.CanClose);
        Assert.Null(state.Action);
        Assert.Same(state, objects.Status.Lasting);
        Assert.False(objects.IsLoading);
        Assert.Equal(
            [(PageStatusKind.Working, "Reading “Group Policy Objects”…"), (PageStatusKind.Error, "Not connected to a domain.")],
            Snapshot(seen));

        // Said again in the language of the moment.
        h.B2.Localizer.SetCulture(Polish);
        await B2Harness.WaitUntilAsync(() => objects.Status.Current.Message == "Brak połączenia z domeną.");
        Assert.Equal(PageStatusKind.Error, objects.Status.Current.Kind);
        Assert.False(objects.Status.CloseCommand.CanExecute(null));
    }

    [Theory]
    [InlineData("en-US", "The container no longer exists.")]
    [InlineData("pl-PL", "Kontener już nie istnieje.")]
    public async Task AContainerThatIsGone_IsTheStateOfItsDocument(string culture, string text)
    {
        var h = await GroupPolicyHarness.CreateAsync(CultureInfo.GetCultureInfo(culture));
        using var scope = await h.ScopeAsync("OU=Nie ma takiej,OU=Firma," + h.DomainDn);

        Assert.True(scope.IsMissing);
        Assert.Equal(text, scope.Error);
        var state = scope.Status.Current;
        Assert.Equal((PageStatusKind.Error, text), (state.Kind, state.Message));
        Assert.Equal("GroupPolicy.ContainerMissing", state.Log.ToString());
        Assert.False(state.CanClose);
        Assert.Null(state.Action);
        Assert.Same(state, scope.Status.Lasting);
        Assert.False(scope.LinkExistingCommand.CanExecute(null));
    }

    [Fact]
    public async Task AGpoThatIsGone_IsTheStateOfItsDocument_AndTheFolderLeftBehindIsDeletedFromTheLine()
    {
        var h = await GroupPolicyHarness.CreateAsync();
        var id = h.Sample.WorkstationRemoteDesktop;
        await h.Session.DeleteAsync(h.GpoDn(id), subtree: true, Ct);
        Assert.True(h.Sysvol.FolderExists(id));

        using var gpo = await h.GpoAsync(id);

        // Gone from the directory, the folder still in SYSVOL: the state of the document, with the one thing to do.
        Assert.True(gpo.IsMissing);
        Assert.True(gpo.HasOrphanedFolder);
        var state = gpo.Status.Current;
        Assert.Equal(PageStatusKind.Error, state.Kind);
        Assert.Equal("The GPO no longer exists in Active Directory, but its SYSVOL folder is still there.", state.Message);
        Assert.Equal(gpo.Error, state.Message);
        Assert.Equal("GroupPolicy.MissingWithFolder", state.Log.ToString());
        Assert.False(state.CanClose);
        var delete = Assert.IsType<PageStatusAction>(state.Action);
        Assert.Equal("Delete the remaining folder", delete.Label);
        Assert.Same(gpo.DeleteOrphanedFolderCommand, delete.Command);
        Assert.True(delete.Command.CanExecute(delete.Parameter));

        // The action of the line plans, previews and executes like every change; afterwards the GPO is just gone.
        await gpo.DeleteOrphanedFolderCommand.ExecuteAsync(null);
        await B2Harness.WaitUntilAsync(() => h.Windows.Previews.Count == 1 && !gpo.IsLoading && !gpo.HasOrphanedFolder);
        Assert.False(h.Sysvol.FolderExists(id));
        var gone = gpo.Status.Current;
        Assert.Equal((PageStatusKind.Error, "The GPO no longer exists."), (gone.Kind, gone.Message));
        Assert.Equal("GroupPolicy.Missing", gone.Log.ToString());
        Assert.Null(gone.Action);
        Assert.False(gone.CanClose);
    }

    [Fact]
    public async Task ThatSysvolIsNotAvailable_AndThatTheSessionIsNotOnThePdcEmulator_AreNotes_NotStatus()
    {
        var h = await GroupPolicyHarness.CreateAsync();
        var servers = $"CN=Servers,CN=Warszawa,CN=Sites,{h.Session.Info.ConfigurationNamingContext}";
        h.Session.Seed(servers, new Dictionary<string, object?> { [Attr.ObjectClass] = new[] { "top", "serversContainer" } });
        var server = $"CN=DC02,{servers}";
        h.Session.Seed(server, new Dictionary<string, object?> { [Attr.ObjectClass] = new[] { "top", "server" }, [Attr.DnsHostName] = "dc02.contoso.test" });
        h.Session.Seed($"CN=NTDS Settings,{server}", new Dictionary<string, object?> { [Attr.ObjectClass] = new[] { "top", "nTDSDSA" } });
        h.Session.SeedAttributes(h.DomainDn, new Dictionary<string, object?> { ["fSMORoleOwner"] = $"CN=NTDS Settings,{server}" });
        h.Sysvol.Unavailable = SysvolUnavailableReason.Unreachable;

        using var objects = await h.ObjectsAsync();

        // Both are said - next to the content, for as long as it is shown - and the line keeps the summary.
        Assert.True(objects.HasSysvolWarning);
        Assert.Contains("SYSVOL unavailable", objects.SysvolWarning, StringComparison.Ordinal);
        Assert.True(objects.HasPdcNote);
        Assert.Contains("dc02.contoso.test", objects.PdcNote, StringComparison.Ordinal);
        Assert.Null(objects.Error);
        Assert.Null(objects.Status.Lasting);
        Assert.Equal((PageStatusKind.Idle, objects.LoadedText), (objects.Status.Current.Kind, objects.Status.Current.Message));
        Assert.Equal(7, objects.Rows.Count);
    }

    [Fact]
    public async Task AKindOfDocument_AddsItsOwnStateThatLasts_BehindTheOnesEveryDocumentHas()
    {
        var h = await GroupPolicyHarness.CreateAsync();
        using var document = new StaleableDocument(h.Services, h.Actions);
        await document.LoadAsync();
        Assert.Equal((PageStatusKind.Idle, "read"), (document.Status.Current.Kind, document.Status.Current.Message));

        // "The results are out of date": an attention of this kind of document, with its one action - it lasts.
        document.SetStale(true);
        var stale = document.Status.Current;
        Assert.Equal((PageStatusKind.Attention, "The results are out of date."), (stale.Kind, stale.Message));
        Assert.False(stale.CanClose);
        Assert.Same(stale, document.Status.Lasting);
        Assert.Same(document.RefreshCommand, Assert.IsType<PageStatusAction>(stale.Action).Command);

        // What every document has comes first: without a session the line says that, and the other state is behind it.
        await h.B2.Sessions.DisconnectAsync();
        await B2Harness.WaitUntilAsync(() => document.Status.Current.Kind == PageStatusKind.Error);
        Assert.Equal("Not connected to a domain.", document.Status.Current.Message);

        // The condition is over: nothing of it is left.
        document.SetStale(false);
        Assert.Equal("Not connected to a domain.", document.Status.Current.Message);
        Assert.Equal(PageStatusKind.Error, document.Status.Lasting?.Kind);
    }

    // ---- Operations the document starts -----------------------------------------------------------------------------

    [Theory]
    [InlineData("en-US", "Rename GPO “Stacje robocze – Zasady haseł”: preparing the changes…", "Reading “Group Policy Objects”…")]
    [InlineData("pl-PL", "Zmiana nazwy obiektu GPO „Stacje robocze – Zasady haseł”: przygotowywanie zmian…", "Wczytywanie „Obiekty zasad grupy”…")]
    public async Task AnOperation_SaysThatItsPlanIsBeingMade_ThenThePreviewHasTheWord_AndTheDocumentIsReadAgain(string culture, string preparing, string reading)
    {
        var h = await GroupPolicyHarness.CreateAsync(CultureInfo.GetCultureInfo(culture));
        using var objects = await h.ObjectsAsync();
        Assert.NotSame(h.Actions, objects.Actions);
        Assert.Same(objects.Status, objects.Actions.Status);
        Assert.Null(h.Actions.Status);
        objects.Selection = [objects.Rows.Single(r => r.Gpo.Id == h.Sample.WorkstationPasswords)];
        h.Windows.NewGpoName = "Stacje robocze – Hasła";
        (PageStatusKind Kind, string Message, bool Working)? duringPreview = null;
        h.Windows.BeforePreview = _ =>
        {
            duringPreview = (objects.Status.Current.Kind, objects.Status.Current.Message, objects.Status.IsWorking);
            return Task.CompletedTask;
        };
        var summary = objects.Status.Current.Message;
        var seen = Record(objects.Status);

        await objects.RenameCommand.ExecuteAsync(null);
        await B2Harness.WaitUntilAsync(() => !objects.IsLoading && objects.Rows.Any(r => r.Name == "Stacje robocze – Hasła")
            && objects.Status.Current.Kind == PageStatusKind.Idle);

        // While the preview window is open the line is the document's again: the window says its own status.
        Assert.Equal((PageStatusKind.Idle, summary, false), duringPreview);
        Assert.Single(h.Windows.Previews);
        Assert.Empty(h.B2.Dialogs.Errors);

        // Preparing with Stop, the summary while the preview is open, then the read after the change, then the summary.
        var said = Snapshot(seen);
        Assert.Equal((PageStatusKind.Working, preparing), said[0]);
        Assert.Equal(PageStatusKind.Idle, said[1].Kind);
        Assert.Contains((PageStatusKind.Working, reading), said.Skip(2));
        Assert.Equal(PageStatusKind.Idle, said[^1].Kind);
        Assert.DoesNotContain(said, s => s.Kind is PageStatusKind.Attention or PageStatusKind.Error or PageStatusKind.Done);
    }

    [Theory]
    [InlineData("en-US", "Stopping…", "Rename GPO “Stacje robocze – Zasady haseł” – nothing was changed: Preparing the changes was stopped. Press Retry to try again.", "Retry")]
    [InlineData("pl-PL", "Zatrzymywanie…",
        "Zmiana nazwy obiektu GPO „Stacje robocze – Zasady haseł” – nic nie zmieniono: Przygotowywanie zmian zatrzymano. Wybierz Ponów, aby spróbować ponownie.", "Ponów")]
    public async Task Stop_WhileThePlanIsMade_ChangesNothing_AndRetryMakesThePlanOfTheSameChange(string culture, string stopping, string stoppedText, string retryLabel)
    {
        var h = await GroupPolicyHarness.CreateAsync(CultureInfo.GetCultureInfo(culture));
        using var objects = await h.ObjectsAsync();
        var dn = h.GpoDn(h.Sample.WorkstationPasswords);
        objects.Selection = [objects.Rows.Single(r => r.Gpo.Id == h.Sample.WorkstationPasswords)];
        h.Windows.NewGpoName = "Stacje robocze – Hasła";
        Task renaming;
        using (var held = new HeldCall(h.Session))
        {
            // From a pool thread: the planner's first read waits for the test on the thread that called it.
            renaming = Task.Run(() => objects.RenameCommand.ExecuteAsync(null), Ct);
            await held.Reached.WaitAsync(HoldLimit, Ct);
            Assert.True(objects.Status.IsWorking);
            Assert.Equal(PageStatusKind.Working, objects.Status.Current.Kind);
            Assert.Equal("GroupPolicy.Status.Preparing", objects.Status.Current.Log.ToString());
            var stop = Assert.IsType<PageStatusAction>(objects.Status.Current.Action);

            // Stop: "Stopping…" at once, although the read is still on its way.
            stop.Command.Execute(stop.Parameter);
            Assert.True(objects.Status.IsCancelling);
            Assert.Equal((PageStatusKind.Working, stopping), (objects.Status.Current.Kind, objects.Status.Current.Message));
        }

        await renaming.WaitAsync(HoldLimit, Ct);

        // Stopped: nothing was previewed, nothing was changed - an attention the operator can close, with Retry.
        Assert.Empty(h.Windows.Previews);
        Assert.Equal("Stacje robocze – Zasady haseł", h.Value(dn, "displayName"));
        Assert.False(objects.Status.IsWorking);
        var stopped = objects.Status.Current;
        Assert.Equal((PageStatusKind.Attention, stoppedText), (stopped.Kind, stopped.Message));
        Assert.Equal("GroupPolicy.Status.PreparingStopped", stopped.Log.ToString());
        Assert.True(stopped.CanClose);
        Assert.Null(objects.Status.Lasting);
        var retry = Assert.IsType<PageStatusAction>(stopped.Action);
        Assert.Equal(retryLabel, retry.Label);

        // Retry makes the plan of the same rename again; this time it is previewed and executed.
        retry.Command.Execute(retry.Parameter);
        await B2Harness.WaitUntilAsync(() => h.Windows.Previews.Count == 1 && !objects.IsLoading && objects.Rows.Any(r => r.Name == "Stacje robocze – Hasła")
            && objects.Status.Current.Kind == PageStatusKind.Idle);
        Assert.Equal("Stacje robocze – Hasła", h.Value(dn, "displayName"));
        Assert.Empty(h.B2.Dialogs.Errors);
    }

    [Theory]
    [InlineData("en-US", "Rename GPO “Stacje robocze – Zasady haseł” – nothing was changed: ", " Press Retry to try again. (ServerUnavailable)")]
    [InlineData("pl-PL", "Zmiana nazwy obiektu GPO „Stacje robocze – Zasady haseł” – nic nie zmieniono: ", " Wybierz Ponów, aby spróbować ponownie. (ServerUnavailable)")]
    public async Task APlanThatCannotBeMade_IsAnErrorInTheLine_NotADialog(string culture, string lead, string tail)
    {
        var h = await GroupPolicyHarness.CreateAsync(CultureInfo.GetCultureInfo(culture));
        using var objects = await h.ObjectsAsync();
        var dn = h.GpoDn(h.Sample.WorkstationPasswords);
        objects.Selection = [objects.Rows.Single(r => r.Gpo.Id == h.Sample.WorkstationPasswords)];
        h.Windows.NewGpoName = "Stacje robocze – Hasła";
        var summary = objects.Status.Current.Message;
        var fail = 1;
        h.Session.BeforeOperation = _ =>
        {
            if (Volatile.Read(ref fail) == 1)
            {
                throw new DirectoryException(DirectoryErrorKind.ServerUnavailable, "the domain controller does not answer (test)");
            }
        };

        await objects.RenameCommand.ExecuteAsync(null);

        // The document is as it was: a result that can be closed - what was not done, why, what to do, the code.
        Assert.Empty(h.Windows.Previews);
        Assert.Empty(h.B2.Dialogs.Errors);
        Assert.Equal(7, objects.Rows.Count);
        var failed = objects.Status.Current;
        Assert.Equal(PageStatusKind.Error, failed.Kind);
        Assert.StartsWith(lead, failed.Message, StringComparison.Ordinal);
        Assert.EndsWith(tail, failed.Message, StringComparison.Ordinal);
        Assert.Equal("GroupPolicy.Status.NotChanged [ServerUnavailable]", failed.Log.ToString());
        Assert.True(failed.CanClose);
        Assert.Null(objects.Status.Lasting);
        var retry = Assert.IsType<PageStatusAction>(failed.Action);

        // Closed: the summary again. Said once more by Retry while the domain controller still does not answer.
        objects.Status.CloseCommand.Execute(null);
        Assert.Equal((PageStatusKind.Idle, summary), (objects.Status.Current.Kind, objects.Status.Current.Message));
        retry.Command.Execute(retry.Parameter);
        await B2Harness.WaitUntilAsync(() => objects.Status.Current.Kind == PageStatusKind.Error);
        Assert.Empty(h.Windows.Previews);

        // The domain controller answers again: Retry leads to the preview, and the rename is made.
        Volatile.Write(ref fail, 0);
        retry = Assert.IsType<PageStatusAction>(objects.Status.Current.Action);
        retry.Command.Execute(retry.Parameter);
        await B2Harness.WaitUntilAsync(() => h.Windows.Previews.Count == 1 && !objects.IsLoading && objects.Rows.Any(r => r.Name == "Stacje robocze – Hasła"));
        Assert.Equal("Stacje robocze – Hasła", h.Value(dn, "displayName"));
        Assert.Empty(h.B2.Dialogs.Errors);
    }

    [Fact]
    public async Task AnOperationThatNeedsSysvol_SaysInTheLineWhyNothingWasChanged_WhenSysvolIsNotThere()
    {
        var h = await GroupPolicyHarness.CreateAsync();
        using var objects = await h.ObjectsAsync();
        objects.Selection = [objects.Rows.Single(r => r.Gpo.Id == h.Sample.WorkstationPasswords)];
        h.Windows.NewGpoName = "Kopia – Zasady haseł";
        Assert.True(objects.CopyCommand.CanExecute(null));

        // SYSVOL was there when the document was read, and is not any more.
        h.Sysvol.Unavailable = SysvolUnavailableReason.Unreachable;
        await objects.CopyCommand.ExecuteAsync(null);

        Assert.Empty(h.Windows.Previews);
        Assert.Empty(h.B2.Dialogs.Errors);
        var refused = objects.Status.Current;
        Assert.Equal(PageStatusKind.Attention, refused.Kind);
        Assert.Equal(
            $"Copy GPO “Stacje robocze – Zasady haseł” – nothing was changed: SYSVOL unavailable on {h.Session.Info.Server}: the share cannot be reached – "
                + "changes that write SYSVOL are disabled. Press Retry to try again.",
            refused.Message);
        Assert.Equal("GroupPolicy.SysvolFailedShort", refused.Log.ToString());
        Assert.True(refused.CanClose);
        var retry = Assert.IsType<PageStatusAction>(refused.Action);
        Assert.Equal("Retry", retry.Label);

        // SYSVOL is back: Retry makes the plan of the same copy, with the name that was typed.
        h.Sysvol.Unavailable = null;
        retry.Command.Execute(retry.Parameter);
        await B2Harness.WaitUntilAsync(() => h.Windows.Previews.Count == 1 && !objects.IsLoading && objects.Rows.Any(r => r.Name == "Kopia – Zasady haseł"));
        Assert.Single(h.Windows.NameDialogs);
    }

    [Theory]
    [InlineData("en-US", "GPO status of “Stacje robocze – Zasady haseł”: nothing to change – the objects already have the requested values.")]
    [InlineData("pl-PL", "Stan obiektu GPO „Stacje robocze – Zasady haseł”: brak zmian – obiekty mają już żądane wartości.")]
    public async Task NothingToChange_IsSaidInTheLineOfTheDocument_AndAsAToastForTheTree(string culture, string text)
    {
        var h = await GroupPolicyHarness.CreateAsync(CultureInfo.GetCultureInfo(culture));
        using var objects = await h.ObjectsAsync();
        var gpo = objects.Rows.Single(r => r.Gpo.Id == h.Sample.WorkstationPasswords).Gpo;

        // Started by the document: its line says it - what the document just did.
        Assert.False(await objects.Actions.SetStatusAsync(gpo, gpo.Status));
        var done = objects.Status.Current;
        Assert.Equal((PageStatusKind.Done, text), (done.Kind, done.Message));
        Assert.Equal("Changes.NothingToDo", done.Log.ToString());
        Assert.True(done.CanClose);
        Assert.Empty(h.B2.Notifications.Items);
        Assert.Empty(h.Windows.Previews);

        // Started by the tree or the shell, which have no line: the toast, as before - and the line of the document
        // keeps what it said.
        Assert.False(await h.Actions.SetStatusAsync(gpo, gpo.Status));
        Assert.Equal(text, Assert.Single(h.B2.Notifications.Items).Message);
        Assert.Same(done, objects.Status.Current);
    }

    [Fact]
    public async Task TheTreeAndTheShell_KeepTheirDialog_WhenAPlanCannotBeMade()
    {
        var h = await GroupPolicyHarness.CreateAsync();
        using var objects = await h.ObjectsAsync();
        var gpo = objects.Rows.Single(r => r.Gpo.Id == h.Sample.WorkstationPasswords).Gpo;
        var summary = objects.Status.Current;
        h.Windows.NewGpoName = "Kopia – Zasady haseł";
        h.Sysvol.Unavailable = SysvolUnavailableReason.Unreachable;

        Assert.False(await h.Actions.CopyAsync(gpo));

        var (title, message) = Assert.Single(h.B2.Dialogs.Errors);
        Assert.Equal("Copy GPO “Stacje robocze – Zasady haseł”", title);
        Assert.Contains("SYSVOL unavailable", message, StringComparison.Ordinal);
        Assert.Same(summary, objects.Status.Current);
        Assert.Empty(h.Windows.Previews);
    }

    // ---- The log copy -----------------------------------------------------------------------------------------------

    [Fact]
    public async Task TheLogCopies_CarryKeysAndCodes_NeverANameOrAServerText()
    {
        var logger = new ListLogger<PageStatusHolder>();
        var h = await GroupPolicyHarness.CreateAsync(null, logger);
        using var objects = await h.ObjectsAsync();
        objects.Selection = [objects.Rows.Single(r => r.Gpo.Id == h.Sample.WorkstationPasswords)];
        h.Windows.NewGpoName = "Stacje robocze – Hasła";

        // A plan that cannot be made, a read that fails, a container that is gone.
        var fail = 1;
        h.Session.BeforeOperation = _ =>
        {
            if (Volatile.Read(ref fail) == 1)
            {
                throw new DirectoryException(DirectoryErrorKind.ServerUnavailable, "serwer dc01.contoso.test nie odpowiada");
            }
        };
        await objects.RenameCommand.ExecuteAsync(null);
        Assert.Contains("Stacje robocze – Zasady haseł", objects.Status.Current.Message, StringComparison.Ordinal);
        await objects.RefreshCommand.ExecuteAsync(null);
        Assert.Contains("Group Policy Objects", objects.Status.Current.Message, StringComparison.Ordinal);
        Volatile.Write(ref fail, 0);
        using var scope = await h.ScopeAsync("OU=Nie ma takiej,OU=Firma," + h.DomainDn);
        Assert.Equal(PageStatusKind.Error, scope.Status.Current.Kind);

        var messages = logger.Entries.Select(entry => entry.Message).ToList();
        Assert.Contains(messages, m => m.Contains("GroupPolicy.Status.Reading", StringComparison.Ordinal));
        Assert.Contains(messages, m => m.Contains("GroupPolicy.Status.Preparing", StringComparison.Ordinal));
        Assert.Contains(messages, m => m.Contains("GroupPolicy.Status.NotChanged", StringComparison.Ordinal) && m.Contains("[ServerUnavailable]", StringComparison.Ordinal));
        Assert.Contains(messages, m => m.Contains("GroupPolicy.Status.ReadFailed", StringComparison.Ordinal) && m.Contains("[ServerUnavailable]", StringComparison.Ordinal));
        Assert.Contains(messages, m => m.Contains("GroupPolicy.ContainerMissing", StringComparison.Ordinal));
        Assert.DoesNotContain("Stacje robocze", logger.AllText, StringComparison.Ordinal);
        Assert.DoesNotContain("Zasady haseł", logger.AllText, StringComparison.Ordinal);
        Assert.DoesNotContain("Group Policy Objects", logger.AllText, StringComparison.Ordinal);
        Assert.DoesNotContain("Nie ma takiej", logger.AllText, StringComparison.Ordinal);
        Assert.DoesNotContain("contoso", logger.AllText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("nie odpowiada", logger.AllText, StringComparison.Ordinal);
    }

    // ---- Helpers ----------------------------------------------------------------------------------------------------

    /// <summary>Every status the line shows from now on: its kind and its text.</summary>
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

    private static List<(PageStatusKind Kind, string Message)> Snapshot(List<(PageStatusKind Kind, string Message)> seen)
    {
        lock (seen)
        {
            return [.. seen];
        }
    }

    /// <summary>A kind of document with a state of its own that lasts: its results can be out of date.</summary>
    private sealed class StaleableDocument(GroupPolicyServices services, GroupPolicyActions actions) : GroupPolicyDocumentBase(services, actions)
    {
        private bool _stale;

        public override string Title => "Results";

        public override ObjectIcon Icon => ObjectIcon.GroupPolicy;

        protected override PageStatus? DocumentLastingState => _stale
            ? PageStatus.Attention("The results are out of date.", new PageStatusLog("Test.Stale"), new PageStatusAction("Refresh", RefreshCommand))
            : null;

        public void SetStale(bool stale)
        {
            _stale = stale;
            UpdateLasting();
        }

        protected override Task LoadCoreAsync(IDirectorySession session, ISysvolWriteConnection? sysvol, CancellationToken cancellationToken)
        {
            LoadedText = "read";
            return Task.CompletedTask;
        }
    }

    /// <summary>
    /// A slow domain controller: the next call of the fake session waits - on the thread that made it - until the test
    /// lets it go (disposing does), so "working" and "Stopping…" are states that last until the test changes them.
    /// </summary>
    private sealed class HeldCall : IDisposable
    {
        private readonly FakeDirectorySession _session;
        private readonly ManualResetEventSlim _release = new();
        private readonly TaskCompletionSource _reached = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _armed = 1;

        public HeldCall(FakeDirectorySession session)
        {
            _session = session;
            session.BeforeOperation = _ =>
            {
                if (Interlocked.Exchange(ref _armed, 0) == 1)
                {
                    _reached.TrySetResult();
                    _release.Wait(HoldLimit);
                }
            };
        }

        /// <summary>Completes when the call is being held.</summary>
        public Task Reached => _reached.Task;

        public void Dispose()
        {
            _release.Set();
            _session.BeforeOperation = null;
        }
    }
}
