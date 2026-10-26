using System.Globalization;
using System.Reflection;
using AdMng.Presentation.Localization;
using AdMng.Presentation.Watchdog;
using AdMng.Watchdog.Client;
using AdMng.Watchdog.Contracts.Api;
using AdMng.Watchdog.Contracts.Configuration;
using AdMng.Watchdog.Contracts.Events;
using AdMng.Watchdog.Core.Rules;
using AdMng.Watchdog.Testing.Client;
using EventAttribution = AdMng.Watchdog.Contracts.Events.Attribution;

namespace AdMng.Presentation.Tests.Watchdog;

/// <summary>
/// AD Manager side of the Linux service (stage B design 18 items 2–5): the Status sections „Konto usługi”, „Kolektor WEF”,
/// „Zawartość GPO” and „Host”, GPO setting rows "ustawienie: było → jest", the collector texts of reasons, the "Źródła i
/// retencja" configuration and a localized text for every contract value.
/// </summary>
public sealed class WatchdogStageBTests
{
    private static readonly CultureInfo Polish = CultureInfo.GetCultureInfo("pl-PL");

    private static readonly ObjectRef DefaultDomainPolicy = new()
    {
        ObjectGuid = Guid.Parse("8d3a5c1e-4f2b-4a6d-9e0c-1b2a3c4d5e6f"),
        Dn = "CN={31B2F340-016D-11D2-945F-00C04FB984F9},CN=Policies,CN=System," + SamplePrincipals.DomainDn,
        Name = "{31B2F340-016D-11D2-945F-00C04FB984F9}",
        DisplayName = "Default Domain Policy",
        Kind = WatchObjectKind.GroupPolicy,
    };

    [Fact]
    public async Task Status_TheLinuxServiceToday_ShowsAccountAndHost_AndTheSectionsThatComeLater()
    {
        using var h = await WatchdogHarness.CreateAsync(culture: Polish);
        h.Client.Status = SampleStatus.CreateLinux();
        await h.Ui.RunAsync(async () =>
        {
            var status = new WatchdogStatusViewModel(h.Services);
            Assert.Equal("Konto usługi", status.ServiceAccountSection.Title);
            Assert.False(status.ServiceAccountSection.IsReported);

            await status.LoadAsync();

            var account = status.ServiceAccountSection;
            Assert.True(account.IsReported);
            Assert.Equal(HealthState.Warning, account.State);
            Assert.Equal("ADMNG-WD$@CONTOSO.TEST", Field(account, "Podmiot Kerberos"));
            Assert.Equal("7", Field(account, "Wersja klucza w keytabie (kvno)"));
            Assert.Equal("Tak", Field(account, "Zgodność keytabu z AD"));
            Assert.Equal("OK – keytab zgodny z AD", Field(account, "Wynik zmiany hasła"));
            Assert.Equal("AES128, AES256 (0x18)", Field(account, "Typy szyfrowania"));
            Assert.Equal("Nie", Field(account, "Członkostwo w grupach uprzywilejowanych"));
            Assert.Equal("Nie", Field(account, "Odmowa zapisu dla SELF (ACE)"));
            var drift = Assert.Single(account.Problems);
            Assert.Equal("serviceAccount.drift", drift.Code);
            Assert.StartsWith("Przywróć ustawienia z 2.5", drift.Remedy, StringComparison.Ordinal);

            var host = status.HostSection;
            Assert.True(host.IsReported);
            Assert.Equal(HealthState.Ok, host.State);
            Assert.Equal("Debian GNU/Linux 13 (trixie)", Field(host, "System"));
            Assert.Equal(".NET 10.0.13 · jednostka systemd", Field(host, "Środowisko .NET"));
            Assert.Equal("TPM2 + klucz hosta", Field(host, "Wariant klucza sekretów"));
            Assert.Equal("+2 s", Field(host, "Różnica zegara względem DC"));
            Assert.Equal("256 · SignAndSeal", Field(host, "Poziom szyfrowania LDAP (SSF)"));
            Assert.Equal(WatchdogStatusViewModel.FormatBytes(38_654_705_664, Polish), Field(host, "Wolne miejsce"));

            // WEF and GPO content come with later milestones: "not reported", no crash, no values.
            foreach (var section in new[] { status.CollectorSection, status.GpoContentSection })
            {
                Assert.False(section.IsReported);
                Assert.Equal(HealthState.Unknown, section.State);
                Assert.Equal(h.Ad.Localizer[L.WatchdogLinux.SectionNotReported], section.Message);
                Assert.Empty(section.Fields);
            }

            Assert.Equal(h.Ad.Localizer.Format(L.Watchdog.StatusAttributionEnabled, "OpenWEC (WEF)"), status.AttributionText);
        });
        h.AssertNoUiErrors();
    }

    [Fact]
    public async Task Status_CollectorAndGpoContent_ShowEveryDcAndTheReadProgress()
    {
        using var h = await WatchdogHarness.CreateAsync(culture: Polish);
        h.Client.Status = SampleStatus.CreateLinux(collectorAndGpo: true);
        await h.Ui.RunAsync(async () =>
        {
            var status = new WatchdogStatusViewModel(h.Services);
            await status.LoadAsync();

            var collector = status.CollectorSection;
            Assert.True(collector.IsReported);
            Assert.Equal(HealthState.Error, collector.State);
            Assert.Equal(["dc01.contoso.test", "dc02.contoso.test"], collector.DomainControllers.Select(d => d.Dc));
            var dc01 = collector.DomainControllers[0];
            Assert.Equal("4,0 s", dc01.LagText);
            Assert.Equal("1,5 MB", dc01.BacklogText);
            Assert.Equal("0", dc01.RejectedText);
            Assert.Null(dc01.ErrorCode);
            Assert.Equal("notForwarding", collector.DomainControllers[1].ErrorCode);
            Assert.Equal(string.Empty, collector.DomainControllers[1].LastRecordText);
            var silent = Assert.Single(collector.Problems);
            Assert.Equal("DC dc02.contoso.test nie przekazuje zdarzeń", silent.Title);
            Assert.StartsWith("DC nie przekazuje zdarzeń – sprawdź GPO subskrypcji", silent.Remedy, StringComparison.Ordinal);

            var gpo = status.GpoContentSection;
            Assert.Equal(HealthState.Warning, gpo.State);
            Assert.Equal("14 z 14 GPO, w kolejce: 0", Field(gpo, "Postęp odczytu"));
            Assert.Equal("1", Field(gpo, "Błędy odczytu"));
            Assert.Equal("dc01.contoso.test", Field(gpo, "Kontroler domeny"));
            Assert.Equal("gpo.6ac1786c-016f-11d2-945f-00c04fb984f9.parseError", Assert.Single(gpo.Problems).Code);
        });
        h.AssertNoUiErrors();
    }

    [Fact]
    public async Task Status_OfTheWindowsService_RendersTheNewSectionsAsNotReported()
    {
        using var h = await WatchdogHarness.CreateAsync();
        await h.Ui.RunAsync(async () =>
        {
            var status = new WatchdogStatusViewModel(h.Services);
            await status.LoadAsync();

            Assert.All(
                new[] { status.ServiceAccountSection, status.CollectorSection, status.GpoContentSection, status.HostSection },
                s => Assert.False(s.IsReported));
            Assert.Equal(h.Ad.Localizer.Format(L.Watchdog.StatusAttributionEnabled, h.Ad.Localizer.AttributionSourceName(AttributionSourceKind.DomainControllers)), status.AttributionText);

            h.Client.Status = SampleStatus.CreateLinux() with
            {
                GpoContent = new GpoContentStatusDto { State = HealthState.Ok, Enabled = false },
            };
            await status.LoadAsync();
            Assert.Equal(HealthState.Disabled, status.GpoContentSection.State);
            Assert.Equal(h.Ad.Localizer[L.WatchdogLinux.GpoContentDisabled], status.GpoContentSection.Message);
        });
        h.AssertNoUiErrors();
    }

    [Theory]
    [InlineData(2.4, "+2 s")]
    [InlineData(-75, "−75 s")]
    [InlineData(0.2, "0 s")]
    public void ClockSkew_IsSigned(double seconds, string expected) =>
        Assert.Equal(expected, WatchdogStatusSectionViewModel.FormatSkew(TimeSpan.FromSeconds(seconds), CultureInfo.InvariantCulture));

    [Fact]
    public async Task GpoSettingsChange_ReadsSettingWasIs_WithAReadablePath()
    {
        using var h = await WatchdogHarness.CreateAsync(culture: Polish);
        var stored = h.Client.AddEvent(new WatchEvent
        {
            EventId = Guid.Parse("01929a3c-7b10-7cc0-9a51-6e0b2f4c8d10"),
            Kind = WatchEventKind.GroupPolicySettingsChanged,
            Severity = Severity.Critical,
            Object = DefaultDomainPolicy,
            DetectedAt = SampleEvents.Now,
            Source = DetectionSource.IncrementalPoll,
            Tags = [EventTags.GppPassword],
            Changes =
            [
                new AttributeChange
                {
                    Attribute = "Computer/Security/Privilege Rights/SeDebugPrivilege",
                    OldValues = ["*S-1-5-32-544"],
                    NewValues = ["*S-1-5-32-544", "*S-1-5-21-1004336348-1177238915-682003330-1180"],
                    Added = ["*S-1-5-21-1004336348-1177238915-682003330-1180"],
                },
                new AttributeChange { Attribute = @"Computer/Registry/HKLM\Software\Policies\Microsoft\Windows\WinRM\Service!AllowBasic", OldValues = ["REG_DWORD:0"], NewValues = ["REG_DWORD:1"] },
                new AttributeChange { Attribute = "Computer/Preferences/Groups/{6F0C2B4D-1A2B-4C3D-8E9F-0A1B2C3D4E5F}/cpassword", Redacted = true, NewCount = 1 },
                new AttributeChange { Attribute = "File/Machine/Preferences/Groups/Groups.xml", OldValues = ["sha256:ab12 size:1024"], NewValues = ["unreadable"] },
                new AttributeChange { Attribute = "gpo.moreChanges", ValueKind = AttributeValueKind.Number, Truncated = true, OldCount = 12, NewCount = 211 },
            ],
        });
        await h.Ui.RunAsync(async () =>
        {
            var details = new WatchdogEventDetailsViewModel(h.Services);
            await details.ShowAsync(new WatchdogEventRowViewModel(stored, h.Ad.Localizer, TimeZoneInfo.Utc));

            Assert.Equal("Zmieniono ustawienia GPO", details.Title);
            Assert.Equal(h.Ad.Localizer.TagName(EventTags.GppPassword), details.TagsText);
            var rows = details.Changes;
            Assert.Equal("Computer › Security › Privilege Rights › SeDebugPrivilege", rows[0].Attribute);
            Assert.Equal("Computer/Security/Privilege Rights/SeDebugPrivilege", rows[0].LdapName);
            Assert.Equal("*S-1-5-32-544", rows[0].OldText);
            Assert.Equal("+*S-1-5-21-1004336348-1177238915-682003330-1180", rows[0].ChangeText);
            Assert.Equal(@"Computer › Registry › HKLM\Software\Policies\Microsoft\Windows\WinRM\Service › AllowBasic", rows[1].Attribute);
            Assert.Equal("REG_DWORD:0", rows[1].OldText);
            Assert.Equal("REG_DWORD:1", rows[1].NewText);
            Assert.True(rows[2].IsRedacted);
            Assert.Equal("nie udało się odczytać", rows[3].NewText);
            Assert.Equal("Pozostałe ustawienia", rows[4].Attribute);
            Assert.Equal("nie pokazano 12 z 211 zmienionych ustawień", rows[4].NewText);
        });
        h.AssertNoUiErrors();
    }

    [Fact]
    public async Task AuditSettingsChange_AndCollectorReasons_AreExplained()
    {
        using var h = await WatchdogHarness.CreateAsync(culture: Polish);
        var stored = h.Client.AddEvent(new WatchEvent
        {
            EventId = Guid.Parse("01929a3c-7b10-7cc0-9a51-6e0b2f4c8d11"),
            Kind = WatchEventKind.SecurityDescriptorChanged,
            Severity = Severity.Critical,
            Object = DefaultDomainPolicy with { Kind = WatchObjectKind.Domain, Dn = SamplePrincipals.DomainDn, Name = "contoso" },
            DetectedAt = SampleEvents.Now,
            Source = DetectionSource.IncrementalPoll,
            Tags = [EventTags.AuditSettings],
            Changes = [new AttributeChange { Attribute = "nTSecurityDescriptor" }],
            Attribution = new EventAttribution { Status = AttributionStatus.Unknown, Reason = AttributionReasons.DcUnreachable },
        });
        await h.Ui.RunAsync(async () =>
        {
            var details = new WatchdogEventDetailsViewModel(h.Services);
            await details.ShowAsync(new WatchdogEventRowViewModel(stored, h.Ad.Localizer, TimeZoneInfo.Utc));

            Assert.Equal(h.Ad.Localizer[L.WatchdogLinux.ValueAuditSettingsChanged], Assert.Single(details.Changes).NewText);
            Assert.Equal(h.Ad.Localizer.ReasonText(AttributionReasons.DcUnreachable), details.ReasonText);

            details.CollectorMode = true;
            Assert.Equal(h.Ad.Localizer[WatchdogDisplay.ReasonKey(AttributionReasons.CollectorVariant(AttributionReasons.DcUnreachable))], details.ReasonText);
            Assert.Equal("Sprawdź, czy DC przekazuje zdarzenia (GPO subskrypcji, WinRM, zapora 5985) – Stan → Kolektor WEF.", details.RemedyText);
        });
        h.AssertNoUiErrors();
    }

    [Fact]
    public async Task TheTab_FollowsTheCollectorModeOfTheService()
    {
        using var h = await WatchdogHarness.CreateAsync();
        h.Client.Status = SampleStatus.CreateLinux();
        await h.Ui.RunAsync(async () =>
        {
            var tab = await h.OpenAsync();
            Assert.True(tab.Events.Details.CollectorMode);
        });
        h.AssertNoUiErrors();
    }

    [Fact]
    public async Task Sources_ShowOpenWecOnly_AndEditTheCollectorGpoAndRetentionSettings()
    {
        using var h = await WatchdogHarness.CreateAsync(culture: Polish);
        await h.Ui.RunAsync(async () =>
        {
            var tab = await h.OpenAsync();
            var sources = tab.Sources;

            Assert.True(sources.CanEdit);
            var option = Assert.Single(sources.SourceOptions);
            Assert.Equal(AttributionSourceKind.OpenWec, option.Value);
            Assert.Equal("OpenWEC (WEF)", option.Text);
            Assert.False(sources.HasSourceWarning);
            Assert.Equal("15", sources.CollectorSilenceWarningMinutes);
            Assert.Equal("60", sources.CollectorSilenceErrorMinutes);
            Assert.Equal("120", sources.GpoSysvolLagToleranceMinutes);
            Assert.Equal("48", sources.LogonRecordsHours);

            sources.DropAnonymousLogons = false;
            sources.CollectorSilenceWarningMinutes = "20";
            sources.GpoContentEnabled = false;
            sources.GpoMaxFiles = "3000";
            sources.LogonRecordsHours = "72";
            sources.GpoMaxFileMb = "dużo";

            Assert.True(tab.Config.IsDirty);
            var working = tab.Config.Working!;
            Assert.False(working.Attribution.DropAnonymousLogons);
            Assert.Equal(TimeSpan.FromMinutes(20), working.Attribution.CollectorSilenceWarning);
            Assert.False(working.Directory.GpoContent.Enabled);
            Assert.Equal(3000, working.Directory.GpoContent.MaxFiles);
            Assert.Equal(5, working.Directory.GpoContent.MaxFileMb);
            Assert.Equal(72, working.Retention.LogonRecordsHours);

            Assert.True(await tab.Config.SaveAsync());
            var saved = h.Client.SavedEnvelopes[^1].Configuration;
            Assert.Equal(72, saved.Retention.LogonRecordsHours);
            Assert.Equal(TimeSpan.FromMinutes(20), saved.Attribution.CollectorSilenceWarning);
        });
        h.AssertNoUiErrors();
    }

    [Fact]
    public async Task Sources_AStoredWindowsSource_IsShownWithAHint_AndViewersReadOnly()
    {
        using var h = await WatchdogHarness.CreateAsync(culture: Polish);
        var envelope = h.Client.StoredConfig;
        h.Client.StoredConfig = envelope with
        {
            Configuration = envelope.Configuration with
            {
                Attribution = envelope.Configuration.Attribution with { Source = AttributionSourceKind.DomainControllers },
            },
        };
        await h.Ui.RunAsync(async () =>
        {
            var tab = await h.OpenAsync();
            var sources = tab.Sources;

            Assert.Equal([AttributionSourceKind.OpenWec, AttributionSourceKind.DomainControllers], sources.SourceOptions.Select(o => o.Value));
            Assert.Equal(AttributionSourceKind.DomainControllers, sources.Source?.Value);
            Assert.Equal("To źródło działa tylko z usługą na Windows – wybierz OpenWEC (WEF).", sources.SourceWarning);

            sources.Source = sources.SourceOptions[0];
            Assert.False(sources.HasSourceWarning);
            Assert.Equal(AttributionSourceKind.OpenWec, tab.Config.Working!.Attribution.Source);
        });

        h.Client.IsAdmin = false;
        await h.Ui.RunAsync(async () =>
        {
            var tab = await h.OpenAsync();
            Assert.False(tab.Sources.CanEdit);
            tab.Sources.LogonRecordsHours = "1";
            Assert.False(tab.Config.IsDirty);
        });
        h.AssertNoUiErrors();
    }

    /// <summary>Every value of the contracts the GUI shows (kinds, tags, reasons, health codes and checks, validation codes,
    /// keytab results, secrets-key variants, attribution sources, client errors, sub-tabs) has an English and a Polish text
    /// of its own – never the raw value.</summary>
    [Fact]
    public void EveryContractValue_HasALocalizedText()
    {
        var pl = new ResxLocalizer(Polish);
        var en = new ResxLocalizer(CultureInfo.GetCultureInfo("en-US"));
        foreach (var localizer in new[] { pl, en })
        {
            Assert.Equal([], Enum.GetValues<WatchEventKind>().Where(k => localizer.KindName(k) == k.ToString() && k != WatchEventKind.Test));
            Assert.Equal([], Constants(typeof(EventTags)).Where(t => !localizer.Contains(WatchdogDisplay.TagKey(t))));
            Assert.Equal([], Constants(typeof(AttributionReasons)).Where(r => localizer.ReasonText(r) == r || localizer.RemedyText(r) is null));
            Assert.Equal([], Constants(typeof(ConfigValidationCodes)).Where(c => !localizer.Contains(WatchdogDisplay.ValidationKey(c))));
            Assert.Equal([], KeytabStatusResults.All.Where(r => localizer.KeytabResultName(r) == r));
            Assert.Equal([], HostCredentialKeys.All.Where(k => localizer.CredentialKeyName(k) == k));
            Assert.Equal([], Enum.GetValues<AttributionSourceKind>().Where(s => !localizer.Contains(WatchdogDisplay.AttributionSourceKey(s))));
            Assert.Equal([], Enum.GetValues<WatchdogSubTab>().Where(t => !localizer.Contains(WatchdogDisplay.SubTabKey(t))));

            // Health checks of a DC / GPO subject resolve to their own text and remedy.
            string[] wecChecks =
            [
                HealthCheckCodes.WecSilent, HealthCheckCodes.WecNotForwarding, HealthCheckCodes.WecLag, HealthCheckCodes.WecIdentityMismatch,
                HealthCheckCodes.WecMalformedLines, HealthCheckCodes.WecBacklog, HealthCheckCodes.WecCollectorFileLost,
            ];
            string[] gpoChecks = [HealthCheckCodes.GpoTooLarge, HealthCheckCodes.GpoParseError, HealthCheckCodes.GpoSysvolLag];
            var codes = wecChecks.Select(c => HealthCheckCodes.Wec("dc01.contoso.test", c))
                .Concat(gpoChecks.Select(c => HealthCheckCodes.Gpo(Guid.Parse("31b2f340-016d-11d2-945f-00c04fb984f9"), c)));
            Assert.All(codes, code =>
            {
                Assert.Equal(code.Split('.')[0] + "." + code.Split('.')[^1], WatchdogDisplay.HealthPrefix(code));
                Assert.NotNull(localizer.HealthRemedy(code));
            });
        }

        // Every error of the client has its own message in the connection banner.
        var connection = new WatchdogConnectionService(new FakeWatchdogClient(), new TestDoubles.InMemorySettingsStore(), pl);
        var generic = connection.Describe(new WatchdogClientException(WatchdogClientErrorKind.ServerError, "x"));
        Assert.Equal([], Enum.GetValues<WatchdogClientErrorKind>()
            .Where(k => k != WatchdogClientErrorKind.ServerError)
            .Where(k => connection.Describe(new WatchdogClientException(k, "x")) == generic));
    }

    private static string Field(WatchdogStatusSectionViewModel section, string label) =>
        Assert.Single(section.Fields, f => f.Label == label).Value;

    private static IEnumerable<string> Constants(Type type) =>
        type.GetFields(BindingFlags.Public | BindingFlags.Static).Where(f => f.IsLiteral && f.FieldType == typeof(string)).Select(f => (string)f.GetRawConstantValue()!);
}
