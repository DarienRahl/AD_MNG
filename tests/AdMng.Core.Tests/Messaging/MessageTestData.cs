using System.Globalization;
using AdMng.Core.ActiveDirectory;
using AdMng.Core.Messaging;
using AdMng.Core.Model;

namespace AdMng.Core.Tests.Messaging;

/// <summary>
/// Accounts of the message tests: a fixed clock on Monday 2026-10-26 10:00 UTC (11:00 in Warsaw, the day after the end of
/// daylight saving time) and Polish people with diacritics.
/// </summary>
internal static class MessageTestData
{
    public static readonly TimeZoneInfo Warsaw = TimeZoneInfo.FindSystemTimeZoneById("Europe/Warsaw");
    public static readonly DateTimeOffset Now = new(2026, 10, 26, 10, 0, 0, TimeSpan.Zero);

    public const string LukaszDn = "CN=Łukasz Wiśniewski,OU=IT,OU=Pracownicy,DC=contoso,DC=test";
    public const string JanDn = "CN=Jan Kowalski,OU=IT,OU=Pracownicy,DC=contoso,DC=test";
    public const string AgnieszkaDn = "CN=Agnieszka Żółć,OU=Sprzedaż,OU=Pracownicy,DC=contoso,DC=test";

    public static UserMessageSender Sender(params string[] allowedDomains) => new()
    {
        From = new MessageAddress("noreply@contoso.test", "Dział IT"),
        ReplyTo = new MessageAddress("helpdesk@contoso.test", "Pomoc techniczna"),
        Cc = [new MessageAddress("helpdesk@contoso.test")],
        AllowedLinkDomains = allowedDomains,
        TimeZone = Warsaw,
    };

    /// <summary>Łukasz: password expires 2026-11-05 07:00 UTC (10 days), manager Jan, Polish.</summary>
    public static DirectoryObject Lukasz(params (string Attribute, object Value)[] extra) => User(
        LukaszDn,
        [
            (Attr.GivenName, "Łukasz"), (Attr.Sn, "Wiśniewski"), (Attr.DisplayName, "Łukasz Wiśniewski"),
            (Attr.SamAccountName, "lukasz.wisniewski"), (Attr.Mail, "lukasz.wisniewski@contoso.test"), (Attr.Manager, JanDn),
            (Attr.PwdLastSet, FileTime(new DateTimeOffset(2026, 8, 7, 7, 0, 0, TimeSpan.Zero))),
            (Attr.PasswordExpiryComputed, FileTime(new DateTimeOffset(2026, 11, 5, 7, 0, 0, TimeSpan.Zero))),
            (Attr.LastLogonTimestamp, FileTime(new DateTimeOffset(2026, 10, 20, 6, 15, 0, TimeSpan.Zero))),
            .. extra,
        ]);

    /// <summary>Jan, the manager.</summary>
    public static DirectoryObject Jan(params (string Attribute, object Value)[] extra) => User(
        JanDn,
        [
            (Attr.GivenName, "Jan"), (Attr.Sn, "Kowalski"), (Attr.DisplayName, "Jan Kowalski"), (Attr.SamAccountName, "jan.kowalski"),
            (Attr.Mail, "jan.kowalski@contoso.test"), (Attr.TelephoneNumber, "+48 22 123 45 67"), .. extra,
        ]);

    /// <summary>Agnieszka: English, no mail but a primary SMTP proxy address, account ends with 2026-10-30.</summary>
    public static DirectoryObject Agnieszka(params (string Attribute, object Value)[] extra) => User(
        AgnieszkaDn,
        [
            (Attr.GivenName, "Agnieszka"), (Attr.Sn, "Żółć"), (Attr.DisplayName, "Agnieszka Żółć"), (Attr.SamAccountName, "agnieszka.zolc"),
            (Attr.ProxyAddresses, new[] { "smtp:a.zolc@contoso.test", "SMTP:agnieszka.zolc@contoso.test", "X500:/o=Contoso" }),
            ("preferredLanguage", "en-GB"),
            (Attr.AccountExpires, FileTime(new DateTimeOffset(2026, 10, 30, 23, 0, 0, TimeSpan.Zero))),
            (Attr.PwdLastSet, FileTime(new DateTimeOffset(2026, 10, 1, 7, 0, 0, TimeSpan.Zero))),
            (Attr.PasswordExpiryComputed, FileTime(new DateTimeOffset(2026, 12, 30, 7, 0, 0, TimeSpan.Zero))),
            .. extra,
        ]);

    public static DirectoryObject User(string dn, IEnumerable<(string Attribute, object Value)> values)
    {
        var attributes = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
        {
            [Attr.ObjectClass] = new[] { "top", "person", "organizationalPerson", ObjectClasses.User },
            [Attr.UserAccountControl] = ((int)UserAccountControl.NormalAccount).ToString(CultureInfo.InvariantCulture),
            [Attr.UserAccountControlComputed] = "0",
        };
        foreach (var (attribute, value) in values)
        {
            attributes[attribute] = value;
        }

        return new DirectoryObject(dn, attributes.Select(a => new AttributeValues(a.Key, a.Value is string[] many ? many : [a.Value])));
    }

    public static string FileTime(DateTimeOffset value) => AdConvert.ToFileTime(value).ToString(CultureInfo.InvariantCulture);

    public static string Uac(UserAccountControl flags) => ((int)flags).ToString(CultureInfo.InvariantCulture);
}
