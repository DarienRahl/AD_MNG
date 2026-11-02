using System.Globalization;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace AdMng.Core.GroupPolicy.AdministrativeTemplates;

/// <summary>
/// The signer of a template package as the update window shows it and as the installer check decides on it (K3,
/// "Zaktualizuj szablony"): the organization (<c>O</c>) of the signing certificate read from its parsed subject – never from a
/// formatted subject string – and compared exactly. Whether the certificate's chain ends in a Microsoft root is the host's
/// part (on Windows <c>WinVerifyTrust</c> and the Microsoft root chain policy); this decides on the certificate itself.
/// </summary>
public static class PackageSigner
{
    /// <summary>The organization of the certificates Microsoft signs its own software with.</summary>
    public const string MicrosoftOrganization = "Microsoft Corporation";

    /// <summary>Longest signer name shown.</summary>
    public const int MaxDisplayLength = 128;

    /// <summary>OID of the organization attribute (<c>O</c>).</summary>
    private const string OrganizationOid = "2.5.4.10";

    /// <summary>
    /// The usages of the certificates Microsoft signs other vendors' drivers with (Windows Hardware Compatibility Publisher:
    /// WHQL and attested WHQL): their subject says Microsoft Corporation too, but the code is not Microsoft's.
    /// </summary>
    private static readonly string[] ThirdPartyDriverUsages = ["1.3.6.1.4.1.311.10.3.5", "1.3.6.1.4.1.311.10.3.5.1"];

    /// <summary>
    /// The organization of a name: the value of its only <c>O</c> attribute. Null when it has none, more than one, or any
    /// multi-valued RDN (which could carry one) – so "O=Microsoft Corporation Fans", a quoted "O=Microsoft Corporation" inside
    /// a CN or OU, and a second O never pass for Microsoft.
    /// </summary>
    public static string? Organization(X500DistinguishedName name)
    {
        ArgumentNullException.ThrowIfNull(name);
        string? organization = null;
        try
        {
            foreach (var rdn in name.EnumerateRelativeDistinguishedNames())
            {
                if (rdn.HasMultipleElements)
                {
                    return null;
                }

                if (!string.Equals(rdn.GetSingleElementType().Value, OrganizationOid, StringComparison.Ordinal))
                {
                    continue;
                }

                if (organization is not null)
                {
                    return null;
                }

                organization = rdn.GetSingleElementValue();
                if (organization is null)
                {
                    return null;
                }
            }
        }
        catch (CryptographicException)
        {
            return null; // a damaged name
        }

        return organization;
    }

    /// <summary>The certificate's organization is exactly <see cref="MicrosoftOrganization"/> – true or not: the chain decides.</summary>
    public static bool ClaimsMicrosoft(X509Certificate2 certificate)
    {
        ArgumentNullException.ThrowIfNull(certificate);
        return string.Equals(Organization(certificate.SubjectName), MicrosoftOrganization, StringComparison.Ordinal);
    }

    /// <summary>
    /// The certificate is one Microsoft signs its own software with: organization exactly <see cref="MicrosoftOrganization"/>
    /// and none of the usages of the certificates Microsoft signs other vendors' drivers with. Its chain must end in a
    /// Microsoft root as well (checked by the host).
    /// </summary>
    public static bool IsMicrosoftPublisher(X509Certificate2 certificate)
    {
        if (!ClaimsMicrosoft(certificate))
        {
            return false;
        }

        try
        {
            return !certificate.Extensions.OfType<X509EnhancedKeyUsageExtension>()
                .Any(e => e.EnhancedKeyUsages.Cast<Oid>().Any(u => ThirdPartyDriverUsages.Contains(u.Value, StringComparer.Ordinal)));
        }
        catch (CryptographicException)
        {
            return false; // a usage extension that cannot be read
        }
    }

    /// <summary>
    /// The signer as shown: the organization, else the common name; control and formatting characters (such as bidirectional
    /// overrides) removed, at most <see cref="MaxDisplayLength"/> characters; null when nothing is left.
    /// </summary>
    public static string? DisplayName(X509Certificate2 certificate)
    {
        ArgumentNullException.ThrowIfNull(certificate);
        var name = Organization(certificate.SubjectName);
        if (string.IsNullOrWhiteSpace(name))
        {
            name = certificate.GetNameInfo(X509NameType.SimpleName, forIssuer: false);
        }

        return Printable(name);
    }

    /// <summary>The text without control and formatting characters, trimmed and shortened; null when nothing is left.</summary>
    internal static string? Printable(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return null;
        }

        var builder = new StringBuilder(text.Length);
        Span<char> utf16 = stackalloc char[2];
        foreach (var rune in text.EnumerateRunes())
        {
            if (Rune.GetUnicodeCategory(rune) is not (UnicodeCategory.Control or UnicodeCategory.Format or UnicodeCategory.LineSeparator
                or UnicodeCategory.ParagraphSeparator or UnicodeCategory.PrivateUse or UnicodeCategory.OtherNotAssigned))
            {
                builder.Append(utf16[..rune.EncodeToUtf16(utf16)]);
            }
        }

        var result = builder.ToString().Trim();
        if (result.Length > MaxDisplayLength)
        {
            var cut = char.IsHighSurrogate(result[MaxDisplayLength - 1]) ? MaxDisplayLength - 1 : MaxDisplayLength;
            result = result[..cut].TrimEnd() + "…";
        }

        return result.Length == 0 ? null : result;
    }
}
