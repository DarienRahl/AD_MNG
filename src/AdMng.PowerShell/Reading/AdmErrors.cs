using System.Globalization;
using System.Management.Automation;
using AdMng.Core.Directory;
using AdMng.Core.Model;

namespace AdMng.PowerShell.Reading;

/// <summary>
/// The error records of the read commands: normal PowerShell errors with the object they are about as the target, their text in the
/// window's language (<see cref="AdmText"/>); the error ids stay the same in every language, so a script keys on them.
/// </summary>
internal static class AdmErrors
{
    /// <summary>An object that was asked for by identity does not exist (or is not of the <paramref name="kind"/> the command returns).</summary>
    public static ErrorRecord NotFound(CultureInfo culture, string identity, ObjectKind kind, string? searchBase = null)
    {
        var noun = kind switch
        {
            ObjectKind.User => "User",
            ObjectKind.Group => "Group",
            ObjectKind.Computer => "Computer",
            _ => "Object",
        };
        var text = searchBase is null
            ? AdmText.Format(culture, "NotFound." + noun, identity)
            : AdmText.Format(culture, "NotFound." + noun + ".Under", identity, searchBase);
        return new ErrorRecord(new ItemNotFoundException(text), "AdmObjectNotFound", ErrorCategory.ObjectNotFound, identity);
    }

    /// <summary>A text that is no usable identity (empty).</summary>
    public static ErrorRecord InvalidIdentity(CultureInfo culture) =>
        new(new ArgumentException(AdmText.Get(culture, AdmText.Keys.IdentityEmpty)), "AdmInvalidIdentity", ErrorCategory.InvalidArgument, null);

    /// <summary>An argument the command refuses before it reads anything (a bad filter, an unknown attribute, a bad search base).</summary>
    public static ErrorRecord InvalidArgument(string message, string errorId, object? target) =>
        new(new ArgumentException(message), errorId, ErrorCategory.InvalidArgument, target);

    /// <summary>The directory refused or failed the read; the message says what and where, and the permission case says so plainly.</summary>
    public static ErrorRecord FromDirectory(CultureInfo culture, DirectoryException error, string? target)
    {
        ArgumentNullException.ThrowIfNull(error);
        var (category, key) = error.Kind switch
        {
            DirectoryErrorKind.AccessDenied => (ErrorCategory.PermissionDenied, "Directory.AccessDenied"),
            DirectoryErrorKind.NoSuchObject => (ErrorCategory.ObjectNotFound, "Directory.NoSuchObject"),
            DirectoryErrorKind.InvalidSyntax or DirectoryErrorKind.NoSuchAttribute => (ErrorCategory.InvalidArgument, "Directory.Refused"),
            DirectoryErrorKind.Timeout or DirectoryErrorKind.ServerUnavailable or DirectoryErrorKind.Busy => (ErrorCategory.ResourceUnavailable, "Directory.NoAnswer"),
            DirectoryErrorKind.SizeLimitExceeded => (ErrorCategory.LimitsExceeded, "Directory.Limited"),
            _ => (ErrorCategory.ReadError, "Directory.Failed"),
        };
        var text = target is null ? AdmText.Get(culture, key) : AdmText.Format(culture, key + ".At", target);
        return new ErrorRecord(
            new InvalidOperationException($"{text} {error.Message}", error),
            "Adm" + error.Kind,
            category,
            target);
    }

    /// <summary>A mandatory attribute did not come back: the DC hides what the account may not read.</summary>
    public static ErrorRecord HiddenAttribute(CultureInfo culture, string attribute, string distinguishedName) =>
        new(
            new UnauthorizedAccessException(AdmText.Format(culture, AdmText.Keys.AttributeHidden, attribute, distinguishedName)),
            "AdmAttributeHidden",
            ErrorCategory.PermissionDenied,
            distinguishedName);
}
