using System.Globalization;

namespace AdMng.Watchdog.Core.Scans;

/// <summary>One failed Kerberos pre-authentication (4771) or NTLM credential validation (4776) – the fields the
/// password-spray detector needs, nothing else is kept.</summary>
/// <param name="EventCode">4771 or 4776.</param>
/// <param name="TargetUserName">The account the password was tried for, normalized (lower case, no domain part).</param>
/// <param name="IpAddress">4771 client address without the <c>::ffff:</c> prefix; null for 4776.</param>
/// <param name="Workstation">4776 workstation name (client-supplied); null for 4771.</param>
/// <param name="Status">The failure code as a number (0x18, 0xC000006A …).</param>
internal sealed record FailedLogon(int EventCode, string TargetUserName, string? IpAddress, string? Workstation, uint Status)
{
    /// <summary>Kerberos: 0x18 wrong password, 0x6 unknown principal; NTLM: 0xC000006A wrong password, 0xC0000064 unknown account.</summary>
    public bool IsBadPassword => EventCode switch
    {
        4771 => Status is 0x18 or 0x6,
        4776 => Status is 0xC000006A or 0xC0000064,
        _ => false,
    };

    /// <summary>The spray source: <c>ip:</c> + address (4771) or <c>ws:</c> + workstation (4776); null when unknown.</summary>
    public string? SourceKey => EventCode == 4771
        ? IpAddress is { Length: > 0 } ip ? "ip:" + ip : null
        : Workstation is { Length: > 0 } ws ? "ws:" + ws.ToUpperInvariant() : null;

    /// <summary>Parses "0x18" / "0xC000006A" (hex) status values.</summary>
    public static uint? ParseStatus(string? value)
    {
        var text = value?.Trim();
        if (string.IsNullOrEmpty(text))
        {
            return null;
        }

        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            text = text[2..];
        }

        return uint.TryParse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var status) ? status : null;
    }
}
