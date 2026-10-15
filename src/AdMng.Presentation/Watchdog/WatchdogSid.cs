using System.Text.RegularExpressions;

namespace AdMng.Presentation.Watchdog;

/// <summary>SID text checks of the Watchdog editors (actor filters and exclusions accept SIDs only – section 8.3 <c>invalidSid</c>).</summary>
public static partial class WatchdogSid
{
    /// <summary>True for "S-1-5-21-…-1105" (no tokens, no names).</summary>
    public static bool IsValid(string? text) => text is not null && SidPattern().IsMatch(text);

    [GeneratedRegex(@"^S-1-\d+(-\d+)+$", RegexOptions.CultureInvariant)]
    private static partial Regex SidPattern();
}
