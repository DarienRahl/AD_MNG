using System.ComponentModel;
using System.Globalization;
using AdMng.Core.ActiveDirectory;
using AdMng.Core.Directory;
using AdMng.Presentation.Comparison;
using AdMng.Presentation.Feedback;
using AdMng.Presentation.Localization;
using AdMng.Presentation.Tests.Dialogs;
using AdMng.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace AdMng.Presentation.Tests.Comparison;

/// <summary>
/// The status line of "Compare with an object in another connection" (track V, stage V2b;
/// docs/dev/v-interface-renewal.md "V2b-1 as built"): the comparison is the operation of the window, the reads of
/// nested membership and nested members that follow it are operations as well, one at a time; at rest the line says
/// which object was found; why the tabs are empty - no match, several candidates, a read that failed, the session that
/// ended - is the state of the window for as long as it lasts. What is compared is covered, unchanged, by
/// <see cref="ObjectComparisonViewModelTests"/>.
/// </summary>
public sealed class ObjectComparisonStatusTests
{
    private const string OtherConnection = "fabrikam.test · FABRIKAM\\anna.zielinska";

    /// <summary>A domain controller that does not answer within a test.</summary>
    private static readonly TimeSpan SlowDomainController = TimeSpan.FromMinutes(5);

    /// <summary>How long a held read waits for the test at most (a failing test must not keep a pool thread for good).</summary>
    private static readonly TimeSpan HoldLimit = TimeSpan.FromSeconds(60);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("en-US", "Comparing “Łukasz Wiśniewski” with the connection " + OtherConnection + "…", "Reading nested membership…", "Found: ")]
    [InlineData("pl-PL", "Porównywanie obiektu „Łukasz Wiśniewski” z połączeniem " + OtherConnection + "…", "Odczytywanie członkostwa zagnieżdżonego…", "Znaleziono: ")]
    public async Task AComparison_IsTheOperationOfTheWindow_ThenTheNestedMembership_ThenWhichObjectWasFound(
        string culture, string comparing, string nested, string found)
    {
        var domains = await ComparisonDomains.CreateAsync();
        var (host, a, _) = await domains.OpenAsync();
        await using var _ = host;
        a.Get<ILocalizer>().SetCulture(CultureInfo.GetCultureInfo(culture));
        try
        {
            using var comparison = a.Compare(domains.Contoso.Company.LukaszWisniewski);
            Assert.IsAssignableFrom<IHasPageStatus>(comparison);
            var seen = Record(comparison.Status);

            await comparison.RunAsync();

            // Working from the first moment: the comparison, then - an operation of its own - the read that follows it
            // in the background; the result is the content of the tabs, and the line says which object was found.
            Assert.Equal((PageStatusKind.Working, comparing), seen[0]);
            Assert.Contains((PageStatusKind.Working, nested), seen);
            Assert.All(seen.Take(seen.Count - 1), s => Assert.Equal(PageStatusKind.Working, s.Kind));
            var summary = comparison.Status.Current;
            Assert.Equal(PageStatusKind.Idle, summary.Kind);
            Assert.Equal(found + domains.FabrikamDomain.Company.LukaszWisniewski, summary.Message);
            Assert.Equal(comparison.StatusText, summary.Message);
            Assert.Equal(seen[^1], (summary.Kind, summary.Message));
            Assert.True(comparison.HasResult);
            Assert.False(comparison.IsRunning);
            Assert.False(comparison.IsNestedLoading);
            Assert.False(comparison.Status.IsWorking);
            Assert.Null(comparison.Status.Lasting);
            Assert.Null(comparison.ErrorText);
        }
        finally
        {
            a.Get<ILocalizer>().SetCulture(CultureInfo.InvariantCulture);
        }
    }

    [Fact]
    public async Task BeforeAComparison_TheLineSaysWhatToChoose_OrNothingWithoutAnotherConnection()
    {
        var domains = await ComparisonDomains.CreateAsync();
        var (host, a, b) = await domains.OpenAsync();
        await using var _ = host;
        await using var c = host.CreateConnection();
        await c.ConnectAsync(ComparisonDomains.Fabrikam, "piotr.nowak");

        // Two other connections: none is preselected, nothing is compared yet.
        using var comparison = a.Compare(domains.Contoso.Company.LukaszWisniewski);
        await comparison.InitializeAsync();
        Assert.Null(comparison.SelectedTarget);
        Assert.Equal(PageStatusKind.Idle, comparison.Status.Current.Kind);
        Assert.Equal("Choose the other connection", comparison.Status.Current.Message);
        Assert.False(comparison.Status.IsWorking);

        // One is chosen: the hint goes; after the comparison the line says which object was found.
        comparison.SelectedTarget = comparison.Targets.Single(t => t.Id == b.Context.Id);
        Assert.Equal(string.Empty, comparison.Status.Current.Message);
        await comparison.CompareCommand.ExecuteAsync(null);
        await comparison.Pending;
        Assert.Equal("Found: " + domains.FabrikamDomain.Company.LukaszWisniewski, comparison.Status.Current.Message);

        // No other connection at all: that is a note next to the connection box, not something the line says.
        await using var lonelyHost = new Connections.ConnectionScopeHost(domains: [domains.Contoso.Session, domains.FabrikamDomain.Session]);
        await using var lonely = lonelyHost.CreateConnection();
        await lonely.ConnectAsync(user: "jan.kowalski");
        using var alone = lonely.Compare(domains.Contoso.Company.LukaszWisniewski);
        await alone.InitializeAsync();
        Assert.NotNull(alone.NoTargetsMessage);
        Assert.Equal(PageStatusKind.Idle, alone.Status.Current.Kind);
        Assert.Equal(string.Empty, alone.Status.Current.Message);
    }

    [Fact]
    public async Task Stop_SaysStoppingAtOnce_AndTheStoppedComparisonIsAnAttention_WithCompareAsItsAction()
    {
        var domains = await ComparisonDomains.CreateAsync();
        var (host, a, b) = await domains.OpenAsync();
        await using var _ = host;
        await b.Sessions.GetDomainPolicyAsync(Ct);
        var fabrikam = domains.FabrikamDomain.Session;
        using var comparison = a.Compare(domains.Contoso.Company.LukaszWisniewski);
        var asked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fabrikam.BeforeOperation = _ => asked.TrySetResult();
        fabrikam.Latency = SlowDomainController;
        List<(PageStatusKind Kind, string Message)> seen;
        try
        {
            // The window compares at once (the only other connection): the other domain controller does not answer.
            var run = comparison.InitializeAsync();
            await asked.Task.WaitAsync(TimeSpan.FromSeconds(20), Ct);
            Assert.True(comparison.IsRunning);
            Assert.True(comparison.Status.IsWorking);
            Assert.Equal("Comparison.Status.Comparing", comparison.Status.Current.Log.ToString());
            var stop = Assert.IsType<PageStatusAction>(comparison.Status.Current.Action);
            Assert.Equal("Stop", stop.Label);
            Assert.Same(comparison.Status.CancelCommand, stop.Command);

            // Pressed: "Stopping…" at once, and the comparison ends well before the domain controller would have answered.
            seen = Record(comparison.Status);
            stop.Command.Execute(stop.Parameter);
            Assert.True(comparison.Status.IsCancelling);
            await run.WaitAsync(TimeSpan.FromSeconds(20), Ct);
            await comparison.Pending.WaitAsync(TimeSpan.FromSeconds(20), Ct);
        }
        finally
        {
            fabrikam.Latency = TimeSpan.Zero;
            fabrikam.BeforeOperation = null;
        }

        // Stopped: what the tabs may lack, and "Compare" as the way to compare again - a result that can be closed.
        Assert.Equal((PageStatusKind.Working, "Stopping…"), seen[0]);
        var stopped = comparison.Status.Current;
        Assert.Equal(PageStatusKind.Attention, stopped.Kind);
        Assert.Equal("Comparing “Łukasz Wiśniewski” was stopped: the tabs may not show everything. Press “Compare” to compare again.", stopped.Message);
        Assert.Equal("Comparison.Status.Stopped", stopped.Log.ToString());
        Assert.True(stopped.CanClose);
        Assert.False(comparison.IsRunning);
        Assert.False(comparison.Status.IsWorking);
        Assert.Null(comparison.ErrorText);
        Assert.Null(comparison.Status.Lasting);
        Assert.Equal("Stopped", comparison.StatusText);
        var again = Assert.IsType<PageStatusAction>(stopped.Action);
        Assert.Equal("Compare", again.Label);
        Assert.Same(comparison.CompareCommand, again.Command);

        // "Compare" compares: the summary, and the "stopped" is gone with the comparison.
        await comparison.CompareCommand.ExecuteAsync(null);
        await comparison.Pending;
        Assert.True(comparison.HasResult);
        Assert.Equal(PageStatusKind.Idle, comparison.Status.Current.Kind);
        Assert.Equal("Found: " + domains.FabrikamDomain.Company.LukaszWisniewski, comparison.Status.Current.Message);
    }

    [Fact]
    public async Task InPolish_TheStoppedAndTheFailedComparison_AreSaidInPolish()
    {
        var domains = await ComparisonDomains.CreateAsync();
        var (host, a, b) = await domains.OpenAsync();
        await using var _ = host;
        await b.Sessions.GetDomainPolicyAsync(Ct);
        var fabrikam = domains.FabrikamDomain.Session;
        a.Get<ILocalizer>().SetCulture(CultureInfo.GetCultureInfo("pl-PL"));
        try
        {
            using var comparison = a.Compare(domains.Contoso.Company.LukaszWisniewski);
            var asked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            fabrikam.BeforeOperation = _ => asked.TrySetResult();
            fabrikam.Latency = SlowDomainController;
            try
            {
                var run = comparison.InitializeAsync();
                await asked.Task.WaitAsync(TimeSpan.FromSeconds(20), Ct);
                Assert.Equal("Zatrzymaj", comparison.Status.Current.Action?.Label);
                comparison.Status.CancelCommand.Execute(null);
                Assert.Equal("Zatrzymywanie…", comparison.Status.Current.Message);
                await run.WaitAsync(TimeSpan.FromSeconds(20), Ct);
                await comparison.Pending.WaitAsync(TimeSpan.FromSeconds(20), Ct);
            }
            finally
            {
                fabrikam.Latency = TimeSpan.Zero;
                fabrikam.BeforeOperation = null;
            }

            var stopped = comparison.Status.Current;
            Assert.Equal(PageStatusKind.Attention, stopped.Kind);
            Assert.Equal(
                "Porównywanie obiektu „Łukasz Wiśniewski” zatrzymano: karty mogą nie pokazywać wszystkiego. Wybierz „Porównaj”, aby porównać ponownie.",
                stopped.Message);
            Assert.Equal("Porównaj", stopped.Action?.Label);

            fabrikam.FailNext(FakeOperationKind.Get, domains.FabrikamDomain.Company.LukaszWisniewski, DirectoryErrorKind.Timeout);
            await comparison.CompareCommand.ExecuteAsync(null);
            await comparison.Pending;
            var failed = comparison.Status.Current;
            Assert.Equal(PageStatusKind.Error, failed.Kind);
            Assert.NotNull(comparison.ErrorText);
            Assert.StartsWith(
                $"Nie udało się porównać obiektu „Łukasz Wiśniewski”: {comparison.ErrorText} Wybierz Ponów, aby spróbować ponownie. (",
                failed.Message,
                StringComparison.Ordinal);
            Assert.Equal("Ponów", failed.Action?.Label);
        }
        finally
        {
            a.Get<ILocalizer>().SetCulture(CultureInfo.InvariantCulture);
        }
    }

    [Fact]
    public async Task TheCancelOfTheCompareCommand_IsTheOperatorsStopAsWell()
    {
        var domains = await ComparisonDomains.CreateAsync();
        var (host, a, b) = await domains.OpenAsync();
        await using var _ = host;
        await b.Sessions.GetDomainPolicyAsync(Ct);
        var fabrikam = domains.FabrikamDomain.Session;
        using var comparison = await a.Compare(domains.Contoso.Company.LukaszWisniewski).RunAsync();
        var asked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fabrikam.BeforeOperation = _ => asked.TrySetResult();
        fabrikam.Latency = SlowDomainController;
        try
        {
            var run = comparison.CompareCommand.ExecuteAsync(null);
            await asked.Task.WaitAsync(TimeSpan.FromSeconds(20), Ct);
            Assert.True(comparison.Status.IsWorking);

            comparison.CompareCancelCommand.Execute(null);
            Assert.True(comparison.Status.IsCancelling);
            Assert.Equal("Stopping…", comparison.Status.Current.Message);
            await run.WaitAsync(TimeSpan.FromSeconds(20), Ct);
            await comparison.Pending.WaitAsync(TimeSpan.FromSeconds(20), Ct);
        }
        finally
        {
            fabrikam.Latency = TimeSpan.Zero;
            fabrikam.BeforeOperation = null;
        }

        Assert.Equal(PageStatusKind.Attention, comparison.Status.Current.Kind);
        Assert.Equal("Comparison.Status.Stopped", comparison.Status.Current.Log.ToString());
        Assert.False(comparison.IsRunning);
    }

    [Fact]
    public async Task AComparisonThatFails_IsTheStateOfTheWindow_WithRetry_UntilTheNextComparison()
    {
        var domains = await ComparisonDomains.CreateAsync();
        var (host, a, _) = await domains.OpenAsync();
        await using var _ = host;
        using var comparison = a.Compare(domains.Contoso.Company.LukaszWisniewski);
        domains.FabrikamDomain.Session.FailNext(FakeOperationKind.Get, domains.FabrikamDomain.Company.LukaszWisniewski, DirectoryErrorKind.Timeout);

        await comparison.RunAsync();

        // The tabs are empty and the line says why - for as long as that is so; it cannot be closed.
        var failed = comparison.Status.Current;
        Assert.Equal(PageStatusKind.Error, failed.Kind);
        Assert.Same(comparison.Status.Lasting, failed);
        Assert.NotNull(comparison.ErrorText);
        Assert.StartsWith($"Could not compare “Łukasz Wiśniewski”: {comparison.ErrorText} Press Retry to try again. (", failed.Message, StringComparison.Ordinal);
        Assert.StartsWith("Comparison.Status.Failed [", failed.Log.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("Wiśniewski", failed.Log.ToString(), StringComparison.Ordinal);
        Assert.False(failed.CanClose);
        Assert.False(comparison.HasResult);
        Assert.False(comparison.IsRunning);
        var retry = Assert.IsType<PageStatusAction>(failed.Action);
        Assert.Equal("Retry", retry.Label);
        Assert.Same(comparison.RefreshCommand, retry.Command);

        // Retry compares again: the state is over.
        await comparison.RefreshCommand.ExecuteAsync(null);
        await comparison.Pending;
        Assert.Null(comparison.Status.Lasting);
        Assert.Null(comparison.ErrorText);
        Assert.True(comparison.HasResult);
        Assert.Equal("Found: " + domains.FabrikamDomain.Company.LukaszWisniewski, comparison.Status.Current.Message);
    }

    [Fact]
    public async Task NoMatch_AndSeveralCandidates_AreTheStateOfTheWindow_UntilSomethingIsCompared()
    {
        var domains = await ComparisonDomains.CreateAsync(fabrikamExtra: b =>
            b.AddUser("Łukasz", "Wiśniewski", b.AddOu("Oddział Gdańsk"), new Dictionary<string, object?> { [Attr.SamAccountName] = "lwisniewski2" }));
        var (host, a, _) = await domains.OpenAsync();
        await using var _ = host;
        using var comparison = a.Compare(domains.Contoso.Company.LukaszWisniewski);
        await comparison.RunAsync();
        Assert.True(comparison.HasResult);

        // Several objects match by name: the tabs are empty, and the line says what to do - not to be closed.
        comparison.SelectedRule = comparison.MatchRules.Single(r => r.Value == ObjectMatchRule.Name);
        await comparison.CompareCommand.ExecuteAsync(null);
        await comparison.Pending;
        var ambiguous = comparison.Status.Current;
        Assert.Equal(PageStatusKind.Attention, ambiguous.Kind);
        Assert.Equal("Found 2 objects – choose one.", ambiguous.Message);
        Assert.Equal("Found 2 objects – choose one", comparison.StatusText);
        Assert.Equal("Comparison.StatusAmbiguous", ambiguous.Log.ToString());
        Assert.Same(comparison.Status.Lasting, ambiguous);
        Assert.False(ambiguous.CanClose);
        Assert.Null(ambiguous.Action);
        Assert.False(comparison.HasResult);
        Assert.Equal(2, comparison.Candidates.Count);

        // One is chosen: compared, and the state is over.
        comparison.SelectedCandidate = comparison.Candidates.Single(c => c.Path.Contains("Gdańsk", StringComparison.Ordinal));
        await comparison.Pending;

        // (The run started the read of the nested membership when it ended: wait for that one as well.)
        await comparison.Pending;
        Assert.Null(comparison.Status.Lasting);
        Assert.Equal(PageStatusKind.Idle, comparison.Status.Current.Kind);
        Assert.StartsWith("Found: CN=Łukasz Wiśniewski,OU=Oddział Gdańsk,", comparison.Status.Current.Message, StringComparison.Ordinal);

        // Nothing matches the typed object: said the same way.
        comparison.SelectedRule = comparison.MatchRules.Single(r => r.Value == ObjectMatchRule.Explicit);
        comparison.ExplicitText = "nikt-taki-nie-istnieje";
        await comparison.CompareCommand.ExecuteAsync(null);
        await comparison.Pending;
        var none = comparison.Status.Current;
        Assert.Equal(PageStatusKind.Attention, none.Kind);
        Assert.Equal("Not found.", none.Message);
        Assert.Equal("Comparison.StatusNotFoundExplicit", none.Log.ToString());
        Assert.False(none.CanClose);
        Assert.False(comparison.HasResult);
    }

    [Fact]
    public async Task AnotherConnection_TakesTheChoiceAmongCandidatesOffTheLine()
    {
        var domains = await ComparisonDomains.CreateAsync(fabrikamExtra: b =>
            b.AddUser("Łukasz", "Wiśniewski", b.AddOu("Oddział Gdańsk"), new Dictionary<string, object?> { [Attr.SamAccountName] = "lwisniewski2" }));
        var (host, a, b) = await domains.OpenAsync();
        await using var _ = host;
        await using var c = host.CreateConnection();
        await c.ConnectAsync(ComparisonDomains.Fabrikam, "piotr.nowak");
        using var comparison = a.Compare(domains.Contoso.Company.LukaszWisniewski);
        await comparison.InitializeAsync();
        comparison.SelectedTarget = comparison.Targets.Single(t => t.Id == b.Context.Id);
        comparison.SelectedRule = comparison.MatchRules.Single(r => r.Value == ObjectMatchRule.Name);
        await comparison.CompareCommand.ExecuteAsync(null);
        await comparison.Pending;
        Assert.Equal(2, comparison.Candidates.Count);
        Assert.Equal("Found 2 objects – choose one.", comparison.Status.Current.Message);
        Assert.NotNull(comparison.Status.Lasting);

        // The candidates were found in the connection that is no longer chosen: they go, and "choose one" with them.
        comparison.SelectedTarget = comparison.Targets.Single(t => t.Id == c.Context.Id);
        Assert.Empty(comparison.Candidates);
        Assert.Null(comparison.Status.Lasting);
        Assert.Equal(PageStatusKind.Idle, comparison.Status.Current.Kind);
        Assert.Equal(string.Empty, comparison.Status.Current.Message);
    }

    [Fact]
    public async Task TheNestedMembership_IsAnOperationOfItsOwn_AndStopEndsItAsAttentionWithRefresh()
    {
        var domains = await ComparisonDomains.CreateAsync();
        var (host, a, _) = await domains.OpenAsync();
        await using var _ = host;
        var fabrikam = domains.FabrikamDomain.Session;
        using var comparison = a.Compare(domains.Contoso.Company.LukaszWisniewski);

        // The read of the nested membership in the other connection waits for the test - and, once it was stopped,
        // answers like a cancelled request.
        using var held = new SemaphoreSlim(0);
        using var release = new ManualResetEventSlim();
        var hold = true;
        fabrikam.BeforeOperation = operation =>
        {
            if (hold && operation.Kind == FakeOperationKind.Search && operation.Search!.Filter.Contains("1.2.840.113556.1.4.1941", StringComparison.Ordinal))
            {
                hold = false;
                held.Release();
                release.Wait(HoldLimit);
                throw new OperationCanceledException();
            }
        };
        List<(PageStatusKind Kind, string Message)> seen;
        try
        {
            await comparison.InitializeAsync();
            Assert.True(await held.WaitAsync(TimeSpan.FromSeconds(20), Ct));

            // The comparison is over - the tabs are filled, "Compare" may be pressed again - and the read that follows
            // it is the operation of the window, with Stop.
            Assert.True(comparison.HasResult);
            Assert.False(comparison.IsRunning);
            Assert.True(comparison.IsNestedLoading);
            Assert.True(comparison.Status.IsWorking);
            Assert.Equal(PageStatusKind.Working, comparison.Status.Current.Kind);
            Assert.Equal("Reading nested membership…", comparison.Status.Current.Message);
            Assert.Equal("Comparison.NestedLoading", comparison.Status.Current.Log.ToString());
            Assert.True(comparison.CompareCommand.CanExecute(null));

            seen = Record(comparison.Status);
            comparison.Status.CancelCommand.Execute(null);
            Assert.Equal("Stopping…", comparison.Status.Current.Message);
        }
        finally
        {
            release.Set();
        }

        await comparison.Pending.WaitAsync(TimeSpan.FromSeconds(20), Ct);
        fabrikam.BeforeOperation = null;

        // Stopped: what is compared, and Refresh as the way to read everything again.
        Assert.Equal((PageStatusKind.Working, "Stopping…"), seen[0]);
        var stopped = comparison.Status.Current;
        Assert.Equal(PageStatusKind.Attention, stopped.Kind);
        Assert.Equal("Reading nested membership was stopped: only direct membership is compared. Press “Refresh” to read everything again.", stopped.Message);
        Assert.Equal("Comparison.Status.NestedStopped", stopped.Log.ToString());
        Assert.True(stopped.CanClose);
        Assert.False(comparison.IsNestedLoading);
        Assert.True(comparison.HasResult);
        var refresh = Assert.IsType<PageStatusAction>(stopped.Action);
        Assert.Equal("Refresh", refresh.Label);
        Assert.Same(comparison.RefreshCommand, refresh.Command);

        // Closed: which object was found.
        comparison.Status.CloseCommand.Execute(null);
        Assert.Equal("Found: " + domains.FabrikamDomain.Company.LukaszWisniewski, comparison.Status.Current.Message);

        // Refresh reads everything, the nested membership too.
        await comparison.RefreshCommand.ExecuteAsync(null);
        await comparison.Pending;
        Assert.Equal(PageStatusKind.Idle, comparison.Status.Current.Kind);
        comparison.OnlyMembershipDifferences = false;
        Assert.Contains(comparison.MembershipRows, r => r is { Name: "DL_Wszyscy", KindTextA: "nested" });
    }

    [Fact]
    public async Task ANestedMembershipThatCannotBeRead_IsAnErrorThatCanBeClosed_AndTheDirectMembershipStays()
    {
        var domains = await ComparisonDomains.CreateAsync();
        var (host, a, _) = await domains.OpenAsync();
        await using var _ = host;
        var fabrikam = domains.FabrikamDomain.Session;
        using var comparison = a.Compare(domains.Contoso.Company.LukaszWisniewski);
        var fail = true;
        fabrikam.BeforeOperation = operation =>
        {
            if (fail && operation.Kind == FakeOperationKind.Search && operation.Search!.Filter.Contains("1.2.840.113556.1.4.1941", StringComparison.Ordinal))
            {
                throw new DirectoryException(DirectoryErrorKind.Timeout, "fabrikam does not answer in time");
            }
        };

        await comparison.RunAsync();
        fail = false;
        fabrikam.BeforeOperation = null;

        var failed = comparison.Status.Current;
        Assert.Equal(PageStatusKind.Error, failed.Kind);
        Assert.StartsWith("Could not read nested membership: ", failed.Message, StringComparison.Ordinal);
        Assert.EndsWith(" Only direct membership is compared. (Timeout)", failed.Message, StringComparison.Ordinal);
        Assert.Equal("Comparison.Status.NestedFailed [Timeout]", failed.Log.ToString());
        Assert.True(failed.CanClose);
        Assert.Null(comparison.Status.Lasting);
        Assert.Same(comparison.RefreshCommand, Assert.IsType<PageStatusAction>(failed.Action).Command);
        Assert.True(comparison.HasResult);
        Assert.False(comparison.IsNestedLoading);
        Assert.Contains(comparison.MembershipRows, r => r.Name == "GG_IT");
    }

    [Fact]
    public async Task TheNestedMembers_WaitForTheNestedMembership_AndAreAnOperationOfTheirOwn()
    {
        var domains = await ComparisonDomains.CreateAsync();
        var (host, a, _) = await domains.OpenAsync();
        await using var _ = host;
        using var comparison = await a.Compare(domains.Contoso.Company.GroupAll).RunAsync();
        Assert.True(comparison.IsGroup);
        var seen = Record(comparison.Status);

        comparison.ShowNestedMembers = true;
        await comparison.Pending;

        Assert.Equal((PageStatusKind.Working, "Reading nested members…"), seen[0]);
        Assert.All(seen.Take(seen.Count - 1), s => Assert.Equal(PageStatusKind.Working, s.Kind));
        Assert.Equal(PageStatusKind.Idle, comparison.Status.Current.Kind);
        Assert.False(comparison.IsNestedLoading);
        Assert.False(comparison.Status.IsWorking);
    }

    [Fact]
    public async Task NestedMembers_AskedForWhileAComparisonRuns_DoNotTakeTheLine_AndAreReadWhenItIsThrough()
    {
        var domains = await ComparisonDomains.CreateAsync();
        var (host, a, _) = await domains.OpenAsync();
        await using var _ = host;
        var fabrikam = domains.FabrikamDomain.Session;
        using var comparison = a.Compare(domains.Contoso.Company.GroupAll);

        // The comparison has both objects (the tabs are shown) and reads the members of the group in the other
        // connection: that read waits for the test.
        var shown = 0;
        comparison.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ObjectComparisonViewModel.HasResult) && comparison.HasResult)
            {
                Volatile.Write(ref shown, 1);
            }
        };
        using var held = new SemaphoreSlim(0);
        using var release = new ManualResetEventSlim();
        var hold = 1;
        fabrikam.BeforeOperation = operation =>
        {
            if (Volatile.Read(ref shown) == 1
                && operation.Kind == FakeOperationKind.Search
                && operation.Search!.Filter.Contains("(distinguishedName=", StringComparison.OrdinalIgnoreCase)
                && Interlocked.Exchange(ref hold, 0) == 1)
            {
                held.Release();
                release.Wait(HoldLimit);
            }
        };
        List<(PageStatusKind Kind, string Message)> seen;
        Task run;
        try
        {
            run = comparison.InitializeAsync();
            Assert.True(await held.WaitAsync(TimeSpan.FromSeconds(20), Ct));
            Assert.True(comparison.HasResult);
            Assert.True(comparison.IsRunning);
            Assert.Equal("Comparison.Status.Comparing", comparison.Status.Current.Log.ToString());

            // The check box of the "Members" tab is there already. Ticking it must not begin a second operation: that
            // would take the line - and its token - from the comparison.
            seen = Record(comparison.Status);
            comparison.ShowNestedMembers = true;
            Assert.Empty(seen);
            Assert.True(comparison.IsRunning);
            Assert.Equal("Comparison.Status.Comparing", comparison.Status.Current.Log.ToString());
            Assert.False(comparison.Status.IsCancelling);
        }
        finally
        {
            release.Set();
        }

        await run.WaitAsync(TimeSpan.FromSeconds(20), Ct);
        await comparison.Pending.WaitAsync(TimeSpan.FromSeconds(20), Ct);
        fabrikam.BeforeOperation = null;

        // The comparison went through - the members are counted - and then read what was asked for, one read at a time.
        Assert.False(comparison.IsRunning);
        Assert.True(comparison.HasResult);
        Assert.StartsWith("Found: ", comparison.StatusText, StringComparison.Ordinal);
        Assert.StartsWith("Members (", comparison.MembersTabHeader, StringComparison.Ordinal);
        Assert.Equal(
            [(PageStatusKind.Working, "Reading nested membership…"), (PageStatusKind.Working, "Reading nested members…")],
            seen.Where(s => s.Kind == PageStatusKind.Working).Distinct());
        Assert.Equal(PageStatusKind.Idle, comparison.Status.Current.Kind);
        Assert.False(comparison.IsNestedLoading);
    }

    [Fact]
    public async Task TheEndOfTheSession_DuringAComparison_EndsIt_AndCompareIsNotLeftAsTheCancelOfNothing()
    {
        var domains = await ComparisonDomains.CreateAsync();
        var (host, a, b) = await domains.OpenAsync();
        await using var _ = host;
        await b.Sessions.GetDomainPolicyAsync(Ct);
        var fabrikam = domains.FabrikamDomain.Session;
        using var comparison = a.Compare(domains.Contoso.Company.LukaszWisniewski);
        var asked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fabrikam.BeforeOperation = _ => asked.TrySetResult();
        fabrikam.Latency = SlowDomainController;
        try
        {
            var run = comparison.InitializeAsync();
            await asked.Task.WaitAsync(TimeSpan.FromSeconds(20), Ct);
            Assert.True(comparison.IsRunning);
            Assert.True(comparison.Status.IsWorking);

            // The session of the window ends while the other domain controller has not answered.
            await a.Sessions.DisconnectAsync();
            Assert.True(comparison.IsSessionEnded);

            // At once: nothing runs any more - "Compare" is not the Cancel of a comparison that is over - and the
            // line says, for good, that the session ended.
            Assert.False(comparison.IsRunning);
            Assert.False(comparison.IsNestedLoading);
            Assert.False(comparison.Status.IsWorking);
            Assert.False(comparison.Status.IsCancelling);
            var ended = comparison.Status.Current;
            Assert.Equal(PageStatusKind.Error, ended.Kind);
            Assert.Equal(comparison.SessionEndedMessage, ended.Message);
            Assert.False(ended.CanClose);

            await run.WaitAsync(TimeSpan.FromSeconds(20), Ct);
            await comparison.Pending.WaitAsync(TimeSpan.FromSeconds(20), Ct);
        }
        finally
        {
            fabrikam.Latency = TimeSpan.Zero;
            fabrikam.BeforeOperation = null;
        }

        // The cancelled comparison said nothing when it returned.
        Assert.False(comparison.IsRunning);
        Assert.Equal(PageStatusKind.Error, comparison.Status.Current.Kind);
        Assert.Equal(comparison.SessionEndedMessage, comparison.Status.Current.Message);
        Assert.Equal("WindowSession.Ended", comparison.Status.Current.Log.ToString());
        Assert.False(comparison.HasResult);
        Assert.False(comparison.CompareCommand.CanExecute(null));
    }

    [Fact]
    public async Task TheEndOfTheSession_IsTheStateOfTheWindow_AndSideAHasNoNoteOfItsOwn()
    {
        var domains = await ComparisonDomains.CreateAsync();
        var (host, a, _) = await domains.OpenAsync();
        await using var _ = host;
        using var comparison = await a.Compare(domains.Contoso.Company.LukaszWisniewski).RunAsync();
        Assert.Equal(PageStatusKind.Idle, comparison.Status.Current.Kind);

        await a.Sessions.DisconnectAsync();

        // An error that cannot be closed: said once, by the line - the results stay, nothing is read any more.
        Assert.True(comparison.IsSessionEnded);
        var ended = comparison.Status.Current;
        Assert.Equal(PageStatusKind.Error, ended.Kind);
        Assert.Equal(comparison.SessionEndedMessage, ended.Message);
        Assert.Equal("WindowSession.Ended", ended.Log.ToString());
        Assert.Same(comparison.Status.Lasting, ended);
        Assert.False(ended.CanClose);
        Assert.Null(ended.Action);
        Assert.True(comparison.SideA.IsEnded);
        Assert.Null(comparison.SideA.EndedMessage);
        Assert.True(comparison.HasResult);
        Assert.False(comparison.CompareCommand.CanExecute(null));
        Assert.False(comparison.RefreshCommand.CanExecute(null));
    }

    [Fact]
    public async Task TheOtherConnectionClosing_IsANoteOfSideB_NotAStatus()
    {
        var domains = await ComparisonDomains.CreateAsync();
        var (host, a, b) = await domains.OpenAsync();
        await using var _ = host;
        using var comparison = await a.Compare(domains.Contoso.Company.LukaszWisniewski).RunAsync();
        var summary = comparison.Status.Current;

        await b.DisposeAsync();
        await B2Harness.WaitUntilAsync(() => comparison.SideB.IsEnded);

        // The results stay and the line keeps saying which object was found; that side B is closed is its note.
        Assert.Equal("The connection " + OtherConnection + " was closed – its results stay visible.", comparison.SideB.EndedMessage);
        Assert.Same(summary, comparison.Status.Current);
        Assert.Null(comparison.Status.Lasting);
        Assert.False(comparison.Status.IsWorking);
    }

    [Fact]
    public async Task TheLogCopies_CarryKeysAndCodes_NeverANameAConnectionOrADistinguishedName()
    {
        var logger = new ListLogger<PageStatusHolder>();
        var domains = await ComparisonDomains.CreateAsync();
        var (host, a, _) = await domains.OpenAsync(s => s.AddSingleton<ILogger<PageStatusHolder>>(logger));
        await using var _ = host;
        using var comparison = await a.Compare(domains.Contoso.Company.LukaszWisniewski).RunAsync();
        Assert.Contains("CN=Łukasz", comparison.Status.Current.Message, StringComparison.Ordinal);
        domains.FabrikamDomain.Session.FailNext(FakeOperationKind.Get, domains.FabrikamDomain.Company.LukaszWisniewski, DirectoryErrorKind.Timeout);
        await comparison.RefreshCommand.ExecuteAsync(null);
        await comparison.Pending;
        Assert.Equal(PageStatusKind.Error, comparison.Status.Current.Kind);

        var messages = logger.Entries.Select(entry => entry.Message).ToList();
        Assert.Contains(messages, m => m.Contains("Comparison.Status.Comparing", StringComparison.Ordinal));
        Assert.Contains(messages, m => m.Contains("Comparison.NestedLoading", StringComparison.Ordinal));
        Assert.Contains(messages, m => m.Contains("Comparison.Status.Failed", StringComparison.Ordinal));
        Assert.DoesNotContain("Wiśniewski", logger.AllText, StringComparison.Ordinal);
        Assert.DoesNotContain("fabrikam", logger.AllText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("anna", logger.AllText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("CN=", logger.AllText, StringComparison.OrdinalIgnoreCase);
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
}
