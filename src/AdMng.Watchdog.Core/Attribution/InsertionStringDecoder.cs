using System.Collections.Frozen;
using System.Globalization;
using System.Text;

namespace AdMng.Watchdog.Core.Attribution;

/// <summary>
/// Decodes the documented <c>%%NNNN</c> insertion strings of Security events (section 11.3): 1792
/// <c>&lt;value changed, but not displayed&gt;</c>, 1793 <c>&lt;value not set&gt;</c>, 1794 <c>&lt;never&gt;</c>, 1797
/// <c>All</c>, 14676 <c>AD DS</c>, 14677 <c>AD LDS</c>, 14678 <c>Yes</c>, 14679 <c>No</c>; the 5136 operation types
/// 14674/14675/14680/14681/14688 become the tokens <see cref="ValueAdded"/>, <see cref="ValueDeleted"/>, …. Unknown
/// codes (e.g. the <c>UserAccountControl</c> list <c>%%2048+b</c>/<c>%%2080+b</c>, cross-check only) are kept as written.
/// </summary>
public static class InsertionStringDecoder
{
    public const string ValueAdded = "ValueAdded";
    public const string ValueDeleted = "ValueDeleted";
    public const string ValueAddedWithExpirationTime = "ValueAddedWithExpirationTime";
    public const string ValueDeletedWithExpirationTime = "ValueDeletedWithExpirationTime";
    public const string ValueAutoDeletedWithExpirationTime = "ValueAutoDeletedWithExpirationTime";

    /// <summary>1792: value changed, but not displayed.</summary>
    public const string ValueChangedNotDisplayed = "<value changed, but not displayed>";

    /// <summary>1793: value not set.</summary>
    public const string ValueNotSet = "<value not set>";

    /// <summary>1794: never.</summary>
    public const string Never = "<never>";

    private static readonly FrozenDictionary<int, string> Texts = new Dictionary<int, string>
    {
        [1792] = ValueChangedNotDisplayed,
        [1793] = ValueNotSet,
        [1794] = Never,
        [1797] = "All",
        [14676] = "AD DS",
        [14677] = "AD LDS",
        [14678] = "Yes",
        [14679] = "No",
        [14674] = ValueAdded,
        [14675] = ValueDeleted,
        [14680] = ValueAddedWithExpirationTime,
        [14681] = ValueDeletedWithExpirationTime,
        [14688] = ValueAutoDeletedWithExpirationTime,
    }.ToFrozenDictionary();

    /// <summary>The value with every known <c>%%NNNN</c> token replaced; null stays null.</summary>
    public static string? Decode(string? value)
    {
        if (value is null || !value.Contains("%%", StringComparison.Ordinal))
        {
            return value;
        }

        var builder = new StringBuilder(value.Length);
        var i = 0;
        while (i < value.Length)
        {
            if (value[i] == '%' && i + 2 < value.Length && value[i + 1] == '%' && char.IsAsciiDigit(value[i + 2]))
            {
                var end = i + 2;
                while (end < value.Length && char.IsAsciiDigit(value[end]))
                {
                    end++;
                }

                if (int.TryParse(value.AsSpan(i + 2, end - i - 2), NumberStyles.None, CultureInfo.InvariantCulture, out var code)
                    && Texts.TryGetValue(code, out var text))
                {
                    builder.Append(text);
                    i = end;
                    continue;
                }

                builder.Append(value, i, end - i);
                i = end;
                continue;
            }

            builder.Append(value[i]);
            i++;
        }

        return builder.ToString();
    }

    /// <summary>The bits a <c>UserAccountControl</c> list (<c>%%2050 %%2089</c>) reports: <c>%%2048+b</c> = SAM bit b
    /// cleared, <c>%%2080+b</c> = SAM bit b set (b = 0…31). Used only to cross-check the Old/NewUacValue decoding.</summary>
    public static (uint Set, uint Cleared) UacListBits(string? value)
    {
        uint set = 0;
        uint cleared = 0;
        foreach (var token in (value ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!token.StartsWith("%%", StringComparison.Ordinal)
                || !int.TryParse(token.AsSpan(2), NumberStyles.None, CultureInfo.InvariantCulture, out var code))
            {
                continue;
            }

            if (code is >= 2048 and < 2080)
            {
                cleared |= 1u << (code - 2048);
            }
            else if (code is >= 2080 and < 2112)
            {
                set |= 1u << (code - 2080);
            }
        }

        return (set, cleared);
    }
}
