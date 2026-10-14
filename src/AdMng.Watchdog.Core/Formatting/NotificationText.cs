using System.Globalization;
using System.Resources;
using AdMng.Watchdog.Contracts.Events;

namespace AdMng.Watchdog.Core.Formatting;

/// <summary>
/// The notification texts (<c>Resources/NotificationStrings(.pl).resx</c>) in one explicit culture – never the thread
/// culture (section 14.1). Key families: <c>Kind.{Kind}.Title|TitlePlural|Sentence</c>, <c>Severity.{Severity}</c>,
/// <c>Attr.{ldapName}</c>, <c>Reason.{code}</c> + <c>.Remedy</c>, <c>Flag.{NAME}</c>, <c>Digest.*</c>, <c>Email.*</c>,
/// <c>Card.*</c>, <c>Label.*</c>, <c>Value.*</c>, <c>Day.*</c>, <c>Rule.{id}</c>, <c>Webhook.*</c>.
/// </summary>
public sealed class NotificationText
{
    /// <summary>Base name of the notification resources.</summary>
    public const string ResourceBaseName = "AdMng.Watchdog.Core.Resources.NotificationStrings";

    private static readonly ResourceManager Resources = new(ResourceBaseName, typeof(NotificationText).Assembly) { IgnoreCase = true };

    private static readonly NotificationText PolishText = new(CultureInfo.GetCultureInfo("pl-PL"));

    private static readonly NotificationText EnglishText = new(CultureInfo.GetCultureInfo("en-US"));

    private NotificationText(CultureInfo culture)
    {
        Culture = culture;
    }

    /// <summary>The culture of every text and number of this instance.</summary>
    public CultureInfo Culture { get; }

    /// <summary>True for Polish.</summary>
    public bool IsPolish => Culture.TwoLetterISOLanguageName == "pl";

    /// <summary>Polish texts.</summary>
    public static NotificationText Polish => PolishText;

    /// <summary>English texts.</summary>
    public static NotificationText English => EnglishText;

    /// <summary>The texts of a configured language (<c>General.Language</c>: "pl" | "en"; anything else is Polish, the
    /// product language).</summary>
    public static NotificationText For(string? language) =>
        string.Equals(language?.Trim(), "en", StringComparison.OrdinalIgnoreCase) ? EnglishText : PolishText;

    /// <summary>The text of <paramref name="key"/>; the key itself in brackets when it is missing (a resx test keeps
    /// every generated key present).</summary>
    public string Get(string key) => Find(key) ?? "[" + key + "]";

    /// <summary>The text of <paramref name="key"/>, or null when missing.</summary>
    public string? Find(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        return Resources.GetString(key, Culture);
    }

    /// <summary>The text of <paramref name="key"/> formatted with <paramref name="args"/> in this culture.</summary>
    public string Format(string key, params object?[] args) => string.Format(Culture, Get(key), args);

    /// <summary>The plural form of <paramref name="keyBase"/> for <paramref name="count"/>
    /// (<c>.One</c> / <c>.Few</c> / <c>.Many</c>, Polish rules) formatted with the count as <c>{0}</c>.</summary>
    public string Plural(string keyBase, long count, params object?[] more) =>
        string.Format(Culture, Get(keyBase + "." + PluralForm(count)), [count, .. more]);

    /// <summary>Polish plural category of <paramref name="count"/>: One (1), Few (2–4, 22–24 …, not 12–14), Many.
    /// English resources repeat the same text for Few and Many.</summary>
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

    /// <summary>Severity word as shown in subjects and banners (upper case).</summary>
    public string SeverityWord(Severity severity) => Get("Severity." + severity).ToUpper(Culture);

    /// <summary>Severity name in normal case.</summary>
    public string SeverityName(Severity severity) => Get("Severity." + severity);

    /// <summary>Title of a kind (singular or plural).</summary>
    public string KindTitle(WatchEventKind kind, bool plural = false) => Get("Kind." + kind + (plural ? ".TitlePlural" : ".Title"));

    /// <summary>A number in this culture.</summary>
    public string Number(long value) => value.ToString("N0", Culture);
}
