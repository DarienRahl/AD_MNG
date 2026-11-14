using System.Globalization;
using AdMng.Core.Directory;
using AdMng.Core.GroupPolicy.Management;
using AdMng.Core.GroupPolicy.Search;
using AdMng.Presentation.Feedback;
using AdMng.Presentation.GroupPolicy.Cleanup;
using AdMng.Presentation.GroupPolicy.Copy;
using AdMng.Presentation.GroupPolicy.Search;
using AdMng.Presentation.Tests.Dialogs;
using AdMng.Presentation.Tests.GroupPolicy.Copy;
using AdMng.Testing;

namespace AdMng.Presentation.Tests.GroupPolicy.Cleanup;

/// <summary>
/// What the cleanup advisor says in its status line (track V, stage V2b-3; docs/dev/v-interface-renewal.md "V2b-3 as
/// built", <i>Group Policy documents</i>): the number of proposals with when the GPOs were read and the selected
/// proposal, the read and the analysis as the operation of the tab - with their stages and Stop, the buttons knowing which
/// of them runs -, a stopped or failed read and "Group Policy changed after the analysis" as the state of the tab that
/// lasts, each with its action, and the refusals of its actions. What the advisor could not see is a note, not status.
/// What the tab proposes and how its actions change Group Policy is covered, unchanged, by
/// <see cref="GpoCleanupDocumentTests"/> and <see cref="GpoSettingsActionsTests"/>.
/// </summary>
public sealed class GpoCleanupStatusTests
{
    private static readonly CultureInfo Polish = CultureInfo.GetCultureInfo("pl-PL");
    private static readonly CultureInfo English = CultureInfo.GetCultureInfo("en-US");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // ---- The summary ------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("en-US", "Analysed GPOs: 16, settings read ", "Proposals shown: 3 of ")]
    [InlineData("pl-PL", "Przeanalizowano obiektów GPO: 16, ustawienia odczytane ", "Pokazano propozycji: 3 z ")]
    public async Task TheLine_SaysTheNumberOfProposals_WhenTheGposWereRead_TheFiltersAndTheSelectedProposal(string culture, string analysed, string shown)
    {
        var k = await K7Harness.CreateAsync(culture: CultureInfo.GetCultureInfo(culture));
        using var document = await k.CleanupAsync();
        Assert.IsAssignableFrom<IHasPageStatus>(document);

        // The proposals, then when the GPOs were read.
        Assert.StartsWith(analysed, document.LastRunText, StringComparison.Ordinal);
        Assert.Equal((PageStatusKind.Idle, $"{document.LoadedText} · {document.LastRunText}"), (document.Status.Current.Kind, document.Status.Current.Message));
        Assert.Equal(document.LoadedText, document.StatusText);
        Assert.Null(document.Status.Lasting);
        Assert.False(document.Status.CloseCommand.CanExecute(null));

        // The selected proposal joins it.
        document.SelectedRow = document.Rows[0];
        Assert.Equal($"{document.LoadedText} · {document.LastRunText} · {document.SelectionText}", document.Status.Current.Message);

        // A filter says how many of the proposals are shown.
        document.SelectedRow = null;
        document.SelectedGpo = document.GpoOptions.Single(o => o.Value == k.Cleanup.Disabled);
        Assert.StartsWith(shown, document.LoadedText, StringComparison.Ordinal);
        Assert.Equal($"{document.LoadedText} · {document.LastRunText}", document.Status.Current.Message);
    }

    [Fact]
    public async Task BeforeTheFirstAnalysis_TheLineSaysItOnce()
    {
        var k = await K7Harness.CreateAsync(culture: English);
        using var document = (GpoCleanupDocumentViewModel)k.H.Factory.Create(GpoCleanupEntry.Request);

        Assert.Equal("Not analysed yet.", document.LoadedText);
        Assert.Equal((PageStatusKind.Idle, "Not analysed yet."), (document.Status.Current.Kind, document.Status.Current.Message));
    }

    [Fact]
    public async Task TheSummary_FollowsTheLanguage_WithoutReadingTheFoldersAgain()
    {
        var k = await K7Harness.CreateAsync(culture: English);
        using var document = await k.CleanupAsync();
        Assert.StartsWith("Analysed GPOs: 16, settings read ", document.LastRunText, StringComparison.Ordinal);
        k.H.Sysvol.ClearCalls();

        k.H.B2.Localizer.SetCulture(Polish);
        await B2Harness.WaitUntilAsync(() => !document.IsLoading && document.Status.Current.Message.Contains("Przeanalizowano obiektów GPO: 16", StringComparison.Ordinal));

        Assert.Equal($"{document.LoadedText} · {document.LastRunText}", document.Status.Current.Message);
        Assert.DoesNotContain(k.H.Sysvol.Calls, c => c.Operation is "List" or "Read" && c.GpoId != Guid.Empty);
    }

    // ---- Reading and analysing --------------------------------------------------------------------------------------

    [Theory]
    [InlineData("en-US", "Reading “GPO cleanup”…", "Reading GPOs and links…", "Reading GPO folders: 12 of 300", "Analysing the GPOs…")]
    [InlineData("pl-PL", "Wczytywanie „Porządki w GPO”…", "Odczyt obiektów GPO i połączeń…", "Odczyt folderów GPO: 12 z 300", "Analiza obiektów GPO…")]
    public async Task TheAnalysis_IsTheOperationOfTheTab_WithItsStages_AndTheButtonKnowsWhichReadRuns(
        string culture, string reading, string directory, string folders, string analysing)
    {
        var k = await K7Harness.CreateAsync(culture: CultureInfo.GetCultureInfo(culture));
        var index = new ScriptedIndexService(k.Index);
        using var document = new GpoCleanupDocumentViewModel(k.H.Services, k.H.Actions, index);
        var said = new StatusRecorder(document.Status);
        var gate = index.HoldNextRead();

        // The first analysis: "Odśwież" is what runs, and the other read waits.
        var loading = document.LoadAsync();
        await gate.Reached.WaitAsync(HeldDirectoryCall.Limit, Ct);
        Assert.Equal(GpoIndexRefresh.IfMissing, gate.Requested);
        Assert.True(document.IsLoading);
        Assert.True(document.IsAnalyzing);
        Assert.False(document.IsReadingAll);
        Assert.False(document.AnalyzeCommand.CanExecute(null));
        Assert.False(document.ReadAllCommand.CanExecute(null));
        Assert.True(document.CancelCommand.CanExecute(null));
        var working = document.Status.Current;
        Assert.Equal((PageStatusKind.Working, directory), (working.Kind, working.Message));
        Assert.False(working.CanClose);
        Assert.Same(document.Status.CancelCommand, Assert.IsType<PageStatusAction>(working.Action).Command);

        // The stages, with how far a stage is: the ring of the line shows the part done.
        gate.Report(new GpoIndexProgress(GpoIndexStage.Folders, 12, 300));
        Assert.Equal((PageStatusKind.Working, folders), (document.Status.Current.Kind, document.Status.Current.Message));
        Assert.Equal(0.04, document.Status.Current.Progress?.Fraction ?? double.NaN, 3);
        Assert.Equal("GroupPolicy.Status.Reading (12, 300)", document.Status.Current.Log.ToString());
        Assert.Equal(folders, document.ProgressText);

        gate.Release();
        await loading.WaitAsync(HeldDirectoryCall.Limit, Ct);

        // The analysis is the content: the summary, and nothing is busy.
        Assert.False(document.IsLoading);
        Assert.False(document.IsAnalyzing);
        Assert.Equal(PageStatusKind.Idle, document.Status.Current.Kind);
        Assert.Null(document.Status.Lasting);
        Assert.Equal(string.Empty, document.ProgressText);
        var plain = said.Plain;
        Assert.Equal([(PageStatusKind.Working, reading), (PageStatusKind.Working, directory), (PageStatusKind.Working, folders)], plain.Take(3));
        Assert.Contains((PageStatusKind.Working, analysing), plain);
        Assert.Equal((PageStatusKind.Idle, $"{document.LoadedText} · {document.LastRunText}"), plain[^1]);
        Assert.All(plain.SkipLast(1), s => Assert.Equal(PageStatusKind.Working, s.Kind));
    }

    [Fact]
    public async Task EveryFolderIsReadAgain_WhenTheOtherButtonIsPressed_AndOnlyThatButtonRuns()
    {
        var k = await K7Harness.CreateAsync(culture: English);
        var index = new ScriptedIndexService(k.Index);
        using var document = new GpoCleanupDocumentViewModel(k.H.Services, k.H.Actions, index);
        await document.LoadAsync();

        var gate = index.HoldNextRead();
        var loading = document.ReadAllCommand.ExecuteAsync(null);
        await gate.Reached.WaitAsync(HeldDirectoryCall.Limit, Ct);
        Assert.Equal(GpoIndexRefresh.Full, gate.Requested);
        Assert.True(document.IsReadingAll);
        Assert.False(document.IsAnalyzing);
        Assert.False(document.AnalyzeCommand.CanExecute(null));
        gate.Release();
        await loading.WaitAsync(HeldDirectoryCall.Limit, Ct);
        Assert.False(document.IsReadingAll);
        Assert.False(document.IsAnalyzing);

        // The next read is an ordinary one again: the flag of the first one is gone.
        gate = index.HoldNextRead();
        loading = document.AnalyzeCommand.ExecuteAsync(null);
        await gate.Reached.WaitAsync(HeldDirectoryCall.Limit, Ct);
        Assert.Equal(GpoIndexRefresh.Incremental, gate.Requested);
        Assert.True(document.IsAnalyzing);
        Assert.False(document.IsReadingAll);
        gate.Release();
        await loading.WaitAsync(HeldDirectoryCall.Limit, Ct);
    }

    [Theory]
    [InlineData("en-US", "Stopping…", "Reading “GPO cleanup” was stopped: the tab may not show everything. Press Refresh to read it again.", "Refresh", "The analysis was cancelled.")]
    [InlineData("pl-PL", "Zatrzymywanie…",
        "Wczytywanie „Porządki w GPO” zatrzymano: karta może nie pokazywać wszystkiego. Wybierz Odśwież, aby wczytać ją ponownie.", "Odśwież", "Analizę anulowano.")]
    public async Task Stop_ReturnsControl_TheStoppedAnalysisIsTheStateOfTheTab_AndRefreshReadsItAgain(
        string culture, string stopping, string stoppedText, string refreshLabel, string cancelled)
    {
        var k = await K7Harness.CreateAsync(culture: CultureInfo.GetCultureInfo(culture));
        var index = new ScriptedIndexService(k.Index);
        using var document = new GpoCleanupDocumentViewModel(k.H.Services, k.H.Actions, index);
        await document.LoadAsync();
        var rows = document.Rows;
        var view = document.View;
        var said = new StatusRecorder(document.Status);

        var gate = index.HoldNextRead();
        var loading = document.ReadAllCommand.ExecuteAsync(null);
        await gate.Reached.WaitAsync(HeldDirectoryCall.Limit, Ct);
        var stop = Assert.IsType<PageStatusAction>(document.Status.Current.Action);

        // Pressed in the line: "Stopping…" at once, and the tab is back in the hands of the operator.
        stop.Command.Execute(stop.Parameter);
        await loading.WaitAsync(HeldDirectoryCall.Limit, Ct);

        // Stopped: the proposals of before stay, and the state of the tab says that it may not show everything - with
        // Refresh, which cannot be closed. The status bar of a host without a line says the analysis was cancelled.
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
        Assert.Contains((PageStatusKind.Working, stopping), said.Plain);
        Assert.Equal((PageStatusKind.Attention, stoppedText), said.Plain[^1]);

        // Refresh, pressed in the line, reads every folder again - the read that was stopped is still wanted.
        k.H.Sysvol.ClearCalls();
        refresh.Command.Execute(refresh.Parameter);
        await B2Harness.WaitUntilAsync(() => !document.IsLoading && document.Status.Current.Kind == PageStatusKind.Idle);
        Assert.Contains(k.H.Sysvol.Calls, c => c.Operation == "List" && c.GpoId != Guid.Empty);
        Assert.NotSame(view, document.View);
        Assert.Null(document.Status.Lasting);
    }

    [Fact]
    public async Task AStoppedRead_IsRepeatedByTheActionOfTheLine_AndByNothingElse()
    {
        var k = await K7Harness.CreateAsync(culture: English);
        var index = new ScriptedIndexService(k.Index);
        using var document = new GpoCleanupDocumentViewModel(k.H.Services, k.H.Actions, index);
        await document.LoadAsync();
        var view = document.View;
        var gate = index.HoldNextRead();
        var loading = document.ReadAllCommand.ExecuteAsync(null);
        await gate.Reached.WaitAsync(HeldDirectoryCall.Limit, Ct);
        document.Status.CancelCommand.Execute(null);
        await loading.WaitAsync(HeldDirectoryCall.Limit, Ct);
        Assert.Equal(PageStatusKind.Attention, document.Status.Current.Kind);

        // A change elsewhere in the window reloads the tab, and a reload only marks the proposals: the folders are not read
        // behind the operator's back, and the stopped read is not started again by it.
        k.H.Sysvol.ClearCalls();
        k.H.Services.NotifyChanged();
        await B2Harness.WaitUntilAsync(() => document.IsStale && !document.IsLoading && document.Status.Current.Message.StartsWith("Group Policy changed", StringComparison.Ordinal));
        Assert.Same(view, document.View);
        Assert.DoesNotContain(k.H.Sysvol.Calls, c => c.Operation == "List" && c.GpoId != Guid.Empty);

        // The action of the line for that state is an ordinary refresh - the GPOs that changed.
        document.Status.Current.Action!.Command.Execute(null);
        await B2Harness.WaitUntilAsync(() => !document.IsStale && !document.IsLoading && document.Status.Current.Kind == PageStatusKind.Idle);
        Assert.NotSame(view, document.View);
    }

    [Theory]
    [InlineData("en-US", "Could not read “GPO cleanup”: ", " Press Retry to try again. (ServerUnavailable)", "Retry")]
    [InlineData("pl-PL", "Nie udało się wczytać „Porządki w GPO”: ", " Wybierz Ponów, aby spróbować ponownie. (ServerUnavailable)", "Ponów")]
    public async Task AReadThatFails_IsTheStateOfTheTab_WithRetry_AndRetryReadsAgain(string culture, string lead, string tail, string retryLabel)
    {
        var k = await K7Harness.CreateAsync(culture: CultureInfo.GetCultureInfo(culture));
        using var document = await k.CleanupAsync();
        var rows = document.Rows;
        var view = document.View;
        var said = new StatusRecorder(document.Status);

        // The domain controller does not answer the refresh.
        var fail = 1;
        k.H.Session.BeforeOperation = _ =>
        {
            if (Volatile.Read(ref fail) == 1)
            {
                throw new DirectoryException(DirectoryErrorKind.ServerUnavailable, "the domain controller does not answer (test)");
            }
        };
        await document.AnalyzeCommand.ExecuteAsync(null);

        // The proposals of before stay; the line says why the GPOs were not read: what was tried, the reason, what to do,
        // the code - with Retry. It lasts, and nothing is busy.
        Assert.False(document.IsLoading);
        Assert.False(document.IsAnalyzing);
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

        // Retry, from the line, reads the GPOs and analyses them again - it is the read that failed, not a look at the old results.
        Volatile.Write(ref fail, 0);
        retry.Command.Execute(retry.Parameter);
        await B2Harness.WaitUntilAsync(() => !document.IsLoading && document.Status.Current.Kind == PageStatusKind.Idle);
        k.H.Session.BeforeOperation = null;
        Assert.Null(document.Error);
        Assert.Null(document.Status.Lasting);
        Assert.NotSame(view, document.View);
    }

    // ---- The state that lasts ---------------------------------------------------------------------------------------

    [Theory]
    [InlineData("en-US", "Group Policy changed after the analysis – the proposals may be out of date.", "Refresh", "pl-PL",
        "Zasady grupy zmieniono po analizie – propozycje mogą być nieaktualne.", "Odśwież")]
    [InlineData("pl-PL", "Zasady grupy zmieniono po analizie – propozycje mogą być nieaktualne.", "Odśwież", "en-US",
        "Group Policy changed after the analysis – the proposals may be out of date.", "Refresh")]
    public async Task GroupPolicyChangedAfterTheAnalysis_IsTheStateOfTheTab_WithRefresh_InTheLanguageOfTheMoment(
        string culture, string text, string label, string otherCulture, string otherText, string otherLabel)
    {
        var k = await K7Harness.CreateAsync(culture: CultureInfo.GetCultureInfo(culture));
        using var document = await k.CleanupAsync();
        var view = document.View;

        // A change elsewhere in this window: the proposals stay, marked - an attention of the tab, with its one action.
        k.H.Services.NotifyChanged();
        await B2Harness.WaitUntilAsync(() => document.IsStale && !document.IsLoading && document.Status.Current.Kind == PageStatusKind.Attention);
        var stale = document.Status.Current;
        Assert.Equal((PageStatusKind.Attention, text), (stale.Kind, stale.Message));
        Assert.Equal("GpoCleanup.Stale", stale.Log.ToString());
        Assert.False(stale.CanClose);
        Assert.Same(stale, document.Status.Lasting);
        var refresh = Assert.IsType<PageStatusAction>(stale.Action);
        Assert.Equal(label, refresh.Label);
        Assert.Same(document.AnalyzeCommand, refresh.Command);
        Assert.Same(view, document.View);
        Assert.False(document.Status.CloseCommand.CanExecute(null));

        // The language of the moment: said again, nothing read again.
        k.H.B2.Localizer.SetCulture(CultureInfo.GetCultureInfo(otherCulture));
        await B2Harness.WaitUntilAsync(() => document.Status.Current.Message == otherText);
        Assert.Equal(PageStatusKind.Attention, document.Status.Current.Kind);
        Assert.Equal(otherLabel, Assert.IsType<PageStatusAction>(document.Status.Current.Action).Label);
        Assert.True(document.IsStale);

        // The action of the line analyses again: the summary is back, and nothing is left to say.
        document.Status.Current.Action!.Command.Execute(null);
        await B2Harness.WaitUntilAsync(() => !document.IsStale && !document.IsLoading && document.Status.Current.Kind == PageStatusKind.Idle);
        Assert.Null(document.Status.Lasting);
        Assert.NotSame(view, document.View);
    }

    [Fact]
    public async Task NotConnected_EmptiesTheTab_AndWhatIsMostImportantComesFirst()
    {
        var k = await K7Harness.CreateAsync(culture: English);
        using var document = await k.CleanupAsync();
        k.H.Services.NotifyChanged();
        await B2Harness.WaitUntilAsync(() => document.IsStale && !document.IsLoading && document.Status.Current.Kind == PageStatusKind.Attention);

        // The session ends while the proposals are marked: the line says that, and the other state is behind it.
        await k.H.B2.Sessions.DisconnectAsync();
        await B2Harness.WaitUntilAsync(() => document.Status.Current.Kind == PageStatusKind.Error);
        Assert.Equal("Not connected to a domain.", document.Status.Current.Message);
        Assert.False(document.IsStale);
        Assert.False(document.HasResult);
        Assert.Empty(document.Rows);
        Assert.False(document.IsAnalyzing);
        Assert.False(document.IsReadingAll);
        Assert.False(document.AnalyzeCommand.CanExecute(null));
        Assert.False(document.Status.CloseCommand.CanExecute(null));
    }

    // ---- Notes ------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task WhatTheAnalysisCouldNotSee_IsANote_NotStatus()
    {
        var k = await K7Harness.CreateAsync(culture: English);
        await using (var connection = await k.H.Sysvol.OpenForWriteAsync(k.H.Session, Ct))
        {
            await connection.DeleteGpoFolderAsync(k.H.Sample.SitePrinters, Ct);
        }

        using var document = await k.CleanupAsync();
        var said = new StatusRecorder(document.Status);
        await document.AnalyzeCommand.ExecuteAsync(null);

        // GPOs whose settings were not read completely are a sentence about the proposals - for as long as they are shown.
        Assert.True(document.HasNotes);
        Assert.Contains(document.Notes, n => n.StartsWith("The settings of 1 GPOs were not read completely", StringComparison.Ordinal));
        Assert.Equal(PageStatusKind.Idle, document.Status.Current.Kind);
        Assert.Null(document.Status.Lasting);
        Assert.DoesNotContain(said.Plain, s => s.Kind is PageStatusKind.Attention or PageStatusKind.Error or PageStatusKind.Done);
    }

    // ---- The actions of the tab -------------------------------------------------------------------------------------

    [Fact]
    public async Task TheActions_SpeakThroughTheLineOfTheTab_AndTheTreeKeepsItsDialog()
    {
        var k = await K7Harness.CreateAsync(culture: English);
        using var document = await k.CleanupAsync();

        Assert.NotSame(k.H.Actions, document.Actions);
        Assert.Same(document.Status, document.Actions.Status);
        Assert.Null(k.H.Actions.Status);
    }

    [Fact]
    public async Task AGuardedDeleteThatCannotCheckTheGpo_SaysWhyInTheLine_NotInADialog_AndTheTreeKeepsItsDialog()
    {
        var k = await K7Harness.CreateAsync(culture: English);
        using var document = await k.CleanupAsync();
        var entry = (await k.IndexAsync()).Index.Find(k.Cleanup.Unlinked)!;
        var expected = GpoExpectedState.Of(entry.Gpo, entry.Links);
        var summary = document.Status.Current;

        // The GPO cannot be read to compare it with what the analysis saw: nothing was deleted - an error of the line, with the
        // code, and no dialog. The same action of the tree, which has no line, says it in a dialog as before.
        var fail = 1;
        k.H.Session.BeforeOperation = _ =>
        {
            if (Volatile.Read(ref fail) == 1)
            {
                throw new DirectoryException(DirectoryErrorKind.ServerUnavailable, "the domain controller does not answer (test)");
            }
        };
        Assert.False(await document.Actions.DeleteCheckedAsync(entry.Gpo, expected));
        var failed = document.Status.Current;
        Assert.Equal(PageStatusKind.Error, failed.Kind);
        Assert.StartsWith($"Delete GPO “{entry.Gpo.DisplayName}” – nothing was changed: ", failed.Message, StringComparison.Ordinal);
        Assert.EndsWith(" (ServerUnavailable)", failed.Message, StringComparison.Ordinal);
        Assert.Equal("GroupPolicy.Status.NotChanged [ServerUnavailable]", failed.Log.ToString());
        Assert.True(failed.CanClose);
        Assert.Empty(k.H.B2.Dialogs.Errors);
        Assert.Empty(k.H.Windows.BackupWindows);

        // Closed, the line is the summary again.
        document.Status.CloseCommand.Execute(null);
        Assert.Same(summary, document.Status.Current);

        Assert.False(await k.H.Actions.DeleteCheckedAsync(entry.Gpo, expected));
        var error = Assert.Single(k.H.B2.Dialogs.Errors);
        Assert.Equal($"Delete GPO “{entry.Gpo.DisplayName}”", error.Title);
        Volatile.Write(ref fail, 0);
        k.H.Session.BeforeOperation = null;
    }

    [Fact]
    public async Task ADeleteWithoutASession_SaysSoInTheLine()
    {
        var k = await K7Harness.CreateAsync(culture: English);
        using var document = await k.CleanupAsync();
        var entry = (await k.IndexAsync()).Index.Find(k.Cleanup.Unlinked)!;
        var expected = GpoExpectedState.Of(entry.Gpo, entry.Links);
        await k.H.B2.Sessions.DisconnectAsync();
        await B2Harness.WaitUntilAsync(() => document.Status.Current.Kind == PageStatusKind.Error);

        Assert.False(await document.Actions.DeleteCheckedAsync(entry.Gpo, expected));

        Assert.Equal(
            (PageStatusKind.Attention, $"Delete GPO “{entry.Gpo.DisplayName}” – nothing was changed: Not connected to a domain."),
            (document.Status.Current.Kind, document.Status.Current.Message));
        Assert.Equal("Errors.NotConnected", document.Status.Current.Log.ToString());
        Assert.Empty(k.H.B2.Dialogs.Errors);
    }

    // ---- The log copy -----------------------------------------------------------------------------------------------

    [Fact]
    public async Task TheLogCopies_CarryKeysCountsAndCodes_NeverAGpoANameOrAServerText()
    {
        var logger = new ListLogger<PageStatusHolder>();
        var k = await K7Harness.CreateAsync(culture: Polish, statusLog: logger);
        var index = new ScriptedIndexService(k.Index);
        using var document = new GpoCleanupDocumentViewModel(k.H.Services, k.H.Actions, index);
        await document.LoadAsync();

        // A read with its stages, a read that fails, proposals that are marked stale.
        var gate = index.HoldNextRead();
        var reading = document.AnalyzeCommand.ExecuteAsync(null);
        await gate.Reached.WaitAsync(HeldDirectoryCall.Limit, Ct);
        gate.Report(new GpoIndexProgress(GpoIndexStage.Folders, 3, 16));
        gate.Release();
        await reading.WaitAsync(HeldDirectoryCall.Limit, Ct);
        k.H.Session.BeforeOperation = _ => throw new DirectoryException(DirectoryErrorKind.ServerUnavailable, "serwer dc01.contoso.test nie odpowiada");
        await document.ReadAllCommand.ExecuteAsync(null);
        k.H.Session.BeforeOperation = null;
        Assert.Equal(PageStatusKind.Error, document.Status.Current.Kind);
        await document.RefreshCommand.ExecuteAsync(null);
        k.H.Services.NotifyChanged();
        await B2Harness.WaitUntilAsync(() => document.IsStale && !document.IsLoading && document.Status.Current.Kind == PageStatusKind.Attention);

        var messages = logger.Entries.Select(entry => entry.Message).ToList();
        Assert.Contains(messages, m => m.Contains("GroupPolicy.Status.Reading", StringComparison.Ordinal));
        Assert.Contains(messages, m => m.Contains("GroupPolicy.Status.ReadFailed", StringComparison.Ordinal) && m.Contains("[ServerUnavailable]", StringComparison.Ordinal));
        Assert.Contains(messages, m => m.Contains("GpoCleanup.Stale", StringComparison.Ordinal));
        Assert.DoesNotContain("Porządki", logger.AllText, StringComparison.Ordinal);
        Assert.DoesNotContain("Stacje robocze", logger.AllText, StringComparison.Ordinal);
        Assert.DoesNotContain("Odczyt folderów", logger.AllText, StringComparison.Ordinal);
        Assert.DoesNotContain("contoso", logger.AllText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("nie odpowiada", logger.AllText, StringComparison.Ordinal);
    }
}
