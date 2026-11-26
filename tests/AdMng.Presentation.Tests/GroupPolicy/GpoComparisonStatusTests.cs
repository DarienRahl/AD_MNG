using System.Globalization;
using AdMng.Core.Directory;
using AdMng.Core.GroupPolicy;
using AdMng.Core.GroupPolicy.Management;
using AdMng.Presentation.Feedback;
using AdMng.Presentation.GroupPolicy;
using AdMng.Presentation.Localization;
using AdMng.Presentation.Services;
using AdMng.Presentation.Tests.GroupPolicy.Copy;
using AdMng.Testing;
using CommunityToolkit.Mvvm.Input;

namespace AdMng.Presentation.Tests.GroupPolicy;

/// <summary>
/// The status line of the comparison of two GPOs (track V, stage V2b-3; docs/dev/v-interface-renewal.md "V2b-3 as built"):
/// the comparison is the operation of the window, with Stop - reading both GPOs, then the administrative templates that
/// name the policies as a stage of the same message -, and at rest the line says how many differences the rows hold. A
/// comparison that was stopped before it showed anything or failed is the state of the window until Refresh or Retry; one
/// that was stopped while the templates were read is a result (the rows are there). What SYSVOL could not give is a note,
/// no status. The comparison itself is covered, unchanged, by <see cref="GroupPolicyBackupAndCompareTests"/>.
/// </summary>
public sealed class GpoComparisonStatusTests
{
    private static readonly CultureInfo Polish = CultureInfo.GetCultureInfo("pl-PL");
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(20);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("en-US", "Comparing “Porządki – Pusty GPO” with “Porządki – Szablony”…", "Reading the administrative templates to name the policies…", @"^\d+ differences in \d+ items$")]
    [InlineData("pl-PL", "Porównywanie „Porządki – Pusty GPO” z „Porządki – Szablony”…", "Wczytywanie szablonów administracyjnych, aby nazwać zasady…", @"^Różnice: \d+ z \d+ pozycji$")]
    public async Task TheComparison_IsTheOperationOfTheWindow_WithTheTemplatesAsAStage_AndTheLineShowsTheDifferencesAfterwards(
        string culture, string comparing, string stage, string summary)
    {
        var k = await K7Harness.CreateAsync(culture: CultureInfo.GetCultureInfo(culture));
        using var comparison = await NewAsync(k);
        Assert.IsAssignableFrom<IHasPageStatus>(comparison);
        Assert.Same(PageStatus.Empty, comparison.Status.Current);
        var seen = new StatusRecorder(comparison.Status);

        // The administrative templates are listed by a call the test holds: the rows are there, the comparison stands at its stage.
        var hold = k.H.Sysvol.HoldNext("ListDefinitions");
        var loading = comparison.InitializeAsync();
        await hold.Reached.WaitAsync(Limit, Ct);

        Assert.NotEmpty(comparison.Rows);
        var working = comparison.Status.Current;
        Assert.Equal((PageStatusKind.Working, stage), (working.Kind, working.Message));
        Assert.Equal("GpoComparison.Status.Comparing", working.Log.ToString()); // the stage is not a new message
        Assert.False(working.CanClose);
        var stop = Assert.IsType<PageStatusAction>(working.Action);
        Assert.Same(comparison.Status.CancelCommand, stop.Command);

        hold.Release();
        await loading.WaitAsync(Limit, Ct);

        // The rows are the result: the line shows how many differences they hold, and nothing else was said.
        Assert.Equal([(PageStatusKind.Working, comparing), (PageStatusKind.Working, stage), (PageStatusKind.Idle, comparison.Status.Idle.Message)], seen.Plain);
        Assert.Matches(summary, comparison.Status.Current.Message);
        Assert.Null(comparison.Status.Lasting);
        Assert.False(comparison.Status.IsWorking);
    }

    [Fact]
    public async Task WithoutRegistrySettings_TheTemplatesAreNotAStage()
    {
        var k = await K7Harness.CreateAsync(culture: CultureInfo.GetCultureInfo("en-US"));
        using var comparison = await NewAsync(k, k.Cleanup.EmptyGpo, k.H.Sample.WorkstationRemoteDesktop);
        var seen = new StatusRecorder(comparison.Status);

        await comparison.InitializeAsync();

        Assert.Equal(PageStatusKind.Working, seen.Plain[0].Kind);
        Assert.Equal(PageStatusKind.Idle, seen.Plain[^1].Kind);
        Assert.DoesNotContain(seen.Plain, s => s.Message.Contains("administrative templates", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("en-US", "Stopping…", "Comparing was stopped: nothing is shown. Press “Refresh” to compare again.", "Refresh")]
    [InlineData("pl-PL", "Zatrzymywanie…", "Porównywanie zatrzymano: nic nie jest pokazane. Wybierz „Odśwież”, aby porównać ponownie.", "Odśwież")]
    public async Task Stop_BeforeTheRowsAreShown_ReturnsControl_TheLineSaysNothingIsShown_AndRefreshComparesAgain(
        string culture, string stopping, string stoppedText, string refreshLabel)
    {
        var k = await K7Harness.CreateAsync(culture: CultureInfo.GetCultureInfo(culture));
        using var comparison = await NewAsync(k);
        var seen = new StatusRecorder(comparison.Status);
        using var held = new HeldDirectoryCall(k.H.Session);
        var loading = Task.Run(comparison.InitializeAsync, Ct);
        await held.Reached.WaitAsync(Limit, Ct);
        Assert.True(comparison.Status.IsWorking);

        // Stop answers at once: "Stopping…" on the page, and only then the comparison is asked to stop.
        comparison.Status.CancelCommand.Execute(null);

        Assert.True(comparison.Status.IsCancelling);
        Assert.Equal(stopping, comparison.Status.Current.Message);
        Assert.True(comparison.Status.IsWorking);
        held.Release();
        await loading.WaitAsync(Limit, Ct);

        // The state of the window until the comparison is repeated: an attention that cannot be closed, with Refresh.
        Assert.False(comparison.Status.IsWorking);
        Assert.Empty(comparison.Rows);
        Assert.Equal([PageStatusKind.Working, PageStatusKind.Working, PageStatusKind.Attention], seen.Plain.Select(s => s.Kind).ToList());
        var state = comparison.Status.Current;
        Assert.Equal(stoppedText, state.Message);
        Assert.Equal("GpoComparison.Status.Stopped", state.Log.ToString());
        Assert.False(state.CanClose);
        Assert.Same(state, comparison.Status.Lasting);
        var refresh = Assert.IsType<PageStatusAction>(state.Action);
        Assert.Equal(refreshLabel, refresh.Label);
        Assert.Same(comparison.LoadCommand, refresh.Command);

        // Refresh compares: the rows, the count of differences, and the state is over.
        await ((IAsyncRelayCommand)refresh.Command).ExecuteAsync(refresh.Parameter);

        Assert.NotEmpty(comparison.Rows);
        Assert.Null(comparison.Status.Lasting);
        Assert.Equal((PageStatusKind.Idle, comparison.Status.Idle.Message), (comparison.Status.Current.Kind, comparison.Status.Current.Message));
    }

    [Theory]
    [InlineData("en-US", "Comparing was stopped: the rows may be out of date. Press “Refresh” to compare again.")]
    [InlineData("pl-PL", "Porównywanie zatrzymano: wiersze mogą być nieaktualne. Wybierz „Odśwież”, aby porównać ponownie.")]
    public async Task Stop_WhileTheRowsOfAnEarlierComparisonAreShown_SaysTheyMayBeOutOfDate_AndTheyStay(string culture, string stoppedText)
    {
        var k = await K7Harness.CreateAsync(culture: CultureInfo.GetCultureInfo(culture));
        using var comparison = await NewAsync(k);
        await comparison.InitializeAsync();
        var rows = comparison.Rows.Count;
        Assert.NotEqual(0, rows);

        // Refresh: the first read of the new comparison waits for the test, and the operator stops it.
        using var held = new HeldDirectoryCall(k.H.Session);
        var refreshing = Task.Run(() => comparison.LoadCommand.ExecuteAsync(null), Ct);
        await held.Reached.WaitAsync(Limit, Ct);
        comparison.Status.CancelCommand.Execute(null);
        held.Release();
        await refreshing.WaitAsync(Limit, Ct);

        // The rows of the comparison before stay - and the line says that they may be out of date, until Refresh repeats it.
        Assert.Equal(rows, comparison.Rows.Count);
        var state = comparison.Status.Current;
        Assert.Equal((PageStatusKind.Attention, stoppedText), (state.Kind, state.Message));
        Assert.Equal("GpoComparison.Status.StoppedStale", state.Log.ToString());
        Assert.False(state.CanClose);
        Assert.Same(state, comparison.Status.Lasting);
        await ((IAsyncRelayCommand)state.Action!.Command).ExecuteAsync(null);
        Assert.Null(comparison.Status.Lasting);
        Assert.Equal((PageStatusKind.Idle, comparison.Status.Idle.Message), (comparison.Status.Current.Kind, comparison.Status.Current.Message));
    }

    [Theory]
    [InlineData("en-US", "Reading the administrative templates was stopped: the policies are shown under their registry names. Press “Refresh” to compare again.")]
    [InlineData("pl-PL", "Wczytywanie szablonów administracyjnych zatrzymano: zasady są pokazane pod nazwami z rejestru. Wybierz „Odśwież”, aby porównać ponownie.")]
    public async Task Stop_WhileTheTemplatesAreRead_IsAResult_TheRowsAreThere_AndClosingItGivesTheCountBack(string culture, string stoppedText)
    {
        var k = await K7Harness.CreateAsync(culture: CultureInfo.GetCultureInfo(culture));
        using var comparison = await NewAsync(k);
        var hold = k.H.Sysvol.HoldNext("ListDefinitions");
        var loading = comparison.InitializeAsync();
        await hold.Reached.WaitAsync(Limit, Ct);
        var rows = comparison.Rows.Count;
        var summary = comparison.Status.Idle.Message;
        Assert.NotEqual(0, rows);

        comparison.Status.CancelCommand.Execute(null);
        hold.Release();
        await loading.WaitAsync(Limit, Ct);

        // The rows are all there - the policies under their registry names - and the line says so: a result that can be
        // closed, not a state, because nothing is missing from the list.
        Assert.Equal(rows, comparison.Rows.Count);
        var result = comparison.Status.Current;
        Assert.Equal((PageStatusKind.Attention, stoppedText), (result.Kind, result.Message));
        Assert.Equal("GpoComparison.Status.TemplatesStopped", result.Log.ToString());
        Assert.True(result.CanClose);
        Assert.Null(comparison.Status.Lasting);
        Assert.Equal(summary, comparison.Status.Idle.Message);
        comparison.Status.CloseCommand.Execute(null);
        Assert.Equal((PageStatusKind.Idle, summary), (comparison.Status.Current.Kind, comparison.Status.Current.Message));
    }

    [Theory]
    [InlineData("en-US", "Could not compare “Porządki – Pusty GPO” with “Porządki – Szablony”: ", " Press Retry to try again. (ServerUnavailable)", "Retry")]
    [InlineData("pl-PL", "Nie udało się porównać „Porządki – Pusty GPO” z „Porządki – Szablony”: ", " Wybierz Ponów, aby spróbować ponownie. (ServerUnavailable)", "Ponów")]
    public async Task AComparisonThatFailed_IsTheStateOfTheWindow_WithTheReasonTheCodeAndRetry(string culture, string lead, string tail, string retryLabel)
    {
        var k = await K7Harness.CreateAsync(culture: CultureInfo.GetCultureInfo(culture));
        using var comparison = await NewAsync(k);
        k.H.Session.BeforeOperation = _ => throw new DirectoryException(DirectoryErrorKind.ServerUnavailable, "serwer dc01.contoso.test nie odpowiada: DSID-0315");
        var seen = new StatusRecorder(comparison.Status);

        await comparison.InitializeAsync();

        Assert.Equal([PageStatusKind.Working, PageStatusKind.Error], seen.Plain.Select(s => s.Kind).ToList());
        var failed = comparison.Status.Current;
        Assert.StartsWith(lead, failed.Message, StringComparison.Ordinal);
        Assert.EndsWith(tail, failed.Message, StringComparison.Ordinal);
        Assert.True(failed.Message.Length > lead.Length + tail.Length); // the reason, in words
        Assert.DoesNotContain("DSID", failed.Message, StringComparison.Ordinal);
        Assert.Equal("GpoComparison.Status.CompareFailed [ServerUnavailable]", failed.Log.ToString());
        Assert.False(failed.CanClose);
        Assert.Same(failed, comparison.Status.Lasting);
        Assert.False(comparison.Status.CloseCommand.CanExecute(null));
        Assert.False(comparison.Status.IsWorking);
        Assert.Empty(comparison.Rows);
        var retry = Assert.IsType<PageStatusAction>(failed.Action);
        Assert.Equal(retryLabel, retry.Label);

        // Retry, with a domain controller that answers: the rows are there, the error is gone.
        k.H.Session.BeforeOperation = null;
        await ((IAsyncRelayCommand)retry.Command).ExecuteAsync(retry.Parameter);

        Assert.NotEmpty(comparison.Rows);
        Assert.Null(comparison.Status.Lasting);
        Assert.Equal(PageStatusKind.Idle, comparison.Status.Current.Kind);
    }

    [Fact]
    public async Task WhatSysvolCouldNotGive_IsANoteAboveTheRows_AndTheLineKeepsTheSummary()
    {
        var k = await K7Harness.CreateAsync(culture: CultureInfo.GetCultureInfo("en-US"));
        k.H.Sysvol.Unavailable = SysvolUnavailableReason.AccessDenied;
        using var comparison = await NewAsync(k);
        var seen = new StatusRecorder(comparison.Status);

        await comparison.InitializeAsync();

        // The comparison of the containers was made: the rows and their count are the status; the sentence about the
        // settings and files that were not compared is the note under it.
        Assert.Contains("were not compared", comparison.Warning, StringComparison.Ordinal);
        Assert.Equal([PageStatusKind.Working, PageStatusKind.Idle], seen.Plain.Select(s => s.Kind).ToList());
        Assert.Equal(comparison.Status.Idle.Message, comparison.Status.Current.Message);
        Assert.DoesNotContain("SYSVOL", comparison.Status.Current.Message, StringComparison.Ordinal);
        Assert.Null(comparison.Status.Lasting);
    }

    /// <summary>
    /// A Refresh that the operator stops before it showed anything leaves the rows of the comparison before - and with them
    /// the note on what that comparison could not compare: the note is about the rows shown (the auditor's note to GP-4: the
    /// stopped refresh cleared it, and the rows looked complete).
    /// </summary>
    [Fact]
    public async Task Stop_OfARefresh_KeepsTheNoteOfTheRowsThatStay()
    {
        var k = await K7Harness.CreateAsync(culture: CultureInfo.GetCultureInfo("en-US"));
        k.H.Sysvol.Unavailable = SysvolUnavailableReason.AccessDenied;
        using var comparison = await NewAsync(k);
        await comparison.InitializeAsync();
        var note = comparison.Warning;
        var rows = comparison.Rows.Count;
        Assert.Contains("were not compared", note, StringComparison.Ordinal);

        // Refresh, with a SYSVOL that answers now: its first read waits for the test, and the operator stops it.
        k.H.Sysvol.Unavailable = null;
        using (var held = new HeldDirectoryCall(k.H.Session))
        {
            var refreshing = Task.Run(() => comparison.LoadCommand.ExecuteAsync(null), Ct);
            await held.Reached.WaitAsync(Limit, Ct);
            comparison.Status.CancelCommand.Execute(null);
            held.Release();
            await refreshing.WaitAsync(Limit, Ct);
        }

        Assert.Equal("GpoComparison.Status.StoppedStale", comparison.Status.Current.Log.ToString());
        Assert.Equal(rows, comparison.Rows.Count);
        Assert.Equal(note, comparison.Warning);

        // A comparison that shows its rows says what it could not compare - here: nothing.
        await comparison.LoadCommand.ExecuteAsync(null);
        Assert.Null(comparison.Warning);
    }

    /// <summary>
    /// An export that fails while the comparison still runs - the rows are there and the templates are read, so Export can be
    /// pressed - is an error toast: the line keeps the running comparison and its Stop, and the answer to the press is not
    /// lost (audit GP-5: it was said nowhere since the error bar went). Afterwards the line has nothing of it.
    /// </summary>
    [Fact]
    public async Task AnExportThatFailedWhileTheTemplatesAreRead_IsAnErrorToast_AndTheLineKeepsTheComparison()
    {
        var k = await K7Harness.CreateAsync(culture: CultureInfo.GetCultureInfo("en-US"));
        using var comparison = await NewAsync(k);
        var hold = k.H.Sysvol.HoldNext("ListDefinitions");
        var loading = comparison.InitializeAsync();
        await hold.Reached.WaitAsync(Limit, Ct);
        Assert.NotEmpty(comparison.Rows);
        var working = comparison.Status.Current;
        using var folder = new TempFolder();
        k.H.B2.Files.NextSavePath = Path.Combine(folder.Path, "no-such-folder", "porównanie.csv");
        var toasts = k.H.B2.Notifications.Items.Count;

        await comparison.ExportCommand.ExecuteAsync(null);

        var toast = Assert.Single(k.H.B2.Notifications.Items.Skip(toasts));
        Assert.Equal(NotificationSeverity.Error, toast.Severity);
        Assert.StartsWith("The comparison was not exported: ", toast.Message, StringComparison.Ordinal);
        Assert.EndsWith(" (DirectoryNotFoundException)", toast.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(folder.Path, toast.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Same(working, comparison.Status.Current);

        hold.Release();
        await loading.WaitAsync(Limit, Ct);
        Assert.Equal((PageStatusKind.Idle, comparison.Status.Idle.Message), (comparison.Status.Current.Kind, comparison.Status.Current.Message));
    }

    [Fact]
    public async Task AnExportThatFailed_SaysSoInTheLine_AsAResultThatCanBeClosed_AndAnExportThatWorkedKeepsItsToast()
    {
        var k = await K7Harness.CreateAsync(culture: CultureInfo.GetCultureInfo("en-US"));
        using var comparison = await NewAsync(k);
        await comparison.InitializeAsync();
        var summary = comparison.Status.Current.Message;
        using var folder = new TempFolder();

        // A folder that does not exist: the file cannot be written.
        k.H.B2.Files.NextSavePath = Path.Combine(folder.Path, "no-such-folder", "porównanie.csv");
        await comparison.ExportCommand.ExecuteAsync(null);

        var failed = comparison.Status.Current;
        Assert.Equal(PageStatusKind.Error, failed.Kind);
        Assert.StartsWith("The comparison was not exported: ", failed.Message, StringComparison.Ordinal);
        Assert.Equal("GpoComparison.Status.ExportFailed [DirectoryNotFoundException]", failed.Log.ToString());
        Assert.True(failed.CanClose);
        Assert.Null(comparison.Status.Lasting);
        comparison.Status.CloseCommand.Execute(null);
        Assert.Equal((PageStatusKind.Idle, summary), (comparison.Status.Current.Kind, comparison.Status.Current.Message));

        // A file that can be written: a toast (an export answers with a toast in every converted window), no line.
        k.H.B2.Files.NextSavePath = Path.Combine(folder.Path, "porównanie.csv");
        var toasts = k.H.B2.Notifications.Items.Count;
        await comparison.ExportCommand.ExecuteAsync(null);

        Assert.Equal((PageStatusKind.Idle, summary), (comparison.Status.Current.Kind, comparison.Status.Current.Message));
        Assert.Equal(toasts + 1, k.H.B2.Notifications.Items.Count);
    }

    [Theory]
    [InlineData("en-US")]
    [InlineData("pl-PL")]
    public async Task WithoutASession_TheLineSaysSoFromTheStart_AndNothingIsRead(string culture)
    {
        var k = await K7Harness.CreateAsync(culture: CultureInfo.GetCultureInfo(culture));
        var (a, b) = await TwoAsync(k, k.Cleanup.EmptyGpo, k.Cleanup.Templates);
        await k.H.B2.Sessions.DisconnectAsync();
        var before = k.H.Session.Operations.Count;
        using var comparison = new GpoComparisonViewModel(k.H.Services, a, b);

        // Said before anything is pressed: an error that cannot be closed.
        var state = comparison.Status.Current;
        Assert.Equal(PageStatusKind.Error, state.Kind);
        Assert.Equal(k.H.B2.Localizer[L.Errors.NotConnected], state.Message);
        Assert.Equal("Errors.NotConnected", state.Log.ToString());
        Assert.False(state.CanClose);
        var seen = new StatusRecorder(comparison.Status);

        await comparison.InitializeAsync();

        Assert.Empty(seen.Seen);
        Assert.Empty(comparison.Rows);
        Assert.False(comparison.Status.IsWorking);
        Assert.Equal(before, k.H.Session.Operations.Count);
    }

    [Fact]
    public async Task TheEndOfTheSession_TakesTheLine_AlsoDuringTheComparison_AndTheSummaryFollowsTheLanguage()
    {
        // While both GPOs are read: the comparison ends without a word, the line says for good that the session ended.
        var k = await K7Harness.CreateAsync(culture: CultureInfo.GetCultureInfo("en-US"));
        using (var comparing = await NewAsync(k))
        {
            using var held = new HeldDirectoryCall(k.H.Session);
            var loading = Task.Run(comparing.InitializeAsync, Ct);
            await held.Reached.WaitAsync(Limit, Ct);
            Assert.True(comparing.Status.IsWorking);
            var seen = new StatusRecorder(comparing.Status);

            await k.H.B2.Sessions.DisconnectAsync();

            Assert.True(comparing.IsSessionEnded);
            Assert.False(comparing.Status.IsWorking);
            Assert.Equal((PageStatusKind.Error, k.H.B2.Localizer[L.WindowSession.Ended]), (comparing.Status.Current.Kind, comparing.Status.Current.Message));
            Assert.False(comparing.Status.Current.CanClose);
            held.Release();
            await loading.WaitAsync(Limit, Ct);

            // Whatever the comparison brought afterwards: one message, and it stays.
            Assert.Equal([(PageStatusKind.Error, k.H.B2.Localizer[L.WindowSession.Ended])], seen.Plain);
            Assert.Equal("WindowSession.Ended", comparing.Status.Current.Log.ToString());
        }

        // At rest, after a language switch: the summary says the same in the other language.
        k = await K7Harness.CreateAsync(culture: CultureInfo.GetCultureInfo("en-US"));
        using var comparison = await NewAsync(k);
        await comparison.InitializeAsync();
        var english = comparison.Status.Current.Message;
        Assert.Matches(@"^\d+ differences in \d+ items$", english);

        k.H.B2.Localizer.SetCulture(Polish);

        Assert.Equal(PageStatusKind.Idle, comparison.Status.Current.Kind);
        Assert.Matches(@"^Różnice: \d+ z \d+ pozycji$", comparison.Status.Current.Message);
        Assert.Equal(comparison.Status.Idle.Message, comparison.Status.Current.Message);
        await k.H.B2.Sessions.DisconnectAsync();
        Assert.Equal(k.H.B2.Localizer[L.WindowSession.Ended], comparison.Status.Current.Message);
    }

    [Fact]
    public async Task TheLogCopies_CarryKeysAndCodes_NeverAGpoNameADomainOrAServerText()
    {
        var logger = new ListLogger<PageStatusHolder>();
        var k = await K7Harness.CreateAsync(culture: CultureInfo.GetCultureInfo("en-US"), statusLog: logger);
        using var comparison = await NewAsync(k);
        var hold = k.H.Sysvol.HoldNext("ListDefinitions");
        var loading = comparison.InitializeAsync();
        await hold.Reached.WaitAsync(Limit, Ct);
        comparison.Status.CancelCommand.Execute(null);
        hold.Release();
        await loading.WaitAsync(Limit, Ct);
        k.H.Session.BeforeOperation = _ => throw new DirectoryException(DirectoryErrorKind.AccessDenied, "00002098: SecErr: DSID-03150F94 for CN={31B2F340}");
        await comparison.LoadCommand.ExecuteAsync(null);
        k.H.Session.BeforeOperation = null;

        var messages = logger.Entries.Select(entry => entry.Message).ToList();
        Assert.Contains(messages, m => m.Contains("GpoComparison.Status.Comparing", StringComparison.Ordinal));
        Assert.Contains(messages, m => m.Contains("GpoComparison.Status.TemplatesStopped", StringComparison.Ordinal));
        Assert.Contains(messages, m => m.Contains("GpoComparison.Status.CompareFailed", StringComparison.Ordinal) && m.Contains("AccessDenied", StringComparison.Ordinal));
        Assert.DoesNotContain("Porządki", logger.AllText, StringComparison.Ordinal);
        Assert.DoesNotContain("contoso", logger.AllText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("DSID", logger.AllText, StringComparison.Ordinal);
        Assert.DoesNotContain("31B2F340", logger.AllText, StringComparison.OrdinalIgnoreCase);
    }

    // ---- Helpers ------------------------------------------------------------------------------------------------

    /// <summary>The comparison of "Porządki – Pusty GPO" (nothing in it) with "Porządki – Szablony" (a Registry.pol policy).</summary>
    private static Task<GpoComparisonViewModel> NewAsync(K7Harness k) => NewAsync(k, k.Cleanup.EmptyGpo, k.Cleanup.Templates);

    private static async Task<GpoComparisonViewModel> NewAsync(K7Harness k, Guid a, Guid b)
    {
        var (gpoA, gpoB) = await TwoAsync(k, a, b);
        return new GpoComparisonViewModel(k.H.Services, gpoA, gpoB);
    }

    private static async Task<(GpoInfo A, GpoInfo B)> TwoAsync(K7Harness k, Guid a, Guid b)
    {
        var reader = k.H.Services.Reader;
        return ((await reader.ReadPolicyAsync(k.H.Session, a, Ct))!, (await reader.ReadPolicyAsync(k.H.Session, b, Ct))!);
    }
}
