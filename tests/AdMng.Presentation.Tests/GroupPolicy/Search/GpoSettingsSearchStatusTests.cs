using System.Globalization;
using AdMng.Core.Directory;
using AdMng.Core.GroupPolicy.Search;
using AdMng.Presentation.Feedback;
using AdMng.Presentation.GroupPolicy.Search;
using AdMng.Presentation.Tests.Dialogs;
using AdMng.Testing;

namespace AdMng.Presentation.Tests.GroupPolicy.Search;

/// <summary>
/// What the settings search says in its status line (track V, stage V2b-3; docs/dev/v-interface-renewal.md "V2b-3 as
/// built", <i>Group Policy documents</i>): the result of the query with when the GPOs were read and the selected
/// occurrence, the read of the index as the operation of the tab - with its stages and Stop, the buttons knowing which
/// read runs -, a read that was stopped or failed and "Group Policy changed in this window" as the state of the tab
/// that lasts, each with its action. What the index could not read is a note, not status. What the tab finds and how
/// it works is covered, unchanged, by <see cref="GpoSettingsSearchDocumentTests"/>.
/// </summary>
public sealed class GpoSettingsSearchStatusTests
{
    private static readonly CultureInfo Polish = CultureInfo.GetCultureInfo("pl-PL");
    private static readonly CultureInfo English = CultureInfo.GetCultureInfo("en-US");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static Task<GpoSettingsSearchDocumentViewModel> OpenAsync(GroupPolicyHarness h, string? searchText = null) =>
        GpoSettingsSearchDocumentTests.OpenAsync(h, searchText);

    // ---- The summary ------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("en-US", "Settings found: 1 (occurrences: 2)", "GPOs searched: 7 · read ")]
    [InlineData("pl-PL", "Znalezione ustawienia: 1 (wystąpienia: 2)", "Przeszukiwane obiekty GPO: 7 · odczyt ")]
    public async Task TheLine_SaysTheResultOfTheQuery_WhenTheGposWereRead_AndTheSelectedOccurrence(string culture, string result, string searched)
    {
        var h = await GroupPolicyHarness.CreateAsync(CultureInfo.GetCultureInfo(culture));
        using var document = await OpenAsync(h);
        Assert.IsAssignableFrom<IHasPageStatus>(document);

        // No query: the summary of the read, once - the line and the status bar of a host without one say the same.
        Assert.StartsWith(searched, document.IndexInfoText, StringComparison.Ordinal);
        Assert.Equal((PageStatusKind.Idle, document.IndexInfoText), (document.Status.Current.Kind, document.Status.Current.Message));
        Assert.Equal(document.StatusText, document.Status.Current.Message);

        // A query: what it found, then when the GPOs were read.
        await GpoSettingsSearchDocumentTests.SearchAsync(document, "MinimumPasswordLength");
        Assert.Equal(result, document.ResultText);
        Assert.Equal(document.ResultText, document.LoadedText);
        Assert.Equal($"{result} · {document.IndexInfoText}", document.Status.Current.Message);
        Assert.Equal(PageStatusKind.Idle, document.Status.Current.Kind);
        Assert.Equal(document.ResultText, document.StatusText);
        Assert.Null(document.Status.Lasting);
        Assert.False(document.Status.CloseCommand.CanExecute(null));

        // The selected occurrence joins it.
        document.SelectedRow = document.Rows[0];
        Assert.Equal($"{result} · {document.IndexInfoText} · {document.SelectionText}", document.Status.Current.Message);

        // A query that finds nothing says so, and when the GPOs were read.
        await GpoSettingsSearchDocumentTests.SearchAsync(document, "nie ma takiego ustawienia");
        Assert.Empty(document.Rows);
        Assert.Equal($"{document.ResultText} · {document.IndexInfoText}", document.Status.Current.Message);
    }

    [Fact]
    public async Task TheSummary_FollowsTheLanguage_WithoutReadingTheFoldersAgain()
    {
        var h = await GroupPolicyHarness.CreateAsync(English);
        using var document = await OpenAsync(h);
        await GpoSettingsSearchDocumentTests.SearchAsync(document, "MinimumPasswordLength");
        Assert.StartsWith("Settings found: 1 (occurrences: 2) · GPOs searched: 7 · read ", document.Status.Current.Message, StringComparison.Ordinal);
        h.Sysvol.ClearCalls();

        h.B2.Localizer.SetCulture(Polish);
        await B2Harness.WaitUntilAsync(() => !document.IsLoading && document.Status.Current.Message.StartsWith("Znalezione ustawienia: ", StringComparison.Ordinal));

        Assert.StartsWith("Znalezione ustawienia: 1 (wystąpienia: 2) · Przeszukiwane obiekty GPO: 7 · odczyt ", document.Status.Current.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(h.Sysvol.Calls, c => c.Operation is "List" or "Read" && c.GpoId != Guid.Empty);
    }

    // ---- Reading ----------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("en-US", "Reading “GPO settings search”…", "Reading GPOs and links…", "Reading GPO folders: 12 of 300", "Preparing the search…")]
    [InlineData("pl-PL", "Wczytywanie „Wyszukiwarka ustawień GPO”…", "Odczyt obiektów GPO i połączeń…", "Odczyt folderów GPO: 12 z 300", "Przygotowanie wyszukiwania…")]
    public async Task ReadingTheIndex_IsTheOperationOfTheTab_WithItsStages_AndTheButtonKnowsWhichReadRuns(
        string culture, string reading, string directory, string folders, string preparing)
    {
        var h = await GroupPolicyHarness.CreateAsync(CultureInfo.GetCultureInfo(culture));
        var index = new ScriptedIndexService(h.Factory.Index);
        using var document = new GpoSettingsSearchDocumentViewModel(h.Services, h.Actions, index);
        var said = new StatusRecorder(document.Status);
        var gate = index.HoldNextRead();

        // The first read: the tab reads the index - "Odśwież" is what runs, and the other read waits.
        var loading = document.LoadAsync();
        await gate.Reached.WaitAsync(HeldDirectoryCall.Limit, Ct);
        Assert.Equal(GpoIndexRefresh.IfMissing, gate.Requested);
        Assert.True(document.IsLoading);
        Assert.True(document.Status.IsWorking);
        Assert.True(document.IsRefreshing);
        Assert.False(document.IsReadingAll);
        Assert.False(document.ReadAllCommand.CanExecute(null));
        Assert.False(document.RefreshIndexCommand.CanExecute(null));
        Assert.True(document.CancelCommand.CanExecute(null));
        var working = document.Status.Current;
        Assert.Equal(PageStatusKind.Working, working.Kind);
        Assert.Equal(directory, working.Message);
        Assert.False(working.CanClose);
        Assert.Same(document.Status.CancelCommand, Assert.IsType<PageStatusAction>(working.Action).Command);

        // The stages, with how far a stage is: the ring of the line shows the part done.
        gate.Report(new GpoIndexProgress(GpoIndexStage.Folders, 12, 300));
        Assert.Equal((PageStatusKind.Working, folders), (document.Status.Current.Kind, document.Status.Current.Message));
        Assert.Equal(0.04, document.Status.Current.Progress?.Fraction ?? double.NaN, 3);
        Assert.Equal("GroupPolicy.Status.Reading (12, 300)", document.Status.Current.Log.ToString());
        Assert.Equal(folders, document.ProgressText);
        Assert.Equal(folders, document.StatusText);

        gate.Release();
        await loading.WaitAsync(HeldDirectoryCall.Limit, Ct);

        // The read is the content: the summary, and nothing is busy.
        Assert.False(document.IsLoading);
        Assert.False(document.IsRefreshing);
        Assert.False(document.IsReadingAll);
        Assert.Equal((PageStatusKind.Idle, document.IndexInfoText), (document.Status.Current.Kind, document.Status.Current.Message));
        Assert.Null(document.Status.Lasting);
        Assert.Equal(string.Empty, document.ProgressText);

        // What the line said, in order: the read, the stages the test reported, then the stages of the real build (the
        // held read went on to the real index), "Preparing the search…", and the summary - nothing else.
        var plain = said.Plain;
        Assert.Equal(
            [(PageStatusKind.Working, reading), (PageStatusKind.Working, directory), (PageStatusKind.Working, folders)],
            plain.Take(3));
        Assert.Equal([(PageStatusKind.Working, preparing), (PageStatusKind.Idle, document.IndexInfoText)], plain.TakeLast(2));
        Assert.All(plain.SkipLast(1), s => Assert.Equal(PageStatusKind.Working, s.Kind));
    }

    [Fact]
    public async Task EveryFolderIsReadAgain_WhenTheOtherButtonIsPressed_AndOnlyThatButtonRuns()
    {
        var h = await GroupPolicyHarness.CreateAsync(English);
        var index = new ScriptedIndexService(h.Factory.Index);
        using var document = new GpoSettingsSearchDocumentViewModel(h.Services, h.Actions, index);
        await document.LoadAsync();

        var gate = index.HoldNextRead();
        var loading = document.ReadAllCommand.ExecuteAsync(null);
        await gate.Reached.WaitAsync(HeldDirectoryCall.Limit, Ct);

        // "Read everything again" is what runs; "Refresh" waits.
        Assert.Equal(GpoIndexRefresh.Full, gate.Requested);
        Assert.True(document.IsReadingAll);
        Assert.False(document.IsRefreshing);
        Assert.False(document.RefreshIndexCommand.CanExecute(null));

        gate.Release();
        await loading.WaitAsync(HeldDirectoryCall.Limit, Ct);
        Assert.False(document.IsReadingAll);
        Assert.False(document.IsRefreshing);

        // The next read is an ordinary one again: the flag of the first one is gone.
        gate = index.HoldNextRead();
        loading = document.RefreshIndexCommand.ExecuteAsync(null);
        await gate.Reached.WaitAsync(HeldDirectoryCall.Limit, Ct);
        Assert.Equal(GpoIndexRefresh.Incremental, gate.Requested);
        Assert.True(document.IsRefreshing);
        Assert.False(document.IsReadingAll);
        gate.Release();
        await loading.WaitAsync(HeldDirectoryCall.Limit, Ct);
    }

    [Theory]
    [InlineData("en-US", "Stopping…", "Reading “GPO settings search” was stopped: the tab may not show everything. Press Refresh to read it again.", "Refresh",
        "Reading the settings was cancelled.")]
    [InlineData("pl-PL", "Zatrzymywanie…",
        "Wczytywanie „Wyszukiwarka ustawień GPO” zatrzymano: karta może nie pokazywać wszystkiego. Wybierz Odśwież, aby wczytać ją ponownie.", "Odśwież",
        "Odczyt ustawień anulowano.")]
    public async Task Stop_ReturnsControl_TheStoppedReadIsTheStateOfTheTab_AndRefreshReadsItAgain(
        string culture, string stopping, string stoppedText, string refreshLabel, string cancelled)
    {
        var h = await GroupPolicyHarness.CreateAsync(CultureInfo.GetCultureInfo(culture));
        var index = new ScriptedIndexService(h.Factory.Index);
        using var document = new GpoSettingsSearchDocumentViewModel(h.Services, h.Actions, index, "MinimumPasswordLength");
        await document.LoadAsync();
        var rows = document.Rows;
        var view = document.View;
        Assert.Single(document.Groups);
        var said = new StatusRecorder(document.Status);

        var gate = index.HoldNextRead();
        var loading = document.ReadAllCommand.ExecuteAsync(null);
        await gate.Reached.WaitAsync(HeldDirectoryCall.Limit, Ct);
        var stop = Assert.IsType<PageStatusAction>(document.Status.Current.Action);

        // Pressed in the line: "Stopping…" at once, and the tab is back in the hands of the operator.
        stop.Command.Execute(stop.Parameter);
        await loading.WaitAsync(HeldDirectoryCall.Limit, Ct);

        // Stopped: the results of before stay, and the state of the tab says that it may not show everything - with
        // Refresh, which cannot be closed. The status bar of a host without a line says the read was cancelled.
        Assert.False(document.IsLoading);
        Assert.False(document.IsReadingAll);
        Assert.Null(document.Error);
        Assert.Same(rows, document.Rows);
        Assert.Same(view, document.View);
        Assert.Equal(cancelled, document.StatusText);
        var stopped = document.Status.Current;
        Assert.Equal((PageStatusKind.Attention, stoppedText), (stopped.Kind, stopped.Message));
        Assert.Equal("GroupPolicy.Status.Stopped", stopped.Log.ToString());
        Assert.False(stopped.CanClose);
        Assert.Same(stopped, document.Status.Lasting);
        var refresh = Assert.IsType<PageStatusAction>(stopped.Action);
        Assert.Equal(refreshLabel, refresh.Label);
        Assert.Same(document.RefreshCommand, refresh.Command);
        var plain = said.Plain;
        Assert.Contains((PageStatusKind.Working, stopping), plain);
        Assert.Equal((PageStatusKind.Attention, stoppedText), plain[^1]);
        Assert.Equal(stopping, plain.Last(s => s.Kind == PageStatusKind.Working).Message);

        // Refresh, pressed in the line, reads every folder again - the read that was stopped is still wanted.
        h.Sysvol.ClearCalls();
        refresh.Command.Execute(refresh.Parameter);
        await B2Harness.WaitUntilAsync(() => !document.IsLoading && document.Status.Current.Kind == PageStatusKind.Idle);
        Assert.Contains(h.Sysvol.Calls, c => c.Operation == "List" && c.GpoId != Guid.Empty);
        Assert.NotSame(view, document.View);
        Assert.Null(document.Status.Lasting);
        Assert.Single(document.Groups);
        Assert.StartsWith(document.ResultText, document.Status.Current.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AStoppedRead_IsRepeatedByTheActionOfTheLine_AndByNothingElse()
    {
        var h = await GroupPolicyHarness.CreateAsync(English);
        var index = new ScriptedIndexService(h.Factory.Index);
        using var document = new GpoSettingsSearchDocumentViewModel(h.Services, h.Actions, index, "MinimumPasswordLength");
        await document.LoadAsync();
        var view = document.View;
        var gate = index.HoldNextRead();
        var loading = document.ReadAllCommand.ExecuteAsync(null);
        await gate.Reached.WaitAsync(HeldDirectoryCall.Limit, Ct);
        document.Status.CancelCommand.Execute(null);
        await loading.WaitAsync(HeldDirectoryCall.Limit, Ct);
        Assert.Equal(PageStatusKind.Attention, document.Status.Current.Kind);

        // A change elsewhere in the window reloads the tab, and a reload only marks the results: the folders are not read
        // behind the operator's back, and the stopped read is not started again by it.
        h.Sysvol.ClearCalls();
        h.Services.NotifyChanged();
        await B2Harness.WaitUntilAsync(() => document.IsStale && !document.IsLoading && document.Status.Current.Message.StartsWith("Group Policy changed", StringComparison.Ordinal));
        Assert.Same(view, document.View);
        Assert.DoesNotContain(h.Sysvol.Calls, c => c.Operation == "List" && c.GpoId != Guid.Empty);

        // The action of the line for that state is an ordinary refresh - the GPOs that changed.
        document.Status.Current.Action!.Command.Execute(null);
        await B2Harness.WaitUntilAsync(() => !document.IsStale && !document.IsLoading && document.Status.Current.Kind == PageStatusKind.Idle);
        Assert.NotSame(view, document.View);
    }

    [Theory]
    [InlineData("en-US", "Could not read “GPO settings search”: ", " Press Retry to try again. (ServerUnavailable)", "Retry")]
    [InlineData("pl-PL", "Nie udało się wczytać „Wyszukiwarka ustawień GPO”: ", " Wybierz Ponów, aby spróbować ponownie. (ServerUnavailable)", "Ponów")]
    public async Task AReadThatFails_IsTheStateOfTheTab_WithRetry_AndRetryReadsAgain(string culture, string lead, string tail, string retryLabel)
    {
        var h = await GroupPolicyHarness.CreateAsync(CultureInfo.GetCultureInfo(culture));
        using var document = await OpenAsync(h, "MinimumPasswordLength");
        var rows = document.Rows;
        var view = document.View;
        var said = new StatusRecorder(document.Status);

        // The domain controller does not answer the refresh.
        var fail = 1;
        h.Session.BeforeOperation = _ =>
        {
            if (Volatile.Read(ref fail) == 1)
            {
                throw new DirectoryException(DirectoryErrorKind.ServerUnavailable, "the domain controller does not answer (test)");
            }
        };
        await document.RefreshIndexCommand.ExecuteAsync(null);

        // The results of before stay; the line says why the read did not happen: what was tried, the reason, what to do,
        // the code - with Retry. It lasts, and it is not busy.
        Assert.False(document.IsLoading);
        Assert.False(document.IsRefreshing);
        Assert.Same(rows, document.Rows);
        Assert.Same(view, document.View);
        Assert.NotNull(document.Error);
        var failed = document.Status.Current;
        Assert.Equal((PageStatusKind.Error, lead + document.Error + tail), (failed.Kind, failed.Message));
        Assert.Equal("GroupPolicy.Status.ReadFailed [ServerUnavailable]", failed.Log.ToString());
        Assert.False(failed.CanClose);
        Assert.Same(failed, document.Status.Lasting);
        var retry = Assert.IsType<PageStatusAction>(failed.Action);
        Assert.Equal(retryLabel, retry.Label);
        Assert.Same(document.RefreshCommand, retry.Command);
        Assert.Equal(PageStatusKind.Error, said.Plain[^1].Kind);
        Assert.DoesNotContain("does not answer", failed.Log.ToString(), StringComparison.Ordinal);

        // Retry, from the line, reads the index again - it is the read that failed, not a look at the old results.
        Volatile.Write(ref fail, 0);
        retry.Command.Execute(retry.Parameter);
        await B2Harness.WaitUntilAsync(() => !document.IsLoading && document.Status.Current.Kind == PageStatusKind.Idle);
        h.Session.BeforeOperation = null;
        Assert.Null(document.Error);
        Assert.Null(document.Status.Lasting);
        Assert.NotSame(view, document.View);
        Assert.Single(document.Groups);
    }

    // ---- The state that lasts ---------------------------------------------------------------------------------------

    [Theory]
    [InlineData("en-US", "Group Policy changed in this window – the results may be out of date.", "Refresh", "pl-PL",
        "Zmieniono zasady grupy w tym oknie – wyniki mogą być nieaktualne.", "Odśwież")]
    [InlineData("pl-PL", "Zmieniono zasady grupy w tym oknie – wyniki mogą być nieaktualne.", "Odśwież", "en-US",
        "Group Policy changed in this window – the results may be out of date.", "Refresh")]
    public async Task GroupPolicyChangedInThisWindow_IsTheStateOfTheTab_WithRefresh_InTheLanguageOfTheMoment(
        string culture, string text, string label, string otherCulture, string otherText, string otherLabel)
    {
        var h = await GroupPolicyHarness.CreateAsync(CultureInfo.GetCultureInfo(culture));
        using var document = await OpenAsync(h, "MinimumPasswordLength");
        var view = document.View;
        var said = new StatusRecorder(document.Status);

        // A change elsewhere in this window: the results stay, marked - an attention of the tab, with its one action.
        h.Services.NotifyChanged();
        await B2Harness.WaitUntilAsync(() => document.IsStale && !document.IsLoading && document.Status.Current.Kind == PageStatusKind.Attention);
        var stale = document.Status.Current;
        Assert.Equal((PageStatusKind.Attention, text), (stale.Kind, stale.Message));
        Assert.Equal("GpoSearch.Stale", stale.Log.ToString());
        Assert.False(stale.CanClose);
        Assert.Same(stale, document.Status.Lasting);
        var refresh = Assert.IsType<PageStatusAction>(stale.Action);
        Assert.Equal(label, refresh.Label);
        Assert.Same(document.RefreshIndexCommand, refresh.Command);
        Assert.Same(view, document.View);
        Assert.False(document.Status.CloseCommand.CanExecute(null));

        // The language of the moment: said again.
        h.B2.Localizer.SetCulture(CultureInfo.GetCultureInfo(otherCulture));
        await B2Harness.WaitUntilAsync(() => document.Status.Current.Message == otherText);
        Assert.Equal(PageStatusKind.Attention, document.Status.Current.Kind);
        Assert.Equal(otherLabel, Assert.IsType<PageStatusAction>(document.Status.Current.Action).Label);
        Assert.True(document.IsStale);

        // Refresh, pressed in the line, reads the GPOs that changed: the summary is back, and nothing is left to say.
        document.Status.Current.Action!.Command.Execute(null);
        await B2Harness.WaitUntilAsync(() => !document.IsStale && !document.IsLoading && document.Status.Current.Kind == PageStatusKind.Idle);
        Assert.Null(document.Status.Lasting);
        Assert.NotSame(view, document.View);
        Assert.StartsWith(document.ResultText, document.Status.Current.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(said.Plain, s => s.Kind == PageStatusKind.Error);
    }

    [Fact]
    public async Task NotConnected_EmptiesTheTab_AndWhatIsMostImportantComesFirst()
    {
        var h = await GroupPolicyHarness.CreateAsync(English);
        using var document = await OpenAsync(h, "MinimumPasswordLength");
        h.Services.NotifyChanged();
        await B2Harness.WaitUntilAsync(() => document.IsStale && !document.IsLoading && document.Status.Current.Kind == PageStatusKind.Attention);

        // The session ends while the results are marked: the line says that, and the other state is behind it.
        await h.B2.Sessions.DisconnectAsync();
        await B2Harness.WaitUntilAsync(() => document.Status.Current.Kind == PageStatusKind.Error);
        Assert.Equal("Not connected to a domain.", document.Status.Current.Message);
        Assert.False(document.IsStale);
        Assert.False(document.HasIndex);
        Assert.Empty(document.Rows);
        Assert.Equal(string.Empty, document.IndexInfoText);
        Assert.False(document.IsRefreshing);
        Assert.False(document.IsReadingAll);
        Assert.False(document.RefreshIndexCommand.CanExecute(null));
        Assert.False(document.Status.CloseCommand.CanExecute(null));
    }

    // ---- Notes ------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task WhatTheIndexCouldNotRead_IsANote_NotStatus()
    {
        var h = await GroupPolicyHarness.CreateAsync(English);
        await using (var connection = await h.Sysvol.OpenForWriteAsync(h.Session, Ct))
        {
            await connection.DeleteGpoFolderAsync(h.Sample.SitePrinters, Ct);
        }

        var index = new ScriptedIndexService(h.Factory.Index);
        using var document = new GpoSettingsSearchDocumentViewModel(h.Services, h.Actions, index, "MinimumPasswordLength");
        var said = new StatusRecorder(document.Status);
        await document.LoadAsync();

        // A GPO whose folder was not read is a sentence about the results - for as long as they are shown.
        Assert.True(document.HasNotes);
        Assert.Contains(document.Notes, n => n.Text.StartsWith("Folders not read: 1", StringComparison.Ordinal));
        Assert.Equal(PageStatusKind.Idle, document.Status.Current.Kind);
        Assert.Null(document.Status.Lasting);
        Assert.StartsWith(document.ResultText, document.Status.Current.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(said.Plain, s => s.Kind is PageStatusKind.Attention or PageStatusKind.Error or PageStatusKind.Done);
    }

    // ---- The operations of the tab ----------------------------------------------------------------------------------

    [Fact]
    public async Task TheOperationsOfTheTab_SpeakThroughItsLine_AndTheTreeAndTheShellKeepTheirDialog()
    {
        var h = await GroupPolicyHarness.CreateAsync(English);
        using var document = await OpenAsync(h, "MinimumPasswordLength");

        Assert.NotSame(h.Actions, document.Actions);
        Assert.Same(document.Status, document.Actions.Status);
        Assert.Null(h.Actions.Status);
    }

    // ---- The log copy -----------------------------------------------------------------------------------------------

    [Fact]
    public async Task TheLogCopies_CarryKeysCountsAndCodes_NeverAGpoANameOrAServerText()
    {
        var logger = new ListLogger<PageStatusHolder>();
        var h = await GroupPolicyHarness.CreateAsync(Polish, logger);
        var index = new ScriptedIndexService(h.Factory.Index);
        using var document = new GpoSettingsSearchDocumentViewModel(h.Services, h.Actions, index, "MinimumPasswordLength");
        await document.LoadAsync();

        // A read with its stages, a read that fails, results that are marked stale.
        var gate = index.HoldNextRead();
        var reading = document.RefreshIndexCommand.ExecuteAsync(null);
        await gate.Reached.WaitAsync(HeldDirectoryCall.Limit, Ct);
        gate.Report(new GpoIndexProgress(GpoIndexStage.Folders, 3, 7));
        gate.Release();
        await reading.WaitAsync(HeldDirectoryCall.Limit, Ct);
        h.Session.BeforeOperation = _ => throw new DirectoryException(DirectoryErrorKind.ServerUnavailable, "serwer dc01.contoso.test nie odpowiada");
        await document.ReadAllCommand.ExecuteAsync(null);
        h.Session.BeforeOperation = null;
        Assert.Equal(PageStatusKind.Error, document.Status.Current.Kind);
        await document.RefreshCommand.ExecuteAsync(null);
        h.Services.NotifyChanged();
        await B2Harness.WaitUntilAsync(() => document.IsStale && !document.IsLoading && document.Status.Current.Kind == PageStatusKind.Attention);

        var messages = logger.Entries.Select(entry => entry.Message).ToList();
        Assert.Contains(messages, m => m.Contains("GroupPolicy.Status.Reading", StringComparison.Ordinal));
        Assert.Contains(messages, m => m.Contains("GroupPolicy.Status.ReadFailed", StringComparison.Ordinal) && m.Contains("[ServerUnavailable]", StringComparison.Ordinal));
        Assert.Contains(messages, m => m.Contains("GpoSearch.Stale", StringComparison.Ordinal));
        Assert.DoesNotContain("Stacje robocze", logger.AllText, StringComparison.Ordinal);
        Assert.DoesNotContain("Wyszukiwarka", logger.AllText, StringComparison.Ordinal);
        Assert.DoesNotContain("Odczyt folderów", logger.AllText, StringComparison.Ordinal);
        Assert.DoesNotContain("contoso", logger.AllText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("nie odpowiada", logger.AllText, StringComparison.Ordinal);
        Assert.DoesNotContain("MinimumPasswordLength", logger.AllText, StringComparison.Ordinal);
    }
}
