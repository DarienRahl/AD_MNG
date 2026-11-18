using System.Globalization;
using AdMng.Core.Directory;
using AdMng.Presentation.Feedback;
using AdMng.Presentation.Localization;
using AdMng.Testing;

namespace AdMng.Presentation.Tests.Dialogs;

/// <summary>
/// The status line of the Find window (track V, stage V2; the tool window pilot of docs/dev/v-interface-renewal.md
/// 6.1): the window has one status, the one of its results list - what to do before the first search, the search as an
/// operation that is stopped where it was started, the summary, a failed search with Retry, and the lasting error once
/// the session of the window ended.
/// </summary>
public class FindStatusTests
{
    [Fact]
    public async Task BeforeTheFirstSearch_TheLineSaysWhatToDo_InTheLanguageOfTheMoment()
    {
        var harness = await B2Harness.CreateAsync();
        using var find = harness.Factory.CreateFind(harness.Company.EmployeesOu);

        Assert.IsAssignableFrom<IHasPageStatus>(find);
        Assert.Same(find.Results.Status, find.Status);
        Assert.Equal(PageStatusKind.Idle, find.Status.Current.Kind);
        Assert.Equal("Say what to find and press Find Now.", find.Status.Current.Message);
        Assert.False(find.Status.IsWorking);
        Assert.False(find.Status.CancelCommand.CanExecute(null));
        Assert.False(find.Status.CloseCommand.CanExecute(null));

        harness.Localizer.SetCulture(CultureInfo.GetCultureInfo("pl-PL"));
        Assert.Equal("Określ, czego szukasz, i wybierz Znajdź teraz.", find.Status.Current.Message);
        Assert.True(find.Status.Current.IsIdle);
    }

    [Fact]
    public async Task ASearch_IsTheOperationOfTheWindow_AndIsStoppedWhereItWasStarted()
    {
        var harness = await B2Harness.CreateAsync();
        using var find = harness.Factory.CreateFind(harness.Company.EmployeesOu);
        find.NameText = "kowal";

        // The domain policy is read once per session, by the first list that needs it: before the directory gets slow.
        await harness.Sessions.GetDomainPolicyAsync(TestContext.Current.CancellationToken);
        harness.Session.Latency = TimeSpan.FromSeconds(30);

        var search = find.FindNowCommand.ExecuteAsync(null);

        // Running: the start command is busy (it cannot run twice), so the button that started it offers Cancel - the
        // holder's flags are what it binds to.
        Assert.True(find.HasSearched);
        Assert.True(find.Status.IsWorking);
        Assert.False(find.Status.IsCancelling);
        Assert.Equal("Reading “Find results”…", find.Status.Current.Message);
        Assert.False(find.FindNowCommand.CanExecute(null));
        Assert.True(find.Status.CancelCommand.CanExecute(null));
        var token = find.Status.Operation!.Token;
        var seen = new List<(PageStatusKind Kind, string Message)>();
        find.Status.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(PageStatusHolder.Current))
            {
                seen.Add((find.Status.Current.Kind, find.Status.Current.Message));
            }
        };

        find.StopCommand.Execute(null);
        Assert.True(token.IsCancellationRequested);
        await search;

        // "Stopping…" first, then how it ended: attention with Refresh.
        Assert.Equal((PageStatusKind.Working, "Stopping…"), seen[0]);
        var stopped = find.Status.Current;
        Assert.Equal(PageStatusKind.Attention, stopped.Kind);
        Assert.StartsWith("Reading “Find results” was stopped before all results arrived", stopped.Message, StringComparison.Ordinal);
        Assert.Equal("Refresh", stopped.Action!.Label);
        Assert.Same(find.Results.RefreshCommand, stopped.Action.Command);
        Assert.False(find.Status.IsWorking);
        Assert.False(find.Status.IsCancelling);
        Assert.True(find.FindNowCommand.CanExecute(null));

        // The next search runs to its end: the summary of the results.
        harness.Session.Latency = TimeSpan.Zero;
        await find.FindNowCommand.ExecuteAsync(null);
        Assert.Contains(find.Results.Items, row => row.Name == "Jan Kowalski");
        Assert.Equal(PageStatusKind.Idle, find.Status.Current.Kind);
        Assert.Equal(find.Results.StatusText, find.Status.Current.Message);
        Assert.StartsWith($"{find.Results.Items.Count} objects", find.Status.Current.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AFailedSearch_IsAnErrorWithRetry()
    {
        var harness = await B2Harness.CreateAsync();
        using var find = harness.Factory.CreateFind(harness.Company.EmployeesOu);
        find.NameText = "kowal";
        harness.Session.FailNext(FakeOperationKind.Search, null, DirectoryErrorKind.ServerUnavailable);

        await find.FindNowCommand.ExecuteAsync(null);

        var failed = find.Status.Current;
        Assert.Equal(PageStatusKind.Error, failed.Kind);
        Assert.Equal(
            "Could not read “Find results”: The domain controller is not available. Press Retry to try again. (LDAP 52)",
            failed.Message);
        Assert.False(failed.CanClose);
        Assert.Equal("Retry", failed.Action!.Label);
        Assert.Empty(find.Results.Items);

        // Retry is the refresh of the results.
        Assert.Same(find.Results.RefreshCommand, failed.Action.Command);
        await find.Results.RefreshCommand.ExecuteAsync(null);
        Assert.Contains(find.Results.Items, row => row.Name == "Jan Kowalski");
        Assert.True(find.Status.Current.IsIdle);
    }

    [Fact]
    public async Task WhenTheSessionOfTheWindowEnded_TheLineSaysSo_AndNothingReplacesIt()
    {
        var harness = await B2Harness.CreateAsync();
        using var find = harness.Factory.CreateFind(null);
        find.NameText = "lukasz";
        await find.FindNowCommand.ExecuteAsync(null);
        Assert.True(find.Status.Current.IsIdle);
        var other = new FakeDirectoryBuilder(timeProvider: harness.Time);
        other.AddSampleCompany();

        await harness.ReconnectAsync(other.Session);

        var ended = find.Status.Current;
        Assert.Equal(PageStatusKind.Error, ended.Kind);
        Assert.Equal(harness.Localizer[L.WindowSession.Ended], ended.Message);
        Assert.Equal(find.SessionEndedMessage, ended.Message);
        Assert.Equal(L.WindowSession.Ended, ended.Log.Key);
        Assert.False(ended.CanClose);
        Assert.Null(ended.Action);
        Assert.False(find.Status.IsWorking);
        Assert.False(find.FindNowCommand.CanExecute(null));

        // The results list of the ended session says nothing any more, whatever is asked of it.
        find.Status.CloseCommand.Execute(null);
        await find.Results.RefreshCommand.ExecuteAsync(null);
        find.Results.SelectedItem = null;
        Assert.Equal(PageStatusKind.Error, find.Status.Current.Kind);
        Assert.Equal(harness.Localizer[L.WindowSession.Ended], find.Status.Current.Message);
        Assert.False(find.Status.IsWorking);

        // In the language of the moment.
        harness.Localizer.SetCulture(CultureInfo.GetCultureInfo("pl-PL"));
        Assert.Equal(harness.Localizer[L.WindowSession.Ended], find.Status.Current.Message);
        Assert.StartsWith("Sesja", find.Status.Current.Message, StringComparison.Ordinal);
        Assert.False(find.Status.Current.CanClose);
    }

    [Fact]
    public async Task ASearchStoppedByTheEndOfTheSession_LeavesTheSessionMessage()
    {
        var harness = await B2Harness.CreateAsync();
        using var find = harness.Factory.CreateFind(null);
        find.NameText = "lukasz";
        await harness.Sessions.GetDomainPolicyAsync(TestContext.Current.CancellationToken);
        harness.Session.Latency = TimeSpan.FromSeconds(30);
        var search = find.FindNowCommand.ExecuteAsync(null);
        Assert.True(find.Status.IsWorking);
        var other = new FakeDirectoryBuilder(timeProvider: harness.Time);
        other.AddSampleCompany();

        await harness.ReconnectAsync(other.Session);
        await search;

        Assert.Equal(PageStatusKind.Error, find.Status.Current.Kind);
        Assert.Equal(harness.Localizer[L.WindowSession.Ended], find.Status.Current.Message);
        Assert.False(find.Status.IsWorking);
        Assert.False(find.Status.IsCancelling);
        Assert.Null(find.Status.Operation);
    }
}
