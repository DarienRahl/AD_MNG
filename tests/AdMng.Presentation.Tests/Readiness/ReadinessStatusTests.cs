using System.Globalization;
using AdMng.Presentation.Feedback;
using AdMng.Presentation.Readiness;
using AdMng.Presentation.Tests.GroupPolicy;

namespace AdMng.Presentation.Tests.Readiness;

/// <summary>
/// What the "Gotowość środowiska" tab says in its status line (track V, stage V2b-4; docs/dev/v-interface-renewal.md "V2b-4
/// as built"), over the healthy sample domain of <see cref="ReadinessHarness"/>: the summary - what the last check found, then
/// when and where it was made -, the check as the operation of the tab with the area it is at, how far it is and Stop, and the
/// states that last (not connected, the connection changed, the shown check was stopped) with their action. What each check
/// finds is covered, unchanged, by <see cref="ReadinessDocumentTests"/> and the tests of the checks.
/// </summary>
public sealed class ReadinessStatusTests
{
    private static readonly CancellationToken Token = TestContext.Current.CancellationToken;

    private static readonly CultureInfo English = CultureInfo.GetCultureInfo("en-US");

    /// <summary>A check that sits at the first read of an event log until it is stopped (the area of the event logs is the fifth).</summary>
    private static TaskCompletionSource HoldTheEventLogs(ReadinessHarness harness)
    {
        var reading = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.Connector.BeforeRead = async (_, _, cancellationToken) =>
        {
            reading.TrySetResult();
            await Task.Delay(Timeout.Infinite, cancellationToken);
        };
        return reading;
    }

    private static int QuickAreas() => Enum.GetValues<ReadinessArea>().Count(ReadinessCodes.IsQuick);

    [Fact]
    public async Task BeforeTheFirstCheck_TheLineSaysWhatToDo_AndTheQuickCheckSaysEveryAreaAndHowFar()
    {
        var harness = await ReadinessHarness.CreateAsync();
        using var document = new ReadinessDocumentViewModel(harness.Services());
        var seen = new StatusRecorder(document.Status);
        Assert.IsAssignableFrom<IHasPageStatus>(document);
        Assert.Equal((PageStatusKind.Idle, "Jeszcze nie sprawdzono. Zacznij od szybkiego sprawdzenia."), (document.Status.Current.Kind, document.Status.Current.Message));
        Assert.False(document.IsQuickChecking);
        Assert.False(document.IsFullChecking);

        await document.QuickCheckCommand.ExecuteAsync(null);

        // Every area of a quick check, in order, as one message with its count: the ring shows the part done.
        var areas = Enum.GetValues<ReadinessArea>().Where(ReadinessCodes.IsQuick).ToList();
        var total = areas.Count;
        Assert.Equal(total, QuickAreas());
        var expected = new List<(PageStatusKind, string)>();
        for (var i = 0; i < total; i++)
        {
            expected.Add((PageStatusKind.Working, $"Szybkie sprawdzenie: {i + 1} z {total} – {harness.B2.Localizer[ReadinessText.AreaKey(areas[i])]}…"));
        }

        expected.Add((PageStatusKind.Idle, document.Status.Current.Message));
        Assert.Equal(expected, seen.Plain);
        Assert.Equal("Szybkie sprawdzenie: 1 z 4 – Połączenie…", seen.Plain[0].Message);
        Assert.Equal(
            Enumerable.Range(0, total).Select(i => (double?)i / total).ToArray(),
            seen.Seen.Where(s => s.Kind == PageStatusKind.Working).Select(s => s.Fraction).Distinct().ToArray());

        // The summary: what was found, then when and where - and the same sentence is not said a second time elsewhere.
        var summary = document.Status.Current;
        Assert.Equal(PageStatusKind.Idle, summary.Kind);
        Assert.Matches(@"^OK: \d+ · Ostrzeżenia: 1 · Błędy: 0 · Nie sprawdzono: \d+ · Ostatnie sprawdzenie: szybkie, .+, na dc01\.contoso\.test$", summary.Message);
        Assert.Equal($"{document.SummaryText} · {document.LastRunText}", summary.Message);
        Assert.Null(document.Status.Lasting);
        Assert.False(document.Status.CloseCommand.CanExecute(null));
        Assert.False(document.IsQuickChecking);
        Assert.False(document.IsFullChecking);
    }

    [Fact]
    public async Task TheFullCheck_SaysItsName_InEnglish_AndTheSummaryOfTheFirstCheckFollowsALanguageSwitch()
    {
        var harness = await ReadinessHarness.CreateAsync("en-US");
        using var document = new ReadinessDocumentViewModel(harness.Services());
        var seen = new StatusRecorder(document.Status);

        await document.FullCheckCommand.ExecuteAsync(null);

        var working = seen.Plain.Where(s => s.Kind == PageStatusKind.Working).Select(s => s.Message).ToList();
        Assert.Equal(8, working.Count);
        Assert.Equal("Full check: 1 of 8 – Connection…", working[0]);
        Assert.StartsWith("Full check: 5 of 8 – ", working[4], StringComparison.Ordinal);
        Assert.StartsWith("Full check: 8 of 8 – ", working[7], StringComparison.Ordinal);
        Assert.Matches(@"^OK: \d+ · Warnings: \d+ · Errors: \d+ · Not checked: \d+ · Last check: full, .+, on dc01\.contoso\.test$", document.Status.Current.Message);

        // The summary is built again in the language of the moment.
        harness.B2.Localizer.SetCulture(CultureInfo.GetCultureInfo("pl-PL"));
        Assert.Matches(@"^OK: \d+ · Ostrzeżenia: \d+ · Błędy: \d+ · Nie sprawdzono: \d+ · Ostatnie sprawdzenie: pełne, .+, na dc01\.contoso\.test$", document.Status.Current.Message);
    }

    [Fact]
    public async Task Stop_ReturnsControlAtOnce_TheStoppedCheckLasts_AndItsActionChecksAgain()
    {
        var harness = await ReadinessHarness.CreateAsync();
        var reading = HoldTheEventLogs(harness);
        using var document = new ReadinessDocumentViewModel(harness.Services());
        var seen = new StatusRecorder(document.Status);

        var run = document.CheckAsync(ReadinessDepth.Full);
        await reading.Task.WaitAsync(TimeSpan.FromSeconds(10), Token);

        // Working: the area it is at and how far it is - with Stop, which is the Cancel of the button that started it.
        var working = document.Status.Current;
        Assert.Equal((PageStatusKind.Working, "Pełne sprawdzenie: 5 z 8 – Dzienniki zdarzeń (E)…"), (working.Kind, working.Message));
        Assert.Equal(0.5, working.Progress?.Fraction);
        Assert.Equal("Readiness.Status.Checking (5, 8)", working.Log.ToString());
        var stop = Assert.IsType<PageStatusAction>(working.Action);
        Assert.Equal("Zatrzymaj", stop.Label);
        Assert.Same(document.Status.CancelCommand, stop.Command);
        Assert.True(document.IsFullChecking);
        Assert.False(document.IsQuickChecking);
        Assert.False(document.FullCheckCommand.CanExecute(null));

        // "Stopping…" at once - the read that is on its way to the domain controller has not returned.
        stop.Command.Execute(null);
        Assert.Equal((PageStatusKind.Working, "Zatrzymywanie…"), (document.Status.Current.Kind, document.Status.Current.Message));
        Assert.True(document.Status.IsCancelling);
        await run;

        Assert.Equal(
            [
                (PageStatusKind.Working, "Pełne sprawdzenie: 1 z 8 – Połączenie…"),
                (PageStatusKind.Working, "Pełne sprawdzenie: 2 z 8 – Katalog…"),
                (PageStatusKind.Working, "Pełne sprawdzenie: 3 z 8 – SYSVOL i zasady grupy (K2–K6)…"),
                (PageStatusKind.Working, "Pełne sprawdzenie: 4 z 8 – Zdalne akcje (K1)…"),
                (PageStatusKind.Working, "Pełne sprawdzenie: 5 z 8 – Dzienniki zdarzeń (E)…"),
                (PageStatusKind.Working, "Zatrzymywanie…"),
                (PageStatusKind.Attention, "Sprawdzanie zatrzymano: sprawdzenia, do których nie doszło, oznaczono jako niesprawdzone. Wybierz „Sprawdź ponownie”, aby sprawdzić ponownie."),
            ],
            seen.Plain);

        // The state of the tab, for as long as it shows that check: it cannot be closed, and its action checks again.
        var stopped = document.Status.Current;
        Assert.Same(document.Status.Lasting, stopped);
        Assert.False(stopped.CanClose);
        Assert.Equal("Readiness.Status.Stopped", stopped.Log.ToString());
        var again = Assert.IsType<PageStatusAction>(stopped.Action);
        Assert.Equal("Sprawdź ponownie", again.Label);
        Assert.Same(document.CheckAgainCommand, again.Command);
        Assert.True(document.WasCancelled);
        Assert.False(document.IsFullChecking);
        Assert.False(document.IsLoading);

        // The same Stop for the command of the document (the old Cancel button, F-keys of tests): checked below as well.
        harness.Connector.BeforeRead = null;
        await document.CheckAgainCommand.ExecuteAsync(null);

        Assert.False(document.WasCancelled);
        Assert.Null(document.Status.Lasting);
        Assert.Equal(PageStatusKind.Idle, document.Status.Current.Kind);
        Assert.Equal(ReadinessDepth.Full, document.LastDepth);
        Assert.Equal($"{document.SummaryText} · {document.LastRunText}", document.Status.Current.Message);
    }

    [Fact]
    public async Task AReconnect_StopsTheRunWithoutAWord_AndTheLineSaysTheConnectionChanged()
    {
        var harness = await ReadinessHarness.CreateAsync();
        var reading = HoldTheEventLogs(harness);
        using var document = new ReadinessDocumentViewModel(harness.Services());
        var seen = new StatusRecorder(document.Status);

        var run = document.CheckAsync(ReadinessDepth.Full);
        await reading.Task.WaitAsync(TimeSpan.FromSeconds(10), Token);
        await harness.ReconnectAsync(ReadinessHarness.Dc02);
        await run;

        // It was not the operator who stopped it: no "Stopping…", and the state that lasts is the more important one - the
        // results are of the previous connection - with the same action.
        Assert.DoesNotContain(seen.Plain, s => s.Message == "Zatrzymywanie…");
        var stale = document.Status.Current;
        Assert.Equal(PageStatusKind.Attention, stale.Kind);
        Assert.Equal(
            "Połączenie tego okna zmieniło się po pokazanym sprawdzeniu: wyniki mogą już nie być aktualne. Wybierz „Sprawdź ponownie”, aby sprawdzić ponownie.",
            stale.Message);
        Assert.Equal("Readiness.Status.Stale", stale.Log.ToString());
        Assert.False(stale.CanClose);
        Assert.Same(document.CheckAgainCommand, stale.Action?.Command);
        Assert.True(document.WasCancelled);
        Assert.True(document.IsStale);

        // Checking again on the new connection ends both.
        harness.Connector.BeforeRead = null;
        await document.CheckAgainCommand.ExecuteAsync(null);
        Assert.False(document.IsStale);
        Assert.False(document.WasCancelled);
        Assert.Null(document.Status.Lasting);
        Assert.EndsWith(", na dc02.contoso.test", document.Status.Current.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AChangedConnection_IsTheStateOfTheTab_UntilTheCheckIsMadeAgain_AndFollowsALanguageSwitch()
    {
        var harness = await ReadinessHarness.CreateAsync();
        using var document = new ReadinessDocumentViewModel(harness.Services());
        await document.CheckAsync(ReadinessDepth.Quick);
        var seen = new StatusRecorder(document.Status);

        await harness.ReconnectAsync(ReadinessHarness.Dc02);

        // The reconnect of the harness leaves the window without a session for a moment: that is said, and then the change.
        Assert.Equal(
            [
                (PageStatusKind.Error, "Brak połączenia z domeną."),
                (PageStatusKind.Attention, "Połączenie tego okna zmieniło się po pokazanym sprawdzeniu: wyniki mogą już nie być aktualne. Wybierz „Sprawdź ponownie”, aby sprawdzić ponownie."),
            ],
            seen.Plain);

        // The state is computed from the facts: in English after a switch, with the action in English.
        harness.B2.Localizer.SetCulture(English);
        var stale = document.Status.Current;
        Assert.Equal(
            "The connection of this window changed after the shown check: the results may no longer be true. Press “Check again” to check again.",
            stale.Message);
        Assert.Equal("Check again", stale.Action?.Label);
        Assert.Same(document.Status.Lasting, stale);

        await document.CheckAgainCommand.ExecuteAsync(null);
        Assert.Null(document.Status.Lasting);
        Assert.Equal(PageStatusKind.Idle, document.Status.Current.Kind);
    }

    [Fact]
    public async Task ATabWithoutAConnection_SaysSoFromTheFirstMoment_AndNothingIsChecked()
    {
        var harness = await ReadinessHarness.CreateAsync();
        await harness.B2.Sessions.DisconnectAsync();
        using var document = new ReadinessDocumentViewModel(harness.Services());

        var state = document.Status.Current;
        Assert.Equal((PageStatusKind.Error, "Brak połączenia z domeną."), (state.Kind, state.Message));
        Assert.False(state.CanClose);
        Assert.Null(state.Action);
        Assert.Equal("Brak połączenia z domeną.", document.StatusText);
        Assert.False(document.QuickCheckCommand.CanExecute(null));

        // A connection arrives: the state goes with it, the summary is back.
        await harness.ReconnectAsync();
        Assert.Equal(PageStatusKind.Idle, document.Status.Current.Kind);
        Assert.Null(document.Status.Lasting);
    }

    [Fact]
    public async Task TheWindowDisconnecting_AfterACheck_SaysNotConnected_NotTheStaleResults()
    {
        var harness = await ReadinessHarness.CreateAsync();
        using var document = new ReadinessDocumentViewModel(harness.Services());
        await document.CheckAsync(ReadinessDepth.Quick);

        await harness.B2.Sessions.DisconnectAsync();

        Assert.True(document.IsStale);
        Assert.Equal((PageStatusKind.Error, "Brak połączenia z domeną."), (document.Status.Current.Kind, document.Status.Current.Message));
        Assert.False(document.CheckAgainCommand.CanExecute(null));
    }

    [Fact]
    public async Task ClosingTheTab_DuringACheck_EndsTheOperation_AndNothingOfTheChecksIsInALogCopy()
    {
        var harness = await ReadinessHarness.CreateAsync();
        var reading = HoldTheEventLogs(harness);
        var document = new ReadinessDocumentViewModel(harness.Services());
        var logs = new List<string>();
        document.Status.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(PageStatusHolder.Current))
            {
                logs.Add(document.Status.Current.ToString());
            }
        };

        var run = document.CheckAsync(ReadinessDepth.Full);
        await reading.Task.WaitAsync(TimeSpan.FromSeconds(10), Token);
        document.Dispose();
        await run;

        Assert.False(document.Status.IsWorking);
        Assert.Null(document.Status.Operation);
        Assert.Contains("Working: Readiness.Status.Checking (5, 8)", logs);
        Assert.DoesNotContain(
            logs,
            log => log.Contains("dc0", StringComparison.OrdinalIgnoreCase) || log.Contains("contoso", StringComparison.OrdinalIgnoreCase)
                || log.Contains("Dzienniki", StringComparison.Ordinal) || log.Contains("Połączenie", StringComparison.Ordinal));
    }
}
