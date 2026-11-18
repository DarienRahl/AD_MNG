using System.Globalization;
using AdMng.Core.Directory;
using AdMng.Core.Pki;
using AdMng.Presentation.Feedback;
using AdMng.Presentation.Localization;
using AdMng.Presentation.Pki;
using AdMng.Presentation.Tests.Dialogs;
using AdMng.Presentation.Tests.GroupPolicy;

namespace AdMng.Presentation.Tests.Pki;

/// <summary>
/// What "Szablony certyfikatów" says in its status line (track V, stage V2b-4, unit 3) over the Polish PKI sample: the summary -
/// the templates, how many accounts were counted, the selected template -, the read with its stages (the objects, the rights, the
/// accounts) and Stop, a read that was stopped, one that failed, and the account pass that could not count - the rights stay on
/// screen and the line says what is missing. The base class's states (not connected, a failed read) are proven with the overview
/// in <see cref="PkiOverviewStatusTests"/>. What the tab shows is covered by <see cref="PkiTemplatesTests"/>.
/// </summary>
public sealed class PkiTemplatesStatusTests
{
    private static readonly CancellationToken Token = TestContext.Current.CancellationToken;

    private const string Reading = "Wczytywanie „Szablony certyfikatów”";

    [Fact]
    public async Task TheSummary_SaysTheTemplates_HowManyAccountsWereCounted_AndTheSelection_AndFollowsALanguageSwitch()
    {
        var h = await PkiStatusHarness.CreateAsync();
        using var document = h.Templates();
        var seen = new StatusRecorder(document.Status);

        await document.LoadAsync();

        var counted = PkiDisplay.Count(h.H.B2.Localizer, h.Cache.Expansion!.AccountsScanned);
        var summary = document.Status.Current;
        Assert.Equal(PageStatusKind.Idle, summary.Kind);
        Assert.Equal($"{document.SummaryText} · policzone konta: {counted} · {document.SelectionText}", summary.Message);
        Assert.StartsWith("Szablony: ", summary.Message, StringComparison.Ordinal);
        Assert.NotEmpty(document.SelectionText);
        Assert.Null(document.Status.Lasting);
        Assert.True(document.IsExpanded);

        // The selected template follows.
        var other = document.Rows.First(r => r != document.SelectedRow);
        document.SelectedRow = other;
        Assert.EndsWith(" · " + other.DisplayName, document.Status.Current.Message, StringComparison.Ordinal);

        h.H.B2.Localizer.SetCulture(CultureInfo.GetCultureInfo("en-US"));
        await B2Harness.WaitUntilAsync(() => !document.IsLoading && document.SummaryText.Contains("templates", StringComparison.Ordinal));
        Assert.Contains(" templates, ", document.Status.Current.Message, StringComparison.Ordinal);
        Assert.Contains(" · accounts counted: ", document.Status.Current.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(seen.Seen, said => said.Kind is PageStatusKind.Error or PageStatusKind.Attention);
    }

    [Fact]
    public async Task TheRead_SaysItsStages_TheObjects_TheRights_AndTheAccounts_WhileTheTemplatesAreAlreadyShown()
    {
        var h = await PkiStatusHarness.CreateAsync();
        var hold = new PkiHold();
        h.Cache.OnExpansion = async (progress, ct) =>
        {
            progress?.Report(new PkiExpansionProgress(500));
            await hold.HoldAsync(ct);
        };
        using var document = h.Templates();
        var seen = new StatusRecorder(document.Status);

        var load = document.LoadAsync();
        await hold.Reached.WaitAsync(Token);

        // The account pass is a stage of the read: the templates and the trustee level are on screen, the line says how many
        // accounts were read so far, with Stop.
        var working = document.Status.Current;
        Assert.Equal((PageStatusKind.Working, $"{Reading} – liczenie kont: 500…"), (working.Kind, working.Message));
        Assert.Equal("PkiDoc.Status.Reading (500)", working.Log.ToString());
        Assert.Same(document.Status.CancelCommand, working.Action!.Command);
        Assert.True(document.IsCounting);
        Assert.NotEmpty(document.Rows);
        Assert.False(document.IsExpanded);

        hold.Release();
        await load;

        var working2 = seen.Plain.Where(p => p.Kind == PageStatusKind.Working).Select(p => p.Message).ToList();
        Assert.Equal($"{Reading}…", working2[0]);
        Assert.Equal($"{Reading} – obiekty AD CS: 0…", working2[1]);
        Assert.Contains($"{Reading} – odczyt uprawnień szablonów…", working2);
        Assert.True(working2.IndexOf($"{Reading} – odczyt uprawnień szablonów…") < working2.IndexOf($"{Reading} – liczenie kont: 0…"));
        Assert.Contains($"{Reading} – liczenie kont: 500…", working2);
        Assert.Equal(PageStatusKind.Idle, document.Status.Current.Kind);
        Assert.False(document.IsLoading);
    }

    [Fact]
    public async Task Stop_DuringTheAccountPass_ReturnsControlAtOnce_TheTemplatesStay_AndTheStoppedReadLasts()
    {
        var h = await PkiStatusHarness.CreateAsync();
        var hold = new PkiHold();
        h.Cache.OnExpansion = (_, ct) => hold.HoldAsync(ct);
        using var document = h.Templates();

        var load = document.LoadAsync();
        await hold.Reached.WaitAsync(Token);
        document.Status.CancelCommand.Execute(null);
        Assert.Equal("Zatrzymywanie…", document.Status.Current.Message);
        hold.Release();
        await load;

        var stopped = document.Status.Current;
        Assert.Equal(PageStatusKind.Attention, stopped.Kind);
        Assert.Equal($"{Reading} zatrzymano: karta może nie pokazywać wszystkiego. Wybierz „Odśwież”, aby wczytać ją ponownie.", stopped.Message);
        Assert.Same(document.Status.Lasting, stopped);
        Assert.Same(document.RefreshCommand, stopped.Action!.Command);
        Assert.NotEmpty(document.Rows);
        Assert.False(document.IsExpanded);
        Assert.False(document.IsCounting);
        Assert.False(document.IsLoading);

        h.Cache.OnExpansion = null;
        stopped.Action.Command.Execute(null);
        await B2Harness.WaitUntilAsync(() => !document.IsLoading && document.IsExpanded);
        Assert.Null(document.Status.Lasting);
        Assert.Equal(PageStatusKind.Idle, document.Status.Current.Kind);
    }

    [Fact]
    public async Task AccountsThatCouldNotBeCounted_AreAStateOfTheTab_TheRightsStayOnScreen_AndRetryCountsAgain()
    {
        var h = await PkiStatusHarness.CreateAsync();
        h.Cache.OnExpansion = (_, _) => throw new IOException("the global catalog is down");
        using var document = h.Templates();
        var logs = new LoggedStatus(document.Status);

        await document.LoadAsync();

        // The list is there, with the trustee level; the line says what it lacks - an attention that cannot be closed.
        Assert.NotEmpty(document.Rows);
        Assert.False(document.IsExpanded);
        Assert.False(document.IsLoading);
        var attention = document.Status.Current;
        Assert.Equal(PageStatusKind.Attention, attention.Kind);
        Assert.StartsWith("Nie udało się policzyć kont: ", attention.Message, StringComparison.Ordinal);
        Assert.EndsWith(" Lista pokazuje, kto może się zarejestrować, ale nie liczbę kont. Wybierz „Ponów”, aby wczytać ponownie. (IOException)", attention.Message, StringComparison.Ordinal);
        Assert.Equal("PkiTemplates.Status.CountFailed [IOException]", attention.Log.ToString());
        Assert.Same(document.Status.Lasting, attention);
        Assert.False(document.Status.CloseCommand.CanExecute(null));
        Assert.Null(document.Error);
        Assert.StartsWith("Nie udało się policzyć kont: ", document.CountStatusText, StringComparison.Ordinal);
        var retry = Assert.IsType<PageStatusAction>(attention.Action);
        Assert.Equal("Ponów", retry.Label);
        Assert.Same(document.RefreshCommand, retry.Command);
        Assert.DoesNotContain(logs.Logs, log => log.Contains("catalog", StringComparison.Ordinal));

        // The sentence is said again in the language of the moment.
        h.H.B2.Localizer.SetCulture(CultureInfo.GetCultureInfo("en-US"));
        await B2Harness.WaitUntilAsync(() => !document.IsLoading && document.Status.Current.Message.StartsWith("Could not count the accounts: ", StringComparison.Ordinal));
        Assert.EndsWith(" The list shows who may enroll, but not how many accounts that is. Press “Retry” to read again. (IOException)", document.Status.Current.Message, StringComparison.Ordinal);

        // Retry reads again and counts: what lasted is gone.
        h.Cache.OnExpansion = null;
        document.Status.Current.Action!.Command.Execute(null);
        await B2Harness.WaitUntilAsync(() => !document.IsLoading && document.IsExpanded);
        Assert.Null(document.Status.Lasting);
        Assert.Equal(PageStatusKind.Idle, document.Status.Current.Kind);
        Assert.Contains(" · accounts counted: ", document.Status.Current.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AFailedRead_LeavesNoTemplates_AndSaysItAsAnErrorWithRetry_NotConnectedClearsTheList()
    {
        var h = await PkiStatusHarness.CreateAsync();
        h.Cache.OnSnapshot = (_, _) => throw new DirectoryException(DirectoryErrorKind.ServerUnavailable, "down");
        using var document = h.Templates();

        await document.LoadAsync();

        var error = document.Status.Current;
        Assert.Equal(PageStatusKind.Error, error.Kind);
        Assert.StartsWith("Nie udało się wczytać „Szablony certyfikatów”: ", error.Message, StringComparison.Ordinal);
        Assert.EndsWith(" Wybierz Ponów, aby spróbować ponownie. (ServerUnavailable)", error.Message, StringComparison.Ordinal);
        Assert.Empty(document.Rows);
        Assert.Same(document.RefreshCommand, error.Action!.Command);

        h.Cache.OnSnapshot = null;
        await document.RefreshCommand.ExecuteAsync(null);
        Assert.NotEmpty(document.Rows);
        Assert.Equal(PageStatusKind.Idle, document.Status.Current.Kind);

        await h.H.B2.Sessions.DisconnectAsync();
        await B2Harness.WaitUntilAsync(() => document.Rows.Count == 0);
        Assert.Equal((PageStatusKind.Error, h.H.B2.Localizer[L.Errors.NotConnected]), (document.Status.Current.Kind, document.Status.Current.Message));
        Assert.Equal(string.Empty, document.SummaryText);
    }

    [Fact]
    public async Task EveryLogCopy_OfTheTemplates_IsAKeyWithCountsAndACode_NeverATemplateOrAnAccount()
    {
        var h = await PkiStatusHarness.CreateAsync();
        using var document = h.Templates();
        var logs = new LoggedStatus(document.Status);
        h.Cache.OnExpansion = (progress, _) =>
        {
            progress?.Report(new PkiExpansionProgress(40));
            throw new IOException("denied");
        };

        await document.LoadAsync();
        h.Cache.OnExpansion = null;
        await document.RefreshAsync();

        var all = logs.Logs.Where(log => log.Length > 0).ToList();
        Assert.NotEmpty(all);
        Assert.All(all, log => Assert.Matches(@"^(PkiDoc|PkiTemplates)\.[A-Za-z.]+( \([0-9, ]+\))?( \[[A-Za-z0-9 ,]+\])?$", log));
        Assert.DoesNotContain(all, log => log.Contains("Kontoso", StringComparison.Ordinal) || log.Contains("denied", StringComparison.Ordinal));
    }
}
