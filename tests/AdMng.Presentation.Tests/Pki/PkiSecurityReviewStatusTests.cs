using System.Globalization;
using AdMng.Core.Directory;
using AdMng.Core.Pki;
using AdMng.Presentation.Feedback;
using AdMng.Presentation.Localization;
using AdMng.Presentation.Tests.Dialogs;
using AdMng.Presentation.Tests.GroupPolicy;

namespace AdMng.Presentation.Tests.Pki;

/// <summary>
/// What "Przegląd bezpieczeństwa AD CS" says in its status line (track V, stage V2b-4, unit 3) over the Polish PKI sample: the
/// summary - the findings, when and where the shown check ran, the selected finding -, the check as the operation of the tab with
/// its stage and Stop (which is also the Cancel of "Sprawdź ponownie"), a check that was stopped - the results of the check
/// before stay and the line says so as the state of the tab - and one that failed. What the findings are is covered by
/// <see cref="PkiSecurityReviewTests"/>.
/// </summary>
public sealed class PkiSecurityReviewStatusTests
{
    private static readonly CancellationToken Token = TestContext.Current.CancellationToken;

    private const string Checking = "Sprawdzanie bezpieczeństwa AD CS";

    [Fact]
    public async Task TheSummary_SaysTheFindings_WhenAndWhereTheCheckRan_AndTheSelection_AndFollowsALanguageSwitchAndTheFilters()
    {
        var h = await PkiStatusHarness.CreateAsync();
        using var review = h.Review();
        var seen = new StatusRecorder(review.Status);
        Assert.IsAssignableFrom<IHasPageStatus>(review);
        Assert.Equal((PageStatusKind.Idle, "Jeszcze nie sprawdzono."), (review.Status.Current.Kind, review.Status.Current.Message));

        await review.LoadAsync();

        Assert.Equal(PageStatusKind.Idle, review.Status.Current.Kind);
        Assert.Equal($"15 uwag · {review.LastRunText}", review.Status.Current.Message);
        Assert.Matches(@"^15 uwag · Ostatnie sprawdzenie: .+ na dc01\.contoso\.test$", review.Status.Current.Message);
        Assert.Null(review.Status.Lasting);
        Assert.False(review.Status.CloseCommand.CanExecute(null));
        Assert.Equal(string.Empty, review.ProgressText);

        // The selected finding follows; a filter says how many are shown.
        review.SelectedRow = review.Rows[0];
        Assert.EndsWith(" · " + review.Rows[0].Title, review.Status.Current.Message, StringComparison.Ordinal);
        review.SelectedRow = null;
        review.SelectedEsc = review.EscOptions.Single(o => o.Value == "ESC1");
        Assert.Equal($"Pokazano uwagi: 2 z 15 · {review.LastRunText}", review.Status.Current.Message);

        h.H.B2.Localizer.SetCulture(CultureInfo.GetCultureInfo("en-US"));
        Assert.Matches(@"^Findings shown: 2 of 15 · Last check: .+ on dc01\.contoso\.test$", review.Status.Current.Message);
        Assert.DoesNotContain(seen.Seen, said => said.Kind is PageStatusKind.Error or PageStatusKind.Attention);
    }

    [Fact]
    public async Task TheCheck_IsTheOperationOfTheTab_SaysItsStages_AndEndsAsTheSummary()
    {
        var h = await PkiStatusHarness.CreateAsync();
        var hold = new PkiHold();
        h.Cache.OnExpansion = async (progress, ct) =>
        {
            progress?.Report(new PkiExpansionProgress(250));
            await hold.HoldAsync(ct);
        };
        using var review = h.Review();
        var seen = new StatusRecorder(review.Status);

        var check = review.LoadAsync();
        await hold.Reached.WaitAsync(Token);

        var working = review.Status.Current;
        Assert.Equal((PageStatusKind.Working, $"{Checking} – konta: 250…"), (working.Kind, working.Message));
        Assert.Equal("PkiSecurity.Status.Checking (250)", working.Log.ToString());
        Assert.Equal("Zatrzymaj", working.Action!.Label);
        Assert.Same(review.Status.CancelCommand, working.Action.Command);
        Assert.True(review.IsLoading);
        Assert.Equal(working.Message, review.ProgressText);

        hold.Release();
        await check;

        // The title, then the stages in order: the objects, the accounts, the permissions, the principals, the rules.
        var plain = seen.Plain.Where(p => p.Kind == PageStatusKind.Working).Select(p => p.Message).ToList();
        Assert.Equal($"{Checking}…", plain[0]);
        Assert.Equal($"{Checking} – obiekty AD CS: 0…", plain[1]);
        var accounts = plain.IndexOf($"{Checking} – konta: 0…");
        var access = plain.IndexOf($"{Checking} – odczyt uprawnień obiektów PKI…");
        var principals = plain.IndexOf($"{Checking} – rozpoznawanie kont i grup…");
        var rules = plain.IndexOf($"{Checking} – ocena reguł ESC…");
        Assert.True(1 < accounts && accounts < access && access < principals && principals < rules, string.Join(" | ", plain));
        Assert.Equal(PageStatusKind.Idle, review.Status.Current.Kind);
        Assert.Equal($"15 uwag · {review.LastRunText}", review.Status.Current.Message);
        Assert.False(review.IsLoading);
        Assert.Equal(string.Empty, review.ProgressText);
    }

    [Fact]
    public async Task Stop_ReturnsControlAtOnce_TheResultsOfTheCheckBeforeStay_AndTheStoppedCheckLastsWithItsAction()
    {
        var h = await PkiStatusHarness.CreateAsync();
        using var review = h.Review();
        await review.LoadAsync();
        review.SelectedRow = review.Rows.Single(r => r.Esc == "ESC13");
        var rows = review.Rows;
        var hold = new PkiHold();
        h.Cache.OnExpansion = (_, ct) => hold.HoldAsync(ct);
        var seen = new StatusRecorder(review.Status);

        var check = review.RefreshAsync();
        await hold.Reached.WaitAsync(Token);

        // The Cancel of the command and the Stop of the line are the same Stop: "Stopping…" at once.
        Assert.True(review.CancelCommand.CanExecute(null));
        review.CancelCommand.Execute(null);
        Assert.Equal((PageStatusKind.Working, "Zatrzymywanie…"), (review.Status.Current.Kind, review.Status.Current.Message));
        Assert.True(review.Status.IsCancelling);
        Assert.False(review.CancelCommand.CanExecute(null));
        hold.Release();
        await check;

        var stopped = review.Status.Current;
        Assert.Equal(PageStatusKind.Attention, stopped.Kind);
        Assert.Equal(
            $"{Checking} zatrzymano: karta pokazuje wyniki wcześniejszego sprawdzenia, jeśli było. Wybierz „Sprawdź ponownie”, aby sprawdzić ponownie.",
            stopped.Message);
        Assert.Equal("PkiSecurity.Status.Stopped", stopped.Log.ToString());
        Assert.Same(review.Status.Lasting, stopped);
        Assert.False(review.Status.CloseCommand.CanExecute(null));
        Assert.Equal("Sprawdź ponownie", stopped.Action!.Label);
        Assert.Same(review.RefreshCommand, stopped.Action.Command);
        Assert.Equal((PageStatusKind.Working, "Zatrzymywanie…"), seen.Plain[^2]);

        // What was shown stays, with its selection; the summary says what happened to the check.
        Assert.Same(rows, review.Rows);
        Assert.Equal("ESC13", review.SelectedRow?.Esc);
        Assert.Equal("Sprawdzanie anulowano.", review.StatusText);
        Assert.True(review.ExportCsvCommand.CanExecute(null));
        Assert.False(review.IsLoading);
        Assert.Null(review.Error);

        // The sentence is said again in the language of the moment.
        h.H.B2.Localizer.SetCulture(CultureInfo.GetCultureInfo("en-US"));
        Assert.Equal(
            "Checking AD CS security was stopped: the tab shows the results of the earlier check, if there was one. Press “Check again” to check again.",
            review.Status.Current.Message);

        // Its action checks again: the state is gone with the result.
        h.Cache.OnExpansion = null;
        review.Status.Current.Action!.Command.Execute(null);
        await B2Harness.WaitUntilAsync(() => !review.IsLoading && review.Status.Current.Kind == PageStatusKind.Idle);
        Assert.Null(review.Status.Lasting);
        Assert.Equal(15, review.TotalCount);
    }

    [Fact]
    public async Task AFirstCheckThatWasStopped_LeavesNoFindings_AndSaysSo()
    {
        var h = await PkiStatusHarness.CreateAsync();
        var hold = new PkiHold();
        h.Cache.OnExpansion = (_, ct) => hold.HoldAsync(ct);
        using var review = h.Review();

        var check = review.LoadAsync();
        await hold.Reached.WaitAsync(Token);
        review.Status.Current.Action!.Command.Execute(null);
        hold.Release();
        await check;

        Assert.Equal(PageStatusKind.Attention, review.Status.Current.Kind);
        Assert.StartsWith($"{Checking} zatrzymano: ", review.Status.Current.Message, StringComparison.Ordinal);
        Assert.Empty(review.Rows);
        Assert.False(review.HasResult);
        Assert.False(review.ExportCsvCommand.CanExecute(null));
        Assert.False(review.CancelCommand.CanExecute(null));
        Assert.Null(review.Error);
    }

    [Fact]
    public async Task AFailedCheck_IsAnErrorThatLasts_WithRetry_AndTheFindingsOfTheCheckBeforeStay()
    {
        var h = await PkiStatusHarness.CreateAsync();
        using var review = h.Review();
        await review.LoadAsync();
        var rows = review.Rows;
        var logs = new LoggedStatus(review.Status);
        h.Cache.OnExpansion = (_, _) => throw new DirectoryException(DirectoryErrorKind.ServerUnavailable, "down");

        await review.RefreshAsync();

        var error = review.Status.Current;
        Assert.Equal(PageStatusKind.Error, error.Kind);
        Assert.NotNull(review.Error);
        Assert.StartsWith($"Nie udało się sprawdzić bezpieczeństwa AD CS: {review.Error!.TrimEnd('.')}", error.Message, StringComparison.Ordinal);
        Assert.EndsWith(" Wybierz Ponów, aby spróbować ponownie. (ServerUnavailable)", error.Message, StringComparison.Ordinal);
        Assert.Equal("PkiSecurity.Status.CheckFailed [ServerUnavailable]", error.Log.ToString());
        Assert.Same(review.Status.Lasting, error);
        Assert.False(review.Status.CloseCommand.CanExecute(null));
        var retry = Assert.IsType<PageStatusAction>(error.Action);
        Assert.Equal("Ponów", retry.Label);
        Assert.Same(review.RefreshCommand, retry.Command);
        Assert.Same(rows, review.Rows);
        Assert.False(review.IsLoading);
        Assert.DoesNotContain(logs.Logs, log => log.Contains("down", StringComparison.Ordinal));

        h.Cache.OnExpansion = null;
        retry.Command.Execute(null);
        await B2Harness.WaitUntilAsync(() => !review.IsLoading && review.Status.Current.Kind == PageStatusKind.Idle);
        Assert.Null(review.Error);
        Assert.Null(review.Status.Lasting);
    }

    [Fact]
    public async Task NotConnected_IsAnErrorThatLasts_AndFollowsALanguageSwitchAtOnce()
    {
        var h = await PkiStatusHarness.CreateAsync();
        using var review = h.Review();
        await review.LoadAsync();

        await h.H.B2.Sessions.DisconnectAsync();
        await B2Harness.WaitUntilAsync(() => !review.HasResult);

        Assert.Equal((PageStatusKind.Error, h.H.B2.Localizer[L.Errors.NotConnected]), (review.Status.Current.Kind, review.Status.Current.Message));
        Assert.False(review.Status.CloseCommand.CanExecute(null));
        h.H.B2.Localizer.SetCulture(CultureInfo.GetCultureInfo("en-US"));
        Assert.Equal(new ResxLocalizer(CultureInfo.GetCultureInfo("en-US"))[L.Errors.NotConnected], review.Status.Current.Message);
    }

    [Fact]
    public async Task EveryLogCopy_OfTheCheck_IsAKeyWithCountsAndACode_NeverAFindingATemplateOrACa()
    {
        var h = await PkiStatusHarness.CreateAsync();
        using var review = h.Review();
        var logs = new LoggedStatus(review.Status);
        var hold = new PkiHold();
        h.Cache.OnExpansion = async (progress, ct) =>
        {
            progress?.Report(new PkiExpansionProgress(250));
            await hold.HoldAsync(ct);
        };

        var check = review.LoadAsync();
        await hold.Reached.WaitAsync(Token);
        review.Status.CancelCommand.Execute(null);
        hold.Release();
        await check;
        h.Cache.OnExpansion = (_, _) => throw new DirectoryException(DirectoryErrorKind.ServerUnavailable, "down");
        await review.RefreshAsync();
        h.Cache.OnExpansion = null;
        await review.RefreshAsync();

        var all = logs.Logs.Where(log => log.Length > 0).ToList();
        Assert.NotEmpty(all);
        Assert.All(all, log => Assert.Matches(@"^(PkiSecurity\.Status\.[A-Za-z]+|Feedback\.Stopping)( \([0-9, ]+\))?( \[[A-Za-z0-9 ,]+\])?$", log));
    }
}
