using AdMng.Core.ActiveDirectory;
using AdMng.Core.Ldap;

namespace AdMng.PowerShell.Reading;

/// <summary>What an <c>-Identity</c> text names.</summary>
internal enum AdmIdentityKind
{
    /// <summary>A distinguished name: read as a base search of that object.</summary>
    DistinguishedName,

    /// <summary>An <c>objectGUID</c> (any text form of a GUID, or <c>&lt;GUID=...&gt;</c>).</summary>
    Guid,

    /// <summary>An <c>objectSid</c> (<c>S-1-5-21-...</c>, or <c>&lt;SID=...&gt;</c>).</summary>
    Sid,

    /// <summary>A <c>sAMAccountName</c> or a user principal name.</summary>
    Account,
}

/// <summary>An <c>-Identity</c> value, classified: a DN, a GUID, a SID or an account name (docs/dev/r1-powershell-window.md 12.2).</summary>
internal sealed record AdmIdentity(string Text, AdmIdentityKind Kind, string Value, byte[]? SidBytes = null, Guid? ObjectGuid = null)
{
    /// <summary>Classifies <paramref name="text"/>; null for an empty text.</summary>
    public static AdmIdentity? Parse(string? text)
    {
        var trimmed = text?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            return null;
        }

        // The extended forms <GUID=...> and <SID=...> (what the extended DN control and Get-ADObject output use).
        if (trimmed.Length > 2 && trimmed[0] == '<' && trimmed[^1] == '>')
        {
            var inner = trimmed[1..^1];
            if (inner.StartsWith("GUID=", StringComparison.OrdinalIgnoreCase) && Guid.TryParse(inner[5..], out var extendedGuid))
            {
                return new AdmIdentity(trimmed, AdmIdentityKind.Guid, extendedGuid.ToString("D"), ObjectGuid: extendedGuid);
            }

            if (inner.StartsWith("SID=", StringComparison.OrdinalIgnoreCase) && TrySid(inner[4..], out var extendedSid))
            {
                return new AdmIdentity(trimmed, AdmIdentityKind.Sid, inner[4..], extendedSid);
            }
        }

        if (Guid.TryParse(trimmed, out var guid))
        {
            return new AdmIdentity(trimmed, AdmIdentityKind.Guid, guid.ToString("D"), ObjectGuid: guid);
        }

        if (trimmed.StartsWith("S-1-", StringComparison.OrdinalIgnoreCase) && TrySid(trimmed, out var sid))
        {
            return new AdmIdentity(trimmed, AdmIdentityKind.Sid, trimmed, sid);
        }

        // A sAMAccountName cannot contain '=': a text with one that parses is a DN.
        if (trimmed.Contains('=', StringComparison.Ordinal) && DistinguishedName.TryParse(trimmed, out var dn) && !dn.IsRoot)
        {
            return new AdmIdentity(trimmed, AdmIdentityKind.DistinguishedName, trimmed);
        }

        return new AdmIdentity(trimmed, AdmIdentityKind.Account, trimmed);
    }

    private static bool TrySid(string text, out byte[] bytes)
    {
        try
        {
            bytes = AdConvert.SidToBytes(text);
            return bytes.Length >= 8;
        }
        catch (Exception ex) when (ex is FormatException or OverflowException or ArgumentException)
        {
            bytes = [];
            return false;
        }
    }
}
