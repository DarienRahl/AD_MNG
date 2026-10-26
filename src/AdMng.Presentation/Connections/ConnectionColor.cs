using System.Text;

namespace AdMng.Presentation.Connections;

/// <summary>
/// The identity colour of a connection window (strip, badge, dialog header). Amber and red are deliberately absent:
/// amber is the Tier 0 identity banner, red means destructive or error. Colour is never the only cue - the domain
/// name is always shown next to it.
/// </summary>
public enum ConnectionColor
{
    Blue,
    Teal,
    Green,
    Purple,
    Magenta,
    Indigo,
    Brown,
    Slate,
}

/// <summary>Where the colour of a connection came from.</summary>
public enum ConnectionColorSource
{
    /// <summary>Derived from the domain name (and moved on a collision with another open identity).</summary>
    Automatic,

    /// <summary>Chosen in the connection profile or the connect dialog (never changed).</summary>
    Profile,
}

/// <summary>The palette and the automatic colour of a domain.</summary>
public static class ConnectionColors
{
    /// <summary>The palette in assignment order.</summary>
    public static IReadOnlyList<ConnectionColor> Palette { get; } = Enum.GetValues<ConnectionColor>();

    /// <summary>
    /// The automatic colour of a domain: <c>palette[FNV-1a(lower-case DNS name) % 8]</c>. Stable across runs and machines
    /// (never <see cref="string.GetHashCode()"/>, which is randomized per process), so operators learn "contoso is …".
    /// </summary>
    public static ConnectionColor Automatic(string domainDnsName)
    {
        ArgumentNullException.ThrowIfNull(domainDnsName);
        var normalized = domainDnsName.Trim().TrimEnd('.').ToLowerInvariant();
        return Palette[(int)(Fnv1a32(normalized) % (uint)Palette.Count)];
    }

    /// <summary>
    /// The sRGB value of a colour: the strip, the badge background and the dot (the badge text is white). Mid-dark tones, so
    /// white text keeps a contrast of at least 4.5:1 (WCAG AA) in the light and the dark theme alike.
    /// </summary>
    public static (byte R, byte G, byte B) Rgb(ConnectionColor color) => color switch
    {
        ConnectionColor.Blue => (0x0B, 0x5C, 0xAD),
        ConnectionColor.Teal => (0x00, 0x71, 0x6F),
        ConnectionColor.Green => (0x1E, 0x7A, 0x32),
        ConnectionColor.Purple => (0x6B, 0x3F, 0xA0),
        ConnectionColor.Magenta => (0xA8, 0x23, 0x6B),
        ConnectionColor.Indigo => (0x39, 0x49, 0xAB),
        ConnectionColor.Brown => (0x7A, 0x4E, 0x2D),
        ConnectionColor.Slate => (0x4A, 0x58, 0x66),
        _ => throw new ArgumentOutOfRangeException(nameof(color), color, null),
    };

    /// <summary>The neutral grey of a window that is not connected.</summary>
    public static (byte R, byte G, byte B) NotConnectedRgb => (0x6B, 0x6B, 0x6B);

    /// <summary>WCAG 2 contrast ratio of two sRGB colours (1 … 21).</summary>
    public static double ContrastRatio((byte R, byte G, byte B) first, (byte R, byte G, byte B) second)
    {
        var a = RelativeLuminance(first);
        var b = RelativeLuminance(second);
        return (Math.Max(a, b) + 0.05) / (Math.Min(a, b) + 0.05);
    }

    /// <summary>The next palette entry after <paramref name="color"/> (wrapping).</summary>
    public static ConnectionColor Next(ConnectionColor color) => Palette[((int)color + 1) % Palette.Count];

    private static double RelativeLuminance((byte R, byte G, byte B) color)
    {
        static double Channel(byte value)
        {
            var c = value / 255.0;
            return c <= 0.03928 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
        }

        return (0.2126 * Channel(color.R)) + (0.7152 * Channel(color.G)) + (0.0722 * Channel(color.B));
    }

    /// <summary>32-bit FNV-1a over the UTF-8 bytes of <paramref name="text"/>.</summary>
    public static uint Fnv1a32(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        const uint OffsetBasis = 2166136261;
        const uint Prime = 16777619;
        var hash = OffsetBasis;
        foreach (var b in Encoding.UTF8.GetBytes(text))
        {
            hash ^= b;
            hash *= Prime;
        }

        return hash;
    }
}
