using System.Globalization;

namespace AdMng.Core.GroupPolicy;

/// <summary>
/// The version of a GPO: <c>versionNumber</c> of the group policy container and <c>Version=</c> of <c>GPT.INI</c> – the
/// user version in the high 16 bits, the computer (machine) version in the low 16 bits ([MS-GPOL] 2.2.4). Clients
/// reprocess a GPO only when a half changed, so versions never decrease (design note docs/dev/k-computers-and-gpo.md 3.4).
/// </summary>
/// <param name="User">User configuration version (0–65 535).</param>
/// <param name="Computer">Computer configuration version (0–65 535).</param>
public readonly record struct GpoVersion(int User, int Computer)
{
    /// <summary>Highest value of a half; a bump beyond it is refused instead of wrapping to 0.</summary>
    public const int MaxHalf = 0xFFFF;

    /// <summary>Version 0/0 of a new GPO.</summary>
    public static GpoVersion Zero { get; } = new(0, 0);

    /// <summary>The halves of a stored value (signed or unsigned 32 bits).</summary>
    public static GpoVersion FromInt32(int raw) => new((raw >> 16) & MaxHalf, raw & MaxHalf);

    /// <summary>The value as stored in <c>versionNumber</c> / <c>GPT.INI</c> (signed 32 bits, as AD keeps it).</summary>
    public int ToInt32() => unchecked((int)(((uint)(User & MaxHalf) << 16) | (uint)(Computer & MaxHalf)));

    /// <summary>The higher value of each half (AD and SYSVOL may disagree while replicating).</summary>
    public static GpoVersion Max(GpoVersion a, GpoVersion b) => new(Math.Max(a.User, b.User), Math.Max(a.Computer, b.Computer));

    /// <summary>True when either half is behind <paramref name="other"/>.</summary>
    public bool IsBehind(GpoVersion other) => User < other.User || Computer < other.Computer;

    /// <summary>
    /// The next version: each named half + 1, the other kept.
    /// </summary>
    /// <exception cref="GpoVersionOverflowException">A half to bump is already at <see cref="MaxHalf"/>.</exception>
    public GpoVersion Bump(bool user, bool computer)
    {
        if ((user && User >= MaxHalf) || (computer && Computer >= MaxHalf))
        {
            throw new GpoVersionOverflowException();
        }

        return new GpoVersion(user ? User + 1 : User, computer ? Computer + 1 : Computer);
    }

    /// <summary>Invariant, for diagnostics: <c>user 3, computer 4</c>.</summary>
    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"user {User}, computer {Computer}");
}

/// <summary>A half of a GPO version reached 65 535 and cannot be bumped (it would wrap and clients would ignore the GPO).</summary>
public sealed class GpoVersionOverflowException : Exception
{
    public GpoVersionOverflowException()
        : base("The GPO version reached its maximum.")
    {
    }

    public GpoVersionOverflowException(string message)
        : base(message)
    {
    }

    public GpoVersionOverflowException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
