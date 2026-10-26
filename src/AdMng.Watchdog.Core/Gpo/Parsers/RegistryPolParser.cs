using System.Buffers.Binary;
using System.Text;

namespace AdMng.Watchdog.Core.Gpo.Parsers;

/// <summary>
/// <c>Machine/Registry.pol</c> and <c>User/Registry.pol</c> ([MS-GPREG] 2.2.1, "Registry Policy File Format"): the header
/// <c>PReg</c> (0x67655250, little endian) and version 1, then entries <c>[key;value;type;size;data]</c> whose brackets and
/// semicolons are UTF-16LE characters, key and value NUL-terminated UTF-16LE strings, type and size 32-bit little endian
/// and data <c>size</c> bytes. Special value names ([MS-GPREG] 2.2.1.1–2.2.1.2): <c>**del.&lt;name&gt;</c> deletes a value,
/// <c>**delvals.</c> every value of the key, <c>**DeleteValues</c> / <c>**DeleteKeys</c> the <c>;</c>-separated values / subkeys
/// of the data, <c>**soft.&lt;name&gt;</c> sets a value only when it does not exist; an entry with an empty value name, type
/// REG_NONE and no data creates the key.
/// Keys: <c>Computer/Registry/HKLM\&lt;key&gt;!&lt;value&gt;</c> (user part: <c>User/Registry/HKCU\…</c>), values
/// <c>REG_DWORD:1</c>, <c>REG_SZ:text</c>, <c>REG_MULTI_SZ:"a","b"</c>, <c>REG_BINARY:0a1b…</c>, <c>DELETE</c>,
/// <c>DELETE_ALL_VALUES</c>, <c>DELETE_KEY</c>, <c>CREATE_KEY</c>, <c>SOFT:…</c>. Later entries win (the client applies them
/// in order). Any structural error fails the whole file.
/// </summary>
public static class RegistryPolParser
{
    /// <summary>"PReg" read as a little-endian 32-bit integer.</summary>
    public const uint Signature = 0x67655250;

    public const uint Version = 1;

    /// <summary>Longest key or value name accepted (registry limits: key path 32 767 characters, value name 16 383).</summary>
    private const int MaxStringChars = 32_767;

    public static void Parse(ReadOnlySpan<byte> content, GpoScope scope, GpoSettingSink sink)
    {
        ArgumentNullException.ThrowIfNull(sink);
        if (content.Length == 0)
        {
            return; // a policy file without entries; GPMC writes the header, but an empty file applies nothing either
        }

        if (content.Length < 8 || BinaryPrimitives.ReadUInt32LittleEndian(content) != Signature)
        {
            throw new GpoParseException("Registry.pol has no PReg signature.");
        }

        if (BinaryPrimitives.ReadUInt32LittleEndian(content[4..]) != Version)
        {
            throw new GpoParseException("Registry.pol has an unsupported version.");
        }

        var (prefix, hive) = scope == GpoScope.User ? ("User/Registry/", "HKCU") : ("Computer/Registry/", "HKLM");
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

            var data = content.Slice(position, (int)size);
            position += (int)size;
            Expect(content, ref position, ']');
            Entry(sink, prefix, hive + (key.Length == 0 ? string.Empty : "\\" + key.Trim('\\')), valueName, type, data);
        }
    }

    private static void Entry(GpoSettingSink sink, string prefix, string keyPath, string valueName, uint type, ReadOnlySpan<byte> data)
    {
        var category = GpoSettingCategories.ForRegistryKey(keyPath);
        var hideNames = GpoSecretSettings.HidesValueNames(keyPath);
        string ValueKey(string name) => prefix + keyPath + "!" + (hideNames ? "*" : name.Length == 0 ? "(Default)" : name);

        if (valueName.StartsWith("**del.", StringComparison.OrdinalIgnoreCase))
        {
            Set(ValueKey(valueName[6..]), "DELETE", masked: hideNames);
        }
        else if (valueName.Equals("**delvals.", StringComparison.OrdinalIgnoreCase))
        {
            Set(prefix + keyPath + "!**delvals.", "DELETE_ALL_VALUES", masked: false);
        }
        else if (valueName.Equals("**DeleteValues", StringComparison.OrdinalIgnoreCase))
        {
            foreach (var name in SemicolonList(data))
            {
                Set(ValueKey(name), "DELETE", masked: hideNames);
            }
        }
        else if (valueName.Equals("**DeleteKeys", StringComparison.OrdinalIgnoreCase))
        {
            foreach (var subkey in SemicolonList(data))
            {
                var path = keyPath + "\\" + subkey.Trim('\\');
                Set(prefix + path, "DELETE_KEY", masked: false, GpoSettingCategories.ForRegistryKey(path));
            }
        }
        else if (valueName.StartsWith("**soft.", StringComparison.OrdinalIgnoreCase))
        {
            var name = valueName[7..];
            Set(ValueKey(name), "SOFT:" + Typed(type, data), masked: GpoSecretSettings.IsSecretRegistryValue(keyPath, name));
        }
        else if (valueName.Length == 0 && type == RegistryFormat.RegNone && data.IsEmpty)
        {
            Set(prefix + keyPath, "CREATE_KEY", masked: false);
        }
        else
        {
            Set(ValueKey(valueName), Typed(type, data), masked: GpoSecretSettings.IsSecretRegistryValue(keyPath, valueName));
        }

        void Set(string key, string value, bool masked, GpoSettingCategory? keyCategory = null)
        {
            if (masked)
            {
                sink.Masked(key, keyCategory ?? category);
            }
            else
            {
                sink.Text(key, value, keyCategory ?? category);
            }
        }
    }

    /// <summary>Canonical typed data.</summary>
    internal static string Typed(uint type, ReadOnlySpan<byte> data)
    {
        var name = RegistryFormat.TypeName(type);
        switch (type)
        {
            case RegistryFormat.RegSz or RegistryFormat.RegExpandSz or RegistryFormat.RegLink:
                return name + ":" + Utf16(data).Split('\0')[0];
            case RegistryFormat.RegMultiSz:
                var items = Utf16(data).Split('\0').ToList();
                while (items.Count > 0 && items[^1].Length == 0)
                {
                    items.RemoveAt(items.Count - 1);
                }

                return name + ":" + string.Join(",", items.Select(GpoValues.Quote));
            case RegistryFormat.RegDword when data.Length == 4:
                return name + ":" + GpoValues.Number(BinaryPrimitives.ReadUInt32LittleEndian(data));
            case RegistryFormat.RegDwordBigEndian when data.Length == 4:
                return name + ":" + GpoValues.Number(BinaryPrimitives.ReadUInt32BigEndian(data));
            case RegistryFormat.RegQword when data.Length == 8:
                return name + ":" + BinaryPrimitives.ReadUInt64LittleEndian(data).ToString(System.Globalization.CultureInfo.InvariantCulture);
            default:
                return name + ":" + Convert.ToHexStringLower(data);
        }
    }

    private static string Utf16(ReadOnlySpan<byte> data) => Encoding.Unicode.GetString(data[..(data.Length & ~1)]);

    private static IEnumerable<string> SemicolonList(ReadOnlySpan<byte> data) =>
        Utf16(data).Split('\0')[0].Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

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
