using System.Diagnostics.CodeAnalysis;
using System.Globalization;

namespace AdMng.Core.ActiveDirectory;

/// <summary>
/// The weekly schedule of the <c>logonHours</c> attribute: 21 bytes = 168 bits, one bit per hour of the week in
/// <b>UTC</b>, starting on Sunday 00:00 UTC. Bit 0 (the least significant bit) of byte 0 is Sunday 00:00–01:00 UTC,
/// bit 7 of byte 0 is Sunday 07:00–08:00, bit 0 of byte 1 is Sunday 08:00–09:00 … bit 7 of byte 20 is Saturday
/// 23:00–24:00 UTC (the layout of <c>USER_INFO_11.usri11_logon_hours</c>). A set bit permits logon in that hour.
/// Immutable, compared by value.
/// </summary>
/// <remarks>
/// An absent attribute means "no restriction" and is the same schedule as <see cref="All"/> (21 × 0xFF, which ADUC
/// writes when every hour is permitted); <see cref="None"/> (21 × 0x00) denies every logon. Operators see the schedule
/// in local time: <see cref="ToLocalGrid"/> / <see cref="FromLocalGrid"/> rotate the week by a whole number of hours,
/// and <see cref="OffsetHours"/> computes that offset like ADUC does (the current UTC offset of the operator's machine,
/// truncated toward zero), so a schedule entered in winter appears one hour later in summer - the DC applies UTC.
/// </remarks>
public sealed class LogonHours : IEquatable<LogonHours>
{
    /// <summary>Hours (bits) per week.</summary>
    public const int HoursPerWeek = 7 * 24;

    /// <summary>Length of the attribute value in bytes.</summary>
    public const int ByteLength = HoursPerWeek / 8;

    private readonly byte[] _bytes;

    private LogonHours(byte[] bytes)
    {
        _bytes = bytes;
        PermittedCount = bytes.Sum(b => System.Numerics.BitOperations.PopCount(b));
    }

    /// <summary>Every hour permitted (what ADUC writes for "no restriction").</summary>
    public static LogonHours All { get; } = new(Enumerable.Repeat((byte)0xFF, ByteLength).ToArray());

    /// <summary>No hour permitted: the account can never log on.</summary>
    public static LogonHours None { get; } = new(new byte[ByteLength]);

    /// <summary>Number of permitted hours in the week.</summary>
    public int PermittedCount { get; }

    public bool IsAllPermitted => PermittedCount == HoursPerWeek;

    public bool IsNonePermitted => PermittedCount == 0;

    /// <summary>The schedule of a 21-byte attribute value.</summary>
    /// <exception cref="ArgumentException">The value is not exactly <see cref="ByteLength"/> bytes long.</exception>
    public static LogonHours FromBytes(ReadOnlySpan<byte> value) =>
        TryFromBytes(value, out var hours)
            ? hours
            : throw new ArgumentException($"logonHours must be {ByteLength} bytes long, not {value.Length}.", nameof(value));

    public static bool TryFromBytes(ReadOnlySpan<byte> value, [NotNullWhen(true)] out LogonHours? hours)
    {
        hours = value.Length == ByteLength ? new LogonHours(value.ToArray()) : null;
        return hours is not null;
    }

    /// <summary>
    /// The effective schedule of an attribute value as read from the directory: absent (null or empty) means all hours
    /// permitted; null when the value is not 21 bytes long (invalid data).
    /// </summary>
    public static LogonHours? FromAttribute(byte[]? value) =>
        value is null || value.Length == 0 ? All : TryFromBytes(value, out var hours) ? hours : null;

    /// <summary>
    /// Parses 42 hexadecimal digits (the change pipeline's and the Watchdog's form). Upper and lower case are accepted,
    /// and so is whitespace between the digits (ADUC's attribute editor shows "FF FF FF …").
    /// </summary>
    public static bool TryParseHex(string? text, [NotNullWhen(true)] out LogonHours? hours)
    {
        hours = null;
        if (string.IsNullOrEmpty(text))
        {
            return false;
        }

        Span<char> digits = stackalloc char[ByteLength * 2];
        var count = 0;
        foreach (var c in text)
        {
            if (char.IsWhiteSpace(c))
            {
                continue;
            }

            if (!char.IsAsciiHexDigit(c) || count == digits.Length)
            {
                return false;
            }

            digits[count++] = c;
        }

        if (count != digits.Length)
        {
            return false;
        }

        var bytes = new byte[ByteLength];
        for (var i = 0; i < ByteLength; i++)
        {
            bytes[i] = byte.Parse(digits.Slice(i * 2, 2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture);
        }

        hours = new LogonHours(bytes);
        return true;
    }

    /// <summary>
    /// The current UTC offset of <paramref name="zone"/> at <paramref name="instant"/> in whole hours, truncated toward
    /// zero (UTC+05:30 → 5, UTC−03:30 → −3) - what ADUC rotates the grid by.
    /// </summary>
    public static int OffsetHours(TimeZoneInfo zone, DateTimeOffset instant)
    {
        ArgumentNullException.ThrowIfNull(zone);
        return (int)(zone.GetUtcOffset(instant).Ticks / TimeSpan.TicksPerHour);
    }

    /// <summary>
    /// The schedule of a local-time grid: local hour of the week <c>L</c> is UTC hour <c>L − offset</c> (modulo the
    /// week), local = UTC + <paramref name="offsetHours"/>.
    /// </summary>
    public static LogonHours FromLocalGrid(LogonHoursGrid grid, int offsetHours)
    {
        ArgumentNullException.ThrowIfNull(grid);
        var bytes = new byte[ByteLength];
        for (var utc = 0; utc < HoursPerWeek; utc++)
        {
            if (grid.IsPermitted(Wrap(utc + offsetHours)))
            {
                bytes[utc / 8] |= (byte)(1 << (utc % 8));
            }
        }

        return new LogonHours(bytes);
    }

    /// <summary>A copy of the attribute value (21 bytes).</summary>
    public byte[] ToBytes() => (byte[])_bytes.Clone();

    /// <summary>42 lowercase hexadecimal digits.</summary>
    public string ToHex() => Convert.ToHexStringLower(_bytes);

    /// <summary>Whether logon is permitted in the UTC hour <paramref name="hourOfWeek"/> (0 = Sunday 00:00–01:00 UTC).</summary>
    public bool IsPermittedUtc(int hourOfWeek)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(hourOfWeek);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(hourOfWeek, HoursPerWeek);
        return (_bytes[hourOfWeek / 8] & (1 << (hourOfWeek % 8))) != 0;
    }

    /// <summary>Whether logon is permitted on <paramref name="day"/> from <paramref name="hour"/>:00 UTC.</summary>
    public bool IsPermittedUtc(DayOfWeek day, int hour) => IsPermittedUtc(LogonHoursGrid.HourOfWeek(day, hour));

    /// <summary>The schedule in local time, local = UTC + <paramref name="offsetHours"/> (see <see cref="OffsetHours"/>).</summary>
    public LogonHoursGrid ToLocalGrid(int offsetHours)
    {
        var grid = new LogonHoursGrid();
        for (var local = 0; local < HoursPerWeek; local++)
        {
            grid.SetPermitted(local, IsPermittedUtc(Wrap(local - offsetHours)));
        }

        return grid;
    }

    public bool Equals(LogonHours? other) => other is not null && _bytes.AsSpan().SequenceEqual(other._bytes);

    public override bool Equals(object? obj) => Equals(obj as LogonHours);

    public override int GetHashCode()
    {
        var hash = default(HashCode);
        hash.AddBytes(_bytes);
        return hash.ToHashCode();
    }

    /// <summary>The hexadecimal form (<see cref="ToHex"/>).</summary>
    public override string ToString() => ToHex();

    private static int Wrap(int hourOfWeek) => ((hourOfWeek % HoursPerWeek) + HoursPerWeek) % HoursPerWeek;
}

/// <summary>
/// A 7 × 24 grid of hours in local time, indexed by <c>[DayOfWeek, hour]</c> (Sunday = 0 like <see cref="DayOfWeek"/>):
/// the editing buffer of the logon hours dialog. Convert with <see cref="LogonHours.ToLocalGrid"/> and
/// <see cref="LogonHours.FromLocalGrid"/>.
/// </summary>
public sealed class LogonHoursGrid
{
    private readonly bool[] _hours = new bool[LogonHours.HoursPerWeek];

    /// <param name="permitted">The initial state of every hour.</param>
    public LogonHoursGrid(bool permitted = false)
    {
        if (permitted)
        {
            Array.Fill(_hours, true);
        }
    }

    /// <summary>Number of permitted hours.</summary>
    public int PermittedCount => _hours.Count(h => h);

    public bool this[DayOfWeek day, int hour]
    {
        get => _hours[HourOfWeek(day, hour)];
        set => _hours[HourOfWeek(day, hour)] = value;
    }

    /// <summary>A grid where <paramref name="permitted"/> decides every hour.</summary>
    public static LogonHoursGrid Create(Func<DayOfWeek, int, bool> permitted)
    {
        ArgumentNullException.ThrowIfNull(permitted);
        var grid = new LogonHoursGrid();
        for (var i = 0; i < LogonHours.HoursPerWeek; i++)
        {
            grid._hours[i] = permitted((DayOfWeek)(i / 24), i % 24);
        }

        return grid;
    }

    public LogonHoursGrid Clone()
    {
        var copy = new LogonHoursGrid();
        Array.Copy(_hours, copy._hours, _hours.Length);
        return copy;
    }

    /// <summary>Hour of the week, 0 = Sunday 00:00.</summary>
    internal static int HourOfWeek(DayOfWeek day, int hour)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan((int)day, (int)DayOfWeek.Sunday, nameof(day));
        ArgumentOutOfRangeException.ThrowIfGreaterThan((int)day, (int)DayOfWeek.Saturday, nameof(day));
        ArgumentOutOfRangeException.ThrowIfNegative(hour);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(hour, 23);
        return ((int)day * 24) + hour;
    }

    internal bool IsPermitted(int hourOfWeek) => _hours[hourOfWeek];

    internal void SetPermitted(int hourOfWeek, bool permitted) => _hours[hourOfWeek] = permitted;
}
