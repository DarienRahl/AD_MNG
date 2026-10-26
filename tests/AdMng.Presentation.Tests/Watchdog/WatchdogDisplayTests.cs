using System.Globalization;
using System.Text.RegularExpressions;
using AdMng.Presentation.Localization;
using AdMng.Presentation.Watchdog;
using AdMng.Watchdog.Contracts.Api;

namespace AdMng.Presentation.Tests.Watchdog;

/// <summary>Pure display helpers of the tab: health codes, durations, DN and SID checks, edit values.</summary>
public sealed partial class WatchdogDisplayTests
{
    private static readonly CultureInfo Polish = CultureInfo.GetCultureInfo("pl-PL");

    [Theory]
    [InlineData("directory.connection", "directory.connection")]
    [InlineData("eventlog.dc02.contoso.test", "eventlog")]
    [InlineData("eventlog.dc02.contoso.test.foreignRecords", "eventlog.foreignRecords")]
    [InlineData("permission.deletedObjects", "permission.deletedObjects")]
    [InlineData("permission.somethingNew", "permission")]
    [InlineData("channel.teams-it", "channel")]
    [InlineData("storage.free", "storage.free")]
    [InlineData("future.check", null)]
    [InlineData("wec.dc01.contoso.com.silent", "wec.silent")]
    [InlineData("wec.dc01.contoso.com.collectorFileLost", "wec.collectorFileLost")]
    [InlineData("wec.dc01.contoso.com.somethingNew", "wec")]
    [InlineData("wec.service", "wec.service")]
    [InlineData("gpo.31b2f340-016d-11d2-945f-00c04fb984f9.parseError", "gpo.parseError")]
    [InlineData("serviceAccount.keytabStale", "serviceAccount.keytabStale")]
    [InlineData("serviceAccount.somethingNew", "serviceAccount")]
    [InlineData("host.clockSkew", "host.clockSkew")]
    [InlineData("sysvol.accessDenied", "sysvol.accessDenied")]
    [InlineData("storage.backupSkipped", "storage.backupSkipped")]
    public void HealthPrefix_IsTheLongestKnownPrefix(string code, string? expected) =>
        Assert.Equal(expected, WatchdogDisplay.HealthPrefix(code));

    [Fact]
    public void HealthText_PutsTheSubjectOfTheCodeIntoTheText()
    {
        var localizer = new ResxLocalizer(Polish);

        Assert.Equal("Dziennik zabezpieczeń dc02.contoso.test", localizer.HealthText("eventlog.dc02.contoso.test"));
        Assert.Equal("Obce rekordy w dzienniku dc02.contoso.test", localizer.HealthText("eventlog.dc02.contoso.test.foreignRecords"));
        Assert.Equal("Odczyt kontenera Deleted Objects", localizer.HealthText("permission.deletedObjects"));
        Assert.StartsWith("Kolektor przekazuje zdarzenia", localizer.HealthRemedy("eventlog.dc02.contoso.test.foreignRecords"), StringComparison.Ordinal);
    }

    [Fact]
    public void HealthText_NamesTheSubjectInTheMiddleOfAStageBCode()
    {
        var localizer = new ResxLocalizer(Polish);

        Assert.Equal("Kontroler dc01.contoso.com milczy", localizer.HealthText(HealthCheckCodes.Wec("dc01.contoso.com", HealthCheckCodes.WecSilent)));
        Assert.Equal("Odrzucone rekordy z dc02.contoso.com", localizer.HealthText(HealthCheckCodes.Wec("DC02.contoso.com", HealthCheckCodes.WecIdentityMismatch)));
        Assert.Equal("Opóźnienie SYSVOL dla GPO 31b2f340-016d-11d2-945f-00c04fb984f9",
            localizer.HealthText(HealthCheckCodes.Gpo(Guid.Parse("31b2f340-016d-11d2-945f-00c04fb984f9"), HealthCheckCodes.GpoSysvolLag)));
        Assert.Equal("Odbiornik OpenWEC", localizer.HealthText(HealthCheckCodes.WecService));
        Assert.Equal("Keytab nieaktualny względem AD", localizer.HealthText(HealthCheckCodes.ServiceAccountKeytabStale));
        Assert.Contains("Set-ADAccountPassword -Identity 'ADMNG-WD$' -Reset -NewPassword $otp", localizer.HealthRemedy(HealthCheckCodes.ServiceAccountKeytabStale), StringComparison.Ordinal);
    }

    [Fact]
    public void HealthCodes_OfStageB_AreAllKnownPrefixes()
    {
        var codes = typeof(HealthCheckCodes).GetFields()
            .Where(f => f.IsLiteral && ((string)f.GetRawConstantValue()!).Contains('.', StringComparison.Ordinal))
            .Select(f => ((string)f.GetRawConstantValue()!).TrimEnd('.'))
            .ToList();

        Assert.Contains("wec", codes);
        Assert.Contains("serviceAccount.drift", codes);
        Assert.Equal([], codes.Except(WatchdogDisplay.HealthCodes, StringComparer.Ordinal));
    }

    [Fact]
    public void HealthText_TheConfiguredDnAndCollectorChecks_AreLocalized()
    {
        // Codes reported by the service (ConfiguredDnMonitor, AttributionSourceResolver) – not shown as raw codes.
        var localizer = new ResxLocalizer(Polish);

        Assert.Equal("Nieistniejące DN w konfiguracji", localizer.HealthText("rules.configuredDns"));
        Assert.StartsWith("Jednostka organizacyjna lub grupa", localizer.HealthRemedy("rules.configuredDns"), StringComparison.Ordinal);
        Assert.Equal("Kolektor zdarzeń (WEF)", localizer.HealthText("attribution.collector"));
        Assert.Contains("nie adres IP", localizer.HealthRemedy("attribution.collector"), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("certificate.expiry", "Certyfikat TLS wkrótce wygaśnie")]
    [InlineData("certificate.key", "Klucz prywatny certyfikatu TLS niedostępny")]
    [InlineData("storage.size", "Rozmiar bazy danych")]
    [InlineData("storage.maintenance", "Konserwacja bazy danych")]
    [InlineData("rules.planner", "Planowanie powiadomień")]
    [InlineData("rules.configuredDns", "Nieistniejące DN w konfiguracji")]
    [InlineData("attribution.collector", "Kolektor zdarzeń (WEF)")]
    [InlineData("attribution.dcDirectory", "Wykrywanie kontrolerów domeny")]
    [InlineData("attribution.source", "Źródło atrybucji")]
    [InlineData("permission.search", "Wyszukiwanie zmian w katalogu")]
    public void HealthCodes_OfTheService_HaveTheirOwnTextAndRemedy(string code, string expected)
    {
        var localizer = new ResxLocalizer(Polish);

        Assert.Equal(code, WatchdogDisplay.HealthPrefix(code));
        Assert.Equal(expected, localizer.HealthText(code));
        Assert.False(string.IsNullOrWhiteSpace(localizer.HealthRemedy(code)));
        Assert.NotEqual(localizer.HealthRemedy(code), new ResxLocalizer(CultureInfo.GetCultureInfo("en-US")).HealthRemedy(code));
    }

    /// <summary>
    /// Every health code the service and its engine report (constants and literals next to <c>HealthCheckDto</c>) has a
    /// known prefix, so the Status tab never shows a raw code without a remedy. Codes ending in "." get a subject
    /// appended (e.g. the DC of "eventlog." + host).
    /// </summary>
    [Fact]
    public void HealthPrefix_ResolvesEveryCodeTheServiceReports()
    {
        var codes = ReportedHealthCodes();

        Assert.Contains("certificate.expiry", codes);
        Assert.Contains("storage.size", codes);
        Assert.Contains("eventlog.", codes);
        Assert.Contains("secret.unreadable", codes);
        var localizer = new ResxLocalizer(Polish);
        Assert.All(codes, literal =>
        {
            var code = literal.EndsWith('.') ? literal + "dc02.contoso.test" : literal;
            Assert.True(WatchdogDisplay.HealthPrefix(code) is not null, $"No Watchdog.Health.* text for the health code '{literal}'.");
            Assert.NotEqual(code, localizer.HealthText(code));
            Assert.NotNull(localizer.HealthRemedy(code));
        });
    }

    private static SortedSet<string> ReportedHealthCodes()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "AdMng.slnx")))
        {
            root = root.Parent;
        }

        Assert.NotNull(root);
        var literal = HealthCodeLiteral();
        var constant = HealthCodeConstant();
        var codes = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var project in new[] { "AdMng.Watchdog.Contracts", "AdMng.Watchdog.Core", "AdMng.Watchdog.Service" })
        {
            var directory = Path.Combine(root.FullName, "src", project);
            foreach (var file in Directory.EnumerateFiles(directory, "*.cs", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(directory, file).Replace('\\', '/');
                if (relative.StartsWith("obj/", StringComparison.Ordinal) || relative.StartsWith("bin/", StringComparison.Ordinal))
                {
                    continue;
                }

                var text = File.ReadAllText(file);
                var matches = text.Contains("HealthCheckDto", StringComparison.Ordinal) || text.Contains("IHealthRegistry", StringComparison.Ordinal)
                    ? literal.Matches(text).Concat(constant.Matches(text))
                    : constant.Matches(text);
                foreach (var match in matches)
                {
                    // A health code has a dot ("storage.free", "eventlog." + host); plain names are job names or details.
                    if (match.Groups[1].Value.Contains('.', StringComparison.Ordinal))
                    {
                        codes.Add(match.Groups[1].Value);
                    }
                }
            }
        }

        return codes;
    }

    /// <summary>A code literal next to a <c>HealthCheckDto</c>: <c>Code = "…"</c>, <c>Report("…"</c>, <c>const string X = "…"</c>.</summary>
    [GeneratedRegex(@"(?:\bCode\s*=\s*|Report\(\s*|const string \w+\s*=\s*)""([a-z][A-Za-z0-9]*(?:\.[A-Za-z0-9]+)*\.?)""", RegexOptions.CultureInvariant)]
    private static partial Regex HealthCodeLiteral();

    /// <summary>A health code constant anywhere (<c>HealthCode</c>, <c>CheckCode</c>, <c>KeyHealthCode</c>…).</summary>
    [GeneratedRegex(@"const string \w*(?:HealthCode|CheckCode)\s*=\s*""([a-z][A-Za-z0-9]*(?:\.[A-Za-z0-9]+)*)""", RegexOptions.CultureInvariant)]
    private static partial Regex HealthCodeConstant();

    [Theory]
    [InlineData(0, "0 ms")]
    [InlineData(840, "840 ms")]
    [InlineData(999, "999 ms")]
    [InlineData(1000, "1,0 s")]
    [InlineData(12_345, "12,3 s")]
    public void FormatDuration_MillisecondsBelowOneSecond(int milliseconds, string expected) =>
        Assert.Equal(expected, WatchdogDisplay.FormatDuration(TimeSpan.FromMilliseconds(milliseconds), Polish));

    [Theory]
    [InlineData("OU=Kraków,OU=Firma,DC=contoso,DC=test", true)]
    [InlineData("CN=Kowalski\\, Jan,OU=IT,DC=contoso,DC=test", true)]
    [InlineData("to nie jest DN", false)]
    [InlineData("OU Kraków", false)]
    public void Dn_IsCheckedWithTheDistinguishedNameParser(string text, bool valid) =>
        Assert.Equal(valid, WatchdogDn.IsValid(text));

    [Fact]
    public void DnLines_SkipEmptyLines_AndReportInvalidOnes()
    {
        var text = "OU=Kraków,OU=Firma,DC=contoso,DC=test\r\n\r\n  zły wpis \nOU=Gdańsk,OU=Firma,DC=contoso,DC=test";

        Assert.Equal(["OU=Kraków,OU=Firma,DC=contoso,DC=test", "zły wpis", "OU=Gdańsk,OU=Firma,DC=contoso,DC=test"], WatchdogDn.Lines(text));
        Assert.Equal(["zły wpis"], WatchdogDn.InvalidLines(text));
        Assert.Empty(WatchdogDn.Lines(null));
    }

    [Theory]
    [InlineData("S-1-5-21-1004336348-1177238915-682003330-1105", true)]
    [InlineData("S-1-5-18", true)]
    [InlineData("s-1-5-18", false)]
    [InlineData("CONTOSO\\jan.kowalski", false)]
    [InlineData("S-1-5-21-abc", false)]
    [InlineData("", false)]
    public void Sid_AcceptsSddlSidsOnly(string text, bool valid) =>
        Assert.Equal(valid, WatchdogSid.IsValid(text));

    [Fact]
    public void EditValues_KeepTheStoredOrder_ThenAppendNewOnes()
    {
        Assert.Equal(["helpdesk", "it-admins", "kierownicy"], WatchdogEditValues.KeepOrder(["helpdesk", "usunieta", "it-admins"], ["kierownicy", "it-admins", "helpdesk", "kierownicy"]));
        Assert.Equal(["B"], WatchdogEditValues.KeepOrder(["b"], ["B"], StringComparer.OrdinalIgnoreCase).Select(s => s.ToUpperInvariant()));
        Assert.Empty(WatchdogEditValues.KeepOrder<string>(["a"], []));
    }

    [Fact]
    public void EditValues_KeepTheStoredPrecision_WhileTheTextIsUnchanged()
    {
        var stored = TimeSpan.FromSeconds(90);
        static string Minutes(TimeSpan t) => ((int)t.TotalMinutes).ToString(CultureInfo.InvariantCulture);
        static TimeSpan Parse(string t) => TimeSpan.FromMinutes(int.Parse(t, CultureInfo.InvariantCulture));

        Assert.Equal(stored, WatchdogEditValues.KeepUnchanged(stored, " 1 ", Minutes, Parse));
        Assert.Equal(TimeSpan.FromMinutes(2), WatchdogEditValues.KeepUnchanged(stored, "2", Minutes, Parse));
    }

    [Fact]
    public void StageAndChannelNames_AreLocalized()
    {
        var localizer = new ResxLocalizer(Polish);

        Assert.Equal("Odpowiedź HTTP", localizer.StageName(DeliveryStage.HttpResponse));
        Assert.NotEqual(nameof(DeliveryStage.Authenticate), localizer.StageName(DeliveryStage.Authenticate));
        Assert.Equal("E-mail", localizer.ChannelTypeName(DeliveryChannelType.Email));
        Assert.Equal("Powiadomienie", localizer.DeliveryKindName(DeliveryKind.Alert));
    }
}
