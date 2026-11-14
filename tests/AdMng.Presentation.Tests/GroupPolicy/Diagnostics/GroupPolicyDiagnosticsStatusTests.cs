using System.Globalization;
using AdMng.Core.Directory;
using AdMng.Core.GroupPolicy.Diagnostics;
using AdMng.Presentation.Feedback;
using AdMng.Presentation.GroupPolicy.Diagnostics;
using AdMng.Presentation.Tests.Dialogs;
using AdMng.Testing;

namespace AdMng.Presentation.Tests.GroupPolicy.Diagnostics;

/// <summary>
/// What the Group Policy diagnostics say in their status line (track V, stage V2b-3; docs/dev/v-interface-renewal.md
/// "V2b-3 as built", <i>Group Policy documents</i>): the number of findings with when and how deep the GPOs were checked
/// and the selected finding, a check as the operation of the tab - with its stages and Stop, the buttons knowing which
/// check runs -, a stopped or failed check and "Group Policy has changed since the last check" as the state of the tab
/// that lasts, each with its action, and the repairs of the tab. What the check could not evaluate is a note, not status.
/// What the tab finds and how its repairs change Group Policy is covered, unchanged, by
/// <see cref="GroupPolicyDiagnosticsDocumentTests"/>.
/// </summary>
public sealed class GroupPolicyDiagnosticsStatusTests
{
    private static readonly CultureInfo Polish = CultureInfo.GetCultureInfo("pl-PL");
    private static readonly CultureInfo English = CultureInfo.GetCultureInfo("en-US");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static GroupPolicyDiagnosticsDocumentViewModel Create(GroupPolicyHarness h) =>
        (GroupPolicyDiagnosticsDocumentViewModel)h.Factory.Create(GroupPolicyDiagnosticsEntry.Request);

    // ---- The summary ------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("en-US", "Last check: quick, ", "Last check: full, ", " of ")]
    [InlineData("pl-PL", "Ostatnie sprawdzenie: szybkie, ", "Ostatnie sprawdzenie: pełne, ", "Pokazano uwagi: ")]
    public async Task TheLine_SaysTheNumberOfFindings_WhenAndHowDeepTheGposWereChecked_AndTheSelectedFinding(
        string culture, string quick, string full, string shown)
    {
        var h = await GroupPolicyHarness.CreateAsync(CultureInfo.GetCultureInfo(culture));
        using var document = await GroupPolicyDiagnosticsDocumentTests.OpenAsync(h);
        Assert.IsAssignableFrom<IHasPageStatus>(document);

        // The findings, then when and how deep the GPOs were checked.
        Assert.StartsWith(quick, document.LastRunText, StringComparison.Ordinal);
        Assert.Equal((PageStatusKind.Idle, $"{document.LoadedText} · {document.LastRunText}"), (document.Status.Current.Kind, document.Status.Current.Message));
        Assert.Equal(document.LoadedText, document.StatusText);
        Assert.Null(document.Status.Lasting);
        Assert.False(document.Status.CloseCommand.CanExecute(null));

        // The full check says that it is the full one.
        await document.FullCheckCommand.ExecuteAsync(null);
        Assert.StartsWith(full, document.LastRunText, StringComparison.Ordinal);
        Assert.Equal($"{document.LoadedText} · {document.LastRunText}", document.Status.Current.Message);

        // The selected finding joins it.
        document.SelectedRow = document.Rows[0];
        Assert.Equal($"{document.LoadedText} · {document.LastRunText} · {document.SelectionText}", document.Status.Current.Message);

        // A filter says how many of the findings are shown.
        document.SelectedRow = null;
        var medium = document.Chips.Single(c => c.Severity == GpoFindingSeverity.Medium);
        medium.IsSelected = true;
        Assert.Contains(shown, document.LoadedText, StringComparison.Ordinal);
        Assert.Equal($"{document.LoadedText} · {document.LastRunText}", document.Status.Current.Message);
    }

    [Fact]
    public async Task BeforeTheFirstCheck_TheLineSaysItOnce()
    {
        var h = await GroupPolicyHarness.CreateAsync(English);
        using var document = Create(h);

        Assert.Equal("Not checked yet.", document.LoadedText);
        Assert.Equal((PageStatusKind.Idle, "Not checked yet."), (document.Status.Current.Kind, document.Status.Current.Message));
    }

    [Fact]
    public async Task TheSummary_FollowsTheLanguage_WithoutReadingAnythingAgain()
    {
        var h = await GroupPolicyHarness.CreateAsync(English);
        using var document = await GroupPolicyDiagnosticsDocumentTests.OpenAsync(h);
        Assert.StartsWith("Last check: quick, ", document.LastRunText, StringComparison.Ordinal);
        var rows = document.Rows.Count;
        h.Session.ClearOperations();

        h.B2.Localizer.SetCulture(Polish);
        await B2Harness.WaitUntilAsync(() => document.Status.Current.Message.Contains("Ostatnie sprawdzenie: szybkie, ", StringComparison.Ordinal));

        Assert.Equal($"{document.LoadedText} · {document.LastRunText}", document.Status.Current.Message);
        Assert.Equal(rows, document.Rows.Count);
        Assert.Empty(h.Session.Operations);
    }

    // ---- Checking ---------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("en-US", "Reading “Group Policy diagnostics”…", "Reading the GPOs, links and WMI filters…", "Reading SYSVOL folders: 7 of 7")]
    [InlineData("pl-PL", "Wczytywanie „Diagnostyka zasad grupy”…", "Odczyt obiektów GPO, połączeń i filtrów WMI…", "Odczyt folderów SYSVOL: 7 z 7")]
    public async Task ACheck_IsTheOperationOfTheTab_WithItsStages_AndTheButtonKnowsWhichCheckRuns(string culture, string reading, string directory, string folders)
    {
        var h = await GroupPolicyHarness.CreateAsync(CultureInfo.GetCultureInfo(culture));
        using var document = Create(h);
        var said = new StatusRecorder(document.Status);

        // The first check - the quick one - waits for the domain controller: "Szybkie sprawdzenie" is what runs.
        Task loading;
        using (var held = new HeldDirectoryCall(h.Session))
        {
            loading = document.LoadAsync();
            await held.Reached.WaitAsync(HeldDirectoryCall.Limit, Ct);
            Assert.True(document.IsLoading);
            Assert.True(document.Status.IsWorking);
            Assert.True(document.IsQuickChecking);
            Assert.False(document.IsFullChecking);
            Assert.False(document.FullCheckCommand.CanExecute(null));
            Assert.False(document.QuickCheckCommand.CanExecute(null));
            Assert.True(document.CancelCommand.CanExecute(null));
            var working = document.Status.Current;
            Assert.Equal((PageStatusKind.Working, directory), (working.Kind, working.Message));
            Assert.False(working.CanClose);
            Assert.Same(document.Status.CancelCommand, Assert.IsType<PageStatusAction>(working.Action).Command);
            Assert.Equal(directory, document.ProgressText);
        }

        await loading.WaitAsync(HeldDirectoryCall.Limit, Ct);
        Assert.False(document.IsQuickChecking);
        Assert.Equal(PageStatusKind.Idle, document.Status.Current.Kind);

        // The full check: its stages, with how far a stage is - the ring of the line shows the part done.
        said = new StatusRecorder(document.Status);
        await document.FullCheckCommand.ExecuteAsync(null);
        var plain = said.Plain;
        Assert.Equal((PageStatusKind.Working, reading), plain[0]);
        Assert.Contains((PageStatusKind.Working, directory), plain);
        Assert.Contains((PageStatusKind.Working, folders), plain);
        Assert.Equal(1.0, said.Seen.Last(s => s.Message == folders).Fraction ?? double.NaN, 3);
        Assert.Equal((PageStatusKind.Idle, $"{document.LoadedText} · {document.LastRunText}"), plain[^1]);
        Assert.All(plain.SkipLast(1), s => Assert.Equal(PageStatusKind.Working, s.Kind));
        Assert.False(document.IsFullChecking);
        Assert.Equal(string.Empty, document.ProgressText);
    }

    [Fact]
    public async Task TheFullCheck_IsWhatRuns_WhenItWasAsked_AndTheQuickOneWaits()
    {
        var h = await GroupPolicyHarness.CreateAsync(English);
        using var document = await GroupPolicyDiagnosticsDocumentTests.OpenAsync(h);

        Task full;
        using (var held = new HeldDirectoryCall(h.Session))
        {
            full = document.FullCheckCommand.ExecuteAsync(null);
            await held.Reached.WaitAsync(HeldDirectoryCall.Limit, Ct);
            Assert.True(document.IsFullChecking);
            Assert.False(document.IsQuickChecking);
            Assert.False(document.QuickCheckCommand.CanExecute(null));
        }

        await full.WaitAsync(HeldDirectoryCall.Limit, Ct);
        Assert.False(document.IsFullChecking);
        Assert.False(document.IsQuickChecking);

        // The next check is an ordinary one again: the flag of the full one is gone.
        Task quick;
        using (var held = new HeldDirectoryCall(h.Session))
        {
            quick = document.QuickCheckCommand.ExecuteAsync(null);
            await held.Reached.WaitAsync(HeldDirectoryCall.Limit, Ct);
            Assert.True(document.IsQuickChecking);
            Assert.False(document.IsFullChecking);
        }

        await quick.WaitAsync(HeldDirectoryCall.Limit, Ct);
    }

    [Theory]
    [InlineData("en-US", "Stopping…", "Reading “Group Policy diagnostics” was stopped: the tab may not show everything. Press Refresh to read it again.", "Refresh",
        "The check was cancelled.")]
    [InlineData("pl-PL", "Zatrzymywanie…",
        "Wczytywanie „Diagnostyka zasad grupy” zatrzymano: karta może nie pokazywać wszystkiego. Wybierz Odśwież, aby wczytać ją ponownie.", "Odśwież",
        "Sprawdzanie anulowano.")]
    public async Task Stop_ReturnsControl_TheStoppedCheckIsTheStateOfTheTab_AndRefreshChecksItAgain(
        string culture, string stopping, string stoppedText, string refreshLabel, string cancelled)
    {
        var h = await GroupPolicyHarness.CreateAsync(CultureInfo.GetCultureInfo(culture));
        using var document = await GroupPolicyDiagnosticsDocumentTests.OpenAsync(h);
        var rows = document.Rows;
        var said = new StatusRecorder(document.Status);

        Task checking;
        using (var held = new HeldDirectoryCall(h.Session))
        {
            checking = document.FullCheckCommand.ExecuteAsync(null);
            await held.Reached.WaitAsync(HeldDirectoryCall.Limit, Ct);
            var stop = Assert.IsType<PageStatusAction>(document.Status.Current.Action);

            // Pressed in the line: "Stopping…" at once - the call that is on its way to the domain controller still has to
            // return.
            stop.Command.Execute(stop.Parameter);
            Assert.True(document.Status.IsCancelling);
            Assert.Equal((PageStatusKind.Working, stopping), (document.Status.Current.Kind, document.Status.Current.Message));
            Assert.Null(document.Status.Current.Action);
            Assert.True(document.IsLoading);
            Assert.True(document.IsFullChecking);
        }

        await checking.WaitAsync(HeldDirectoryCall.Limit, Ct);

        // Stopped: the findings of before stay, and the state of the tab says that it may not show everything - with
        // Refresh, which cannot be closed. The status bar of a host without a line says the check was cancelled.
        Assert.False(document.IsLoading);
        Assert.False(document.IsFullChecking);
        Assert.Null(document.Error);
        Assert.Equal(GpoDiagnosticsDepth.Quick, document.LastDepth);
        Assert.Same(rows, document.Rows);
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

        // Refresh, pressed in the line, runs the check that was stopped - the full one - again.
        refresh.Command.Execute(refresh.Parameter);
        await B2Harness.WaitUntilAsync(() => !document.IsLoading && document.Status.Current.Kind == PageStatusKind.Idle);
        Assert.Equal(GpoDiagnosticsDepth.Full, document.LastDepth);
        Assert.Null(document.Status.Lasting);
    }

    [Fact]
    public async Task AStoppedCheck_IsRepeatedByTheActionOfTheLine_AndByNothingElse()
    {
        var h = await GroupPolicyHarness.CreateAsync(English);
        using var document = await GroupPolicyDiagnosticsDocumentTests.OpenAsync(h);
        using (var held = new HeldDirectoryCall(h.Session))
        {
            var checking = document.FullCheckCommand.ExecuteAsync(null);
            await held.Reached.WaitAsync(HeldDirectoryCall.Limit, Ct);
            document.Status.CancelCommand.Execute(null);
            held.Release();
            await checking.WaitAsync(HeldDirectoryCall.Limit, Ct);
        }

        Assert.Equal(PageStatusKind.Attention, document.Status.Current.Kind);

        // A change elsewhere in the window reloads the tab, and a reload only marks the findings: the long check is not
        // started behind the operator's back, and the stopped one is not started again by it.
        h.Services.NotifyChanged();
        await B2Harness.WaitUntilAsync(() => document.IsStale && !document.IsLoading && document.Status.Current.Message.StartsWith("Group Policy has changed", StringComparison.Ordinal));
        Assert.Equal(GpoDiagnosticsDepth.Quick, document.LastDepth);

        // The action of the line for that state checks again at the depth of the findings.
        document.Status.Current.Action!.Command.Execute(null);
        await B2Harness.WaitUntilAsync(() => !document.IsStale && !document.IsLoading && document.Status.Current.Kind == PageStatusKind.Idle);
        Assert.Equal(GpoDiagnosticsDepth.Quick, document.LastDepth);
    }

    [Theory]
    [InlineData("en-US", "Could not read “Group Policy diagnostics”: ", " Press Retry to try again. (ServerUnavailable)", "Retry")]
    [InlineData("pl-PL", "Nie udało się wczytać „Diagnostyka zasad grupy”: ", " Wybierz Ponów, aby spróbować ponownie. (ServerUnavailable)", "Ponów")]
    public async Task ACheckThatFails_IsTheStateOfTheTab_WithRetry_AndRetryChecksAgainAtTheSameDepth(string culture, string lead, string tail, string retryLabel)
    {
        var h = await GroupPolicyHarness.CreateAsync(CultureInfo.GetCultureInfo(culture));
        using var document = await GroupPolicyDiagnosticsDocumentTests.OpenAsync(h);
        var rows = document.Rows;
        var said = new StatusRecorder(document.Status);

        // The domain controller does not answer the full check.
        var fail = 1;
        h.Session.BeforeOperation = _ =>
        {
            if (Volatile.Read(ref fail) == 1)
            {
                throw new DirectoryException(DirectoryErrorKind.ServerUnavailable, "the domain controller does not answer (test)");
            }
        };
        await document.FullCheckCommand.ExecuteAsync(null);

        // The findings of before stay; the line says why the check did not happen: what was tried, the reason, what to do,
        // the code - with Retry. It lasts, and nothing is busy.
        Assert.False(document.IsLoading);
        Assert.False(document.IsFullChecking);
        Assert.Same(rows, document.Rows);
        Assert.Equal(GpoDiagnosticsDepth.Quick, document.LastDepth);
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

        // Retry, from the line, runs the check that failed - the full one - and not a look at the old findings.
        Volatile.Write(ref fail, 0);
        retry.Command.Execute(retry.Parameter);
        await B2Harness.WaitUntilAsync(() => !document.IsLoading && document.Status.Current.Kind == PageStatusKind.Idle);
        h.Session.BeforeOperation = null;
        Assert.Null(document.Error);
        Assert.Null(document.Status.Lasting);
        Assert.Equal(GpoDiagnosticsDepth.Full, document.LastDepth);
    }

    // ---- The state that lasts ---------------------------------------------------------------------------------------

    [Theory]
    [InlineData("en-US", "Group Policy has changed since the last check – the results may be out of date.", "Check again", "pl-PL",
        "Zasady grupy zmieniły się od ostatniego sprawdzenia – wyniki mogą być nieaktualne.", "Sprawdź ponownie")]
    [InlineData("pl-PL", "Zasady grupy zmieniły się od ostatniego sprawdzenia – wyniki mogą być nieaktualne.", "Sprawdź ponownie", "en-US",
        "Group Policy has changed since the last check – the results may be out of date.", "Check again")]
    public async Task GroupPolicyChangedSinceTheLastCheck_IsTheStateOfTheTab_WithCheckAgain_InTheLanguageOfTheMoment(
        string culture, string text, string label, string otherCulture, string otherText, string otherLabel)
    {
        var h = await GroupPolicyHarness.CreateAsync(CultureInfo.GetCultureInfo(culture));
        using var document = await GroupPolicyDiagnosticsDocumentTests.OpenAsync(h);
        await document.FullCheckCommand.ExecuteAsync(null);
        var rows = document.Rows;

        // A change elsewhere in this window: the findings stay, marked - an attention of the tab, with its one action.
        h.Services.NotifyChanged();
        await B2Harness.WaitUntilAsync(() => document.IsStale && !document.IsLoading && document.Status.Current.Kind == PageStatusKind.Attention);
        var stale = document.Status.Current;
        Assert.Equal((PageStatusKind.Attention, text), (stale.Kind, stale.Message));
        Assert.Equal("GpoDiag.Stale", stale.Log.ToString());
        Assert.False(stale.CanClose);
        Assert.Same(stale, document.Status.Lasting);
        var again = Assert.IsType<PageStatusAction>(stale.Action);
        Assert.Equal(label, again.Label);
        Assert.Same(document.CheckAgainCommand, again.Command);
        Assert.Same(rows, document.Rows);
        Assert.False(document.Status.CloseCommand.CanExecute(null));

        // The language of the moment: said again, nothing read again.
        h.B2.Localizer.SetCulture(CultureInfo.GetCultureInfo(otherCulture));
        await B2Harness.WaitUntilAsync(() => document.Status.Current.Message == otherText);
        Assert.Equal(PageStatusKind.Attention, document.Status.Current.Kind);
        Assert.Equal(otherLabel, Assert.IsType<PageStatusAction>(document.Status.Current.Action).Label);
        Assert.True(document.IsStale);

        // The action of the line checks again at the depth of the findings: the summary is back, and nothing is left to say.
        document.Status.Current.Action!.Command.Execute(null);
        await B2Harness.WaitUntilAsync(() => !document.IsStale && !document.IsLoading && document.Status.Current.Kind == PageStatusKind.Idle);
        Assert.Null(document.Status.Lasting);
        Assert.Equal(GpoDiagnosticsDepth.Full, document.LastDepth);
        Assert.NotSame(rows, document.Rows);
    }

    [Fact]
    public async Task NotConnected_EmptiesTheTab_AndWhatIsMostImportantComesFirst()
    {
        var h = await GroupPolicyHarness.CreateAsync(English);
        using var document = await GroupPolicyDiagnosticsDocumentTests.OpenAsync(h);
        h.Services.NotifyChanged();
        await B2Harness.WaitUntilAsync(() => document.IsStale && !document.IsLoading && document.Status.Current.Kind == PageStatusKind.Attention);

        // The session ends while the findings are marked: the line says that, and the other state is behind it.
        await h.B2.Sessions.DisconnectAsync();
        await B2Harness.WaitUntilAsync(() => document.Status.Current.Kind == PageStatusKind.Error);
        Assert.Equal("Not connected to a domain.", document.Status.Current.Message);
        Assert.False(document.IsStale);
        Assert.False(document.HasResult);
        Assert.Empty(document.Rows);
        Assert.False(document.IsQuickChecking);
        Assert.False(document.IsFullChecking);
        Assert.False(document.QuickCheckCommand.CanExecute(null));
        Assert.False(document.Status.CloseCommand.CanExecute(null));
    }

    // ---- Notes ------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task WhatTheCheckCouldNotEvaluate_IsANote_NotStatus()
    {
        var h = await GroupPolicyHarness.CreateAsync(English);
        using var document = Create(h);
        var said = new StatusRecorder(document.Status);
        await document.LoadAsync();

        // What a quick check does not cover is a sentence about the findings - for as long as they are shown.
        Assert.True(document.HasNotes);
        Assert.StartsWith("A quick check does not cover: ", Assert.Single(document.Notes), StringComparison.Ordinal);
        Assert.Equal(PageStatusKind.Idle, document.Status.Current.Kind);
        Assert.Null(document.Status.Lasting);
        Assert.DoesNotContain(said.Plain, s => s.Kind is PageStatusKind.Attention or PageStatusKind.Error or PageStatusKind.Done);
    }

    // ---- The repairs of the tab -------------------------------------------------------------------------------------

    [Fact]
    public async Task ARepairThatCannotBePlanned_SaysWhyInTheLineOfTheTab_WithRetry_NotInADialog()
    {
        var h = await GroupPolicyHarness.CreateAsync(English);
        using var document = await GroupPolicyDiagnosticsDocumentTests.OpenAsync(h);
        await document.FullCheckCommand.ExecuteAsync(null);
        Assert.Same(document.Status, document.Actions.Status);
        document.SelectedRow = document.Rows.Single(r => r.Code == GpoFindingCodes.GpoLinksAllDisabled);
        var summary = document.Status.Current;
        var rows = document.Rows;

        // The domain controller does not answer the planner: nothing was changed - an error of the line, with the reason
        // and the code, and Retry, which makes the plan of the same repair again. No dialog.
        var fail = 1;
        h.Session.BeforeOperation = _ =>
        {
            if (Volatile.Read(ref fail) == 1)
            {
                throw new DirectoryException(DirectoryErrorKind.ServerUnavailable, "the domain controller does not answer (test)");
            }
        };
        var said = new StatusRecorder(document.Status);
        await document.FixCommand.ExecuteAsync(null);

        var failed = document.Status.Current;
        Assert.Equal(PageStatusKind.Error, failed.Kind);
        Assert.Contains("– nothing was changed: ", failed.Message, StringComparison.Ordinal);
        Assert.EndsWith("Press Retry to try again. (ServerUnavailable)", failed.Message, StringComparison.Ordinal);
        Assert.Equal("GroupPolicy.Status.NotChanged [ServerUnavailable]", failed.Log.ToString());
        Assert.Equal("Retry", Assert.IsType<PageStatusAction>(failed.Action).Label);
        Assert.Empty(h.B2.Dialogs.Errors);
        Assert.Empty(h.Windows.Previews);
        Assert.Same(rows, document.Rows);
        Assert.Equal(PageStatusKind.Working, said.Plain[0].Kind);
        Assert.Contains("preparing the changes…", said.Plain[0].Message, StringComparison.Ordinal);

        // Closed, the line is the summary again.
        document.Status.CloseCommand.Execute(null);
        Assert.Same(summary, document.Status.Current);
        Volatile.Write(ref fail, 0);
        h.Session.BeforeOperation = null;
    }

    // ---- The log copy -----------------------------------------------------------------------------------------------

    [Fact]
    public async Task TheLogCopies_CarryKeysCountsAndCodes_NeverAGpoANameOrAServerText()
    {
        var logger = new ListLogger<PageStatusHolder>();
        var h = await GroupPolicyHarness.CreateAsync(Polish, logger);
        using var document = await GroupPolicyDiagnosticsDocumentTests.OpenAsync(h);

        // A check with its stages, a check that fails, findings that are marked stale.
        await document.FullCheckCommand.ExecuteAsync(null);
        h.Session.BeforeOperation = _ => throw new DirectoryException(DirectoryErrorKind.ServerUnavailable, "serwer dc01.contoso.test nie odpowiada");
        await document.FullCheckCommand.ExecuteAsync(null);
        h.Session.BeforeOperation = null;
        Assert.Equal(PageStatusKind.Error, document.Status.Current.Kind);
        await document.RefreshCommand.ExecuteAsync(null);
        h.Services.NotifyChanged();
        await B2Harness.WaitUntilAsync(() => document.IsStale && !document.IsLoading && document.Status.Current.Kind == PageStatusKind.Attention);

        var messages = logger.Entries.Select(entry => entry.Message).ToList();
        Assert.Contains(messages, m => m.Contains("GroupPolicy.Status.Reading", StringComparison.Ordinal));
        Assert.Contains(messages, m => m.Contains("GroupPolicy.Status.ReadFailed", StringComparison.Ordinal) && m.Contains("[ServerUnavailable]", StringComparison.Ordinal));
        Assert.Contains(messages, m => m.Contains("GpoDiag.Stale", StringComparison.Ordinal));
        Assert.DoesNotContain("Stacje robocze", logger.AllText, StringComparison.Ordinal);
        Assert.DoesNotContain("Mapowanie dysków", logger.AllText, StringComparison.Ordinal);
        Assert.DoesNotContain("Diagnostyka", logger.AllText, StringComparison.Ordinal);
        Assert.DoesNotContain("Odczyt folderów", logger.AllText, StringComparison.Ordinal);
        Assert.DoesNotContain("contoso", logger.AllText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("nie odpowiada", logger.AllText, StringComparison.Ordinal);
    }
}
