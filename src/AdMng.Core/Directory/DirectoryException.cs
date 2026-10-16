using System.Globalization;
using System.Text.RegularExpressions;

namespace AdMng.Core.Directory;

/// <summary>Classification of directory errors used for user friendly, localized messages and retry policy.</summary>
public enum DirectoryErrorKind
{
    Unknown,
    InvalidCredentials,
    AccountRestriction,
    ServerUnavailable,
    Busy,
    Timeout,
    AccessDenied,
    NoSuchObject,
    AlreadyExists,
    ConstraintViolation,
    PasswordPolicy,
    ObjectClassViolation,
    NotAllowedOnNonLeaf,
    UnwillingToPerform,
    StrongAuthRequired,
    SizeLimitExceeded,
    UnsupportedControl,
    InvalidSyntax,
    NoSuchAttribute,
    AttributeOrValueExists,
    Cancelled,
    Referral,
}

/// <summary>An LDAP / Active Directory error with the information needed to explain it to the operator.</summary>
public sealed partial class DirectoryException : Exception
{
    public DirectoryException(DirectoryErrorKind kind, string message, Exception? innerException = null)
        : base(message, innerException)
    {
        Kind = kind;
    }

    public DirectoryException()
        : this(DirectoryErrorKind.Unknown, "Directory error.")
    {
    }

    public DirectoryException(string message)
        : this(DirectoryErrorKind.Unknown, message)
    {
    }

    public DirectoryException(string message, Exception innerException)
        : this(DirectoryErrorKind.Unknown, message, innerException)
    {
    }

    public DirectoryErrorKind Kind { get; }

    /// <summary>LDAP result code (RFC 4511), when the error came from a server response.</summary>
    public int? LdapResultCode { get; init; }

    /// <summary>Win32 error extracted from the AD diagnostic message (e.g. 0x52D password policy), when present.</summary>
    public int? Win32Error { get; init; }

    /// <summary>Raw diagnostic message from the server, e.g. "0000052D: SvcErr: DSID-031A1254, problem 5003 ...".</summary>
    public string? ServerMessage { get; init; }

    /// <summary>The object the operation targeted.</summary>
    public string? DistinguishedName { get; init; }

    /// <summary>True for errors where retrying the same request later may succeed.</summary>
    public bool IsTransient => Kind is DirectoryErrorKind.Busy or DirectoryErrorKind.ServerUnavailable or DirectoryErrorKind.Timeout;

    /// <summary>
    /// Extracts the Win32 error code from an AD diagnostic string ("0000052D: ...") or
    /// a bind error ("80090308: LdapErr: DSID-0C09050F, comment: AcceptSecurityContext error, data 775").
    /// For bind errors the "data" value is returned (e.g. 0x775 = account locked out).
    /// </summary>
    public static int? ParseWin32Error(string? serverMessage)
    {
        if (string.IsNullOrWhiteSpace(serverMessage))
        {
            return null;
        }

        var data = DataRegex().Match(serverMessage);
        if (data.Success
            && int.TryParse(data.Groups[1].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var dataCode)
            && dataCode != 0)
        {
            return dataCode;
        }

        var lead = LeadRegex().Match(serverMessage);
        return lead.Success && int.TryParse(lead.Groups[1].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var code)
            ? code
            : null;
    }

    /// <summary>Maps an LDAP result code (+ optional Win32 detail) to a <see cref="DirectoryErrorKind"/>.</summary>
    public static DirectoryErrorKind Classify(int ldapResultCode, int? win32Error)
    {
        switch (win32Error)
        {
            case 0x52D: // ERROR_PASSWORD_RESTRICTION
                return DirectoryErrorKind.PasswordPolicy;
            case 0x52E: // ERROR_LOGON_FAILURE
                return DirectoryErrorKind.InvalidCredentials;
            case 0x530 or 0x531 or 0x532 or 0x533 or 0x701 or 0x773 or 0x775: // time/workstation restriction, expired, disabled, must change, locked
                return DirectoryErrorKind.AccountRestriction;
            case 0x5: // ERROR_ACCESS_DENIED
                return DirectoryErrorKind.AccessDenied;
        }

        return ldapResultCode switch
        {
            0 => DirectoryErrorKind.Unknown,
            3 => DirectoryErrorKind.Timeout,
            4 => DirectoryErrorKind.SizeLimitExceeded,
            8 => DirectoryErrorKind.StrongAuthRequired,
            10 => DirectoryErrorKind.Referral,
            12 => DirectoryErrorKind.UnsupportedControl,
            16 => DirectoryErrorKind.NoSuchAttribute,
            19 => DirectoryErrorKind.ConstraintViolation,
            20 => DirectoryErrorKind.AttributeOrValueExists,
            21 => DirectoryErrorKind.InvalidSyntax,
            32 => DirectoryErrorKind.NoSuchObject,
            34 => DirectoryErrorKind.InvalidSyntax,
            48 or 49 => DirectoryErrorKind.InvalidCredentials,
            50 => DirectoryErrorKind.AccessDenied,
            51 => DirectoryErrorKind.Busy,
            52 or 81 or 91 => DirectoryErrorKind.ServerUnavailable,
            53 => DirectoryErrorKind.UnwillingToPerform,
            64 or 65 or 69 => DirectoryErrorKind.ObjectClassViolation,
            66 => DirectoryErrorKind.NotAllowedOnNonLeaf,
            68 => DirectoryErrorKind.AlreadyExists,
            85 => DirectoryErrorKind.Timeout,
            88 => DirectoryErrorKind.Cancelled,
            _ => DirectoryErrorKind.Unknown,
        };
    }

    [GeneratedRegex(@"\bdata ([0-9a-fA-F]+)\b")]
    private static partial Regex DataRegex();

    [GeneratedRegex(@"^\s*([0-9a-fA-F]{8}):")]
    private static partial Regex LeadRegex();
}
