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

    /// <summary>The next palette entry after <paramref name="color"/> (wrapping).</summary>
    public static ConnectionColor Next(ConnectionColor color) => Palette[((int)color + 1) % Palette.Count];

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
