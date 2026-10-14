using AdMng.Watchdog.Contracts.Events;
using AdMng.Watchdog.Core.Formatting;

namespace AdMng.Watchdog.Core.Tests.Formatting;

public sealed class AttributeDisplayTests
{
    private readonly AttributeDisplay _pl = FormattingSamples.Context("pl").Attributes;

    private readonly AttributeDisplay _en = FormattingSamples.Context("en").Attributes;

    [Fact]
    public void Names_ComeFromTheResources_WithTheLdapNameAsFallback()
    {
        Assert.Equal("Dział", _pl.Name("department"));
        Assert.Equal("Department", _en.Name("department"));
        Assert.Equal("extensionAttribute7", _pl.Name("extensionAttribute7"));
        Assert.Equal("Konto wyłączone", _pl.FlagName("ACCOUNTDISABLE"));
        Assert.Equal("0x4000", _pl.FlagName("0x4000"));
    }

    [Fact]
    public void FileTimes_AreLocalTimes_AndZeroOrMaxAreNever()
    {
        var change = new AttributeChange { Attribute = "pwdLastSet", ValueKind = AttributeValueKind.FileTime };
        var fileTime = new DateTimeOffset(2026, 10, 14, 12, 32, 5, TimeSpan.Zero).ToFileTime().ToString(System.Globalization.CultureInfo.InvariantCulture);

        Assert.Equal("14.10.2026 14:32:05 (UTC+02:00)", _pl.Value(change, fileTime));
        Assert.Equal("2026-10-14 14:32:05 (UTC+02:00)", _en.Value(change, fileTime));
        Assert.Equal("nigdy", _pl.Value(change, "0"));
        Assert.Equal("never", _en.Value(change, "9223372036854775807"));
    }

    [Fact]
    public void AccountExpires_ShowsTheLastValidDay()
    {
        // ADUC "End of 21.10.2026" stores the local midnight that starts 22.10.2026.
        var midnight = new DateTimeOffset(2026, 10, 22, 0, 0, 0, TimeSpan.FromHours(2)).ToFileTime().ToString(System.Globalization.CultureInfo.InvariantCulture);
        var change = new AttributeChange { Attribute = "accountExpires", ValueKind = AttributeValueKind.FileTime };

        Assert.Equal("z końcem dnia 21.10.2026", _pl.Value(change, midnight));
        Assert.Equal("end of 2026-10-21", _en.Value(change, midnight));
    }

    [Fact]
    public void OtherKinds_AreRenderedReadably()
    {
        Assert.Equal("tak", _pl.Value(new AttributeChange { Attribute = "msDS-PasswordComplexityEnabled", ValueKind = AttributeValueKind.Boolean }, "TRUE"));
        Assert.Equal("Łukasz Wiśniewski",
            _pl.Value(new AttributeChange { Attribute = "manager", ValueKind = AttributeValueKind.Dn }, "CN=Łukasz Wiśniewski,OU=Zarząd,DC=contoso,DC=com"));
        Assert.Equal("wartość binarna", _pl.Value(new AttributeChange { Attribute = "userCertificate", ValueKind = AttributeValueKind.Hash }, "sha256:00"));
        Assert.Equal("14.10.2026 14:32:05 (UTC+02:00)",
            _pl.Value(new AttributeChange { Attribute = "whenCreated", ValueKind = AttributeValueKind.GeneralizedTime }, "2026-10-14T12:32:05Z"));
    }

    [Fact]
    public void FlagChanges_AreAppendedToTheNewValue()
    {
        var change = new AttributeChange
        {
            Attribute = "userAccountControl", ValueKind = AttributeValueKind.Flags, OldValues = ["66048"], NewValues = ["514"],
            FlagsAdded = ["ACCOUNTDISABLE"], FlagsRemoved = ["DONT_EXPIRE_PASSWORD"],
        };

        Assert.Equal(["514 (+Konto wyłączone, −Hasło nigdy nie wygasa)"], _pl.After(change));
        Assert.Equal(["66048"], _pl.Before(change));
    }

    [Fact]
    public void LogonHours_AreSummarizedPerDayInLocalStandardTime()
    {
        // Monday–Friday 07:00–17:00 local (UTC+1 standard time) = 06:00–16:00 UTC.
        var bits = new bool[168];
        for (var day = 1; day <= 5; day++)
        {
            for (var hour = 6; hour < 16; hour++)
            {
                bits[(day * 24) + hour] = true;
            }
        }

        var bytes = new byte[21];
        for (var i = 0; i < 168; i++)
        {
            if (bits[i])
            {
                bytes[i / 8] |= (byte)(1 << (i % 8));
            }
        }

        var change = new AttributeChange { Attribute = "logonHours" };

        Assert.Equal("pon–pt 7–17", _pl.Value(change, Convert.ToHexStringLower(bytes)));
        Assert.Equal("Mon–Fri 7–17", _en.Value(change, Convert.ToHexStringLower(bytes)));
        Assert.Equal("zawsze", _pl.Value(change, new string('f', 42)));
        Assert.Equal("nigdy (brak godzin logowania)", _pl.Value(change, new string('0', 42)));
        Assert.Equal("nieprawidłowe", _pl.Value(change, "nieprawidłowe"));
    }
}
