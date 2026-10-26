using System.Globalization;

namespace AdMng.Core.Security;

/// <summary>
/// A managed reader of the DACL of an SDDL string (MS-DTYP 2.5.1), as the schema stores it in a class's
/// <c>defaultSecurityDescriptor</c>: ACE types, flags, rights (letters or hex), object GUIDs and SIDs (S-1-… or the
/// two-letter aliases, domain-relative ones with the domain or forest root SID). Pure code, no Windows API, never throws.
/// </summary>
public static class Sddl
{
    private static readonly Dictionary<string, string> FixedAliases = new(StringComparer.Ordinal)
    {
        ["AA"] = "S-1-5-32-579",
        ["AC"] = "S-1-15-2-1",
        ["AN"] = "S-1-5-7",
        ["AO"] = "S-1-5-32-548",
        ["AU"] = "S-1-5-11",
        ["BA"] = "S-1-5-32-544",
        ["BG"] = "S-1-5-32-546",
        ["BO"] = "S-1-5-32-551",
        ["BU"] = "S-1-5-32-545",
        ["CD"] = "S-1-5-32-574",
        ["CG"] = "S-1-3-1",
        ["CO"] = "S-1-3-0",
        ["CY"] = "S-1-5-32-569",
        ["ED"] = "S-1-5-9",
        ["ER"] = "S-1-5-32-573",
        ["ES"] = "S-1-5-32-576",
        ["HA"] = "S-1-5-32-578",
        ["IS"] = "S-1-5-32-568",
        ["IU"] = "S-1-5-4",
        ["LS"] = "S-1-5-19",
        ["LU"] = "S-1-5-32-559",
        ["MS"] = "S-1-5-32-577",
        ["MU"] = "S-1-5-32-558",
        ["NO"] = "S-1-5-32-556",
        ["NS"] = "S-1-5-20",
        ["NU"] = "S-1-5-2",
        ["OW"] = "S-1-3-4",
        ["PO"] = "S-1-5-32-550",
        ["PS"] = "S-1-5-10",
        ["PU"] = "S-1-5-32-547",
        ["RA"] = "S-1-5-32-575",
        ["RC"] = "S-1-5-12",
        ["RD"] = "S-1-5-32-555",
        ["RE"] = "S-1-5-32-552",
        ["RM"] = "S-1-5-32-580",
        ["RU"] = "S-1-5-32-554",
        ["SO"] = "S-1-5-32-549",
        ["SU"] = "S-1-5-6",
        ["SY"] = "S-1-5-18",
        ["WD"] = "S-1-1-0",
        ["WR"] = "S-1-5-33",
    };

    /// <summary>Aliases relative to the domain SID (RID).</summary>
    private static readonly Dictionary<string, int> DomainAliases = new(StringComparer.Ordinal)
    {
        ["AP"] = 525,
        ["CA"] = 517,
        ["CN"] = 522,
        ["DA"] = 512,
        ["DC"] = 515,
        ["DD"] = 516,
        ["DG"] = 514,
        ["DU"] = 513,
        ["KA"] = 526,
        ["LA"] = 500,
        ["LG"] = 501,
        ["PA"] = 520,
        ["RS"] = 553,
    };

    /// <summary>Aliases relative to the forest root domain SID (RID).</summary>
    private static readonly Dictionary<string, int> RootAliases = new(StringComparer.Ordinal)
    {
        ["EA"] = 519,
        ["EK"] = 527,
        ["RO"] = 498,
        ["SA"] = 518,
    };

    private static readonly Dictionary<string, int> Rights = new(StringComparer.Ordinal)
    {
        ["GA"] = DirectoryAccessMask.GenericAll,
        ["GR"] = DirectoryAccessMask.GenericRead,
        ["GW"] = DirectoryAccessMask.GenericWrite,
        ["GX"] = DirectoryAccessMask.GenericExecute,
        ["RC"] = DirectoryAccessMask.ReadControl,
        ["SD"] = DirectoryAccessMask.Delete,
        ["WD"] = DirectoryAccessMask.WriteDacl,
        ["WO"] = DirectoryAccessMask.WriteOwner,
        ["RP"] = DirectoryAccessMask.ReadProperty,
        ["WP"] = DirectoryAccessMask.WriteProperty,
        ["CC"] = DirectoryAccessMask.CreateChild,
        ["DC"] = DirectoryAccessMask.DeleteChild,
        ["LC"] = DirectoryAccessMask.ListChildren,
        ["SW"] = DirectoryAccessMask.Self,
        ["LO"] = DirectoryAccessMask.ListObject,
        ["DT"] = DirectoryAccessMask.DeleteTree,
        ["CR"] = DirectoryAccessMask.ControlAccess,
        ["FA"] = 0x001F_01FF,
        ["FR"] = 0x0012_0089,
        ["FW"] = 0x0012_0116,
        ["FX"] = 0x0012_00A0,
        ["KA"] = 0x000F_003F,
        ["KR"] = 0x0002_0019,
        ["KW"] = 0x0002_0006,
        ["KX"] = 0x0002_0019,
    };

    private static readonly Dictionary<string, byte> AceTypes = new(StringComparer.Ordinal)
    {
        ["A"] = 0x00,
        ["D"] = 0x01,
        ["AU"] = 0x02,
        ["AL"] = 0x03,
        ["OA"] = 0x05,
        ["OD"] = 0x06,
        ["OU"] = 0x07,
        ["OL"] = 0x08,
        ["XA"] = 0x09,
        ["XD"] = 0x0A,
        ["ZA"] = 0x0B,
        ["XU"] = 0x0D,
        ["ML"] = 0x11,
        ["RA"] = 0x12,
        ["SP"] = 0x13,
    };

    private static readonly Dictionary<string, byte> AceFlags = new(StringComparer.Ordinal)
    {
        ["OI"] = SecurityDescriptorAce.ObjectInheritFlag,
        ["CI"] = SecurityDescriptorAce.ContainerInheritFlag,
        ["NP"] = SecurityDescriptorAce.NoPropagateInheritFlag,
        ["IO"] = SecurityDescriptorAce.InheritOnlyFlag,
        ["ID"] = SecurityDescriptorAce.InheritedFlag,
        ["SA"] = 0x40,
        ["FA"] = 0x80,
    };

    /// <summary>
    /// The ACEs of the DACL (<c>D:</c>) of <paramref name="sddl"/>. ACEs that cannot be read - a malformed part, an
    /// unknown alias, a domain-relative alias without the SID it needs - are skipped; null or empty input gives none.
    /// </summary>
    /// <param name="sddl">The SDDL string (e.g. a defaultSecurityDescriptor).</param>
    /// <param name="domainSid">SID of the domain (DA, DU, KA … resolve to it).</param>
    /// <param name="rootDomainSid">SID of the forest root domain (EA, SA, EK, RO); null = <paramref name="domainSid"/>.</param>
    public static IReadOnlyList<SecurityDescriptorAce> ParseDacl(string? sddl, string? domainSid, string? rootDomainSid = null)
    {
        var aces = new List<SecurityDescriptorAce>();
        if (string.IsNullOrWhiteSpace(sddl) || FindComponent(sddl, 'D') is not { } start)
        {
            return aces;
        }

        var position = start;
        while (position < sddl.Length && sddl[position] != '(' && !IsComponentStart(sddl, position))
        {
            position++; // DACL flags: P, AI, AR, NO_ACCESS_CONTROL
        }

        while (position < sddl.Length && sddl[position] == '(')
        {
            var close = sddl.IndexOf(')', position + 1);
            if (close < 0)
            {
                break;
            }

            if (ParseAce(sddl[(position + 1)..close], domainSid, rootDomainSid ?? domainSid) is { } ace)
            {
                aces.Add(ace);
            }

            position = close + 1;
        }

        return aces;
    }

    /// <summary>The SID of an alias (<c>DA</c>, <c>BA</c> …) or of an <c>S-1-…</c> string; null when it cannot be resolved.</summary>
    public static string? ResolveSid(string? token, string? domainSid, string? rootDomainSid = null)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return null;
        }

        token = token.Trim();
        if (token.StartsWith("S-", StringComparison.OrdinalIgnoreCase))
        {
            return "S-" + token[2..];
        }

        if (FixedAliases.TryGetValue(token, out var sid))
        {
            return sid;
        }

        if (DomainAliases.TryGetValue(token, out var rid))
        {
            return string.IsNullOrEmpty(domainSid) ? null : string.Create(CultureInfo.InvariantCulture, $"{domainSid}-{rid}");
        }

        var root = rootDomainSid ?? domainSid;
        return RootAliases.TryGetValue(token, out rid) && !string.IsNullOrEmpty(root)
            ? string.Create(CultureInfo.InvariantCulture, $"{root}-{rid}")
            : null;
    }

    private static SecurityDescriptorAce? ParseAce(string body, string? domainSid, string? rootDomainSid)
    {
        var parts = body.Split(';');
        if (parts.Length < 6 || !AceTypes.TryGetValue(parts[0].Trim(), out var type))
        {
            return null;
        }

        if (!TryParseFlags(parts[1].Trim(), out var flags) || !TryParseRights(parts[2].Trim(), out var mask))
        {
            return null;
        }

        Guid? objectType = null;
        Guid? inheritedObjectType = null;
        if (parts[3].Trim() is { Length: > 0 } objectText)
        {
            if (!Guid.TryParse(objectText, out var parsed))
            {
                return null;
            }

            objectType = parsed;
        }

        if (parts[4].Trim() is { Length: > 0 } inheritedText)
        {
            if (!Guid.TryParse(inheritedText, out var parsed))
            {
                return null;
            }

            inheritedObjectType = parsed;
        }

        return ResolveSid(parts[5], domainSid, rootDomainSid) is { } sid
            ? new SecurityDescriptorAce
            {
                Type = type,
                Flags = flags,
                Mask = mask,
                Sid = sid,
                ObjectType = objectType,
                InheritedObjectType = inheritedObjectType,
            }
            : null;
    }

    private static bool TryParseFlags(string text, out byte flags)
    {
        flags = 0;
        if (text.Length % 2 != 0)
        {
            return false;
        }

        for (var i = 0; i < text.Length; i += 2)
        {
            if (!AceFlags.TryGetValue(text.Substring(i, 2), out var flag))
            {
                return false;
            }

            flags |= flag;
        }

        return true;
    }

    private static bool TryParseRights(string text, out int mask)
    {
        mask = 0;
        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            if (!uint.TryParse(text.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var hex))
            {
                return false;
            }

            mask = unchecked((int)hex);
            return true;
        }

        if (text.Length % 2 != 0)
        {
            return false;
        }

        for (var i = 0; i < text.Length; i += 2)
        {
            if (!Rights.TryGetValue(text.Substring(i, 2), out var right))
            {
                return false;
            }

            mask |= right;
        }

        return true;
    }

    /// <summary>The position after <c>X:</c> of a top-level component (O, G, D, S), or null.</summary>
    private static int? FindComponent(string sddl, char component)
    {
        var position = 0;
        while (position + 1 < sddl.Length)
        {
            if (!IsComponentStart(sddl, position))
            {
                return null;
            }

            var current = sddl[position];
            position += 2;
            if (current == component)
            {
                return position;
            }

            if (current is 'O' or 'G')
            {
                position = SkipSid(sddl, position);
            }
            else
            {
                // D: or S: - flags and ACEs up to the next component.
                while (position < sddl.Length && !IsComponentStart(sddl, position))
                {
                    if (sddl[position] == '(')
                    {
                        var close = sddl.IndexOf(')', position + 1);
                        position = close < 0 ? sddl.Length : close + 1;
                    }
                    else
                    {
                        position++;
                    }
                }
            }
        }

        return null;
    }

    private static bool IsComponentStart(string sddl, int position) =>
        position + 1 < sddl.Length && sddl[position + 1] == ':' && sddl[position] is 'O' or 'G' or 'D' or 'S';

    /// <summary>Skips an owner/group SID: <c>S-1-…</c> (digits and dashes) or a two-letter alias.</summary>
    private static int SkipSid(string sddl, int position)
    {
        if (position + 1 < sddl.Length && sddl[position] == 'S' && sddl[position + 1] == '-')
        {
            position += 2;
            while (position < sddl.Length && (char.IsAsciiDigit(sddl[position]) || sddl[position] == '-'))
            {
                position++;
            }

            return position;
        }

        return Math.Min(position + 2, sddl.Length);
    }
}
