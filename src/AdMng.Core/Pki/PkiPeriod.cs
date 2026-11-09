namespace AdMng.Core.Pki;

/// <summary>
/// A template period – <c>pKIExpirationPeriod</c> or <c>pKIOverlapPeriod</c> (docs/dev/p-certificate-services.md 2.2): an
/// 8-byte little-endian <b>negative</b> 100-ns interval. The Presentation renders it in the largest unit that divides it
/// exactly (years, weeks, days, hours – as <c>certtmpl.msc</c>).
/// </summary>
/// <param name="Ticks">The length in 100-ns units (positive: the stored value negated).</param>
public readonly record struct PkiPeriod(long Ticks)
{
    /// <summary>The length.</summary>
    public TimeSpan Duration => TimeSpan.FromTicks(Ticks);

    /// <summary>Reads the stored value; null when it is not 8 bytes.</summary>
    public static PkiPeriod? Decode(ReadOnlySpan<byte> value) => throw new NotImplementedException("P1 C2: PkiPeriod.Decode");

    /// <summary>No value (template data).</summary>
    public override string ToString() => nameof(PkiPeriod);
}
