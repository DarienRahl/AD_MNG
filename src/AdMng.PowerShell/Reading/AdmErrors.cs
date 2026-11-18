using System.Management.Automation;
using AdMng.Core.Directory;

namespace AdMng.PowerShell.Reading;

/// <summary>The error records of the read commands: normal PowerShell errors with the object they are about as the target.</summary>
internal static class AdmErrors
{
    /// <summary>An object that was asked for by identity does not exist (or is not of the type the command returns).</summary>
    public static ErrorRecord NotFound(string identity, string description, string? searchBase = null)
    {
        var where = searchBase is null ? string.Empty : $" under '{searchBase}'";
        return new ErrorRecord(
            new ItemNotFoundException($"Cannot find {description} with identity '{identity}'{where}: it does not exist, or this account cannot see it."),
            "AdmObjectNotFound",
            ErrorCategory.ObjectNotFound,
            identity);
    }

    /// <summary>A text that is no usable identity (empty).</summary>
    public static ErrorRecord InvalidIdentity() =>
        new(new ArgumentException("The identity is empty."), "AdmInvalidIdentity", ErrorCategory.InvalidArgument, null);

    /// <summary>An argument the command refuses before it reads anything (a bad filter, an unknown attribute, a bad search base).</summary>
    public static ErrorRecord InvalidArgument(string message, string errorId, object? target) =>
        new(new ArgumentException(message), errorId, ErrorCategory.InvalidArgument, target);

    /// <summary>The directory refused or failed the read; the message says what and where, and the permission case says so plainly.</summary>
    public static ErrorRecord FromDirectory(DirectoryException error, string? target)
    {
        ArgumentNullException.ThrowIfNull(error);
        var place = target is null ? string.Empty : $" '{target}'";
        var (category, text) = error.Kind switch
        {
            DirectoryErrorKind.AccessDenied => (ErrorCategory.PermissionDenied, $"Access denied reading{place}: the account of this window may not read it."),
            DirectoryErrorKind.NoSuchObject => (ErrorCategory.ObjectNotFound, $"The object{place} does not exist."),
            DirectoryErrorKind.InvalidSyntax or DirectoryErrorKind.NoSuchAttribute => (ErrorCategory.InvalidArgument, $"The directory refused the request{place}."),
            DirectoryErrorKind.Timeout or DirectoryErrorKind.ServerUnavailable or DirectoryErrorKind.Busy => (ErrorCategory.ResourceUnavailable, $"The domain controller did not answer the read{place}."),
            DirectoryErrorKind.SizeLimitExceeded => (ErrorCategory.LimitsExceeded, $"The domain controller limited the result{place}."),
            _ => (ErrorCategory.ReadError, $"The read{place} failed."),
        };
        return new ErrorRecord(
            new InvalidOperationException($"{text} {error.Message}", error),
            "Adm" + error.Kind,
            category,
            target);
    }

    /// <summary>A mandatory attribute did not come back: the DC hides what the account may not read.</summary>
    public static ErrorRecord HiddenAttribute(string attribute, string distinguishedName) =>
        new(
            new UnauthorizedAccessException(
                $"The attribute '{attribute}' of '{distinguishedName}' was not returned although every object of its class has it: this account has no permission to read it. " +
                "Other objects are affected the same way; this message is shown once."),
            "AdmAttributeHidden",
            ErrorCategory.PermissionDenied,
            distinguishedName);
}
