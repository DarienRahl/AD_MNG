using AdMng.Core.Csv;
using AdMng.Presentation.Navigation;
using AdMng.Presentation.Readiness;
using AdMng.Presentation.Services;
using AdMng.Presentation.Shell;
using AdMng.Presentation.Tests.Connections;
using Microsoft.Extensions.DependencyInjection;

namespace AdMng.Presentation.Tests.Readiness;

/// <summary>
/// The "Gotowość środowiska" tab: the entry points (Narzędzia, Pomoc, palette <c>tools.readiness</c>; one tab per window), the
/// quick check when it opens, the full check with the picked test computer, cancel, the stale bar, the results published to
/// <see cref="IReadinessResults"/> as value-free code/status pairs, copy and the HTML/CSV exports (escaped; they name hosts).
/// </summary>
public sealed class ReadinessDocumentTests
{
    private static readonly CancellationToken Token = TestContext.Current.CancellationToken;

    private static ShellViewModel CreateShell(ReadinessHarness harness, ReadinessServices? services = null)
    {
        var b2 = harness.B2;
        var readiness = services ?? harness.Services();
        return new ShellViewModel(
            b2.Lists,
            new NavigationTreeViewModel(b2.Sessions, b2.Settings, b2.Localizer, b2.Dispatcher, b2.Errors, b2.Views, b2.Changes),
            b2.Activities,
            b2.Coordinator,
            b2.Views,
            b2.Dialogs,
            b2.Files,
            b2.Clipboard,
            b2.Notifications,
            new CsvCodec(),
            b2.Regional,
            readinessFactory: () => new ReadinessDocumentViewModel(readiness));
    }

    [Fact]
    public async Task Shell_OffersThePaletteCommand_OpensOneTab_AndRunsTheQuickCheck()
    {
        var harness = await ReadinessHarness.CreateAsync();
        using var shell = CreateShell(harness);

        var command = shell.CommandPalette.Find("tools.readiness");
        Assert.NotNull(command);
        Assert.Equal("Sprawdź gotowość środowiska…", harness.B2.Localizer[command!.TitleKey]);
        Assert.True(shell.IsReadinessAvailable);
        Assert.True(shell.ShowReadinessCommand.CanExecute(null));
        shell.ShowReadinessCommand.Execute(null);
        var document = Assert.IsType<ReadinessDocumentViewModel>(shell.SelectedTab);
        await document.Running!;
        shell.ShowReadinessCommand.Execute(null);

        Assert.Same(document, Assert.Single(shell.Documents.OfType<ReadinessDocumentViewModel>()));
        Assert.Same(document, shell.SelectedTab);
        Assert.Equal("Gotowość środowiska", document.Title);
        Assert.Equal(ReadinessDepth.Quick, document.LastDepth);
        Assert.Equal(Enum.GetValues<ReadinessArea>(), document.Groups.Select(g => g.Area));
        Assert.Equal("Połączenie", document.Groups[0].Title);
        Assert.Equal(ReadinessStatus.Ok, document.Groups[0].Status);
        Assert.Equal("OK: 6", document.Groups[0].SummaryText);
        Assert.StartsWith("Ostatnie sprawdzenie: szybkie, ", document.LastRunText, StringComparison.Ordinal);
        Assert.EndsWith(", na dc01.contoso.test", document.LastRunText, StringComparison.Ordinal);
        Assert.Matches(@"^OK: \d+ · Ostrzeżenia: 1 · Błędy: 0 · Nie sprawdzono: \d+$", document.SummaryText);
        Assert.Equal(document.SummaryText, shell.StatusText);
        Assert.False(document.IsLoading);
        Assert.Equal(ReadinessDepth.Quick, harness.Results.LastDepth);
    }

    [Fact]
    public async Task Shell_WithoutTheServices_OffersNothing_AndADisconnectedTabWaits()
    {
        var harness = await ReadinessHarness.CreateAsync();
        var b2 = harness.B2;
        using var plain = b2.CreateShell();
        Assert.False(plain.IsReadinessAvailable);
        Assert.False(plain.ShowReadinessCommand.CanExecute(null));
        Assert.Null(plain.OpenReadiness());

        await b2.Sessions.DisconnectAsync();
        using var shell = CreateShell(harness);
        var document = shell.OpenReadiness()!;

        Assert.Null(document.Running);
        Assert.False(document.HasSession);
        Assert.False(document.QuickCheckCommand.CanExecute(null));
        Assert.False(document.FullCheckCommand.CanExecute(null));
        Assert.Equal("Brak połączenia z domeną.", document.StatusText);
        await document.CheckAsync(ReadinessDepth.Full);
        Assert.Empty(document.Groups);
        Assert.Empty(harness.Results.Last);
    }

    [Fact]
    public async Task FullCheck_WithThePickedComputer_PublishesValueFreeCodePairs()
    {
        var harness = await ReadinessHarness.CreateAsync();
        var changed = 0;
        harness.Results.Changed += (_, _) => changed++;
        using var document = new ReadinessDocumentViewModel(harness.Services());
        harness.B2.Dialogs.PickedObjects.Enqueue([harness.Company.Computer1]);

        await document.PickTestComputerCommand.ExecuteAsync(null);
        await document.FullCheckCommand.ExecuteAsync(null);

        Assert.Equal("WAW-PC001", document.TestComputerText);
        Assert.Equal(ReadinessHarness.TestComputer, harness.Remote.Calls[0].Host);
        Assert.Equal(1, changed);
        IReadinessResults results = harness.Results;
        Assert.Equal(ReadinessDepth.Full, results.LastDepth);
        Assert.False(results.LastCancelled);
        Assert.Equal(harness.B2.Time.GetLocalNow(), results.LastRunAt);

        // One pair per code in check order; a per-DC check has the worst status of its DCs (dc02's short log: a warning).
        Assert.Equal(ReadinessCodes.All.Select(c => c.Code), results.Last.Select(p => p.Code));
        Assert.Contains((ReadinessCodes.EventLogsCoverage, ReadinessStatus.Warning), results.Last);
        Assert.Contains((ReadinessCodes.EventLogsSession, ReadinessStatus.Ok), results.Last);
        Assert.Contains((ReadinessCodes.RemoteCimSession, ReadinessStatus.Ok), results.Last);

        // Value-free: no host, account or domain in what the problem report reads.
        var text = string.Join(";", results.Last.Select(p => $"{p.Code}={p.Status}"));
        Assert.DoesNotContain("contoso", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("waw-pc001", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("dc0", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task TypedText_ReplacesThePickedComputer()
    {
        var harness = await ReadinessHarness.CreateAsync();
        harness.Resolver.Add("krk-lt001.contoso.test", "10.20.5.40");
        harness.Remote.Computers["krk-lt001.contoso.test"] = new Testing.FakeRemoteComputer();
        using var document = new ReadinessDocumentViewModel(harness.Services());
        harness.B2.Dialogs.PickedObjects.Enqueue([harness.Company.Computer1]);
        await document.PickTestComputerCommand.ExecuteAsync(null);

        document.TestComputerText = "KRK-LT001";
        await document.FullCheckCommand.ExecuteAsync(null);

        Assert.Equal("krk-lt001.contoso.test", harness.Remote.Calls[0].Host);
    }

    [Fact]
    public async Task Cancel_StopsTheRun_TheUnreachedChecksAreNotChecked_AndTheResultsSaySo()
    {
        var harness = await ReadinessHarness.CreateAsync();
        var reading = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.Connector.BeforeRead = async (_, _, cancellationToken) =>
        {
            reading.TrySetResult();
            await Task.Delay(Timeout.Infinite, cancellationToken);
        };
        using var document = new ReadinessDocumentViewModel(harness.Services());

        var run = document.CheckAsync(ReadinessDepth.Full);
        await reading.Task.WaitAsync(TimeSpan.FromSeconds(10), Token);
        Assert.True(document.IsLoading);
        Assert.True(document.CancelCommand.CanExecute(null));
        Assert.False(document.FullCheckCommand.CanExecute(null));
        Assert.StartsWith("Sprawdzanie: Dzienniki zdarzeń (E) (5 z 8)", document.ProgressText, StringComparison.Ordinal);
        document.CancelCommand.Execute(null);
        await run;

        Assert.False(document.IsLoading);
        Assert.True(document.WasCancelled);
        var report = document.Report!;
        Assert.True(report.Cancelled);
        Assert.Equal(ReadinessCodes.All.Select(c => c.Code), report.Results.Select(r => r.Code).Distinct());
        Assert.All(report.Results.Where(r => r.Area >= ReadinessArea.EventLogs), r =>
        {
            Assert.Equal(ReadinessStatus.NotChecked, r.Status);
            Assert.Equal("Nie sprawdzono: sprawdzanie anulowano.", r.Explanation);
        });
        Assert.Equal(ReadinessStatus.Ok, report.Status(ReadinessCodes.SysvolReachable));
        Assert.True(harness.Results.LastCancelled);
        Assert.Equal(0, harness.Connector.OpenReaders);
        Assert.Equal(0, harness.Outlook.Connections);
        Assert.Empty(harness.Watchdog.Calls.All);
    }

    [Fact]
    public async Task ChangedConnection_MarksTheResultsStale_AndCheckAgainRunsTheSameDepth()
    {
        var harness = await ReadinessHarness.CreateAsync();
        using var document = new ReadinessDocumentViewModel(harness.Services());
        await document.CheckAsync(ReadinessDepth.Quick);
        Assert.False(document.IsStale);

        await harness.ReconnectAsync(ReadinessHarness.Dc02);
        Assert.True(document.IsStale);
        await document.CheckAgainCommand.ExecuteAsync(null);

        Assert.False(document.IsStale);
        Assert.Equal(ReadinessDepth.Quick, document.LastDepth);
        Assert.EndsWith(", na dc02.contoso.test", document.LastRunText, StringComparison.Ordinal);
        Assert.Equal(ReadinessStatus.Warning, document.Groups[0].Status);
    }

    [Fact]
    public async Task ARunOfTheOldConnection_IsStoppedByAReconnect_AndShownAsStale()
    {
        var harness = await ReadinessHarness.CreateAsync();
        var reading = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.Connector.BeforeRead = async (_, _, cancellationToken) =>
        {
            reading.TrySetResult();
            await Task.Delay(Timeout.Infinite, cancellationToken);
        };
        using var document = new ReadinessDocumentViewModel(harness.Services());

        var run = document.CheckAsync(ReadinessDepth.Full);
        await reading.Task.WaitAsync(TimeSpan.FromSeconds(10), Token);
        await harness.ReconnectAsync(ReadinessHarness.Dc02);
        await run;

        Assert.True(document.WasCancelled);
        Assert.True(document.IsStale);
        Assert.EndsWith(", na dc01.contoso.test", document.LastRunText, StringComparison.Ordinal);
        Assert.True(document.CheckAgainCommand.CanExecute(null));
    }

    [Fact]
    public async Task Refresh_RunsTheDepthOfTheShownResults()
    {
        var harness = await ReadinessHarness.CreateAsync();
        using var document = new ReadinessDocumentViewModel(harness.Services());
        await document.CheckAsync(ReadinessDepth.Full);
        var calls = harness.Remote.Calls.Count;

        await ((IShellDocument)document).RefreshAsync();

        Assert.Equal(ReadinessDepth.Full, document.LastDepth);
        Assert.Equal(2 * calls, harness.Remote.Calls.Count);
    }

    [Fact]
    public async Task Rows_ShowTheRemedyAndTheChapter_OnlyWhereSomethingIsToDo()
    {
        var harness = await ReadinessHarness.CreateAsync();
        using var document = new ReadinessDocumentViewModel(harness.Services());
        await document.CheckAsync(ReadinessDepth.Quick);

        var rows = document.Groups.SelectMany(g => g.Rows).ToList();
        var clock = rows.Single(r => r.Code == ReadinessCodes.ConnectionClockSkew);
        var laps = rows.Single(r => r.Code == ReadinessCodes.DirectoryWindowsLaps);

        Assert.Equal("OK", clock.StatusText);
        Assert.False(clock.HasRemedy);
        Assert.False(clock.ShowsDocumentation);
        Assert.Equal("dc01.contoso.test", clock.Host);
        Assert.Equal("Ostrzeżenie", laps.StatusText);
        Assert.StartsWith("Co zrobić: Rozszerz schemat", laps.RemedyText, StringComparison.Ordinal);
        Assert.Equal("Przeczytaj: KOMPUTERY.md, rozdział 2 „Właściwości komputera”", laps.DocumentationText);
        Assert.True(laps.ShowsDocumentation);
        Assert.Equal(ReadinessStatus.Warning, document.Groups.Single(g => g.Area == ReadinessArea.Directory).Status);
        Assert.Equal("Ostrzeżenia: 1 · OK: 4", document.Groups.Single(g => g.Area == ReadinessArea.Directory).SummaryText);
    }

    [Fact]
    public async Task Copy_PutsTheResultsOnTheClipboard()
    {
        var harness = await ReadinessHarness.CreateAsync();
        using var document = new ReadinessDocumentViewModel(harness.Services());
        Assert.False(document.CopyCommand.CanExecute(null));
        await document.CheckAsync(ReadinessDepth.Quick);

        document.CopyCommand.Execute(null);

        var text = harness.B2.Clipboard.Text!;
        Assert.StartsWith("Gotowość środowiska", text, StringComparison.Ordinal);
        Assert.Contains("Katalog – Ostrzeżenie", text, StringComparison.Ordinal);
        Assert.Contains("  [OK] Różnica czasu względem DC (dc01.contoso.test): Zegar tego komputera zgadza się", text, StringComparison.Ordinal);
        Assert.Contains("Przeczytaj: KOMPUTERY.md, rozdział 2 „Właściwości komputera”", text, StringComparison.Ordinal);
        Assert.Contains(harness.B2.Notifications.Items, n => n.Message == "Skopiowano wyniki.");
    }

    [Fact]
    public async Task ExportHtml_IsSelfContained_EscapesEveryValue_AndSaysItNamesHosts()
    {
        var harness = await ReadinessHarness.CreateAsync();
        using var document = new ReadinessDocumentViewModel(harness.Services());
        Assert.False(document.ExportHtmlCommand.CanExecute(null));
        document.TestComputerText = "<b>WAW&PC'1</b>";
        await document.CheckAsync(ReadinessDepth.Full);
        var path = Path.Combine(Path.GetTempPath(), $"admng-readiness-{Guid.NewGuid():N}.html");
        harness.B2.Files.NextSavePath = path;
        try
        {
            await document.ExportHtmlCommand.ExecuteAsync(null);
            var html = await File.ReadAllTextAsync(path, Token);

            Assert.StartsWith("Gotowość środowiska – contoso.test_", harness.B2.Files.LastDefaultFileName, StringComparison.Ordinal);
            Assert.EndsWith(".html", harness.B2.Files.LastDefaultFileName, StringComparison.Ordinal);
            Assert.StartsWith("<!DOCTYPE html>", html.TrimStart('﻿'), StringComparison.Ordinal);
            Assert.Contains("default-src 'none'", html, StringComparison.Ordinal);
            Assert.DoesNotContain("<script", html, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("<b>WAW", html, StringComparison.Ordinal);
            Assert.Contains("Nie znaleziono w domenie komputera &lt;b&gt;WAW&amp;PC&#39;1&lt;/b&gt;.", html, StringComparison.Ordinal);
            Assert.Contains("<h1>Gotowość środowiska – contoso.test</h1>", html, StringComparison.Ordinal);
            Assert.Contains("Eksport zawiera nazwy domeny, kontrolerów domeny i komputera testowego", html, StringComparison.Ordinal);
            Assert.Contains("<code>Sysvol.PoliciesWrite</code>", html, StringComparison.Ordinal);
            Assert.Contains("dc02.contoso.test", html, StringComparison.Ordinal);
            Assert.Contains(harness.B2.Notifications.Items, n => n.Severity == NotificationSeverity.Success && n.Message.EndsWith("przechowuj go jak inne dokumenty inwentaryzacyjne.", StringComparison.Ordinal));
        }
        finally
        {
            File.Delete(path);
        }

        Assert.False(File.Exists(path));
    }

    [Fact]
    public async Task ExportCsv_HasOneRowPerCheck_WithTheCode()
    {
        var harness = await ReadinessHarness.CreateAsync();
        using var document = new ReadinessDocumentViewModel(harness.Services());
        await document.CheckAsync(ReadinessDepth.Full);
        var path = Path.Combine(Path.GetTempPath(), $"admng-readiness-{Guid.NewGuid():N}.csv");
        harness.B2.Files.NextSavePath = path;
        try
        {
            await document.ExportCsvCommand.ExecuteAsync(null);
            var lines = (await File.ReadAllLinesAsync(path, Token)).Select(l => l.TrimStart('﻿')).ToList();

            Assert.Equal("Obszar;Sprawdzenie;Stan;Host;Wynik;Co zrobić;Dokumentacja;Code", lines[0]);
            Assert.Equal(document.Report!.Results.Count, lines.Count - 1);
            Assert.Contains(lines, l => l.StartsWith("Dzienniki zdarzeń (E);Jak daleko wstecz sięga dziennik Zabezpieczenia;Ostrzeżenie;dc02.contoso.test;", StringComparison.Ordinal)
                && l.EndsWith(";EventLogs.Coverage", StringComparison.Ordinal));
            Assert.Contains(lines, l => l.StartsWith("Ten komputer;Środowisko WebView2 (podglądy);OK;;", StringComparison.Ordinal));
        }
        finally
        {
            File.Delete(path);
        }

        Assert.False(File.Exists(path));
    }

    [Fact]
    public async Task CancelledSaveDialog_WritesNothing()
    {
        var harness = await ReadinessHarness.CreateAsync();
        using var document = new ReadinessDocumentViewModel(harness.Services());
        await document.CheckAsync(ReadinessDepth.Quick);
        harness.B2.Files.NextSavePath = null;

        await document.ExportCsvCommand.ExecuteAsync(null);
        await document.ExportHtmlCommand.ExecuteAsync(null);

        Assert.Empty(harness.B2.Notifications.Items);
    }

    [Fact]
    public async Task ResultsAndServices_ArePerConnectionWindow()
    {
        await using var host = new ConnectionScopeHost();
        await using var first = host.CreateConnection();
        await using var second = host.CreateConnection();

        var results = first.Services.GetRequiredService<IReadinessResults>();
        Assert.Same(results, first.Services.GetRequiredService<ReadinessResults>());
        Assert.NotSame(results, second.Services.GetRequiredService<IReadinessResults>());
        Assert.NotSame(first.Services.GetRequiredService<ReadinessServices>(), second.Services.GetRequiredService<ReadinessServices>());
        Assert.Same(first.Services.GetRequiredService<ILocalComputerInfo>(), second.Services.GetRequiredService<ILocalComputerInfo>());
        var factory = first.Services.GetRequiredService<Func<ReadinessDocumentViewModel>>();
        using var a = factory();
        using var b = factory();
        Assert.NotSame(a, b);
        Assert.Empty(results.Last);
        Assert.NotNull(first.Services.GetRequiredService<ReadinessServices>().Watchdog);
    }
}
