using AdMng.Core.Correspondence.Outlook;
using AdMng.Core.Directory;
using AdMng.Presentation.Localization;
using AdMng.Presentation.Readiness;
using AdMng.Testing;

namespace AdMng.Presentation.Tests.Readiness;

/// <summary>
/// The quick check (design note docs/dev/s-pilot-readiness.md 2): the connection, the directory, this computer and the
/// presence of Outlook – every check's OK and failure paths over the fake directory; the other areas are "not checked", and
/// nothing is written or opened beyond LDAP.
/// </summary>
public sealed class ReadinessQuickCheckTests
{
    private static readonly CancellationToken Token = TestContext.Current.CancellationToken;

    [Fact]
    public async Task QuickCheck_OfAHealthyDomain_IsOk_AndLeavesTheFullOnlyAreasNotChecked()
    {
        var harness = await ReadinessHarness.CreateAsync();
        harness.Builder.AddSchemaObject("msLAPS-Password");
        harness.Session.ClearOperations();

        var report = await harness.RunAsync(ReadinessDepth.Quick);

        Assert.False(report.Cancelled);
        Assert.Equal(ReadinessDepth.Quick, report.Depth);
        Assert.Equal(ReadinessHarness.Dc01, report.Server);
        Assert.Equal(ReadinessCodes.All.Select(c => c.Code), report.Results.Select(r => r.Code));
        Assert.All(report.Results.Where(r => ReadinessCodes.IsQuick(r.Code)), r => Assert.Equal(ReadinessStatus.Ok, r.Status));
        Assert.All(report.Results.Where(r => !ReadinessCodes.IsQuick(r.Code)), r =>
        {
            Assert.Equal(ReadinessStatus.NotChecked, r.Status);
            Assert.Equal("Sprawdzane tylko w pełnym sprawdzeniu.", r.Explanation);
        });

        Assert.Equal("Okno używa konta Windows tego procesu: Negotiate (Kerberos) dla LDAP, SYSVOL, dzienników zdarzeń i WinRM.",
            report.Single(ReadinessCodes.ConnectionLogon).Explanation);
        Assert.Equal("Podpisywanie i szyfrowanie Kerberos na porcie 389.", report.Single(ReadinessCodes.ConnectionTransport).Explanation);
        Assert.Equal("Zegar tego komputera zgadza się z dc01.contoso.test (różnica 0 s).", report.Single(ReadinessCodes.ConnectionClockSkew).Explanation);
        Assert.StartsWith("Okno pracuje na emulatorze PDC dc01.contoso.test", report.Single(ReadinessCodes.ConnectionPdcEmulator).Explanation, StringComparison.Ordinal);
        Assert.Equal("dc01.contoso.test jest kontrolerem domeny z zapisem.", report.Single(ReadinessCodes.ConnectionReadOnlyDc).Explanation);
        Assert.Equal("dc01.contoso.test jest serwerem wykazu globalnego.", report.Single(ReadinessCodes.ConnectionGlobalCatalog).Explanation);
        Assert.Equal("Windows Server 2016 (poziom 7).", report.Single(ReadinessCodes.DirectoryDomainLevel).Explanation);
        Assert.Equal("Windows Server 2016 (poziom 7).", report.Single(ReadinessCodes.DirectoryForestLevel).Explanation);
        Assert.StartsWith("Włączony", report.Single(ReadinessCodes.DirectoryRecycleBin).Explanation, StringComparison.Ordinal);
        Assert.StartsWith("Kontener Password Settings Container jest czytelny, ale nie widać", report.Single(ReadinessCodes.DirectoryPasswordSettings).Explanation, StringComparison.Ordinal);
        Assert.Equal("Schemat zawiera Windows LAPS (msLAPS-Password).", report.Single(ReadinessCodes.DirectoryWindowsLaps).Explanation);
        Assert.Equal("Klasyczny Outlook jest zainstalowany.", report.Single(ReadinessCodes.CorrespondenceOutlook).Explanation);
        Assert.Equal(ReadinessStatus.NotChecked, report.Status(ReadinessCodes.CorrespondenceProfile));
        Assert.StartsWith("Windows 11 Enterprise (Windows 11 24H2) (kompilacja 26100.4652), wspierany do ", report.Single(ReadinessCodes.ComputerWindows).Explanation, StringComparison.Ordinal);
        Assert.Equal("Zainstalowano WebView2 Runtime 142.0.3595.53: podglądy wiadomości są renderowane.", report.Single(ReadinessCodes.ComputerWebView2).Explanation);

        // Read-only and LDAP only: no write, no SYSVOL, no event log, no computer, no Watchdog call, Outlook not started.
        Assert.Empty(harness.Session.Writes);
        Assert.Empty(harness.Sysvol.Calls);
        Assert.Empty(harness.Connector.OpenedHosts);
        Assert.Empty(harness.Remote.Calls);
        Assert.Empty(harness.Resolver.Lookups);
        Assert.Empty(harness.Watchdog.Calls.All);
        Assert.Equal(0, harness.Outlook.Connections);
    }

    [Fact]
    public async Task EveryRow_HasItsTitle_AndTheChapterToRead()
    {
        var harness = await ReadinessHarness.CreateAsync("en-US");

        var report = await harness.RunAsync(ReadinessDepth.Quick);

        Assert.All(report.Results, r =>
        {
            Assert.False(string.IsNullOrWhiteSpace(r.Title));
            Assert.DoesNotContain("[", r.Title, StringComparison.Ordinal);
            Assert.EndsWith("”", r.Documentation, StringComparison.Ordinal);
            Assert.Contains(".md, chapter ", r.Documentation, StringComparison.Ordinal);
        });
        Assert.Equal("Clock difference to the DC", report.Single(ReadinessCodes.ConnectionClockSkew).Title);
        Assert.Equal("WATCHDOG-LINUX.md, chapter 1.4 “Czas w domenie”", report.Single(ReadinessCodes.ConnectionClockSkew).Documentation);
    }

    [Fact]
    public async Task Logon_SimpleBind_IsAWarning_ThatNamesTheWindowsLogon()
    {
        var harness = await ReadinessHarness.CreateAsync();
        await harness.ReconnectAsync(info: i => i with { Authentication = AuthenticationMode.SimpleBind, Security = TransportSecurity.Tls, Port = 636 });

        var report = await harness.RunAsync(ReadinessDepth.Full);

        var logon = report.Single(ReadinessCodes.ConnectionLogon);
        Assert.Equal(ReadinessStatus.Warning, logon.Status);
        Assert.Contains("prostym powiązaniem LDAP", logon.Explanation, StringComparison.Ordinal);
        Assert.Equal("Połącz okno kontem Windows albo poświadczeniami domeny (DOMENA\\konto).", logon.Remedy);
        Assert.Equal("LDAPS (TLS) na porcie 636.", report.Single(ReadinessCodes.ConnectionTransport).Explanation);

        // The event logs need a Windows logon: every row says so (the reason of the event log documents) with the remedy.
        Assert.All(report.Results.Where(r => r.Area == ReadinessArea.EventLogs), r =>
        {
            Assert.Equal(ReadinessStatus.NotChecked, r.Status);
            Assert.Equal(logon.Remedy, r.Remedy);
        });
        Assert.Empty(harness.Connector.OpenedHosts);
    }

    [Fact]
    public async Task Logon_WithCredentials_IsOk()
    {
        var harness = await ReadinessHarness.CreateAsync();
        await harness.ReconnectAsync(info: i => i with { Authentication = AuthenticationMode.Credentials });

        var report = await harness.RunAsync(ReadinessDepth.Quick);

        Assert.Equal(ReadinessStatus.Ok, report.Status(ReadinessCodes.ConnectionLogon));
        Assert.StartsWith("Okno używa poświadczeń podanych przy łączeniu", report.Single(ReadinessCodes.ConnectionLogon).Explanation, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(TransportSecurity.None, null, ReadinessStatus.Error)]
    [InlineData(TransportSecurity.SignAndSeal, 0, ReadinessStatus.Warning)]
    [InlineData(TransportSecurity.SignAndSeal, 256, ReadinessStatus.Ok)]
    [InlineData(TransportSecurity.StartTls, null, ReadinessStatus.Ok)]
    public async Task Transport_UnprotectedOrUnsealed_IsReported(TransportSecurity security, int? ssf, ReadinessStatus expected)
    {
        var harness = await ReadinessHarness.CreateAsync();
        await harness.ReconnectAsync(info: i => i with { Security = security, SecurityStrengthFactor = ssf });

        var report = await harness.RunAsync(ReadinessDepth.Quick);

        var transport = report.Single(ReadinessCodes.ConnectionTransport);
        Assert.Equal(expected, transport.Status);
        Assert.Equal(expected == ReadinessStatus.Ok, transport.Remedy.Length == 0);
    }

    [Theory]
    [InlineData(90, ReadinessStatus.Ok, "różnica 90 s")]
    [InlineData(-180, ReadinessStatus.Warning, "o 180 s; Kerberos przestaje działać przy 5 minutach")]
    [InlineData(600, ReadinessStatus.Error, "o 600 s – więcej, niż przyjmuje Kerberos")]
    public async Task ClockSkew_IsMeasuredAgainstTheDcsCurrentTime(int seconds, ReadinessStatus expected, string text)
    {
        var harness = await ReadinessHarness.CreateAsync();
        await harness.ReconnectAsync(clockOffset: TimeSpan.FromSeconds(seconds));

        var report = await harness.RunAsync(ReadinessDepth.Quick);

        var clock = report.Single(ReadinessCodes.ConnectionClockSkew);
        Assert.Equal(expected, clock.Status);
        Assert.Contains(text, clock.Explanation, StringComparison.Ordinal);
        Assert.Equal(ReadinessHarness.Dc01, clock.Host);
        Assert.Equal(expected != ReadinessStatus.Ok, clock.Remedy.Contains("w32tm /resync", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AnotherDc_IsNotThePdcEmulator_NorAGlobalCatalog_AndTheRemedyNamesThePdc()
    {
        var harness = await ReadinessHarness.CreateAsync();
        await harness.ReconnectAsync(ReadinessHarness.Dc02);

        var report = await harness.RunAsync(ReadinessDepth.Quick);

        var pdc = report.Single(ReadinessCodes.ConnectionPdcEmulator);
        Assert.Equal(ReadinessStatus.Warning, pdc.Status);
        Assert.StartsWith("Okno pracuje na dc02.contoso.test, a emulatorem PDC jest dc01.contoso.test.", pdc.Explanation, StringComparison.Ordinal);
        Assert.Equal("Przed edycją zasad grupy użyj polecenia „Zmień kontroler domeny…” i wybierz dc01.contoso.test.", pdc.Remedy);
        Assert.Equal("ZASADY-GRUPY.md, rozdział 13 „Kontroler domeny, emulator PDC i SYSVOL”", pdc.Documentation);
        Assert.Equal(ReadinessStatus.Warning, report.Status(ReadinessCodes.ConnectionGlobalCatalog));
        Assert.Equal(ReadinessStatus.Ok, report.Status(ReadinessCodes.ConnectionReadOnlyDc));
    }

    [Fact]
    public async Task ReadOnlyDomainController_IsAnError()
    {
        var harness = await ReadinessHarness.CreateAsync();
        harness.Builder.AddDomainController("dc03.contoso.test", new Guid("3f0f0f0f-1111-2222-3333-444455556666"), isReadOnly: true);
        await harness.ReconnectAsync("dc03.contoso.test");

        var report = await harness.RunAsync(ReadinessDepth.Quick);

        var rodc = report.Single(ReadinessCodes.ConnectionReadOnlyDc);
        Assert.Equal(ReadinessStatus.Error, rodc.Status);
        Assert.Equal("dc03.contoso.test jest kontrolerem tylko do odczytu (RODC): każda zmiana zostanie odrzucona.", rodc.Explanation);
        Assert.Contains("kontroler z zapisem", rodc.Remedy, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnknownDc_AndNoRoleOwner_AreNotChecked()
    {
        var harness = await ReadinessHarness.CreateAsync();
        harness.Session.SetValues(harness.Session.Info.DefaultNamingContext, "fSMORoleOwner", "CN=NTDS Settings,CN=GONE,CN=Servers,CN=Default-First-Site-Name,CN=Sites," + harness.Builder.ConfigurationDn);
        await harness.ReconnectAsync("dc09.contoso.test");

        var report = await harness.RunAsync(ReadinessDepth.Quick);

        Assert.Equal(ReadinessStatus.NotChecked, report.Status(ReadinessCodes.ConnectionPdcEmulator));
        Assert.Equal("Nie udało się odczytać emulatora PDC (fSMORoleOwner domeny).", report.Single(ReadinessCodes.ConnectionPdcEmulator).Explanation);
        Assert.Equal(ReadinessStatus.NotChecked, report.Status(ReadinessCodes.ConnectionReadOnlyDc));
        Assert.Equal(ReadinessStatus.NotChecked, report.Status(ReadinessCodes.ConnectionGlobalCatalog));
    }

    [Theory]
    [InlineData(10, ReadinessStatus.Ok, "Windows Server 2025 (poziom 10).")]
    [InlineData(6, ReadinessStatus.Warning, "Windows Server 2012 R2 (poziom 6): AD Manager działa")]
    [InlineData(2, ReadinessStatus.Error, "Windows Server 2003 (poziom 2): poniżej Windows Server 2008")]
    public async Task FunctionalLevels_AreReadLive_FromTheDomainHeadAndThePartitions(int level, ReadinessStatus expected, string text)
    {
        var harness = await ReadinessHarness.CreateAsync();
        harness.Session.SetValues(harness.Session.Info.DefaultNamingContext, "msDS-Behavior-Version", level);
        harness.Session.SetValues(harness.Builder.PartitionsDn, "msDS-Behavior-Version", level);

        var report = await harness.RunAsync(ReadinessDepth.Quick);

        foreach (var code in new[] { ReadinessCodes.DirectoryDomainLevel, ReadinessCodes.DirectoryForestLevel })
        {
            Assert.Equal(expected, report.Status(code));
            Assert.StartsWith(text, report.Single(code).Explanation, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task RecycleBin_Disabled_IsAWarning_WithTheWayToEnableIt()
    {
        var harness = await ReadinessHarness.CreateAsync();
        harness.Session.SetValues(harness.Builder.PartitionsDn, "msDS-EnabledFeature", $"CN=Some Other Feature,CN=Optional Features,{harness.Builder.DirectoryServiceDn}");

        var report = await harness.RunAsync(ReadinessDepth.Quick);

        var recycleBin = report.Single(ReadinessCodes.DirectoryRecycleBin);
        Assert.Equal(ReadinessStatus.Warning, recycleBin.Status);
        Assert.Contains("Enable-ADOptionalFeature", recycleBin.Remedy, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PasswordSettings_VisibleOnes_AreCounted_AndAMissingContainerIsAWarning()
    {
        var harness = await ReadinessHarness.CreateAsync();
        harness.Builder.AddPasswordSettings("PSO-Księgowość", 10, TimeSpan.FromMinutes(15), [harness.Company.GroupAccounting]);

        var visible = await harness.RunAsync(ReadinessDepth.Quick);
        await harness.Session.DeleteAsync(harness.Builder.PasswordSettingsContainerDn, subtree: true, Token);
        var missing = await harness.RunAsync(ReadinessDepth.Quick);

        Assert.Equal(ReadinessStatus.Ok, visible.Status(ReadinessCodes.DirectoryPasswordSettings));
        Assert.EndsWith("widocznych obiektów zasad haseł: 1.", visible.Single(ReadinessCodes.DirectoryPasswordSettings).Explanation, StringComparison.Ordinal);
        Assert.Equal(ReadinessStatus.Warning, missing.Status(ReadinessCodes.DirectoryPasswordSettings));
        Assert.StartsWith("Nie można odczytać kontenera Password Settings Container", missing.Single(ReadinessCodes.DirectoryPasswordSettings).Explanation, StringComparison.Ordinal);
        Assert.Equal("DZIENNIKI-ZDARZEN.md, rozdział 4 „Skąd blokada konta”", missing.Single(ReadinessCodes.DirectoryPasswordSettings).Documentation);
    }

    [Fact]
    public async Task Laps_LegacyOnly_OrNone_IsAWarning()
    {
        var harness = await ReadinessHarness.CreateAsync();

        var none = await harness.RunAsync(ReadinessDepth.Quick);
        harness.Builder.AddSchemaObject("ms-Mcs-AdmPwd");
        var legacy = await harness.RunAsync(ReadinessDepth.Quick);

        Assert.Equal(ReadinessStatus.Warning, none.Status(ReadinessCodes.DirectoryWindowsLaps));
        Assert.StartsWith("Schemat nie zawiera atrybutów LAPS", none.Single(ReadinessCodes.DirectoryWindowsLaps).Explanation, StringComparison.Ordinal);
        Assert.Equal(ReadinessStatus.Warning, legacy.Status(ReadinessCodes.DirectoryWindowsLaps));
        Assert.StartsWith("Schemat zawiera tylko dawny LAPS (ms-Mcs-AdmPwd)", legacy.Single(ReadinessCodes.DirectoryWindowsLaps).Explanation, StringComparison.Ordinal);
        Assert.Contains("Update-LapsADSchema", legacy.Single(ReadinessCodes.DirectoryWindowsLaps).Remedy, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ADirectoryReadThatFails_IsThatChecksError_AndTheOthersStillRun()
    {
        var harness = await ReadinessHarness.CreateAsync();
        harness.Session.FailNext(FakeOperationKind.Get, harness.Session.Info.DefaultNamingContext, DirectoryErrorKind.Busy, times: 10);

        var report = await harness.RunAsync(ReadinessDepth.Quick);

        var level = report.Single(ReadinessCodes.DirectoryDomainLevel);
        Assert.Equal(ReadinessStatus.Error, level.Status);
        Assert.StartsWith("Sprawdzenie nie powiodło się: ", level.Explanation, StringComparison.Ordinal);
        Assert.Equal(ReadinessStatus.Ok, report.Status(ReadinessCodes.DirectoryRecycleBin));
        Assert.Equal(ReadinessStatus.Ok, report.Status(ReadinessCodes.ComputerWindows));
    }

    [Theory]
    [InlineData("Windows 10 Pro", 19045, ReadinessStatus.Error, "wsparcie zakończyło się")]
    [InlineData("Windows 11 Enterprise", 99999, ReadinessStatus.Ok, "AD Manager nie zna daty końca jego wsparcia")]
    public async Task ThisComputer_WindowsRelease_IsCheckedAgainstTheReleaseCatalogue(string product, int build, ReadinessStatus expected, string text)
    {
        var harness = await ReadinessHarness.CreateAsync();
        harness.Computer.OperatingSystem = product;
        harness.Computer.Build = build;

        var report = await harness.RunAsync(ReadinessDepth.Quick);

        Assert.Equal(expected, report.Status(ReadinessCodes.ComputerWindows));
        Assert.Contains(text, report.Single(ReadinessCodes.ComputerWindows).Explanation, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ThisComputer_NotWindows_NoWebView2_OrWebView2SwitchedOff_AreReported()
    {
        var harness = await ReadinessHarness.CreateAsync();
        harness.Computer.IsWindows = false;
        harness.Computer.OperatingSystem = "Debian GNU/Linux 13";
        harness.Computer.WebView2Version = null;

        var missing = await harness.RunAsync(ReadinessDepth.Quick);
        harness.Computer.IsWebView2Disabled = true;
        var disabled = await harness.RunAsync(ReadinessDepth.Quick);
        harness.Computer.CanProbeWebView2 = false;
        var unknown = await harness.RunAsync(ReadinessDepth.Quick);

        Assert.Equal(ReadinessStatus.Error, missing.Status(ReadinessCodes.ComputerWindows));
        Assert.Equal("AD Manager działa tylko w Windows; ten komputer ma Debian GNU/Linux 13.", missing.Single(ReadinessCodes.ComputerWindows).Explanation);
        Assert.Equal(ReadinessStatus.Warning, missing.Status(ReadinessCodes.ComputerWebView2));
        Assert.Equal("Zainstaluj Microsoft Edge WebView2 Runtime (Evergreen).", missing.Single(ReadinessCodes.ComputerWebView2).Remedy);
        Assert.Equal(ReadinessStatus.NotChecked, disabled.Status(ReadinessCodes.ComputerWebView2));
        Assert.Contains("ADMNG_UI_NO_WEBVIEW2=1", disabled.Single(ReadinessCodes.ComputerWebView2).Explanation, StringComparison.Ordinal);
        Assert.Equal(ReadinessStatus.NotChecked, unknown.Status(ReadinessCodes.ComputerWebView2));
    }

    [Fact]
    public async Task Outlook_NotInstalled_IsAWarning_AndAHostWithoutOutlookIsNotChecked()
    {
        var harness = await ReadinessHarness.CreateAsync();
        harness.Outlook.Availability = OutlookAvailability.NotInstalled;

        var notInstalled = await harness.RunAsync(ReadinessDepth.Full);
        var noClient = await harness.RunAsync(ReadinessDepth.Quick, services: harness.Services(outlook: false));

        Assert.Equal(ReadinessStatus.Warning, notInstalled.Status(ReadinessCodes.CorrespondenceOutlook));
        Assert.Equal("KORESPONDENCJA.md, rozdział 1 „Wymagania”", notInstalled.Single(ReadinessCodes.CorrespondenceOutlook).Documentation);
        Assert.Equal(ReadinessStatus.NotChecked, notInstalled.Status(ReadinessCodes.CorrespondenceProfile));
        Assert.Equal(0, harness.Outlook.Connections);
        Assert.All(noClient.Results.Where(r => r.Area == ReadinessArea.Correspondence), r => Assert.Equal(ReadinessStatus.NotChecked, r.Status));
    }

    [Fact]
    public void Codes_AreUnique_HaveTitlesAndChapters_InBothLanguages()
    {
        Assert.Equal(ReadinessCodes.All.Count, ReadinessCodes.All.Select(c => c.Code).Distinct(StringComparer.Ordinal).Count());
        Assert.All(ReadinessCodes.All, c => Assert.Matches(@"^[A-Z][A-Za-z]+\.[A-Z][A-Za-z0-9]+$", c.Code));
        var en = new ResxLocalizer(System.Globalization.CultureInfo.GetCultureInfo("en-US"));
        var pl = new ResxLocalizer(System.Globalization.CultureInfo.GetCultureInfo("pl-PL"));
        Assert.All(ReadinessText.AllKeys(), key =>
        {
            Assert.True(en.Contains(key), key);
            Assert.True(pl.Contains(key), key);
        });
        Assert.Throws<ArgumentException>(() => ReadinessCodes.AreaOf("Nieznany.Kod"));
    }
}
