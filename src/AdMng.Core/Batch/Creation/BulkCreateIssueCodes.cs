namespace AdMng.Core.Batch.Creation;

/// <summary>
/// The issue codes of the bulk creation wizard (docs/dev/phase3-bulk-create.md 2.5; complete in the contract). They are
/// translated through <c>Issue.&lt;code&gt;</c> like <see cref="IssueCodes"/>; the codes of <see cref="AttributeAndArguments"/>
/// take <c>{0}</c> = the attribute's display name and <c>{1}</c>… = <see cref="ValidationIssue.Arguments"/>, every other code
/// takes its first argument as <c>{0}</c> or, raised with an attribute and no argument, the attribute's display name (the
/// rendering of <c>DisplayNames.IssueText</c>); run issues have none. The XML doc of each code names its placeholders, which
/// the texts of the contract resx block (2.9) use. Arguments are names, counts and row numbers – never a password.
/// The existing <see cref="IssueCodes.TooLong"/>, <see cref="IssueCodes.TooShort"/>, <see cref="IssueCodes.SingleValued"/>,
/// <see cref="IssueCodes.InvalidValue"/>, <see cref="IssueCodes.MandatoryAttribute"/>, <see cref="IssueCodes.CannotCreateChild"/>,
/// <see cref="IssueCodes.TargetNotFound"/>, <see cref="IssueCodes.UnknownAttribute"/>, <see cref="IssueCodes.NotWritable"/> and
/// <see cref="IssueCodes.TemplateError"/> are reused with the attribute only.
/// </summary>
public static class BulkCreateIssueCodes
{
    /// <summary>More than 100 000 candidates or a number above 999 999 999 (2.4).</summary>
    public const string CounterExhausted = "CounterExhausted";

    /// <summary>A counter value over the attribute's limit; {0} the attribute, {1} the limit – a counter value is never cut (2.4).</summary>
    public const string CounterTooLong = "CounterTooLong";

    /// <summary>A value was taken and got a disambiguator (<c>jan.kowalski2</c>, <c>Jan Kowalski (2)</c>); {0} the attribute, no arguments (warning, 2.4).</summary>
    public const string Disambiguated = "Disambiguated";

    /// <summary>The value is taken in AD; {0} the attribute, {1} the owner's name (2.4).</summary>
    public const string DuplicateInDirectory = "DuplicateInDirectory";

    /// <summary>Another row has the same value; {0} the attribute, {1} the other row's number (2.4).</summary>
    public const string DuplicateRow = "DuplicateRow";

    /// <summary>The value clashes with an implicit UPN (<c>sam@domain</c>); {0} the attribute, {1} the owner's name (2.4).</summary>
    public const string ImplicitUpnClash = "ImplicitUpnClash";

    /// <summary>A token of an identifier rendered empty; {0} the attribute, {1} the token's source text (warning, 2.3).</summary>
    public const string EmptyToken = "EmptyToken";

    /// <summary>An object in the AD Recycle Bin holds the name; restoring it later needs a rename; {0} the attribute, no arguments (warning, 2.4).</summary>
    public const string NameInRecycleBin = "NameInRecycleBin";

    /// <summary>The UPN suffix is neither a domain of the forest nor one of uPNSuffixes (warning, 2.2).</summary>
    public const string UpnSuffixNotInForest = "UpnSuffixNotInForest";

    /// <summary>The mail domain is neither a domain of the forest nor a UPN suffix (warning, 2.4).</summary>
    public const string MailDomainNotInForest = "MailDomainNotInForest";

    /// <summary>The mail domain ends in .local, .lan, .internal, .localdomain or .corp (warning, 2.4).</summary>
    public const string NonRoutableMailDomain = "NonRoutableMailDomain";

    /// <summary>An existing user or contact looks like the same person; {0} the attribute, {1} the owner's name (warning, 2.4).</summary>
    public const string PossibleExistingAccount = "PossibleExistingAccount";

    /// <summary>Two rows describe the same person; {0} the attribute, {1} the other row (warning, 2.4).</summary>
    public const string DuplicateSourceRow = "DuplicateSourceRow";

    /// <summary>A phone value in scientific notation (<c>4,86E+11</c>); {0} the attribute, no arguments (warning, 2.2).</summary>
    public const string ScientificNotation = "ScientificNotation";

    /// <summary><c>PhoneFormat.International</c> could not format the phone number; {0} the attribute, no arguments (warning, 2.2).</summary>
    public const string InvalidPhone = "InvalidPhone";

    /// <summary>A line break or another control character in a single-line attribute; {0} the attribute, no arguments (2.2).</summary>
    public const string LineBreakInValue = "LineBreakInValue";

    /// <summary>A name column in capitals with case <c>AsIs</c>; {0} the attribute, no arguments (warning; 2.6 offers <em>Jak W Nazwie</em>).</summary>
    public const string AllCapsName = "AllCapsName";

    /// <summary>A user sAMAccountName ending in <c>$</c>, or a computer sAMAccountName without exactly one trailing <c>$</c> (2.2).</summary>
    public const string TrailingDollar = "TrailingDollar";

    /// <summary>The <c>Country</c> cell names no known country; nothing is written (warning, 2.5).</summary>
    public const string CountryNotFound = "CountryNotFound";

    /// <summary>The <c>ExpiresAt</c> cell is not a date in a supported format (2.5).</summary>
    public const string InvalidDate = "InvalidDate";

    /// <summary>No account matches the manager cell; the row is created without a manager (warning, 2.5).</summary>
    public const string ManagerNotFound = "ManagerNotFound";

    /// <summary>Several accounts match the manager cell; {0} the count (warning, 2.5).</summary>
    public const string ManagerAmbiguous = "ManagerAmbiguous";

    /// <summary>The manager is another row of the batch and does not exist yet (warning, 2.5).</summary>
    public const string ManagerInBatch = "ManagerInBatch";

    /// <summary>A group of the options or of the row's Groups cell does not exist; {0} the group's name as given (warning, 2.5).</summary>
    public const string GroupNotFound = "GroupNotFound";

    /// <summary>Several groups match a part of the row's Groups cell; {0} the name as given (warning, 2.5).</summary>
    public const string GroupAmbiguous = "GroupAmbiguous";

    /// <summary>The Groups cell was split on commas (warning, 2.5).</summary>
    public const string GroupsSplitOnCommas = "GroupsSplitOnCommas";

    /// <summary>The created group cannot be nested in the group (scope rules); {0} the group (2.5).</summary>
    public const string GroupScopeNotAllowed = "GroupScopeNotAllowed";

    /// <summary>The group is the account's primary group and is left out; {0} the group's name (info, 2.5).</summary>
    public const string PrimaryGroupSkipped = "PrimaryGroupSkipped";

    /// <summary>An option group has a fine-grained password policy that applies only after the account joins it; {0} the group (warning, 2.5).</summary>
    public const string GroupPasswordPolicy = "GroupPasswordPolicy";

    /// <summary>Enabled user accounts need a password (run error, 2.5).</summary>
    public const string EnabledWithoutPassword = "EnabledWithoutPassword";

    /// <summary>Passwords cannot be set over an unencrypted connection (run error, 2.5).</summary>
    public const string PasswordsNeedEncryptedChannel = "PasswordsNeedEncryptedChannel";

    /// <summary>The password length is below the domain minimum; {0} the minimum (run error, 2.5).</summary>
    public const string PasswordTooShortForDomain = "PasswordTooShortForDomain";

    /// <summary>The shared password does not meet the domain's complexity rule (run error, 2.5).</summary>
    public const string PasswordNotComplex = "PasswordNotComplex";

    /// <summary>The shared password contains the account name or a part of the display name (warning, 2.5).</summary>
    public const string PasswordContainsName = "PasswordContainsName";

    /// <summary>UPN and address uniqueness was checked in the domain only (no global catalog; run note, 2.4).</summary>
    public const string CheckedInDomainOnly = "CheckedInDomainOnly";

    /// <summary>The schema is not loaded: only the attributes of 2.2's table are accepted (run warning, 2.5).</summary>
    public const string SchemaUnavailable = "SchemaUnavailable";

    /// <summary>The computer name contains <c>_</c>, which is not valid in a DNS name (warning, 2.2).</summary>
    public const string ComputerNameNotDnsSafe = "ComputerNameNotDnsSafe";

    /// <summary>A join that reuses the computer account fails unless its owner is trusted (KB5020276); {0} the owner – the window's account (run warning, 2.5).</summary>
    public const string JoinOwnerNotTrusted = "JoinOwnerNotTrusted";

    /// <summary>The DC has fewer free RIDs than rows and another DC holds the RID master role (warning, 2.5).</summary>
    public const string RidPoolLow = "RidPoolLow";

    /// <summary>The value was taken since the check: the object is not retried (<em>wróć do Sprawdzenia</em>, 2.5).</summary>
    public const string ValueTakenSinceCheck = "ValueTakenSinceCheck";

    /// <summary>The created object is gone or another object holds its DN; the undo leaves it (2.5).</summary>
    public const string UndoObjectReplaced = "UndoObjectReplaced";

    /// <summary>The created account was used (logon or password change) since it was created (warning, 2.5).</summary>
    public const string UndoAccountInUse = "UndoAccountInUse";

    /// <summary>The created object has child objects; its delete will fail (warning, 2.5).</summary>
    public const string UndoHasChildren = "UndoHasChildren";

    /// <summary>Every code of this class, in declaration order.</summary>
    public static IReadOnlyList<string> All { get; } =
    [
        CounterExhausted, CounterTooLong, Disambiguated, DuplicateInDirectory, DuplicateRow, ImplicitUpnClash,
        EmptyToken, NameInRecycleBin, UpnSuffixNotInForest, MailDomainNotInForest, NonRoutableMailDomain,
        PossibleExistingAccount, DuplicateSourceRow, ScientificNotation, InvalidPhone, LineBreakInValue, AllCapsName,
        TrailingDollar, CountryNotFound, InvalidDate, ManagerNotFound, ManagerAmbiguous, ManagerInBatch, GroupNotFound,
        GroupAmbiguous, GroupsSplitOnCommas, GroupScopeNotAllowed, PrimaryGroupSkipped, GroupPasswordPolicy,
        EnabledWithoutPassword, PasswordsNeedEncryptedChannel, PasswordTooShortForDomain, PasswordNotComplex,
        PasswordContainsName, CheckedInDomainOnly, SchemaUnavailable, ComputerNameNotDnsSafe, JoinOwnerNotTrusted,
        RidPoolLow, ValueTakenSinceCheck, UndoObjectReplaced, UndoAccountInUse, UndoHasChildren,
    ];

    /// <summary>
    /// The codes whose localized text takes <c>{0}</c> = the attribute's display name and <c>{1}</c>… =
    /// <see cref="ValidationIssue.Arguments"/> (<c>DisplayNames.IssueText</c>).
    /// </summary>
    public static IReadOnlySet<string> AttributeAndArguments { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        CounterTooLong, DuplicateInDirectory, DuplicateRow, ImplicitUpnClash, EmptyToken, PossibleExistingAccount, DuplicateSourceRow,
    };
}
