using System.Net;
using AdMng.Core.ActiveDirectory;
using AdMng.Core.Computers.Remote;
using AdMng.Core.Directory;
using AdMng.Core.EventLogs;
using AdMng.Core.EventLogs.Analysis;
using AdMng.Presentation.EventLogs;
using AdMng.Presentation.Localization;
using AdMng.Presentation.Services;
using AdMng.Presentation.Settings;
using AdMng.Presentation.Tests.Watchdog;
using AdMng.Testing;
using AdMng.Testing.EventLogs;

namespace AdMng.Presentation.Tests.EventLogs;

/// <summary>Regressions of the track E review: the reader, the lockout source and the failed logons.</summary>
public class EventLogReviewRegressionTests
{
    private static readonly DateTime Day = new(2026, 11, 3);

    // ---- Lockout source ---------------------------------------------------------------------------------------------------

    /// <summary>Jan Kowalski locked out on dc01 (the samples' lockout scenario in the connector).</summary>
    private static async Task<EventLogHarness> LockoutHarnessAsync(IHostAddressResolver? resolver = null)
    {
        var harness = await EventLogHarness.CreateAsync(resolver: resolver);
        for (var i = 0; i < 5; i++)
        {
            harness.Session.SimulateBadPassword(harness.Company.JanKowalski);
        }

        harness.Session.SimulateLockout(harness.Company.JanKowalski);
        harness.Connector.Add(EventLogSamples.LockoutScenario());
        return harness;
    }

    [Fact]
    public async Task DcNameThatTimesOutInDns_IsStillRead_AndNeitherAnalysisAborts()
    {
        var resolver = new FakeAddressResolver();
        resolver.Addresses[EventLogHarness.Dc01] = [IPAddress.Parse("10.10.0.1")];
        resolver.Failures[EventLogHarness.Dc02] = new RemoteActionException(RemoteErrorKind.Timeout);
        var harness = await LockoutHarnessAsync(resolver);

        var lockout = harness.Services.CreateLockout(harness.Company.JanKowalski, "Jan Kowalski");
        await lockout.RunAsync();
        Assert.Null(lockout.Error);
        Assert.True(lockout.HasReport);
        Assert.Contains(lockout.Hosts, h => h.Host == EventLogHarness.Dc02 && h.State == EventLogHostState.Done);

        var failed = harness.Services.CreateFailedLogons();
        await failed.RunAsync();
        Assert.Null(failed.Error);
        Assert.True(failed.HasReport);
    }

    [Fact]
    public async Task PsoTheOperatorCannotRead_IsAnUnknownPolicy_NeverTheDomainPolicy()
    {
        var harness = await LockoutHarnessAsync();
        var psoDn = $"CN=Księgowość – hasła,CN=Password Settings Container,CN=System,{harness.Session.Info.DefaultNamingContext}";
        harness.Session.SetResultantPso(harness.Company.JanKowalski, psoDn);

        var document = harness.Services.CreateLockout(harness.Company.JanKowalski, "Jan Kowalski");
        await document.RunAsync();

        Assert.Null(document.Error);
        Assert.Equal(harness.B2.Localizer.Format(L.EventLogsLockout.PolicyPsoUnreadable, "Księgowość – hasła"), document.PolicyText);
        Assert.DoesNotContain("zasady domeny", document.PolicyText, StringComparison.Ordinal);
        Assert.DoesNotContain(document.Report!.Notes, n => n.Key == LockoutNoteKeys.LockoutPolicyDisabled);
    }

    [Fact]
    public async Task AfterAnUnlock_TheDcThatUnlocked_IsNotWhereTheLockoutWasWritten()
    {
        var harness = await LockoutHarnessAsync();
        var document = harness.Services.CreateLockout(harness.Company.JanKowalski, "Jan Kowalski");
        await document.RunAsync();
        var originating = harness.B2.Localizer[L.EventLogsLockout.Originating];
        Assert.Contains(originating, document.DomainControllerRows.Single(r => r.Host == EventLogHarness.Dc01).RolesText, StringComparison.Ordinal);

        // The helpdesk unlocks through the window's DC: lockoutTime = 0 is now the last originating write there.
        await harness.Session.ModifyAsync(harness.Company.JanKowalski, [AttributeModification.Replace(Attr.LockoutTime, "0")], null,
            TestContext.Current.CancellationToken);
        await document.RunAsync();

        Assert.Null(document.Error);
        Assert.All(document.DomainControllerRows, r => Assert.DoesNotContain(originating, r.RolesText, StringComparison.Ordinal));
        Assert.All(document.Hosts, h => Assert.DoesNotContain(originating, h.KindText, StringComparison.Ordinal));
    }

    [Fact]
    public async Task CustomWindow_TheToDayIsIncluded_AndOneDayIsAValidWindow()
    {
        var harness = await LockoutHarnessAsync();
        var document = harness.Services.CreateLockout(harness.Company.JanKowalski, "Jan Kowalski");
        document.SelectedTimePreset = document.TimePresetOptions.Single(o => o.Value == EventLogTimePreset.Custom);
        document.CustomFrom = Day;
        document.CustomTo = Day;

        await document.RunAsync();

        Assert.Null(document.Error);
        Assert.Single(document.Report!.Lockouts);
        Assert.Contains(document.Sources, s => s.Value == EventLogSamples.WawPc001);
        var query = harness.Connector.Reads[^1].Query;
        Assert.Equal(new DateTimeOffset(Day, TimeSpan.Zero), query.From);
        Assert.Equal(harness.B2.Time.GetUtcNow(), query.To);
    }

    [Fact]
    public async Task Pivot_LocalAndForeignAccountsOfTheSameName_AreNotTheLockedAccount()
    {
        var harness = await LockoutHarnessAsync();
        var computer = new FakeRemoteComputer();
        computer.Users.Add(new LoggedOnUser(".\\jan.kowalski", 2, EventLogSamples.Morning, IsConsole: true));
        computer.Users.Add(new LoggedOnUser("WAW-PC001\\jan.kowalski", 3, EventLogSamples.Morning, IsConsole: false));
        computer.Users.Add(new LoggedOnUser("contoso.test\\jan.kowalski", 4, EventLogSamples.Morning, IsConsole: false));
        computer.Services.Add(new RemoteService("KopiaLokalna", "Kopia lokalna", "Running", "Auto", false, ".\\jan.kowalski"));
        computer.Services.Add(new RemoteService("SyncFabrikam", "Synchronizacja Fabrikam", "Running", "Auto", false, "FABRIKAM\\jan.kowalski"));
        computer.Services.Add(new RemoteService("Dnscache", "Klient DNS", "Running", "Auto", false, "NT AUTHORITY\\NetworkService"));
        computer.ScheduledTasks.Add(new RemoteScheduledTask("\\", "Kopia na dysk lokalny", "WAW-PC001\\jan.kowalski", RemoteTaskLogonType.Password, RemoteTaskState.Ready, true));
        computer.ScheduledTasks.Add(new RemoteScheduledTask("\\", "Raport Fabrikam", "jan.kowalski@fabrikam.com", RemoteTaskLogonType.Password, RemoteTaskState.Ready, true));
        computer.ScheduledTasks.Add(new RemoteScheduledTask("\\Firma\\", "Mapowanie dysku Z", "jan.kowalski", RemoteTaskLogonType.Password, RemoteTaskState.Ready, true));
        computer.ScheduledTasks.Add(new RemoteScheduledTask("\\Firma\\", "Synchronizacja kalendarza", "JAN.KOWALSKI@CONTOSO.TEST", RemoteTaskLogonType.Interactive, RemoteTaskState.Ready, true));
        harness.Remote.Computers["waw-pc001.contoso.test"] = computer;
        var document = harness.Services.CreateLockout(harness.Company.JanKowalski, "Jan Kowalski");
        await document.RunAsync();

        document.SelectedSource = Assert.Single(document.Sources, s => s.Value == EventLogSamples.WawPc001);
        await document.PivotCommand.ExecuteAsync(null);

        Assert.Equal(
        [
            "Sesje konta: contoso.test\\jan.kowalski",
            harness.B2.Localizer[L.EventLogsLockout.PivotNoServices],
            "Zadania zaplanowane uruchamiane jako to konto: \\Firma\\Mapowanie dysku Z (zapisane hasło), \\Firma\\Synchronizacja kalendarza",
        ], document.PivotLines);
    }

    // ---- Reader -----------------------------------------------------------------------------------------------------------

    private static async Task<(EventLogHarness Harness, EventLogReaderDocumentViewModel Reader)> OpenReaderAsync()
    {
        var harness = await EventLogHarness.CreateAsync();
        harness.Connector.Add(EventLogSamples.LockoutScenario());
        var reader = harness.Services.CreateReader();
        await reader.OpenAsync();
        return (harness, reader);
    }

    private static async Task<EventLogSourceViewModel> OpenFileAsync(EventLogHarness harness, EventLogReaderDocumentViewModel reader, string path,
        params EventLogRecord[] records)
    {
        if (records.Length == 0)
        {
            harness.Connector.AddUnreadableFile(path);
        }
        else
        {
            harness.Connector.AddFile(path, records);
        }

        harness.B2.Files.NextOpenPath = path;
        await reader.OpenFilesCommand.ExecuteAsync(null);
        return reader.Sources[^1];
    }

    private static EventLogRecord LockedOut(long recordId, string account) =>
        EventLogSamples.AccountLockedOut("dc01.contoso.test", recordId, EventLogSamples.Morning.AddMinutes(recordId), account, "WAW-PC001");

    [Fact]
    public async Task FilesOfTheSameName_GetTheirOwnSource_RowAndRecordHost()
    {
        var (harness, reader) = await OpenReaderAsync();
        var folder = Path.Combine(Path.GetTempPath(), "admng-test-" + Guid.NewGuid().ToString("N"));
        var first = await OpenFileAsync(harness, reader, Path.Combine(folder, "DC01", "Security.evtx"), LockedOut(1, EventLogSamples.JanKowalski));
        var second = await OpenFileAsync(harness, reader, Path.Combine(folder, "DC02", "Security.evtx"),
            LockedOut(2, EventLogSamples.ZofiaZak), LockedOut(3, EventLogSamples.ZanetaSzymanska));
        var third = await OpenFileAsync(harness, reader, Path.Combine(folder, "DC02", "kopia", "DC02", "Security.evtx"));

        Assert.Equal(["Security.evtx", Path.Combine("DC02", "Security.evtx"), "Security.evtx (2)"], new[] { first, second, third }.Select(s => s.Host));
        foreach (var source in reader.Sources)
        {
            source.IsSelected = source.IsFiles;
        }

        await reader.RunCommand.ExecuteAsync(null);

        Assert.Equal([1, 2, 0], reader.Hosts.Select(h => h.Records));
        Assert.Equal([EventLogHostState.Done, EventLogHostState.Done, EventLogHostState.Failed], reader.Hosts.Select(h => h.State));
        Assert.True(reader.HasHostProblems);
        Assert.Equal(first.Host, Assert.Single(reader.Rows, r => r.Record.RecordId == 1).Record.Host);
        Assert.All(reader.Rows.Where(r => r.Record.RecordId > 1), r => Assert.Equal(second.Host, r.Record.Host));
        Assert.Equal(new[] { first.Host, second.Host }.Order(StringComparer.Ordinal), reader.Rows.Select(r => r.HostText).Distinct().Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task HostColumn_ShowsFileNamesWhole_AndFullNamesOfHostsSharingAFirstLabel()
    {
        var (harness, reader) = await OpenReaderAsync();
        var folder = Path.Combine(Path.GetTempPath(), "admng-test-" + Guid.NewGuid().ToString("N"));
        await OpenFileAsync(harness, reader, Path.Combine(folder, "Security.DC01.evtx"), LockedOut(1, EventLogSamples.JanKowalski));
        await OpenFileAsync(harness, reader, Path.Combine(folder, "Security.DC02.evtx"), LockedOut(2, EventLogSamples.ZofiaZak));
        foreach (var source in reader.Sources)
        {
            source.IsSelected = source.IsFiles;
        }

        await reader.RunCommand.ExecuteAsync(null);
        Assert.Equal(["Security.DC01.evtx", "Security.DC02.evtx"], reader.Rows.Select(r => r.HostText).Order(StringComparer.Ordinal));

        const string euDc = "dc01.eu.contoso.test";
        harness.Connector.AddHost(euDc, "Security").Add(EventLogSamples.AccountLockedOut(euDc, 7, EventLogSamples.Morning.AddHours(1), "łucja.wójcik", "WAW-PC002"));
        reader.ComputerText = euDc;
        reader.AddComputerCommand.Execute(null);
        foreach (var source in reader.Sources)
        {
            source.IsSelected = !source.IsFiles;
        }

        await reader.RunCommand.ExecuteAsync(null);
        Assert.Equal(["dc01.contoso.test", euDc, "dc02"], reader.Rows.Select(r => r.HostText).Distinct().Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task Search_RunsOffTheUiThread_OrdinalCaseInsensitive_NewestWins()
    {
        var (harness, reader) = await OpenReaderAsync();
        await reader.RunCommand.ExecuteAsync(null);
        Assert.Equal(7, reader.Rows.Count);

        using var ui = new TestUiThread();
        var shownRightAfterTyping = await ui.RunAsync(() =>
        {
            reader.SearchText = "ZOFIA";
            return Task.FromResult(reader.Rows.Count);
        });
        Assert.Equal(7, shownRightAfterTyping);
        await EventLogHarness.WaitAsync(() => reader.Rows.Count == 1);
        Assert.Equal(EventLogSamples.ZofiaZak, Assert.Single(reader.Rows).Record.Get("TargetUserName"));

        await ui.RunAsync(() =>
        {
            reader.SearchText = "zofia";
            reader.SearchText = "JAN.KOWALSKI";
            return Task.CompletedTask;
        });
        await EventLogHarness.WaitAsync(() => reader.Rows.Count == 6);
        Assert.All(reader.Rows, r => Assert.True(r.Matches("jan.kowalski")));
        Assert.Empty(ui.Errors);

        var row = new EventLogRecordRow(EventLogSamples.LogonFailed("dc01.contoso.test", 1, EventLogSamples.Morning, "żaneta.szymańska", null, "KRK-LT001"),
            harness.B2.Localizer, TimeZoneInfo.Utc);
        Assert.True(row.Matches("ŻANETA.SZYMAŃSKA"));
        Assert.False(row.Matches("zaneta"));
    }

    [Fact]
    public async Task CustomWindow_TheToDayIsIncluded_AndOneDayIsAValidWindow_InTheReader()
    {
        var (harness, reader) = await OpenReaderAsync();
        reader.SelectedTimePreset = reader.TimePresetOptions.Single(o => o.Value == EventLogTimePreset.Custom);
        reader.CustomFrom = Day.AddDays(-1);
        reader.CustomTo = Day.AddDays(-1);

        Assert.True(reader.TryBuildQuery(out var query, out var error), error);
        Assert.Equal(new DateTimeOffset(Day.AddDays(-1), TimeSpan.Zero), query!.From);
        Assert.Equal(new DateTimeOffset(Day, TimeSpan.Zero), query.To);

        reader.CustomTo = Day;
        Assert.True(reader.TryBuildQuery(out query, out _));
        Assert.Equal(harness.B2.Time.GetUtcNow(), query!.To);
        await reader.RunCommand.ExecuteAsync(null);
        Assert.Equal(7, reader.Rows.Count);

        reader.CustomFrom = Day.AddDays(1);
        Assert.False(reader.TryBuildQuery(out _, out error));
        Assert.Equal(harness.B2.Localizer[L.EventLogs.InvalidRange], error);
    }

    [Fact]
    public async Task SavingANewQuery_AtTheLimit_SaysSo_AndNeverReportsItSaved()
    {
        var (harness, reader) = await OpenReaderAsync();
        await harness.B2.Settings.UpdateAsync(s =>
        {
            for (var i = 0; i < EventLogSettings.MaxSavedQueries; i++)
            {
                s.EventLogs.SavedQueries.Add(new EventLogSavedQuery { Name = $"Zapytanie {i:000}" });
            }
        }, TestContext.Current.CancellationToken);
        harness.B2.Notifications.Clear();

        reader.SavedQueryName = "Blokady – księgowość";
        await reader.SaveQueryCommand.ExecuteAsync(null);

        Assert.Equal(EventLogSettings.MaxSavedQueries, harness.B2.Settings.Current.EventLogs.SavedQueries.Count);
        Assert.DoesNotContain(harness.B2.Settings.Current.EventLogs.SavedQueries, q => q.Name == "Blokady – księgowość");
        Assert.Equal((NotificationSeverity.Warning, harness.B2.Localizer.Format(L.EventLogs.SavedQueriesFull, EventLogSettings.MaxSavedQueries)),
            Assert.Single(harness.B2.Notifications.Items));

        harness.B2.Notifications.Clear();
        reader.SavedQueryName = "Zapytanie 007";
        reader.EventIdsText = "4740";
        await reader.SaveQueryCommand.ExecuteAsync(null);

        Assert.Equal(NotificationSeverity.Success, Assert.Single(harness.B2.Notifications.Items).Severity);
        Assert.Equal("Zapytanie 007", reader.SelectedSavedQuery);
        Assert.Equal("4740", harness.B2.Settings.Current.EventLogs.SavedQueries.Single(q => q.Name == "Zapytanie 007").EventIds);
    }

    [Fact]
    public async Task Details_LocalOrForeignAccount_IsNotLinkedToTheDomainAccountOfTheSameName()
    {
        var (harness, reader) = await OpenReaderAsync();
        harness.Connector.Add(EventLogSamples.Record(EventLogHarness.Dc01, "Security", EventLogSamples.SecurityAuditing, 4625, 77, EventLogSamples.Morning.AddHours(1),
            EventLogLevels.LogAlways,
            [
                ("TargetUserName", EventLogSamples.JanKowalski), ("TargetDomainName", "SRV-APLIKACJE01"),
                ("TargetOutboundUserName", EventLogSamples.JanKowalski), ("TargetOutboundDomainName", "NT AUTHORITY"),
                ("SubjectUserName", EventLogSamples.JanKowalski), ("SubjectDomainName", "CONTOSO"),
            ],
            EventLogSamples.AuditFailureKeywords));
        await reader.RunCommand.ExecuteAsync(null);

        reader.SelectedRow = reader.Rows.Single(r => r.Record.RecordId == 77);
        var subject = reader.Fields.Single(f => f.Name == "SubjectUserName");
        await EventLogHarness.WaitAsync(() => subject.HasObject);

        Assert.Equal(harness.Company.JanKowalski, subject.ObjectDn);
        Assert.Null(reader.Fields.Single(f => f.Name == "TargetUserName").ObjectDn);
        Assert.Null(reader.Fields.Single(f => f.Name == "TargetOutboundUserName").ObjectDn);
    }

    // ---- Failed logons ----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task DrillDown_UnknownNamesAreMaskedInTheRecords_UntilRevealed()
    {
        var harness = await EventLogHarness.CreateAsync();
        harness.Connector.Add(EventLogSamples.FailedLogonScenario());
        var document = harness.Services.CreateFailedLogons();
        document.EnumerationThreshold = 3;
        await document.RunAsync();

        document.SelectedFinding = Assert.Single(document.Findings, f => f.Kind == FailedLogonFindingKind.AccountEnumeration);
        Assert.NotEmpty(document.SelectedRecords);
        var masked = harness.B2.Localizer[L.EventLogsFailedLogons.MaskedAccount];
        Assert.All(document.SelectedRecords, r => Assert.StartsWith(masked, r.Summary, StringComparison.Ordinal));
        Assert.All(document.SelectedRecords, r => Assert.DoesNotContain("kowalski.jan", r.AutomationName, StringComparison.Ordinal));
        Assert.All(document.SelectedRecords, r => Assert.DoesNotContain("zofia.żak", r.Summary, StringComparison.Ordinal));

        document.RevealAccountCommand.Execute(document.SelectedAccounts.Single(a => a.Account.Account == "kowalski.jan"));

        Assert.Equal(2, document.SelectedRecords.Count(r => r.Summary.Contains("kowalski.jan", StringComparison.Ordinal)));
        Assert.All(document.SelectedRecords, r => Assert.DoesNotContain("zofia.żak", r.Summary, StringComparison.Ordinal));
    }
}
