using System.Globalization;
using System.Text;

namespace AdMng.Core.GroupPolicy;

/// <summary>
/// <c>gPCMachineExtensionNames</c> / <c>gPCUserExtensionNames</c> ([MS-GPOL] 2.2.4): <c>[{CSE}{tool}…][{CSE}{tool}…]</c> – each
/// client-side extension that has settings in the GPO, with the editor extensions ("tools") that wrote them. GPMC keeps the
/// pairs sorted by the extension's GUID and the tools of a pair sorted; a client runs only the extensions listed here.
/// </summary>
public static class GpoExtensionNames
{
    /// <summary>The Registry client-side extension (Registry.pol: administrative templates).</summary>
    public static readonly Guid Registry = new("35378EAC-683F-11D2-A89A-00C04FBBCFA2");

    /// <summary>The administrative templates editor extension of the computer configuration.</summary>
    public static readonly Guid AdministrativeTemplatesMachineTool = new("0F6B957D-509E-11D1-A7CC-0000F87571E3");

    /// <summary>The administrative templates editor extension of the user configuration.</summary>
    public static readonly Guid AdministrativeTemplatesUserTool = new("0F6B957E-509E-11D1-A7CC-0000F87571E3");

    /// <summary>The Security client-side extension (<c>GptTmpl.inf</c>).</summary>
    public static readonly Guid Security = new("827D319E-6EAC-11D2-A4EA-00C04F79F83A");

    /// <summary>The security settings editor extension of the computer configuration.</summary>
    public static readonly Guid SecurityTool = new("803E14A0-B4FB-11D0-A0D0-00A0C90F574B");

    /// <summary>The Audit Policy Configuration client-side extension (advanced audit policy, <c>audit.csv</c>).</summary>
    public static readonly Guid AuditPolicy = new("F3CCC681-B74C-4060-9F26-CD84525DCA2A");

    /// <summary>The advanced audit policy editor extension.</summary>
    public static readonly Guid AuditPolicyTool = new("0F3F3735-573D-9804-99E4-AB2A69BA5FD4");

    /// <summary>The Group Policy Preferences Local Users and Groups client-side extension.</summary>
    public static readonly Guid PreferencesLocalUsersAndGroups = new("17D89FEC-5C44-4972-B12D-241CAEF74509");

    /// <summary>The Local Users and Groups preference editor extension of the computer configuration.</summary>
    public static readonly Guid PreferencesLocalUsersAndGroupsTool = new("79F92669-4224-476C-9C5C-6EFB4D87DF4A");

    /// <summary>The "core" pseudo-extension under which GPMC lists every preference editor extension of a half.</summary>
    public static readonly Guid PreferencesCore = Guid.Empty;

    /// <summary>The pairs of a value in order; null when it is not in the <c>[{guid}{guid}…]…</c> form.</summary>
    public static IReadOnlyList<(Guid Extension, IReadOnlyList<Guid> Tools)>? Parse(string? value)
    {
        var result = new List<(Guid, IReadOnlyList<Guid>)>();
        var text = (value ?? string.Empty).Trim();
        var position = 0;
        while (position < text.Length)
        {
            if (text[position] != '[')
            {
                return null;
            }

            var close = text.IndexOf(']', position);
            if (close < 0)
            {
                return null;
            }

            var guids = new List<Guid>();
            var inner = text[(position + 1)..close];
            var at = 0;
            while (at < inner.Length)
            {
                var end = inner.IndexOf('}', at);
                if (inner[at] != '{' || end < 0 || !Guid.TryParse(inner.AsSpan(at + 1, end - at - 1), out var guid))
                {
                    return null;
                }

                guids.Add(guid);
                at = end + 1;
            }

            if (guids.Count == 0)
            {
                return null;
            }

            result.Add((guids[0], guids[1..]));
            position = close + 1;
        }

        return result;
    }

    /// <summary>The value of the pairs: merged per extension, extensions and tools sorted (upper-case GUIDs in braces).</summary>
    public static string Format(IEnumerable<(Guid Extension, IEnumerable<Guid> Tools)> pairs)
    {
        ArgumentNullException.ThrowIfNull(pairs);
        var merged = new Dictionary<Guid, SortedSet<string>>();
        foreach (var (extension, tools) in pairs)
        {
            if (!merged.TryGetValue(extension, out var set))
            {
                set = new SortedSet<string>(StringComparer.Ordinal);
                merged[extension] = set;
            }

            foreach (var tool in tools)
            {
                set.Add(Text(tool));
            }
        }

        var builder = new StringBuilder();
        foreach (var (extension, tools) in merged.OrderBy(p => Text(p.Key), StringComparer.Ordinal))
        {
            builder.Append('[').Append(Text(extension));
            foreach (var tool in tools)
            {
                builder.Append(tool);
            }

            builder.Append(']');
        }

        return builder.ToString();
    }

    /// <summary>
    /// <paramref name="value"/> with the pair added: unchanged (the same string) when it already lists the tool for the
    /// extension; otherwise the merged, sorted value. A value that cannot be parsed gets the pair appended.
    /// </summary>
    public static string With(string? value, Guid extension, Guid tool)
    {
        var parsed = Parse(value);
        if (parsed is null)
        {
            return (value ?? string.Empty) + "[" + Text(extension) + Text(tool) + "]";
        }

        if (parsed.Any(p => p.Extension == extension && p.Tools.Contains(tool)))
        {
            return value!;
        }

        return Format([.. parsed.Select(p => (p.Extension, (IEnumerable<Guid>)p.Tools)), (extension, [tool])]);
    }

    private static string Text(Guid guid) => guid.ToString("B", CultureInfo.InvariantCulture).ToUpperInvariant();
}
