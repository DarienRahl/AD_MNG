using AdMng.Core.ActiveDirectory;

namespace AdMng.Core.Batch;

/// <summary>
/// One operation configured by the operator in the batch editor and applied to every selected object.
/// Operations are declarative; <see cref="IChangePlanner"/> turns them into a concrete, previewable
/// <see cref="ChangePlan"/> using fresh values read from the directory.
/// </summary>
/// <remarks>
/// Value templates use the syntax described in <see cref="Templates.ITemplateEngine"/>, e.g.
/// <c>{givenName|nodiacritics|lower}.{sn|nodiacritics|lower}@contoso.com</c>.
/// </remarks>
public abstract record BatchOperation;

/// <summary>Sets an attribute to the rendered template (single value). An empty result clears the attribute
/// only when <paramref name="EmptyClears"/> is true, otherwise the object is left unchanged.</summary>
public sealed record SetAttributeOperation(string Attribute, string ValueTemplate, bool EmptyClears = false) : BatchOperation;

/// <summary>Removes all values of an attribute.</summary>
public sealed record ClearAttributeOperation(string Attribute) : BatchOperation;

/// <summary>
/// Sets an attribute to exactly <paramref name="Values"/> (no templating: braces are literal text). An empty list
/// clears the attribute. Used by property sheets and the attribute editor, where the operator edits the final values.
/// </summary>
/// <remarks>Booleans are normalized to TRUE/FALSE; duplicates (per the attribute's matching rule) are dropped.</remarks>
public sealed record SetValuesOperation(string Attribute, IReadOnlyList<string> Values) : BatchOperation
{
    public override string ToString() => $"{nameof(SetValuesOperation)} {{ Attribute = {Attribute}, Values = {Values.Count} }}";
}

/// <summary>Adds values (templates) to a multi-valued attribute, e.g. proxyAddresses or otherTelephone.</summary>
public sealed record AddValuesOperation(string Attribute, IReadOnlyList<string> ValueTemplates) : BatchOperation;

/// <summary>Removes the given values from a multi-valued attribute (case-insensitive match).</summary>
public sealed record RemoveValuesOperation(string Attribute, IReadOnlyList<string> Values) : BatchOperation;

/// <summary>Find and replace in every value of an attribute (plain text or .NET regular expression).</summary>
public sealed record FindReplaceOperation(string Attribute, string Find, string Replace, bool UseRegex = false, bool MatchCase = false) : BatchOperation;

/// <summary>Enables or disables the account (userAccountControl ACCOUNTDISABLE bit).</summary>
public sealed record SetAccountEnabledOperation(bool Enabled) : BatchOperation;

/// <summary>Sets or clears one userAccountControl flag such as <see cref="UserAccountControl.DontExpirePassword"/>.
/// Use <see cref="SetAccountEnabledOperation"/> for ACCOUNTDISABLE.</summary>
public sealed record SetAccountFlagOperation(UserAccountControl Flag, bool Value) : BatchOperation;

/// <summary>Unlocks locked-out accounts (lockoutTime = 0). Objects that are not locked are skipped.</summary>
public sealed record UnlockAccountOperation : BatchOperation;

/// <summary>"User must change password at next logon": true sets pwdLastSet = 0, false sets pwdLastSet = -1 (now).</summary>
public sealed record RequirePasswordChangeOperation(bool Required) : BatchOperation;

/// <summary>Sets accountExpires. Null = never expires.</summary>
public sealed record SetAccountExpirationOperation(DateTimeOffset? ExpiresAt) : BatchOperation;

/// <summary>
/// Sets logonHours of user accounts to <paramref name="Hours"/> (UTC, see <see cref="LogonHours"/>). Like ADUC the
/// attribute is never cleared: "all hours" is written as 21 × 0xFF, but only when the account has a restriction - an
/// absent attribute already means all hours, so it is left alone. Other object kinds are skipped with a warning.
/// </summary>
public sealed record SetLogonHoursOperation(LogonHours Hours) : BatchOperation;

/// <summary>How new passwords are produced by <see cref="ResetPasswordOperation"/>.</summary>
public enum PasswordMode
{
    /// <summary>A unique random password per account (cryptographic RNG, complexity compliant).</summary>
    GenerateUnique,

    /// <summary>The same operator-provided password for every account.</summary>
    Fixed,
}

/// <summary>Administrative password reset, optionally forcing a change at next logon and unlocking.</summary>
public sealed record ResetPasswordOperation(
    PasswordMode Mode,
    string? FixedPassword = null,
    bool MustChangeAtNextLogon = true,
    bool UnlockAccount = true,
    int GeneratedLength = 16) : BatchOperation
{
    /// <summary>Never print the password in logs or ToString().</summary>
    public override string ToString() =>
        $"{nameof(ResetPasswordOperation)} {{ Mode = {Mode}, MustChangeAtNextLogon = {MustChangeAtNextLogon}, UnlockAccount = {UnlockAccount} }}";
}

/// <summary>Moves objects into another container / OU.</summary>
public sealed record MoveOperation(string TargetContainerDn) : BatchOperation;

/// <summary>Renames objects (new RDN value from a template, e.g. <c>{givenName} {sn}</c>).
/// Optionally also sets displayName to the same value (ADUC rename behaviour for users).</summary>
public sealed record RenameOperation(string NameTemplate, bool AlsoSetDisplayName = false) : BatchOperation
{
    /// <summary>
    /// With <see cref="AlsoSetDisplayName"/>: the display name follows the rename only while it is empty or equal to the
    /// current name (RDN value, case-insensitive); a display name maintained separately - "Kowalski Jan (IT)", or
    /// "Jan Kowalski" for <c>CN=jkowalski</c> - is kept. Used by the inline rename (F2), which has no preview.
    /// </summary>
    public bool KeepCustomDisplayName { get; init; }
}

/// <summary>Adds objects to groups (modifies the groups' member attribute, idempotent).</summary>
public sealed record AddToGroupsOperation(IReadOnlyList<string> GroupDns) : BatchOperation;

/// <summary>Removes objects from groups (idempotent).</summary>
public sealed record RemoveFromGroupsOperation(IReadOnlyList<string> GroupDns) : BatchOperation;

/// <summary>Deletes objects. Cannot be combined with other operations.</summary>
public sealed record DeleteOperation(bool IncludeSubtree = false) : BatchOperation;

/// <summary>
/// "Resetuj konto" of a computer (K1 design note 4.6): a password reset of the machine account with a random password of
/// <see cref="GeneratedLength"/> characters generated at plan time that nobody sees - it is never shown, copied, exported or
/// written to a report. The computer loses its trust relationship until it is joined again or its secure channel is
/// repaired. Domain controllers are refused. Not reversible.
/// </summary>
public sealed record ResetComputerAccountOperation : BatchOperation
{
    /// <summary>Length of the generated machine password.</summary>
    public const int GeneratedLength = 120;
}
