using System.Net.Mail;

namespace AdMng.Watchdog.Core.Rules.Validation;

/// <summary>
/// <c>invalidAddress</c> (8.3): an address (optionally with a display name, <c>Nadzór AD &lt;nadzor@contoso.com&gt;</c>)
/// parses with the <see cref="MailAddress"/> rules and contains no control characters (CR/LF would inject headers).
/// </summary>
public static class EmailAddressRules
{
    /// <summary>True when <paramref name="address"/> is a single valid mailbox.</summary>
    public static bool IsValid(string? address)
    {
        if (string.IsNullOrWhiteSpace(address) || address.Any(char.IsControl))
        {
            return false;
        }

        // A list of addresses is not one mailbox: a comma is allowed only inside a quoted display name.
        var trimmed = address.Trim();
        return !HasUnquotedComma(trimmed)
            && MailAddress.TryCreate(trimmed, out var parsed)
            && parsed.Address.Contains('@', StringComparison.Ordinal)
            && !parsed.Address.Any(char.IsWhiteSpace);
    }

    private static bool HasUnquotedComma(string value)
    {
        var quoted = false;
        for (var i = 0; i < value.Length; i++)
        {
            switch (value[i])
            {
                case '\\' when quoted:
                    i++;
                    break;
                case '"':
                    quoted = !quoted;
                    break;
                case ',' when !quoted:
                    return true;
            }
        }

        return false;
    }
}
