using AdMng.Core.ActiveDirectory;
using AdMng.Core.Model;
using AdMng.Core.Schema;
using AdMng.Presentation.Localization;

namespace AdMng.Presentation.Shell;

/// <summary>What "Kopiuj jako…" (Copy as) puts on the clipboard for each selected object.</summary>
public enum CopyFormat
{
    /// <summary>mail – several objects are joined with "; " for Outlook's To field.</summary>
    Mail,

    /// <summary>userPrincipalName.</summary>
    UserPrincipalName,

    /// <summary>sAMAccountName (pre-Windows 2000 logon name).</summary>
    SamAccountName,

    /// <summary>NETBIOS\sAMAccountName.</summary>
    DomainLogin,

    /// <summary>The RDN value (name).</summary>
    Name,

    /// <summary>displayName.</summary>
    DisplayName,

    /// <summary>The distinguished name.</summary>
    DistinguishedName,

    /// <summary>Canonical path, e.g. contoso.test/Firma/IT/Jan Kowalski.</summary>
    CanonicalPath,

    /// <summary>objectGUID ("D" format).</summary>
    ObjectGuid,

    /// <summary>objectSid (S-1-5-21-...).</summary>
    ObjectSid,
}

/// <summary>
/// The values of <see cref="CopyFormat"/>: which attributes must be read, how a value is taken from an object and how
/// values of several objects are joined. None of the formats is a secret attribute; <see cref="IsAllowed"/> refuses one
/// a schema marks confidential (copying secrets needs the Attribute Editor's explicit reveal).
/// </summary>
public static class ObjectCopyFormats
{
    /// <summary>Separator of the e-mail addresses of several objects (Outlook's To field).</summary>
    public const string MailSeparator = "; ";

    /// <summary>Resource key of the format's title ("Adres e-mail").</summary>
    public static string LabelKey(CopyFormat format) => format switch
    {
        CopyFormat.Mail => L.Copy.Mail,
        CopyFormat.UserPrincipalName => L.Copy.Upn,
        CopyFormat.SamAccountName => L.Copy.Login,
        CopyFormat.DomainLogin => L.Copy.DomainLogin,
        CopyFormat.Name => L.Copy.Name,
        CopyFormat.DisplayName => L.Copy.DisplayName,
        CopyFormat.DistinguishedName => L.Copy.DistinguishedName,
        CopyFormat.CanonicalPath => L.Copy.CanonicalPath,
        CopyFormat.ObjectGuid => L.Copy.Guid,
        CopyFormat.ObjectSid => L.Copy.Sid,
        _ => throw new ArgumentOutOfRangeException(nameof(format), format, null),
    };

    /// <summary>
    /// Attributes to read for the format; empty when the value follows from the DN (name, DN, canonical path) and the
    /// list rows already have it.
    /// </summary>
    public static IReadOnlyList<string> Attributes(CopyFormat format) => format switch
    {
        CopyFormat.Mail => [Attr.Mail],
        CopyFormat.UserPrincipalName => [Attr.UserPrincipalName],
        CopyFormat.SamAccountName or CopyFormat.DomainLogin => [Attr.SamAccountName],
        CopyFormat.DisplayName => [Attr.DisplayName],
        CopyFormat.ObjectGuid => [Attr.ObjectGuid],
        CopyFormat.ObjectSid => [Attr.ObjectSid],
        _ => [],
    };

    /// <summary>Values of several objects: e-mail addresses on one line for Outlook, everything else one per line.</summary>
    public static string Separator(CopyFormat format) => format == CopyFormat.Mail ? MailSeparator : Environment.NewLine;

    /// <summary>False when an attribute the format reads is a secret in <paramref name="schema"/> (never copied this way).</summary>
    public static bool IsAllowed(CopyFormat format, ISchemaCatalog? schema) =>
        !Attributes(format).Any(a => SecretAttributes.IsSecret(a, schema));

    /// <summary>
    /// The value of <paramref name="format"/> for <paramref name="obj"/> (read with <see cref="Attributes"/>), or null
    /// when the object has none. <paramref name="netBiosName"/> qualifies <see cref="CopyFormat.DomainLogin"/>.
    /// </summary>
    public static string? ValueOf(DirectoryObject obj, CopyFormat format, string? netBiosName)
    {
        ArgumentNullException.ThrowIfNull(obj);
        var value = format switch
        {
            CopyFormat.Mail => obj.GetString(Attr.Mail),
            CopyFormat.UserPrincipalName => obj.GetString(Attr.UserPrincipalName),
            CopyFormat.SamAccountName => obj.GetString(Attr.SamAccountName),
            CopyFormat.DomainLogin => Qualify(netBiosName, obj.GetString(Attr.SamAccountName)),
            CopyFormat.Name => obj.Name,
            CopyFormat.DisplayName => obj.GetString(Attr.DisplayName),
            CopyFormat.DistinguishedName => obj.DistinguishedName,
            CopyFormat.CanonicalPath => obj.Dn.ToCanonicalName(),
            CopyFormat.ObjectGuid => obj.ObjectGuid?.ToString("D"),
            CopyFormat.ObjectSid => obj.ObjectSid,
            _ => null,
        };
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    /// <summary>NETBIOS\login, or null when either part is unknown.</summary>
    public static string? Qualify(string? netBiosName, string? login) =>
        string.IsNullOrWhiteSpace(netBiosName) || string.IsNullOrWhiteSpace(login) ? null : $@"{netBiosName.Trim()}\{login.Trim()}";
}
