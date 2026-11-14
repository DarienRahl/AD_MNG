using AdMng.Core.ActiveDirectory;
using AdMng.Core.Audit;
using AdMng.Core.Batch;
using AdMng.Core.Batch.Creation;
using AdMng.Core.Directory;
using AdMng.Core.Model;
using AdMng.Core.Security;
using AdMng.Testing;

namespace AdMng.Core.Tests.Batch.Creation;

/// <summary>
/// The fixture of the B2 tests (docs/dev/phase3-bulk-create.md 2.11): the sample company with the bulk-create sample (SAM rules
/// on, the bulk-create schema), the planner on the fake clock and helpers that build tables, inputs and rule sets. Tables are
/// headerless by default (columns A, B …): a header is only needed where a test is about headers.
/// </summary>
internal sealed class BulkCreateTestBed
{
    /// <summary>The skip reason of the tests that need the bodies of the other wave-1 agent (B1), 2.13.</summary>
    public const string Wave1 = "F3 wave 1 merge";

    public BulkCreateTestBed(bool samRules = true, TimeProvider? time = null)
    {
        Time = time as ManualTimeProvider ?? new ManualTimeProvider();
        Builder = new FakeDirectoryBuilder(timeProvider: Time, schema: FakeSchema.Create(bulkCreate: true));
        Company = Builder.AddSampleCompany();
        Sample = Builder.AddBulkCreateSample(Company, samRules);
        Planner = new BulkCreatePlanner(new PasswordGenerator(), Time);
    }

    public ManualTimeProvider Time { get; }

    public FakeDirectoryBuilder Builder { get; }

    public SampleCompany Company { get; }

    public BulkCreateSample Sample { get; }

    public FakeDirectorySession Session => Builder.Session;

    public BulkCreatePlanner Planner { get; }

    public BulkCreateLookups Lookups { get; private set; } = new();

    public InMemoryAuditLog Audit { get; } = new();

    public static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>The numbered profile of 2.6 (<em>Konta numerowane</em>): kd{$nr:201}, UPN and mail from it, cn and displayName from the name.</summary>
    public static IReadOnlyList<AttributeRule> NumberedRules { get; } =
    [
        new(Attr.SamAccountName, "kd{$nr:201}"),
        new(Attr.UserPrincipalName, "{sAMAccountName}"),
        new(Attr.Mail, "{sAMAccountName}@{$mailDomain}"),
        new(Attr.Cn, "{givenName} {sn}"),
        new(Attr.DisplayName, "{givenName} {sn}"),
    ];

    /// <summary>The standard name-based profile of 2.6.</summary>
    public static IReadOnlyList<AttributeRule> StandardRules { get; } =
    [
        new(Attr.Cn, "{givenName} {sn}"),
        new(Attr.DisplayName, "{givenName} {sn}"),
        new(Attr.SamAccountName, "{givenName|sam|lower}.{sn|sam|lower}"),
        new(Attr.UserPrincipalName, "{sAMAccountName}"),
    ];

    /// <summary>A headerless table (columns A, B …, every header empty) with rows numbered from 1.</summary>
    public static InputTable Table(params string[][] rows)
    {
        var width = rows.Length == 0 ? 0 : rows.Max(r => r.Length);
        var columns = Enumerable.Range(0, width).Select(i => new TableColumn(i, TableTextReader.ColumnLetter(i), string.Empty)).ToList();
        return new InputTable(columns, [.. rows.Select((r, i) => new TableRow(i + 1, r))], HasHeader: false, TableSource.Typed, []);
    }

    /// <summary>A table with headers.</summary>
    public static InputTable TableWithHeaders(string[] headers, params string[][] rows)
    {
        var columns = headers.Select((h, i) => new TableColumn(i, TableTextReader.ColumnLetter(i), h)).ToList();
        return new InputTable(columns, [.. rows.Select((r, i) => new TableRow(i + 1, r))], HasHeader: true, TableSource.ClipboardText, []);
    }

    /// <summary>Given name in column A, surname in column B (the usual mapping of these tests).</summary>
    public static IReadOnlyList<ColumnMapping> NameMappings { get; } =
    [
        new(0, ColumnTargetKind.Attribute, Attr.GivenName),
        new(1, ColumnTargetKind.Attribute, Attr.Sn),
    ];

    /// <summary>Rows of given names and surnames.</summary>
    public static InputTable People(params (string GivenName, string Surname)[] people) =>
        Table([.. people.Select(p => new[] { p.GivenName, p.Surname })]);

    /// <summary>The generated rows of <see cref="AdMng.Testing.BulkCreate.ExcelSamples.Rows"/> as a headerless table (given name, surname, e-mail, mobile, department).</summary>
    public static InputTable GeneratedRows(int count)
    {
        var lines = AdMng.Testing.BulkCreate.ExcelSamples.Rows(count).Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        return Table([.. lines.Skip(1).Select(l => l.Split('\t'))]);
    }

    public BulkCreateInput Input(
        InputTable table,
        IReadOnlyList<AttributeRule>? rules = null,
        IReadOnlyList<ColumnMapping>? mappings = null,
        Func<BulkCreateRules, BulkCreateRules>? configure = null,
        BulkAccountOptions? options = null,
        ObjectKind kind = ObjectKind.User,
        string? container = null)
    {
        var ruleSet = new BulkCreateRules
        {
            Kind = kind,
            Attributes = rules ?? NumberedRules,
            UpnSuffix = "contoso.com",
            MailDomain = "contoso.com",
        };
        return new BulkCreateInput
        {
            ContainerDn = container ?? Sample.NewEmployeesOu,
            Table = table,
            Mappings = mappings ?? NameMappings,
            Rules = configure is null ? ruleSet : configure(ruleSet),
            Options = options ?? new BulkAccountOptions { MatchAttributes = [] },
            UpnSuffixes = FakeDirectoryBuilder.BulkCreateUpnSuffixes,
        };
    }

    public Task<BulkCreateComputation> ComputeAsync(BulkCreateInput input, IDirectorySession? session = null) =>
        Planner.ComputeAsync(session ?? Session, input, Lookups, null, Ct);

    /// <summary>Forgets the cached lookups (a new window or <em>Sprawdź ponownie</em>).</summary>
    public void Refresh() => Lookups.Refresh();

    /// <summary>New lookups (e.g. with a forest reader).</summary>
    public void UseLookups(BulkCreateLookups lookups) => Lookups = lookups;

    public Task<BatchResult> ExecuteAsync(ChangePlan plan, IDirectorySession? session = null) =>
        new BatchExecutor(Audit, Time).ExecuteAsync(session ?? Session, plan, new BatchOptions { InitialRetryDelay = TimeSpan.FromMilliseconds(1) }, null, Ct);

    /// <summary>The values of <paramref name="attribute"/> of every row, in row order.</summary>
    public static IReadOnlyList<string?> Values(BulkCreateComputation computation, string attribute) =>
        [.. computation.Rows.Select(r => r.Value(attribute))];

    /// <summary>The searches the planner ran (the operation log starts empty after the sample).</summary>
    public IReadOnlyList<FakeOperation> Searches => [.. Session.Operations.Where(o => o.Kind == FakeOperationKind.Search)];

    /// <summary>An issue of the row with the code (and the attribute when given).</summary>
    public static ValidationIssue? IssueOf(BulkCreateRow row, string code, string? attribute = null) =>
        row.Issues.FirstOrDefault(i => i.Code == code && (attribute is null || string.Equals(i.Attribute, attribute, StringComparison.OrdinalIgnoreCase)));

    /// <summary>Adds a user to the OU <em>Masa</em> of the sample (outside the target container).</summary>
    public string AddUser(string givenName, string surname, IReadOnlyDictionary<string, object?>? attributes = null) =>
        Builder.AddUser(givenName, surname, Company.SalesOu, attributes);
}
