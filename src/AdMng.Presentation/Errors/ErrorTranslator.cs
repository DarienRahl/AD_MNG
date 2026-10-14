using AdMng.Core.Batch;
using AdMng.Core.Directory;
using AdMng.Core.Schema;
using AdMng.Presentation.Localization;

namespace AdMng.Presentation.Errors;

/// <summary>
/// Turns exceptions, directory error kinds and validation issues into localized, operator friendly messages.
/// Raw server diagnostics are never shown alone - they are appended as a detail when they add information.
/// </summary>
public sealed class ErrorTranslator(ILocalizer localizer)
{
    // Win32 sub-codes from AD bind / operation diagnostics ("data 775", "0000052D").
    private const int UserNotFound = 0x525;
    private const int LogonFailure = 0x52E;
    private const int InvalidLogonHours = 0x530;
    private const int InvalidWorkstation = 0x531;
    private const int PasswordExpired = 0x532;
    private const int AccountDisabled = 0x533;
    private const int AccountExpired = 0x701;
    private const int MustChangePassword = 0x773;
    private const int AccountLockedOut = 0x775;
    private const int PasswordRestriction = 0x52D;

    /// <summary>Message for any exception (directory errors, cancellation, file errors, unexpected failures).</summary>
    public string Translate(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        return exception switch
        {
            DirectoryException de => Translate(de.Kind, de.Win32Error),
            OperationCanceledException => localizer[L.Errors.Cancelled],
            TimeoutException => localizer.ErrorKindName(DirectoryErrorKind.Timeout),
            UnauthorizedAccessException or IOException => localizer.Format(L.Errors.File, exception.Message),
            AggregateException { InnerExceptions.Count: 1 } ae => Translate(ae.InnerExceptions[0]),
            _ => localizer.Format(L.Errors.Unexpected, exception.Message),
        };
    }

    /// <summary>Message for an error kind, refined by the Win32 detail when AD provided one.</summary>
    public string Translate(DirectoryErrorKind kind, int? win32Error = null) => win32Error switch
    {
        AccountLockedOut => localizer[L.Errors.AccountLockedOut],
        AccountDisabled => localizer[L.Errors.AccountDisabled],
        AccountExpired => localizer[L.Errors.AccountExpired],
        PasswordExpired => localizer[L.Errors.PasswordExpired],
        MustChangePassword => localizer[L.Errors.MustChangePassword],
        InvalidLogonHours => localizer[L.Errors.InvalidLogonHours],
        InvalidWorkstation => localizer[L.Errors.InvalidWorkstation],
        PasswordRestriction => localizer.ErrorKindName(DirectoryErrorKind.PasswordPolicy),
        UserNotFound or LogonFailure => localizer.ErrorKindName(DirectoryErrorKind.InvalidCredentials),
        _ => localizer.ErrorKindName(kind),
    };

    /// <summary>Message for a failed step of a batch (<see cref="ChangeResult.ErrorKind"/> + <see cref="ChangeResult.ErrorMessage"/>).</summary>
    /// <remarks>
    /// A compare-and-swap failure (<see cref="BatchExecutor.ConflictMessagePrefix"/>: the value was changed by
    /// somebody else after the preview) is reported as such, not as the raw LDAP error (noSuchAttribute...); so is a
    /// restore skipped because its parent container was not restored (<see cref="BatchExecutor.ParentNotRestoredMessage"/>).
    /// </remarks>
    public string TranslateFailure(DirectoryErrorKind? kind, string? errorMessage)
    {
        if (errorMessage is not null && errorMessage.StartsWith(BatchExecutor.ConflictMessagePrefix, StringComparison.Ordinal))
        {
            return localizer[L.Errors.Conflict];
        }

        if (string.Equals(errorMessage, BatchExecutor.ParentNotRestoredMessage, StringComparison.Ordinal))
        {
            return localizer[L.Errors.ParentNotRestored];
        }

        if (kind is null or DirectoryErrorKind.Unknown)
        {
            return string.IsNullOrWhiteSpace(errorMessage)
                ? localizer.ErrorKindName(DirectoryErrorKind.Unknown)
                : localizer.Format(L.Errors.Unexpected, errorMessage);
        }

        var win32 = DirectoryException.ParseWin32Error(errorMessage);
        return Translate(kind.Value, win32);
    }

    /// <summary>
    /// Message for a failed connection attempt: the error plus a hint how to fix it (credentials, network,
    /// LDAP signing policy, account state).
    /// </summary>
    public string TranslateConnectError(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        var message = Translate(exception);
        var hint = exception is DirectoryException de ? HintFor(de) : null;
        return hint is null ? message : $"{message}{Environment.NewLine}{localizer[hint]}";
    }

    /// <summary>Localized validation issue text.</summary>
    public string TranslateIssue(ValidationIssue issue, ISchemaCatalog? schema = null) => localizer.IssueText(issue, schema);

    private static string? HintFor(DirectoryException exception) => exception.Win32Error switch
    {
        AccountLockedOut or AccountDisabled or AccountExpired or InvalidLogonHours or InvalidWorkstation => L.Hints.AccountRestriction,
        PasswordExpired or MustChangePassword => L.Hints.ChangePassword,
        _ => exception.Kind switch
        {
            DirectoryErrorKind.InvalidCredentials => L.Hints.InvalidCredentials,
            DirectoryErrorKind.AccountRestriction => L.Hints.AccountRestriction,
            DirectoryErrorKind.ServerUnavailable or DirectoryErrorKind.Timeout => L.Hints.ServerUnavailable,
            DirectoryErrorKind.StrongAuthRequired => L.Hints.StrongAuthRequired,
            DirectoryErrorKind.UnwillingToPerform => L.Hints.UnwillingToPerform,
            _ => null,
        },
    };
}
