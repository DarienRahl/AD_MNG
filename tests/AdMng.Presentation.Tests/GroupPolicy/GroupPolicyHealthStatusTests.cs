using System.ComponentModel;
using System.Globalization;
using AdMng.Core.Directory;
using AdMng.Core.GroupPolicy;
using AdMng.Presentation.Feedback;
using AdMng.Presentation.GroupPolicy;
using AdMng.Presentation.Tests.Dialogs;
using AdMng.Presentation.Tests.GroupPolicy.Diagnostics;
using AdMng.Testing;

namespace AdMng.Presentation.Tests.GroupPolicy;

/// <summary>
/// What the "Stan" tab of a GPO says (track V, stage V2b-3; docs/dev/v-interface-renewal.md "V2b-3 as built"): the tab
/// has a status of its own, in front of the document's while it is shown, so the one status line of the document says
/// the check of the GPO - with Stop -, when the GPO was checked, a check the operator stopped and, as the state of the
/// tab, a check that failed. A check that is a step of the document's load starts no operation of the tab. What a check
/// could not evaluate is a note above the findings. What the check finds is covered, unchanged, by
/// <see cref="GroupPolicyHealthTabTests"/>.
/// </summary>
public sealed class GroupPolicyHealthStatusTests
{
    private static readonly CultureInfo Polish = CultureInfo.GetCultureInfo("pl-PL");

    /// <summary>How long a held call waits for the test at most (a failing test must not keep a pool thread for good).</summary>
    private static readonly TimeSpan HoldLimit = TimeSpan.FromSeconds(60);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task TheTab_IsInFrontWhileItIsShown_AndSaysWhenTheGpoWasChecked_InBothLanguages()
    {
        var h = await GroupPolicyHarness.CreateAsync();
        using var gpo = await h.GpoAsync(h.Sample.DriveMappings, GpoDocumentTab.Status);

        // The tab is shown: the line of the document says what the tab says - when the GPO was checked, how many findings.
        Assert.Same(gpo.HealthStatus, gpo.Status.Front);
        Assert.False(gpo.IsCheckingHealth);
        Assert.StartsWith("Last check: full, ", gpo.HealthStatusText, StringComparison.Ordinal);
        Assert.Same(gpo.HealthStatus.Current, gpo.Status.Current);
        Assert.Equal((PageStatusKind.Idle, gpo.HealthStatusText), (gpo.Status.Current.Kind, gpo.Status.Current.Message));
        Assert.Null(gpo.HealthStatus.Lasting);
        Assert.False(gpo.Status.CloseCommand.CanExecute(null));

        // Another tab: the summary of the document. Back: the tab again - the findings are current, nothing is checked.
        gpo.SelectedTab = GpoDocumentTab.Scope;
        Assert.Null(gpo.Status.Front);
        Assert.NotEqual(gpo.HealthStatusText, gpo.LoadedText);
        Assert.Equal((PageStatusKind.Idle, gpo.LoadedText), (gpo.Status.Current.Kind, gpo.Status.Current.Message));
        gpo.SelectedTab = GpoDocumentTab.Status;
        Assert.Same(gpo.HealthStatus, gpo.Status.Front);
        Assert.False(gpo.IsCheckingHealth);
        Assert.Equal((PageStatusKind.Idle, gpo.HealthStatusText), (gpo.Status.Current.Kind, gpo.Status.Current.Message));

        // The language: the document reads itself again and, on this tab, checks the GPO again.
        h.B2.Localizer.SetCulture(Polish);
        await B2Harness.WaitUntilAsync(() => !gpo.IsLoading && !gpo.IsCheckingHealth
            && gpo.HealthStatusText.StartsWith("Ostatnie sprawdzenie: pełne, ", StringComparison.Ordinal)
            && gpo.Status.Current.Kind == PageStatusKind.Idle);
        Assert.Equal(gpo.HealthStatusText, gpo.Status.Current.Message);
    }

    [Theory]
    [InlineData("en-US", "Stop", "Stopping…", "Checking the GPO was stopped: the findings were not updated. Press “Check again” to check it again.", "Check again", "Last check: full, ")]
    [InlineData("pl-PL", "Zatrzymaj", "Zatrzymywanie…",
        "Sprawdzanie obiektu GPO zatrzymano: wyników nie zaktualizowano. Wybierz „Sprawdź ponownie”, aby sprawdzić go ponownie.", "Sprawdź ponownie",
        "Ostatnie sprawdzenie: pełne, ")]
    public async Task ACheckTheTabStarts_IsTheOperationOfTheTab_AndStopReturnsControl(
        string culture, string stopLabel, string stopping, string stoppedText, string againLabel, string summaryStart)
    {
        var h = await GroupPolicyHarness.CreateAsync(CultureInfo.GetCultureInfo(culture));
        using var gpo = await h.GpoAsync(h.Sample.DriveMappings);
        Assert.Null(gpo.Status.Front);
        Assert.Equal(string.Empty, gpo.HealthStatusText);
        var said = Record(gpo.HealthStatus);
        using (var held = new HeldCall(h.Session))
        {
            // The tab is selected: it checks the GPO, and its first read waits for the test.
            gpo.SelectedTab = GpoDocumentTab.Status;
            await held.Reached.WaitAsync(HoldLimit, Ct);

            // Working - the operation is the tab's, and so is its Stop; the document itself is not working.
            Assert.True(gpo.IsCheckingHealth);
            Assert.True(gpo.HealthStatus.IsWorking);
            Assert.False(gpo.Status.IsWorking);
            Assert.False(gpo.IsLoading);
            var working = gpo.Status.Current;
            Assert.Same(gpo.HealthStatus.Current, working);
            Assert.Equal(PageStatusKind.Working, working.Kind);
            Assert.StartsWith("GpoDiag.HealthChecking", working.Log.ToString(), StringComparison.Ordinal);
            var stop = Assert.IsType<PageStatusAction>(working.Action);
            Assert.Equal(stopLabel, stop.Label);
            Assert.Same(gpo.HealthStatus.CancelCommand, stop.Command);

            // Stop: "Stopping…" at once - the read that is on its way to the domain controller still has to return.
            stop.Command.Execute(stop.Parameter);
            Assert.True(gpo.HealthStatus.IsCancelling);
            Assert.Equal((PageStatusKind.Working, stopping), (gpo.Status.Current.Kind, gpo.Status.Current.Message));
            Assert.Null(gpo.Status.Current.Action);
        }

        await B2Harness.WaitUntilAsync(() => !gpo.IsCheckingHealth && gpo.Status.Current.Kind == PageStatusKind.Attention);

        // Stopped: a result the operator can close - the tab keeps what it had - with the button's own text as the action.
        var stopped = gpo.Status.Current;
        Assert.Equal(stoppedText, stopped.Message);
        Assert.Equal("GroupPolicy.Status.HealthStopped", stopped.Log.ToString());
        Assert.True(stopped.CanClose);
        Assert.Null(gpo.HealthStatus.Lasting);
        Assert.Null(gpo.Error);
        Assert.Empty(gpo.HealthFindings);
        var again = Assert.IsType<PageStatusAction>(stopped.Action);
        Assert.Equal(againLabel, again.Label);
        Assert.Same(gpo.CheckHealthCommand, again.Command);
        var sequence = Snapshot(said);
        Assert.Equal(PageStatusKind.Working, sequence[0].Kind);
        Assert.Contains((PageStatusKind.Working, stopping), sequence);
        Assert.Equal((PageStatusKind.Attention, stoppedText), sequence[^1]);
        Assert.DoesNotContain(sequence, s => s.Kind is PageStatusKind.Error or PageStatusKind.Done);

        // "Check again", from the line, checks the GPO: the summary of the tab.
        Assert.True(again.Command.CanExecute(again.Parameter));
        again.Command.Execute(again.Parameter);
        await B2Harness.WaitUntilAsync(() => !gpo.IsCheckingHealth && gpo.HealthFindings.Count > 0 && gpo.Status.Current.Kind == PageStatusKind.Idle);
        Assert.StartsWith(summaryStart, gpo.Status.Current.Message, StringComparison.Ordinal);
        Assert.Equal(gpo.HealthStatusText, gpo.Status.Current.Message);
    }

    [Theory]
    [InlineData("en-US", "Could not check the GPO: ", " Press Retry to try again. (ServerUnavailable)", "Retry")]
    [InlineData("pl-PL", "Nie udało się sprawdzić obiektu GPO: ", " Wybierz Ponów, aby spróbować ponownie. (ServerUnavailable)", "Ponów")]
    public async Task ACheckThatFails_IsTheStateOfTheTab_WithRetry_AndNotTheErrorOfTheDocument(string culture, string lead, string tail, string retryLabel)
    {
        var h = await GroupPolicyHarness.CreateAsync(CultureInfo.GetCultureInfo(culture));
        using var gpo = await h.GpoAsync(h.Sample.DriveMappings);
        var summary = gpo.LoadedText;
        var fail = 1;
        h.Session.BeforeOperation = _ =>
        {
            if (Volatile.Read(ref fail) == 1)
            {
                throw new DirectoryException(DirectoryErrorKind.ServerUnavailable, "the domain controller does not answer (test)");
            }
        };

        gpo.SelectedTab = GpoDocumentTab.Status;
        await B2Harness.WaitUntilAsync(() => !gpo.IsCheckingHealth && gpo.Status.Current.Kind == PageStatusKind.Error);

        // The tab is empty and the line says why - for as long as the tab shows nothing because of it. No note says
        // it a second time, and the document itself has no error: its other tabs are as they were.
        var failed = gpo.Status.Current;
        Assert.StartsWith(lead, failed.Message, StringComparison.Ordinal);
        Assert.EndsWith(tail, failed.Message, StringComparison.Ordinal);
        Assert.Equal("GroupPolicy.Status.HealthFailed [ServerUnavailable]", failed.Log.ToString());
        Assert.False(failed.CanClose);
        Assert.Same(failed, gpo.HealthStatus.Lasting);
        Assert.Null(gpo.Status.Lasting);
        Assert.Null(gpo.Error);
        Assert.Empty(gpo.HealthNotes);
        Assert.Null(gpo.HealthNote);
        Assert.Empty(gpo.HealthFindings);
        Assert.Equal(string.Empty, gpo.HealthStatusText);
        var retry = Assert.IsType<PageStatusAction>(failed.Action);
        Assert.Equal(retryLabel, retry.Label);
        Assert.Same(gpo.CheckHealthCommand, retry.Command);

        // The domain controller answers again: Retry checks the GPO, and the state is gone with the check.
        Volatile.Write(ref fail, 0);
        retry.Command.Execute(retry.Parameter);
        await B2Harness.WaitUntilAsync(() => !gpo.IsCheckingHealth && gpo.HealthFindings.Count > 0 && gpo.Status.Current.Kind == PageStatusKind.Idle);
        Assert.Null(gpo.HealthStatus.Lasting);
        Assert.Equal(gpo.HealthStatusText, gpo.Status.Current.Message);

        // On another tab the line is the document's.
        gpo.SelectedTab = GpoDocumentTab.Details;
        Assert.Equal((PageStatusKind.Idle, summary), (gpo.Status.Current.Kind, gpo.Status.Current.Message));
    }

    [Fact]
    public async Task ATabThatFailed_SaysSoOnlyWhileItIsShown()
    {
        var h = await GroupPolicyHarness.CreateAsync();
        using var gpo = await h.GpoAsync(h.Sample.DriveMappings);
        var fail = 1;
        h.Session.BeforeOperation = _ =>
        {
            if (Volatile.Read(ref fail) == 1)
            {
                throw new DirectoryException(DirectoryErrorKind.ServerUnavailable, "the domain controller does not answer (test)");
            }
        };
        gpo.SelectedTab = GpoDocumentTab.Status;
        await B2Harness.WaitUntilAsync(() => !gpo.IsCheckingHealth && gpo.HealthStatus.Lasting is not null);
        Assert.Equal(PageStatusKind.Error, gpo.Status.Current.Kind);

        // Another tab is shown: the line is the document's - the failure stays with its tab.
        gpo.SelectedTab = GpoDocumentTab.Scope;
        Assert.Equal((PageStatusKind.Idle, gpo.LoadedText), (gpo.Status.Current.Kind, gpo.Status.Current.Message));
        Assert.NotNull(gpo.HealthStatus.Lasting);

        // The GPO is deleted elsewhere and the document reads itself again: nothing of the tab is left to talk about.
        Volatile.Write(ref fail, 0);
        await h.Session.DeleteAsync(h.GpoDn(h.Sample.DriveMappings), subtree: true, Ct);
        await gpo.RefreshCommand.ExecuteAsync(null);
        Assert.True(gpo.IsMissing);
        Assert.Null(gpo.HealthStatus.Lasting);
        Assert.Equal(PageStatusKind.Idle, gpo.HealthStatus.Current.Kind);
        Assert.Equal(string.Empty, gpo.HealthStatus.Current.Message);
        gpo.SelectedTab = GpoDocumentTab.Status;
        Assert.Equal(PageStatusKind.Error, gpo.Status.Current.Kind);
        Assert.Equal(gpo.Error, gpo.Status.Current.Message);
        Assert.False(gpo.IsCheckingHealth);
    }

    [Fact]
    public async Task WhatACheckCouldNotEvaluate_IsANoteAboveTheFindings_NotStatus()
    {
        var h = await GroupPolicyHarness.CreateAsync();
        h.Sysvol.Unavailable = SysvolUnavailableReason.Unreachable;
        using var gpo = await h.GpoAsync(h.Sample.WorkstationPasswords, GpoDocumentTab.Status);

        Assert.NotNull(gpo.SysvolWarning);
        Assert.Equal([gpo.SysvolWarning], gpo.HealthNotes);
        Assert.True(gpo.HasHealthNotes);
        Assert.Equal(gpo.SysvolWarning, gpo.HealthNote);
        Assert.Null(gpo.HealthStatus.Lasting);
        Assert.Null(gpo.Status.Lasting);
        Assert.Equal(PageStatusKind.Idle, gpo.Status.Current.Kind);
        Assert.StartsWith("Last check: quick, ", gpo.Status.Current.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ACheckThatIsAStepOfTheLoad_BelongsToTheLoad_TheTabStartsNoOperation()
    {
        var h = await GroupPolicyHarness.CreateAsync();
        using var gpo = (GroupPolicyObjectDocumentViewModel)h.Factory.Create(new GroupPolicyDocumentRequest(GroupPolicyDocumentKind.Object)
        {
            GpoId = h.Sample.DriveMappings,
            Name = "Użytkownicy – Mapowanie dysków",
            Tab = GpoDocumentTab.Status,
        });
        Assert.Same(gpo.HealthStatus, gpo.Status.Front);
        var tab = Record(gpo.HealthStatus);
        var document = Record(gpo.Status);

        await gpo.LoadAsync();

        // One operation for one piece of work: the document reads - and, as a step of that, checks the GPO; the tab only
        // gets its summary. The line works until the load is over and then says what the tab says.
        Assert.NotEmpty(gpo.HealthFindings);
        Assert.DoesNotContain(Snapshot(tab), s => s.Kind != PageStatusKind.Idle);
        var said = Snapshot(document);
        Assert.Equal((PageStatusKind.Working, "Reading “Użytkownicy – Mapowanie dysków”…"), said[0]);
        Assert.All(said.Take(said.Count - 1), s => Assert.Equal(PageStatusKind.Working, s.Kind));
        Assert.Contains(said, s => s.Kind == PageStatusKind.Working && s.Message.StartsWith("Reading the GPOs, links and WMI filters", StringComparison.Ordinal));
        Assert.Equal((PageStatusKind.Idle, gpo.HealthStatusText), said[^1]);
        Assert.StartsWith("Last check: full, ", gpo.HealthStatusText, StringComparison.Ordinal);
        Assert.False(gpo.Status.IsWorking);
        Assert.False(gpo.HealthStatus.IsWorking);
    }

    [Fact]
    public async Task TheLogCopies_OfTheTab_CarryKeysAndCodes_NeverTheNameOfTheGpo()
    {
        var logger = new ListLogger<PageStatusHolder>();
        var h = await GroupPolicyHarness.CreateAsync(null, logger);
        using var gpo = await h.GpoAsync(h.Sample.DriveMappings);
        Assert.Equal("Użytkownicy – Mapowanie dysków", gpo.Title);

        // A check that is stopped, then one that fails.
        using (var held = new HeldCall(h.Session))
        {
            gpo.SelectedTab = GpoDocumentTab.Status;
            await held.Reached.WaitAsync(HoldLimit, Ct);
            gpo.HealthStatus.CancelCommand.Execute(null);
        }

        await B2Harness.WaitUntilAsync(() => !gpo.IsCheckingHealth && gpo.Status.Current.Kind == PageStatusKind.Attention);
        h.Session.BeforeOperation = _ => throw new DirectoryException(DirectoryErrorKind.ServerUnavailable, "serwer dc01.contoso.test nie odpowiada");
        await gpo.CheckHealthCommand.ExecuteAsync(null);
        h.Session.BeforeOperation = null;
        Assert.Equal(PageStatusKind.Error, gpo.Status.Current.Kind);

        var messages = logger.Entries.Select(entry => entry.Message).ToList();
        Assert.Contains(messages, m => m.Contains("GpoDiag.HealthChecking", StringComparison.Ordinal));
        Assert.Contains(messages, m => m.Contains("GroupPolicy.Status.HealthStopped", StringComparison.Ordinal));
        Assert.Contains(messages, m => m.Contains("GroupPolicy.Status.HealthFailed", StringComparison.Ordinal) && m.Contains("[ServerUnavailable]", StringComparison.Ordinal));
        Assert.DoesNotContain("Mapowanie", logger.AllText, StringComparison.Ordinal);
        Assert.DoesNotContain("contoso", logger.AllText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("nie odpowiada", logger.AllText, StringComparison.Ordinal);
    }

    // ---- Helpers ----------------------------------------------------------------------------------------------------

    /// <summary>Every status a holder shows from now on: its kind and its text.</summary>
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
