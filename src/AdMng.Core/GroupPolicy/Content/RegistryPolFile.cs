using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace AdMng.Core.GroupPolicy.Content;

/// <summary>One entry of a Registry.pol file ([MS-GPREG] 2.2.1): key, value name, type and data, as stored.</summary>
/// <param name="Key">The key below HKLM / HKCU, as written (no hive).</param>
/// <param name="ValueName">The value name, including the special prefixes (<c>**del.</c>, <c>**delvals.</c> …).</param>
/// <param name="Type">The registry type (<see cref="RegistryFormat"/>).</param>
/// <param name="Data">The data bytes.</param>
public sealed record RegistryPolEntry(string Key, string ValueName, uint Type, byte[] Data)
{
    /// <summary>The data of a deletion entry as the Microsoft editor writes it: REG_SZ " " (a space and the terminator).</summary>
    public static byte[] DeletionData => [0x20, 0x00, 0x00, 0x00];

    public bool SameData(RegistryPolEntry other) => other is not null && Type == other.Type && Data.AsSpan().SequenceEqual(other.Data);

    /// <summary>Never the data (directory content stays out of logs).</summary>
    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"{Key};{ValueName};{RegistryFormat.TypeName(Type)};{Data.Length} B");
}

/// <summary>
/// Reads and writes the entries of Registry.pol ([MS-GPREG] 2.2.1, "Registry Policy File Format"): the header <c>PReg</c>
/// and version 1, then <c>[key;value;type;size;data]</c> with UTF-16LE brackets and semicolons, NUL-terminated UTF-16LE
/// strings, 32-bit little-endian type and size. <see cref="RegistryPolParser"/> turns the entries into normalized settings;
/// the administrative templates editor edits them (K3). Any structural error fails the whole file.
/// </summary>
public static class RegistryPolFile
{
    /// <summary>"PReg" read as a little-endian 32-bit integer.</summary>
    public const uint Signature = 0x67655250;

    public const uint Version = 1;

    /// <summary>Longest key or value name accepted (registry limits: key path 32 767 characters, value name 16 383).</summary>
    public const int MaxStringChars = 32_767;

    /// <summary>The 8 header bytes: an empty policy file (what the Microsoft editor leaves when nothing is configured).</summary>
    public static byte[] Empty => Write([]);

    /// <summary>The entries in file order; an empty file (0 bytes) has none.</summary>
    /// <exception cref="GpoParseException">The file is malformed.</exception>
    public static IReadOnlyList<RegistryPolEntry> Read(ReadOnlySpan<byte> content)
    {
        var entries = new List<RegistryPolEntry>();
        if (content.Length == 0)
        {
            return entries; // a policy file without entries; GPMC writes the header, but an empty file applies nothing either
        }

        if (content.Length < 8 || BinaryPrimitives.ReadUInt32LittleEndian(content) != Signature)
        {
            throw new GpoParseException("Registry.pol has no PReg signature.");
        }

        if (BinaryPrimitives.ReadUInt32LittleEndian(content[4..]) != Version)
        {
            throw new GpoParseException("Registry.pol has an unsupported version.");
        }

        var position = 8;
        while (position < content.Length)
        {
            Expect(content, ref position, '[');
            var key = ReadString(content, ref position);
            Expect(content, ref position, ';');
            var valueName = ReadString(content, ref position);
            Expect(content, ref position, ';');
            var type = ReadUInt32(content, ref position);
            Expect(content, ref position, ';');
            var size = ReadUInt32(content, ref position);
            Expect(content, ref position, ';');
            if (size > (uint)(content.Length - position))
            {
                throw new GpoParseException("Registry.pol entry data exceeds the file.");
            }

            var data = content.Slice(position, (int)size).ToArray();
            position += (int)size;
            Expect(content, ref position, ']');
            entries.Add(new RegistryPolEntry(key, valueName, type, data));
        }

        return entries;
    }

    /// <summary>The file of <paramref name="entries"/>: header, then each entry in order.</summary>
    public static byte[] Write(IEnumerable<RegistryPolEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        using var stream = new MemoryStream();
        Span<byte> number = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(number, Signature);
        stream.Write(number);
        BinaryPrimitives.WriteUInt32LittleEndian(number, Version);
        stream.Write(number);
        foreach (var entry in entries)
        {
            ArgumentNullException.ThrowIfNull(entry);
            if (entry.Key.Length > MaxStringChars || entry.ValueName.Length > MaxStringChars || entry.Key.Contains('\0', StringComparison.Ordinal)
                || entry.ValueName.Contains('\0', StringComparison.Ordinal))
            {
                throw new ArgumentException("A key or value name of a Registry.pol entry is not valid.", nameof(entries));
            }

            Char(stream, '[');
            Text(stream, entry.Key);
            Char(stream, ';');
            Text(stream, entry.ValueName);
            Char(stream, ';');
            BinaryPrimitives.WriteUInt32LittleEndian(number, entry.Type);
            stream.Write(number);
            Char(stream, ';');
            BinaryPrimitives.WriteUInt32LittleEndian(number, (uint)entry.Data.Length);
            stream.Write(number);
            Char(stream, ';');
            stream.Write(entry.Data);
            Char(stream, ']');
        }

        return stream.ToArray();
    }

    /// <summary>REG_SZ / REG_EXPAND_SZ data: UTF-16LE with the terminator.</summary>
    public static byte[] StringData(string value) => Encoding.Unicode.GetBytes((value ?? string.Empty) + "\0");

    /// <summary>REG_MULTI_SZ data: each string terminated, then one more terminator.</summary>
    public static byte[] MultiStringData(IEnumerable<string> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        var builder = new StringBuilder();
        foreach (var value in values)
        {
            builder.Append(value).Append('\0');
        }

        return Encoding.Unicode.GetBytes(builder.Append('\0').ToString());
    }

    public static byte[] DwordData(uint value)
    {
        var data = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(data, value);
        return data;
    }

    public static byte[] QwordData(ulong value)
    {
        var data = new byte[8];
        BinaryPrimitives.WriteUInt64LittleEndian(data, value);
        return data;
    }

    /// <summary>The first string of REG_SZ / REG_EXPAND_SZ data (up to the terminator).</summary>
    public static string ReadString(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        return Encoding.Unicode.GetString(data, 0, data.Length & ~1).Split('\0')[0];
    }

    /// <summary>The strings of REG_MULTI_SZ data (the final empty ones dropped).</summary>
    public static IReadOnlyList<string> ReadMultiString(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        var items = Encoding.Unicode.GetString(data, 0, data.Length & ~1).Split('\0').ToList();
        while (items.Count > 0 && items[^1].Length == 0)
        {
            items.RemoveAt(items.Count - 1);
        }

        return items;
    }

    private static void Char(Stream stream, char c)
    {
        Span<byte> bytes = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16LittleEndian(bytes, c);
        stream.Write(bytes);
    }

    private static void Text(Stream stream, string text)
    {
        stream.Write(Encoding.Unicode.GetBytes(text));
        Char(stream, '\0');
    }

    private static void Expect(ReadOnlySpan<byte> content, ref int position, char expected)
    {
        if (content.Length - position < 2 || BinaryPrimitives.ReadUInt16LittleEndian(content[position..]) != expected)
        {
            throw new GpoParseException("Registry.pol is malformed: expected '" + expected + "'.");
        }

        position += 2;
    }

    private static uint ReadUInt32(ReadOnlySpan<byte> content, ref int position)
    {
        if (content.Length - position < 4)
        {
            throw new GpoParseException("Registry.pol is truncated.");
        }

        var value = BinaryPrimitives.ReadUInt32LittleEndian(content[position..]);
        position += 4;
        return value;
    }

    /// <summary>A NUL-terminated UTF-16LE string (the terminator consumed).</summary>
    private static string ReadString(ReadOnlySpan<byte> content, ref int position)
    {
        var start = position;
        while (true)
        {
            if (content.Length - position < 2)
            {
                throw new GpoParseException("Registry.pol has an unterminated string.");
            }

            if (BinaryPrimitives.ReadUInt16LittleEndian(content[position..]) == 0)
            {
                break;
            }

            position += 2;
            if ((position - start) / 2 > MaxStringChars)
            {
                throw new GpoParseException("Registry.pol has an over-long string.");
            }
        }

        var text = Encoding.Unicode.GetString(content[start..position]);
        position += 2;
        return text;
    }
}
