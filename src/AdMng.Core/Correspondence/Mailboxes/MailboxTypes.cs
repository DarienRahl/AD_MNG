namespace AdMng.Core.Correspondence.Mailboxes;

/// <summary>The Exchange mailbox type of an account (<c>msExchRecipientTypeDetails</c>).</summary>
public enum MailboxType
{
    /// <summary>No <c>msExchRecipientTypeDetails</c> value.</summary>
    None,
    User,
    Linked,
    Shared,
    Room,
    Equipment,
    RemoteUser,
    RemoteShared,
    RemoteRoom,
    RemoteEquipment,
    Other,
}

/// <summary>Exchange attributes and values of mailbox accounts (design note 3.7).</summary>
public static class MailboxTypes
{
    private const string Track = "U5 T2 mailboxes";

    public const string RecipientTypeDetails = "msExchRecipientTypeDetails";

    public const string DelegateListLink = "msExchDelegateListLink";

    public const string PublicDelegates = "publicDelegates";

    /// <summary>
    /// 1 User, 2 Linked, 4 Shared, 16 Room, 32 Equipment, 2147483648 RemoteUser, 8589934592 RemoteRoom, 17179869184
    /// RemoteEquipment, 34359738368 RemoteShared; other values → <see cref="MailboxType.Other"/>; missing →
    /// <see cref="MailboxType.None"/>.
    /// </summary>
    public static MailboxType FromValue(long? recipientTypeDetails) => throw new NotImplementedException(Track);

    /// <summary>The <c>msExchRecipientTypeDetails</c> values of <paramref name="type"/>.</summary>
    public static IReadOnlyList<long> ValuesOf(MailboxType type) => throw new NotImplementedException(Track);

    /// <summary>Not <see cref="MailboxType.None"/>.</summary>
    public static bool IsMailbox(MailboxType type) => throw new NotImplementedException(Track);

    /// <summary>Shared, Room, Equipment and their Remote twins.</summary>
    public static bool IsFunctional(MailboxType type) => throw new NotImplementedException(Track);
}

/// <summary>Which accounts the mailbox owner query reads.</summary>
public enum MailboxSelection
{
    Shared,
    SharedRoomsAndEquipment,
    AllMailboxes,

    /// <summary>Every user account of the scope (no Exchange attributes needed – for directories without the schema).</summary>
    AllAccounts,
}

/// <summary>Where the owner's name is read from on the mailbox account.</summary>
public enum OwnerNameSource
{
    GivenNameAndSurname,
    DisplayName,
    Description,
    Attribute,
}
