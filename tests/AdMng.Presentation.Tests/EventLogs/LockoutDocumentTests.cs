using AdMng.Core.ActiveDirectory;
using AdMng.Core.Batch;
using AdMng.Core.Computers.Remote;
using AdMng.Core.EventLogs;
using AdMng.Core.EventLogs.Analysis;
using AdMng.Presentation.EventLogs;
using AdMng.Presentation.GroupPolicy;
using AdMng.Presentation.Localization;
using AdMng.Testing;
using AdMng.Testing.EventLogs;

namespace AdMng.Presentation.Tests.EventLogs;

public class LockoutDocumentTests
{
    /// <summary>
    /// Jan Kowalski locked out by an old password on WAW-PC001 (the samples' lockout scenario): dc01 (PDC emulator, the
    /// window's DC) wrote lockoutTime and counts 5 bad passwords, dc02 counts 2; dc02's Security log reaches back two days.
    /// </summary>
    private static async Task<(EventLogHarness Harness, LockoutDocumentViewModel Document)> InvestigateAsync(
        string culture = "pl-PL", bool withRemote = true, Action<EventLogHarness>? arrange = null)
    {
        var harness = await EventLogHarness.CreateAsync(culture, withRemote: withRemote);
        var jan = harness.Company.JanKowalski;
        for (var i = 0; i < 5; i++)
        {
            harness.Session.SimulateBadPassword(jan);
        }

        harness.Session.SimulateLockout(jan);
        var now = harness.B2.Time.GetUtcNow();
        harness.Dc02Values[jan] = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [Attr.BadPwdCount] = "2",
            [Attr.BadPasswordTime] = EventLogHarness.FileTime(EventLogSamples.Morning.AddMinutes(33)),
            [Attr.LockoutTime] = EventLogHarness.FileTime(now),
            [Attr.LastLogon] = EventLogHarness.FileTime(EventLogSamples.Morning.AddHours(-20)),
        };
        harness.Connector.Add(EventLogSamples.LockoutScenario());
        harness.Connector.SetCoverage(EventLogHarness.Dc02, "Security", new EventLogCoverage
        {
            Host = EventLogHarness.Dc02,
            Channel = "Security",
            Oldest = now.AddDays(-2),
            RecordCount = 3,
            Mode = EventLogMode.Circular,
        });
        arrange?.Invoke(harness);
        var document = harness.Services.CreateLockout(jan, "Jan Kowalski");
        await document.RunAsync();
        return (harness, document);
    }

    [Fact]
    public async Task Investigation_ReadsEveryDc_PdcAndOriginatingFirst_AndShowsTheSummary()
    {
        var (harness, document) = await InvestigateAsync();

        Assert.Null(document.Error);
        Assert.True(document.HasReport);
        Assert.Equal("Skąd blokada konta – Jan Kowalski", document.Title);
        Assert.True(document.IsLockedNow);
        Assert.StartsWith("Konto jest teraz zablokowane na: DC01", document.LockStateText, StringComparison.OrdinalIgnoreCase);
        Assert.StartsWith("Ostatnia blokada: ", document.LastLockoutText, StringComparison.Ordinal);
        Assert.Equal("Próg blokady: 5 złych haseł w ciągu 30 min; blokada na 30 min (zasady domeny).", document.PolicyText);
        Assert.Equal("Nieudane próby: 5, w tym złe hasła: 5.", document.CountsText);

        Assert.Equal([EventLogHarness.Dc01, EventLogHarness.Dc02], document.Hosts.Select(h => h.Host));
        Assert.Equal(2, harness.Connector.OpenedHosts.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.All(harness.Connector.Reads, r => Assert.Equal("Security", r.Query.Channel));
        Assert.Contains(harness.Connector.Reads, r => r.Query.EventIds.Contains(4740));
        var dc01 = document.DomainControllerRows.Single(r => r.Host == EventLogHarness.Dc01);
        var dc02 = document.DomainControllerRows.Single(r => r.Host == EventLogHarness.Dc02);
        Assert.Contains("emulator PDC", dc01.RolesText, StringComparison.Ordinal);
        Assert.Contains("tu zapisano blokadę", dc01.RolesText, StringComparison.Ordinal);
        Assert.Equal("5", dc01.BadPasswordCountText);
        Assert.Equal("2", dc02.BadPasswordCountText);
        Assert.True(dc02.IsLocked);
        Assert.Equal("blokady: 0, niepowodzenia: 2", dc02.RecordsText);
    }

    [Fact]
    public async Task Sources_AreRanked_WithCodesExplained()
    {
        var (_, document) = await InvestigateAsync();

        var workstation = Assert.Single(document.Sources, s => s.Value == EventLogSamples.WawPc001);
        var address = Assert.Single(document.Sources, s => s.Value == EventLogSamples.WawPc001Ip);
        Assert.Equal("Stacja", workstation.KindText);
        Assert.Equal("Adres", address.KindText);
        Assert.Equal("1", workstation.LockoutsText);
        Assert.Equal("4", address.FailuresText);
        Assert.Contains("4771 · 0x18 – wstępne uwierzytelnianie nieudane (złe hasło): 4", address.CodesText, StringComparison.Ordinal);
        Assert.Contains("4776 · 0xC000006A – złe hasło: 1", workstation.CodesText, StringComparison.Ordinal);
        Assert.Equal([1, 2], document.Sources.Select(s => s.Rank));
        Assert.Same(document.Sources[0], document.SelectedSource);
        Assert.All(document.Sources, s => Assert.True(s.CanPivot));
        Assert.Contains(document.Timeline, t => t.IsLockout && t.KindText == "Blokada");
        Assert.Equal(6, document.Timeline.Count);
    }

    [Fact]
    public async Task Notes_AndAuditEvidence_AreShownInPolish()
    {
        var (harness, document) = await InvestigateAsync();

        Assert.Contains(document.Notes, n => n.Text.StartsWith("Złe hasło równe jednemu z dwóch ostatnich haseł", StringComparison.Ordinal));
        Assert.Contains(document.Notes, n => n.Text.StartsWith("Niepowodzenia odczytano z emulatora PDC", StringComparison.Ordinal));
        Assert.All(document.Notes, n => Assert.DoesNotContain("EventLogs.", n.Text, StringComparison.Ordinal));

        var notAudited = Assert.Single(document.AuditRows, a => a.Host == EventLogHarness.Dc02 && a.Entry.Requirement.EventId == 4740);
        Assert.Equal(AuditEvidenceState.NotSeenWhileCovered, notAudited.State);
        Assert.Equal("brak zdarzeń żadnego konta, choć dziennik obejmuje przedział – prawdopodobnie nie jest inspekcjonowane", notAudited.StateText);
        Assert.True(notAudited.IsProbablyNotAudited);
        Assert.Equal("4740 (sukces)", notAudited.EventText);
        Assert.Equal(harness.B2.Localizer[Presentation.GroupPolicy.SecuritySettings.SecuritySettingsText.AuditSubcategoryKey("UserAccountManagement")], notAudited.SubcategoryText);
        Assert.Contains(document.AuditRows, a => a.Host == EventLogHarness.Dc01 && a.Entry.Requirement.EventId == 4740 && a.State == AuditEvidenceState.Seen);

        await document.ShowInModelCommand.ExecuteAsync(notAudited);
        var model = Assert.Single(harness.GroupPolicyRequests);
        Assert.Equal(GroupPolicyDocumentKind.Modeling, model.Kind);
        Assert.Equal($"CN=DC02,{harness.B2.Builder.DomainControllersDn}", model.ComputerDn);

        document.OpenGroupPolicyDiagnosticsCommand.Execute(notAudited);
        Assert.Equal(GroupPolicyDocumentKind.Diagnostics, harness.GroupPolicyRequests[^1].Kind);
    }

    /// <summary>
    /// E-PRES-02/E-AN-01: the plan reads Jan's records only, so the audit evidence comes from unfiltered one-record probes –
    /// another account's 4625 on DC02 proves the Logon audit there, although Jan never logged on at DC02.
    /// </summary>
    [Fact]
    public async Task AuditEvidence_ComesFromUnfilteredProbes_NotFromTheAccountsRecords()
    {
        var (harness, document) = await InvestigateAsync(arrange: h => h.Connector.Add(EventLogSamples.LogonFailed(EventLogSamples.Dc02, 5_100,
            EventLogSamples.Morning.AddMinutes(40), EventLogSamples.LukaszWisniewski, "10.10.1.30", "WAW-PC009")));

        var probes = harness.Connector.Reads.Where(r => r.Query.MaxRecords == 1 && r.Query.Data.Count == 0).ToList();
        Assert.All(probes, p => Assert.Equal(EventLogHarness.Dc02, p.Host, ignoreCase: true));
        Assert.Equal([4625, 4740], probes.Select(p => p.Query.EventIds.Single()).Order());
        var dc02 = document.AuditRows.Where(a => a.Host == EventLogHarness.Dc02).ToDictionary(a => a.Entry.Requirement.EventId, a => a.State);
        Assert.Equal(AuditEvidenceState.Seen, dc02[4625]);
        Assert.Equal(AuditEvidenceState.NotSeenWhileCovered, dc02[4740]);
        Assert.DoesNotContain(document.AuditRows, a => a.Host == EventLogHarness.Dc01 && a.State == AuditEvidenceState.NotSeenWhileCovered);
        // The probe's record of another account is evidence only: Jan's counts and sources stay his.
        Assert.Equal("Nieudane próby: 5, w tym złe hasła: 5.", document.CountsText);
        Assert.DoesNotContain(document.Sources, s => s.Value == "10.10.1.30");
    }

    /// <summary>
    /// E-PRES-06/E-AN-12: a custom window that ends before the lockout would have expired does not make an expired lockout
    /// "locked now" – the state is judged at the current time.
    /// </summary>
    [Fact]
    public async Task CustomWindowEndingInThePast_LockStateJudgedNow()
    {
        var harness = await EventLogHarness.CreateAsync();
        var jan = harness.Company.JanKowalski;
        var lockedAt = EventLogSamples.Morning.AddMinutes(34);
        harness.Session.SetValues(jan, Attr.LockoutTime, EventLogHarness.FileTime(lockedAt));
        harness.Dc02Values[jan] = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { [Attr.LockoutTime] = EventLogHarness.FileTime(lockedAt) };
        harness.Connector.Add(EventLogSamples.LockoutScenario());
        var document = harness.Services.CreateLockout(jan, "Jan Kowalski");
        var zone = harness.Services.Zone;
        document.SelectedTimePreset = document.TimePresetOptions.Single(o => o.Value == EventLogTimePreset.Custom);
        document.CustomFrom = TimeZoneInfo.ConvertTime(lockedAt.AddHours(-2), zone).DateTime;
        document.CustomTo = TimeZoneInfo.ConvertTime(lockedAt.AddMinutes(10), zone).DateTime;

        await document.RunAsync();

        Assert.Null(document.Error);
        // "Do" is a date: the window takes the whole chosen day, capped at now (the lockout has expired by then).
        Assert.True(document.Report!.To >= lockedAt.AddMinutes(10));
        Assert.True(document.Report.To <= document.Report.Now);
        Assert.False(document.IsLockedNow);
        Assert.Equal(harness.B2.Localizer[L.EventLogsLockout.NotLockedNow], document.LockStateText);
        Assert.False(document.UnlockCommand.CanExecute(null));
    }

    [Fact]
    public async Task Pivot_FindsTheSourceComputer_AndItsSessionsServicesAndScheduledTasks()
    {
        var (harness, document) = await InvestigateAsync(arrange: h =>
        {
            var computer = new FakeRemoteComputer();
            computer.Users.Add(new LoggedOnUser("CONTOSO\\jan.kowalski", 2, EventLogSamples.Morning, IsConsole: true));
            computer.Users.Add(new LoggedOnUser("CONTOSO\\lukasz.wisniewski", 3, EventLogSamples.Morning, IsConsole: false));
            computer.Services.Add(new RemoteService("KopiaZapasowa", "Kopia zapasowa działu IT", "Running", "Auto", false, "CONTOSO\\jan.kowalski"));
            computer.Services.Add(new RemoteService("Spooler", "Bufor wydruku", "Running", "Auto", false, "LocalSystem"));
            computer.ScheduledTasks.Add(new RemoteScheduledTask("\\Firma\\", "Synchronizacja dysku Z", "jan.kowalski@contoso.test", RemoteTaskLogonType.Password, RemoteTaskState.Ready, true));
            computer.ScheduledTasks.Add(new RemoteScheduledTask("\\", "Aktualizacja", "SYSTEM", RemoteTaskLogonType.ServiceAccount, RemoteTaskState.Ready, true));
            h.Remote.Computers["waw-pc001.contoso.test"] = computer;
        });

        document.SelectedSource = Assert.Single(document.Sources, s => s.Value == EventLogSamples.WawPc001);
        Assert.True(document.PivotCommand.CanExecute(null));
        await document.PivotCommand.ExecuteAsync(null);

        Assert.Equal(harness.Company.Computer1, document.PivotComputerDn);
        Assert.Equal("Komputer źródłowy: WAW-PC001 (waw-pc001.contoso.test)", document.PivotHeading);
        Assert.Equal(
        [
            "Sesje konta: CONTOSO\\jan.kowalski (konsola)",
            "Usługi działające jako to konto: Kopia zapasowa działu IT (KopiaZapasowa)",
            "Zadania zaplanowane uruchamiane jako to konto: \\Firma\\Synchronizacja dysku Z (zapisane hasło)",
        ], document.PivotLines);
        Assert.Contains(harness.Remote.Calls, c => c.Operation == "ScheduledTasks");
    }

    [Fact]
    public async Task Pivot_FromAnAddress_UsesReverseDns_AndSaysWhenTheComputerDoesNotAnswer()
    {
        var (harness, document) = await InvestigateAsync(arrange: h => h.Lookup.Names[EventLogSamples.WawPc001Ip] = "waw-pc001.contoso.test");

        document.SelectedSource = Assert.Single(document.Sources, s => s.Value == EventLogSamples.WawPc001Ip);
        await document.PivotCommand.ExecuteAsync(null);

        Assert.Equal(harness.Company.Computer1, document.PivotComputerDn);
        Assert.Equal("Adres 10.10.1.21 to waw-pc001.contoso.test (DNS).", document.PivotLines![0]);
        Assert.StartsWith("Nie odczytano: ", document.PivotLines[1], StringComparison.Ordinal);
    }

    [Fact]
    public async Task Pivot_WithoutRemoteActions_StillNamesTheComputer()
    {
        var (harness, document) = await InvestigateAsync(withRemote: false);

        document.SelectedSource = Assert.Single(document.Sources, s => s.Value == EventLogSamples.WawPc001);
        await document.PivotCommand.ExecuteAsync(null);

        Assert.Equal(harness.Company.Computer1, document.PivotComputerDn);
        Assert.Equal([harness.B2.Localizer[L.EventLogsLockout.PivotNotAvailable]], document.PivotLines);
    }

    [Fact]
    public async Task Unlock_GoesThroughTheChangePlanPreview()
    {
        var (harness, document) = await InvestigateAsync();

        Assert.True(document.UnlockCommand.CanExecute(null));
        await document.UnlockCommand.ExecuteAsync(null);

        var request = Assert.Single(harness.B2.Dialogs.BatchEditorRequests);
        Assert.Equal([harness.Company.JanKowalski], request.TargetDns);
        Assert.IsType<UnlockAccountOperation>(Assert.Single(request.Operations));
        Assert.True(request.OpenPreview);
        Assert.NotNull(harness.Session.GetValues(harness.Company.JanKowalski, Attr.LockoutTime).FirstOrDefault());
    }

    [Fact]
    public async Task ReadFailures_AreNotes_AndTheOtherDcStillCounts()
    {
        var (_, document) = await InvestigateAsync(arrange: h => h.Connector.FailHost(EventLogHarness.Dc02, EventLogAccessError.AccessDenied));

        Assert.True(document.HasReport);
        Assert.Contains(document.Notes, n => n.IsWarning && n.Text.Contains("Czytelnicy dziennika zdarzeń", StringComparison.Ordinal));
        Assert.Contains("Odmowa dostępu", document.DomainControllerRows.Single(r => r.Host == EventLogHarness.Dc02).CoverageText, StringComparison.Ordinal);
        Assert.Contains(document.Hosts, h => h.Host == EventLogHarness.Dc02 && h.HasError);
    }

    [Fact]
    public async Task Export_HasEverySection_AndThePersonalDataNote()
    {
        var (harness, document) = await InvestigateAsync();
        var path = Path.Combine(Path.GetTempPath(), "admng-test-" + Guid.NewGuid().ToString("N") + ".html");
        try
        {
            harness.B2.Files.NextSavePath = path;
            await document.ExportHtmlCommand.ExecuteAsync(null);
            var html = await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken);
            Assert.Contains(EventLogExport.E(harness.B2.Localizer[L.EventLogs.PersonalDataNote]), html, StringComparison.Ordinal);
            Assert.Contains("Źródła prób", html, StringComparison.Ordinal);
            Assert.Contains("Oś czasu", html, StringComparison.Ordinal);
            Assert.Contains("Inspekcja na kontrolerach domeny", html, StringComparison.Ordinal);
            Assert.Contains(EventLogSamples.WawPc001, html, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task NotAUser_IsAnError_AndNotSupported_ReadsNothing()
    {
        var harness = await EventLogHarness.CreateAsync();
        var computer = harness.Services.CreateLockout(harness.Company.Computer1, null);
        await computer.RunAsync();
        Assert.Equal("Ten obiekt nie jest kontem użytkownika.", computer.Error);

        harness.Connector.IsSupported = false;
        var document = harness.Services.CreateLockout(harness.Company.JanKowalski, null);
        Assert.True(document.IsNotSupported);
        Assert.False(document.RunCommand.CanExecute(null));
        await document.RunAsync();
        Assert.False(document.HasReport);
        Assert.Empty(harness.Connector.Reads);
    }
}
