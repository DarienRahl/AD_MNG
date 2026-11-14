using System.Globalization;
using AdMng.Presentation.Feedback;
using AdMng.Presentation.Localization;
using AdMng.Presentation.Tests.TestDoubles;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;

namespace AdMng.Presentation.Tests.Feedback;

/// <summary>
/// What stage V2b added to the status holder (docs/dev/v-interface-renewal.md, "V2b-1 as built", Foundations): the state
/// of a page that lasts - it takes the place of the summary and is back when a result was closed - and the page in
/// front, with which one status line serves a window whose pages say something of their own.
/// </summary>
public class PageStatusSourcesTests
{
    private const string Reading = "Wczytywanie „Jan Kowalski”…";
    private static readonly PageStatusLog ReadingLog = new(L.PropertySheetStatus.Reading);
    private static readonly PageStatusLog SavedLog = new(L.PropertySheetStatus.Saved);

    // ---- A state that lasts -------------------------------------------------------------------------------------

    [Fact]
    public void ALastingState_TakesThePlaceOfTheSummary_AndCannotBeClosed()
    {
        var (holder, changes) = Create();
        holder.SetIdle("Grupy: 12");
        var refresh = new RelayCommand(() => { });
        changes.Clear();

        // Handed over as something that can be closed: as a state of the page it cannot.
        holder.SetLasting(PageStatus.Attention("Obiekt zmieniono w innym miejscu.", new PageStatusLog(L.PropertySheet.Outdated), new PageStatusAction("Odśwież", refresh)));

        var lasting = Assert.IsType<PageStatus>(holder.Lasting);
        Assert.Same(lasting, holder.Current);
        Assert.Equal(PageStatusKind.Attention, lasting.Kind);
        Assert.Equal("Obiekt zmieniono w innym miejscu.", lasting.Message);
        Assert.Same(refresh, Assert.IsType<PageStatusAction>(lasting.Action).Command);
        Assert.False(lasting.CanClose);
        Assert.False(holder.CloseCommand.CanExecute(null));
        Assert.False(holder.IsWorking);
        Assert.Contains(nameof(PageStatusHolder.Lasting), changes);
        Assert.Contains(nameof(PageStatusHolder.Current), changes);

        // The close command does not take it away, and the summary changes quietly behind it.
        holder.CloseCommand.Execute(null);
        Assert.Same(lasting, holder.Current);
        holder.SetIdle("Grupy: 13");
        Assert.Same(lasting, holder.Current);
        Assert.Equal("Grupy: 13", holder.Idle.Message);

        // It goes when the page says so: the summary again.
        holder.SetLasting(null);
        Assert.Null(holder.Lasting);
        Assert.Equal("Grupy: 13", holder.Current.Message);
        Assert.True(holder.Current.IsIdle);
    }

    [Fact]
    public void AnOperationAndItsResult_CoverTheLastingState_AndItIsBackWhenTheResultIsClosed()
    {
        var (holder, _) = Create();
        holder.SetIdle("Odczytano z dc01.contoso.test");
        holder.SetLasting(PageStatus.Attention("Obiekt zmieniono w innym miejscu.", new PageStatusLog(L.PropertySheet.Outdated)));
        var lasting = holder.Current;

        // An operation takes the line…
        var scope = holder.Begin("Zapisywanie zmian obiektu „Jan Kowalski”…", new PageStatusLog(L.PropertySheetStatus.Saving), canCancel: false);
        Assert.True(holder.IsWorking);
        Assert.Equal(PageStatusKind.Working, holder.Current.Kind);
        Assert.Same(lasting, holder.Lasting);

        // …its result is shown and can be closed…
        scope.CompleteError("Nie udało się zapisać zmian obiektu „Jan Kowalski”: Odmowa dostępu.", new PageStatusLog(L.PropertySheetStatus.SaveFailed));
        Assert.Equal(PageStatusKind.Error, holder.Current.Kind);
        Assert.True(holder.CloseCommand.CanExecute(null));

        // …and closed, the page says its lasting state again - not the summary.
        holder.CloseCommand.Execute(null);
        Assert.Same(lasting, holder.Current);
        Assert.False(holder.CloseCommand.CanExecute(null));

        // An operation that ends without a message ends in the lasting state as well, and so does Clear.
        using (holder.Begin(Reading, ReadingLog))
        {
            Assert.True(holder.IsWorking);
        }

        Assert.Same(lasting, holder.Current);
        holder.Show(PageStatus.Done("Gotowe.", SavedLog));
        holder.Clear();
        Assert.Same(lasting, holder.Current);
    }

    [Fact]
    public void ALastingStateSetWhileAnOperationRuns_WaitsForItsEnd()
    {
        var (holder, changes) = Create();
        holder.SetIdle("Grupy: 12");
        var scope = holder.Begin(Reading, ReadingLog);
        var working = holder.Current;
        changes.Clear();

        // The read failed: the page will be empty for as long as this lasts - said when the operation is over.
        var failed = PageStatus.Error("Nie udało się wczytać „Jan Kowalski”: Kontroler domeny jest niedostępny.", new PageStatusLog(L.PropertySheetStatus.ReadFailed));
        holder.SetLasting(failed);
        Assert.Same(working, holder.Current);
        Assert.True(holder.IsWorking);
        Assert.False(scope.IsEnded);
        Assert.False(scope.Token.IsCancellationRequested);
        Assert.DoesNotContain(nameof(PageStatusHolder.Current), changes);
        Assert.Contains(nameof(PageStatusHolder.Lasting), changes);

        scope.Dispose();
        Assert.Equal(PageStatusKind.Error, holder.Current.Kind);
        Assert.Equal(failed.Message, holder.Current.Message);
        Assert.False(holder.Current.CanClose);
        Assert.Same(holder.Lasting, holder.Current);

        // Removed while another operation runs: that operation ends in the summary.
        var again = holder.Begin(Reading, ReadingLog);
        holder.SetLasting(null);
        Assert.True(holder.IsWorking);
        again.Complete(null);
        Assert.Equal("Grupy: 12", holder.Current.Message);
        Assert.True(holder.Current.IsIdle);
    }

    [Fact]
    public void ANewLastingState_IsSaidAtOnce_AlsoInPlaceOfAResult_AndTheSameStateAgainChangesNothing()
    {
        var (holder, changes) = Create();
        var done = PageStatus.Done("Zmiany obiektu „Jan Kowalski” zapisano w domenie contoso.test.", SavedLog);
        holder.Show(done);

        // Something happened to the page after the result: it speaks last.
        holder.SetLasting(PageStatus.Error("Obiekt „Jan Kowalski” usunięto, gdy to okno było otwarte.", new PageStatusLog(L.PropertySheetStatus.Deleted)));
        var deleted = holder.Current;
        Assert.Equal(PageStatusKind.Error, deleted.Kind);
        Assert.Same(holder.Lasting, deleted);

        // A page computes its state anew whenever one of its conditions changes: an equal state is no change - it does
        // not take a result off the line that was shown since.
        holder.Show(done);
        changes.Clear();
        holder.SetLasting(PageStatus.Error("Obiekt „Jan Kowalski” usunięto, gdy to okno było otwarte.", new PageStatusLog(L.PropertySheetStatus.Deleted), canClose: false));
        Assert.Same(done, holder.Current);
        Assert.Same(deleted, holder.Lasting);
        Assert.Empty(changes);

        // Another text is another state.
        holder.SetLasting(PageStatus.Error("Sesja tego okna została zakończona.", new PageStatusLog(L.WindowSession.Ended)));
        Assert.Equal("Sesja tego okna została zakończona.", holder.Current.Message);

        // Removing a state that is not shown leaves what is shown.
        holder.Show(done);
        holder.SetLasting(null);
        Assert.Same(done, holder.Current);
        holder.CloseCommand.Execute(null);
        Assert.Same(PageStatus.Empty, holder.Current);
    }

    [Fact]
    public void ALastingState_IsAnAttentionOrAnError_AndIsLoggedWithoutItsText()
    {
        var logger = new RecordingLogger();
        var holder = new PageStatusHolder(new ResxLocalizer(CultureInfo.GetCultureInfo("pl-PL")), new SynchronousUiDispatcher(), logger);

        Assert.Throws<ArgumentException>(() => holder.SetLasting(PageStatus.Done("Gotowe.", SavedLog)));
        Assert.Throws<ArgumentException>(() => holder.SetLasting(PageStatus.Working(Reading, ReadingLog)));
        Assert.Throws<ArgumentException>(() => holder.SetLasting(PageStatus.Idle("Grupy: 12")));
        Assert.Null(holder.Lasting);
        Assert.Same(PageStatus.Empty, holder.Current);
        Assert.Empty(logger.Entries);

        holder.SetLasting(PageStatus.Error(
            "Nie udało się wczytać „Łukasz Żółć”: Kontroler domeny jest niedostępny. (LDAP 81)",
            new PageStatusLog(L.PropertySheetStatus.ReadFailed).WithCode(ResultCode.Ldap(81))));

        var entry = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Error, entry.Level);
        Assert.Equal(L.PropertySheetStatus.ReadFailed, entry.Event.Name);
        Assert.DoesNotContain("Żółć", entry.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(entry.Values, value => value.Contains("Żółć", StringComparison.Ordinal));
        Assert.Contains("LDAP 81", entry.Message, StringComparison.Ordinal);
    }

    // ---- The page in front --------------------------------------------------------------------------------------

    [Fact]
    public void WithoutAPageInFront_AHolderShowsWhatItSaysItself()
    {
        var (window, changes) = Create();
        window.SetIdle("Odczytano z dc01.contoso.test");

        Assert.Null(window.Front);
        Assert.Same(window.Own, window.Current);
        using var scope = window.Begin(Reading, ReadingLog);
        Assert.Same(window.Own, window.Current);
        Assert.DoesNotContain(nameof(PageStatusHolder.Front), changes);
    }

    [Fact]
    public void ThePageInFront_SaysItsSummaryAndItsStatus_InTheLineOfTheWindow()
    {
        var (window, changes) = Create();
        var (general, _) = Create();
        var (memberOf, _) = Create();
        window.SetIdle("Odczytano z dc01.contoso.test");
        memberOf.SetIdle("Grupy: 12");

        // A page without anything to say: the summary of the window.
        window.Front = general;
        Assert.Same(general, window.Front);
        Assert.Contains(nameof(PageStatusHolder.Front), changes);
        Assert.Equal("Odczytano z dc01.contoso.test", window.Current.Message);

        // A page with a summary of its own.
        changes.Clear();
        window.Front = memberOf;
        Assert.Equal("Grupy: 12", window.Current.Message);
        Assert.Contains(nameof(PageStatusHolder.Current), changes);
        Assert.Equal("Odczytano z dc01.contoso.test", window.Own.Message);

        // The page works: the line of the window says so, with the Stop of the page - the window itself is not working.
        var scope = memberOf.Begin("Wczytywanie listy „Wszystkie (zagnieżdżone)”…", new PageStatusLog(L.PropertySheetStatus.ListReading));
        Assert.Same(memberOf.Current, window.Current);
        Assert.Equal(PageStatusKind.Working, window.Current.Kind);
        Assert.False(window.IsWorking);
        Assert.Null(window.Operation);
        Assert.False(window.CancelCommand.CanExecute(null));
        var stop = Assert.IsType<PageStatusAction>(window.Current.Action);
        Assert.Same(memberOf.CancelCommand, stop.Command);

        // Stop, pressed in the line of the window, stops the operation of the page.
        stop.Command.Execute(stop.Parameter);
        Assert.True(scope.Token.IsCancellationRequested);
        Assert.Equal("Zatrzymywanie…", window.Current.Message);
        scope.CompleteAttention("Wczytywanie listy zatrzymano.", new PageStatusLog(L.PropertySheetStatus.ListStopped));
        Assert.Equal(PageStatusKind.Attention, window.Current.Kind);

        // The close button of the window's line closes the result in the holder that said it.
        Assert.True(window.CloseCommand.CanExecute(null));
        window.CloseCommand.Execute(null);
        Assert.True(memberOf.Current.IsIdle);
        Assert.Equal("Grupy: 12", window.Current.Message);
        Assert.False(window.CloseCommand.CanExecute(null));

        // No page in front any more: the window alone.
        window.Front = null;
        Assert.Equal("Odczytano z dc01.contoso.test", window.Current.Message);
        memberOf.SetIdle("Grupy: 13");
        Assert.Equal("Odczytano z dc01.contoso.test", window.Current.Message);
    }

    [Fact]
    public void ATabThatFailedWhileItWasNotSelected_SaysSoWhenItIsSelected()
    {
        var (window, _) = Create();
        var (general, _) = Create();
        var (editor, _) = Create();
        window.SetIdle("Odczytano z dc01.contoso.test");
        window.Front = general;

        // The window reads; a page that is not in front fails meanwhile.
        using (window.Begin(Reading, ReadingLog))
        {
            editor.SetLasting(PageStatus.Error(
                "Nie udało się wczytać karty „Edytor atrybutów”: Kontroler domeny jest niedostępny.",
                new PageStatusLog(L.PropertySheetStatus.PageReadFailed)));
            Assert.Equal(PageStatusKind.Working, window.Current.Kind);
        }

        // Nothing of it on the page in front…
        Assert.Equal("Odczytano z dc01.contoso.test", window.Current.Message);

        // …and all of it when the failed tab is selected - and gone again when the operator leaves it.
        window.Front = editor;
        Assert.Same(editor.Current, window.Current);
        Assert.Equal(PageStatusKind.Error, window.Current.Kind);
        Assert.False(window.CloseCommand.CanExecute(null));
        window.Front = general;
        Assert.True(window.Current.IsIdle);
        window.Front = editor;
        Assert.Equal(PageStatusKind.Error, window.Current.Kind);
    }

    [Fact]
    public void TheWindowsOwnOperation_TakesTheLineWhileItRuns_AndItsResultIsNotLostToAPage()
    {
        var (window, _) = Create();
        var (page, _) = Create();
        window.Front = page;
        page.SetLasting(PageStatus.Error("Nie udało się wczytać karty „Członek grupy”.", new PageStatusLog(L.PropertySheetStatus.PageReadFailed)));
        var pageFailed = page.Current;
        Assert.Same(pageFailed, window.Current);

        // The operator presses Apply on a page that could not be read: the window's operation takes the line…
        var saving = window.Begin("Zapisywanie zmian obiektu „Jan Kowalski”…", new PageStatusLog(L.PropertySheetStatus.Saving), canCancel: false);
        Assert.Same(window.Own, window.Current);
        Assert.True(window.Current.IsWorking);

        // …a page that says something new meanwhile does not take it…
        using (var pageLoad = page.Begin("Wczytywanie karty „Członek grupy”…", new PageStatusLog(L.PropertySheetStatus.PageReading)))
        {
            Assert.Same(window.Own, window.Current);
        }

        Assert.Same(window.Own, window.Current);

        // …and the result of the window is what the operator sees: it was said last.
        saving.CompleteDone("Zmiany obiektu „Jan Kowalski” zapisano w domenie contoso.test.", SavedLog);
        Assert.Equal(PageStatusKind.Done, window.Current.Kind);
        Assert.Same(pageFailed, page.Current);

        // Closed, the page in front says again why it is empty.
        Assert.True(window.CloseCommand.CanExecute(null));
        window.CloseCommand.Execute(null);
        Assert.Same(pageFailed, window.Current);
        Assert.False(window.CloseCommand.CanExecute(null));
    }

    [Fact]
    public void AResultOfTheWindow_DoesNotVanishBecauseAPageFinishedLoading()
    {
        var (window, _) = Create();
        var (page, _) = Create();
        page.SetIdle("Opublikowane certyfikaty: 2");
        window.Front = page;

        // The page reads; the window finishes something of its own meanwhile.
        var pageLoad = page.Begin("Wczytywanie karty „Opublikowane certyfikaty”…", new PageStatusLog(L.PropertySheetStatus.PageReading));
        Assert.True(window.Current.IsWorking);
        window.Show(PageStatus.Done("Zmiany obiektu „Jan Kowalski” zapisano w domenie contoso.test.", SavedLog));
        var saved = window.Current;
        Assert.Equal(PageStatusKind.Done, saved.Kind);

        // A new count of the page's running operation is not "saying something new"…
        pageLoad.Report("Wczytywanie karty „Opublikowane certyfikaty”: 1 z 2…", PageProgress.Of(1, 2), new PageStatusLog(L.PropertySheetStatus.PageReading, 1, 2));
        Assert.Same(saved, window.Current);

        // …and the page finishing its load leaves the result of the window where it is.
        pageLoad.Complete(null);
        Assert.True(page.Current.IsIdle);
        Assert.Same(saved, window.Current);

        // A result of the page, said after that, is the newest thing said.
        page.Show(PageStatus.Attention("Nie wszystkie kontrolery domeny odpowiedziały: dc02.", new PageStatusLog(L.Computers.ExactNotAnswered, 1)));
        Assert.Equal(PageStatusKind.Attention, window.Current.Kind);

        // Closed, the result of the window is back; closed too, the summary of the page.
        window.CloseCommand.Execute(null);
        Assert.Same(saved, window.Current);
        window.CloseCommand.Execute(null);
        Assert.Equal("Opublikowane certyfikaty: 2", window.Current.Message);
    }

    [Fact]
    public void AnErrorOfTheWindowThatCannotBeClosed_IsSaidWhateverThePagesSay()
    {
        var (window, _) = Create();
        var (page, _) = Create();
        var (other, _) = Create();
        window.Front = page;
        other.SetLasting(PageStatus.Error("Nie udało się wczytać karty „Członek grupy”.", new PageStatusLog(L.PropertySheetStatus.PageReadFailed)));

        window.SetLasting(PageStatus.Error("Sesja, w której otwarto to okno, została zakończona.", new PageStatusLog(L.WindowSession.Ended)));
        var ended = window.Current;
        Assert.Equal(PageStatusKind.Error, ended.Kind);

        // A page with an error of its own comes to the front, a page starts to read, a page fails: the window still says
        // that nothing here can be used any more.
        window.Front = other;
        Assert.Same(ended, window.Current);
        using (other.Begin("Wczytywanie karty „Członek grupy”…", new PageStatusLog(L.PropertySheetStatus.PageReading)))
        {
            Assert.Same(ended, window.Current);
        }

        page.Show(PageStatus.Error("Nie udało się odczytać.", new PageStatusLog(L.PropertySheetStatus.ComputerExactFailed)));
        window.Front = page;
        Assert.Same(ended, window.Current);
        Assert.False(window.CloseCommand.CanExecute(null));

        // An attention of the window that lasts is not that strong: the page that comes to the front is heard, and the
        // attention is back when the page has nothing to say.
        window.SetLasting(PageStatus.Attention("Obiekt zmieniono w innym miejscu.", new PageStatusLog(L.PropertySheet.Outdated)));
        var outdated = window.Own;
        Assert.Same(outdated, window.Current);
        window.Front = other;
        Assert.Same(other.Current, window.Current);
        other.SetLasting(null);
        Assert.Same(outdated, window.Current);
    }

    [Fact]
    public void APartInFrontOfThePageInFront_IsShownThroughBoth()
    {
        var (window, _) = Create();
        var (memberOf, _) = Create();
        var (nested, _) = Create();
        window.SetIdle("Odczytano z dc01.contoso.test");
        memberOf.SetIdle("Grupy: 2");
        nested.SetIdle("5 obiektów");
        window.Front = memberOf;

        // The inner tab "All (nested)" of the page in front.
        memberOf.Front = nested;
        Assert.Equal("5 obiektów", window.Current.Message);
        var load = nested.Begin("Wczytywanie listy „Wszystkie (zagnieżdżone)”…", new PageStatusLog(L.PropertySheetStatus.ListReading));
        Assert.Same(nested.Current, window.Current);
        nested.SetLasting(PageStatus.Error("Nie udało się wczytać listy „Wszystkie (zagnieżdżone)”.", new PageStatusLog(L.PropertySheetStatus.ListReadFailed)));
        load.Dispose();
        Assert.Equal(PageStatusKind.Error, window.Current.Kind);

        // Back on the inner tab "Direct": the list in front could be read, the line says its summary.
        memberOf.Front = null;
        Assert.Equal("Grupy: 2", window.Current.Message);
        memberOf.Front = nested;
        Assert.Equal(PageStatusKind.Error, window.Current.Kind);

        // A holder cannot be in front of itself, directly or through its parts.
        Assert.Throws<ArgumentException>(() => nested.Front = nested);
        Assert.Throws<ArgumentException>(() => nested.Front = window);
        Assert.Null(nested.Front);
    }

    [Fact]
    public void AHolder_ListensOnlyToThePageThatIsInFront()
    {
        var (window, changes) = Create();
        var (first, _) = Create();
        var (second, _) = Create();
        window.Front = first;
        window.Front = second;
        changes.Clear();

        // The page that left the front says something: the window does not hear it.
        first.Show(PageStatus.Error("Nie udało się wczytać.", new PageStatusLog(L.PropertySheetStatus.PageReadFailed)));
        first.SetIdle("Grupy: 3");
        Assert.Empty(changes);
        Assert.Same(PageStatus.Empty, window.Current);

        // Setting the same page again is no change.
        window.Front = second;
        Assert.Empty(changes);
    }

    [Fact]
    public void EveryHolder_WritesItsOwnMessagesToTheLog_OnceEach()
    {
        var logger = new RecordingLogger();
        var localizer = new ResxLocalizer(CultureInfo.GetCultureInfo("pl-PL"));
        var window = new PageStatusHolder(localizer, new SynchronousUiDispatcher(), logger);
        var page = new PageStatusHolder(localizer, new SynchronousUiDispatcher(), logger);
        window.Front = page;

        // What the page says is shown by the window, but it is the page that said it: one log entry.
        page.Show(PageStatus.Error("Nie udało się wczytać karty „Członek grupy”.", new PageStatusLog(L.PropertySheetStatus.PageReadFailed)));
        Assert.Same(page.Current, window.Current);
        Assert.Equal(L.PropertySheetStatus.PageReadFailed, Assert.Single(logger.Entries).Event.Name);

        window.Front = null;
        window.Front = page;
        Assert.Single(logger.Entries);
    }

    // ---- "12 of 300" --------------------------------------------------------------------------------------------

    [Fact]
    public void Progress_SaysHowFarAnOperationOverManyObjectsIs_InBothLanguages()
    {
        var english = new ResxLocalizer(CultureInfo.GetCultureInfo("en-US"));
        var polish = new ResxLocalizer(CultureInfo.GetCultureInfo("pl-PL"));

        Assert.Equal("12 of 300", PageStatusText.Progress(english, 12, 300));
        Assert.Equal("12 z 300", PageStatusText.Progress(polish, 12, 300));
        Assert.Equal("1,500 of 52,000", PageStatusText.Progress(english, 1500, 52000));
        Assert.Equal($"1{Group(polish)}500 z 52{Group(polish)}000", PageStatusText.Progress(polish, 1500, 52000));
        Assert.Equal("0 of 0", PageStatusText.Progress(english, -4, -1));
        Assert.Throws<ArgumentNullException>(() => PageStatusText.Progress(null!, 1, 2));
    }

    [Fact]
    public void AnOperationOverManyObjects_ReportsItsTextItsPartDoneAndItsCounts_InOneCall()
    {
        var localizer = new ResxLocalizer(CultureInfo.GetCultureInfo("en-US"));
        var holder = new PageStatusHolder(localizer, new SynchronousUiDispatcher());
        using var scope = holder.Begin("Saving the accounts…", new PageStatusLog("Test.Saving"));
        Assert.True(Assert.NotNull(holder.Current.Progress).IsIndeterminate);
        var seen = new List<(string Message, double? Fraction)>();
        holder.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(PageStatusHolder.Current))
            {
                seen.Add((holder.Current.Message, holder.Current.Progress?.Fraction));
            }
        };

        foreach (var done in new[] { 12, 150, 300 })
        {
            scope.Report(
                $"Saving the accounts: {PageStatusText.Progress(localizer, done, 300)}…",
                PageProgress.Of(done, 300),
                new PageStatusLog("Test.Saving", done, 300));
        }

        // The text reads "12 of 300", the ring gets the part done, the log copy the two numbers - and it stays one
        // message: nothing is announced or animated three times.
        Assert.Equal(
            [("Saving the accounts: 12 of 300…", 0.04), ("Saving the accounts: 150 of 300…", 0.5), ("Saving the accounts: 300 of 300…", 1d)],
            seen);
        Assert.Equal("Test.Saving (300, 300)", holder.Current.Log.ToString());
        Assert.True(holder.Current.IsSameMessageAs(PageStatus.Working("Saving the accounts…", new PageStatusLog("Test.Saving"))));
        Assert.True(holder.IsWorking);
    }

    private static string Group(ILocalizer localizer) => localizer.Culture.NumberFormat.NumberGroupSeparator;

    private static (PageStatusHolder Holder, List<string?> Changes) Create()
    {
        var holder = new PageStatusHolder(new ResxLocalizer(CultureInfo.GetCultureInfo("pl-PL")), new SynchronousUiDispatcher());
        var changes = new List<string?>();
        holder.PropertyChanged += (_, e) => changes.Add(e.PropertyName);
        return (holder, changes);
    }

    /// <summary>Keeps every log call: the level, the event, the formatted text and the values behind the template.</summary>
    private sealed class RecordingLogger : ILogger
    {
        public List<(LogLevel Level, EventId Event, string Message, IReadOnlyList<string> Values)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            var values = state is IEnumerable<KeyValuePair<string, object?>> pairs
                ? pairs.Select(pair => Convert.ToString(pair.Value, CultureInfo.InvariantCulture) ?? string.Empty).ToList()
                : [];
            Entries.Add((logLevel, eventId, formatter(state, exception), values));
        }
    }
}
