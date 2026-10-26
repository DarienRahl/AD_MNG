using System.Globalization;
using AdMng.Core.GroupPolicy;

namespace AdMng.Watchdog.Core.Gpo.Parsers;

/// <summary>The fields of <c>GPT.INI</c> the service uses.</summary>
public sealed record GptIni(int? Version, string? DisplayName);

/// <summary>
/// <c>GPT.INI</c> at the root of the GPO folder ([MS-GPOL] 2.2.4): <c>[General]</c> with <c>Version=&lt;decimal&gt;</c> –
/// computer version in the low 16 bits, user version in the high 16 bits – and optionally <c>displayName</c>. ANSI or
/// UTF-8. The version fields are not settings (8.5).
/// </summary>
public static class GptIniParser
{
    public static GptIni Parse(ReadOnlySpan<byte> content)
    {
        var text = GpoText.DecodeAnsiOrUtf8(content, allowAnsi: true);
        int? version = null;
        string? displayName = null;
        foreach (var line in IniReader.Read(text))
        {
            if (!line.Section.Equals("General", StringComparison.OrdinalIgnoreCase) || line.Value is null)
            {
                continue;
            }

            if (line.Key.Equals("Version", StringComparison.OrdinalIgnoreCase))
            {
                // versionNumber is a signed 32-bit attribute; tools write it unsigned or signed.
                if (!long.TryParse(line.Value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var raw)
                    || raw < int.MinValue || raw > uint.MaxValue)
                {
                    throw new GpoParseException("GPT.INI has an invalid Version.");
                }

                version = unchecked((int)raw);
            }
            else if (line.Key.Equals("displayName", StringComparison.OrdinalIgnoreCase))
            {
                displayName = GpoValues.Cap(line.Value).Value;
            }
        }

        return new GptIni(version, displayName);
    }
}
