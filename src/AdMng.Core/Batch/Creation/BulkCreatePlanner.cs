using System.Globalization;
using AdMng.Core.ActiveDirectory;
using AdMng.Core.Directory;
using AdMng.Core.Model;
using AdMng.Core.Security;

namespace AdMng.Core.Batch.Creation;

/// <summary>
/// The planner of the bulk creation wizard (docs/dev/phase3-bulk-create.md 2.4, 2.5, owner B2; split into partials
/// <c>BulkCreatePlanner.*.cs</c>: <c>Compute</c> – rows and rules, <c>Counters</c>, <c>Uniqueness</c>, <c>References</c> –
/// managers, groups and the run reads, <c>Plan</c>, <c>Retry</c>, <c>Undo</c>). Stateless and thread-safe; caches live in
/// <see cref="BulkCreateLookups"/>.
/// </summary>
/// <param name="passwords">Generates the per-account passwords (and the random machine passwords of computers).</param>
/// <param name="time">The clock of <see cref="ChangePlan.CreatedAt"/>, <see cref="BulkCreateComputation.ComputedAt"/> and <c>{$date}</c>.</param>
public sealed partial class BulkCreatePlanner(PasswordGenerator passwords, TimeProvider time) : IBulkCreatePlanner
{
    /// <summary>
    /// The edit key (<see cref="BulkCreateInput.Edits"/>) that replaces the text of the row's <see cref="ColumnTargetKind.Manager"/>
    /// cell (the editable <em>Menedżer</em> cell of the check grid, 2.7); <c>managedBy</c> is accepted as well.
    /// </summary>
    public const string ManagerEditKey = Attr.Manager;

    /// <summary>The edit key that replaces the text of the row's <see cref="ColumnTargetKind.Groups"/> cell (the <em>Grupy</em> cell, 2.7).</summary>
    public const string GroupsEditKey = Attr.MemberOf;

    /// <summary>Values looked up per batched <c>LdapFilter.Or</c> (<c>PlanValidation.LookupBatchSize</c>, 2.4).</summary>
    public const int LookupBatchSize = 50;

    /// <summary>
    /// More unknown values of one namespace than this are read with one paged search of the whole namespace
    /// (<c>(sAMAccountName=*)</c> …) instead of batched equality lookups (2.4: the budget is an upper bound; rule 6).
    /// </summary>
    public const int FullListingThreshold = 1000;

    /// <summary>Manager values per search (each value is 2–3 clauses: at most 50 clauses, 2.5).</summary>
    public const int ManagerBatchSize = 12;

    /// <summary>The number of candidates a counter tries per row before <see cref="BulkCreateIssueCodes.CounterExhausted"/> (2.4).</summary>
    public const int MaxCounterCandidates = 100_000;

    /// <summary>The highest disambiguator (<c>jan.kowalski99</c>) before a collision is an error (2.4).</summary>
    public const int MaxDisambiguator = 99;

    /// <summary>The row count above which the RID pool of the DC is checked (2.5).</summary>
    public const int RidPoolCheckRows = 100;

    private const string SentinelText = CreationPatternTokens.NumberSentinel;

    /// <summary>The unique attributes of 2.4 (the object name is <c>cn</c> = the RDN value).</summary>
    private static readonly string[] UniqueAttributes = [Attr.SamAccountName, Attr.UserPrincipalName, Attr.Mail, Attr.Cn];

    /// <summary>The order in which the primary counter attribute is chosen (2.4); the others follow by name.</summary>
    private static readonly string[] PrimaryCounterOrder = [Attr.SamAccountName, Attr.Cn, Attr.UserPrincipalName, Attr.Mail];

    /// <summary>The fixed check-grid columns of <see cref="BulkCreateComputation.Attributes"/> (2.5).</summary>
    private static readonly string[] GridOrder =
        [Attr.Cn, Attr.SamAccountName, Attr.UserPrincipalName, Attr.Mail, Attr.GivenName, Attr.Sn, Attr.Initials, Attr.DisplayName];

    private readonly PasswordGenerator _passwords = passwords ?? throw new ArgumentNullException(nameof(passwords));
    private readonly TimeProvider _time = time ?? throw new ArgumentNullException(nameof(time));

    public Task<BulkCreateComputation> ComputeAsync(
        IDirectorySession session,
        BulkCreateInput input,
        BulkCreateLookups lookups,
        IProgress<int>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(lookups);
        return new ComputeRun(this, session, input, lookups, cancellationToken).ExecuteAsync(progress);
    }

    public Task<CounterPreview> PreviewCounterAsync(
        IDirectorySession session,
        BulkCreateInput input,
        BulkCreateLookups lookups,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(lookups);
        return new ComputeRun(this, session, input, lookups, cancellationToken).PreviewCounterAsync();
    }

    /// <summary>The generator and the clock (for tests that check what the planner was built with).</summary>
    internal (PasswordGenerator Passwords, TimeProvider Time) Dependencies => (_passwords, _time);

    /// <summary>The longest value of a unique attribute (counter values over it are <see cref="BulkCreateIssueCodes.CounterTooLong"/>, 2.4).</summary>
    internal static int LimitOf(string attribute, ObjectKind kind) => attribute switch
    {
        _ when Is(attribute, Attr.SamAccountName) => CreationNames.MaxSamAccountName(kind),
        _ when Is(attribute, Attr.Cn) => CreationNames.MaxCn,
        _ when Is(attribute, Attr.UserPrincipalName) => CreationNames.MaxUpn,
        _ when Is(attribute, Attr.Mail) => CreationNames.MaxMailLocalPart,
        _ => int.MaxValue,
    };

    /// <summary>The length the limit of <see cref="LimitOf"/> applies to (the local part of a mail address).</summary>
    internal static int LimitedLength(string attribute, string value) =>
        Is(attribute, Attr.Mail) && value.LastIndexOf('@') is var at and >= 0 ? at : value.Length;

    private static bool Is(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private static bool IsUnique(string attribute) => UniqueAttributes.Contains(attribute, StringComparer.OrdinalIgnoreCase);

    private static bool HasSamAccountName(ObjectKind kind) => kind is ObjectKind.User or ObjectKind.InetOrgPerson or ObjectKind.Group or ObjectKind.Computer;

    private static bool IsUserKind(ObjectKind kind) => kind is ObjectKind.User or ObjectKind.InetOrgPerson;

    private static string Text(int value) => value.ToString(CultureInfo.InvariantCulture);

    private static ValidationIssue Error(string code, string message, string? attribute = null, params string[] arguments) =>
        new(IssueSeverity.Error, code, message, attribute) { Arguments = arguments };

    private static ValidationIssue Warning(string code, string message, string? attribute = null, params string[] arguments) =>
        new(IssueSeverity.Warning, code, message, attribute) { Arguments = arguments };

    private static ValidationIssue Info(string code, string message, string? attribute = null, params string[] arguments) =>
        new(IssueSeverity.Info, code, message, attribute) { Arguments = arguments };

    /// <summary>A computed value of a row while it is being computed (a class: it is never printed).</summary>
    private sealed class Draft(string attribute, IReadOnlyList<string> values, BulkValueSource source)
    {
        public string Attribute { get; } = attribute;

        public IReadOnlyList<string> Values { get; } = values;

        public BulkValueSource Source { get; } = source;

        public bool Sanitized { get; init; }

        public bool Truncated { get; init; }

        public bool Adjusted { get; set; }

        /// <summary>The value depends on the counter number in this row.</summary>
        public bool IsCounter { get; init; }

        public string First => Values.Count > 0 ? Values[0] : string.Empty;

        public bool HasValue => Values.Count > 0 && Values[0].Length > 0;

        /// <summary>A column, an edited cell or a frozen value: the operator's (never changed, never disambiguated).</summary>
        public bool IsOperatorValue => Source is BulkValueSource.Column or BulkValueSource.Edited or BulkValueSource.Frozen;

        public bool IsRuleValue => Source is BulkValueSource.Pattern or BulkValueSource.Fixed;

        public BulkValue ToValue() => new(Attribute, Values, Source) { Adjusted = Adjusted, Sanitized = Sanitized, Truncated = Truncated };

        public override string ToString() => $"Draft {Attribute} ({Source})";
    }
}
