using AdMng.Core.ActiveDirectory;
using AdMng.Core.Messaging;
using AdMng.Core.Model;
using static AdMng.Core.Tests.Messaging.MessageTestData;

namespace AdMng.Core.Tests.Messaging;

/// <summary>The computed fields of messages: dates in the message language and Europe/Warsaw, days, manager, inputs.</summary>
public class UserMessageFieldsTests
{
    private readonly UserMessageFields _fields = new();

    private static UserMessageFieldData Data(DirectoryObject account, MessageLanguage language = MessageLanguage.Polish, DirectoryObject? manager = null) => new()
    {
        Account = account,
        Status = AccountStatus.Evaluate(account, Now, Warsaw),
        Manager = manager,
        Language = language,
        TimeZone = Warsaw,
        Inputs = new Dictionary<string, string> { ["termin"] = "30 listopada 2026" },
        Domain = "contoso.test",
    };

    [Fact]
    public void PasswordExpiry_InPolish_UsesGenitiveMonthNames_AndPolishDiacriticsStay()
    {
        var text = _fields.Render(
            "Dzień dobry {givenName} {sn}, hasło do konta {sAMAccountName} wygaśnie {$passwordExpires:d MMMM yyyy} (za {$passwordExpiresInDays} dni).",
            Data(Lukasz()));

        Assert.Equal("Dzień dobry Łukasz Wiśniewski, hasło do konta lukasz.wisniewski wygaśnie 5 listopada 2026 (za 10 dni).", text);
    }

    [Fact]
    public void PasswordExpiry_InEnglish_AndTheDefaultFormat()
    {
        var data = Data(Lukasz(), MessageLanguage.English);

        Assert.Equal("5 November 2026", _fields.Render("{$passwordExpires}", data));
        Assert.Equal("Thursday, 05.11.2026", _fields.Render("{$passwordExpires:dddd, dd.MM.yyyy}", data));
        Assert.Equal("czwartek, 05.11.2026", _fields.Render("{$passwordExpires:dddd, dd.MM.yyyy}", Data(Lukasz())));
    }

    [Fact]
    public void Dates_AreInEuropeWarsaw_NotUtc_AcrossTheDaylightSavingChange()
    {
        // 2026-10-24 22:30 UTC is already 25 October 00:30 in Warsaw (summer time, UTC+2); the lock at 07:30 UTC on
        // 26 October is 08:30 (winter time, UTC+1).
        var account = Lukasz(
            (Attr.PasswordExpiryComputed, FileTime(new DateTimeOffset(2026, 10, 24, 22, 30, 0, TimeSpan.Zero))),
            (Attr.LockoutTime, FileTime(new DateTimeOffset(2026, 10, 26, 7, 30, 0, TimeSpan.Zero))),
            (Attr.UserAccountControlComputed, Uac(UserAccountControl.Lockout | UserAccountControl.PasswordExpired)));
        var data = Data(account);

        Assert.Equal(PasswordState.Expired, data.Status.Password);
        Assert.Equal("25 października 2026, 00:30", _fields.Render("{$passwordExpires:d MMMM yyyy, HH:mm}", data));
        Assert.Equal(string.Empty, _fields.Render("{$passwordExpiresInDays}", data));
        Assert.Equal("26 października 2026, 08:30", _fields.Render("{$lockedSince:d MMMM yyyy, HH:mm}", data));
        Assert.Equal("26.10.2026", _fields.Render("{$today:dd.MM.yyyy}", data));
    }

    [Fact]
    public void AccountExpiry_IsTheLastValidDay_InTheMessageLanguage()
    {
        var data = Data(Agnieszka(), MessageLanguage.English);

        Assert.Equal("30 October 2026 (4)", _fields.Render("{$accountExpires} ({$accountExpiresInDays})", data));
        Assert.Equal("30 października 2026", _fields.Render("{$accountExpires:d MMMM yyyy}", Data(Agnieszka())));
    }

    [Fact]
    public void NeverExpiringOrMustChangePasswords_LeaveTheExpiryFieldsEmpty()
    {
        var never = Data(Lukasz((Attr.UserAccountControl, Uac(UserAccountControl.NormalAccount | UserAccountControl.DontExpirePassword))));
        var mustChange = Data(Lukasz((Attr.PwdLastSet, "0")));
        var empty = new List<string>();

        Assert.Equal("|", _fields.Render("{$passwordExpires}|{$passwordExpiresInDays}", never, emptyField: empty.Add));
        Assert.Equal(["{$passwordExpires}", "{$passwordExpiresInDays}"], empty);
        Assert.Equal(PasswordState.MustChange, mustChange.Status.Password);
        Assert.Equal(string.Empty, _fields.Render("{$passwordExpires}", mustChange));
    }

    [Fact]
    public void Manager_Inputs_LastLogon_AndBatchEditorFunctions()
    {
        var data = Data(Lukasz(), manager: Jan());

        Assert.Equal("Jan Kowalski, +48 22 123 45 67", _fields.Render("{$manager:displayName}, {$manager:telephoneNumber}", data));
        Assert.Equal("30 listopada 2026 / 30 LISTOPADA 2026", _fields.Render("{$input:termin} / {$input:TERMIN|upper}", data));
        Assert.Equal("20 października 2026", _fields.Render("{$lastLogon}", data));
        Assert.Equal("lwisniewski@contoso.test", _fields.Render("{givenName|first:1|nodiacritics|lower}{sn|nodiacritics|lower}@{$domain}", data));
        Assert.Equal(string.Empty, _fields.Render("{$manager:displayName}", Data(Lukasz())));
    }

    [Fact]
    public void TimeZone_ResolvesIanaIds_AndFallsBack()
    {
        Assert.Equal(Warsaw.BaseUtcOffset, UserMessageFields.ResolveTimeZone("Europe/Warsaw").BaseUtcOffset);
        Assert.Equal(Warsaw.BaseUtcOffset, UserMessageFields.ResolveTimeZone("Nie/Istnieje").BaseUtcOffset);
        Assert.Equal(TimeSpan.FromHours(-5), UserMessageFields.ResolveTimeZone("America/New_York").BaseUtcOffset);
    }
}
