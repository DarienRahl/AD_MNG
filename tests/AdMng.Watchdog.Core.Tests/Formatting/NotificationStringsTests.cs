using System.Collections;
using System.Globalization;
using System.Reflection;
using System.Resources;
using System.Text.RegularExpressions;
using AdMng.Watchdog.Contracts.Api;
using AdMng.Watchdog.Contracts.Events;
using AdMng.Watchdog.Core.Formatting;
using AdMng.Watchdog.Core.Notifications.Digest;

namespace AdMng.Watchdog.Core.Tests.Formatting;

public sealed partial class NotificationStringsTests
{
    private static readonly string[] BuiltInRules =
    [
        "tier0-privilege", "tier0-security", "security-review", "tier0-domain", "tier0-eventlog", "bursts", "privileged-accounts",
        "new-accounts", "new-computers", "computers-unusual", "deletions", "account-status", "lockouts", "unlocks", "passwords",
        "account-flags", "account-names", "logon-settings", "account-attributes", "group-changes", "watchlist-groups", "ou-gpo",
        "expiry", "health",

        // stage B (docs/dev/watchdog-linux-design.md 8.6, 9.1–9.3)
        "tier0-badsuccessor", "tier0-machine-quota", "watchdog-self", "tier0-gpo",
    ];

    /// <summary>Stage B texts outside the generated families (7.8 collector remedy, 7.8 ADWS source, 8.4 unreadable file,
    /// 9.4 SACL change row, 12.1 notify-failure message).</summary>
    private static readonly string[] StageBKeys =
    [
        "Reason.DC_UNREACHABLE.Collector", "Reason.DC_UNREACHABLE.Collector.Remedy", "Source.Adws", "Value.Unreadable",
        "Value.AuditSettingsChanged", "Subject.ServiceStopped", "Email.ServiceStopped",
    ];

    private static readonly Dictionary<string, string> English = Load(CultureInfo.InvariantCulture);

    private static readonly Dictionary<string, string> Polish = Load(CultureInfo.GetCultureInfo("pl"));

    public static IEnumerable<string> GeneratedKeys()
    {
        foreach (var kind in Enum.GetValues<WatchEventKind>())
        {
            yield return $"Kind.{kind}.Title";
            yield return $"Kind.{kind}.TitlePlural";
            yield return $"Kind.{kind}.Sentence";
        }

        foreach (var severity in Enum.GetValues<Severity>())
        {
            yield return "Severity." + severity;
            yield return "SeverityName." + severity;
        }

        foreach (var confidence in Enum.GetValues<AttributionConfidence>())
        {
            yield return "Confidence." + confidence;
        }

        foreach (var attribute in AttributeDisplay.KnownAttributes)
        {
            yield return "Attr." + attribute;
        }

        foreach (var flag in AttributeDisplay.KnownFlags)
        {
            yield return "Flag." + flag;
        }

        foreach (var reason in typeof(AttributionReasons).GetFields(BindingFlags.Public | BindingFlags.Static).Where(f => f.IsLiteral))
        {
            yield return "Reason." + reason.GetRawConstantValue();
            yield return "Reason." + reason.GetRawConstantValue() + ".Remedy";
        }

        foreach (var day in Enum.GetValues<DayOfWeek>())
        {
            yield return $"Day.{day}.Short";
        }

        foreach (var rule in BuiltInRules)
        {
            yield return "Rule." + rule;
        }

        foreach (var kind in Enum.GetValues<DeliveryKind>())
        {
            yield return "DeliveryKind." + kind;
        }

        foreach (var status in Enum.GetValues<OutboxStatus>())
        {
            yield return "OutboxStatus." + status;
        }

        foreach (var plural in new[] { "Subject.DigestEvents", "Subject.DigestAttention", "Card.BurstTitle" })
        {
            yield return plural + ".One";
            yield return plural + ".Few";
            yield return plural + ".Many";
        }

        foreach (var key in DigestKeys())
        {
            yield return key;
        }

        foreach (var key in StageBKeys)
        {
            yield return key;
        }
    }

    /// <summary>Keys the digest formatters build from enum values and counter names.</summary>
    public static IEnumerable<string> DigestKeys()
    {
        foreach (var section in Enum.GetValues<DigestSectionKind>())
        {
            yield return "Digest.Section." + section;
        }

        foreach (var state in Enum.GetValues<HealthState>())
        {
            yield return "Digest.Health.State." + state;
        }

        foreach (var name in new DigestCounts().ToDictionary().Keys)
        {
            yield return "Digest.Count." + char.ToUpperInvariant(name[0]) + name[1..];
        }

        var facts = DigestSectionBuilder.Passwords([], 1).Facts
            .Concat(DigestSectionBuilder.Inactive(null, 1).Facts)
            .Concat(DigestSectionBuilder.Hygiene(null).Facts);
        foreach (var fact in facts)
        {
            yield return "Digest.Fact." + fact.Key;
        }

        foreach (var kind in Enum.GetValues<ExpiryKind>())
        {
            yield return "Digest.Expiry." + kind;
        }
    }

    [Fact]
    public void EveryGeneratedKey_ExistsInBothCultures_AndIsNotEmpty()
    {
        var missing = GeneratedKeys()
            .SelectMany(k => new[] { (Key: k, Culture: "en", Value: English.GetValueOrDefault(k)), (Key: k, Culture: "pl", Value: Polish.GetValueOrDefault(k)) })
            .Where(e => string.IsNullOrWhiteSpace(e.Value))
            .Select(e => e.Culture + ":" + e.Key)
            .ToList();

        Assert.Empty(missing);
    }

    [Fact]
    public void StageBTexts_FollowTheDesign()
    {
        Assert.Equal("Sprawdź, czy DC przekazuje zdarzenia (GPO subskrypcji, WinRM, zapora 5985) – Stan → Kolektor WEF.",
            Polish["Reason." + AttributionReasons.CollectorVariant(AttributionReasons.DcUnreachable) + ".Remedy"]);
        Assert.Equal("Zmiana plików SYSVOL bez zmiany wersji GPO – wykonawca nieznany; sprawdź, kto ma zapis do SYSVOL.",
            Polish["Reason." + AttributionReasons.SysvolNotAudited + ".Remedy"]);
        Assert.Equal("przez ADWS – adres z logowania tej samej osoby", Polish["Source.Adws"]);
        Assert.Equal("nie udało się odczytać", Polish["Value.Unreadable"]);
        Assert.Equal("lista inspekcji (SACL) albo inne ustawienie zabezpieczeń zmienione – treści konto usługi nie odczyta",
            Polish["Value.AuditSettingsChanged"]);
        Assert.StartsWith("AdMng Watchdog zatrzymany – sprawdź systemctl status admng-watchdog", Polish["Email.ServiceStopped"], StringComparison.Ordinal);
    }

    [Fact]
    public void BothCultures_HaveTheSameKeys()
    {
        Assert.Empty(English.Keys.Except(Polish.Keys));
        Assert.Empty(Polish.Keys.Except(English.Keys));
    }

    [Fact]
    public void BothCultures_UseTheSamePlaceholders()
    {
        var mismatched = English.Keys
            .Where(k => !Placeholders(English[k]).SetEquals(Placeholders(Polish[k])))
            .ToList();

        Assert.Empty(mismatched);
    }

    [Fact]
    public void PolishTexts_ArePolish_AndEnglishTextsAreNot()
    {
        Assert.Equal("Konto {0} zostało wyłączone.", Polish["Kind.AccountDisabled.Sentence"]);
        Assert.Equal("Dział", Polish["Attr.department"]);
        Assert.Equal("KRYTYCZNE", NotificationText.Polish.SeverityWord(Severity.Critical));
        Assert.Equal("ŚREDNI", NotificationText.Polish.SeverityWord(Severity.Medium));
        Assert.Equal("CRITICAL", NotificationText.English.SeverityWord(Severity.Critical));
        Assert.Equal("Krytyczny", NotificationText.Polish.SeverityName(Severity.Critical));     // "Priorytet: Wysoki → Krytyczny"
        Assert.Equal("Critical", NotificationText.English.SeverityName(Severity.Critical));
        Assert.Equal("Department", NotificationText.English.Get("Attr.department"));
        Assert.DoesNotMatch("[ąćęłńóśźż]", string.Concat(English.Values));
    }

    [Theory]
    [InlineData(1, "One")]
    [InlineData(2, "Few")]
    [InlineData(4, "Few")]
    [InlineData(5, "Many")]
    [InlineData(12, "Many")]
    [InlineData(14, "Many")]
    [InlineData(22, "Few")]
    [InlineData(25, "Many")]
    [InlineData(71, "Many")]
    [InlineData(102, "Few")]
    [InlineData(0, "Many")]
    public void PluralForms_FollowPolishRules(long count, string form) => Assert.Equal(form, NotificationText.PluralForm(count));

    [Fact]
    public void Text_UsesItsOwnCulture_NotTheThreadCulture()
    {
        var previous = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("en-US");
            Assert.Equal("Dział", NotificationText.For("pl").Get("Attr.department"));
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("pl-PL");
            Assert.Equal("Department", NotificationText.For("en").Get("Attr.department"));
        }
        finally
        {
            CultureInfo.CurrentUICulture = previous;
        }

        Assert.Same(NotificationText.Polish, NotificationText.For("xx"));
        Assert.Equal("[Brak.Klucza]", NotificationText.Polish.Get("Brak.Klucza"));
        Assert.Equal("Dział", NotificationText.Polish.Get("attr.DEPARTMENT"));
    }

    private static HashSet<string> Placeholders(string value) => [.. PlaceholderPattern().Matches(value).Select(m => m.Value)];

    private static Dictionary<string, string> Load(CultureInfo culture)
    {
        var manager = new ResourceManager(NotificationText.ResourceBaseName, typeof(NotificationText).Assembly);
        var set = manager.GetResourceSet(culture, createIfNotExists: true, tryParents: false)
            ?? throw new InvalidOperationException("No resources for " + culture.Name);
        return set.Cast<DictionaryEntry>().ToDictionary(e => (string)e.Key, e => (string)e.Value!, StringComparer.Ordinal);
    }

    [GeneratedRegex(@"\{\d+\}")]
    private static partial Regex PlaceholderPattern();
}
