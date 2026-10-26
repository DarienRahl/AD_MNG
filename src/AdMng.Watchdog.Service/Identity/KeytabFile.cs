using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace AdMng.Watchdog.Service.Identity;

/// <summary>One keytab entry as the service sees it: principal, kvno and enctype – never the key.</summary>
public sealed record KeytabEntry(string Principal, uint Kvno, int Enctype)
{
    /// <summary>MIT name of the enctype, e.g. <c>aes256-cts-hmac-sha1-96</c>.</summary>
    public string EnctypeName => Enctype switch
    {
        18 => "aes256-cts-hmac-sha1-96",
        17 => "aes128-cts-hmac-sha1-96",
        19 => "aes128-cts-hmac-sha256-128",
        20 => "aes256-cts-hmac-sha384-192",
        23 => "arcfour-hmac",
        _ => "enctype-" + Enctype.ToString(System.Globalization.CultureInfo.InvariantCulture),
    };
}

/// <summary>
/// Managed reader of an MIT keytab file (format 0x0502, big-endian) for the self-checks of design 4.7: the entries in file
/// order (the first one must be the account principal – 4.4), their kvnos and enctypes. Key bytes are skipped and the read
/// buffer is zeroed; nothing is kept or logged.
/// </summary>
public static class KeytabFile
{
    private const ushort Version = 0x0502;

    /// <summary>The entries of <paramref name="path"/>.</summary>
    /// <exception cref="InvalidDataException">Not a 0x0502 keytab or truncated.</exception>
    /// <exception cref="IOException">Unreadable.</exception>
    public static IReadOnlyList<KeytabEntry> Read(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var bytes = File.ReadAllBytes(path);
        try
        {
            return Parse(bytes);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    /// <summary>Parses keytab bytes (exposed for tests).</summary>
    /// <exception cref="InvalidDataException">Malformed.</exception>
    public static IReadOnlyList<KeytabEntry> Parse(ReadOnlySpan<byte> data)
    {
        if (data.Length < 2 || BinaryPrimitives.ReadUInt16BigEndian(data) != Version)
        {
            throw new InvalidDataException("Not an MIT keytab (format 0x0502).");
        }

        var entries = new List<KeytabEntry>();
        var position = 2;
        while (position + 4 <= data.Length)
        {
            var size = BinaryPrimitives.ReadInt32BigEndian(data[position..]);
            position += 4;
            if (size == 0)
            {
                break;
            }

            var length = size < 0 ? -(long)size : size;
            if (position + length > data.Length)
            {
                throw new InvalidDataException("Truncated keytab entry.");
            }

            if (size > 0)
            {
                entries.Add(ParseEntry(data.Slice(position, size)));
            }

            position += (int)length;
        }

        return entries;
    }

    private static KeytabEntry ParseEntry(ReadOnlySpan<byte> record)
    {
        var reader = new Reader(record);
        var components = reader.UInt16();
        var realm = reader.CountedString();
        var names = new string[components];
        for (var i = 0; i < components; i++)
        {
            names[i] = reader.CountedString();
        }

        _ = reader.UInt32();                 // name type
        _ = reader.UInt32();                 // timestamp
        uint kvno = reader.Byte();
        var enctype = reader.UInt16();
        reader.Skip(reader.UInt16());        // key – skipped, never copied
        if (reader.Remaining >= 4 && reader.UInt32() is var extended and not 0)
        {
            kvno = extended;
        }

        return new KeytabEntry(string.Join('/', names) + "@" + realm, kvno, enctype);
    }

    private ref struct Reader(ReadOnlySpan<byte> data)
    {
        private readonly ReadOnlySpan<byte> _data = data;
        private int _position;

        public readonly int Remaining => _data.Length - _position;

        public byte Byte() => Take(1)[0];

        public ushort UInt16() => BinaryPrimitives.ReadUInt16BigEndian(Take(2));

        public uint UInt32() => BinaryPrimitives.ReadUInt32BigEndian(Take(4));

        public string CountedString() => Encoding.UTF8.GetString(Take(UInt16()));

        public void Skip(int count) => Take(count);

        private ReadOnlySpan<byte> Take(int count)
        {
            if (count < 0 || _position + count > _data.Length)
            {
                throw new InvalidDataException("Truncated keytab entry.");
            }

            var slice = _data.Slice(_position, count);
            _position += count;
            return slice;
        }
    }
}
