using System.Runtime.Versioning;

namespace AdMng.Outlook;

/// <summary>
/// The voting options of an item. AD Manager keeps them separated by ';' (<c>OutlookMessage.VotingOptions</c>), but Outlook
/// splits <c>MailItem.VotingOptions</c> on the Windows list separator – the value <c>sList</c> under
/// <c>HKCU\Control Panel\International</c> (Outlook object model reference) – which is ';' with Polish formats and ',' with
/// English (United States) ones. The options are therefore joined with the user's list separator; a delimiter inside an
/// option, which Outlook would split into two buttons, becomes a look-alike.
/// </summary>
internal static class OutlookVotingOptions
{
    /// <summary>The separator of AD Manager's options, and Outlook's when the user's one cannot be read.</summary>
    public const string Separator = ";";

    /// <summary>What stands for a comma inside an option while ',' is Outlook's delimiter: U+201A, which looks like one (and
    /// exists in the ANSI code pages 1250 and 1252 of the item's ANSI names). <see cref="RestoreVote"/> turns it back.</summary>
    public const char CommaLookAlike = '‚';

    /// <summary>The user's list separator: <c>GetLocaleInfoEx(LOCALE_NAME_USER_DEFAULT, LOCALE_SLIST)</c>, i.e. the sList
    /// Outlook reads – not the culture of AD Manager's threads, which may follow its UI language. <see cref="Separator"/>
    /// when it cannot be read or off Windows.</summary>
    public static string UserListSeparator()
    {
        if (!OperatingSystem.IsWindows())
        {
            return Separator;
        }

        try
        {
            return ReadListSeparator() is { Length: > 0 } separator ? separator : Separator;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return Separator;
        }
    }

    /// <summary>
    /// <c>MailItem.VotingOptions</c> for <paramref name="options"/> (separated by ';'): the options trimmed, empty ones
    /// dropped, joined with <paramref name="delimiter"/> (Outlook's, <see cref="UserListSeparator"/>; null or empty: ';'). A
    /// delimiter inside an option becomes a look-alike – a comma <see cref="CommaLookAlike"/>, a blank a no-break space,
    /// anything else a space – so every option stays one button. Null when no option is left.
    /// </summary>
    public static string? ForOutlook(string? options, string? delimiter)
    {
        if (string.IsNullOrWhiteSpace(options))
        {
            return null;
        }

        var outlook = string.IsNullOrEmpty(delimiter) ? Separator : delimiter;
        var lookAlike = outlook == "," ? CommaLookAlike.ToString() : string.IsNullOrWhiteSpace(outlook) ? " " : " ";
        var parts = options.Split(Separator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(option => option.Replace(outlook, lookAlike, StringComparison.Ordinal).Trim())
            .Where(option => option.Length > 0)
            .ToList();
        return parts.Count == 0 ? null : string.Join(outlook, parts);
    }

    /// <summary>A vote read back (<c>Recipient.AutoResponse</c>) as the option was written in AD Manager: the comma
    /// look-alike of <see cref="ForOutlook"/> becomes a comma again.</summary>
    public static string RestoreVote(string vote) => vote.Replace(CommaLookAlike, ',');

    [SupportedOSPlatform("windows")]
    private static unsafe string? ReadListSeparator()
    {
        const uint LocaleSList = 0x0000000C;
        const int Capacity = 8;
        var buffer = stackalloc char[Capacity];
        var length = ComNative.GetLocaleInfoEx(null, LocaleSList, buffer, Capacity);
        return length > 1 ? new string(buffer, 0, length - 1) : null;
    }
}
