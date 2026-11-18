using System.Globalization;
using AdMng.Core.ActiveDirectory;
using AdMng.Core.Model;

namespace AdMng.Core.Batch.Creation;

// The model of the bulk creation wizard (docs/dev/phase3-bulk-create.md 2.4, 2.5, 2.12; complete in the contract). Every
// ToString prints kinds, counts, row numbers and statuses – never a value, a DN, a header or a password (rule 1 and 2,
// pinned by BulkCreateContractTests).

/// <summary>Where a counter starts looking for a free number (2.4).</summary>
public enum CounterMode
{
    /// <summary><em>Najniższy wolny numer (uzupełnia luki)</em>: from the start number, the first number free in AD.</summary>
    FillGaps,

    /// <summary><em>Kolejny po najwyższym istniejącym</em>: the first grid number above the highest number taken.</summary>
    AfterHighest,
}

/// <summary>What happens when a name-based value is taken (2.4).</summary>
public enum CollisionStrategy
{
    /// <summary><em>Dopisz numer</em>: the row's roots get 2, 3 … (<c>jan.kowalski2</c>, <c>Jan Kowalski (2)</c>).</summary>
    AppendNumber,

    /// <summary><em>Oznacz jako błąd</em>: the row is an error (always for computers).</summary>
    MarkError,
}

/// <summary>The initial password of created users (2.5).</summary>
public enum InitialPasswordMode
{
    /// <summary><em>Generuj osobne hasło dla każdego konta</em>.</summary>
    Generate,

    /// <summary><em>Jedno hasło dla wszystkich kont</em> (typed at plan time, never stored).</summary>
    SameForAll,

    /// <summary><em>Bez hasła – konto wyłączone</em>.</summary>
    None,
}

/// <summary>The format of phone attributes (2.1, 2.2).</summary>
public enum PhoneFormat
{
    /// <summary>As typed (normalized only).</summary>
    AsIs,

    /// <summary><c>+48 600 100 200</c> (<see cref="CellText.NormalizePhone"/>).</summary>
    International,
}

/// <summary>The status of a row of the check grid (2.5).</summary>
public enum BulkRowStatus
{
    Ok,
    Warning,
    Error,

    /// <summary>Excluded by the operator (no lookups).</summary>
    Excluded,

    /// <summary>Already created by an earlier execution of this window (no lookups).</summary>
    Created,
}

/// <summary>Where a computed value came from (2.3).</summary>
public enum BulkValueSource
{
    /// <summary>A mapped column cell.</summary>
    Column,

    /// <summary>A cell the operator edited in the check grid.</summary>
    Edited,

    /// <summary>A rule with tokens.</summary>
    Pattern,

    /// <summary>A rule without tokens (a fixed value).</summary>
    Fixed,

    /// <summary>An account option (password, enable, expiry …).</summary>
    Option,

    /// <summary>A value frozen from the check computation (<see cref="BulkCreateInput.FrozenValues"/>).</summary>
    Frozen,
}

/// <summary>How far the uniqueness of UPN, <c>mail</c> and <c>proxyAddresses</c> was checked (2.4).</summary>
public enum UniquenessScope
{
    /// <summary>Several domains, no global catalog: <em>sprawdzone tylko w domenie</em>.</summary>
    Domain,

    /// <summary>One domain: the domain searches cover the forest.</summary>
    ForestSingleDomain,

    /// <summary>Several domains, searched on the global catalog of the window's DC.</summary>
    Forest,
}

/// <summary>The naming rule of one attribute (2.3).</summary>
/// <param name="Attribute">The LDAP display name (for the UPN: the prefix pattern).</param>
/// <param name="Pattern">The pattern in the template syntax (a pattern without tokens is a fixed value).</param>
/// <param name="OverridesColumn">The rule comes before a mapped column (an edit still wins).</param>
public sealed record AttributeRule(string Attribute, string Pattern, bool OverridesColumn = false)
{
    /// <summary>The attribute and the pattern's length only (a pattern may hold literal names or addresses).</summary>
    public override string ToString() =>
        $"{Attribute}: rule of {Pattern.Length} characters{(OverridesColumn ? ", overrides the column" : string.Empty)}";
}

/// <summary>The naming rules and options of a run (from the profile, 2.6).</summary>
public sealed record BulkCreateRules
{
    /// <summary>The kind of the created objects.</summary>
    public ObjectKind Kind { get; init; } = ObjectKind.User;

    /// <summary>One rule per attribute.</summary>
    public IReadOnlyList<AttributeRule> Attributes { get; init; } = [];

    /// <summary>The UPN suffix of the run (<c>{$suffix}</c>); null = the window's domain.</summary>
    public string? UpnSuffix { get; init; }

    /// <summary>The mail domain of the run (<c>{$mailDomain}</c>); null = the UPN suffix.</summary>
    public string? MailDomain { get; init; }

    public CounterMode CounterMode { get; init; } = CounterMode.FillGaps;

    /// <summary>A counter number held only by an object in the AD Recycle Bin is free (with a warning).</summary>
    public bool ReuseRecycleBinNumbers { get; init; }

    public CollisionStrategy Collisions { get; init; } = CollisionStrategy.AppendNumber;

    /// <summary>Identifier rule values are sanitized character-wise (2.2).</summary>
    public bool SanitizeIdentifiers { get; init; } = true;

    /// <summary>sAMAccountName and cn rule values are cut to their limits (never a counter value).</summary>
    public bool TruncateNames { get; init; } = true;

    /// <summary>The primary <c>SMTP:</c> address in <c>proxyAddresses</c> equal to <c>mail</c> (off by default, 2.14).</summary>
    public bool AddPrimarySmtp { get; init; }

    public PhoneFormat Phones { get; init; } = PhoneFormat.AsIs;

    public override string ToString() =>
        $"{Kind} rules: {Attributes.Count} rules, suffix={(UpnSuffix is not null)}, mail domain={(MailDomain is not null)}, {CounterMode}, "
        + $"reuse recycled={ReuseRecycleBinNumbers}, {Collisions}, sanitize={SanitizeIdentifiers}, truncate={TruncateNames}, "
        + $"primary SMTP={AddPrimarySmtp}, phones={Phones}";
}

/// <summary>The account options of a run (2.5, 2.6); the shared password is never part of them.</summary>
public sealed record BulkAccountOptions
{
    public InitialPasswordMode PasswordMode { get; init; } = InitialPasswordMode.Generate;

    /// <summary>The length of generated passwords (never below the domain minimum).</summary>
    public int GeneratedLength { get; init; } = 16;

    /// <summary><em>Użytkownik musi zmienić hasło przy następnym logowaniu</em> (<c>pwdLastSet = 0</c>).</summary>
    public bool MustChangeAtLogon { get; init; } = true;

    /// <summary>The accounts are enabled after the password is set (needs a password the domain accepts).</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>The end of the chosen day for every account (an <c>ExpiresAt</c> cell of a row wins); null = never.</summary>
    public DateTimeOffset? ExpiresAt { get; init; }

    /// <summary>Groups every created object is added to (DNs, picked in the window's domain).</summary>
    public IReadOnlyList<string> GroupDns { get; init; } = [];

    /// <summary>The scope of created groups.</summary>
    public GroupScope GroupScope { get; init; } = GroupScope.Global;

    /// <summary>Created groups are security groups (else distribution).</summary>
    public bool SecurityGroup { get; init; } = true;

    /// <summary>The SID of the principal allowed to join created computers (K1 join right); null = none.</summary>
    public string? JoinerSid { get; init; }

    /// <summary>The name of <see cref="JoinerSid"/> for the plan and the PowerShell comment.</summary>
    public string? JoinerName { get; init; }

    /// <summary>
    /// The attributes the possible-existing-account check uses (2.4): null = the default order (<c>employeeID</c>,
    /// <c>employeeNumber</c>, <c>mail</c>, <c>mobile</c>, then given name + surname), empty = off.
    /// </summary>
    public IReadOnlyList<string>? MatchAttributes { get; init; }

    public override string ToString() =>
        $"Account options: {PasswordMode}, length {GeneratedLength}, must change={MustChangeAtLogon}, enabled={Enabled}, "
        + $"expires={(ExpiresAt is not null)}, groups={GroupDns.Count}, {GroupScope}, security={SecurityGroup}, joiner={(JoinerSid is not null)}, "
        + $"match={(MatchAttributes is null ? "default" : MatchAttributes.Count.ToString(CultureInfo.InvariantCulture))}";
}

/// <summary>A country of the <c>Country</c> target (2.5): <c>c</c>, <c>co</c> and <c>countryCode</c>.</summary>
/// <param name="Code">ISO 3166 alpha-2 code (<c>c</c>).</param>
/// <param name="Name">The name in the UI language (<c>co</c>, as ADUC writes it).</param>
/// <param name="NumericCode">ISO 3166 numeric code (<c>countryCode</c>).</param>
public sealed record CountryValue(string Code, string Name, int NumericCode)
{
    public override string ToString() => "Country value";
}

/// <summary>Everything a computation needs (2.5): the table, the mappings, the rules, the options and the wizard's state.</summary>
public sealed record BulkCreateInput
{
    /// <summary>The container all objects are created in (one per run, decision 1).</summary>
    public required string ContainerDn { get; init; }

    public required InputTable Table { get; init; }

    public required IReadOnlyList<ColumnMapping> Mappings { get; init; }

    public required BulkCreateRules Rules { get; init; }

    public required BulkAccountOptions Options { get; init; }

    /// <summary>Cells edited in the check grid: row number → attribute → value (an edit wins over everything).</summary>
    public IReadOnlyDictionary<int, IReadOnlyDictionary<string, string>> Edits { get; init; } = new Dictionary<int, IReadOnlyDictionary<string, string>>();

    /// <summary>Row numbers the operator excluded.</summary>
    public IReadOnlyCollection<int> ExcludedRows { get; init; } = [];

    /// <summary>Rows created by an earlier execution of this window: row number → the created DN.</summary>
    public IReadOnlyDictionary<int, string> CreatedRows { get; init; } = new Dictionary<int, string>();

    /// <summary>The counter numbers of the previous computation (row number → number), kept while still free (2.4).</summary>
    public IReadOnlyDictionary<int, int> PinnedNumbers { get; init; } = new Dictionary<int, int>();

    /// <summary>
    /// The finished values of the check computation per row (set by the plan step, 2.7): nothing is rendered, numbered or
    /// disambiguated; a collision becomes an error.
    /// </summary>
    public IReadOnlyDictionary<int, IReadOnlyList<BulkValue>>? FrozenValues { get; init; }

    /// <summary>Identifiers this window created and its own undo deleted: never taken because of their deleted copy (2.4).</summary>
    public IReadOnlyCollection<string> ReleasedValues { get; init; } = [];

    /// <summary>The UPN suffixes of the forest (<c>uPNSuffixes</c>, read by the rules step).</summary>
    public IReadOnlyList<string> UpnSuffixes { get; init; } = [];

    /// <summary>The countries of the <c>Country</c> target by folded name or code (<c>BulkCreateCountries.Build</c>, 2.6).</summary>
    public IReadOnlyDictionary<string, CountryValue> Countries { get; init; } = new Dictionary<string, CountryValue>();

    /// <summary>Lookups in flight at most (the batch parallelism setting).</summary>
    public int MaxParallelLookups { get; init; } = 4;

    public override string ToString() =>
        $"Bulk create input: {Table}, {Mappings.Count} mappings, {Rules.Kind}, edits={Edits.Count}, excluded={ExcludedRows.Count}, "
        + $"created={CreatedRows.Count}, pins={PinnedNumbers.Count}, frozen={(FrozenValues is not null)}, released={ReleasedValues.Count}";
}

/// <summary>The computed values of one attribute of one row (2.3).</summary>
/// <param name="Attribute">The LDAP display name.</param>
/// <param name="Values">The values (one for single-valued attributes).</param>
/// <param name="Source">Where they came from.</param>
public sealed record BulkValue(string Attribute, IReadOnlyList<string> Values, BulkValueSource Source)
{
    /// <summary>Disambiguated (<c>jan.kowalski2</c>; row warning <c>Disambiguated</c>).</summary>
    public bool Adjusted { get; init; }

    /// <summary>Characters were changed by <see cref="CreationValues.Finish"/>.</summary>
    public bool Sanitized { get; init; }

    /// <summary>Cut to the attribute's limit by <see cref="CreationValues.Finish"/>.</summary>
    public bool Truncated { get; init; }

    public override string ToString() =>
        $"{Attribute}: {Values.Count} values from {Source}{(Adjusted ? ", adjusted" : string.Empty)}{(Sanitized ? ", sanitized" : string.Empty)}{(Truncated ? ", truncated" : string.Empty)}";
}

/// <summary>One row of the check grid (2.5).</summary>
public sealed record BulkCreateRow
{
    /// <summary>The wizard's row number (<see cref="TableRow.Number"/>).</summary>
    public required int Number { get; init; }

    public required BulkRowStatus Status { get; init; }

    /// <summary>The computed values per attribute.</summary>
    public required IReadOnlyList<BulkValue> Values { get; init; }

    /// <summary>The DN to create (null when <c>cn</c> is invalid) or, for created rows, the created DN.</summary>
    public string? DistinguishedName { get; init; }

    /// <summary>The counter number assigned to the row (the next pin).</summary>
    public int? CounterNumber { get; init; }

    /// <summary>The resolved manager.</summary>
    public string? ManagerDn { get; init; }

    /// <summary>The candidates of an ambiguous manager (the cell's drop-down, 2.7).</summary>
    public IReadOnlyList<string> ManagerCandidates { get; init; } = [];

    /// <summary>The groups the object is added to (option groups and the row's <c>Groups</c> cells).</summary>
    public IReadOnlyList<string> GroupDns { get; init; } = [];

    /// <summary>Possible existing accounts of the same person (<c>PossibleExistingAccount</c>).</summary>
    public IReadOnlyList<string> MatchDns { get; init; } = [];

    public IReadOnlyList<ValidationIssue> Issues { get; init; } = [];

    /// <summary>The first value of <paramref name="attribute"/>, or null.</summary>
    public string? Value(string attribute)
    {
        ArgumentNullException.ThrowIfNull(attribute);
        foreach (var value in Values)
        {
            if (string.Equals(value.Attribute, attribute, StringComparison.OrdinalIgnoreCase))
            {
                return value.Values.Count > 0 ? value.Values[0] : null;
            }
        }

        return null;
    }

    public override string ToString() =>
        $"Row {Number}: {Status}, {Values.Count} values, {Issues.Count} issues, counter={(CounterNumber is not null)}, manager={(ManagerDn is not null)}, "
        + $"candidates={ManagerCandidates.Count}, groups={GroupDns.Count}, matches={MatchDns.Count}";
}

/// <summary>The domain's password policy (<c>minPwdLength</c>, <c>pwdProperties</c> bit 1).</summary>
public sealed record DomainPasswordPolicy(int MinimumLength, bool ComplexityRequired)
{
    public override string ToString() => $"Password policy: minimum {MinimumLength}, complexity={ComplexityRequired}";
}

/// <summary>The result of <see cref="IBulkCreatePlanner.ComputeAsync"/>: the check grid (2.5).</summary>
public sealed record BulkCreateComputation
{
    /// <summary>The input it was computed from.</summary>
    public required BulkCreateInput Input { get; init; }

    public required IReadOnlyList<BulkCreateRow> Rows { get; init; }

    /// <summary>The check-grid columns: <c>cn</c>, <c>sAMAccountName</c>, <c>userPrincipalName</c>, <c>mail</c> … then by name.</summary>
    public required IReadOnlyList<string> Attributes { get; init; }

    /// <summary>The DC the values were read from (the plan's <see cref="ChangePlan.Server"/>, rule 4).</summary>
    public required string Server { get; init; }

    public UniquenessScope Scope { get; init; } = UniquenessScope.Domain;

    /// <summary>Run issues (not bound to a row): target, permissions, patterns, options, password policy …</summary>
    public IReadOnlyList<ValidationIssue> Issues { get; init; } = [];

    public DomainPasswordPolicy? PasswordPolicy { get; init; }

    /// <summary>The DNS names of the forest's domains (crossRef <c>dnsRoot</c>).</summary>
    public IReadOnlyList<string> ForestDomains { get; init; } = [];

    /// <summary>The window's DC holds the PDC emulator role; null = unknown.</summary>
    public bool? IsPdcEmulator { get; init; }

    public DateTimeOffset ComputedAt { get; init; }

    /// <summary>A run issue is an error: nothing can be planned.</summary>
    public bool HasRunErrors => Issues.Any(i => i.Severity == IssueSeverity.Error);

    /// <summary>The rows with <paramref name="status"/>.</summary>
    public int Count(BulkRowStatus status) => Rows.Count(r => r.Status == status);

    /// <summary>The counter number per row number (the pins of the next computation).</summary>
    public IReadOnlyDictionary<int, int> Numbers =>
        Rows.Where(r => r.CounterNumber is not null).ToDictionary(r => r.Number, r => r.CounterNumber!.Value);

    public override string ToString() =>
        $"Bulk create computation: {Rows.Count} rows (ok {Count(BulkRowStatus.Ok)}, warnings {Count(BulkRowStatus.Warning)}, errors {Count(BulkRowStatus.Error)}, "
        + $"excluded {Count(BulkRowStatus.Excluded)}, created {Count(BulkRowStatus.Created)}), {Attributes.Count} attributes, {Scope}, {Issues.Count} run issues, "
        + $"domains={ForestDomains.Count}";
}

/// <summary>The live sample of the <em>Numeracja</em> panel (2.4 step 3); null values when the prefix is not constant.</summary>
/// <param name="Prefix">The rules' constant prefix of the primary counter attribute.</param>
/// <param name="FirstFree">The first free number.</param>
/// <param name="Highest">The highest number taken.</param>
public sealed record CounterPreview(string? Prefix, int? FirstFree, int? Highest)
{
    public override string ToString() =>
        $"Counter preview (prefix={(Prefix is not null)}, first free={(FirstFree is not null)}, highest={(Highest is not null)})";
}

/// <summary>An object this wizard created (memory only; the basis of the undo, 2.5).</summary>
/// <param name="Row">The wizard's row number.</param>
/// <param name="DistinguishedName">The DN it was created at.</param>
/// <param name="Kind">Its kind.</param>
/// <param name="ObjectGuid">Its objectGUID (read after the execution).</param>
/// <param name="WhenCreated">Its whenCreated.</param>
/// <param name="MustChangeSet">The plan set <c>pwdLastSet = 0</c> (the undo warns when it is no longer 0).</param>
public sealed record CreatedObject(int Row, string DistinguishedName, ObjectKind Kind, Guid? ObjectGuid, DateTimeOffset? WhenCreated, bool MustChangeSet)
{
    public override string ToString() => $"Row {Row}: created {Kind} (guid={(ObjectGuid is not null)}, must change={MustChangeSet})";
}
