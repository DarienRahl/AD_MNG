using System.Globalization;

namespace AdMng.Directory;

/// <summary>
/// Range option of a ranged attribute description ([MS-ADTS] 3.1.1.3.1.3.3), e.g. <c>member;range=0-1499</c>
/// or <c>member;range=1500-*</c>.
/// </summary>
internal readonly record struct AttributeRange(string AttributeName, int Start, int? End)
{
    private const string RangeOption = ";range=";

    /// <summary>True for the last chunk (<c>x-*</c>).</summary>
    public bool IsLast => End is null;

    /// <summary>The attribute description to request the chunk starting at <paramref name="start"/>.</summary>
    /// <param name="attribute">Attribute name without options.</param>
    /// <param name="start">Zero based index of the first value.</param>
    /// <param name="count">Number of values wanted, or null for "as many as the server allows" (<c>start-*</c>).</param>
    public static string Request(string attribute, int start, int? count = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(attribute);
        ArgumentOutOfRangeException.ThrowIfNegative(start);
        if (count is { } c)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(c, 1, nameof(count));
            return string.Create(CultureInfo.InvariantCulture, $"{attribute}{RangeOption}{start}-{start + c - 1}");
        }

        return string.Create(CultureInfo.InvariantCulture, $"{attribute}{RangeOption}{start}-*");
    }

    /// <summary>Parses an attribute description returned by the server. False when it carries no range option.</summary>
    public static bool TryParse(string? description, out AttributeRange range)
    {
        range = default;
        if (string.IsNullOrEmpty(description))
        {
            return false;
        }

        var index = description.IndexOf(RangeOption, StringComparison.OrdinalIgnoreCase);
        if (index <= 0)
        {
            return false;
        }

        var name = description[..index];
        var spec = description[(index + RangeOption.Length)..];
        var nextOption = spec.IndexOf(';', StringComparison.Ordinal);
        if (nextOption >= 0)
        {
            spec = spec[..nextOption];
        }

        var dash = spec.IndexOf('-', StringComparison.Ordinal);
        if (dash <= 0
            || !int.TryParse(spec.AsSpan(0, dash), NumberStyles.None, CultureInfo.InvariantCulture, out var start))
        {
            return false;
        }

        var endText = spec[(dash + 1)..];
        int? end;
        if (endText == "*")
        {
            end = null;
        }
        else if (int.TryParse(endText, NumberStyles.None, CultureInfo.InvariantCulture, out var e) && e >= start - 1)
        {
            end = e;
        }
        else
        {
            return false;
        }

        // Keep other options (e.g. ";binary") out of the base name.
        var semicolon = name.IndexOf(';', StringComparison.Ordinal);
        range = new AttributeRange(semicolon < 0 ? name : name[..semicolon], start, end);
        return true;
    }
}
