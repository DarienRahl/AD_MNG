using System.Buffers.Binary;

namespace AdMng.Core.Pki;

/// <summary>The unit a <see cref="PkiPeriod"/> is shown in (the units of <c>certtmpl.msc</c>).</summary>
public enum PkiPeriodUnit
{
    Hours,
    Days,
    Weeks,

    /// <summary>30 days, as <c>certtmpl.msc</c> and Certipy count a month.</summary>
    Months,

    /// <summary>365 days (the default one-year validity is exactly 365 days).</summary>
    Years,
}

/// <summary>
/// A template period – <c>pKIExpirationPeriod</c> or <c>pKIOverlapPeriod</c> (docs/dev/p-certificate-services.md 2.2): an
/// 8-byte little-endian <b>negative</b> 100-ns interval. The Presentation renders it in the largest unit that divides it
/// exactly (<see cref="Unit"/> × <see cref="Count"/>: years of 365 days, months of 30 days, weeks, days, hours – as
/// <c>certtmpl.msc</c> and Certipy).
/// </summary>
/// <param name="Ticks">The length in 100-ns units (positive: the stored value negated).</param>
public readonly record struct PkiPeriod(long Ticks)
{
    private const long TicksPerHour = TimeSpan.TicksPerHour;
    private const long TicksPerDay = TimeSpan.TicksPerDay;

    /// <summary>The length.</summary>
    public TimeSpan Duration => TimeSpan.FromTicks(Ticks);

    /// <summary>
    /// The largest unit that divides the length exactly (years, months, weeks, days, hours); null when no whole number of
    /// hours does (or the length is 0) – the UI then shows <see cref="Duration"/>.
    /// </summary>
    public PkiPeriodUnit? Unit => Split().Unit;

    /// <summary>The number of <see cref="Unit"/>s (0 when <see cref="Unit"/> is null).</summary>
    public long Count => Split().Count;

    /// <summary>
    /// Reads the stored value; null when it is not 8 bytes or not a non-positive interval (a positive value would be an
    /// absolute time, <see cref="long.MinValue"/> cannot be negated).
    /// </summary>
    public static PkiPeriod? Decode(ReadOnlySpan<byte> value)
    {
        if (value.Length != sizeof(long))
        {
            return null;
        }

        var stored = BinaryPrimitives.ReadInt64LittleEndian(value);
        return stored is > 0 or long.MinValue ? null : new PkiPeriod(-stored);
    }

    /// <summary>No value (template data).</summary>
    public override string ToString() => nameof(PkiPeriod);

    private (PkiPeriodUnit? Unit, long Count) Split()
    {
        if (Ticks <= 0)
        {
            return (null, 0);
        }

        foreach (var (unit, length) in Units)
        {
            if (Ticks % length == 0)
            {
                return (unit, Ticks / length);
            }
        }

        return (null, 0);
    }

    private static readonly (PkiPeriodUnit Unit, long Ticks)[] Units =
    [
        (PkiPeriodUnit.Years, 365 * TicksPerDay),
        (PkiPeriodUnit.Months, 30 * TicksPerDay),
        (PkiPeriodUnit.Weeks, 7 * TicksPerDay),
        (PkiPeriodUnit.Days, TicksPerDay),
        (PkiPeriodUnit.Hours, TicksPerHour),
    ];
}
