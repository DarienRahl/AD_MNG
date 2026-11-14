using System.ComponentModel;
using System.Globalization;
using AdMng.Core.Directory;
using AdMng.Core.Model;
using AdMng.Presentation.Dialogs;
using AdMng.Presentation.Feedback;
using AdMng.Presentation.Lists;
using AdMng.Presentation.Localization;
using AdMng.Testing;
using CommunityToolkit.Mvvm.Input;

namespace AdMng.Presentation.Tests.Dialogs;

/// <summary>
/// The status line of the object picker (track V, stage V2b; docs/dev/v-interface-renewal.md "V2b-2 as built"): "Find
/// Now" and the check of the typed names are the operations of the dialog - each with Stop in the line - and the line
/// says how they ended: how many objects the list shows (the summary), that more match than are listed, which names
/// were not resolved, stopped, failed with Retry; and, as the state that lasts, that the session of the dialog ended.
/// How names are resolved and objects are chosen is covered, unchanged, by <see cref="PickerTests"/>.
/// </summary>
public sealed class ObjectPickerStatusTests
{
    private static readonly CultureInfo Polish = CultureInfo.GetCultureInfo("pl-PL");
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(20);

    /// <summary>How long a held search waits for the test at most (a failing test must not keep a pool thread for good).</summary>
    private static readonly TimeSpan HoldLimit = TimeSpan.FromSeconds(60);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("en-US", "Searching in “contoso.test/Firma”…", "Found: 4 objects", "Found: 1 object", "Found: 0 objects", "Stop")]
    [InlineData("pl-PL", "Wyszukiwanie w „contoso.test/Firma”…", "Znaleziono: 4 obiekty", "Znaleziono: 1 obiekt", "Znaleziono: 0 obiektów", "Zatrzymaj")]
    public async Task FindNow_IsAnOperationOfTheDialog_AndTheCountOfTheListIsItsSummary(
        string culture, string working, string four, string one, string none, string stopLabel)
    {
        var harness = await B2Harness.CreateAsync(CultureInfo.GetCultureInfo(culture));
        using var picker = Groups(harness);
        Assert.IsAssignableFrom<IHasPageStatus>(picker);
        Assert.Same(PageStatus.Empty, picker.Status.Current);
        Assert.Null(picker.Status.Lasting);
        var seen = Record(picker.Status);
        Task finding;
        using (var hold = new Hold(harness.Session))
        {
            try
            {
                finding = picker.FindNowCommand.ExecuteAsync(null);
                await hold.Entered.WaitAsync(Limit, Ct);

                // Working: where the dialog searches, with Stop; the other commands wait.
                Assert.True(picker.IsSearching);
                Assert.True(picker.Status.IsWorking);
                Assert.Equal(PageStatusKind.Working, picker.Status.Current.Kind);
                Assert.Equal(working, picker.Status.Current.Message);
                Assert.Equal("ObjectPicker.Status.Searching", picker.Status.Current.Log.ToString());
                var stop = Assert.IsType<PageStatusAction>(picker.Status.Current.Action);
                Assert.Equal(stopLabel, stop.Label);
                Assert.Same(picker.Status.CancelCommand, stop.Command);
                Assert.False(picker.FindNowCommand.CanExecute(null));
                Assert.False(picker.OkCommand.CanExecute(null));
            }
            finally
            {
                hold.Release();
            }

            await finding.WaitAsync(Limit, Ct);
        }

        // The list is the result: no message, the summary says how many objects it shows.
        Assert.Equal([(PageStatusKind.Working, working), (PageStatusKind.Idle, four)], seen);
        Assert.Equal(4, picker.Results.Count);
        Assert.False(picker.IsSearching);
        Assert.False(picker.Status.CloseCommand.CanExecute(null));
        Assert.NotNull(picker.StatusText);

        // The plural form of the language: one object, none.
        picker.SearchText = "GG_IT";
        await picker.FindNowCommand.ExecuteAsync(null);
        Assert.Equal(one, picker.Status.Current.Message);
        picker.SearchText = "nie ma takiej grupy";
        await picker.FindNowCommand.ExecuteAsync(null);
        Assert.Equal(PageStatusKind.Idle, picker.Status.Current.Kind);
        Assert.Equal(none, picker.Status.Current.Message);
        Assert.Empty(picker.Results);

        // The summary follows the language.
        var other = culture == "pl-PL" ? CultureInfo.GetCultureInfo("en-US") : Polish;
        harness.Localizer.SetCulture(other);
        Assert.Equal(culture == "pl-PL" ? "Found: 0 objects" : "Znaleziono: 0 obiektów", picker.Status.Current.Message);
    }

    [Theory]
    [InlineData("en-US", "Searching was stopped: the list was not changed. Press “Find Now” to search again.", "Find Now")]
    [InlineData("pl-PL", "Wyszukiwanie zatrzymano: listy nie zmieniono. Wybierz „Znajdź teraz”, aby wyszukać ponownie.", "Znajdź teraz")]
    public async Task Stop_SaysStoppingAtOnce_AndTheSearchEndsAsAttention_WithTheListAsItWas(string culture, string stoppedText, string again)
    {
        var harness = await B2Harness.CreateAsync(CultureInfo.GetCultureInfo(culture));
        using var picker = Groups(harness);
        await picker.FindNowCommand.ExecuteAsync(null);
        Assert.Equal(4, picker.Results.Count);
        var summary = picker.Status.Current.Message;
        picker.SearchText = "GG_IT";
        var seen = Record(picker.Status);
        string working;
        Task finding;
        using (var hold = new Hold(harness.Session))
        {
            try
            {
                finding = picker.FindNowCommand.ExecuteAsync(null);
                await hold.Entered.WaitAsync(Limit, Ct);
                working = picker.Status.Current.Message;

                // Pressed: "Stopping…" at once, for as long as the domain controller has not answered.
                picker.Status.CancelCommand.Execute(null);
                Assert.True(picker.Status.IsCancelling);
                Assert.Equal(PageStatusKind.Working, picker.Status.Current.Kind);
                Assert.Equal(harness.Localizer[L.Feedback.Stopping], picker.Status.Current.Message);
                Assert.True(picker.IsSearching);
            }
            finally
            {
                hold.Release();
            }

            await finding.WaitAsync(Limit, Ct);
        }

        // Stopped: the list of before stays, "Find Now" searches again - and behind the message the summary of that list.
        Assert.Equal(
            [(PageStatusKind.Working, working), (PageStatusKind.Working, harness.Localizer[L.Feedback.Stopping]), (PageStatusKind.Attention, stoppedText)],
            seen);
        var stopped = picker.Status.Current;
        Assert.Equal("ObjectPicker.Status.SearchStopped", stopped.Log.ToString());
        Assert.True(stopped.CanClose);
        Assert.False(picker.IsSearching);
        Assert.Equal(4, picker.Results.Count);
        var action = Assert.IsType<PageStatusAction>(stopped.Action);
        Assert.Equal(again, action.Label);
        Assert.Same(picker.FindNowCommand, action.Command);
        Assert.Equal(summary, picker.Status.Idle.Message);

        await ((IAsyncRelayCommand)action.Command).ExecuteAsync(action.Parameter);
        Assert.Equal(PageStatusKind.Idle, picker.Status.Current.Kind);
        Assert.Single(picker.Results);
    }

    [Theory]
    [InlineData("en-US", "Could not search in “contoso.test/Firma”: ", " Press Retry to try again. (ServerUnavailable)", "Retry")]
    [InlineData("pl-PL", "Nie udało się przeszukać „contoso.test/Firma”: ", " Wybierz Ponów, aby spróbować ponownie. (ServerUnavailable)", "Ponów")]
    public async Task ASearchThatFails_IsAnErrorWithTheReasonTheCodeAndRetry_AndTheListIsAsItWas(string culture, string lead, string tail, string retryLabel)
    {
        var harness = await B2Harness.CreateAsync(CultureInfo.GetCultureInfo(culture));
        using var picker = Groups(harness);
        await picker.FindNowCommand.ExecuteAsync(null);
        var fail = true;
        harness.Session.BeforeOperation = operation =>
        {
            if (fail && operation.Kind == FakeOperationKind.Search)
            {
                throw new DirectoryException(DirectoryErrorKind.ServerUnavailable, "the domain controller dc01.contoso.test does not answer");
            }
        };
        var seen = Record(picker.Status);

        await picker.FindNowCommand.ExecuteAsync(null);

        Assert.Equal([PageStatusKind.Working, PageStatusKind.Error], seen.Select(s => s.Kind));
        var failed = picker.Status.Current;
        Assert.StartsWith(lead, failed.Message, StringComparison.Ordinal);
        Assert.EndsWith(tail, failed.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("dc01", failed.Message, StringComparison.Ordinal);
        Assert.Equal("ObjectPicker.Status.SearchFailed [ServerUnavailable]", failed.Log.ToString());
        Assert.True(failed.CanClose);
        Assert.Null(picker.Status.Lasting);
        Assert.False(picker.IsSearching);
        Assert.Equal(4, picker.Results.Count);
        var retry = Assert.IsType<PageStatusAction>(failed.Action);
        Assert.Equal(retryLabel, retry.Label);
        Assert.Same(picker.FindNowCommand, retry.Command);

        fail = false;
        await ((IAsyncRelayCommand)retry.Command).ExecuteAsync(retry.Parameter);
        Assert.Equal(PageStatusKind.Idle, picker.Status.Current.Kind);
        Assert.Equal(4, picker.Results.Count);
    }

    [Theory]
    [InlineData("en-US", "More than 500 objects match: the list shows the first 500. Type a name or choose another location to narrow the search.", "Found: 500 objects")]
    [InlineData("pl-PL", "Pasuje ponad 500 obiektów: lista pokazuje pierwsze 500. Wpisz nazwę albo wybierz inną lokalizację, aby zawęzić wyszukiwanie.", "Znaleziono: 500 obiektów")]
    public async Task MoreObjectsThanTheListShows_IsAnAttention_AndTheSummaryCountsWhatIsListed(string culture, string tooMany, string summary)
    {
        var harness = await B2Harness.CreateAsync(CultureInfo.GetCultureInfo(culture));
        var many = harness.Builder.AddOu("Pracownie");
        for (var i = 1; i <= ObjectPickerViewModel.MaxResults + 1; i++)
        {
            harness.Builder.AddComputer($"LAB-PC{i:0000}", many);
        }

        using var picker = harness.Factory.CreateObjectPicker(harness.Session, [ObjectKind.Computer], multiSelect: true);
        picker.LocationDn = many;
        var seen = Record(picker.Status);

        await picker.FindNowCommand.ExecuteAsync(null);

        // What is missing and what to do - an attention the operator can close; behind it, what the list shows.
        Assert.Equal([PageStatusKind.Working, PageStatusKind.Attention], seen.Select(s => s.Kind));
        var attention = picker.Status.Current;
        Assert.Equal(tooMany, attention.Message);
        Assert.Equal("ObjectPicker.Status.TooMany (500)", attention.Log.ToString());
        Assert.Null(attention.Action);
        Assert.True(attention.CanClose);
        Assert.Equal(ObjectPickerViewModel.MaxResults, picker.Results.Count);
        picker.Status.CloseCommand.Execute(null);
        Assert.Equal(PageStatusKind.Idle, picker.Status.Current.Kind);
        Assert.Equal(summary, picker.Status.Current.Message);
    }

    [Theory]
    [InlineData(
        "en-US",
        "Checking the names (3)…",
        "Not every name was resolved: Several objects match 'GG_' - select one in the list. No object found for 'nikt'.",
        "Found: 3 objects")]
    [InlineData(
        "pl-PL",
        "Sprawdzanie nazw (3)…",
        "Nie wszystkie nazwy rozpoznano: Nazwie „GG_” odpowiada kilka obiektów - wybierz jeden z listy. Nie znaleziono obiektu dla „nikt”.",
        "Znaleziono: 3 obiekty")]
    public async Task CheckingTheNames_IsAnOperationOfTheDialog_AndTheLineSaysWhichNamesWereNotResolved(string culture, string working, string unresolved, string summary)
    {
        var harness = await B2Harness.CreateAsync(CultureInfo.GetCultureInfo(culture));
        using var picker = harness.Factory.CreateObjectPicker(harness.Session, [ObjectKind.User, ObjectKind.Group], multiSelect: true);
        picker.SearchText = "Kowalski; GG_; nikt";
        var seen = Record(picker.Status);
        Task checking;
        using (var hold = new Hold(harness.Session))
        {
            try
            {
                checking = picker.CheckNamesCommand.ExecuteAsync(null);
                await hold.Entered.WaitAsync(Limit, Ct);

                // Working: how many names, with Stop.
                Assert.True(picker.IsSearching);
                Assert.Equal(PageStatusKind.Working, picker.Status.Current.Kind);
                Assert.Equal(working, picker.Status.Current.Message);
                Assert.Equal("ObjectPicker.Status.Checking (3)", picker.Status.Current.Log.ToString());
                Assert.Same(picker.Status.CancelCommand, Assert.IsType<PageStatusAction>(picker.Status.Current.Action).Command);
                Assert.Empty(picker.Chosen);
            }
            finally
            {
                hold.Release();
            }

            await checking.WaitAsync(Limit, Ct);
        }

        // What was skipped and why, name by name - an attention the operator can close; the names stay in the box, the
        // unique match is chosen, the matches of the ambiguous name are listed and counted behind the message.
        Assert.Equal([(PageStatusKind.Working, working), (PageStatusKind.Attention, unresolved)], seen);
        var attention = picker.Status.Current;
        Assert.Equal("ObjectPicker.Status.NotResolved (1, 1)", attention.Log.ToString());
        Assert.Null(attention.Action);
        Assert.True(attention.CanClose);
        Assert.Equal(["Jan Kowalski"], picker.Chosen.Select(c => c.Name));
        Assert.Equal("GG_; nikt", picker.SearchText);
        Assert.Equal(3, picker.Results.Count);
        Assert.False(picker.IsSearching);
        picker.Status.CloseCommand.Execute(null);
        Assert.Equal(summary, picker.Status.Current.Message);
    }

    [Fact]
    public async Task ACheckThatResolvedEveryName_SaysNothing_AndTheLineExplainsThreeNamesAtMost()
    {
        var harness = await B2Harness.CreateAsync();
        using var picker = harness.Factory.CreateObjectPicker(harness.Session, [ObjectKind.User, ObjectKind.Group], multiSelect: true);
        var seen = Record(picker.Status);

        // Every name has one match: the chosen objects are the answer.
        picker.SearchText = "Kowalski; GG_IT";
        await picker.CheckNamesCommand.ExecuteAsync(null);
        Assert.Equal([(PageStatusKind.Working, "Checking the names (2)…"), (PageStatusKind.Idle, string.Empty)], seen);
        Assert.Equal(2, picker.Chosen.Count);
        Assert.Equal(string.Empty, picker.SearchText);
        Assert.Same(PageStatus.Empty, picker.Status.Current);

        // Four names nobody has: three sentences in the line, all four names still in the box.
        picker.SearchText = "pierwszy; drugi; trzeci; czwarty";
        await picker.CheckNamesCommand.ExecuteAsync(null);
        var attention = picker.Status.Current;
        Assert.Equal(PageStatusKind.Attention, attention.Kind);
        Assert.Equal(
            "Not every name was resolved: No object found for 'pierwszy'. No object found for 'drugi'. No object found for 'trzeci'.",
            attention.Message);
        Assert.Equal("ObjectPicker.Status.NotResolved (4, 0)", attention.Log.ToString());
        Assert.Equal("pierwszy; drugi; trzeci; czwarty", picker.SearchText);
    }

    [Theory]
    [InlineData("en-US", "Checking the names was stopped: no object was added. Press “Check Names” to check again.", "Check Names")]
    [InlineData("pl-PL", "Sprawdzanie nazw zatrzymano: nie dodano żadnego obiektu. Wybierz „Sprawdź nazwy”, aby sprawdzić ponownie.", "Sprawdź nazwy")]
    public async Task StopWhileTheNamesAreChecked_AddsNothing_AlsoNotTheNamesThatWereAlreadyFound(string culture, string stoppedText, string again)
    {
        var harness = await B2Harness.CreateAsync(CultureInfo.GetCultureInfo(culture));
        using var picker = harness.Factory.CreateObjectPicker(harness.Session, [ObjectKind.User, ObjectKind.Group], multiSelect: true);
        picker.SearchText = "Kowalski; GG_IT";
        var seen = Record(picker.Status);
        string working;
        Task checking;

        // The first name is found; the search for the second one waits for the test.
        using (var hold = new Hold(harness.Session, skip: 1))
        {
            try
            {
                checking = picker.CheckNamesCommand.ExecuteAsync(null);
                await hold.Entered.WaitAsync(Limit, Ct);
                working = picker.Status.Current.Message;
                picker.Status.CancelCommand.Execute(null);
                Assert.True(picker.Status.IsCancelling);
                Assert.Equal(harness.Localizer[L.Feedback.Stopping], picker.Status.Current.Message);
            }
            finally
            {
                hold.Release();
            }

            await checking.WaitAsync(Limit, Ct);
        }

        // Stopped is stopped: nothing was added and the names are as they were typed.
        Assert.Equal(
            [(PageStatusKind.Working, working), (PageStatusKind.Working, harness.Localizer[L.Feedback.Stopping]), (PageStatusKind.Attention, stoppedText)],
            seen);
        var stopped = picker.Status.Current;
        Assert.Equal("ObjectPicker.Status.CheckStopped", stopped.Log.ToString());
        Assert.Empty(picker.Chosen);
        Assert.Equal("Kowalski; GG_IT", picker.SearchText);
        Assert.False(picker.IsSearching);
        var action = Assert.IsType<PageStatusAction>(stopped.Action);
        Assert.Equal(again, action.Label);
        Assert.Same(picker.CheckNamesCommand, action.Command);

        // The action checks again: both names are chosen and nothing is left to say.
        await ((IAsyncRelayCommand)action.Command).ExecuteAsync(action.Parameter);
        Assert.Equal(2, picker.Chosen.Count);
        Assert.Same(PageStatus.Empty, picker.Status.Current);
    }

    [Theory]
    [InlineData("en-US", "Could not check the names: ", " Press Retry to try again. (AccessDenied)", "Retry")]
    [InlineData("pl-PL", "Nie udało się sprawdzić nazw: ", " Wybierz Ponów, aby spróbować ponownie. (AccessDenied)", "Ponów")]
    public async Task ACheckThatFails_IsAnErrorWithRetry_AndOkStaysOpen(string culture, string lead, string tail, string retryLabel)
    {
        var harness = await B2Harness.CreateAsync(CultureInfo.GetCultureInfo(culture));
        using var picker = harness.Factory.CreateObjectPicker(harness.Session, [ObjectKind.User], multiSelect: false);
        bool? closed = null;
        picker.CloseRequested += (_, result) => closed = result;
        var fail = true;
        harness.Session.BeforeOperation = operation =>
        {
            if (fail && operation.Kind == FakeOperationKind.Search)
            {
                throw new DirectoryException(DirectoryErrorKind.AccessDenied, "00002098: SecErr: DSID-03150F94, problem 4003 (INSUFF_ACCESS_RIGHTS)");
            }
        };

        // OK with a name in the box checks the name first: the dialog stays open and the line says why.
        picker.SearchText = "Kowalski";
        await picker.OkCommand.ExecuteAsync(null);
        Assert.Null(closed);
        var failed = picker.Status.Current;
        Assert.Equal(PageStatusKind.Error, failed.Kind);
        Assert.StartsWith(lead, failed.Message, StringComparison.Ordinal);
        Assert.EndsWith(tail, failed.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("DSID", failed.Message, StringComparison.Ordinal);
        Assert.Equal("ObjectPicker.Status.CheckFailed [AccessDenied]", failed.Log.ToString());
        Assert.True(failed.CanClose);
        Assert.Empty(picker.Chosen);
        var retry = Assert.IsType<PageStatusAction>(failed.Action);
        Assert.Equal(retryLabel, retry.Label);
        Assert.Same(picker.CheckNamesCommand, retry.Command);

        // With a domain controller that answers, OK resolves the name and closes: the line says nothing more.
        fail = false;
        var seen = Record(picker.Status);
        await picker.OkCommand.ExecuteAsync(null);
        Assert.True(closed);
        Assert.Equal([harness.Company.JanKowalski], picker.Result);
        Assert.Equal([PageStatusKind.Working, PageStatusKind.Idle], seen.Select(s => s.Kind));
    }

    [Fact]
    public async Task TheEndOfTheSession_TakesTheLine_EndsBusyAtOnce_AndNothingCanBeSearchedAnyMore()
    {
        var harness = await B2Harness.CreateAsync();
        using var picker = Groups(harness);
        Assert.Null(picker.Status.Lasting);
        Task finding;
        List<(PageStatusKind Kind, string Message)> seen;
        using (var hold = new Hold(harness.Session))
        {
            try
            {
                finding = picker.FindNowCommand.ExecuteAsync(null);
                await hold.Entered.WaitAsync(Limit, Ct);
                Assert.True(picker.IsSearching);
                seen = Record(picker.Status);

                await harness.Sessions.DisconnectAsync();

                // At once - not when the domain controller answers: one message, and nothing is "busy".
                Assert.True(picker.IsSessionEnded);
                Assert.False(picker.IsSearching);
                Assert.False(picker.Status.IsWorking);
                Assert.Equal(PageStatusKind.Error, picker.Status.Current.Kind);
                Assert.Equal(harness.Localizer[L.WindowSession.Ended], picker.Status.Current.Message);
                Assert.False(picker.Status.Current.CanClose);
            }
            finally
            {
                hold.Release();
            }

            await finding.WaitAsync(Limit, Ct);
        }

        Assert.Equal([(PageStatusKind.Error, harness.Localizer[L.WindowSession.Ended])], seen);
        Assert.Equal("WindowSession.Ended", picker.Status.Current.Log.ToString());
        Assert.Empty(picker.Results);
        Assert.False(picker.IsSearching);

        // Nothing can be searched or checked; the dialog can be cancelled. In Polish after a language switch.
        picker.SearchText = "Kowalski";
        Assert.False(picker.FindNowCommand.CanExecute(null));
        Assert.False(picker.CheckNamesCommand.CanExecute(null));
        Assert.True(picker.CancelCommand.CanExecute(null));
        var english = picker.Status.Current.Message;
        harness.Localizer.SetCulture(Polish);
        Assert.Equal(harness.Localizer[L.WindowSession.Ended], picker.Status.Current.Message);
        Assert.NotEqual(english, picker.Status.Current.Message);
        Assert.Equal(PageStatusKind.Error, picker.Status.Current.Kind);
    }

    [Fact]
    public async Task TheLogCopies_CarryKeysNumbersAndCodes_NeverANameALocationOrAServerText()
    {
        var harness = await B2Harness.CreateAsync();
        var logger = new ListLogger<PageStatusHolder>();
        var lists = new ObjectListServices(harness.Sessions, harness.Localizer, harness.Dispatcher, harness.Settings, harness.Errors, harness.Changes, harness.Time, logger);
        var services = new DialogServices(
            harness.Sessions, harness.Localizer, harness.Dispatcher, harness.Settings, harness.Errors, harness.Changes, harness.Time, harness.Dialogs, harness.Files,
            harness.Clipboard, harness.Notifications, harness.Coordinator, harness.Activities, lists, harness.Regional);
        using var picker = new ObjectPickerViewModel(services, harness.Session, [ObjectKind.User, ObjectKind.Group], multiSelect: true);
        picker.LocationDn = harness.Company.CompanyOu;

        picker.SearchText = "Kowalski; GG_; nikt";
        await picker.CheckNamesCommand.ExecuteAsync(null);
        Assert.Contains("nikt", picker.Status.Current.Message, StringComparison.Ordinal);
        picker.SearchText = string.Empty;
        await picker.FindNowCommand.ExecuteAsync(null);
        harness.Session.BeforeOperation = operation =>
        {
            if (operation.Kind == FakeOperationKind.Search)
            {
                throw new DirectoryException(DirectoryErrorKind.ServerUnavailable, "dc01.contoso.test does not answer for OU=Firma");
            }
        };
        await picker.FindNowCommand.ExecuteAsync(null);
        harness.Session.BeforeOperation = null;
        Assert.Contains("Firma", picker.Status.Current.Message, StringComparison.Ordinal);

        var messages = logger.Entries.Select(entry => entry.Message).ToList();
        Assert.Contains(messages, m => m.Contains("ObjectPicker.Status.Checking (3)", StringComparison.Ordinal));
        Assert.Contains(messages, m => m.Contains("ObjectPicker.Status.NotResolved (1, 1)", StringComparison.Ordinal));
        Assert.Contains(messages, m => m.Contains("ObjectPicker.Status.Searching", StringComparison.Ordinal));
        Assert.Contains(messages, m => m.Contains("ObjectPicker.Status.SearchFailed", StringComparison.Ordinal) && m.Contains("ServerUnavailable", StringComparison.Ordinal));
        Assert.DoesNotContain("nikt", logger.AllText, StringComparison.Ordinal);
        Assert.DoesNotContain("GG_", logger.AllText, StringComparison.Ordinal);
        Assert.DoesNotContain("Kowalski", logger.AllText, StringComparison.Ordinal);
        Assert.DoesNotContain("Firma", logger.AllText, StringComparison.Ordinal);
        Assert.DoesNotContain("contoso", logger.AllText, StringComparison.OrdinalIgnoreCase);
    }

    // ---- Helpers ------------------------------------------------------------------------------------------------

    /// <summary>The picker of groups in OU=Firma: four groups of the sample company.</summary>
    private static ObjectPickerViewModel Groups(B2Harness harness)
    {
        var picker = harness.Factory.CreateObjectPicker(harness.Session, [ObjectKind.Group], multiSelect: true);
        picker.LocationDn = harness.Company.CompanyOu;
        return picker;
    }

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

    /// <summary>
    /// A slow domain controller: one search of the session - the first, or the one after <c>skip</c> others - waits
    /// until the test lets go and, like a real server, does not notice a cancellation meanwhile: what the dialog says
    /// while it waits lasts until then.
    /// </summary>
    private sealed class Hold : IDisposable
    {
        private readonly FakeDirectorySession _session;
        private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly ManualResetEventSlim _release = new();
        private int _left;

        public Hold(FakeDirectorySession session, int skip = 0)
        {
            _session = session;
            _left = skip + 1;
            session.BeforeOperation = operation =>
            {
                if (operation.Kind == FakeOperationKind.Search && Interlocked.Decrement(ref _left) == 0)
                {
                    _entered.TrySetResult();
                    _release.Wait(HoldLimit);
                }
            };
        }

        /// <summary>Completed once the held search reached the domain controller.</summary>
        public Task Entered => _entered.Task;

        public void Release() => _release.Set();

        public void Dispose()
        {
            _release.Set();
            _session.BeforeOperation = null;
        }
    }
}
