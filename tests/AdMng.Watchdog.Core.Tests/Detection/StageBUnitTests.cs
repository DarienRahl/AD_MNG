using AdMng.Watchdog.Contracts.Api;
using AdMng.Watchdog.Contracts.Configuration;
using AdMng.Watchdog.Contracts.Events;
using AdMng.Watchdog.Core.Detection;
using AdMng.Watchdog.Core.Formatting;
using AdMng.Watchdog.Core.Model;

namespace AdMng.Watchdog.Core.Tests.Detection;

/// <summary>The pure parts of stage B 9.1–9.4: the watchdog-self rotation exclusion, the own-account identity, the
/// severities and the audit-settings change row.</summary>
public class StageBUnitTests
{
    private static readonly DateTimeOffset Changed = new(2026, 10, 14, 3, 15, 0, TimeSpan.Zero);

    // ------------------------------------------------------------------ WatchdogSelfRotation (9.3)

    [Theory]
    [InlineData(7, 0, KeytabStatusResults.Ok, true)]
    [InlineData(7, 0, KeytabStatusResults.ReplicationPending, true)]
    [InlineData(7, 10, KeytabStatusResults.Ok, true)]               // the edge of ±10 minutes
    [InlineData(7, -10, KeytabStatusResults.Ok, true)]
    [InlineData(7, 11, KeytabStatusResults.Ok, false)]
    [InlineData(7, -11, KeytabStatusResults.Ok, false)]
    [InlineData(6, 0, KeytabStatusResults.Ok, false)]               // the tool recorded the previous key version
    [InlineData(8, 0, KeytabStatusResults.Ok, false)]
    [InlineData(7, 0, KeytabStatusResults.OtpActive, false)]
    [InlineData(7, 0, KeytabStatusResults.ChangeFailed, false)]
    [InlineData(7, 0, KeytabStatusResults.KeytabWriteFailed, false)]
    [InlineData(7, 0, KeytabStatusResults.VerifyFailed, false)]
    [InlineData(7, 0, "OK", false)]                                  // results are exact
    public void IsPlanned(int recordedKvno, int minutesAway, string result, bool expected)
    {
        var record = new KeytabRotationRecord(recordedKvno, Changed, result);

        Assert.Equal(expected, WatchdogSelfRotation.IsPlanned(record, 7, Changed.AddMinutes(minutesAway)));
    }

    [Fact]
    public void IsPlanned_NeedsEveryValue()
    {
        Assert.False(WatchdogSelfRotation.IsPlanned(null, 7, Changed));
        Assert.False(WatchdogSelfRotation.IsPlanned(new KeytabRotationRecord(null, Changed, KeytabStatusResults.Ok), 7, Changed));
        Assert.False(WatchdogSelfRotation.IsPlanned(new KeytabRotationRecord(7, null, KeytabStatusResults.Ok), 7, Changed));
        Assert.False(WatchdogSelfRotation.IsPlanned(new KeytabRotationRecord(7, Changed, null), 7, Changed));
        Assert.False(WatchdogSelfRotation.IsPlanned(new KeytabRotationRecord(7, Changed, KeytabStatusResults.Ok), null, Changed));
        Assert.False(WatchdogSelfRotation.IsPlanned(new KeytabRotationRecord(7, Changed, KeytabStatusResults.Ok), 7, null));
    }

    // ------------------------------------------------------------------ WatchdogAccountIdentity (9.3)

    [Theory]
    [InlineData("ADMNG-WD$@CONTOSO.COM", "ADMNG-WD$")]
    [InlineData(" admng-wd$@contoso.test ", "admng-wd$")]
    [InlineData("ADMNG-WD$", "ADMNG-WD$")]
    [InlineData("@CONTOSO.COM", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void SamAccountNameOf(string? principal, string? expected) =>
        Assert.Equal(expected, WatchdogAccountIdentity.SamAccountNameOf(principal));

    [Fact]
    public void Matches_ByGuidSidOrName()
    {
        var guid = Guid.Parse("0b6f2c1e-7d3a-4e59-9c8b-1a2b3c4d5e6f");
        var identity = new WatchdogAccountIdentity { SamAccountName = "ADMNG-WD$", ObjectGuid = guid, Sid = "S-1-5-21-1-2-3-1601" };

        Assert.True(identity.Matches(guid, null, "INNE-KONTO$"));                   // renamed
        Assert.True(identity.Matches(Guid.NewGuid(), "s-1-5-21-1-2-3-1601", null));
        Assert.True(identity.Matches(Guid.NewGuid(), "S-1-5-21-1-2-3-1777", "admng-wd$"));   // re-created under the name
        Assert.False(identity.Matches(Guid.NewGuid(), "S-1-5-21-1-2-3-1777", "KRK-WS01$"));
        Assert.False(identity.Matches(null, null, null));
        Assert.True(new WatchdogAccountIdentity { SamAccountName = "ADMNG-WD$" }.Matches(guid, null, "ADMNG-WD$"));
    }

    // ------------------------------------------------------------------ severities (9.1, 9.2)

    [Fact]
    public void BadSuccessorTag_RaisesAnyKindToCritical()
    {
        var e = new WatchEvent
        {
            EventId = Guid.NewGuid(),
            Kind = WatchEventKind.ServiceAccountCreated,
            Object = new ObjectRef { Dn = "CN=dmsa-łódź,OU=Łódź,DC=contoso,DC=test", Kind = WatchObjectKind.ServiceAccount },
            DetectedAt = Changed,
            Source = DetectionSource.IncrementalPoll,
        };

        Assert.Equal(Severity.Medium, new SeverityResolver().Resolve(e));
        Assert.Equal(Severity.Critical, new SeverityResolver().Resolve(e with { Tags = [EventTags.BadSuccessor] }));
        var dmsaClass = new AttributeChange { Attribute = "objectClass", NewValues = ["msDS-DelegatedManagedServiceAccount"] };
        Assert.Equal(Severity.Critical, new SeverityResolver().BaseSeverity(e with { Changes = [dmsaClass] }));
        Assert.Equal(Severity.Critical, EventKindCatalog.DefaultSeverity(WatchEventKind.ManagedAccountLinkChanged, WatchObjectKind.User));
    }

    // ------------------------------------------------------------------ the audit-settings row (9.4)

    [Theory]
    [InlineData("pl", "lista inspekcji (SACL) albo inne ustawienie zabezpieczeń zmienione – treści konto usługi nie odczyta")]
    [InlineData("en", "audit list (SACL) or another security setting changed – the service account cannot read its content")]
    public void AuditSettingsRow_SaysWhatChanged(string language, string expected)
    {
        var context = NotificationContext.Create(new WatchdogConfiguration { General = new GeneralSettings { Language = language } }, DetailLevel.Full);
        var e = new WatchEvent
        {
            EventId = Guid.NewGuid(),
            Kind = WatchEventKind.SecurityDescriptorChanged,
            Object = new ObjectRef { Dn = "DC=contoso,DC=test", Kind = WatchObjectKind.Domain },
            Changes = [new AttributeChange { Attribute = "nTSecurityDescriptor", ValueKind = AttributeValueKind.Text }],
            Tags = [EventTags.AuditSettings],
            DetectedAt = Changed,
            Source = DetectionSource.IncrementalPoll,
        };

        var row = Assert.Single(AlertContentBuilder.ChangeRows(e, context));
        Assert.Null(row.Before);
        Assert.Equal(expected, row.After);

        var dacl = Assert.Single(AlertContentBuilder.ChangeRows(e with { Tags = [] }, context));
        Assert.NotEqual(expected, dacl.After);
    }
}
