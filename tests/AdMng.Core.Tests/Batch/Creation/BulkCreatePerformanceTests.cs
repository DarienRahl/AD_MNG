using System.Diagnostics;
using AdMng.Core.ActiveDirectory;
using AdMng.Core.Batch.Creation;

namespace AdMng.Core.Tests.Batch.Creation;

/// <summary>
/// 5 000 rows against a fake with 10 000 existing users (docs/dev/phase3-bulk-create.md 2.4, 2.11): the counter profile and the
/// name-based profile each compute within 10 s and within the query budget of 2.4, and the plan of 5 000 accounts is built
/// within 2 s.
/// </summary>
[Collection(TimingCollection.Name)]
public sealed class BulkCreatePerformanceTests
{
    private const int Rows = 5000;
    private const int Existing = 10_000;
    private static readonly TimeSpan ComputeBudget = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan PlanBudget = TimeSpan.FromSeconds(2);

    /// <summary>The numbered profile with the account name as <c>cn</c> (the generated names repeat every 400 rows; the disambiguation of names is measured separately).</summary>
    private static readonly IReadOnlyList<AttributeRule> Numbered =
    [
        new(Attr.SamAccountName, "kd{$nr:201}"),
        new(Attr.UserPrincipalName, "{sAMAccountName}"),
        new(Attr.Mail, "{sAMAccountName}@{$mailDomain}"),
        new(Attr.Cn, "{sAMAccountName}"),
        new(Attr.DisplayName, "{givenName} {sn}"),
    ];

    private readonly BulkCreateTestBed _bed = new();

    public BulkCreatePerformanceTests()
    {
        var ou = _bed.Builder.AddOu("Masa", _bed.Company.CompanyOu);
        for (var i = 0; i < Existing; i++)
        {
            _bed.Session.Seed($"CN=Pracownik {i:D5},{ou}", new Dictionary<string, object?>
            {
                [Attr.ObjectClass] = new[] { "top", "person", "organizationalPerson", "user" },
                [Attr.SamAccountName] = $"pracownik{i:D5}",
                [Attr.UserPrincipalName] = $"pracownik{i:D5}@contoso.test",
                [Attr.Mail] = $"pracownik.{i:D5}@contoso.test",
                [Attr.GivenName] = "Pracownik",
                [Attr.Sn] = $"Numer {i:D5}",
                [Attr.SamAccountType] = SamAccountType.UserObject,
            });
        }

        _bed.Session.ClearOperations();
    }

    [Fact]
    public async Task CounterProfile_5000Rows_WithinTheBudget_AndOneQueryPerPrefix()
    {
        var input = _bed.Input(BulkCreateTestBed.GeneratedRows(Rows), Numbered, options: new BulkAccountOptions { MatchAttributes = [], GroupDns = [_bed.Sample.GroupNew] });

        var watch = Stopwatch.StartNew();
        var computation = await _bed.ComputeAsync(input);
        watch.Stop();

        Assert.True(watch.Elapsed < ComputeBudget, $"compute took {watch.Elapsed}");
        Assert.Equal(Rows, computation.Rows.Count(r => r.CounterNumber is not null));
        Assert.Equal("kd202", computation.Rows[0].Value(Attr.SamAccountName));
        Assert.Equal(Rows, computation.Rows.Select(r => r.Value(Attr.SamAccountName)).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        var operations = _bed.Session.Operations.Count;
        Assert.True(operations <= 16, $"{operations} reads: {string.Join(" | ", _bed.Session.Operations.Select(o => o.Search?.Filter ?? o.Kind.ToString()))}");
    }

    [Fact]
    public async Task CounterProfile_WithTheExistingAccountCheck_OneMoreRead()
    {
        var input = _bed.Input(BulkCreateTestBed.GeneratedRows(Rows), Numbered, options: new BulkAccountOptions());

        var watch = Stopwatch.StartNew();
        var computation = await _bed.ComputeAsync(input);
        watch.Stop();

        Assert.True(watch.Elapsed < ComputeBudget, $"compute took {watch.Elapsed}");
        Assert.Equal(Rows, computation.Rows.Count);
        Assert.True(_bed.Session.Operations.Count <= 17, $"{_bed.Session.Operations.Count} reads");
        Assert.Contains(computation.Rows, r => r.MatchDns.Count == 0);
    }

    [Fact]
    public async Task NameBasedProfile_5000Rows_WithinTheBudget()
    {
        var rules = new List<AttributeRule>(BulkCreateTestBed.StandardRules) { new(Attr.Mail, "{sAMAccountName}@{$mailDomain}") };
        var input = _bed.Input(BulkCreateTestBed.GeneratedRows(Rows), rules, configure: r => r with { Collisions = CollisionStrategy.MarkError });

        var watch = Stopwatch.StartNew();
        var computation = await _bed.ComputeAsync(input);
        watch.Stop();

        Assert.True(watch.Elapsed < ComputeBudget, $"compute took {watch.Elapsed}");
        Assert.Equal(Rows, computation.Rows.Count);
        Assert.True(_bed.Session.Operations.Count <= (5 * ((Rows + 49) / 50)) + 2 + 16, $"{_bed.Session.Operations.Count} reads");
    }

    [Fact]
    public async Task NameBasedProfile_WithDisambiguation_5000Rows_WithinTheBudget()
    {
        var rules = new List<AttributeRule>(BulkCreateTestBed.StandardRules) { new(Attr.Mail, "{sAMAccountName}@{$mailDomain}") };
        var input = _bed.Input(BulkCreateTestBed.GeneratedRows(Rows), rules);

        var watch = Stopwatch.StartNew();
        var computation = await _bed.ComputeAsync(input);
        watch.Stop();

        Assert.True(watch.Elapsed < ComputeBudget, $"compute took {watch.Elapsed}");
        Assert.Equal(Rows, computation.Rows.Select(r => r.Value(Attr.SamAccountName)).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.DoesNotContain(computation.Rows, r => r.Status == BulkRowStatus.Error);
    }

    [Fact]
    public async Task BuildPlan_5000Accounts_WithinTheBudget()
    {
        var computation = await _bed.ComputeAsync(_bed.Input(BulkCreateTestBed.GeneratedRows(Rows), Numbered,
            options: new BulkAccountOptions { MatchAttributes = [], GroupDns = [_bed.Sample.GroupNew] }));

        var watch = Stopwatch.StartNew();
        var plan = _bed.Planner.BuildPlan(computation, null, "Utworzenie kont");
        watch.Stop();

        Assert.True(watch.Elapsed < PlanBudget, $"BuildPlan took {watch.Elapsed}");
        Assert.Equal(Rows, plan.ExecutableCount);
    }
}
