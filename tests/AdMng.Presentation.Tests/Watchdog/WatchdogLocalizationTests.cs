using System.Collections;
using System.Globalization;
using System.Reflection;
using System.Resources;
using AdMng.Presentation.Localization;
using AdMng.Presentation.Watchdog;
using AdMng.Watchdog.Contracts.Api;
using AdMng.Watchdog.Contracts.Configuration;
using AdMng.Watchdog.Contracts.Events;

namespace AdMng.Presentation.Tests.Watchdog;

/// <summary>Every "Watchdog.*" key family the tab builds at run time exists in English and Polish.</summary>
public sealed class WatchdogLocalizationTests
{
    private static readonly ResourceManager Resources = new(ResxLocalizer.ResourceBaseName, typeof(ResxLocalizer).Assembly);

    /// <summary>Keys whose Polish text is the same word as in English (product names, protocol terms).</summary>
    private static readonly HashSet<string> SameInBothLanguages = new(StringComparer.Ordinal)
    {
        "Watchdog.Title", "Watchdog.ObjectKind.Watchdog", "Watchdog.ChannelType.Email", "Watchdog.ChannelType.Teams",
        "Watchdog.ChannelType.Webhook", "Watchdog.EntraCloud.UsGovernment", "Watchdog.HealthState.Ok", "Watchdog.Role.Admin",
        "Watchdog.Stage.Tls", "Watchdog.Stage.Token", "Watchdog.Tag.krbtgt", "Watchdog.Ui.SmtpPort",
    };

    public static TheoryData<string, string[]> Families() => new()
    {
        { "Kind", [.. Enum.GetValues<WatchEventKind>().Select(WatchdogDisplay.KindKey)] },
        { "Category", [.. Enum.GetValues<EventCategory>().Select(WatchdogDisplay.CategoryKey)] },
        { "Severity", [.. Enum.GetValues<Severity>().Select(WatchdogDisplay.SeverityKey)] },
        { "ObjectKind", [.. Enum.GetValues<WatchObjectKind>().Select(WatchdogDisplay.ObjectKindKey)] },
        { "Reason", [.. WatchdogDisplay.ReasonCodes.SelectMany(c => new[] { WatchdogDisplay.ReasonKey(c), WatchdogDisplay.RemedyKey(c) })] },
        { "Health", [.. WatchdogDisplay.HealthCodes.SelectMany(c => new[] { WatchdogDisplay.HealthKey(c), WatchdogDisplay.HealthRemedyKey(c) })] },
        { "Hint", [.. WatchdogDisplay.Hints.Select(WatchdogDisplay.HintKey)] },
        { "Validation", [.. WatchdogDisplay.ValidationCodes.Select(WatchdogDisplay.ValidationKey)] },
        { "Stage", [.. Enum.GetValues<DeliveryStage>().Select(WatchdogDisplay.StageKey)] },
        { "Outbox", [.. Enum.GetValues<OutboxStatus>().Select(WatchdogDisplay.OutboxKey)] },
        { "State", [.. Enum.GetValues<WatchdogConnectionState>().Select(WatchdogDisplay.StateKey)] },
        { "DetailLevel", [.. Enum.GetValues<DetailLevel>().Select(WatchdogDisplay.DetailLevelKey)] },
        { "DeliveryHours", [.. Enum.GetValues<DeliveryHours>().Select(WatchdogDisplay.DeliveryHoursKey)] },
        { "SmtpSecurity", [.. Enum.GetValues<SmtpSecurity>().Select(WatchdogDisplay.SmtpSecurityKey)] },
        { "SmtpAuthentication", [.. Enum.GetValues<SmtpAuthentication>().Select(WatchdogDisplay.SmtpAuthenticationKey)] },
        { "TeamsAuthMode", [.. Enum.GetValues<TeamsAuthMode>().Select(WatchdogDisplay.TeamsAuthModeKey)] },
        { "GroupFilter", [.. Enum.GetValues<GroupFilter>().Select(WatchdogDisplay.GroupFilterKey)] },
        { "EntraCloud", [.. Enum.GetValues<EntraCloud>().Select(WatchdogDisplay.EntraCloudKey)] },
        { "Tag", [.. WatchdogDisplay.Tags.Select(WatchdogDisplay.TagKey)] },
        { "AttributionStatus", [.. Enum.GetValues<AttributionStatus>().Select(WatchdogDisplay.AttributionStatusKey)] },
        { "Suggestion", [.. WatchdogDisplay.SuggestionReasons.Select(WatchdogDisplay.SuggestionKey)] },
        { "SourceOrigin", [.. WatchdogDisplay.SourceMethods.Select(WatchdogDisplay.SourceOriginKey)] },
        { "Confidence", [.. Enum.GetValues<AttributionConfidence>().Select(WatchdogDisplay.ConfidenceKey)] },
        { "HealthState", [.. Enum.GetValues<HealthState>().Select(WatchdogDisplay.HealthStateKey)] },
        { "ChannelType", [.. Enum.GetValues<DeliveryChannelType>().Select(WatchdogDisplay.ChannelTypeKey)] },
        { "DeliveryKind", [.. Enum.GetValues<DeliveryKind>().Select(WatchdogDisplay.DeliveryKindKey)] },
        { "CertificateValidation", [.. Enum.GetValues<CertificateValidationMode>().Select(WatchdogDisplay.CertificateValidationKey)] },
        { "Tab", [.. Enum.GetValues<WatchdogSubTab>().Select(WatchdogDisplay.SubTabKey)] },
        { "Role", [.. Enum.GetValues<WatchdogRole>().Select(WatchdogDisplay.RoleKey)] },
        { "ExpiryKind", [.. Enum.GetValues<ExpiryKind>().Select(WatchdogDisplay.ExpiryKindKey)] },
    };

    [Theory]
    [MemberData(nameof(Families))]
    public void KeyFamily_ExistsInBothLanguages(string family, string[] keys)
    {
        var en = Load(CultureInfo.InvariantCulture);
        var pl = Load(CultureInfo.GetCultureInfo("pl"));

        Assert.NotEmpty(keys);
        Assert.Equal([], keys.Where(k => !en.ContainsKey(k)).Select(k => $"{family}: en {k}"));
        Assert.Equal([], keys.Where(k => !pl.ContainsKey(k)).Select(k => $"{family}: pl {k}"));
        Assert.Equal([], keys.Where(k => en.TryGetValue(k, out var v) && string.IsNullOrWhiteSpace(v)).Select(k => $"{family}: empty {k}"));
    }

    [Fact]
    public void DisplayLists_CoverEveryCodeOfTheContracts()
    {
        Assert.Equal([], Constants(typeof(EventTags)).Except(WatchdogDisplay.Tags, StringComparer.Ordinal));
        Assert.Equal([], Constants(typeof(AttributionReasons)).Except(WatchdogDisplay.ReasonCodes, StringComparer.Ordinal));
        Assert.Equal([], Constants(typeof(DeliveryHints)).Except(WatchdogDisplay.Hints, StringComparer.Ordinal));
        Assert.Equal([], EventKindCatalog.All.Select(i => i.Kind).Except(Enum.GetValues<WatchEventKind>()));
    }

    [Fact]
    public void Polish_IsATranslation_NotTheEnglishText()
    {
        var en = Load(CultureInfo.InvariantCulture);
        var pl = Load(CultureInfo.GetCultureInfo("pl"));

        var untranslated = en.Keys
            .Where(k => k.StartsWith("Watchdog.", StringComparison.Ordinal) && !SameInBothLanguages.Contains(k))
            .Where(k => string.Equals(en[k], pl[k], StringComparison.Ordinal))
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.True(en.Keys.Count(k => k.StartsWith("Watchdog.", StringComparison.Ordinal)) > 600);
        Assert.Equal([], untranslated);
    }

    [Theory]
    [InlineData("Watchdog.Kind.AccountLockedOut", "Zablokowano konto")]
    [InlineData("Watchdog.Health.eventlog", "Dziennik zabezpieczeń {0}")]
    [InlineData("Watchdog.Secret.Set", "ustawione")]
    [InlineData("Watchdog.DeliveryKind.Alert", "Powiadomienie")]
    [InlineData("Watchdog.Digest.Queued", "Raport dzienny został przekazany do wysłania.")]
    public void Polish_ReadsNaturally(string key, string expected)
    {
        var localizer = new ResxLocalizer(CultureInfo.GetCultureInfo("pl-PL"));
        Assert.Equal(expected, localizer[key]);
    }

    [Fact]
    public void Texts_FallBackToTheCode_ForCodesOfANewerService()
    {
        var localizer = new ResxLocalizer(CultureInfo.GetCultureInfo("pl-PL"));

        Assert.Equal("brand.newHint", localizer.HintText("brand.newHint"));
        Assert.Equal("newReason", localizer.SuggestionText("newReason"));
        Assert.Equal("future.check", localizer.HealthText("future.check"));
        Assert.Null(localizer.HealthRemedy("future.check"));
        Assert.Equal("nowyTag", localizer.TagName("nowyTag"));
        Assert.Equal("ldap", localizer.SourceOriginText("ldap"));
        Assert.Equal(string.Empty, localizer.SourceOriginText(null));
        Assert.Equal("Zablokowano konto", localizer.KindName(WatchEventKind.AccountLockedOut));
        Assert.Equal("998", localizer.KindName((WatchEventKind)998));
    }

    private static IEnumerable<string> Constants(Type type) =>
        type.GetFields(BindingFlags.Public | BindingFlags.Static).Where(f => f.IsLiteral && f.FieldType == typeof(string)).Select(f => (string)f.GetRawConstantValue()!);

    private static Dictionary<string, string> Load(CultureInfo culture)
    {
        var set = Resources.GetResourceSet(culture, createIfNotExists: true, tryParents: false)
            ?? throw new InvalidOperationException($"No resources for {culture.Name}.");
        return set.Cast<DictionaryEntry>().ToDictionary(e => (string)e.Key, e => (string?)e.Value ?? string.Empty, StringComparer.Ordinal);
    }
}
