using System.Globalization;
using System.Resources;

namespace AdMng.PowerShell.Reading;

/// <summary>
/// The texts of AD Manager's commands (<c>Resources/CommandStrings(.pl).resx</c>) in the window's language - the culture of the
/// session's host (audit ps-commands-8: about 28 errors and warnings were English in a Polish window). The help of the commands
/// (<c>Get-Help</c>) stays English like the help of PowerShell itself, and so do the messages of the engine and of the window's own
/// refusals of the session (the guide says so). A missing key comes back as itself in brackets (a test keeps every key present).
/// </summary>
internal static class AdmText
{
    private static readonly ResourceManager Resources = new("AdMng.PowerShell.Resources.CommandStrings", typeof(AdmText).Assembly);

    /// <summary>The text of <paramref name="key"/> in <paramref name="culture"/>.</summary>
    public static string Get(CultureInfo culture, string key) => Resources.GetString(key, culture) ?? "[" + key + "]";

    /// <summary>The text of <paramref name="key"/> formatted with <paramref name="args"/> in <paramref name="culture"/>.</summary>
    public static string Format(CultureInfo culture, string key, params object?[] args) => string.Format(culture, Get(culture, key), args);

    /// <summary>The plural form of <paramref name="keyBase"/> for <paramref name="count"/> (<c>.One</c> / <c>.Few</c> / <c>.Many</c>, the Polish rules), with the count as <c>{0}</c>.</summary>
    public static string Plural(CultureInfo culture, string keyBase, long count, params object?[] more) =>
        Format(culture, keyBase + "." + PluralForm(count), [count, .. more]);

    /// <summary>One (1), Few (2-4, 22-24, ..., not 12-14), Many; the English texts repeat one form for Few and Many.</summary>
    public static string PluralForm(long count)
    {
        var n = Math.Abs(count);
        if (n == 1)
        {
            return "One";
        }

        var lastDigit = n % 10;
        var lastTwo = n % 100;
        return lastDigit is >= 2 and <= 4 && lastTwo is < 12 or > 14 ? "Few" : "Many";
    }

    /// <summary>The keys of the texts.</summary>
    internal static class Keys
    {
        public const string NotFoundUser = "NotFound.User";
        public const string NotFoundUserUnder = "NotFound.User.Under";
        public const string NotFoundGroup = "NotFound.Group";
        public const string NotFoundGroupUnder = "NotFound.Group.Under";
        public const string NotFoundComputer = "NotFound.Computer";
        public const string NotFoundComputerUnder = "NotFound.Computer.Under";
        public const string NotFoundObject = "NotFound.Object";
        public const string NotFoundObjectUnder = "NotFound.Object.Under";
        public const string IdentityEmpty = "Identity.Empty";
        public const string DirectoryAccessDenied = "Directory.AccessDenied";
        public const string DirectoryAccessDeniedAt = "Directory.AccessDenied.At";
        public const string DirectoryNoSuchObject = "Directory.NoSuchObject";
        public const string DirectoryNoSuchObjectAt = "Directory.NoSuchObject.At";
        public const string DirectoryRefused = "Directory.Refused";
        public const string DirectoryRefusedAt = "Directory.Refused.At";
        public const string DirectoryNoAnswer = "Directory.NoAnswer";
        public const string DirectoryNoAnswerAt = "Directory.NoAnswer.At";
        public const string DirectoryLimited = "Directory.Limited";
        public const string DirectoryLimitedAt = "Directory.Limited.At";
        public const string DirectoryFailed = "Directory.Failed";
        public const string DirectoryFailedAt = "Directory.Failed.At";
        public const string AttributeHidden = "Attribute.Hidden";
        public const string PropertiesNotAName = "Properties.NotAName";
        public const string PropertiesUnknown = "Properties.Unknown";
        public const string LdapFilterInvalid = "LdapFilter.Invalid";
        public const string SearchBaseInvalid = "SearchBase.Invalid";
        public const string OpenTooMany = "Open.TooMany";
        public const string OpenNotOpened = "Open.NotOpened";
        public const string GridNoObjects = "Grid.NoObjects";
        public const string ContextMissing = "Context.Missing";
        public const string ServicesUnavailable = "Services.Unavailable";
        public const string ExportInvalidDelimiter = "Export.InvalidDelimiter";
        public const string ExportNoObjects = "Export.NoObjects";
        public const string ExportNoFile = "Export.NoFile";
        public const string ExportFileExists = "Export.FileExists";
        public const string ExportNotFileSystem = "Export.NotFileSystem";
        public const string SearchNotAName = "Search.NotAName";
        public const string SearchUnknown = "Search.Unknown";
        public const string SearchSecret = "Search.Secret";
        public const string SearchEmptyValue = "Search.EmptyValue";
        public const string CellSecureString = "Cell.SecureString";

        /// <summary>Plural: {0} = how many members, {1} = the group, {2} = examples.</summary>
        public const string MembersMissing = "Members.Missing";

        /// <summary>Plural: {0} = the size of a binary value in bytes.</summary>
        public const string CellBinary = "Cell.Binary";
    }
}
