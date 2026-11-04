using AdMng.Core.ActiveDirectory;
using AdMng.Core.Computers.Remote;
using AdMng.Core.Correspondence.Outlook;
using AdMng.Core.EventLogs;
using AdMng.Core.GroupPolicy;
using AdMng.Core.GroupPolicy.Management;
using AdMng.Presentation.Readiness;
using AdMng.Testing;
using AdMng.Testing.EventLogs;
using AdMng.Watchdog.Client;
using AdMng.Watchdog.Contracts.Api;
using AdMng.Watchdog.Testing.Client;

namespace AdMng.Presentation.Tests.Readiness;

/// <summary>
/// The full check (design note docs/dev/s-pilot-readiness.md 2): SYSVOL and Group Policy, the remote actions on the test
/// computer, the event log of every DC and of the test computer, the Watchdog and the Outlook profile – each check's OK and
/// failure paths through the existing services, read-only.
/// </summary>
public sealed class ReadinessFullCheckTests
{
    private static readonly CancellationToken Token = TestContext.Current.CancellationToken;

    [Fact]
    public async Task FullCheck_OfAHealthyDomain_ChecksEveryArea_AndWritesNothing()
    {
        var harness = await ReadinessHarness.CreateAsync();
        harness.Watchdog.Status = SampleStatus.Create() with { Checks = [new HealthCheckDto { Code = "storage.free", State = HealthState.Ok, Args = ["182"] }] };
        harness.Builder.AddSchemaObject("msLAPS-Password");
        harness.Session.ClearOperations();

        var report = await harness.RunAsync(ReadinessDepth.Full);

        Assert.False(report.Cancelled);
        string[] dc01Ok =
        [
            ReadinessCodes.EventLogsSession, ReadinessCodes.EventLogsSecurity, ReadinessCodes.EventLogsCoverage,
            ReadinessCodes.EventLogsAuditLockout, ReadinessCodes.EventLogsAuditFailedLogons,
        ];
        Assert.All(dc01Ok, code => Assert.Equal(ReadinessStatus.Ok, report.Status(code, ReadinessHarness.Dc01)));
        Assert.All(
            report.Results.Where(r => r.Host is null || !string.Equals(r.Host, ReadinessHarness.Dc02, StringComparison.Ordinal)),
            r => Assert.True(r.Status == ReadinessStatus.Ok, $"{r.Code}: {r.Status} – {r.Explanation}"));

        Assert.Equal("SYSVOL na dc01.contoso.test otwarto logowaniem okna.", report.Single(ReadinessCodes.SysvolReachable).Explanation);
        Assert.StartsWith("Lista ACL folderu Policies pozwala kontu okna tworzyć foldery GPO", report.Single(ReadinessCodes.SysvolPoliciesWrite).Explanation, StringComparison.Ordinal);
        Assert.Equal("Plików ADMX: 2; języki: en-US, pl-PL.", report.Single(ReadinessCodes.SysvolCentralStore).Explanation);
        Assert.Equal("Default Domain Policy i Default Domain Controllers Policy istnieją w AD i w SYSVOL.", report.Single(ReadinessCodes.SysvolDefaultGpos).Explanation);
        Assert.Equal("SYSVOL jest replikowany przez DFSR.", report.Single(ReadinessCodes.SysvolReplication).Explanation);
        Assert.Equal("waw-pc001.contoso.test wskazuje adres 10.10.1.21.", report.Single(ReadinessCodes.RemoteNameResolves).Explanation);
        Assert.Equal("WinRM odpowiedział z Kerberos; waw-pc001.contoso.test działa pod kontrolą Microsoft Windows 11 Enterprise 10.0.26200.6899.",
            report.Single(ReadinessCodes.RemoteCimSession).Explanation);
        Assert.StartsWith("Dziennik Zabezpieczenia na dc01.contoso.test sięga 9 dni wstecz (od ", report.Single(ReadinessCodes.EventLogsCoverage, ReadinessHarness.Dc01).Explanation, StringComparison.Ordinal);
        Assert.EndsWith("Rozmiar 128 MB z 128 MB; nadpisywanie w razie potrzeby.", report.Single(ReadinessCodes.EventLogsCoverage, ReadinessHarness.Dc01).Explanation, StringComparison.Ordinal);
        Assert.Equal("dc01.contoso.test rejestruje wszystkie zdarzenia potrzebne analizie (4740, 4771, 4776, 4625).",
            report.Single(ReadinessCodes.EventLogsAuditLockout, ReadinessHarness.Dc01).Explanation);
        Assert.Equal("Dziennik System na waw-pc001.contoso.test jest czytelny; sięga 30 dni wstecz.", report.Single(ReadinessCodes.EventLogsTestComputer).Explanation);
        Assert.Equal("Adres Watchdoga: admng-wd.contoso.com:8443.", report.Single(ReadinessCodes.WatchdogConfigured).Explanation);
        Assert.Equal("Rola: Administrator.", report.Single(ReadinessCodes.WatchdogRole).Explanation);
        Assert.Equal("Watchdog 1.0.0: wszystkie kontrole stanu są w porządku.", report.Single(ReadinessCodes.WatchdogHealth).Explanation);
        Assert.Equal("Outlook 16.0.0.17928, profil Outlook, kont: 2.", report.Single(ReadinessCodes.CorrespondenceProfile).Explanation);

        // dc02's log reaches two days back and holds none of the audited events: both are reported, with what to do.
        var coverage = report.Single(ReadinessCodes.EventLogsCoverage, ReadinessHarness.Dc02);
        Assert.Equal(ReadinessStatus.Warning, coverage.Status);
        Assert.Contains("sięga 2 dni wstecz", coverage.Explanation, StringComparison.Ordinal);
        Assert.Contains("zalecane jest 7 dni", coverage.Explanation, StringComparison.Ordinal);
        Assert.Equal("WATCHDOG-LINUX.md, rozdział 2.10 „Zasady inspekcji na kontrolerach i rozmiar dziennika Security”", coverage.Documentation);
        var lockout = report.Single(ReadinessCodes.EventLogsAuditLockout, ReadinessHarness.Dc02);
        Assert.Equal(ReadinessStatus.Warning, lockout.Status);
        Assert.StartsWith("dc02.contoso.test: brak 4740 (Inspekcja zarządzania kontami użytkowników, Inspekcja sukcesów), 4771 (Inspekcja usługi uwierzytelniania Kerberos, Inspekcja niepowodzeń), ", lockout.Explanation, StringComparison.Ordinal);
        Assert.Contains("w ostatnich 2 dni", lockout.Explanation, StringComparison.Ordinal);
        Assert.Contains("Zaawansowane zasady inspekcji", lockout.Remedy, StringComparison.Ordinal);
        Assert.Equal(ReadinessStatus.Warning, report.Status(ReadinessCodes.EventLogsAuditFailedLogons, ReadinessHarness.Dc02));

        // Read-only: no LDAP write, no SYSVOL write (the Policies ACL is read, never probed by writing), only the OS read on
        // the computer, and the Outlook session released.
        Assert.Empty(harness.Session.Writes);
        Assert.Empty(harness.Sysvol.Writes);
        Assert.Contains(harness.Sysvol.Calls, c => c.Operation == "GetPoliciesSecurity");
        Assert.Equal(0, harness.Sysvol.OpenConnections);
        Assert.Equal(["Connect", "OperatingSystem", "Dispose"], harness.Remote.Calls.Select(c => c.Operation));
        Assert.True(Assert.Single(harness.Outlook.Sessions).IsDisposed);
        Assert.Empty(harness.Outlook.Items);
        Assert.Equal(0, harness.Connector.OpenReaders);
        Assert.Equal([ReadinessHarness.Dc01, ReadinessHarness.Dc02, ReadinessHarness.TestComputer], harness.Connector.OpenedHosts);
        Assert.All(harness.Connector.Reads, r => Assert.Equal(1, r.Query.MaxRecords));
    }

    [Theory]
    [InlineData(SysvolUnavailableReason.AccessDenied, "Konto potrzebuje odczytu udziału SYSVOL na DC")]
    [InlineData(SysvolUnavailableReason.DistinguishedNameLogon, "Połącz okno kontem Windows albo poświadczeniami domeny.")]
    [InlineData(SysvolUnavailableReason.Unreachable, "SMB (TCP 445)")]
    public async Task Sysvol_Unavailable_IsAnError_WithTheReasonsRemedy_AndTheFileChecksAreNotChecked(SysvolUnavailableReason reason, string remedy)
    {
        var harness = await ReadinessHarness.CreateAsync();
        harness.Sysvol.Unavailable = reason;

        var report = await harness.RunAsync(ReadinessDepth.Full);

        var reachable = report.Single(ReadinessCodes.SysvolReachable);
        Assert.Equal(ReadinessStatus.Error, reachable.Status);
        Assert.StartsWith("SYSVOL niedostępny na dc01.contoso.test", reachable.Explanation, StringComparison.Ordinal);
        Assert.Contains(remedy, reachable.Remedy, StringComparison.Ordinal);
        Assert.Equal(ReadinessStatus.NotChecked, report.Status(ReadinessCodes.SysvolPoliciesWrite));
        Assert.Equal(ReadinessStatus.NotChecked, report.Status(ReadinessCodes.SysvolCentralStore));
        Assert.Equal(ReadinessStatus.NotChecked, report.Status(ReadinessCodes.SysvolDefaultGpos));
        Assert.Contains("ich folderów w SYSVOL nie sprawdzono", report.Single(ReadinessCodes.SysvolDefaultGpos).Explanation, StringComparison.Ordinal);
        Assert.Equal(ReadinessStatus.Ok, report.Status(ReadinessCodes.SysvolReplication));
    }

    [Fact]
    public async Task PoliciesWrite_IsJudgedFromTheAclForTheWindowsAccount()
    {
        var harness = await ReadinessHarness.CreateAsync();
        await harness.ReconnectAsync(info: i => i with { BoundUserDn = harness.Company.JanKowalski, BoundIdentity = @"CONTOSO\jan.kowalski" });

        var denied = await harness.RunAsync(ReadinessDepth.Full);
        harness.Sysvol.PoliciesSecurity = null;
        var unreadable = await harness.RunAsync(ReadinessDepth.Full);

        var write = denied.Single(ReadinessCodes.SysvolPoliciesWrite);
        Assert.Equal(ReadinessStatus.Warning, write.Status);
        Assert.StartsWith("Lista ACL folderu Policies nie pozwala kontu okna tworzyć folderów i plików", write.Explanation, StringComparison.Ordinal);
        Assert.Equal("Twórz GPO kontem z grupy Twórcy-właściciele zasad grupy albo Administratorzy domeny.", write.Remedy);
        Assert.Equal(ReadinessStatus.NotChecked, unreadable.Status(ReadinessCodes.SysvolPoliciesWrite));
        Assert.Empty(harness.Sysvol.Writes);
    }

    [Fact]
    public async Task CentralStore_MissingOrEmpty_IsAWarning_WithTheUpdate()
    {
        var harness = await ReadinessHarness.CreateAsync();
        var emptyStore = new FakeSysvol { PoliciesSecurity = harness.PoliciesAcl() };
        emptyStore.AddGpoFolder(GpoInfo.DefaultDomainPolicyId);
        emptyStore.AddGpoFolder(GpoInfo.DefaultDomainControllersPolicyId);
        var noStore = await harness.RunAsync(ReadinessDepth.Full, services: Services(harness, emptyStore));
        emptyStore.CreateCentralStore();
        var empty = await harness.RunAsync(ReadinessDepth.Full, services: Services(harness, emptyStore));
        emptyStore.PutPolicyDefinition("windows.admx", "<policyDefinitions />"u8.ToArray());
        var noLanguage = await harness.RunAsync(ReadinessDepth.Full, services: Services(harness, emptyStore));

        Assert.Equal(ReadinessStatus.Warning, noStore.Status(ReadinessCodes.SysvolCentralStore));
        Assert.StartsWith("Nie ma magazynu centralnego (Policies\\PolicyDefinitions)", noStore.Single(ReadinessCodes.SysvolCentralStore).Explanation, StringComparison.Ordinal);
        Assert.Contains("„Aktualizuj szablony ADMX”", noStore.Single(ReadinessCodes.SysvolCentralStore).Remedy, StringComparison.Ordinal);
        Assert.Equal("ZASADY-GRUPY.md, rozdział 8 „Aktualizacja szablonów ADMX (magazyn centralny)”", noStore.Single(ReadinessCodes.SysvolCentralStore).Documentation);
        Assert.StartsWith("Magazyn centralny istnieje, ale nie ma plików ADMX", empty.Single(ReadinessCodes.SysvolCentralStore).Explanation, StringComparison.Ordinal);
        Assert.StartsWith("Plików ADMX: 1, ale brak folderu językowego", noLanguage.Single(ReadinessCodes.SysvolCentralStore).Explanation, StringComparison.Ordinal);
        Assert.Equal(ReadinessStatus.Warning, noLanguage.Status(ReadinessCodes.SysvolCentralStore));
    }

    [Fact]
    public async Task DefaultGpos_MissingInSysvolOrAd_AreAnError()
    {
        var harness = await ReadinessHarness.CreateAsync();
        var sysvol = new FakeSysvol { PoliciesSecurity = harness.PoliciesAcl() };
        sysvol.AddGpoFolder(GpoInfo.DefaultDomainPolicyId);
        await harness.Session.DeleteAsync(harness.Builder.DefaultDomainPolicyDn, subtree: true, Token);

        var report = await harness.RunAsync(ReadinessDepth.Full, services: Services(harness, sysvol));

        var gpos = report.Single(ReadinessCodes.SysvolDefaultGpos);
        Assert.Equal(ReadinessStatus.Error, gpos.Status);
        Assert.Equal("Brakuje: Default Domain Policy w AD, Default Domain Controllers Policy w SYSVOL (GPT.INI).", gpos.Explanation);
        Assert.Contains("dcgpofix", gpos.Remedy, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null, true, ReadinessStatus.Error, "SYSVOL jest replikowany przez FRS")]
    [InlineData(32, true, ReadinessStatus.Warning, "stanie „Redirected”")]
    [InlineData(16, true, ReadinessStatus.Error, "(stan „Prepared”)")]
    [InlineData(null, false, ReadinessStatus.NotChecked, "Nie da się ustalić z katalogu")]
    public async Task Replication_FrsOrAnUnfinishedMigration_IsReported(int? flags, bool frsReplicaSet, ReadinessStatus expected, string text)
    {
        var harness = await ReadinessHarness.CreateAsync();
        harness.Session.SetValues(harness.DfsrGlobalSettingsDn, "msDFSR-Flags", flags is { } value ? new object[] { value } : []);
        if (frsReplicaSet)
        {
            var frs = harness.Builder.AddContainer("File Replication Service", harness.Builder.SystemDn);
            harness.Session.Seed($"CN=Domain System Volume (SYSVOL share),{frs}", new Dictionary<string, object?>
            {
                [Attr.ObjectClass] = new[] { "top", "nTFRSReplicaSet" },
            });
        }

        var report = await harness.RunAsync(ReadinessDepth.Full);

        var replication = report.Single(ReadinessCodes.SysvolReplication);
        Assert.Equal(expected, replication.Status);
        Assert.Contains(text, replication.Explanation, StringComparison.Ordinal);
        Assert.Equal(expected is ReadinessStatus.Error or ReadinessStatus.Warning, replication.Remedy.Contains("dfsrmig", StringComparison.Ordinal));
    }

    [Fact]
    public async Task NoTestComputer_LeavesTheRemoteChecksNotChecked_WithWhatToDo()
    {
        var harness = await ReadinessHarness.CreateAsync();

        var report = await harness.RunAsync(ReadinessDepth.Full, testComputer: "  ");

        foreach (var code in new[] { ReadinessCodes.RemoteNameResolves, ReadinessCodes.RemoteCimSession, ReadinessCodes.EventLogsTestComputer })
        {
            var row = report.Single(code);
            Assert.Equal(ReadinessStatus.NotChecked, row.Status);
            Assert.Equal("Nie wybrano komputera testowego.", row.Explanation);
            Assert.StartsWith("Wpisz nazwę komputera albo użyj przycisku Wybierz…", row.Remedy, StringComparison.Ordinal);
        }

        Assert.Empty(harness.Remote.Calls);
        Assert.Empty(harness.Resolver.Lookups);
    }

    [Fact]
    public async Task TestComputer_ByNameDnOrDnsName_IsFoundInAd_AndAnUnknownOneIsAnError()
    {
        var harness = await ReadinessHarness.CreateAsync();

        var byName = await harness.RunAsync(ReadinessDepth.Full, testComputer: "WAW-PC001");
        var byDn = await harness.RunAsync(ReadinessDepth.Full, testComputer: harness.Company.Computer1);
        var unknown = await harness.RunAsync(ReadinessDepth.Full, testComputer: "GDA-PC404");

        Assert.Equal(ReadinessStatus.Ok, byName.Status(ReadinessCodes.RemoteCimSession, ReadinessHarness.TestComputer));
        Assert.Equal(ReadinessStatus.Ok, byDn.Status(ReadinessCodes.RemoteCimSession, ReadinessHarness.TestComputer));
        var notFound = unknown.Single(ReadinessCodes.RemoteNameResolves);
        Assert.Equal(ReadinessStatus.Error, notFound.Status);
        Assert.Equal("Nie znaleziono w domenie komputera GDA-PC404.", notFound.Explanation);
        Assert.Equal(ReadinessStatus.NotChecked, unknown.Status(ReadinessCodes.RemoteCimSession));
        Assert.Equal("Nie sprawdzono: komputer testowy nie ma użytecznej nazwy DNS.", unknown.Single(ReadinessCodes.EventLogsTestComputer).Explanation);
    }

    [Fact]
    public async Task TestComputer_WithoutDnsName_NotResolving_OrWithoutWinRm_IsAnError()
    {
        var harness = await ReadinessHarness.CreateAsync();
        harness.Session.SetValues(harness.Company.Computer2, Attr.DnsHostName, []);
        harness.Resolver.Addresses.Clear();
        harness.Remote.ConnectFailures[ReadinessHarness.TestComputer] = RemoteErrorKind.WinRmNotAvailable;

        var noDns = await harness.RunAsync(ReadinessDepth.Full, testComputer: "WAW-PC002");
        var report = await harness.RunAsync(ReadinessDepth.Full);

        Assert.Equal("WAW-PC002 nie ma w AD nazwy DNS (dNSHostName), której potrzebuje Kerberos.", noDns.Single(ReadinessCodes.RemoteNameResolves).Explanation);
        Assert.Equal(ReadinessStatus.Error, report.Status(ReadinessCodes.RemoteNameResolves));
        Assert.Equal("waw-pc001.contoso.test nie rozwiązuje się w DNS z tego komputera.", report.Single(ReadinessCodes.RemoteNameResolves).Explanation);
        var cim = report.Single(ReadinessCodes.RemoteCimSession);
        Assert.Equal(ReadinessStatus.Error, cim.Status);
        Assert.False(string.IsNullOrWhiteSpace(cim.Remedy));
        Assert.Equal("KOMPUTERY.md, rozdział 5 „Włączenie WinRM na komputerach zasadą grupy”", cim.Documentation);
    }

    [Fact]
    public async Task EventLogs_RpcAndAccessErrors_CarryTheRemediesOfTheReader()
    {
        var harness = await ReadinessHarness.CreateAsync();
        harness.Connector.FailHost(ReadinessHarness.Dc02, EventLogAccessError.RpcUnavailable, 1722);
        harness.Connector.FailChannel(ReadinessHarness.Dc01, EventLogSamples.SecurityChannel, EventLogAccessError.AccessDenied);
        harness.Connector.FailHost(ReadinessHarness.TestComputer, EventLogAccessError.AccessDenied);

        var report = await harness.RunAsync(ReadinessDepth.Full);

        var rpc = report.Single(ReadinessCodes.EventLogsSession, ReadinessHarness.Dc02);
        Assert.Equal(ReadinessStatus.Error, rpc.Status);
        Assert.Contains("1722", rpc.Explanation, StringComparison.Ordinal);
        Assert.Contains("Zdalne zarządzanie dziennikiem zdarzeń", rpc.Remedy, StringComparison.Ordinal);
        Assert.All(new[] { ReadinessCodes.EventLogsSecurity, ReadinessCodes.EventLogsCoverage, ReadinessCodes.EventLogsAuditLockout, ReadinessCodes.EventLogsAuditFailedLogons },
            code => Assert.Equal(ReadinessStatus.NotChecked, report.Status(code, ReadinessHarness.Dc02)));
        Assert.Equal(ReadinessStatus.Ok, report.Status(ReadinessCodes.EventLogsSession, ReadinessHarness.Dc01));
        var security = report.Single(ReadinessCodes.EventLogsSecurity, ReadinessHarness.Dc01);
        Assert.Equal(ReadinessStatus.Error, security.Status);
        Assert.Contains("Czytelnicy dziennika zdarzeń", security.Remedy, StringComparison.Ordinal);
        Assert.Equal(ReadinessStatus.NotChecked, report.Status(ReadinessCodes.EventLogsCoverage, ReadinessHarness.Dc01));
        Assert.Equal(ReadinessStatus.Error, report.Status(ReadinessCodes.EventLogsTestComputer));
    }

    [Fact]
    public async Task EventLogs_AStoppedLog_IsAnError_AndAnUnknownReach_IsNotChecked()
    {
        var harness = await ReadinessHarness.CreateAsync();
        harness.Connector.SetCoverage(ReadinessHarness.Dc01, EventLogSamples.SecurityChannel, ReadinessHarness.Coverage(ReadinessHarness.Dc01, EventLogSamples.SecurityChannel,
            harness.Now.AddDays(-20)) with { IsFull = true, Mode = EventLogMode.Retain, Newest = harness.Now.AddDays(-3) });
        harness.Connector.SetCoverage(ReadinessHarness.Dc02, EventLogSamples.SecurityChannel, new EventLogCoverage { Host = ReadinessHarness.Dc02, Channel = EventLogSamples.SecurityChannel });

        var report = await harness.RunAsync(ReadinessDepth.Full);

        var stopped = report.Single(ReadinessCodes.EventLogsCoverage, ReadinessHarness.Dc01);
        Assert.Equal(ReadinessStatus.Error, stopped.Status);
        Assert.EndsWith("Dziennik jest pełny i ustawiony bez nadpisywania: nowe zdarzenia są tracone.", stopped.Explanation, StringComparison.Ordinal);
        Assert.Equal(ReadinessStatus.NotChecked, report.Status(ReadinessCodes.EventLogsCoverage, ReadinessHarness.Dc02));
        Assert.Equal(ReadinessStatus.NotChecked, report.Status(ReadinessCodes.EventLogsAuditLockout, ReadinessHarness.Dc02));
        Assert.StartsWith("dc02.contoso.test: nie da się ustalić", report.Single(ReadinessCodes.EventLogsAuditLockout, ReadinessHarness.Dc02).Explanation, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EventLogs_WithoutAReader_AreNotChecked()
    {
        var harness = await ReadinessHarness.CreateAsync();
        harness.Connector.IsSupported = false;

        var unsupported = await harness.RunAsync(ReadinessDepth.Full);
        var noServices = await harness.RunAsync(ReadinessDepth.Full, services: harness.Services(eventLogs: false, sysvol: false, remote: false));

        Assert.All(unsupported.Results.Where(r => r.Area == ReadinessArea.EventLogs), r => Assert.Equal(ReadinessStatus.NotChecked, r.Status));
        Assert.All(noServices.Results.Where(r => r.Area is ReadinessArea.EventLogs or ReadinessArea.RemoteActions), r => Assert.Equal(ReadinessStatus.NotChecked, r.Status));
        Assert.Equal(ReadinessStatus.NotChecked, noServices.Status(ReadinessCodes.SysvolReachable));
        Assert.Equal(ReadinessStatus.Ok, noServices.Status(ReadinessCodes.SysvolReplication));
        Assert.Empty(harness.Connector.OpenedHosts);
    }

    [Fact]
    public async Task Watchdog_NotConfigured_IsAWarning_AndTheRestIsNotChecked()
    {
        var harness = await ReadinessHarness.CreateAsync();
        await harness.B2.Settings.UpdateAsync(s => s.Watchdog.ServiceUrl = null, Token);

        var report = await harness.RunAsync(ReadinessDepth.Full);

        Assert.Equal(ReadinessStatus.Warning, report.Status(ReadinessCodes.WatchdogConfigured));
        Assert.Equal("Wpisz adres usługi w Narzędzia → Watchdog (https://nazwa:port).", report.Single(ReadinessCodes.WatchdogConfigured).Remedy);
        Assert.All(report.Results.Where(r => r.Area == ReadinessArea.Watchdog).Skip(1), r =>
        {
            Assert.Equal(ReadinessStatus.NotChecked, r.Status);
            Assert.Equal("Nie sprawdzono: brak adresu Watchdoga.", r.Explanation);
        });
        Assert.Empty(harness.Watchdog.Calls.All);
    }

    [Theory]
    [InlineData(WatchdogClientErrorKind.Unreachable, ReadinessCodes.WatchdogReachable, "admng-watchdog.service")]
    [InlineData(WatchdogClientErrorKind.CertificateUntrusted, ReadinessCodes.WatchdogReachable, "potwierdzić certyfikat")]
    [InlineData(WatchdogClientErrorKind.Unauthorized, ReadinessCodes.WatchdogAuthentication, "SPN HTTP")]
    [InlineData(WatchdogClientErrorKind.Forbidden, ReadinessCodes.WatchdogRole, "AdMng-Watchdog-Admins")]
    public async Task Watchdog_Failures_AreTheErrorOfTheStepThatFailed(WatchdogClientErrorKind kind, string failedCode, string remedy)
    {
        var harness = await ReadinessHarness.CreateAsync();
        harness.Watchdog.FailWith(kind);

        var report = await harness.RunAsync(ReadinessDepth.Full);

        var failed = report.Single(failedCode);
        Assert.Equal(ReadinessStatus.Error, failed.Status);
        Assert.Contains(remedy, failed.Remedy, StringComparison.Ordinal);
        var codes = ReadinessCodes.Of(ReadinessArea.Watchdog);
        var index = codes.ToList().IndexOf(failedCode);
        Assert.All(codes.Take(index), code => Assert.Equal(ReadinessStatus.Ok, report.Status(code)));
        Assert.All(codes.Skip(index + 1), code => Assert.Equal(ReadinessStatus.NotChecked, report.Status(code)));
    }

    [Fact]
    public async Task Watchdog_ViewerRole_HealthIssues_AndAnotherDomain_AreShown()
    {
        var harness = await ReadinessHarness.CreateAsync();
        harness.Watchdog.IsAdmin = false;
        harness.Watchdog.Status = SampleStatus.Create() with { Domain = "fabrikam.test" };

        var report = await harness.RunAsync(ReadinessDepth.Full);

        Assert.Equal(ReadinessStatus.Ok, report.Status(ReadinessCodes.WatchdogRole));
        Assert.Equal("Rola: Przeglądający – konfiguracja jest dla tego konta tylko do odczytu.", report.Single(ReadinessCodes.WatchdogRole).Explanation);
        var health = report.Single(ReadinessCodes.WatchdogHealth);
        Assert.Equal(ReadinessStatus.Error, health.Status);
        Assert.StartsWith("Watchdog 1.0.0: ", health.Explanation, StringComparison.Ordinal);
        Assert.Contains("dc02.contoso.test", health.Explanation, StringComparison.Ordinal);
        Assert.EndsWith("Watchdog obserwuje fabrikam.test, a okno jest połączone z contoso.test.", health.Explanation, StringComparison.Ordinal);
        Assert.Contains("zakładkę „Stan”", health.Remedy, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OutlookProfile_ThatCannotConnect_IsAWarning_WithTheOutlookCode()
    {
        var harness = await ReadinessHarness.CreateAsync();
        harness.Outlook.ConnectFailure = new OutlookFailure(OutlookFailureKind.Elevated, OutlookFailureCodes.Elevated, null, null);

        var report = await harness.RunAsync(ReadinessDepth.Full);

        var profile = report.Single(ReadinessCodes.CorrespondenceProfile);
        Assert.Equal(ReadinessStatus.Warning, profile.Status);
        Assert.StartsWith("AD Manager działa jako administrator", profile.Explanation, StringComparison.Ordinal);
        Assert.Equal("KORESPONDENCJA.md, rozdział 13 „Gdy coś nie działa”", profile.Documentation);
        Assert.Equal(ReadinessStatus.Ok, report.Status(ReadinessCodes.CorrespondenceOutlook));
    }

    private static ReadinessServices Services(ReadinessHarness harness, FakeSysvol sysvol) => new(
        harness.B2.Services,
        harness.Results,
        new Core.Csv.CsvCodec(),
        new Core.Computers.WindowsReleaseCatalog(),
        new GroupPolicyReader(),
        new Presentation.GroupPolicy.GroupPolicyServices(harness.B2.Services, harness.B2.Engines, new GroupPolicyPlanner(new GroupPolicyReader(), harness.B2.Time),
            new GroupPolicy.ScriptedGroupPolicyDialogs(), sysvol),
        harness.EventLogs,
        harness.Remote,
        harness.Resolver,
        harness.WatchdogConnection,
        harness.Outlook,
        harness.Computer);
}
