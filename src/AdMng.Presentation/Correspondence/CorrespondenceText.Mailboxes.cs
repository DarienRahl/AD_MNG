using AdMng.Core.Correspondence.Mailboxes;

namespace AdMng.Presentation.Correspondence;

public static partial class CorrespondenceText
{
    /// <summary><c>Correspondence.Mailboxes.Status.{status}</c> of the matcher's result.</summary>
    public static string MailboxStatusKey(OwnerMatchStatus status) => "Correspondence.Mailboxes.Status." + status;

    /// <summary><c>Correspondence.Mailboxes.Status.Manual</c> / <c>.Excluded</c> (the operator's decision); none for Automatic.</summary>
    public static string MailboxStatusKey(MailboxOwnerRowState state) => "Correspondence.Mailboxes.Status." + state;

    /// <summary><c>Correspondence.Mailboxes.Selection.{value}</c>.</summary>
    public static string SelectionKey(MailboxSelection selection) => "Correspondence.Mailboxes.Selection." + selection;

    /// <summary><c>Correspondence.Mailboxes.NameSource.{value}</c>.</summary>
    public static string NameSourceKey(OwnerNameSource source) => "Correspondence.Mailboxes.NameSource." + source;

    /// <summary><c>Correspondence.Mailboxes.Filter.{value}</c>.</summary>
    public static string FilterKey(MailboxRowFilter filter) => "Correspondence.Mailboxes.Filter." + filter;

    /// <summary><c>Correspondence.Mailboxes.Type.{type}</c>: the mailbox type of a row.</summary>
    public static string MailboxTypeKey(MailboxType type) => "Correspondence.Mailboxes.Type." + type;

    /// <summary><c>Correspondence.Mailboxes.Level.{level}</c>: why a candidate matches.</summary>
    public static string LevelKey(OwnerMatchLevel level) => "Correspondence.Mailboxes.Level." + level;

    /// <summary>Dynamic keys of the mailbox owners page (track mailboxes).</summary>
    private static partial IEnumerable<string> MailboxesKeys() =>
    [
        .. Enum.GetValues<OwnerMatchStatus>().Select(MailboxStatusKey),
        .. Enum.GetValues<MailboxOwnerRowState>().Where(s => s != MailboxOwnerRowState.Automatic).Select(MailboxStatusKey),
        .. Enum.GetValues<MailboxSelection>().Select(SelectionKey),
        .. Enum.GetValues<OwnerNameSource>().Select(NameSourceKey),
        .. Enum.GetValues<MailboxRowFilter>().Select(FilterKey),
        .. Enum.GetValues<MailboxType>().Select(MailboxTypeKey),
        .. Enum.GetValues<OwnerMatchLevel>().Select(LevelKey),
    ];
}
