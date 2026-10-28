using AdMng.Core.ActiveDirectory;
using AdMng.Core.Messaging;
using AdMng.Core.Model;
using AdMng.Core.Templates;
using static AdMng.Core.Tests.Messaging.MessageTestData;

namespace AdMng.Core.Tests.Messaging;

/// <summary><c>{$created[:format]}</c> (U5): when the account – a mailbox – was created, a date token like <c>{$lastLogon}</c>.</summary>
public class UserMessageCreatedFieldTests
{
    private readonly UserMessageFields _fields = new();

    private static UserMessageFieldData Data(DirectoryObject account, MessageLanguage language = MessageLanguage.Polish) => new()
    {
        Account = account,
        Status = AccountStatus.Evaluate(account, Now, Warsaw),
        Language = language,
        TimeZone = Warsaw,
        Domain = "contoso.test",
    };

    private static DirectoryObject Mailbox(DateTimeOffset? created) => created is { } value
        ? Lukasz((Attr.WhenCreated, AdConvert.ToGeneralizedTime(value)), (Attr.DisplayName, "Księgowość – faktury"))
        : Lukasz((Attr.DisplayName, "Księgowość – faktury"));

    [Fact]
    public void Created_InPolishAndEnglish_DefaultAndExplicitFormats()
    {
        var mailbox = Mailbox(new DateTimeOffset(2025, 3, 15, 8, 30, 0, TimeSpan.Zero));

        Assert.Equal("15 marca 2025", _fields.Render("{$created}", Data(mailbox)));
        Assert.Equal("Skrzynka Księgowość – faktury utworzona 15 marca 2025, 09:30.",
            _fields.Render("Skrzynka {displayName} utworzona {$created:d MMMM yyyy, HH:mm}.", Data(mailbox)));
        Assert.Equal("15 March 2025", _fields.Render("{$created:d MMMM yyyy}", Data(mailbox, MessageLanguage.English)));
        Assert.Equal("sobota, 15.03.2025", _fields.Render("{$created:dddd, dd.MM.yyyy}", Data(mailbox)));
        Assert.Equal("15 marca 2025", UserMessageFields.Evaluate(UserMessageFields.Created, null, Data(mailbox)));
    }

    [Fact]
    public void Created_IsInEuropeWarsaw_AcrossMidnightAndDaylightSaving()
    {
        // 22:30 UTC on 24 October 2026 is 00:30 on 25 October in Warsaw (summer time, UTC+2).
        var mailbox = Mailbox(new DateTimeOffset(2026, 10, 24, 22, 30, 0, TimeSpan.Zero));

        Assert.Equal("25 października 2026, 00:30", _fields.Render("{$created:d MMMM yyyy, HH:mm}", Data(mailbox)));
    }

    [Fact]
    public void Created_WithoutWhenCreated_IsEmpty_AndReportedAsAnEmptyField()
    {
        var empty = new List<string>();

        Assert.Equal("Utworzona: .", _fields.Render("Utworzona: {$created:d MMMM yyyy}.", Data(Mailbox(null)), emptyField: empty.Add));
        Assert.Equal(["{$created:d MMMM yyyy}"], empty);
    }

    [Fact]
    public void Created_IsADateToken_WithAnExample()
    {
        var token = Assert.Single(UserMessageFields.Tokens, t => t.Name == UserMessageFields.Created);

        Assert.Equal("created", UserMessageFields.Created);
        Assert.True(token.IsDateFormat);
        Assert.Equal(TemplateTokenArgument.Optional, token.Argument);
        Assert.Contains("{$created:d MMMM yyyy}", UserMessageFields.Examples);
        Assert.True(_fields.Engine.Parse("{$created}").IsValid);
        Assert.True(_fields.Engine.Parse("{$created:d MMMM yyyy}|{$created:yyyy-MM-dd}").IsValid);
        Assert.Contains(Attr.WhenCreated, AccountStatus.Attributes);
    }
}
