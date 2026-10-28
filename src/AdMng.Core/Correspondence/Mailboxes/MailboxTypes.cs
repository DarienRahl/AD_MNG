using AdMng.Core.Ldap;
using AdMng.Core.Model;
using AdMng.Core.Schema;

namespace AdMng.Core.Correspondence.Mailboxes;

/// <summary>The Exchange mailbox type of an account (<c>msExchRecipientTypeDetails</c>).</summary>
public enum MailboxType
{
    /// <summary>No <c>msExchRecipientTypeDetails</c> value.</summary>
    None,

    /// <summary>A personal mailbox (1).</summary>
    User,

    /// <summary>A linked mailbox of an account in another forest (2).</summary>
    Linked,

    /// <summary>A shared mailbox (4).</summary>
    Shared,

    /// <summary>A room mailbox (16).</summary>
    Room,

    /// <summary>An equipment mailbox (32).</summary>
    Equipment,

    /// <summary>A personal mailbox in Exchange Online of a hybrid organization (2147483648).</summary>
    RemoteUser,

    /// <summary>A shared mailbox in Exchange Online (34359738368).</summary>
    RemoteShared,

    /// <summary>A room mailbox in Exchange Online (8589934592).</summary>
    RemoteRoom,

    /// <summary>An equipment mailbox in Exchange Online (17179869184).</summary>
    RemoteEquipment,

    /// <summary>Any other value (mail users, arbitration and discovery mailboxes …).</summary>
    Other,
}

/// <summary>Exchange attributes and values of mailbox accounts (design note 3.7).</summary>
public static class MailboxTypes
{
    public const string RecipientTypeDetails = "msExchRecipientTypeDetails";

    /// <summary>Users with full access to the mailbox (auto-mapping) – a delegate confirms an owner.</summary>
    public const string DelegateListLink = "msExchDelegateListLink";

    /// <summary>Users allowed to send on behalf of the mailbox – a delegate confirms an owner.</summary>
    public const string PublicDelegates = "publicDelegates";

    private const long UserValue = 1;
    private const long LinkedValue = 2;
    private const long SharedValue = 4;
    private const long RoomValue = 16;
    private const long EquipmentValue = 32;
    private const long RemoteUserValue = 2147483648;
    private const long RemoteRoomValue = 8589934592;
    private const long RemoteEquipmentValue = 17179869184;
    private const long RemoteSharedValue = 34359738368;

    private static readonly IReadOnlyList<MailboxType> FunctionalTypes = Array.AsReadOnly(
    [
        MailboxType.Shared, MailboxType.Room, MailboxType.Equipment,
        MailboxType.RemoteShared, MailboxType.RemoteRoom, MailboxType.RemoteEquipment,
    ]);

    /// <summary>
    /// 1 User, 2 Linked, 4 Shared, 16 Room, 32 Equipment, 2147483648 RemoteUser, 8589934592 RemoteRoom, 17179869184
    /// RemoteEquipment, 34359738368 RemoteShared; other values → <see cref="MailboxType.Other"/>; missing →
    /// <see cref="MailboxType.None"/>.
    /// </summary>
    public static MailboxType FromValue(long? recipientTypeDetails) => recipientTypeDetails switch
    {
        null => MailboxType.None,
        UserValue => MailboxType.User,
        LinkedValue => MailboxType.Linked,
        SharedValue => MailboxType.Shared,
        RoomValue => MailboxType.Room,
        EquipmentValue => MailboxType.Equipment,
        RemoteUserValue => MailboxType.RemoteUser,
        RemoteRoomValue => MailboxType.RemoteRoom,
        RemoteEquipmentValue => MailboxType.RemoteEquipment,
        RemoteSharedValue => MailboxType.RemoteShared,
        _ => MailboxType.Other,
    };

    /// <summary>
    /// The type of an account read with <see cref="RecipientTypeDetails"/>: <see cref="MailboxType.None"/> without the value,
    /// <see cref="MailboxType.Other"/> for a value that is no number.
    /// </summary>
    public static MailboxType Of(DirectoryObject account)
    {
        ArgumentNullException.ThrowIfNull(account);
        var text = account.GetString(RecipientTypeDetails);
        if (string.IsNullOrWhiteSpace(text))
        {
            return MailboxType.None;
        }

        return account.GetInt64(RecipientTypeDetails) is { } value ? FromValue(value) : MailboxType.Other;
    }

    /// <summary>
    /// The <c>msExchRecipientTypeDetails</c> values of <paramref name="type"/>; empty for <see cref="MailboxType.None"/>
    /// and <see cref="MailboxType.Other"/> (no single value).
    /// </summary>
    public static IReadOnlyList<long> ValuesOf(MailboxType type) => type switch
    {
        MailboxType.User => [UserValue],
        MailboxType.Linked => [LinkedValue],
        MailboxType.Shared => [SharedValue],
        MailboxType.Room => [RoomValue],
        MailboxType.Equipment => [EquipmentValue],
        MailboxType.RemoteUser => [RemoteUserValue],
        MailboxType.RemoteShared => [RemoteSharedValue],
        MailboxType.RemoteRoom => [RemoteRoomValue],
        MailboxType.RemoteEquipment => [RemoteEquipmentValue],
        _ => [],
    };

    /// <summary>Not <see cref="MailboxType.None"/>.</summary>
    public static bool IsMailbox(MailboxType type) => type != MailboxType.None;

    /// <summary>Shared, Room, Equipment and their Remote twins.</summary>
    public static bool IsFunctional(MailboxType type) => FunctionalTypes.Contains(type);

    /// <summary>
    /// The types a selection reads: <see cref="MailboxSelection.Shared"/> – shared (on-premises and remote);
    /// <see cref="MailboxSelection.SharedRoomsAndEquipment"/> – every functional type; <see cref="MailboxSelection.AllMailboxes"/>
    /// – every known mailbox type (personal, linked and functional); <see cref="MailboxSelection.AllAccounts"/> – none
    /// (every user account of the scope, no type filter).
    /// </summary>
    public static IReadOnlyList<MailboxType> TypesOf(MailboxSelection selection) => selection switch
    {
        MailboxSelection.Shared => [MailboxType.Shared, MailboxType.RemoteShared],
        MailboxSelection.SharedRoomsAndEquipment => FunctionalTypes,
        MailboxSelection.AllMailboxes =>
        [
            MailboxType.User, MailboxType.Linked, MailboxType.Shared, MailboxType.Room, MailboxType.Equipment,
            MailboxType.RemoteUser, MailboxType.RemoteShared, MailboxType.RemoteRoom, MailboxType.RemoteEquipment,
        ],
        MailboxSelection.AllAccounts => [],
        _ => throw new ArgumentOutOfRangeException(nameof(selection), selection, null),
    };

    /// <summary>
    /// The LDAP filter of the <c>msExchRecipientTypeDetails</c> values of <paramref name="selection"/>
    /// (<c>(|(msExchRecipientTypeDetails=4)(msExchRecipientTypeDetails=34359738368))</c>), or null for
    /// <see cref="MailboxSelection.AllAccounts"/>. To be ANDed with the user filter, the scope and the operator's filter.
    /// </summary>
    public static string? Filter(MailboxSelection selection)
    {
        var values = TypesOf(selection).SelectMany(ValuesOf).ToList();
        return values.Count == 0 ? null : LdapFilter.Or(values.Select(v => LdapFilter.Equal(RecipientTypeDetails, v)));
    }

    /// <summary>Whether the schema has the Exchange attribute <see cref="RecipientTypeDetails"/> (the type selections need it).</summary>
    public static bool HasExchangeAttributes(ISchemaCatalog? schema) => schema?.FindAttribute(RecipientTypeDetails) is not null;
}

/// <summary>Which accounts the mailbox owner query reads.</summary>
public enum MailboxSelection
{
    /// <summary>Shared mailboxes (on-premises and remote).</summary>
    Shared,

    /// <summary>Shared, room and equipment mailboxes (on-premises and remote).</summary>
    SharedRoomsAndEquipment,

    /// <summary>Every mailbox type, personal ones included.</summary>
    AllMailboxes,

    /// <summary>Every user account of the scope (no Exchange attributes needed – for directories without the schema).</summary>
    AllAccounts,
}

/// <summary>Where the owner's name is read from on the mailbox account.</summary>
public enum OwnerNameSource
{
    /// <summary><c>givenName</c> and <c>sn</c> of the mailbox account.</summary>
    GivenNameAndSurname,

    /// <summary>The display name ("Skrzynka: dr Jan Kowalski").</summary>
    DisplayName,

    /// <summary>The description.</summary>
    Description,

    /// <summary>An attribute chosen by the operator.</summary>
    Attribute,
}
