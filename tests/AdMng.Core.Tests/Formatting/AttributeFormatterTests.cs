using System.Globalization;
using System.Text;
using AdMng.Core.ActiveDirectory;
using AdMng.Core.Formatting;
using AdMng.Core.Model;
using AdMng.Core.Schema;
using AdMng.Testing;

namespace AdMng.Core.Tests.Formatting;

public class AttributeFormatterTests
{
    private static readonly CultureInfo Polish = CultureInfo.GetCultureInfo("pl-PL");
    private static readonly CultureInfo English = CultureInfo.GetCultureInfo("en-US");
    private readonly AttributeFormatter _formatter = new(FakeSchema.Create());

    private static string Local(DateTimeOffset utc, CultureInfo culture) =>
        TimeZoneInfo.ConvertTime(utc, TimeZoneInfo.Local).ToString("G", culture);

    private object Parse(string attribute, string input, CultureInfo? culture = null)
    {
        Assert.True(_formatter.TryParse(attribute, input, culture ?? Polish, out var value, out var error), error);
        Assert.Null(error);
        return value!;
    }

    private string ParseError(string attribute, string input)
    {
        Assert.False(_formatter.TryParse(attribute, input, Polish, out var value, out var error));
        Assert.Null(value);
        return error!;
    }

    [Theory]
    [InlineData(Attr.AccountExpires, "0")]
    [InlineData(Attr.AccountExpires, "9223372036854775807")]
    [InlineData(Attr.LastLogonTimestamp, "0")]
    [InlineData(Attr.PwdLastSet, "0")]
    public void FileTime_SentinelsAreNever(string attribute, string value)
    {
        Assert.Equal(AttributeFormatter.NeverText, _formatter.FormatValue(attribute, value, Polish));
    }

    [Fact]
    public void FileTime_IsShownInLocalTimeOfTheCulture()
    {
        var when = new DateTimeOffset(2026, 10, 13, 12, 30, 15, TimeSpan.Zero);
        var raw = AdConvert.ToFileTime(when).ToString(CultureInfo.InvariantCulture);

        Assert.Equal(Local(when, Polish), _formatter.FormatValue(Attr.PwdLastSet, raw, Polish));
        Assert.Equal(Local(when, English), _formatter.FormatValue("lastLogonTimestamp", raw, English));
        Assert.Equal("not-a-number", _formatter.FormatValue(Attr.PwdLastSet, "not-a-number", Polish));
    }

    [Fact]
    public void GeneralizedTime_IsShownInLocalTime()
    {
        var expected = Local(new DateTimeOffset(2026, 10, 13, 14, 15, 0, TimeSpan.Zero), Polish);

        Assert.Equal(expected, _formatter.FormatValue(Attr.WhenCreated, "20261013141500.0Z", Polish));
        Assert.Equal(expected, new AttributeFormatter(SchemaCatalog.Empty).FormatValue(Attr.WhenChanged, "20261013141500.0Z", Polish));
        Assert.Equal(AttributeFormatter.NeverText, _formatter.FormatValue(Attr.WhenCreated, "16010101000000.0Z", Polish));
        Assert.Equal("garbage", _formatter.FormatValue(Attr.WhenCreated, "garbage", Polish));
    }

    [Fact]
    public void Identifiers_AreDecoded()
    {
        var guid = new Guid("01020304-0506-0708-090a-0b0c0d0e0f10");
        var sid = "S-1-5-21-3623811015-3361044348-30300820-1104";

        Assert.Equal(guid.ToString("D"), _formatter.FormatValue(Attr.ObjectGuid, guid.ToByteArray(), Polish));
        Assert.Equal(sid, _formatter.FormatValue(Attr.ObjectSid, AdConvert.SidToBytes(sid), Polish));
        Assert.Equal(sid, _formatter.FormatValue(Attr.SidHistory, AdConvert.SidToBytes(sid), Polish));
        Assert.Equal("01 02", _formatter.FormatValue(Attr.ObjectSid, new byte[] { 1, 2 }, Polish));
    }

    [Theory]
    [InlineData("512", "512 (NORMAL_ACCOUNT)")]
    [InlineData("514", "514 (ACCOUNTDISABLE, NORMAL_ACCOUNT)")]
    [InlineData("66048", "66048 (NORMAL_ACCOUNT, DONT_EXPIRE_PASSWORD)")]
    [InlineData("532480", "532480 (SERVER_TRUST_ACCOUNT, TRUSTED_FOR_DELEGATION)")]
    [InlineData("4", "4 (0x4)")]
    [InlineData("0", "0")]
    [InlineData("x", "x")]
    public void UserAccountControl_ShowsFlagNames(string value, string expected)
    {
        Assert.Equal(expected, _formatter.FormatValue(Attr.UserAccountControl, value, Polish));
    }

    [Theory]
    [InlineData("-2147483646", "-2147483646 (ACCOUNT_GROUP, SECURITY_ENABLED)")]
    [InlineData("-2147483644", "-2147483644 (RESOURCE_GROUP, SECURITY_ENABLED)")]
    [InlineData("8", "8 (UNIVERSAL_GROUP)")]
    [InlineData("-2147483643", "-2147483643 (BUILTIN_LOCAL_GROUP, RESOURCE_GROUP, SECURITY_ENABLED)")]
    public void GroupType_ShowsFlagNames(string value, string expected)
    {
        Assert.Equal(expected, _formatter.FormatValue(Attr.GroupType, value, Polish));
    }

    [Fact]
    public void FlagNameHelpers_ArePublic()
    {
        Assert.Equal(["ACCOUNTDISABLE", "NORMAL_ACCOUNT"], AttributeFormatter.GetUserAccountControlFlagNames(514));
        Assert.Equal(["ACCOUNT_GROUP", "SECURITY_ENABLED"], AttributeFormatter.GetGroupTypeFlagNames(-2147483646));
    }

    [Fact]
    public void Booleans_AreUpperCase()
    {
        Assert.Equal("TRUE", _formatter.FormatValue(Attr.IsCriticalSystemObject, "true", Polish));
        Assert.Equal("FALSE", _formatter.FormatValue("msExchHideFromAddressLists", "False", Polish));
    }

    [Fact]
    public void Binary_IsHexAndTruncated()
    {
        var small = new byte[] { 0x01, 0xAB, 0xFF };
        var large = Enumerable.Range(0, 300).Select(i => (byte)i).ToArray();

        var formatted = _formatter.FormatValue(Attr.ThumbnailPhoto, large, Polish);

        Assert.Equal("01 AB FF", _formatter.FormatValue(Attr.ThumbnailPhoto, small, Polish));
        Assert.EndsWith(" ...", formatted, StringComparison.Ordinal);
        Assert.Equal((AttributeFormatter.MaxDisplayedBytes * 3) - 1 + 4, formatted.Length);
        Assert.StartsWith("00 01 02", formatted, StringComparison.Ordinal);
    }

    [Fact]
    public void TextAttributesReturnedAsBytesAreDecoded()
    {
        Assert.Equal("Księgowość", _formatter.FormatValue(Attr.Department, Encoding.UTF8.GetBytes("Księgowość"), Polish));
        Assert.Equal("FF FE", _formatter.FormatValue(Attr.Department, new byte[] { 0xFF, 0xFE }, Polish));
    }

    [Fact]
    public void Format_JoinsValues()
    {
        var values = new AttributeValues(Attr.OtherTelephone, ["111", "222"]);

        Assert.Equal("111; 222", _formatter.Format(Attr.OtherTelephone, values, Polish));
        Assert.Equal(string.Empty, _formatter.Format(Attr.OtherTelephone, null, Polish));
        Assert.Equal(string.Empty, _formatter.Format(Attr.OtherTelephone, new AttributeValues(Attr.OtherTelephone, []), Polish));
        Assert.Equal("CN=Jan Kowalski,OU=IT,DC=contoso,DC=test", _formatter.FormatValue(Attr.Manager, "CN=Jan Kowalski,OU=IT,DC=contoso,DC=test", Polish));
        Assert.Equal("512 (NORMAL_ACCOUNT)", _formatter.Format("userAccountControl;range=0-*", new AttributeValues("userAccountControl", ["512"]), Polish));
    }

    [Theory]
    [InlineData(Attr.UserAccountControl, "512", "512")]
    [InlineData(Attr.UserAccountControl, " 0x200 ", "512")]
    [InlineData(Attr.GroupType, "0x80000002", "-2147483646")]
    [InlineData(Attr.GroupType, "-2147483646", "-2147483646")]
    [InlineData(Attr.CountryCode, "616", "616")]
    [InlineData(Attr.PwdLastSet, "-1", "-1")]
    [InlineData(Attr.UsnChanged, "9007199254740993", "9007199254740993")]
    [InlineData(Attr.IsCriticalSystemObject, "true", "TRUE")]
    [InlineData(Attr.IsCriticalSystemObject, "0", "FALSE")]
    [InlineData(Attr.Manager, " CN=Jan Kowalski,OU=IT,DC=contoso,DC=test ", "CN=Jan Kowalski,OU=IT,DC=contoso,DC=test")]
    [InlineData(Attr.Department, "Księgowość", "Księgowość")]
    [InlineData(Attr.Department, " z odstępami ", " z odstępami ")]
    [InlineData(Attr.AccountExpires, "never", "9223372036854775807")]
    [InlineData(Attr.AccountExpires, "0", "0")]
    [InlineData(Attr.LockoutTime, "NEVER", "0")]
    [InlineData("unknownAttribute", "anything", "anything")]
    public void TryParse_ConvertsToWireValues(string attribute, string input, string expected)
    {
        Assert.Equal(expected, Parse(attribute, input));
    }

    [Theory]
    [InlineData(Attr.UserAccountControl, "abc", "is not a valid integer")]
    [InlineData(Attr.UserAccountControl, "4294967296", "out of range")]
    [InlineData(Attr.CountryCode, "70000", "out of the allowed range (0 to 65535)")]
    [InlineData(Attr.IsCriticalSystemObject, "maybe", "use TRUE or FALSE")]
    [InlineData(Attr.Manager, "not a dn", "is not a valid distinguished name")]
    [InlineData(Attr.WhenCreated, "wczoraj", "is not a valid date")]
    [InlineData(Attr.AccountExpires, "kiedyś", "is not a date, a FILETIME number or 'never'")]
    [InlineData(Attr.AccountExpires, "1500-01-01", "is before 1601-01-01")]
    [InlineData(Attr.AccountExpires, "0001-01-01", "")]
    [InlineData(Attr.Department, "", "A value is required")]
    [InlineData(Attr.ThumbnailPhoto, "ABC", "is not valid hexadecimal data")]
    [InlineData(Attr.ObjectGuid, "01 02", "The value is too short (2 bytes, minimum 16)")]
    [InlineData(Attr.ObjectSid, "S-1-x", "is not a valid SID")]
    public void TryParse_RejectsInvalidInput(string attribute, string input, string expectedFragment)
    {
        Assert.Contains(expectedFragment, ParseError(attribute, input), StringComparison.Ordinal);
    }

    [Fact]
    public void TryParse_ChecksStringLengthAgainstSchema()
    {
        Assert.Equal("The value is too long (65 characters, maximum 64).", ParseError(Attr.Department, new string('x', 65)));
        Assert.Equal("The value is too long (4 characters, maximum 3).", ParseError(Attr.HomeDrive, "H:\\x"));
        Assert.Equal(new string('x', 64), Parse(Attr.Department, new string('x', 64)));
    }

    [Fact]
    public void TryParse_DatesUseCultureAndLocalTime()
    {
        var local = new DateTime(2026, 10, 13, 14, 15, 0, DateTimeKind.Unspecified);
        var expected = new DateTimeOffset(local, TimeZoneInfo.Local.GetUtcOffset(local));

        Assert.Equal(AdConvert.ToGeneralizedTime(expected), Parse(Attr.WhenCreated, "13.10.2026 14:15", Polish));
        Assert.Equal(AdConvert.ToGeneralizedTime(expected), Parse(Attr.WhenCreated, "10/13/2026 2:15 PM", English));
        Assert.Equal(AdConvert.ToGeneralizedTime(expected), Parse(Attr.WhenCreated, "2026-10-13 14:15", Polish));
        Assert.Equal("20261013141500.0Z", Parse(Attr.WhenCreated, "20261013141500.0Z"));
        Assert.Equal(AdConvert.ToFileTime(expected).ToString(CultureInfo.InvariantCulture), Parse(Attr.AccountExpires, "13.10.2026 14:15", Polish));
    }

    [Fact]
    public void TryParse_BinaryAndIdentifiers()
    {
        var guid = Guid.NewGuid();
        const string sid = "S-1-5-21-3623811015-3361044348-30300820-1104";

        Assert.Equal(new byte[] { 0x01, 0x02, 0x0A }, Parse(Attr.ThumbnailPhoto, "01 02 0a"));
        Assert.Equal(new byte[] { 0x01, 0xA2 }, Parse(Attr.ThumbnailPhoto, "0x01 0xA2"));
        Assert.Equal(new byte[] { 0x01, 0xA2, 0xFF }, Parse(Attr.ThumbnailPhoto, "01-A2:ff"));
        Assert.Equal(guid.ToByteArray(), Parse(Attr.ObjectGuid, guid.ToString("B")));
        Assert.Equal(AdConvert.SidToBytes(sid), Parse(Attr.ObjectSid, sid));
        Assert.Equal(AdConvert.SidToBytes(sid), Parse(Attr.ObjectSid, Convert.ToHexString(AdConvert.SidToBytes(sid))));
    }

    [Fact]
    public void FormatAndParse_RoundTrip()
    {
        var when = new DateTimeOffset(2026, 3, 4, 5, 6, 7, TimeSpan.Zero);
        var fileTime = AdConvert.ToFileTime(when).ToString(CultureInfo.InvariantCulture);
        var generalized = AdConvert.ToGeneralizedTime(when);

        foreach (var culture in new[] { Polish, English, CultureInfo.InvariantCulture })
        {
            Assert.Equal(fileTime, Parse(Attr.PwdLastSet, _formatter.FormatValue(Attr.PwdLastSet, fileTime, culture), culture));
            Assert.Equal(generalized, Parse(Attr.WhenCreated, _formatter.FormatValue(Attr.WhenCreated, generalized, culture), culture));
        }

        var bytes = new byte[] { 0x00, 0x7F, 0x80, 0xFF };
        Assert.Equal(bytes, Parse(Attr.ThumbnailPhoto, _formatter.FormatValue(Attr.ThumbnailPhoto, bytes, Polish)));
        var never = _formatter.FormatValue(Attr.AccountExpires, "9223372036854775807", Polish);
        Assert.Equal("9223372036854775807", Parse(Attr.AccountExpires, never));
    }
}
