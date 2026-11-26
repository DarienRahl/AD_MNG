using System.Globalization;
using AdMng.Core.Directory;
using AdMng.Core.Pki;
using AdMng.Core.Pki.Tls;
using AdMng.Presentation.Feedback;
using AdMng.Presentation.Localization;
using AdMng.Presentation.Pki;
using AdMng.Presentation.Tests.Dialogs;
using AdMng.Presentation.Tests.GroupPolicy;
using AdMng.Testing.Pki;

namespace AdMng.Presentation.Tests.Pki;

/// <summary>
/// What "Usługi kluczy publicznych" says in its status line (track V, stage V2b-4, unit 3; docs/dev/v-interface-renewal.md
/// "V2b-4 as built", <i>Areas</i> 5) over the Polish PKI sample: the summary - what the read found, when and from where, the
/// selected row -, the read as the operation of the tab with its stage and Stop, the states that last (a failed read, a stopped
/// one, not connected), and the two tabs that have an operation of their own - the HTTP check and the probe of the domain
/// controllers - which say it in front of the document while they are shown. A slow domain controller is a call the test holds
/// (<see cref="ScriptedPkiSnapshotService"/>), never a delay. What the tabs show is covered by <see cref="PkiOverviewTests"/>.
/// </summary>
public sealed class PkiOverviewStatusTests
{
    private static readonly CancellationToken Token = TestContext.Current.CancellationToken;

    private static readonly CultureInfo English = CultureInfo.GetCultureInfo("en-US");

    private const string Reading = "Wczytywanie „Usługi kluczy publicznych”";

    [Fact]
    public async Task TheSummary_SaysWhatWasRead_WhenAndFromWhere_AndTheSelection_AndFollowsALanguageSwitch()
    {
        var h = await PkiStatusHarness.CreateAsync();
        using var document = h.Overview();
        var seen = new StatusRecorder(document.Status);
        Assert.IsAssignableFrom<IHasPageStatus>(document);

        await document.LoadAsync();

        // What was read, then when and from where, then the selected row - one line, said once.
        var summary = document.Status.Current;
        Assert.Equal(PageStatusKind.Idle, summary.Kind);
        Assert.Equal($"{document.SummaryText} · odczytano z {PkiSamples.DcHost}, {Date(document)} · {document.SelectionText}", summary.Message);
        Assert.StartsWith("Urzędy przedsiębiorstwa: 2 · szablony: ", summary.Message, StringComparison.Ordinal);
        Assert.NotEmpty(document.SelectionText);
        Assert.Null(document.Status.Lasting);
        Assert.Null(summary.Action);
        Assert.False(document.Status.CloseCommand.CanExecute(null));
        Assert.Equal(string.Empty, document.ProgressText);

        // The selection follows the row and the tab.
        var other = document.Authorities.First(a => a.Name != document.SelectedAuthority!.Name);
        document.SelectedAuthority = other;
        Assert.EndsWith(" · " + other.Name, document.Status.Current.Message, StringComparison.Ordinal);
        document.SelectedTab = PkiOverviewTab.Trust;
        Assert.EndsWith(" · " + document.SelectedCertificate!.Subject, document.Status.Current.Message, StringComparison.Ordinal);
        document.SelectedTab = PkiOverviewTab.Authorities;

        // The summary is built again in the language of the moment.
        h.H.B2.Localizer.SetCulture(English);
        await B2Harness.WaitUntilAsync(() => !document.IsLoading && document.SummaryText.StartsWith("Enterprise CAs: 2", StringComparison.Ordinal));
        Assert.StartsWith("Enterprise CAs: 2 · templates: ", document.Status.Current.Message, StringComparison.Ordinal);
        Assert.Contains($" · read from {PkiSamples.DcHost}, ", document.Status.Current.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(seen.Seen, said => said.Kind is PageStatusKind.Error or PageStatusKind.Attention);
    }

    [Fact]
    public async Task TheRead_IsTheOperationOfTheTab_WithItsStageAndStop_AndEndsAsTheSummary()
    {
        var h = await PkiStatusHarness.CreateAsync();
        var hold = new PkiHold();
        h.Cache.OnSnapshot = async (progress, ct) =>
        {
            progress?.Report(new PkiReadProgress(120));
            await hold.HoldAsync(ct);
        };
        using var document = h.Overview();
        var seen = new StatusRecorder(document.Status);
        var logs = new LoggedStatus(document.Status);

        var load = document.LoadAsync();
        await hold.Reached.WaitAsync(Token);

        // Working from the first moment: what is read, the stage and how many objects so far - with Stop, and nothing to close.
        var working = document.Status.Current;
        Assert.Equal((PageStatusKind.Working, $"{Reading} – obiekty AD CS: 120…"), (working.Kind, working.Message));
        Assert.Equal("PkiDoc.Status.Reading (120)", working.Log.ToString());
        var stop = Assert.IsType<PageStatusAction>(working.Action);
        Assert.Equal("Zatrzymaj", stop.Label);
        Assert.Same(document.Status.CancelCommand, stop.Command);
        Assert.True(document.IsLoading);
        Assert.True(document.Status.IsWorking);
        Assert.False(document.Status.CloseCommand.CanExecute(null));
        Assert.Equal(working.Message, document.ProgressText);

        hold.Release();
        await load;

        var plain = seen.Plain;
        Assert.Equal((PageStatusKind.Working, $"{Reading}…"), plain[0]);
        Assert.Equal((PageStatusKind.Working, $"{Reading} – obiekty AD CS: 0…"), plain[1]);
        Assert.Contains(plain, p => p == (PageStatusKind.Working, $"{Reading} – obiekty AD CS: 120…"));
        Assert.All(plain.Take(plain.Count - 1), p => Assert.Equal(PageStatusKind.Working, p.Kind));
        Assert.StartsWith(Reading, plain[^2].Message, StringComparison.Ordinal);
        Assert.Equal(PageStatusKind.Idle, plain[^1].Kind);
        Assert.Equal(document.Status.Idle.Message, plain[^1].Message);
        Assert.False(document.IsLoading);
        Assert.Equal(string.Empty, document.ProgressText);

        // A log copy is the key and the counts - never the title of the document, a server, a CA or a template.
        Assert.All(logs.Logs.Where(log => log.Length > 0), log => Assert.StartsWith("PkiDoc.Status.Reading", log, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Stop_ReturnsControlAtOnce_TheStoppedReadLasts_AndItsActionReadsAgain()
    {
        var h = await PkiStatusHarness.CreateAsync();
        var hold = new PkiHold();
        h.Cache.OnSnapshot = (_, ct) => hold.HoldAsync(ct);
        using var document = h.Overview();
        var seen = new StatusRecorder(document.Status);

        var load = document.LoadAsync();
        await hold.Reached.WaitAsync(Token);

        // "Stopping…" at once - the read is still on its way to the domain controller.
        document.Status.Current.Action!.Command.Execute(null);
        Assert.Equal((PageStatusKind.Working, "Zatrzymywanie…"), (document.Status.Current.Kind, document.Status.Current.Message));
        Assert.True(document.Status.IsCancelling);
        hold.Release();
        await load;

        Assert.False(document.IsLoading);
        Assert.Null(document.Error);
        Assert.Equal(
            [
                (PageStatusKind.Working, $"{Reading}…"),
                (PageStatusKind.Working, $"{Reading} – obiekty AD CS: 0…"),
                (PageStatusKind.Working, "Zatrzymywanie…"),
                (PageStatusKind.Attention, $"{Reading} zatrzymano: karta może nie pokazywać wszystkiego. Wybierz „Odśwież”, aby wczytać ją ponownie."),
            ],
            seen.Plain);

        // A state of the tab, for as long as it shows what it shows: it cannot be closed, and its action reads again.
        var stopped = document.Status.Current;
        Assert.Same(document.Status.Lasting, stopped);
        Assert.False(document.Status.CloseCommand.CanExecute(null));
        Assert.Equal("PkiDoc.Status.Stopped", stopped.Log.ToString());
        var action = Assert.IsType<PageStatusAction>(stopped.Action);
        Assert.Equal("Odśwież", action.Label);
        Assert.Same(document.RefreshCommand, action.Command);

        h.Cache.OnSnapshot = null;
        action.Command.Execute(null);
        await B2Harness.WaitUntilAsync(() => !document.IsLoading && document.Status.Current.Kind == PageStatusKind.Idle);
        Assert.Null(document.Status.Lasting);
        Assert.StartsWith("Urzędy przedsiębiorstwa: 2", document.Status.Current.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AFailedRead_IsAnErrorThatLasts_WithRetry_AndTheLogCopyHasTheCodeOnly()
    {
        var h = await PkiStatusHarness.CreateAsync();
        h.Cache.OnSnapshot = (_, _) => throw new DirectoryException(DirectoryErrorKind.ServerUnavailable, "down");
        using var document = h.Overview();
        var logs = new LoggedStatus(document.Status);

        await document.LoadAsync();

        var error = document.Status.Current;
        Assert.Equal(PageStatusKind.Error, error.Kind);
        Assert.NotNull(document.Error);
        Assert.StartsWith($"Nie udało się wczytać „Usługi kluczy publicznych”: {document.Error!.TrimEnd('.')}", error.Message, StringComparison.Ordinal);
        Assert.EndsWith(" Wybierz Ponów, aby spróbować ponownie. (ServerUnavailable)", error.Message, StringComparison.Ordinal);
        Assert.Equal("PkiDoc.Status.ReadFailed [ServerUnavailable]", error.Log.ToString());
        Assert.Same(document.Status.Lasting, error);
        Assert.False(document.Status.CloseCommand.CanExecute(null));
        var retry = Assert.IsType<PageStatusAction>(error.Action);
        Assert.Equal("Ponów", retry.Label);
        Assert.Same(document.RefreshCommand, retry.Command);
        Assert.False(document.IsLoading);
        Assert.DoesNotContain(logs.Logs, log => log.Contains("down", StringComparison.Ordinal) || log.Contains("Usługi", StringComparison.Ordinal));

        // Retry reads again and the error is gone.
        h.Cache.OnSnapshot = null;
        retry.Command.Execute(null);
        await B2Harness.WaitUntilAsync(() => !document.IsLoading && document.Status.Current.Kind == PageStatusKind.Idle);
        Assert.Null(document.Error);
        Assert.Null(document.Status.Lasting);
    }

    [Fact]
    public async Task NotConnected_IsAnErrorThatLasts_AndFollowsALanguageSwitchAtOnce()
    {
        var h = await PkiStatusHarness.CreateAsync();
        using var document = h.Overview();
        await document.LoadAsync();

        await h.H.B2.Sessions.DisconnectAsync();
        await B2Harness.WaitUntilAsync(() => document.Status.Current.Kind == PageStatusKind.Error);
        var polish = h.H.B2.Localizer[L.Errors.NotConnected];
        Assert.Equal(polish, document.Status.Current.Message);
        Assert.Equal(PageStatusKind.Error, document.Status.Current.Kind);
        Assert.Null(document.Status.Current.Action);
        Assert.False(document.Status.CloseCommand.CanExecute(null));
        Assert.Empty(document.Authorities);

        // Computed from the facts: the new language, without a read.
        h.H.B2.Localizer.SetCulture(English);
        Assert.Equal(new ResxLocalizer(English)[L.Errors.NotConnected], document.Status.Current.Message);
        Assert.NotEqual(polish, document.Status.Current.Message);
    }

    [Fact]
    public async Task ANewerRead_ReplacesTheRunningOne_WithoutAWord()
    {
        var h = await PkiStatusHarness.CreateAsync();
        var hold = new PkiHold();
        var first = true;
        h.Cache.OnSnapshot = (_, ct) =>
        {
            if (first)
            {
                first = false;
                return hold.HoldAsync(ct);
            }

            return Task.CompletedTask;
        };
        using var document = h.Overview();
        var seen = new StatusRecorder(document.Status);

        var running = document.LoadAsync();
        await hold.Reached.WaitAsync(Token);
        await document.RefreshAsync();
        hold.Release();
        await running;

        Assert.False(document.IsLoading);
        Assert.Null(document.Status.Lasting);
        Assert.DoesNotContain(seen.Seen, said => said.Kind is PageStatusKind.Attention or PageStatusKind.Error);
        Assert.Equal(PageStatusKind.Idle, document.Status.Current.Kind);
        Assert.StartsWith("Urzędy przedsiębiorstwa: 2", document.Status.Current.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheHealthTab_SaysItsCountsInFront_AndTheHttpCheckIsItsOwnOperation_WithStop()
    {
        var h = await PkiStatusHarness.CreateAsync();
        using var document = h.Overview();
        await document.LoadAsync();
        var documentSummary = document.Status.Current.Message;
        var total = PkiHealth.HttpUrls(document.Snapshot!).Count;
        Assert.True(total > 1);

        // The tab in front says what it has to say; before the check it says that the HTTP rows are unknown - as a note.
        Assert.Null(document.Status.Front);
        document.SelectedTab = PkiOverviewTab.Health;
        Assert.Same(document.HealthStatus, document.Status.Front);
        Assert.Equal(document.HealthSummaryText, document.Status.Current.Message);
        Assert.Equal(PageStatusKind.Idle, document.Status.Current.Kind);
        Assert.True(document.HasHttpNote);
        Assert.Equal(h.H.B2.Localizer[L.Pki.HttpNotChecked], document.HttpNote);

        // The check is the operation of the tab, with how far it is - the document itself does not work.
        var hold = new PkiHold();
        h.Cache.OnHttp = async (progress, ct) =>
        {
            progress?.Report(2);
            await hold.HoldAsync(ct);
        };
        var seen = new StatusRecorder(document.HealthStatus);
        var check = document.CheckHttpCommand.ExecuteAsync(null);
        await hold.Reached.WaitAsync(Token);

        var working = document.Status.Current;
        Assert.Equal((PageStatusKind.Working, $"Sprawdzanie adresów AIA, CDP i OCSP – 2 z {total}…"), (working.Kind, working.Message));
        Assert.Equal(2d / total, working.Progress?.Fraction);
        Assert.Equal($"PkiOverview.Status.HttpChecking (2, {total})", working.Log.ToString());
        var stop = Assert.IsType<PageStatusAction>(working.Action);
        Assert.Same(document.HealthStatus.CancelCommand, stop.Command);
        Assert.True(document.IsCheckingHttp);
        Assert.True(document.HealthStatus.IsWorking);
        Assert.False(document.Status.IsWorking);
        Assert.False(document.CheckHttpCommand.CanExecute(null));

        // Another tab: the line is the document's again; the check goes on and is back when its tab is.
        document.SelectedTab = PkiOverviewTab.Authorities;
        Assert.Equal(documentSummary, document.Status.Current.Message);
        document.SelectedTab = PkiOverviewTab.Health;
        Assert.Equal(PageStatusKind.Working, document.Status.Current.Kind);

        // Stop returns control at once; the results of before are not changed, and the stopped check is a result of the tab.
        stop.Command.Execute(null);
        Assert.Equal("Zatrzymywanie…", document.Status.Current.Message);
        hold.Release();
        await check;
        Assert.False(document.IsCheckingHttp);
        var stopped = document.Status.Current;
        Assert.Equal(PageStatusKind.Attention, stopped.Kind);
        Assert.Equal(
            "Sprawdzanie adresów AIA, CDP i OCSP zatrzymano: wyników nie zmieniono. Wybierz „Sprawdź adresy HTTP”, aby sprawdzić ponownie.",
            stopped.Message);
        Assert.Equal("PkiDoc.Status.StoppedKept", stopped.Log.ToString());
        Assert.True(document.Status.CloseCommand.CanExecute(null));
        Assert.Empty(document.HttpRows);
        Assert.True(document.HasHttpNote);
        var again = Assert.IsType<PageStatusAction>(stopped.Action);
        Assert.Equal("Sprawdź adresy HTTP", again.Label);
        Assert.Same(document.CheckHttpCommand, again.Command);

        // Pressing it again checks: the result is the rows and the summary of the tab, the note about the unknown rows is gone.
        h.Cache.OnHttp = null;
        await document.CheckHttpCommand.ExecuteAsync(null);
        Assert.True(document.HasHttpRows);
        Assert.False(document.HasHttpNote);
        Assert.Equal(PageStatusKind.Idle, document.Status.Current.Kind);
        Assert.Equal($"{document.HealthSummaryText} · {document.HttpStatusText}", document.Status.Current.Message);
        Assert.Contains("Sprawdzone adresy HTTP: ", document.Status.Current.Message, StringComparison.Ordinal);
        Assert.Contains(seen.Plain, p => p == (PageStatusKind.Working, $"Sprawdzanie adresów AIA, CDP i OCSP – 0 z {total}…"));
        Assert.Contains(seen.Plain, p => p == (PageStatusKind.Working, $"Sprawdzanie adresów AIA, CDP i OCSP – 2 z {total}…"));
        Assert.Contains(seen.Plain, p => p.Kind == PageStatusKind.Attention);
    }

    [Fact]
    public async Task AFailedHttpCheck_IsAnErrorOfTheTab_ThatCanBeClosed_WithRetry()
    {
        var h = await PkiStatusHarness.CreateAsync();
        using var document = h.Overview();
        await document.LoadAsync();
        document.SelectedTab = PkiOverviewTab.Health;
        var logs = new LoggedStatus(document.HealthStatus);
        h.Cache.OnHttp = (_, _) => throw new IOException("the proxy said no");

        await document.CheckHttpCommand.ExecuteAsync(null);

        var error = document.Status.Current;
        Assert.Equal(PageStatusKind.Error, error.Kind);
        Assert.StartsWith("Nie udało się sprawdzić adresów HTTP: ", error.Message, StringComparison.Ordinal);
        Assert.EndsWith(" Wybierz Ponów, aby spróbować ponownie. (IOException)", error.Message, StringComparison.Ordinal);
        Assert.Equal("PkiOverview.Status.HttpFailed [IOException]", error.Log.ToString());
        Assert.Contains(document.HttpStatusText, error.Message, StringComparison.Ordinal);
        Assert.False(document.IsCheckingHttp);
        Assert.Same(document.CheckHttpCommand, error.Action!.Command);
        Assert.DoesNotContain(logs.Logs, log => log.Contains("proxy", StringComparison.Ordinal));

        // Nothing lasts: closing it brings the summary of the tab back.
        Assert.True(document.Status.CloseCommand.CanExecute(null));
        document.Status.CloseCommand.Execute(null);
        Assert.Equal(PageStatusKind.Idle, document.Status.Current.Kind);
        Assert.Equal(document.HealthSummaryText, document.Status.Current.Message);
    }

    [Fact]
    public async Task TheLdapsProbe_IsTheOperationOfItsTab_StopsWithoutChangingTheResults_AndItsSummaryIsWhatItFound()
    {
        var h = await PkiStatusHarness.CreateAsync();
        h.H.TlsProbe.Set(PkiSamples.DcHost, 636, TlsProbeOutcome.Ok, PkiSamples.DcLdapsDer(), PkiSamples.IssuingCaDer);
        using var document = h.Overview();
        await document.LoadAsync();
        Assert.Empty(h.H.TlsProbe.Calls);

        // Before the probe: what the tab knows, as its summary.
        document.SelectedTab = PkiOverviewTab.Ldaps;
        await B2Harness.WaitUntilAsync(() => document.HasLdapsRows && !document.IsProbingLdaps);
        Assert.Same(document.LdapsStatus, document.Status.Front);
        Assert.Equal(document.LdapsStatusText, document.Status.Current.Message);
        Assert.StartsWith("Sprawdzone porty: ", document.Status.Current.Message, StringComparison.Ordinal);

        // The button probes: the line says it, with Stop, and the button is its Cancel.
        var hold = new PkiHold();
        h.Cache.OnDcCertificates = hold.HoldAsync;
        var probe = document.ProbeLdapsCommand.ExecuteAsync(null);
        await hold.Reached.WaitAsync(Token);
        var working = document.Status.Current;
        Assert.Equal((PageStatusKind.Working, "Sprawdzanie certyfikatów LDAPS kontrolerów domeny…"), (working.Kind, working.Message));
        Assert.Equal("PkiDoc.Status.LdapsChecking", working.Log.ToString());
        Assert.True(document.LdapsStatus.IsWorking);
        Assert.False(document.Status.IsWorking);
        Assert.True(document.IsProbingLdaps);
        var rows = document.LdapsRows.ToList();

        working.Action!.Command.Execute(null);
        Assert.Equal("Zatrzymywanie…", document.Status.Current.Message);
        hold.Release();
        await probe;

        var stopped = document.Status.Current;
        Assert.Equal(PageStatusKind.Attention, stopped.Kind);
        Assert.Equal(
            "Sprawdzanie certyfikatów LDAPS kontrolerów domeny zatrzymano: wyników nie zmieniono. Wybierz „Sprawdź certyfikaty LDAPS”, aby sprawdzić ponownie.",
            stopped.Message);
        Assert.Same(document.ProbeLdapsCommand, stopped.Action!.Command);
        Assert.False(document.IsProbingLdaps);
        Assert.Equal(rows, document.LdapsRows);
        Assert.Null(document.LdapsStatus.Lasting);

        // Probing again: the result is the rows and the summary - nothing is said but the count.
        h.Cache.OnDcCertificates = null;
        await document.ProbeLdapsCommand.ExecuteAsync(null);
        Assert.Equal(PageStatusKind.Idle, document.Status.Current.Kind);
        Assert.Equal(document.LdapsStatusText, document.Status.Current.Message);
    }

    [Fact]
    public async Task AProbeThatCannotBeMade_IsAnErrorOfTheTabThatLasts_UntilTheNextProbe()
    {
        var h = await PkiStatusHarness.CreateAsync();
        using var document = h.Overview();
        await document.LoadAsync();
        document.SelectedTab = PkiOverviewTab.Ldaps;
        await B2Harness.WaitUntilAsync(() => document.HasLdapsRows && !document.IsProbingLdaps);
        var logs = new LoggedStatus(document.LdapsStatus);
        h.Cache.OnDcCertificates = (_) => throw new IOException("the firewall");

        await document.ProbeLdapsCommand.ExecuteAsync(null);

        var error = document.Status.Current;
        Assert.Equal(PageStatusKind.Error, error.Kind);
        Assert.StartsWith("Nie udało się sprawdzić certyfikatów LDAPS: ", error.Message, StringComparison.Ordinal);
        Assert.EndsWith(" Wybierz Ponów, aby spróbować ponownie. (IOException)", error.Message, StringComparison.Ordinal);
        Assert.Equal(error.Message, document.LdapsStatus.Lasting!.Message);
        Assert.False(document.Status.CloseCommand.CanExecute(null));
        Assert.Contains(document.LdapsStatusText.TrimEnd('.'), error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(logs.Logs, log => log.Contains("firewall", StringComparison.Ordinal));

        // Retry probes again; what lasts is gone with the success.
        var retry = Assert.IsType<PageStatusAction>(error.Action);
        Assert.Equal("Ponów", retry.Label);
        Assert.Same(document.ProbeLdapsCommand, retry.Command);
        h.Cache.OnDcCertificates = null;
        await document.ProbeLdapsCommand.ExecuteAsync(null);
        Assert.Null(document.LdapsStatus.Lasting);
        Assert.Equal(PageStatusKind.Idle, document.Status.Current.Kind);
    }

    [Fact]
    public async Task AReadThatProbesTheLdapsTab_SaysTheProbeAsAStageOfTheRead_NotAsAnOperationOfTheTab()
    {
        var h = await PkiStatusHarness.CreateAsync();
        h.H.TlsProbe.Set(PkiSamples.DcHost, 636, TlsProbeOutcome.Ok, PkiSamples.DcLdapsDer(), PkiSamples.IssuingCaDer);
        using var document = h.Overview();
        await document.LoadAsync();
        document.SelectedTab = PkiOverviewTab.Ldaps;
        await B2Harness.WaitUntilAsync(() => document.HasLdapsRows && !document.IsProbingLdaps);

        var hold = new PkiHold();
        h.Cache.OnDcCertificates = hold.HoldAsync;
        var read = document.RefreshAsync();
        await hold.Reached.WaitAsync(Token);

        // The window's own operation is the line, whichever tab is shown; the tab's own holder does not work.
        Assert.Equal(
            (PageStatusKind.Working, $"{Reading} – sprawdzanie certyfikatów LDAPS kontrolerów domeny…"),
            (document.Status.Current.Kind, document.Status.Current.Message));
        Assert.True(document.Status.IsWorking);
        Assert.False(document.LdapsStatus.IsWorking);
        Assert.False(document.ProbeLdapsCommand.CanExecute(null));

        hold.Release();
        await read;
        Assert.Equal(PageStatusKind.Idle, document.Status.Current.Kind);
        Assert.Equal(document.LdapsStatusText, document.Status.Current.Message);
    }

    [Fact]
    public async Task EveryLogCopy_OfTheDocumentAndItsTabs_IsAKeyWithCountsAndACode_NeverANameOrAServer()
    {
        var h = await PkiStatusHarness.CreateAsync();
        h.H.TlsProbe.Set(PkiSamples.DcHost, 636, TlsProbeOutcome.Ok, PkiSamples.DcLdapsDer(), PkiSamples.IssuingCaDer);
        using var document = h.Overview();
        var logs = new[] { new LoggedStatus(document.Status), new LoggedStatus(document.HealthStatus), new LoggedStatus(document.LdapsStatus) };

        await document.LoadAsync();
        document.SelectedTab = PkiOverviewTab.Health;
        await document.CheckHttpCommand.ExecuteAsync(null);
        document.SelectedTab = PkiOverviewTab.Ldaps;
        await B2Harness.WaitUntilAsync(() => document.HasLdapsRows && !document.IsProbingLdaps);
        h.Cache.OnSnapshot = (_, _) => throw new DirectoryException(DirectoryErrorKind.ServerUnavailable, "down");
        await document.RefreshAsync();

        var all = logs.SelectMany(l => l.Logs).Where(log => log.Length > 0).ToList();
        Assert.NotEmpty(all);
        Assert.All(all, log => Assert.Matches(@"^(Pki|PkiDoc|PkiOverview)\.[A-Za-z.]+( \([0-9, ]+\))?( \[[A-Za-z0-9 ,]+\])?$", log));
        Assert.DoesNotContain(all, log => log.Contains("Kontoso", StringComparison.Ordinal) || log.Contains("contoso", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task TheStatusBarOfTheMainWindow_SaysNothingOfAPkiTab_ThatHasItsOwnLine()
    {
        var h = await PkiHarness.CreateAsync();
        using var shell = h.CreateShell();
        var overview = Assert.IsType<PkiOverviewDocumentViewModel>(shell.OpenPkiDocument(new PkiDocumentRequest(PkiDocumentKind.Overview)));
        await B2Harness.WaitUntilAsync(() => !overview.IsLoading && overview.Snapshot is not null);

        // The summary and the selection are the line's: the bar of the window does not say them a second time.
        Assert.NotEmpty(overview.StatusText);
        Assert.Equal(string.Empty, shell.StatusBarText);
        Assert.Equal(string.Empty, shell.StatusBarSelectionText);
        foreach (var kind in new[] { PkiDocumentKind.Templates, PkiDocumentKind.SecurityReview, PkiDocumentKind.Readiness })
        {
            var document = shell.OpenPkiDocument(new PkiDocumentRequest(kind));
            Assert.Same(document, shell.SelectedTab);
            Assert.IsAssignableFrom<IHasPageStatus>(document);
            Assert.Equal(string.Empty, shell.StatusBarText);
        }
    }

    private static string Date(PkiOverviewDocumentViewModel document) =>
        document.ReadAtText.Substring(document.ReadAtText.IndexOf(", ", StringComparison.Ordinal) + 2).TrimEnd('.');
}
