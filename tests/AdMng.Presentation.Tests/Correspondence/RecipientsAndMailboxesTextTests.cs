using AdMng.Core.Correspondence.Import;
using AdMng.Core.Correspondence.Mailboxes;
using AdMng.Presentation.Correspondence;
using AdMng.Presentation.Localization;
using static AdMng.Presentation.Tests.Correspondence.PageTests;

namespace AdMng.Presentation.Tests.Correspondence;

/// <summary>
/// The dynamic texts of the recipients and mailbox owners pages (design note 6.5): every family is part of
/// <see cref="CorrespondenceText.AllDynamicKeys"/> and has a text in both languages; the Polish plural forms of the
/// status lines; texts and records that carry addresses or names never show them in <c>ToString()</c> (rule 1: logs).
/// </summary>
public sealed class RecipientsAndMailboxesTextTests
{
    public static TheoryData<string> PageKeys()
    {
        var data = new TheoryData<string>();
        foreach (var key in Enum.GetValues<RecipientStatusKind>().Where(k => k != RecipientStatusKind.None).Select(CorrespondenceText.StatusKey)
            .Concat(Enum.GetValues<ContactSourceFormat>().Select(CorrespondenceText.FormatKey))
            .Concat(Enum.GetValues<OwnerMatchStatus>().Select(CorrespondenceText.MailboxStatusKey))
            .Concat([CorrespondenceText.MailboxStatusKey(MailboxOwnerRowState.Manual), CorrespondenceText.MailboxStatusKey(MailboxOwnerRowState.Excluded)])
            .Concat(Enum.GetValues<MailboxSelection>().Select(CorrespondenceText.SelectionKey))
            .Concat(Enum.GetValues<OwnerNameSource>().Select(CorrespondenceText.NameSourceKey))
            .Concat(Enum.GetValues<MailboxRowFilter>().Select(CorrespondenceText.FilterKey))
            .Concat(Enum.GetValues<MailboxType>().Select(CorrespondenceText.MailboxTypeKey))
            .Concat(Enum.GetValues<OwnerMatchLevel>().Select(CorrespondenceText.LevelKey)))
        {
            data.Add(key);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(PageKeys))]
    public void EveryKeyOfThePages_IsADynamicKey_WithATextInBothLanguages(string key)
    {
        Assert.Contains(key, CorrespondenceText.AllDynamicKeys());
        var localizer = new ResxLocalizer(Polish);
        localizer.SetCulture(English);
        var english = localizer[key];
        localizer.SetCulture(Polish);
        var polish = localizer[key];

        Assert.NotEqual(key, english);
        Assert.NotEqual(key, polish);
        Assert.False(string.IsNullOrWhiteSpace(english));
        Assert.False(string.IsNullOrWhiteSpace(polish));
    }

    [Fact]
    public void TheFamilies_AreDistinct_AndNoneIsLeftOut()
    {
        var keys = CorrespondenceText.AllDynamicKeys();

        Assert.Equal(keys.Count, keys.Distinct(StringComparer.Ordinal).Count());
        Assert.DoesNotContain(CorrespondenceText.StatusKey(RecipientStatusKind.None), keys);
        Assert.DoesNotContain(CorrespondenceText.MailboxStatusKey(MailboxOwnerRowState.Automatic), keys);
        Assert.Equal(PageKeys().Count(), keys.Count(k => k.StartsWith("Correspondence.Status.", StringComparison.Ordinal)
            || k.StartsWith("Correspondence.Format.", StringComparison.Ordinal)
            || k.StartsWith("Correspondence.Mailboxes.", StringComparison.Ordinal)));
    }

    [Theory]
    [InlineData(0, "0 nowych", "0 new")]
    [InlineData(1, "1 nowy", "1 new")]
    [InlineData(2, "2 nowe", "2 new")]
    [InlineData(4, "4 nowe", "4 new")]
    [InlineData(5, "5 nowych", "5 new")]
    [InlineData(12, "12 nowych", "12 new")]
    [InlineData(14, "14 nowych", "14 new")]
    [InlineData(21, "21 nowych", "21 new")]
    [InlineData(22, "22 nowe", "22 new")]
    [InlineData(112, "112 nowych", "112 new")]
    [InlineData(1_024, "1 024 nowe", "1,024 new")]
    public void Plural_FollowsThePolishForms(int count, string polish, string english)
    {
        var localizer = new ResxLocalizer(Polish);
        localizer.SetCulture(Polish);
        string Text() => CorrespondenceText.Plural(localizer, count,
            L.CorrespondenceRecipients.NewOne, L.CorrespondenceRecipients.NewFew, L.CorrespondenceRecipients.NewMany).Replace(' ', ' ');

        Assert.Equal(polish, Text());
        localizer.SetCulture(English);
        Assert.Equal(english, Text());
    }

    [Fact]
    public void StatusText_IsTheBaseTextOfAKind()
    {
        var localizer = new ResxLocalizer(Polish);
        localizer.SetCulture(Polish);

        Assert.Equal(string.Empty, CorrespondenceText.StatusText(localizer, RecipientStatusKind.None));
        Assert.Equal("wysłano", CorrespondenceText.StatusText(localizer, RecipientStatusKind.Sent));
        Assert.Equal("wersja robocza", CorrespondenceText.StatusText(localizer, RecipientStatusKind.Draft));
        Assert.Equal("test wysłany", CorrespondenceText.StatusText(localizer, RecipientStatusKind.Test));
        Assert.Equal("błąd", CorrespondenceText.StatusText(localizer, RecipientStatusKind.Failed));
        Assert.Equal("pominięto", CorrespondenceText.StatusText(localizer, RecipientStatusKind.Skipped));
        Assert.Equal("Excel", localizer[CorrespondenceText.FormatKey(ContactSourceFormat.Excel)]);
        Assert.Equal("skrzynka współdzielona (Exchange Online)", localizer[CorrespondenceText.MailboxTypeKey(MailboxType.RemoteShared)]);
        Assert.Throws<ArgumentNullException>(() => CorrespondenceText.StatusText(null!, RecipientStatusKind.Sent));
    }

    [Fact]
    public void RecordsWithAddressesOrNames_NeverShowThemInToString()
    {
        var person = new PersonCandidate("CN=Jan Kowalski,OU=IT,DC=contoso,DC=test", "Jan", "Kowalski", "Jan Kowalski",
            "jan.kowalski@contoso.test", IsDisabled: false, MailboxType.None, "IT", "Administrator", "jan.kowalski");
        var option = new CandidateOption(person, "Jan Kowalski – IT, jan.kowalski@contoso.test", IsDelegate: false, OwnerMatchLevel.Exact);
        var entry = new MailboxEntry("CN=Sekretariat IT,OU=Skrzynki,DC=contoso,DC=test", "Sekretariat IT", "sekretariat.it@contoso.test",
            MailboxType.Shared, [], IsDisabled: true);
        var match = new OwnerMatch(OwnerMatchStatus.Matched, new OwnerCandidateMatch(person, OwnerMatchLevel.Exact, false, 65), []);
        var row = new MailboxOwnerRowViewModel(entry, "contoso.test/Skrzynki", "skrzynka współdzielona", new OwnerName("Jan", "Kowalski", "Jan Kowalski"), match, [option]);
        var directoryRow = new DirectoryRow("jan.kowalski@contoso.test", "Jan Kowalski", person.DistinguishedName, new Dictionary<string, string> { ["Imię"] = "Jan" });
        var field = new DirectoryFieldViewModel(new("Imię", "{givenName}"));

        foreach (var text in new[] { option.ToString(), row.ToString(), directoryRow.ToString(), field.ToString(),
            new MailboxSearchScope("DC=contoso,DC=test", [entry.DistinguishedName]).ToString(),
            new MailboxRead(new Core.Model.DirectoryObject(entry.DistinguishedName, []), entry, null).ToString() })
        {
            Assert.DoesNotContain("@", text, StringComparison.Ordinal);
            Assert.DoesNotContain("Kowalski", text, StringComparison.Ordinal);
            Assert.DoesNotContain("Sekretariat", text, StringComparison.Ordinal);
        }
    }
}
