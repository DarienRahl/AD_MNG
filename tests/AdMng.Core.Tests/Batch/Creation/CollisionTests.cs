using AdMng.Core.ActiveDirectory;
using AdMng.Core.Batch.Creation;
using AdMng.Core.Model;

namespace AdMng.Core.Tests.Batch.Creation;

/// <summary>
/// Uniqueness and collisions (docs/dev/phase3-bulk-create.md 2.4) on the bulk-create sample: the disambiguation of rule roots
/// (AppendNumber), errors for columns, edits, computers, counters and MarkError, the object name in the container, deleted
/// objects through lastKnownParent, the implicit UPN, frozen values, possible existing accounts and the mail domain checks.
/// </summary>
public sealed class CollisionTests
{
    private readonly BulkCreateTestBed _bed = new();

    private static readonly IReadOnlyList<AttributeRule> StandardWithMail =
        [.. BulkCreateTestBed.StandardRules, new AttributeRule(Attr.Mail, "{sAMAccountName}@{$mailDomain}")];

    private static Func<BulkCreateRules, BulkCreateRules> MarkError => r => r with { Collisions = CollisionStrategy.MarkError };

    private static List<ColumnMapping> NamesAnd(int column, string attribute) =>
        [.. BulkCreateTestBed.NameMappings, new ColumnMapping(column, ColumnTargetKind.Attribute, attribute)];

    [Fact]
    public async Task ATakenLogin_GetsANumber_TheUpnAndTheMailFollow()
    {
        var computation = await _bed.ComputeAsync(_bed.Input(BulkCreateTestBed.People(("Jan", "Kowalski")), StandardWithMail));

        var row = Assert.Single(computation.Rows);
        Assert.Equal("jan.kowalski2", row.Value(Attr.SamAccountName));
        Assert.Equal("jan.kowalski2@contoso.com", row.Value(Attr.UserPrincipalName));
        Assert.Equal("jan.kowalski2@contoso.com", row.Value(Attr.Mail));
        Assert.All(row.Values.Where(v => v.Attribute is Attr.SamAccountName or Attr.UserPrincipalName or Attr.Mail), v => Assert.True(v.Adjusted));
        Assert.NotNull(BulkCreateTestBed.IssueOf(row, BulkCreateIssueCodes.Disambiguated, Attr.SamAccountName));
        Assert.Equal(BulkRowStatus.Warning, row.Status);
        Assert.False(row.Values.Single(v => v.Attribute == Attr.DisplayName).Adjusted);
    }

    [Fact]
    public async Task AContactsAddress_BumpsTheLoginRoot_SoLoginAndAddressStayEqual()
    {
        _bed.Builder.AddContact("Anna", "Nowak (zewnętrzna)", _bed.Company.CompanyOu, new Dictionary<string, object?> { [Attr.Mail] = "anna.nowak@contoso.com" });

        var computation = await _bed.ComputeAsync(_bed.Input(BulkCreateTestBed.People(("Anna", "Nowak")), StandardWithMail));

        var row = Assert.Single(computation.Rows);
        Assert.Equal(("anna.nowak2", "anna.nowak2@contoso.com"), (row.Value(Attr.SamAccountName), row.Value(Attr.Mail)));
    }

    [Fact]
    public async Task TheSameNameTwice_TheSecondCnGetsTheNumber_TheDisplayNameStays()
    {
        var computation = await _bed.ComputeAsync(_bed.Input(BulkCreateTestBed.People(("Łucja", "Żak"), ("Łucja", "Żak"))));

        Assert.Equal(["Łucja Żak", "Łucja Żak (2)"], BulkCreateTestBed.Values(computation, Attr.Cn));
        Assert.Equal(["Łucja Żak", "Łucja Żak"], BulkCreateTestBed.Values(computation, Attr.DisplayName));
        Assert.Equal(["kd202", "kd204"], BulkCreateTestBed.Values(computation, Attr.SamAccountName));
    }

    [Fact]
    public async Task AGroupNamedLikeAChildOu_CollidesOnTheObjectName()
    {
        var table = BulkCreateTestBed.Table(["Księgowość"]);
        var input = _bed.Input(table, [new AttributeRule(Attr.SamAccountName, "{cn}")], [new ColumnMapping(0, ColumnTargetKind.Attribute, Attr.Cn)], kind: ObjectKind.Group);

        var computation = await _bed.ComputeAsync(input);

        var row = Assert.Single(computation.Rows);
        Assert.Equal(BulkRowStatus.Error, row.Status);
        var issue = BulkCreateTestBed.IssueOf(row, BulkCreateIssueCodes.DuplicateInDirectory, Attr.Cn)!;
        Assert.Equal(["Księgowość"], issue.Arguments);
        Assert.Equal("Księgowość", row.Value(Attr.Cn));
    }

    [Fact]
    public async Task ADeletedObjectOfTheContainer_FoundThroughLastKnownParent_OnlyWarns()
    {
        var rules = new AttributeRule[] { new(Attr.Cn, "{givenName} {sn}"), new(Attr.SamAccountName, "kd{$nr:301}") };

        var computation = await _bed.ComputeAsync(_bed.Input(BulkCreateTestBed.People(("Jan", "Kowalski")), rules));

        var row = Assert.Single(computation.Rows);
        Assert.Equal(BulkRowStatus.Warning, row.Status);
        Assert.Equal("Jan Kowalski", row.Value(Attr.Cn));
        Assert.NotNull(BulkCreateTestBed.IssueOf(row, BulkCreateIssueCodes.NameInRecycleBin, Attr.Cn));
        Assert.NotNull(row.DistinguishedName);
        Assert.Contains(_bed.Searches, s => s.Search!.IncludeDeleted && s.Search.Filter.StartsWith("(lastKnownParent=", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RecycledObjects_AreIgnored()
    {
        _bed.Session.Recycle(_bed.Sample.DeletedJanKowalski);
        var rules = new AttributeRule[] { new(Attr.Cn, "{givenName} {sn}"), new(Attr.SamAccountName, "kd{$nr:301}") };

        var computation = await _bed.ComputeAsync(_bed.Input(BulkCreateTestBed.People(("Jan", "Kowalski")), rules));

        Assert.Equal(BulkRowStatus.Ok, Assert.Single(computation.Rows).Status);
    }

    [Fact]
    public async Task ImplicitUpn_ALoginAgainstAnExistingUpn()
    {
        _bed.AddUser("Inny", "Użytkownik", new Dictionary<string, object?> { [Attr.SamAccountName] = "inny", [Attr.UserPrincipalName] = "lucja.zak@contoso.test" });

        var computation = await _bed.ComputeAsync(_bed.Input(BulkCreateTestBed.People(("Łucja", "Żak")), configure: MarkError, rules: BulkCreateTestBed.StandardRules));

        var row = Assert.Single(computation.Rows);
        Assert.Equal(BulkRowStatus.Error, row.Status);
        Assert.Equal(["Inny Użytkownik"], BulkCreateTestBed.IssueOf(row, BulkCreateIssueCodes.ImplicitUpnClash, Attr.SamAccountName)!.Arguments);
        Assert.Equal("lucja.zak@contoso.com", row.Value(Attr.UserPrincipalName));
    }

    [Fact]
    public async Task ImplicitUpn_AUpnPrefixAgainstAnExistingLogin()
    {
        var rules = new AttributeRule[]
        {
            new(Attr.SamAccountName, "n{$column:A}"),
            new(Attr.UserPrincipalName, "kd{$column:A}"),
            new(Attr.Cn, "Konto {$column:A}"),
        };
        var input = _bed.Input(BulkCreateTestBed.Table(["201"]), rules, [], configure: r => r with { UpnSuffix = "contoso.test", Collisions = CollisionStrategy.MarkError });

        var computation = await _bed.ComputeAsync(input);

        var row = Assert.Single(computation.Rows);
        Assert.Equal("kd201@contoso.test", row.Value(Attr.UserPrincipalName));
        Assert.Equal(["Adam Nowicki"], BulkCreateTestBed.IssueOf(row, BulkCreateIssueCodes.ImplicitUpnClash, Attr.UserPrincipalName)!.Arguments);
    }

    [Fact]
    public async Task Computers_AreNeverDisambiguated()
    {
        var input = _bed.Input(BulkCreateTestBed.Table(["waw-pc001"]), [new AttributeRule(Attr.SamAccountName, "{cn}")],
            [new ColumnMapping(0, ColumnTargetKind.Attribute, Attr.Cn)], kind: ObjectKind.Computer);

        var computation = await _bed.ComputeAsync(input);

        var row = Assert.Single(computation.Rows);
        Assert.Equal(("WAW-PC001", "WAW-PC001$"), (row.Value(Attr.Cn), row.Value(Attr.SamAccountName)));
        Assert.Equal(BulkRowStatus.Error, row.Status);
        Assert.NotNull(BulkCreateTestBed.IssueOf(row, BulkCreateIssueCodes.DuplicateInDirectory, Attr.SamAccountName));
        Assert.DoesNotContain(row.Values, v => v.Adjusted);
    }

    [Fact]
    public async Task AColumnValue_IsNeverChanged_ATakenOneIsAnError()
    {
        var table = BulkCreateTestBed.Table(["Jan", "Kowalski", "jan.kowalski"]);

        var computation = await _bed.ComputeAsync(_bed.Input(table, BulkCreateTestBed.StandardRules, NamesAnd(2, Attr.SamAccountName)));

        var row = Assert.Single(computation.Rows);
        Assert.Equal("jan.kowalski", row.Value(Attr.SamAccountName));
        Assert.Equal(["Jan Kowalski"], BulkCreateTestBed.IssueOf(row, BulkCreateIssueCodes.DuplicateInDirectory, Attr.SamAccountName)!.Arguments);
        Assert.DoesNotContain(row.Values, v => v.Adjusted);
    }

    [Fact]
    public async Task AnEditedValue_IsNeverChanged_ATakenOneIsAnError()
    {
        var edits = new Dictionary<int, IReadOnlyDictionary<string, string>> { [1] = new Dictionary<string, string> { [Attr.SamAccountName] = " jan.kowalski " } };

        var computation = await _bed.ComputeAsync(_bed.Input(BulkCreateTestBed.People(("Łucja", "Żak")), BulkCreateTestBed.StandardRules) with { Edits = edits });

        var row = Assert.Single(computation.Rows);
        Assert.Equal("jan.kowalski", row.Value(Attr.SamAccountName));
        Assert.Equal(BulkValueSource.Edited, row.Values.Single(v => v.Attribute == Attr.SamAccountName).Source);
        Assert.NotNull(BulkCreateTestBed.IssueOf(row, BulkCreateIssueCodes.DuplicateInDirectory, Attr.SamAccountName));
        Assert.Equal("jan.kowalski@contoso.com", row.Value(Attr.UserPrincipalName));
    }

    [Fact]
    public async Task TwoRowsWithTheSameColumnValue_AreBothErrors()
    {
        var table = BulkCreateTestBed.Table(["Łucja", "Żak", "nowy.login"], ["Zofia", "Bąk", "nowy.login"]);

        var computation = await _bed.ComputeAsync(_bed.Input(table, BulkCreateTestBed.StandardRules, NamesAnd(2, Attr.SamAccountName)));

        Assert.All(computation.Rows, r => Assert.Equal(BulkRowStatus.Error, r.Status));
        Assert.Equal(["2"], BulkCreateTestBed.IssueOf(computation.Rows[0], BulkCreateIssueCodes.DuplicateRow, Attr.SamAccountName)!.Arguments);
        Assert.Equal(["1"], BulkCreateTestBed.IssueOf(computation.Rows[1], BulkCreateIssueCodes.DuplicateRow, Attr.SamAccountName)!.Arguments);
    }

    [Fact]
    public async Task MarkError_ATakenRuleValue_IsAnError()
    {
        var computation = await _bed.ComputeAsync(_bed.Input(BulkCreateTestBed.People(("Jan", "Kowalski")), BulkCreateTestBed.StandardRules, configure: MarkError));

        var row = Assert.Single(computation.Rows);
        Assert.Equal("jan.kowalski", row.Value(Attr.SamAccountName));
        Assert.Equal(["Jan Kowalski"], BulkCreateTestBed.IssueOf(row, BulkCreateIssueCodes.DuplicateInDirectory, Attr.SamAccountName)!.Arguments);
        Assert.Equal(BulkRowStatus.Error, row.Status);
    }

    [Fact]
    public async Task MarkError_TheSecondRowWithTheSameRuleValue()
    {
        var computation = await _bed.ComputeAsync(_bed.Input(BulkCreateTestBed.People(("Łucja", "Żak"), ("Łucja", "Żak")), BulkCreateTestBed.StandardRules, configure: MarkError));

        Assert.Equal(BulkRowStatus.Ok, computation.Rows[0].Status);
        Assert.Equal(BulkRowStatus.Error, computation.Rows[1].Status);
        Assert.Equal(["1"], BulkCreateTestBed.IssueOf(computation.Rows[1], BulkCreateIssueCodes.DuplicateRow, Attr.SamAccountName)!.Arguments);
        Assert.Equal(["1"], BulkCreateTestBed.IssueOf(computation.Rows[1], BulkCreateIssueCodes.DuplicateRow, Attr.Cn)!.Arguments);
    }

    [Fact]
    public async Task FrozenValues_TurnANewCollisionIntoAnError_WithoutRenumbering()
    {
        var input = _bed.Input(BulkCreateTestBed.People(("Łucja", "Żak"), ("Zofia", "Bąk")));
        var check = await _bed.ComputeAsync(input);
        _bed.AddUser("Ktoś", "Szybszy", new Dictionary<string, object?> { [Attr.SamAccountName] = "kd202" });
        _bed.Refresh();

        var frozen = check.Rows.ToDictionary(r => r.Number, r => r.Values);
        var plan = await _bed.ComputeAsync(input with { FrozenValues = frozen, PinnedNumbers = check.Numbers });

        Assert.Equal(["kd202", "kd204"], BulkCreateTestBed.Values(plan, Attr.SamAccountName));
        Assert.Equal([202, 204], plan.Rows.Select(r => r.CounterNumber!.Value));
        Assert.Equal(BulkRowStatus.Error, plan.Rows[0].Status);
        Assert.Equal(["Ktoś Szybszy"], BulkCreateTestBed.IssueOf(plan.Rows[0], BulkCreateIssueCodes.DuplicateInDirectory, Attr.SamAccountName)!.Arguments);
        Assert.Equal(BulkRowStatus.Ok, plan.Rows[1].Status);
        Assert.All(plan.Rows.SelectMany(r => r.Values), v => Assert.Equal(BulkValueSource.Frozen, v.Source));
    }

    [Theory]
    [InlineData(Attr.EmployeeId, "E-1001")]
    [InlineData(Attr.Mobile, "+48 600 100 200")]
    [InlineData(Attr.Mail, "ewa.wisniewska@contoso.test")]
    public async Task PossibleExistingAccount_ByTheFirstMappedIdentifier(string attribute, string value)
    {
        var table = BulkCreateTestBed.Table(["Ewa", "Wiśniewska", value]);
        var input = _bed.Input(table, mappings: NamesAnd(2, attribute), options: new BulkAccountOptions());

        var computation = await _bed.ComputeAsync(input);

        var row = Assert.Single(computation.Rows);
        Assert.Equal(["Ewa Wiśniewska"], BulkCreateTestBed.IssueOf(row, BulkCreateIssueCodes.PossibleExistingAccount, attribute)!.Arguments);
        Assert.Equal([_bed.Sample.EwaWisniewska], row.MatchDns);
        Assert.Single(row.Issues, i => i.Code == BulkCreateIssueCodes.PossibleExistingAccount);
    }

    [Fact]
    public async Task PossibleExistingAccount_ByGivenNameAndSurname_WithoutAnIdentifier()
    {
        var computation = await _bed.ComputeAsync(_bed.Input(BulkCreateTestBed.People(("Ewa", "Wiśniewska"), ("Ewa", "Nowak")), options: new BulkAccountOptions()));

        Assert.Equal(["Ewa Wiśniewska"], BulkCreateTestBed.IssueOf(computation.Rows[0], BulkCreateIssueCodes.PossibleExistingAccount, Attr.Cn)!.Arguments);
        Assert.Equal(BulkRowStatus.Warning, computation.Rows[0].Status);
        Assert.Equal(BulkRowStatus.Ok, computation.Rows[1].Status);
    }

    [Fact]
    public async Task PossibleExistingAccount_IsOffWithAnEmptyList_AndIgnoresRuleValues()
    {
        var off = await _bed.ComputeAsync(_bed.Input(BulkCreateTestBed.People(("Ewa", "Wiśniewska"))));
        var ruleMail = await _bed.ComputeAsync(_bed.Input(BulkCreateTestBed.People(("Ewa", "Wiśniewska")),
            options: new BulkAccountOptions { MatchAttributes = [Attr.Mail] }));

        Assert.Empty(off.Rows[0].Issues);
        Assert.Empty(ruleMail.Rows[0].Issues);
    }

    [Fact]
    public async Task DuplicateSourceRow_ForARepeatedPaste()
    {
        var table = BulkCreateTestBed.Table(["Łucja", "Żak", "+48 600 999 111"], ["Łucja", "Żak", "+48 600 999 111"], ["Zofia", "Bąk", "+48 600 999 222"]);

        var computation = await _bed.ComputeAsync(_bed.Input(table, mappings: NamesAnd(2, Attr.Mobile), configure: MarkError, options: new BulkAccountOptions()));

        Assert.Equal(["2"], BulkCreateTestBed.IssueOf(computation.Rows[0], BulkCreateIssueCodes.DuplicateSourceRow, Attr.Mobile)!.Arguments);
        Assert.Equal(["1"], BulkCreateTestBed.IssueOf(computation.Rows[1], BulkCreateIssueCodes.DuplicateSourceRow, Attr.Mobile)!.Arguments);
        Assert.Null(BulkCreateTestBed.IssueOf(computation.Rows[2], BulkCreateIssueCodes.DuplicateSourceRow));
    }

    [Theory]
    [InlineData("lucja@gmail.com", true, false)]
    [InlineData("jan@firma.local", true, true)]
    [InlineData("lucja.zak@firma.pl", false, false)]
    [InlineData("lucja.zak@contoso.test", false, false)]
    public async Task MailDomain_OutsideTheForest_OrNotRoutable(string mail, bool notInForest, bool nonRoutable)
    {
        var table = BulkCreateTestBed.Table(["Łucja", "Żak", mail]);

        var computation = await _bed.ComputeAsync(_bed.Input(table, mappings: NamesAnd(2, Attr.Mail)));

        var row = Assert.Single(computation.Rows);
        Assert.Equal(notInForest, BulkCreateTestBed.IssueOf(row, BulkCreateIssueCodes.MailDomainNotInForest, Attr.Mail) is not null);
        Assert.Equal(nonRoutable, BulkCreateTestBed.IssueOf(row, BulkCreateIssueCodes.NonRoutableMailDomain, Attr.Mail) is not null);
    }

    [Fact]
    public async Task ManyRowsWithTheSameNames_AreDisambiguated_InAFewLookups_NotOnePerRow()
    {
        string[] givenNames = ["Anna", "Jan", "Piotr", "Ewa", "Marek", "Ola", "Kasia", "Tomasz", "Adam", "Zofia"];
        var people = Enumerable.Range(0, 200).Select(i => (givenNames[i % givenNames.Length], "Nowak")).ToArray();

        var computation = await _bed.ComputeAsync(_bed.Input(BulkCreateTestBed.People(people), StandardWithMail));

        Assert.DoesNotContain(computation.Rows, r => r.Status == BulkRowStatus.Error);
        Assert.Equal(200, computation.Rows.Select(r => r.Value(Attr.SamAccountName)).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Equal(["anna.nowak", "anna.nowak2", "anna.nowak20"], new[] { 0, 10, 190 }.Select(i => computation.Rows[i].Value(Attr.SamAccountName)));
        Assert.Equal("anna.nowak20@contoso.com", computation.Rows[190].Value(Attr.Mail));
        Assert.Equal("Anna Nowak (20)", computation.Rows[190].Value(Attr.Cn));

        // 2.4: 5 × ⌈rows / 50⌉ + 2 lookups plus the collision passes – before, every repeated row asked again per namespace.
        Assert.True(_bed.Searches.Count <= (5 * 4) + 2 + 16, $"{_bed.Searches.Count} searches");
    }

    [Fact]
    public async Task ACollisionDeeperThanThePrefetch_LooksUpTheNextLevelsTogether()
    {
        foreach (var suffix in new[] { string.Empty, "2", "3", "4", "5", "6", "7", "8", "9" })
        {
            _bed.AddUser("Ewa", "Bąk" + suffix, new Dictionary<string, object?> { [Attr.SamAccountName] = "ewa.bak" + suffix });
        }

        _bed.Session.ClearOperations();
        var computation = await _bed.ComputeAsync(_bed.Input(BulkCreateTestBed.People(("Ewa", "Bąk")), StandardWithMail));

        Assert.Equal("ewa.bak10", Assert.Single(computation.Rows).Value(Attr.SamAccountName));
        var samLookups = _bed.Searches.Count(s => s.Search!.Filter.Contains("(sAMAccountName=ewa.bak", StringComparison.Ordinal));
        Assert.True(samLookups <= 3, $"{samLookups} sAMAccountName lookups: the value, the levels 2–6 and one window for 7–11");
    }

    [Fact]
    public async Task NameBasedLookups_AreBatchedBy50()
    {
        var computation = await _bed.ComputeAsync(_bed.Input(BulkCreateTestBed.GeneratedRows(120), BulkCreateTestBed.StandardRules, configure: MarkError));

        Assert.Equal(120, computation.Rows.Count);
        var samLookups = _bed.Searches.Where(s => s.Search!.Filter.StartsWith("(|(sAMAccountName=", StringComparison.Ordinal)).ToList();
        Assert.Equal(3, samLookups.Count);
        Assert.All(samLookups, s => Assert.True(s.Search!.Filter.Split("(sAMAccountName=").Length - 1 <= 50));
    }
}
