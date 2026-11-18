using System.Globalization;
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
/// What "Gotowość na silne mapowanie i NTAuth" says in its status line (track V, stage V2b-4, unit 3) over the Polish PKI sample:
/// the summary - what the rows found, then when and where the report was read -, the read with its stages and Stop, the areas it
/// did not reach saying so when it is stopped, and the probe of the domain controllers - the one action of the report that
/// connects to them - as the operation of the tab with Stop. What each area finds is covered by <see cref="PkiReadinessTests"/>.
/// </summary>
public sealed class PkiReadinessStatusTests
{
    private static readonly CancellationToken Token = TestContext.Current.CancellationToken;

    private const string Reading = "Wczytywanie „Gotowość na silne mapowanie i NTAuth”";

    private static PkiReadinessGroupViewModel Area(PkiReadinessDocumentViewModel document, PkiReadinessArea area) =>
        document.Groups.Single(g => g.Area == area);

    [Fact]
    public async Task TheSummary_SaysWhatTheRowsFound_ThenWhenAndWhere_AndFollowsALanguageSwitch()
    {
        var h = await PkiStatusHarness.CreateAsync();
        using var document = h.Readiness();
        var seen = new StatusRecorder(document.Status);
        Assert.IsAssignableFrom<IHasPageStatus>(document);
        Assert.Equal((PageStatusKind.Idle, "Jeszcze nie odczytano."), (document.Status.Current.Kind, document.Status.Current.Message));

        await document.LoadAsync();

        var summary = document.Status.Current;
        Assert.Equal(PageStatusKind.Idle, summary.Kind);
        Assert.Equal("OK: 4 · Ostrzeżenia: 3 · Problemy: 3 · Nie sprawdzono: 2 · " + document.LastRunText, summary.Message);
        Assert.Matches(@"^OK: 4 · .+ · Odczytano: .+ na dc01\.contoso\.test$", summary.Message);
        Assert.Null(document.Status.Lasting);
        Assert.False(document.Status.CloseCommand.CanExecute(null));
        Assert.Equal(string.Empty, document.ProgressText);

        h.H.B2.Localizer.SetCulture(CultureInfo.GetCultureInfo("en-US"));
        Assert.Matches(@"^OK: 4 · Warnings: 3 · Problems: 3 · Not checked: 2 · Read: .+ on dc01\.contoso\.test$", document.Status.Current.Message);
        Assert.DoesNotContain(seen.Seen, said => said.Kind is PageStatusKind.Error or PageStatusKind.Attention);
    }

    [Fact]
    public async Task TheRead_SaysItsStages_TheAreasThatAreBeingReadSayReading_AndTheRowsAreTheResult()
    {
        var h = await PkiStatusHarness.CreateAsync();
        var hold = new PkiHold();
        h.Cache.OnExpansion = async (progress, ct) =>
        {
            progress?.Report(new PkiExpansionProgress(300));
            await hold.HoldAsync(ct);
        };
        using var document = h.Readiness();
        var seen = new StatusRecorder(document.Status);

        var load = document.LoadAsync();
        await hold.Reached.WaitAsync(Token);

        var working = document.Status.Current;
        Assert.Equal((PageStatusKind.Working, $"{Reading} – konta: 300…"), (working.Kind, working.Message));
        Assert.Equal("PkiDoc.Status.Reading (300)", working.Log.ToString());
        Assert.Same(document.Status.CancelCommand, working.Action!.Command);
        Assert.True(document.IsLoading);
        Assert.True(document.IsBusy);

        // The area that waits for the accounts says so in its rows; the ones that were read are shown.
        Assert.Equal(h.H.B2.Localizer[L.PkiReadiness.Reading], Area(document, PkiReadinessArea.Mappings).AllRows[0].Detail);
        Assert.NotEqual(h.H.B2.Localizer[L.PkiReadiness.Reading], Area(document, PkiReadinessArea.NtAuth).AllRows[0].Detail);

        hold.Release();
        await load;

        // The title, then the stages in order: the objects (as many reports as the reader made), the domain controllers, the accounts.
        var plain = seen.Plain.Where(p => p.Kind == PageStatusKind.Working).Select(p => p.Message).ToList();
        Assert.Equal($"{Reading}…", plain[0]);
        Assert.Equal($"{Reading} – obiekty AD CS: 0…", plain[1]);
        var controllers = plain.IndexOf($"{Reading} – odczyt kontrolerów domeny…");
        var accounts = plain.IndexOf($"{Reading} – konta: 0…");
        var counted = plain.IndexOf($"{Reading} – konta: 300…");
        Assert.True(1 < controllers && controllers < accounts && accounts < counted, string.Join(" | ", plain));
        Assert.All(plain.Take(controllers).Skip(1), message => Assert.StartsWith($"{Reading} – obiekty AD CS: ", message, StringComparison.Ordinal));
        Assert.All(plain.Skip(accounts), message => Assert.StartsWith($"{Reading} – konta: ", message, StringComparison.Ordinal));
        Assert.Equal(PageStatusKind.Idle, document.Status.Current.Kind);
        Assert.False(document.IsBusy);
    }

    [Fact]
    public async Task Stop_ReturnsControlAtOnce_TheAreasItDidNotReachSayNotRead_AndTheStoppedReadLastsWithItsAction()
    {
        var h = await PkiStatusHarness.CreateAsync();
        var hold = new PkiHold();
        h.Cache.OnExpansion = (_, ct) => hold.HoldAsync(ct);
        using var document = h.Readiness();
        var seen = new StatusRecorder(document.Status);

        var load = document.LoadAsync();
        await hold.Reached.WaitAsync(Token);
        document.Status.Current.Action!.Command.Execute(null);
        Assert.Equal((PageStatusKind.Working, "Zatrzymywanie…"), (document.Status.Current.Kind, document.Status.Current.Message));
        Assert.True(document.Status.IsCancelling);
        hold.Release();
        await load;

        Assert.Equal((PageStatusKind.Attention, $"{Reading} zatrzymano: karta może nie pokazywać wszystkiego. Wybierz „Odczytaj ponownie”, aby wczytać ją ponownie."), seen.Plain[^1]);
        var stopped = document.Status.Current;
        Assert.Same(document.Status.Lasting, stopped);
        Assert.False(document.Status.CloseCommand.CanExecute(null));
        Assert.Equal("Odczytaj ponownie", stopped.Action!.Label);
        Assert.Same(document.RefreshCommand, stopped.Action.Command);
        Assert.False(document.IsLoading);

        // What it did not reach does not say "reading…" any more.
        Assert.Equal(h.H.B2.Localizer[L.PkiReadiness.NeverRun], Area(document, PkiReadinessArea.Mappings).AllRows[0].Detail);

        h.Cache.OnExpansion = null;
        stopped.Action.Command.Execute(null);
        await B2Harness.WaitUntilAsync(() => !document.IsLoading && document.Status.Current.Kind == PageStatusKind.Idle);
        Assert.Null(document.Status.Lasting);
        Assert.Equal(PkiReadinessStatus.Problem, Area(document, PkiReadinessArea.Mappings).Status);
    }

    [Fact]
    public async Task TheLdapsProbe_IsTheOperationOfTheTab_WithStop_AndTheAreaKeepsWhatItShowedWhenItIsStopped()
    {
        var h = await PkiStatusHarness.CreateAsync();
        h.H.TlsProbe.Set(PkiSamples.DcHost, 636, TlsProbeOutcome.Ok, PkiSamples.DcLdapsDer(), PkiSamples.IssuingCaDer);
        using var document = h.Readiness();
        await document.LoadAsync();
        var seen = new StatusRecorder(document.Status);
        Assert.Empty(h.H.TlsProbe.Calls);
        Assert.Equal(PkiReadinessStatus.NotChecked, Area(document, PkiReadinessArea.DomainControllerLdaps).Status);
        var hold = new PkiHold();
        h.Cache.OnDcCertificates = hold.HoldAsync;

        var probe = document.CheckLdapsCommand.ExecuteAsync(null);
        await hold.Reached.WaitAsync(Token);

        // The probe of the domain controllers says what it does, with Stop; a read cannot start meanwhile, and the probe cannot start
        // while the report is read.
        var working = document.Status.Current;
        Assert.Equal((PageStatusKind.Working, "Sprawdzanie certyfikatów LDAPS kontrolerów domeny…"), (working.Kind, working.Message));
        Assert.Equal("PkiDoc.Status.LdapsChecking", working.Log.ToString());
        Assert.Same(document.Status.CancelCommand, working.Action!.Command);
        Assert.True(document.IsCheckingLdaps);
        Assert.False(document.IsLoading);
        Assert.False(document.CheckLdapsCommand.CanExecute(null));

        working.Action.Command.Execute(null);
        Assert.Equal("Zatrzymywanie…", document.Status.Current.Message);
        hold.Release();
        await probe;

        var stopped = document.Status.Current;
        Assert.Equal(PageStatusKind.Attention, stopped.Kind);
        Assert.Equal(
            "Sprawdzanie certyfikatów LDAPS kontrolerów domeny zatrzymano: wyników nie zmieniono. Wybierz „Sprawdź certyfikaty LDAPS”, aby sprawdzić ponownie.",
            stopped.Message);
        Assert.Same(document.CheckLdapsCommand, stopped.Action!.Command);
        Assert.True(document.Status.CloseCommand.CanExecute(null));
        Assert.False(document.IsCheckingLdaps);
        Assert.Equal(PkiReadinessStatus.NotChecked, Area(document, PkiReadinessArea.DomainControllerLdaps).Status);
        Assert.Empty(h.H.TlsProbe.Calls);

        // Again: nothing is said but the result, which is the area and the summary.
        h.Cache.OnDcCertificates = null;
        await document.CheckLdapsCommand.ExecuteAsync(null);
        Assert.Equal(PageStatusKind.Idle, document.Status.Current.Kind);
        Assert.Equal($"{document.SummaryText} · {document.LastRunText}", document.Status.Current.Message);
        Assert.Equal(PkiReadinessStatus.Ok, Area(document, PkiReadinessArea.DomainControllerLdaps).Status);
        Assert.Contains(seen.Seen, said => said.Kind == PageStatusKind.Working && said.Message == "Sprawdzanie certyfikatów LDAPS kontrolerów domeny…");
    }

    [Fact]
    public async Task AProbeThatCannotBeMade_IsAnErrorWithRetry_AndTheAreaSaysWhy_AReadReplacesAProbeWithoutAWord()
    {
        var h = await PkiStatusHarness.CreateAsync();
        using var document = h.Readiness();
        await document.LoadAsync();
        var logs = new LoggedStatus(document.Status);
        h.Cache.OnDcCertificates = (_) => throw new IOException("the firewall");

        await document.CheckLdapsCommand.ExecuteAsync(null);

        var error = document.Status.Current;
        Assert.Equal(PageStatusKind.Error, error.Kind);
        Assert.StartsWith("Nie udało się sprawdzić certyfikatów LDAPS: ", error.Message, StringComparison.Ordinal);
        Assert.EndsWith(" Wybierz Ponów, aby spróbować ponownie. (IOException)", error.Message, StringComparison.Ordinal);
        Assert.Equal("PkiDoc.Status.LdapsFailed [IOException]", error.Log.ToString());
        Assert.True(document.Status.CloseCommand.CanExecute(null));
        Assert.Same(document.CheckLdapsCommand, error.Action!.Command);
        Assert.False(document.IsCheckingLdaps);
        var reason = h.H.B2.Errors.Translate(new IOException("the firewall"));
        Assert.Contains(reason.TrimEnd('.'), error.Message, StringComparison.Ordinal);
        Assert.Contains(
            Area(document, PkiReadinessArea.DomainControllerLdaps).AllRows,
            row => row.Status == PkiReadinessStatus.NotChecked && row.Detail.Contains(reason, StringComparison.Ordinal));
        Assert.DoesNotContain(logs.Logs, log => log.Contains("firewall", StringComparison.Ordinal));

        // A probe that is on its way when the report is read again is of no interest any more: replaced, and the read says its own.
        var hold = new PkiHold();
        h.Cache.OnDcCertificates = hold.HoldAsync;
        var probe = document.CheckLdapsCommand.ExecuteAsync(null);
        await hold.Reached.WaitAsync(Token);
        var seen = new StatusRecorder(document.Status);
        h.Cache.OnDcCertificates = null;
        var read = document.RefreshAsync();
        hold.Release();
        await Task.WhenAll(probe, read);

        Assert.False(document.IsCheckingLdaps);
        Assert.False(document.IsLoading);
        Assert.DoesNotContain(seen.Seen, said => said.Kind is PageStatusKind.Attention or PageStatusKind.Error);
        Assert.Equal(PageStatusKind.Idle, document.Status.Current.Kind);
        Assert.Null(document.Status.Lasting);
    }

    [Fact]
    public async Task NotConnected_IsAnErrorThatLasts_AndNothingCanBeRead_OrProbed()
    {
        var h = await PkiStatusHarness.CreateAsync();
        using var document = h.Readiness();
        await document.LoadAsync();

        await h.H.B2.Sessions.DisconnectAsync();
        await B2Harness.WaitUntilAsync(() => document.Status.Current.Kind == PageStatusKind.Error);

        Assert.Equal(h.H.B2.Localizer[L.Errors.NotConnected], document.Status.Current.Message);
        Assert.False(document.Status.CloseCommand.CanExecute(null));
        Assert.False(document.CheckLdapsCommand.CanExecute(null));
        Assert.False(document.HasResult);
        h.H.B2.Localizer.SetCulture(CultureInfo.GetCultureInfo("en-US"));
        Assert.Equal(new ResxLocalizer(CultureInfo.GetCultureInfo("en-US"))[L.Errors.NotConnected], document.Status.Current.Message);
    }

    [Fact]
    public async Task EveryLogCopy_OfTheReport_IsAKeyWithCountsAndACode_NeverAnAreaARowOrAServer()
    {
        var h = await PkiStatusHarness.CreateAsync();
        h.H.TlsProbe.Set(PkiSamples.DcHost, 636, TlsProbeOutcome.Ok, PkiSamples.DcLdapsDer(), PkiSamples.IssuingCaDer);
        using var document = h.Readiness();
        var logs = new LoggedStatus(document.Status);

        await document.LoadAsync();
        await document.CheckLdapsCommand.ExecuteAsync(null);
        h.Cache.OnDcCertificates = (_) => throw new IOException("the firewall");
        await document.CheckLdapsCommand.ExecuteAsync(null);

        var all = logs.Logs.Where(log => log.Length > 0).ToList();
        Assert.NotEmpty(all);
        Assert.All(all, log => Assert.Matches(@"^PkiDoc\.[A-Za-z.]+( \([0-9, ]+\))?( \[[A-Za-z0-9 ,]+\])?$", log));
    }
}
