using AdMng.Core.EventLogs;
using AdMng.Core.EventLogs.Analysis;
using AdMng.Presentation.EventLogs;
using AdMng.Presentation.Localization;
using AdMng.Testing.EventLogs;

namespace AdMng.Presentation.Tests.EventLogs;

public class FailedLogonsDocumentTests
{
    /// <summary>The samples' failed logons; the enumeration threshold lowered to 3 (the samples try four unknown names).</summary>
    private static async Task<(EventLogHarness Harness, FailedLogonsDocumentViewModel Document)> AnalyzeAsync(Action<FailedLogonsDocumentViewModel>? arrange = null)
    {
        var harness = await EventLogHarness.CreateAsync();
        harness.Connector.Add(EventLogSamples.FailedLogonScenario());
        var document = harness.Services.CreateFailedLogons();
        document.EnumerationThreshold = 3;
        arrange?.Invoke(document);
        await document.RunAsync();
        return (harness, document);
    }

    [Fact]
    public async Task Run_ReadsFailuresThenTheCandidatesSuccesses_OnEveryDc()
    {
        var (harness, document) = await AnalyzeAsync();

        Assert.Null(document.Error);
        Assert.True(document.HasReport);
        var step1 = harness.Connector.Reads.Where(r => r.Query.Outcome == EventAuditOutcome.Failure).ToList();
        Assert.Equal(2, step1.Count);
        Assert.All(step1, r => Assert.Equal([4625, 4768, 4771, 4776], r.Query.EventIds.Order()));
        var step2 = harness.Connector.Reads.Where(r => r.Query.EventIds.Contains(4624)).ToList();
        Assert.Equal(2, step2.Count);
        Assert.Contains(step2[0].Query.Data, d => d.Values.Contains(EventLogSamples.ZofiaZak));
        Assert.Equal([EventLogHarness.Dc01, EventLogHarness.Dc02], document.Hosts.Select(h => h.Host));
        Assert.StartsWith("Niepowodzenia: ", document.SummaryText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Findings_ShowSprayingGuessingAndEnumeration_WithChips()
    {
        var (_, document) = await AnalyzeAsync();

        var kinds = document.Findings.Select(f => f.Kind).ToHashSet();
        Assert.Contains(FailedLogonFindingKind.PasswordSpray, kinds);
        Assert.Contains(FailedLogonFindingKind.BruteForce, kinds);
        Assert.Contains(FailedLogonFindingKind.AccountEnumeration, kinds);
        var spray = Assert.Single(document.Findings, f => f.Kind == FailedLogonFindingKind.PasswordSpray);
        Assert.Equal("Adres 10.99.0.66", spray.SourceText);
        Assert.Equal("Rozpylanie haseł", spray.KindText);
        Assert.StartsWith("Wiele kont z jednego źródła", spray.Explanation, StringComparison.Ordinal);
        Assert.Equal(document.Findings.Count, document.Chips.Sum(c => c.Count));
        Assert.Equal(4, document.Chips.Count);
        Assert.NotEmpty(document.TopAccounts);
        Assert.NotEmpty(document.TopSources);
        Assert.Contains(document.CodeCounts, c => c.Contains("0x18 – wstępne uwierzytelnianie nieudane (złe hasło)", StringComparison.Ordinal));
        Assert.NotEmpty(document.AuditRows);
        Assert.Contains(document.Notes, n => n.Text.StartsWith("Ustalenia według adresu", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Filters_ChipsAndSearch_NarrowTheList()
    {
        var (_, document) = await AnalyzeAsync();
        var all = document.Findings.Count;

        document.SelectedKind = document.KindOptions.Single(o => o.Value == FailedLogonFindingKind.BruteForce);
        Assert.All(document.Findings, f => Assert.Equal(FailedLogonFindingKind.BruteForce, f.Kind));
        Assert.True(document.IsFiltered);

        document.ClearFiltersCommand.Execute(null);
        Assert.Equal(all, document.Findings.Count);

        var chip = document.Chips.First(c => c.HasFindings);
        chip.IsSelected = true;
        Assert.Equal(chip.Severity, document.SelectedSeverity.Value);
        Assert.All(document.Findings, f => Assert.Equal(chip.Severity, f.Severity));

        document.ClearFiltersCommand.Execute(null);
        document.SearchText = "10.99.0.66";
        Assert.Equal(FailedLogonFindingKind.PasswordSpray, Assert.Single(document.Findings).Kind);
        document.SearchText = "nie ma";
        Assert.Empty(document.Findings);
        Assert.Equal("Żadne ustalenie nie spełnia filtrów.", document.ListNote);
    }

    [Fact]
    public async Task DrillDown_ShowsTheRecordsOfTheFinding_AndUnknownNamesMasked()
    {
        var (harness, document) = await AnalyzeAsync();

        document.SelectedFinding = Assert.Single(document.Findings, f => f.Kind == FailedLogonFindingKind.PasswordSpray);
        Assert.Equal(10, document.SelectedRecords.Count);
        Assert.All(document.SelectedRecords, r => Assert.Equal(4771, r.EventId));
        Assert.Equal("Zdarzenia (10)", document.RecordsHeading);
        Assert.Equal(10, document.SelectedAccounts.Count);

        var enumeration = Assert.Single(document.Findings, f => f.Kind == FailedLogonFindingKind.AccountEnumeration);
        document.SelectedFinding = enumeration;
        Assert.Contains("••••••", enumeration.AccountsText, StringComparison.Ordinal);
        var masked = document.SelectedAccounts.First(a => a.CanReveal);
        Assert.StartsWith("••••••", masked.Text, StringComparison.Ordinal);
        document.RevealAccountCommand.Execute(masked);
        Assert.StartsWith(masked.Account.Account, masked.Text, StringComparison.Ordinal);
        Assert.False(masked.CanReveal);
        Assert.Equal(harness.B2.Localizer[L.EventLogsFailedLogons.Title], document.Title);
    }

    [Fact]
    public async Task Thresholds_AreUsed_AndDcAddressesAreNeverFindings()
    {
        var (_, document) = await AnalyzeAsync(d => d.BruteForceThreshold = 50);

        Assert.DoesNotContain(document.Findings, f => f.Kind == FailedLogonFindingKind.BruteForce);
        var options = document.Options(["dc01.contoso.test", "10.10.0.1"]);
        Assert.Equal(50, options.BruteForceThreshold);
        Assert.Equal(["dc01.contoso.test", "10.10.0.1"], options.DomainControllers);
        document.SprayWindowMinutes = 0;
        Assert.Equal(TimeSpan.FromMinutes(1), document.Options([]).SprayWindow);
    }

    /// <summary>
    /// E-AN-04/E-PRES-02: step 2 reads the candidates' successes only, so the success audit evidence comes from unfiltered
    /// one-record probes: another account's logon on DC01 proves the Logon success audit there.
    /// </summary>
    [Fact]
    public async Task SuccessAuditEvidence_ComesFromUnfilteredProbes()
    {
        var harness = await EventLogHarness.CreateAsync();
        harness.Connector.Add(EventLogSamples.FailedLogonScenario());
        harness.Connector.Add(EventLogSamples.LogonSucceeded(EventLogSamples.Dc01, 21_000, EventLogSamples.Morning.AddMinutes(90),
            EventLogSamples.LukaszWisniewski, "10.10.1.30", "WAW-PC009"));
        var now = harness.B2.Time.GetUtcNow();
        foreach (var host in new[] { EventLogHarness.Dc01, EventLogHarness.Dc02 })
        {
            harness.Connector.SetCoverage(host, "Security",
                new EventLogCoverage { Host = host, Channel = "Security", Oldest = now.AddDays(-2), Mode = EventLogMode.Circular });
        }

        var document = harness.Services.CreateFailedLogons();
        await document.RunAsync();

        var probes = harness.Connector.Reads.Where(r => r.Query.MaxRecords == 1).ToList();
        Assert.Equal(3, probes.Count);
        Assert.All(probes, p => Assert.Equal((0, (EventAuditOutcome?)EventAuditOutcome.Success), (p.Query.Data.Count, p.Query.Outcome)));
        var states = document.AuditRows
            .Where(a => a.Entry.Requirement.Outcome == EventAuditOutcome.Success)
            .ToDictionary(a => (a.Host.ToLowerInvariant(), a.Entry.Requirement.EventId), a => a.State);
        Assert.Equal(AuditEvidenceState.Seen, states[(EventLogHarness.Dc01, 4624)]);
        Assert.Equal(AuditEvidenceState.NotSeenWhileCovered, states[(EventLogHarness.Dc01, 4768)]);
        Assert.Equal(AuditEvidenceState.Seen, states[(EventLogHarness.Dc02, 4768)]);
        Assert.Equal(AuditEvidenceState.NotSeenWhileCovered, states[(EventLogHarness.Dc02, 4624)]);
        // The probe's record is evidence only, not a success of the analysis.
        Assert.Equal(1, document.Report!.SuccessCount);
    }

    /// <summary>E-AN-02: dc01 is the PDC emulator – its 4776 copies of dc02's NTLM failures are the same attempts, not a brute force.</summary>
    [Fact]
    public async Task NtlmRetriesAtThePdcEmulator_AreNotABruteForce()
    {
        var harness = await EventLogHarness.CreateAsync();
        for (var i = 0; i < 6; i++)
        {
            var at = EventLogSamples.Morning.AddHours(2).AddMinutes(i);
            harness.Connector.Add(
                EventLogSamples.CredentialValidation(EventLogSamples.Dc02, 30_000 + i, at, EventLogSamples.MalgorzataWojcik, "WAW-PC009"),
                EventLogSamples.CredentialValidation(EventLogSamples.Dc01, 31_000 + i, at.AddSeconds(1), EventLogSamples.MalgorzataWojcik, "WAW-PC009"));
        }

        var retried = harness.Services.CreateFailedLogons();
        await retried.RunAsync();

        Assert.True(retried.HasReport);
        Assert.DoesNotContain(retried.Findings, f => f.Kind == FailedLogonFindingKind.BruteForce);
        Assert.Equal(6, retried.Report!.FailureCount);
    }

    [Fact]
    public async Task Export_WritesTheFindings_WithThePersonalDataNote_AndMaskedNames()
    {
        var (harness, document) = await AnalyzeAsync();
        var path = Path.Combine(Path.GetTempPath(), "admng-test-" + Guid.NewGuid().ToString("N") + ".csv");
        try
        {
            harness.B2.Files.NextSavePath = path;
            await document.ExportCsvCommand.ExecuteAsync(null);
            var text = await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken);
            Assert.Contains(harness.B2.Localizer[L.EventLogs.PersonalDataNote], text, StringComparison.Ordinal);
            Assert.Contains("Rozpylanie haseł", text, StringComparison.Ordinal);
            Assert.DoesNotContain("administrator1", text, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
