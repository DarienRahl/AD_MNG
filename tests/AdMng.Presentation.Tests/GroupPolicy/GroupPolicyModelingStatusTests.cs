using System.Globalization;
using AdMng.Core.Directory;
using AdMng.Core.GroupPolicy;
using AdMng.Core.GroupPolicy.Modeling;
using AdMng.Presentation.Feedback;
using AdMng.Presentation.GroupPolicy;
using AdMng.Presentation.GroupPolicy.Modeling;
using AdMng.Presentation.Tests.Dialogs;
using AdMng.Testing;

namespace AdMng.Presentation.Tests.GroupPolicy;

/// <summary>
/// What the model of a computer or a user says in its status line (track V, stage V2b-3; docs/dev/v-interface-renewal.md
/// "V2b-3 as built", <i>Group Policy documents</i>): what they get, in numbers, as the summary; the calculation - the
/// button "Przelicz" and the refresh of the toolbar start it - as the operation of the tab, with Stop; a calculation that
/// was stopped or failed as the state of the tab, with its action. What the model could not use (a target that does not
/// exist, SYSVOL unavailable) is a note, and "the inputs changed" stays a sentence next to the inputs: neither is status.
/// What the model computes is covered, unchanged, by <see cref="GroupPolicyModelingTests"/>.
/// </summary>
public sealed class GroupPolicyModelingStatusTests
{
    private static readonly CultureInfo Polish = CultureInfo.GetCultureInfo("pl-PL");
    private static readonly CultureInfo English = CultureInfo.GetCultureInfo("en-US");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<GroupPolicyHarness> HarnessAsync(CultureInfo culture, ListLogger<PageStatusHolder>? logger = null)
    {
        var harness = logger is null ? await GroupPolicyHarness.CreateAsync(culture) : await GroupPolicyHarness.CreateAsync(culture, logger);
        harness.Session.ComputesTokenGroups = true;
        return harness;
    }

    private static GroupPolicyModelingDocumentViewModel Create(GroupPolicyHarness h, string? computer = null, string? user = null) =>
        (GroupPolicyModelingDocumentViewModel)h.Factory.Create(new GroupPolicyDocumentRequest(GroupPolicyDocumentKind.Modeling) { ComputerDn = computer, UserDn = user });

    private static Task<GroupPolicyModelingDocumentViewModel> ModelAsync(GroupPolicyHarness h, string? computer = null, string? user = null) =>
        h.OpenAsync<GroupPolicyModelingDocumentViewModel>(new GroupPolicyDocumentRequest(GroupPolicyDocumentKind.Modeling) { ComputerDn = computer, UserDn = user });

    // ---- The summary ------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("en-US", "Computer: 3 GPO, ", "No result – choose a computer or a user and click “Recalculate”.")]
    [InlineData("pl-PL", "Komputer: 3 GPO, ", "Brak wyniku – wybierz komputer lub użytkownika i kliknij „Przelicz”.")]
    public async Task TheLine_SaysWhatTheComputerGets_InNumbers_AndWhatToDoBeforeThereIsAModel(string culture, string summary, string empty)
    {
        var h = await HarnessAsync(CultureInfo.GetCultureInfo(culture));
        using var nothing = Create(h);
        Assert.IsAssignableFrom<IHasPageStatus>(nothing);

        // Nothing is read before the tab loads; with neither a computer nor a user the model says what to do - once.
        Assert.Equal((PageStatusKind.Idle, string.Empty), (nothing.Status.Current.Kind, nothing.Status.Current.Message));
        await nothing.LoadAsync();
        Assert.Equal((PageStatusKind.Idle, empty), (nothing.Status.Current.Kind, nothing.Status.Current.Message));
        Assert.Equal(empty, nothing.StatusText);

        using var model = await ModelAsync(h, computer: h.Company.Computer1);

        // The summary of the model is said by the line - once, with the status bar of a host without one.
        Assert.True(model.HasModel);
        Assert.StartsWith(summary, model.SummaryText, StringComparison.Ordinal);
        Assert.Equal((PageStatusKind.Idle, model.SummaryText), (model.Status.Current.Kind, model.Status.Current.Message));
        Assert.Equal(model.SummaryText, model.LoadedText);
        Assert.Equal(model.SummaryText, model.StatusText);
        Assert.Null(model.Status.Lasting);
        Assert.False(model.Status.CloseCommand.CanExecute(null));

        // The inputs changed: a sentence next to the inputs ("click Recalculate"), not an event of the line.
        model.WmiFilters = WmiFilterAssumption.AssumeFalse;
        Assert.True(model.IsStale);
        Assert.Equal((PageStatusKind.Idle, model.SummaryText), (model.Status.Current.Kind, model.Status.Current.Message));
        Assert.Null(model.Status.Lasting);
    }

    [Fact]
    public async Task TheSummary_FollowsTheLanguage()
    {
        var h = await HarnessAsync(English);
        using var model = await ModelAsync(h, computer: h.Company.Computer1);
        Assert.StartsWith("Computer: 3 GPO, ", model.Status.Current.Message, StringComparison.Ordinal);

        h.B2.Localizer.SetCulture(Polish);
        await B2Harness.WaitUntilAsync(() => !model.IsLoading && model.Status.Current.Message.StartsWith("Komputer: 3 GPO, ", StringComparison.Ordinal));

        Assert.Equal((PageStatusKind.Idle, model.SummaryText), (model.Status.Current.Kind, model.Status.Current.Message));
    }

    // ---- Calculating ------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("en-US", "Reading “What the computer gets – WAW-PC001”…", "Stop", "Stopping…",
        "Reading “What the computer gets – WAW-PC001” was stopped: the tab may not show everything. Press Refresh to read it again.", "Refresh")]
    [InlineData("pl-PL", "Wczytywanie „Co dostanie komputer – WAW-PC001”…", "Zatrzymaj", "Zatrzymywanie…",
        "Wczytywanie „Co dostanie komputer – WAW-PC001” zatrzymano: karta może nie pokazywać wszystkiego. Wybierz Odśwież, aby wczytać ją ponownie.", "Odśwież")]
    public async Task TheCalculation_IsTheOperationOfTheTab_StopReturnsControl_AndAStoppedOneIsTheStateOfTheTabWithRefresh(
        string culture, string reading, string stopLabel, string stopping, string stoppedText, string refreshLabel)
    {
        var h = await HarnessAsync(CultureInfo.GetCultureInfo(culture));
        using var model = Create(h, h.Company.Computer1);
        var said = new StatusRecorder(model.Status);
        Task loading;
        using (var held = new HeldDirectoryCall(h.Session))
        {
            loading = Task.Run(() => model.LoadAsync(), Ct);
            await held.Reached.WaitAsync(HeldDirectoryCall.Limit, Ct);

            // Working: what is calculated - with Stop, and nothing to close.
            Assert.True(model.IsLoading);
            Assert.True(model.Status.IsWorking);
            var working = model.Status.Current;
            Assert.Equal((PageStatusKind.Working, reading), (working.Kind, working.Message));
            Assert.Equal("GroupPolicy.Status.Reading", working.Log.ToString());
            Assert.False(working.CanClose);
            var stop = Assert.IsType<PageStatusAction>(working.Action);
            Assert.Equal(stopLabel, stop.Label);
            Assert.Same(model.Status.CancelCommand, stop.Command);

            // Pressed in the line: "Stopping…" at once - the call that is on its way to the domain controller still has to
            // return.
            stop.Command.Execute(stop.Parameter);
            Assert.True(model.Status.IsCancelling);
            Assert.Equal((PageStatusKind.Working, stopping), (model.Status.Current.Kind, model.Status.Current.Message));
            Assert.Null(model.Status.Current.Action);
            Assert.True(model.IsLoading);
        }

        await loading.WaitAsync(HeldDirectoryCall.Limit, Ct);

        // Stopped: the state of the tab until it is calculated again - what it may lack, and Refresh. It cannot be closed.
        Assert.False(model.IsLoading);
        Assert.False(model.HasModel);
        Assert.Null(model.Error);
        var stopped = model.Status.Current;
        Assert.Equal((PageStatusKind.Attention, stoppedText), (stopped.Kind, stopped.Message));
        Assert.Equal("GroupPolicy.Status.Stopped", stopped.Log.ToString());
        Assert.False(stopped.CanClose);
        Assert.Same(stopped, model.Status.Lasting);
        var refresh = Assert.IsType<PageStatusAction>(stopped.Action);
        Assert.Equal(refreshLabel, refresh.Label);
        Assert.Same(model.RefreshCommand, refresh.Command);
        Assert.Equal(
            [(PageStatusKind.Working, reading), (PageStatusKind.Working, stopping), (PageStatusKind.Attention, stoppedText)],
            said.Plain);

        // Refresh, pressed in the line, calculates the model: the summary, and "stopped" is gone with the calculation.
        await model.RefreshCommand.ExecuteAsync(null);
        Assert.True(model.HasModel);
        Assert.Null(model.Status.Lasting);
        Assert.Equal((PageStatusKind.Idle, model.SummaryText), (model.Status.Current.Kind, model.Status.Current.Message));
    }

    [Fact]
    public async Task ARecalculationThatIsStopped_LeavesTheModelOfBefore_AndSaysItMayNotShowEverything()
    {
        var h = await HarnessAsync(English);
        using var model = await ModelAsync(h, computer: h.Company.Computer1);
        var summary = model.SummaryText;
        model.WmiFilters = WmiFilterAssumption.AssumeFalse;

        Task recalculating;
        using (var held = new HeldDirectoryCall(h.Session))
        {
            recalculating = Task.Run(() => model.RecalculateCommand.ExecuteAsync(null), Ct);
            await held.Reached.WaitAsync(HeldDirectoryCall.Limit, Ct);
            model.Status.CancelCommand.Execute(null);
            Assert.Equal("Stopping…", model.Status.Current.Message);
        }

        await recalculating.WaitAsync(HeldDirectoryCall.Limit, Ct);
        Assert.True(model.HasModel);
        Assert.Equal(summary, model.SummaryText);
        Assert.True(model.IsStale);
        Assert.False(model.ExportCsvCommand.CanExecute(null));
        Assert.Equal(PageStatusKind.Attention, model.Status.Current.Kind);

        await model.RecalculateCommand.ExecuteAsync(null);
        Assert.False(model.IsStale);
        Assert.Null(model.Status.Lasting);
        Assert.Equal(PageStatusKind.Idle, model.Status.Current.Kind);
    }

    [Theory]
    [InlineData("en-US", "Could not read “What the computer gets – WAW-PC001”: ", " Press Retry to try again. (ServerUnavailable)", "Retry")]
    [InlineData("pl-PL", "Nie udało się wczytać „Co dostanie komputer – WAW-PC001”: ", " Wybierz Ponów, aby spróbować ponownie. (ServerUnavailable)", "Ponów")]
    public async Task ACalculationThatFails_IsTheStateOfTheTab_WithRetry_AndRetryCalculatesAgain(string culture, string lead, string tail, string retryLabel)
    {
        var h = await HarnessAsync(CultureInfo.GetCultureInfo(culture));
        using var model = await ModelAsync(h, computer: h.Company.Computer1);
        var said = new StatusRecorder(model.Status);

        // The domain controller does not answer "Przelicz".
        var fail = 1;
        h.Session.BeforeOperation = _ =>
        {
            if (Volatile.Read(ref fail) == 1)
            {
                throw new DirectoryException(DirectoryErrorKind.ServerUnavailable, "the domain controller does not answer (test)");
            }
        };
        await model.RecalculateCommand.ExecuteAsync(null);

        // No model of before stays under the new title: the tab is empty and the line says why - what was tried, the
        // reason, what to do, the code - with Retry. It lasts.
        Assert.False(model.IsLoading);
        Assert.False(model.HasModel);
        Assert.False(model.ExportCsvCommand.CanExecute(null));
        Assert.NotNull(model.Error);
        var failed = model.Status.Current;
        Assert.Equal((PageStatusKind.Error, lead + model.Error + tail), (failed.Kind, failed.Message));
        Assert.Equal("GroupPolicy.Status.ReadFailed [ServerUnavailable]", failed.Log.ToString());
        Assert.False(failed.CanClose);
        Assert.Same(failed, model.Status.Lasting);
        var retry = Assert.IsType<PageStatusAction>(failed.Action);
        Assert.Equal(retryLabel, retry.Label);
        Assert.Same(model.RefreshCommand, retry.Command);
        Assert.Equal([PageStatusKind.Working, PageStatusKind.Error], said.Plain.Select(s => s.Kind));
        Assert.DoesNotContain("does not answer", failed.Log.ToString(), StringComparison.Ordinal);

        // Retry, from the line, calculates the model again.
        Volatile.Write(ref fail, 0);
        retry.Command.Execute(retry.Parameter);
        await B2Harness.WaitUntilAsync(() => !model.IsLoading && model.Status.Current.Kind == PageStatusKind.Idle);
        h.Session.BeforeOperation = null;
        Assert.True(model.HasModel);
        Assert.Null(model.Error);
        Assert.Equal((PageStatusKind.Idle, model.SummaryText), (model.Status.Current.Kind, model.Status.Current.Message));
    }

    [Fact]
    public async Task NotConnected_IsTheStateOfTheTab_AndTheModelIsGone()
    {
        var h = await HarnessAsync(English);
        using var model = await ModelAsync(h, computer: h.Company.Computer1);

        await h.B2.Sessions.DisconnectAsync();
        await B2Harness.WaitUntilAsync(() => model.Status.Current.Kind == PageStatusKind.Error);

        Assert.Equal("Not connected to a domain.", model.Status.Current.Message);
        Assert.False(model.HasModel);
        Assert.False(model.IsLoading);
        Assert.False(model.RecalculateCommand.CanExecute(null));
        Assert.False(model.Status.CloseCommand.CanExecute(null));
    }

    // ---- Notes ------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task WhatTheModelCouldNotUse_IsANote_NotStatus()
    {
        var h = await HarnessAsync(English);

        // A computer that does not exist: nothing to model - a sentence about the model, and the line keeps its summary.
        var missing = "CN=Nie ma,OU=Komputery,OU=Firma," + h.DomainDn;
        using var model = Create(h, missing);
        var said = new StatusRecorder(model.Status);
        await model.LoadAsync();

        Assert.False(model.HasModel);
        Assert.Equal(["The account contoso.test/Firma/Komputery/Nie ma does not exist."], model.Notes);
        Assert.Equal((PageStatusKind.Idle, model.SummaryText), (model.Status.Current.Kind, model.Status.Current.Message));
        Assert.StartsWith("No result", model.Status.Current.Message, StringComparison.Ordinal);
        Assert.Null(model.Status.Lasting);
        Assert.DoesNotContain(said.Plain, s => s.Kind is PageStatusKind.Attention or PageStatusKind.Error or PageStatusKind.Done);

        // SYSVOL is not available: the GPO lists are complete, their settings are not - a note, the summary is the model's.
        var bare = await HarnessAsync(English);
        bare.Sysvol.Unavailable = SysvolUnavailableReason.Unreachable;
        using var withoutSettings = await ModelAsync(bare, computer: bare.Company.Computer1);
        Assert.Contains("SYSVOL unavailable: the GPOs' settings were not read; the GPO lists are complete.", withoutSettings.Notes);
        Assert.True(withoutSettings.HasSysvolWarning);
        Assert.Equal((PageStatusKind.Idle, withoutSettings.SummaryText), (withoutSettings.Status.Current.Kind, withoutSettings.Status.Current.Message));
        Assert.Null(withoutSettings.Status.Lasting);
    }

    // ---- The log copy -----------------------------------------------------------------------------------------------

    [Fact]
    public async Task TheLogCopies_CarryKeysAndCodes_NeverANameOrAServerText()
    {
        var logger = new ListLogger<PageStatusHolder>();
        var h = await HarnessAsync(Polish, logger);
        using var model = await ModelAsync(h, computer: h.Company.Computer1);

        h.Session.BeforeOperation = _ => throw new DirectoryException(DirectoryErrorKind.ServerUnavailable, "serwer dc01.contoso.test nie odpowiada");
        await model.RecalculateCommand.ExecuteAsync(null);
        h.Session.BeforeOperation = null;
        Assert.Equal(PageStatusKind.Error, model.Status.Current.Kind);

        var messages = logger.Entries.Select(entry => entry.Message).ToList();
        Assert.Contains(messages, m => m.Contains("GroupPolicy.Status.Reading", StringComparison.Ordinal));
        Assert.Contains(messages, m => m.Contains("GroupPolicy.Status.ReadFailed", StringComparison.Ordinal) && m.Contains("[ServerUnavailable]", StringComparison.Ordinal));
        Assert.DoesNotContain("WAW-PC001", logger.AllText, StringComparison.Ordinal);
        Assert.DoesNotContain("Co dostanie", logger.AllText, StringComparison.Ordinal);
        Assert.DoesNotContain("Komputer:", logger.AllText, StringComparison.Ordinal);
        Assert.DoesNotContain("contoso", logger.AllText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("nie odpowiada", logger.AllText, StringComparison.Ordinal);
    }
}
