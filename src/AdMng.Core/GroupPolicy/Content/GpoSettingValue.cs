using System.Globalization;
using System.Text;

namespace AdMng.Core.GroupPolicy.Content;

/// <summary>What a registry setting value does on the client (<see cref="RegistryPolParser"/>, <c>[Registry Values]</c>).</summary>
public enum GpoRegistryAction
{
    /// <summary>Sets a typed value (<c>REG_DWORD:1</c>).</summary>
    Set,

    /// <summary>Sets a typed value only when it does not exist yet (<c>SOFT:REG_SZ:…</c>, <c>**soft.</c>).</summary>
    SetIfMissing,

    /// <summary>Deletes the value (<c>DELETE</c>, <c>**del.</c>).</summary>
    DeleteValue,

    /// <summary>Deletes every value of the key (<c>DELETE_ALL_VALUES</c>, <c>**delvals.</c>).</summary>
    DeleteAllValues,

    /// <summary>Deletes the key (<c>DELETE_KEY</c>, <c>**DeleteKeys</c>).</summary>
    DeleteKey,

    /// <summary>Creates the key (<c>CREATE_KEY</c>).</summary>
    CreateKey,
}

/// <summary>Reads the canonical setting values of the shared parsers (design note docs/dev/k-computers-and-gpo.md 20.1).</summary>
public static class GpoSettingValue
{
    /// <summary>Value of a Registry.pol <c>**del.</c> entry.</summary>
    public const string Delete = "DELETE";

    /// <summary>Value of a Registry.pol <c>**delvals.</c> entry.</summary>
    public const string DeleteAllValues = "DELETE_ALL_VALUES";

    /// <summary>Value of a deleted key (<c>**DeleteKeys</c>).</summary>
    public const string DeleteKey = "DELETE_KEY";

    /// <summary>Value of a created key.</summary>
    public const string CreateKey = "CREATE_KEY";

    /// <summary>Prefix of a value set only when missing (<c>**soft.</c>).</summary>
    public const string SoftPrefix = "SOFT:";

    /// <summary>
    /// A typed registry value (<c>REG_DWORD:1</c>, <c>REG_SZ:tekst</c>, <c>REG_MULTI_SZ:"a","b"</c>, <c>REG_BINARY:0a1b</c>,
    /// <c>SOFT:REG_SZ:…</c>): its type (4, 1, 7 …) and data – decimal for REG_DWORD/REG_QWORD, hex for binary data, and for
    /// REG_MULTI_SZ the unquoted strings joined by <see cref="GpoSetting.ListSeparator"/>. False for the actions without data
    /// (<c>DELETE</c>, <c>DELETE_ALL_VALUES</c>, <c>DELETE_KEY</c>, <c>CREATE_KEY</c> – see the overload with
    /// <see cref="GpoRegistryAction"/>), masked or hashed values and anything else.
    /// </summary>
    public static bool TryParseRegistry(string? value, out uint type, out string data)
    {
        if (TryParseRegistry(value, out var action, out type, out data) && action is GpoRegistryAction.Set or GpoRegistryAction.SetIfMissing)
        {
            return true;
        }

        type = RegistryFormat.RegNone;
        data = string.Empty;
        return false;
    }

    /// <summary>
    /// A registry setting value with its action: a typed value as <see cref="TryParseRegistry(string?, out uint, out string)"/>
    /// (<see cref="GpoRegistryAction.SetIfMissing"/> for <c>SOFT:</c>), or one of the actions without data (type
    /// <see cref="RegistryFormat.RegNone"/>, empty data). False when the value is none of these.
    /// </summary>
    public static bool TryParseRegistry(string? value, out GpoRegistryAction action, out uint type, out string data)
    {
        action = GpoRegistryAction.Set;
        type = RegistryFormat.RegNone;
        data = string.Empty;
        switch (value)
        {
            case null:
                return false;
            case Delete:
                action = GpoRegistryAction.DeleteValue;
                return true;
            case DeleteAllValues:
                action = GpoRegistryAction.DeleteAllValues;
                return true;
            case DeleteKey:
                action = GpoRegistryAction.DeleteKey;
                return true;
            case CreateKey:
                action = GpoRegistryAction.CreateKey;
                return true;
        }

        var text = value;
        if (text.StartsWith(SoftPrefix, StringComparison.Ordinal))
        {
            action = GpoRegistryAction.SetIfMissing;
            text = text[SoftPrefix.Length..];
        }

        var colon = text.IndexOf(':', StringComparison.Ordinal);
        if (colon <= 0 || !TryParseTypeName(text[..colon], out var parsedType))
        {
            action = GpoRegistryAction.Set;
            return false;
        }

        var raw = text[(colon + 1)..];
        if (parsedType == RegistryFormat.RegMultiSz)
        {
            if (!TryUnquoteList(raw, out var lines))
            {
                action = GpoRegistryAction.Set;
                return false;
            }

            raw = string.Join(GpoSetting.ListSeparator, lines);
        }

        type = parsedType;
        data = raw;
        return true;
    }

    /// <summary>The type of a name of <see cref="RegistryFormat.TypeName"/> (<c>REG_DWORD</c> → 4, <c>REG_TYPE_9</c> → 9).</summary>
    public static bool TryParseTypeName(string? name, out uint type)
    {
        type = RegistryFormat.RegNone;
        switch (name)
        {
            case "REG_NONE":
                return true;
            case "REG_SZ":
                type = RegistryFormat.RegSz;
                return true;
            case "REG_EXPAND_SZ":
                type = RegistryFormat.RegExpandSz;
                return true;
            case "REG_BINARY":
                type = RegistryFormat.RegBinary;
                return true;
            case "REG_DWORD":
                type = RegistryFormat.RegDword;
                return true;
            case "REG_DWORD_BIG_ENDIAN":
                type = RegistryFormat.RegDwordBigEndian;
                return true;
            case "REG_LINK":
                type = RegistryFormat.RegLink;
                return true;
            case "REG_MULTI_SZ":
                type = RegistryFormat.RegMultiSz;
                return true;
            case "REG_QWORD":
                type = RegistryFormat.RegQword;
                return true;
        }

        const string Other = "REG_TYPE_";
        return name is not null && name.StartsWith(Other, StringComparison.Ordinal)
            && uint.TryParse(name.AsSpan(Other.Length), NumberStyles.None, CultureInfo.InvariantCulture, out type);
    }

    /// <summary>The strings of a quoted list (<c>"a","b\"c"</c>, <see cref="GpoValues.Quote"/>); empty text is no string.</summary>
    public static bool TryUnquoteList(string? text, out IReadOnlyList<string> items)
    {
        var result = new List<string>();
        items = result;
        if (string.IsNullOrEmpty(text))
        {
            return text is not null;
        }

        var current = new StringBuilder();
        var i = 0;
        while (true)
        {
            if (text[i] != '"')
            {
                return false;
            }

            i++;
            var closed = false;
            while (i < text.Length)
            {
                var c = text[i];
                if (c == '\\' && i + 1 < text.Length)
                {
                    current.Append(text[i + 1]);
                    i += 2;
                }
                else if (c == '"')
                {
                    i++;
                    closed = true;
                    break;
                }
                else
                {
                    current.Append(c);
                    i++;
                }
            }

            if (!closed)
            {
                return false;
            }

            result.Add(current.ToString());
            current.Clear();
            if (i == text.Length)
            {
                return true;
            }

            if (text[i] != ',' || i + 1 == text.Length)
            {
                return false;
            }

            i++;
        }
    }
}
