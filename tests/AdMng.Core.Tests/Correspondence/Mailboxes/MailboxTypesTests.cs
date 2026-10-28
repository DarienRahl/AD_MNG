using AdMng.Core.ActiveDirectory;
using AdMng.Core.Correspondence.Mailboxes;
using AdMng.Core.Ldap;
using AdMng.Testing;
using static AdMng.Core.Tests.Correspondence.Mailboxes.MailboxTestData;

namespace AdMng.Core.Tests.Correspondence.Mailboxes;

/// <summary>Mailbox types from <c>msExchRecipientTypeDetails</c>, the selections and their LDAP filters.</summary>
public class MailboxTypesTests
{
    [Theory]
    [InlineData(1L, MailboxType.User)]
    [InlineData(2L, MailboxType.Linked)]
    [InlineData(4L, MailboxType.Shared)]
    [InlineData(16L, MailboxType.Room)]
    [InlineData(32L, MailboxType.Equipment)]
    [InlineData(2147483648L, MailboxType.RemoteUser)]
    [InlineData(8589934592L, MailboxType.RemoteRoom)]
    [InlineData(17179869184L, MailboxType.RemoteEquipment)]
    [InlineData(34359738368L, MailboxType.RemoteShared)]
    [InlineData(0L, MailboxType.Other)]
    [InlineData(-1L, MailboxType.Other)]
    [InlineData(64L, MailboxType.Other)]                 // mail contact
    [InlineData(128L, MailboxType.Other)]                // mail user
    [InlineData(8388608L, MailboxType.Other)]            // arbitration mailbox
    [InlineData(536870912L, MailboxType.Other)]          // discovery mailbox
    [InlineData(2147483649L, MailboxType.Other)]         // no flag combinations
    public void FromValue_MapsTheExchangeValues(long value, MailboxType expected) =>
        Assert.Equal(expected, MailboxTypes.FromValue(value));

    [Fact]
    public void FromValue_WithoutAValue_IsNone() => Assert.Equal(MailboxType.None, MailboxTypes.FromValue(null));

    [Fact]
    public void ValuesOf_RoundTripsEveryKnownType_AndIsEmptyForNoneAndOther()
    {
        foreach (var type in Enum.GetValues<MailboxType>())
        {
            var values = MailboxTypes.ValuesOf(type);
            if (type is MailboxType.None or MailboxType.Other)
            {
                Assert.Empty(values);
                continue;
            }

            Assert.Equal(type, MailboxTypes.FromValue(Assert.Single(values)));
        }
    }

    [Fact]
    public void IsMailbox_IsEverythingButNone_AndIsFunctional_TheSharedRoomAndEquipmentTypesWithTheirRemoteTwins()
    {
        MailboxType[] functional =
        [
            MailboxType.Shared, MailboxType.Room, MailboxType.Equipment, MailboxType.RemoteShared, MailboxType.RemoteRoom,
            MailboxType.RemoteEquipment,
        ];
        foreach (var type in Enum.GetValues<MailboxType>())
        {
            Assert.Equal(type != MailboxType.None, MailboxTypes.IsMailbox(type));
            Assert.Equal(functional.Contains(type), MailboxTypes.IsFunctional(type));
        }

        Assert.False(MailboxTypes.IsFunctional(MailboxType.User));
        Assert.False(MailboxTypes.IsFunctional(MailboxType.Linked));
        Assert.False(MailboxTypes.IsFunctional(MailboxType.RemoteUser));
        Assert.False(MailboxTypes.IsFunctional(MailboxType.Other));
    }

    [Fact]
    public void TypesOf_TheSelections()
    {
        Assert.Equal([MailboxType.Shared, MailboxType.RemoteShared], MailboxTypes.TypesOf(MailboxSelection.Shared));
        Assert.All(MailboxTypes.TypesOf(MailboxSelection.SharedRoomsAndEquipment), t => Assert.True(MailboxTypes.IsFunctional(t)));
        Assert.Equal(6, MailboxTypes.TypesOf(MailboxSelection.SharedRoomsAndEquipment).Count);
        Assert.Equal(
            Enum.GetValues<MailboxType>().Where(t => t is not (MailboxType.None or MailboxType.Other)).Order(),
            MailboxTypes.TypesOf(MailboxSelection.AllMailboxes).Order());
        Assert.Empty(MailboxTypes.TypesOf(MailboxSelection.AllAccounts));
        Assert.Throws<ArgumentOutOfRangeException>(() => MailboxTypes.TypesOf((MailboxSelection)42));
    }

    [Fact]
    public void Filter_IsAnOrOfTheValues_ValidAndNullForAllAccounts()
    {
        Assert.Equal("(|(msExchRecipientTypeDetails=4)(msExchRecipientTypeDetails=34359738368))", MailboxTypes.Filter(MailboxSelection.Shared));
        var rooms = MailboxTypes.Filter(MailboxSelection.SharedRoomsAndEquipment)!;
        Assert.Contains("(msExchRecipientTypeDetails=16)", rooms, StringComparison.Ordinal);
        Assert.Contains("(msExchRecipientTypeDetails=17179869184)", rooms, StringComparison.Ordinal);
        Assert.DoesNotContain("(msExchRecipientTypeDetails=1)", rooms, StringComparison.Ordinal);
        Assert.Contains("(msExchRecipientTypeDetails=1)", MailboxTypes.Filter(MailboxSelection.AllMailboxes)!, StringComparison.Ordinal);
        Assert.Contains("(msExchRecipientTypeDetails=2147483648)", MailboxTypes.Filter(MailboxSelection.AllMailboxes)!, StringComparison.Ordinal);
        Assert.Null(MailboxTypes.Filter(MailboxSelection.AllAccounts));
        foreach (var selection in new[] { MailboxSelection.Shared, MailboxSelection.SharedRoomsAndEquipment, MailboxSelection.AllMailboxes })
        {
            Assert.Null(LdapFilter.Validate(MailboxTypes.Filter(selection)));
        }
    }

    [Fact]
    public void Of_ReadsTheAttribute_MissingIsNone_GarbageIsOther()
    {
        Assert.Equal(MailboxType.Shared, MailboxTypes.Of(Account("Sekretariat", (MailboxTypes.RecipientTypeDetails, "4"))));
        Assert.Equal(MailboxType.RemoteShared, MailboxTypes.Of(Account("Zdalna", (MailboxTypes.RecipientTypeDetails, "34359738368"))));
        Assert.Equal(MailboxType.None, MailboxTypes.Of(Account("Jan Kowalski", (Attr.GivenName, "Jan"))));
        Assert.Equal(MailboxType.None, MailboxTypes.Of(Account("Pusta", (MailboxTypes.RecipientTypeDetails, " "))));
        Assert.Equal(MailboxType.Other, MailboxTypes.Of(Account("Śmieci", (MailboxTypes.RecipientTypeDetails, "współdzielona"))));
        Assert.Throws<ArgumentNullException>(() => MailboxTypes.Of(null!));
    }

    [Fact]
    public void HasExchangeAttributes_FollowsTheSchema()
    {
        Assert.True(MailboxTypes.HasExchangeAttributes(FakeSchema.Create()));
        Assert.False(MailboxTypes.HasExchangeAttributes(FakeSchema.CreateWithoutMailboxes()));
        Assert.False(MailboxTypes.HasExchangeAttributes(null));
    }
}
