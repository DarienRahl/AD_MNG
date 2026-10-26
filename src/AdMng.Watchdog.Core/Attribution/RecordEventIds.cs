using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using AdMng.Watchdog.Core.Model;

namespace AdMng.Watchdog.Core.Attribution;

/// <summary>
/// Stable event ids of watch events created from one Security-log record (event-log-only detections and the gap of a
/// 1102/1104/1100, section 11.6): a Guid in the version-7 layout whose timestamp is the record's <c>TimeCreated</c> and
/// whose remaining bits are a SHA-256 of (what, computer, record id, exact time, event id). Reading the same record again
/// – a page re-read after an interrupted pass, the time-window overlap of ForwardedEvents mode – yields the same id, so
/// the detection is appended once (<see cref="SecurityLogIngestor"/> looks the id up first). A cleared log that restarts
/// at record 1 yields a new id: the time differs.
/// </summary>
public static class RecordEventIds
{
    /// <summary>The id of the watch event <paramref name="what"/> (a kind or gap reason) created from
    /// <paramref name="record"/>.</summary>
    public static Guid Create(SecurityEvent record, string what)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentException.ThrowIfNullOrWhiteSpace(what);
        var key = string.Join('|', what, HostNames.Normalize(record.Computer), record.RecordId.ToString(CultureInfo.InvariantCulture),
            record.TimeCreated.UtcTicks.ToString(CultureInfo.InvariantCulture), record.EventCode.ToString(CultureInfo.InvariantCulture));
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(key));

        Span<byte> bytes = stackalloc byte[16];
        var milliseconds = Math.Max(0, record.TimeCreated.ToUnixTimeMilliseconds());
        Span<byte> time = stackalloc byte[8];
        BinaryPrimitives.WriteInt64BigEndian(time, milliseconds);
        time[2..].CopyTo(bytes);                                  // 48-bit Unix milliseconds
        bytes[6] = (byte)(0x70 | (hash[0] & 0x0F));               // version 7
        bytes[7] = hash[1];
        bytes[8] = (byte)(0x80 | (hash[2] & 0x3F));               // RFC 9562 variant
        hash.AsSpan(3, 7).CopyTo(bytes[9..]);
        return new Guid(bytes, bigEndian: true);
    }
}
