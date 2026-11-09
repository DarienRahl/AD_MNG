using AdMng.Core.ActiveDirectory;
using AdMng.Core.Batch;
using AdMng.Core.Batch.Creation;
using AdMng.Core.Model;
using AdMng.Testing;

namespace AdMng.Core.Tests.Batch.Creation;

/// <summary>
/// The planner of the bulk creation wizard (docs/dev/phase3-bulk-create.md 2.5) on the bulk-create sample: Polish data,
/// managers, groups and their nesting rules, the run checks (container, create right, encrypted channel, password policy,
/// PSOs, RID pool, join owner, forest scope), countries and expiry dates, and the change plan per kind.
/// </summary>
public sealed class BulkCreatePlannerTests
{
    private readonly BulkCreateTestBed _bed = new();

    private static readonly (string, string)[] TwoPeople = [("Łucja", "Żak"), ("Grzegorz", "Brzęczyszczykiewicz")];

    private static List<ColumnMapping> NamesAnd(int column, ColumnTargetKind kind, string? attribute = null) =>
        [.. BulkCreateTestBed.NameMappings, new ColumnMapping(column, kind, attribute)];

    private static ValidationIssue? RunIssue(BulkCreateComputation computation, string code) => computation.Issues.FirstOrDefault(i => i.Code == code);

    // ------------------------------------------------------------------ values

    [Fact]
    public async Task PolishData_KeepsItsDiacritics_IdentifiersAreAscii()
    {
        var computation = await _bed.ComputeAsync(_bed.Input(BulkCreateTestBed.People(("Łucja", "Żak"), ("Grzegorz", "Brzęczyszczykiewicz")), BulkCreateTestBed.StandardRules));

        var row = computation.Rows[0];
        Assert.Equal(("Łucja", "Żak", "Łucja Żak", "Łucja Żak"), (row.Value(Attr.GivenName), row.Value(Attr.Sn), row.Value(Attr.DisplayName), row.Value(Attr.Cn)));
        Assert.Equal(("lucja.zak", "lucja.zak@contoso.com"), (row.Value(Attr.SamAccountName), row.Value(Attr.UserPrincipalName)));
        Assert.StartsWith("grzegorz.brzeczyszcz", computation.Rows[1].Value(Attr.SamAccountName), StringComparison.Ordinal);
        Assert.Equal($"CN=Łucja Żak,{_bed.Sample.NewEmployeesOu}", row.DistinguishedName);
        Assert.Equal(BulkValueSource.Column, row.Values.Single(v => v.Attribute == Attr.GivenName).Source);
        Assert.Equal(BulkValueSource.Pattern, row.Values.Single(v => v.Attribute == Attr.SamAccountName).Source);
        Assert.Equal([Attr.Cn, Attr.SamAccountName, Attr.UserPrincipalName, Attr.Mail, Attr.GivenName, Attr.Sn, Attr.Initials, Attr.DisplayName], computation.Attributes);
        Assert.Equal(_bed.Session.Info.Server, computation.Server);
        Assert.Equal(_bed.Time.GetUtcNow(), computation.ComputedAt);
    }

    [Fact(Skip = BulkCreateTestBed.Wave1)]
    public async Task Examples_TheFinishingOfRuleValues()
    {
        var rules = new AttributeRule[]
        {
            new(Attr.Cn, "{givenName} {sn}"),
            new(Attr.SamAccountName, "{givenName|first:1|sam|lower}{sn|sam|lower}"),
            new(Attr.UserPrincipalName, "{givenName}.{sn}"),
        };

        var computation = await _bed.ComputeAsync(_bed.Input(BulkCreateTestBed.People(("Anna", "Konstantynopolitańczykowa"), ("Grzegorz", "Brzęczyszczykiewicz")), rules));

        Assert.Equal(["akonstantynopolitanc", "gbrzeczyszczykiewicz"], BulkCreateTestBed.Values(computation, Attr.SamAccountName));
        Assert.True(computation.Rows[0].Values.Single(v => v.Attribute == Attr.SamAccountName).Truncated);
        Assert.Equal("Anna.Konstantynopolitanczykowa@contoso.com", computation.Rows[0].Value(Attr.UserPrincipalName));
        Assert.True(computation.Rows[0].Values.Single(v => v.Attribute == Attr.UserPrincipalName).Sanitized);
    }

    [Fact]
    public async Task Precedence_AColumnWinsOverTheRule_UnlessTheRuleOverridesIt()
    {
        var table = BulkCreateTestBed.Table(["Łucja", "Żak", "lucja@firma.pl"], ["Zofia", "Bąk", string.Empty]);
        var mappings = NamesAnd(2, ColumnTargetKind.Attribute, Attr.Mail);
        var overriding = BulkCreateTestBed.NumberedRules.Select(r => r.Attribute == Attr.Mail ? r with { OverridesColumn = true } : r).ToList();

        var columnWins = await _bed.ComputeAsync(_bed.Input(table, mappings: mappings));
        var ruleWins = await _bed.ComputeAsync(_bed.Input(table, overriding, mappings));

        Assert.Equal(["lucja@firma.pl", "kd204@contoso.com"], BulkCreateTestBed.Values(columnWins, Attr.Mail));
        Assert.Equal(["kd202@contoso.com", "kd204@contoso.com"], BulkCreateTestBed.Values(ruleWins, Attr.Mail));
        Assert.Equal(BulkValueSource.Column, columnWins.Rows[0].Values.Single(v => v.Attribute == Attr.Mail).Source);
    }

    [Fact]
    public async Task Description_APipeInTheCell_IsText_NotASecondValue()
    {
        // description is multi-valued in the schema, but AD (SAM) and the fake's SAM rules take one value on a user.
        var table = BulkCreateTestBed.Table(["Łucja", "Żak", "Dział IT | Kraków"]);
        var computation = await _bed.ComputeAsync(_bed.Input(table, mappings: NamesAnd(2, ColumnTargetKind.Attribute, Attr.Description)));

        var row = Assert.Single(computation.Rows);
        Assert.Equal(["Dział IT | Kraków"], row.Values.Single(v => v.Attribute == Attr.Description).Values);
        var result = await _bed.ExecuteAsync(_bed.Planner.BuildPlan(computation, null, "Utworzenie kont"));
        Assert.Equal(ObjectResultStatus.Succeeded, Assert.Single(result.Objects).Status);
        Assert.Equal(["Dział IT | Kraków"], _bed.Session.GetValues(row.DistinguishedName!, Attr.Description));
    }

    [Fact(Skip = BulkCreateTestBed.Wave1)]
    public async Task Precedence_AnEditWinsOverEverything_AndDependentRulesFollow()
    {
        var edits = new Dictionary<int, IReadOnlyDictionary<string, string>> { [1] = new Dictionary<string, string> { [Attr.SamAccountName] = "l.zak" } };

        var computation = await _bed.ComputeAsync(_bed.Input(BulkCreateTestBed.People(TwoPeople), BulkCreateTestBed.StandardRules) with { Edits = edits });

        Assert.Equal(("l.zak", "l.zak@contoso.com"), (computation.Rows[0].Value(Attr.SamAccountName), computation.Rows[0].Value(Attr.UserPrincipalName)));
    }

    [Fact]
    public async Task Upn_APrefixWithAnAt_IsTheWholeUpn()
    {
        var table = BulkCreateTestBed.Table(["Łucja", "Żak", "lucja.zak@firma.pl"]);
        var rules = new AttributeRule[] { new(Attr.Cn, "{givenName} {sn}"), new(Attr.SamAccountName, "kd{$nr:201}"), new(Attr.UserPrincipalName, "{mail}") };

        var computation = await _bed.ComputeAsync(_bed.Input(table, rules, NamesAnd(2, ColumnTargetKind.Attribute, Attr.Mail)));

        Assert.Equal("lucja.zak@firma.pl", computation.Rows[0].Value(Attr.UserPrincipalName));
        Assert.Equal(BulkRowStatus.Ok, computation.Rows[0].Status);
    }

    [Fact]
    public async Task UpnSuffix_OutsideTheForest_Warns_EveryDomainAndUpnSuffixIsAccepted()
    {
        var gmail = await _bed.ComputeAsync(_bed.Input(BulkCreateTestBed.People(TwoPeople), configure: r => r with { UpnSuffix = "gmail.com" }));
        var firma = await _bed.ComputeAsync(_bed.Input(BulkCreateTestBed.People(TwoPeople), configure: r => r with { UpnSuffix = "firma.pl" }));
        var domain = await _bed.ComputeAsync(_bed.Input(BulkCreateTestBed.People(TwoPeople), configure: r => r with { UpnSuffix = "contoso.test" }));

        Assert.All(gmail.Rows, r => Assert.NotNull(BulkCreateTestBed.IssueOf(r, BulkCreateIssueCodes.UpnSuffixNotInForest, Attr.UserPrincipalName)));
        Assert.All(firma.Rows.Concat(domain.Rows), r => Assert.Null(BulkCreateTestBed.IssueOf(r, BulkCreateIssueCodes.UpnSuffixNotInForest)));
        Assert.Equal(["contoso.test"], domain.ForestDomains);
    }

    [Fact]
    public async Task Mandatory_ANameIsNeeded()
    {
        var computation = await _bed.ComputeAsync(_bed.Input(BulkCreateTestBed.People(TwoPeople), [new AttributeRule(Attr.SamAccountName, "kd{$nr:201}")]));

        var row = computation.Rows[0];
        Assert.Equal(BulkRowStatus.Error, row.Status);
        Assert.NotNull(BulkCreateTestBed.IssueOf(row, IssueCodes.MandatoryAttribute, Attr.Cn));
        Assert.Null(row.DistinguishedName);
    }

    [Fact(Skip = BulkCreateTestBed.Wave1)]
    public async Task UnknownAttribute_InARule_IsARunWarning()
    {
        var rules = BulkCreateTestBed.NumberedRules.Append(new AttributeRule(Attr.Description, "{imie}")).ToList();

        var computation = await _bed.ComputeAsync(_bed.Input(BulkCreateTestBed.People(TwoPeople), rules));

        var issue = RunIssue(computation, IssueCodes.TemplateError)!;
        Assert.Equal((IssueSeverity.Warning, Attr.Description), (issue.Severity, issue.Attribute));
        Assert.False(computation.HasRunErrors);
    }

    [Fact]
    public async Task PatternError_IsARunError()
    {
        var rules = BulkCreateTestBed.NumberedRules.Append(new AttributeRule(Attr.Description, "{$dn}")).ToList();

        var computation = await _bed.ComputeAsync(_bed.Input(BulkCreateTestBed.People(TwoPeople), rules));

        Assert.True(computation.HasRunErrors);
        Assert.Equal((IssueSeverity.Error, Attr.Description), (RunIssue(computation, IssueCodes.TemplateError)!.Severity, RunIssue(computation, IssueCodes.TemplateError)!.Attribute));
    }

    public static TheoryData<string> CountryCells() => ["Polska", "POLAND", "pl", "616"];

    [Theory]
    [MemberData(nameof(CountryCells))]
    public async Task Country_ByNameOrCode_WritesTheThreeAttributes(string cell)
    {
        var poland = new CountryValue("PL", "Polska", 616);
        var countries = new Dictionary<string, CountryValue>(StringComparer.OrdinalIgnoreCase) { ["polska"] = poland, ["poland"] = poland, ["pl"] = poland, ["616"] = poland };
        var input = _bed.Input(BulkCreateTestBed.Table(["Łucja", "Żak", cell]), mappings: NamesAnd(2, ColumnTargetKind.Country)) with { Countries = countries };

        var computation = await _bed.ComputeAsync(input);

        var row = computation.Rows[0];
        Assert.Equal(("PL", "Polska", "616"), (row.Value(Attr.Country), row.Value(Attr.CountryName), row.Value(Attr.CountryCode)));
        Assert.Equal(BulkRowStatus.Ok, row.Status);
    }

    [Fact(Skip = BulkCreateTestBed.Wave1)]
    public async Task Country_Unknown_WritesNothing_AndWarns()
    {
        var input = _bed.Input(BulkCreateTestBed.Table(["Łucja", "Żak", "Atlantyda"]), mappings: NamesAnd(2, ColumnTargetKind.Country));

        var row = (await _bed.ComputeAsync(input)).Rows[0];

        Assert.Null(row.Value(Attr.Country));
        Assert.NotNull(BulkCreateTestBed.IssueOf(row, BulkCreateIssueCodes.CountryNotFound));
    }

    [Theory]
    [InlineData("31.12.2026")]
    [InlineData("31.12.2026 ")]
    [InlineData("2026-12-31")]
    [InlineData("46387")]
    [InlineData("46387,5")]
    public async Task Expiry_PerRow_TheEndOfThatDay(string cell)
    {
        var input = _bed.Input(BulkCreateTestBed.Table(["Łucja", "Żak", cell]), mappings: NamesAnd(2, ColumnTargetKind.ExpiresAt));

        var row = (await _bed.ComputeAsync(input)).Rows[0];

        var expected = AdConvert.ToFileTime(new DateTimeOffset(2027, 1, 1, 0, 0, 0, TimeSpan.Zero));
        Assert.Equal(expected.ToString(System.Globalization.CultureInfo.InvariantCulture), row.Value(Attr.AccountExpires));
        Assert.Equal(BulkRowStatus.Ok, row.Status);
    }

    [Theory]
    [InlineData("1.2.2027", 2027, 2, 2)]
    public async Task Expiry_ShortDayAndMonth(string cell, int year, int month, int day)
    {
        var input = _bed.Input(BulkCreateTestBed.Table(["Łucja", "Żak", cell]), mappings: NamesAnd(2, ColumnTargetKind.ExpiresAt));

        var row = (await _bed.ComputeAsync(input)).Rows[0];

        Assert.Equal(AdConvert.ToFileTime(new DateTimeOffset(year, month, day, 0, 0, 0, TimeSpan.Zero)).ToString(System.Globalization.CultureInfo.InvariantCulture), row.Value(Attr.AccountExpires));
    }

    [Theory]
    [InlineData("jutro")]
    [InlineData("31/12/2026")]
    [InlineData("2026-13-01")]
    [InlineData("31.12")] // Excel's dd.MM display (the xlsx built-in format 16), not serial 31 of January 1900
    [InlineData("12.2026")] // a month and a year, not serial 12
    [InlineData("1,5")]
    public async Task Expiry_InvalidDate_IsAnError(string cell)
    {
        var input = _bed.Input(BulkCreateTestBed.Table(["Łucja", "Żak", cell]), mappings: NamesAnd(2, ColumnTargetKind.ExpiresAt));

        var row = (await _bed.ComputeAsync(input)).Rows[0];

        Assert.Equal(BulkRowStatus.Error, row.Status);
        Assert.NotNull(BulkCreateTestBed.IssueOf(row, BulkCreateIssueCodes.InvalidDate, Attr.AccountExpires));
    }

    // ------------------------------------------------------------------ managers

    [Theory]
    [InlineData("pawel.lewandowski")]
    [InlineData("pawel.lewandowski@contoso.test")]
    [InlineData("Paweł Lewandowski")]
    [InlineData("PAWEŁ LEWANDOWSKI")]
    public async Task Manager_BySamAccountNameUpnMailOrDisplayName(string cell)
    {
        var input = _bed.Input(BulkCreateTestBed.Table(["Łucja", "Żak", cell]), mappings: NamesAnd(2, ColumnTargetKind.Manager));

        var row = (await _bed.ComputeAsync(input)).Rows[0];

        Assert.Equal(_bed.Company.PawelLewandowski, row.ManagerDn);
        Assert.Equal(BulkRowStatus.Ok, row.Status);
    }

    [Fact]
    public async Task Manager_ByDistinguishedName()
    {
        var input = _bed.Input(BulkCreateTestBed.Table(["Łucja", "Żak", _bed.Company.JanKowalski]), mappings: NamesAnd(2, ColumnTargetKind.Manager));

        Assert.Equal(_bed.Company.JanKowalski, (await _bed.ComputeAsync(input)).Rows[0].ManagerDn);
    }

    [Fact]
    public async Task Manager_Ambiguous_KeepsTheCandidates()
    {
        var input = _bed.Input(BulkCreateTestBed.Table(["Łucja", "Żak", "Piotr Zieliński"]), mappings: NamesAnd(2, ColumnTargetKind.Manager));

        var row = (await _bed.ComputeAsync(input)).Rows[0];

        Assert.Null(row.ManagerDn);
        Assert.Equal(["2"], BulkCreateTestBed.IssueOf(row, BulkCreateIssueCodes.ManagerAmbiguous, Attr.Manager)!.Arguments);
        Assert.Equal(new HashSet<string> { _bed.Sample.PiotrZielinski1, _bed.Sample.PiotrZielinski2 }, row.ManagerCandidates.ToHashSet());
        Assert.Equal(BulkRowStatus.Warning, row.Status);
    }

    [Fact]
    public async Task Manager_NotFound_OrInTheBatch()
    {
        var table = BulkCreateTestBed.Table(["Łucja", "Żak", "Nikt Taki"], ["Zofia", "Bąk", "Łucja Żak"]);

        var computation = await _bed.ComputeAsync(_bed.Input(table, mappings: NamesAnd(2, ColumnTargetKind.Manager)));

        Assert.NotNull(BulkCreateTestBed.IssueOf(computation.Rows[0], BulkCreateIssueCodes.ManagerNotFound));
        Assert.NotNull(BulkCreateTestBed.IssueOf(computation.Rows[1], BulkCreateIssueCodes.ManagerInBatch));
        Assert.All(computation.Rows, r => Assert.Null(r.ManagerDn));
    }

    [Fact]
    public async Task ManagedBy_OfAGroup_MayBeAGroup()
    {
        var table = BulkCreateTestBed.Table(["GG_Nowy_Zespół", "GG_IT"]);
        var mappings = new ColumnMapping[] { new(0, ColumnTargetKind.Attribute, Attr.Cn), new(1, ColumnTargetKind.Manager) };
        var input = _bed.Input(table, [new AttributeRule(Attr.SamAccountName, "{cn}")], mappings, kind: ObjectKind.Group);

        var row = (await _bed.ComputeAsync(input)).Rows[0];

        Assert.Equal(_bed.Company.GroupIt, row.ManagerDn);
    }

    [Fact]
    public async Task Manager_OfAUser_IsNeverAGroup()
    {
        var input = _bed.Input(BulkCreateTestBed.Table(["Łucja", "Żak", "GG_IT"]), mappings: NamesAnd(2, ColumnTargetKind.Manager));

        Assert.NotNull(BulkCreateTestBed.IssueOf((await _bed.ComputeAsync(input)).Rows[0], BulkCreateIssueCodes.ManagerNotFound));
    }

    // ------------------------------------------------------------------ groups

    [Fact]
    public async Task OptionGroups_ForEveryRow_AMissingOneIsDropped()
    {
        var missing = $"CN=GG_Nieistniejaca,{_bed.Company.GroupsOu}";
        var options = new BulkAccountOptions { MatchAttributes = [], GroupDns = [_bed.Sample.GroupNew, missing, _bed.Sample.GroupNew] };

        var computation = await _bed.ComputeAsync(_bed.Input(BulkCreateTestBed.People(TwoPeople), options: options));

        Assert.All(computation.Rows, r => Assert.Equal([_bed.Sample.GroupNew], r.GroupDns));
        Assert.Equal(["GG_Nieistniejaca"], RunIssue(computation, BulkCreateIssueCodes.GroupNotFound)!.Arguments);
        Assert.False(computation.HasRunErrors);
    }

    [Fact]
    public async Task OptionGroups_ThePrimaryGroupIsLeftOut()
    {
        var options = new BulkAccountOptions { MatchAttributes = [], GroupDns = [_bed.Builder.DomainUsersDn, _bed.Sample.GroupNew] };

        var row = (await _bed.ComputeAsync(_bed.Input(BulkCreateTestBed.People(TwoPeople), options: options))).Rows[0];

        Assert.Equal([_bed.Sample.GroupNew], row.GroupDns);
        var skipped = BulkCreateTestBed.IssueOf(row, BulkCreateIssueCodes.PrimaryGroupSkipped)!;
        Assert.Equal((IssueSeverity.Info, "Domain Users"), (skipped.Severity, skipped.Arguments.Single()));
        Assert.Equal(BulkRowStatus.Ok, row.Status);
    }

    [Theory]
    [InlineData(GroupScope.DomainLocal, false, true, false)]
    [InlineData(GroupScope.Universal, false, true, true)]
    [InlineData(GroupScope.Global, true, true, true)]
    public async Task CreatedGroups_FollowTheNestingRules(GroupScope created, bool intoGlobal, bool intoDomainLocal, bool intoUniversal)
    {
        var options = new BulkAccountOptions
        {
            MatchAttributes = [],
            GroupScope = created,
            GroupDns = [_bed.Sample.GroupNew, _bed.Sample.GroupDomainLocal, _bed.Sample.GroupUniversal],
        };
        var input = _bed.Input(BulkCreateTestBed.Table(["GG_Zespół_A"]), [new AttributeRule(Attr.SamAccountName, "{cn}")],
            [new ColumnMapping(0, ColumnTargetKind.Attribute, Attr.Cn)], options: options, kind: ObjectKind.Group);

        var row = (await _bed.ComputeAsync(input)).Rows[0];

        Assert.Equal(intoGlobal, row.GroupDns.Contains(_bed.Sample.GroupNew));
        Assert.Equal(intoDomainLocal, row.GroupDns.Contains(_bed.Sample.GroupDomainLocal));
        Assert.Equal(intoUniversal, row.GroupDns.Contains(_bed.Sample.GroupUniversal));
        Assert.Equal(3 - row.GroupDns.Count, row.Issues.Count(i => i.Code == BulkCreateIssueCodes.GroupScopeNotAllowed));
        Assert.NotEqual(BulkRowStatus.Error, row.Status);
    }

    [Fact(Skip = BulkCreateTestBed.Wave1)]
    public async Task GroupsColumn_NamesAndDns_TheCommaFallback_AndUnknownNames()
    {
        var table = BulkCreateTestBed.Table(
            ["Łucja", "Żak", "GG_Nowi; DL_Drukarki"],
            ["Zofia", "Bąk", "GG_Nowi, UG_Projekty"],
            ["Ewa", "Nowak", $"{_bed.Sample.GroupUniversal}\r\nGG_Brak"]);

        var computation = await _bed.ComputeAsync(_bed.Input(table, mappings: NamesAnd(2, ColumnTargetKind.Groups)));

        Assert.Equal([_bed.Sample.GroupNew, _bed.Sample.GroupDomainLocal], computation.Rows[0].GroupDns);
        Assert.Equal([_bed.Sample.GroupNew, _bed.Sample.GroupUniversal], computation.Rows[1].GroupDns);
        Assert.NotNull(BulkCreateTestBed.IssueOf(computation.Rows[1], BulkCreateIssueCodes.GroupsSplitOnCommas));
        Assert.Equal([_bed.Sample.GroupUniversal], computation.Rows[2].GroupDns);
        Assert.Equal(["GG_Brak"], BulkCreateTestBed.IssueOf(computation.Rows[2], BulkCreateIssueCodes.GroupNotFound)!.Arguments);
    }

    [Fact]
    public async Task GroupPasswordPolicy_OfAnOptionGroup_WarnsWithThePsoLimits()
    {
        var options = new BulkAccountOptions { MatchAttributes = [], GroupDns = [_bed.Sample.GroupWithPso] };

        var computation = await _bed.ComputeAsync(_bed.Input(BulkCreateTestBed.People(TwoPeople), options: options));

        var issue = RunIssue(computation, BulkCreateIssueCodes.GroupPasswordPolicy)!;
        Assert.Equal(IssueSeverity.Warning, issue.Severity);
        Assert.Equal(["GG_Uprzywilejowani", "14", "1"], issue.Arguments);
    }

    // ------------------------------------------------------------------ run checks

    [Fact]
    public async Task CannotCreateChild_WhereTheAccountMayNotCreateUsers()
    {
        var computation = await _bed.ComputeAsync(_bed.Input(BulkCreateTestBed.People(TwoPeople), container: _bed.Sample.BlockedOu));

        Assert.True(computation.HasRunErrors);
        Assert.Equal(IssueSeverity.Error, RunIssue(computation, IssueCodes.CannotCreateChild)!.Severity);
    }

    [Fact]
    public async Task CannotCreateChild_NotForAKindTheOuAllows()
    {
        var input = _bed.Input(BulkCreateTestBed.Table(["Kontakt Nowy", "Nowy"]), [new AttributeRule(Attr.Cn, "{givenName}")],
            container: _bed.Sample.BlockedOu, kind: ObjectKind.Contact);

        Assert.Null(RunIssue(await _bed.ComputeAsync(input), IssueCodes.CannotCreateChild));
    }

    [Fact]
    public async Task TargetNotFound_WhenTheContainerIsGone()
    {
        var computation = await _bed.ComputeAsync(_bed.Input(BulkCreateTestBed.People(TwoPeople), container: $"OU=Nie ma,{_bed.Company.CompanyOu}"));

        Assert.Equal(IssueSeverity.Error, RunIssue(computation, IssueCodes.TargetNotFound)!.Severity);
        Assert.All(computation.Rows, r => Assert.Null(r.DistinguishedName));
    }

    [Fact]
    public async Task PasswordsNeedAnEncryptedChannel()
    {
        _bed.Session.IsChannelEncrypted = false;

        var users = await _bed.ComputeAsync(_bed.Input(BulkCreateTestBed.People(TwoPeople)));
        var noPassword = await _bed.ComputeAsync(_bed.Input(BulkCreateTestBed.People(TwoPeople),
            options: new BulkAccountOptions { MatchAttributes = [], PasswordMode = InitialPasswordMode.None, Enabled = false }));
        var computers = await _bed.ComputeAsync(_bed.Input(BulkCreateTestBed.Table(["PC-NOWY-01"]), [new AttributeRule(Attr.SamAccountName, "{cn}")],
            [new ColumnMapping(0, ColumnTargetKind.Attribute, Attr.Cn)], kind: ObjectKind.Computer));

        Assert.NotNull(RunIssue(users, BulkCreateIssueCodes.PasswordsNeedEncryptedChannel));
        Assert.Null(RunIssue(noPassword, BulkCreateIssueCodes.PasswordsNeedEncryptedChannel));
        Assert.NotNull(RunIssue(computers, BulkCreateIssueCodes.PasswordsNeedEncryptedChannel));
    }

    [Fact]
    public async Task EnabledWithoutPassword_IsARunError()
    {
        var options = new BulkAccountOptions { MatchAttributes = [], PasswordMode = InitialPasswordMode.None, Enabled = true };

        var computation = await _bed.ComputeAsync(_bed.Input(BulkCreateTestBed.People(TwoPeople), options: options));

        Assert.Equal(IssueSeverity.Error, RunIssue(computation, BulkCreateIssueCodes.EnabledWithoutPassword)!.Severity);
    }

    [Fact]
    public async Task GeneratedLength_BelowTheDomainMinimum_IsARunError()
    {
        _bed.Session.SetValues(_bed.Builder.DomainDn, "minPwdLength", "14");

        var computation = await _bed.ComputeAsync(_bed.Input(BulkCreateTestBed.People(TwoPeople), options: new BulkAccountOptions { MatchAttributes = [], GeneratedLength = 12 }));

        Assert.Equal(["14"], RunIssue(computation, BulkCreateIssueCodes.PasswordTooShortForDomain)!.Arguments);
        Assert.Equal(new DomainPasswordPolicy(14, true), computation.PasswordPolicy);
    }

    [Fact]
    public async Task Scope_OneDomain_TheDomainCoversTheForest()
    {
        var computation = await _bed.ComputeAsync(_bed.Input(BulkCreateTestBed.People(TwoPeople)));

        Assert.Equal(UniquenessScope.ForestSingleDomain, computation.Scope);
        Assert.Null(RunIssue(computation, BulkCreateIssueCodes.CheckedInDomainOnly));
        Assert.True(computation.IsPdcEmulator);
    }

    [Fact]
    public async Task Scope_SeveralDomains_WithoutAGlobalCatalog_IsTheDomain()
    {
        var child = AddChildDomain();
        _ = child;

        var computation = await _bed.ComputeAsync(_bed.Input(BulkCreateTestBed.People(TwoPeople)));

        Assert.Equal(UniquenessScope.Domain, computation.Scope);
        Assert.Equal(IssueSeverity.Warning, RunIssue(computation, BulkCreateIssueCodes.CheckedInDomainOnly)!.Severity);
        Assert.Equal(["contoso.test", "child.contoso.test"], computation.ForestDomains);
    }

    [Fact]
    public async Task Scope_SeveralDomains_TheGlobalCatalogFindsAUpnOfAnotherDomain()
    {
        var child = AddChildDomain();
        child.AddUser("Inna", "Domena", null, new Dictionary<string, object?> { [Attr.SamAccountName] = "inna", [Attr.UserPrincipalName] = "kd202@contoso.com" });
        var gc = new FakeGlobalCatalogReader(_bed.Session, child.Session);
        _bed.UseLookups(new BulkCreateLookups(gc));

        var computation = await _bed.ComputeAsync(_bed.Input(BulkCreateTestBed.People(TwoPeople)));

        Assert.Equal(UniquenessScope.Forest, computation.Scope);
        Assert.Equal(["kd204", "kd206"], BulkCreateTestBed.Values(computation, Attr.SamAccountName));
        Assert.True(gc.SearchCount > 0);
        Assert.Null(RunIssue(computation, BulkCreateIssueCodes.CheckedInDomainOnly));
    }

    [Fact]
    public async Task RidPoolLow_WhenAnotherDcIsTheRidMaster()
    {
        var other = _bed.Session.SeedDomainController("dc02.contoso.test", Guid.NewGuid(), isPdc: false);
        _bed.Session.SetValues(_bed.Builder.RidManagerDn, "fSMORoleOwner", other);
        _bed.Session.SetValues(_bed.Sample.RidSet, "rIDNextRID", "2000");
        var input = _bed.Input(BulkCreateTestBed.GeneratedRows(150), mappings: BulkCreateTestBed.NameMappings);

        var low = await _bed.ComputeAsync(input);
        _bed.Session.SetValues(_bed.Sample.RidSet, "rIDNextRID", "1650");
        _bed.Refresh();
        var enough = await _bed.ComputeAsync(input);

        Assert.Equal(IssueSeverity.Warning, RunIssue(low, BulkCreateIssueCodes.RidPoolLow)!.Severity);
        Assert.Null(RunIssue(enough, BulkCreateIssueCodes.RidPoolLow));
    }

    [Fact]
    public async Task RidPool_TheRidMasterGetsANewPoolItself()
    {
        _bed.Session.SetValues(_bed.Sample.RidSet, "rIDNextRID", "2090");

        var computation = await _bed.ComputeAsync(_bed.Input(BulkCreateTestBed.GeneratedRows(150)));

        Assert.Null(RunIssue(computation, BulkCreateIssueCodes.RidPoolLow));
    }

    [Fact]
    public async Task JoinOwnerNotTrusted_WithAJoinerAndANonAdminWindow()
    {
        var input = _bed.Input(BulkCreateTestBed.Table(["PC-NOWY-01"]), [new AttributeRule(Attr.SamAccountName, "{cn}")],
            [new ColumnMapping(0, ColumnTargetKind.Attribute, Attr.Cn)],
            options: new BulkAccountOptions { MatchAttributes = [], JoinerSid = $"{_bed.Builder.DomainSid}-1500", JoinerName = "Instalatorzy" },
            kind: ObjectKind.Computer);

        var admin = await _bed.ComputeAsync(input);
        _bed.Session.Info = _bed.Session.Info with { BoundUserDn = _bed.Company.PawelLewandowski, BoundIdentity = @"CONTOSO\pawel.lewandowski" };
        _bed.Refresh();
        var user = await _bed.ComputeAsync(input);

        Assert.Null(RunIssue(admin, BulkCreateIssueCodes.JoinOwnerNotTrusted));
        Assert.Equal([@"CONTOSO\pawel.lewandowski"], RunIssue(user, BulkCreateIssueCodes.JoinOwnerNotTrusted)!.Arguments);
    }

    [Fact]
    public async Task ExcludedAndCreatedRows_AreNotLookedUp()
    {
        var created = $"CN=Łucja Żak,{_bed.Sample.NewEmployeesOu}";
        var input = _bed.Input(BulkCreateTestBed.People(TwoPeople.Append(("Zofia", "Bąk")).ToArray())) with
        {
            ExcludedRows = [2],
            CreatedRows = new Dictionary<int, string> { [1] = created },
            PinnedNumbers = new Dictionary<int, int> { [1] = 202 },
        };

        var computation = await _bed.ComputeAsync(input);

        Assert.Equal([BulkRowStatus.Created, BulkRowStatus.Excluded, BulkRowStatus.Ok], computation.Rows.Select(r => r.Status));
        Assert.Equal(created, computation.Rows[0].DistinguishedName);
        Assert.Equal("kd202", computation.Rows[0].Value(Attr.SamAccountName));
        Assert.Equal("kd204", computation.Rows[2].Value(Attr.SamAccountName));
        Assert.Empty(computation.Rows[0].Issues);
    }

    [Fact]
    public async Task Progress_ReportsTheRows()
    {
        var reported = new List<int>();
        var progress = new SynchronousProgress(reported.Add);

        await _bed.Planner.ComputeAsync(_bed.Session, _bed.Input(BulkCreateTestBed.GeneratedRows(300)), _bed.Lookups, progress, BulkCreateTestBed.Ct);

        Assert.Equal(0, reported[0]);
        Assert.Equal(300, reported[^1]);
    }

    [Fact]
    public async Task Cancellation_StopsTheComputation()
    {
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            _bed.Planner.ComputeAsync(_bed.Session, _bed.Input(BulkCreateTestBed.GeneratedRows(10)), _bed.Lookups, null, cancelled.Token));
    }

    // ------------------------------------------------------------------ the change plan

    [Fact]
    public async Task Plan_User_TheStepsInTheAduOrder()
    {
        var table = BulkCreateTestBed.Table(["Łucja", "Żak", "pawel.lewandowski"]);
        var options = new BulkAccountOptions { MatchAttributes = [], GroupDns = [_bed.Sample.GroupNew] };
        var computation = await _bed.ComputeAsync(_bed.Input(table, mappings: NamesAnd(2, ColumnTargetKind.Manager), options: options));

        var plan = _bed.Planner.BuildPlan(computation, null, "Utworzenie kont");

        Assert.Equal(("Utworzenie kont", _bed.Session.Info.Server, false, _bed.Time.GetUtcNow()), (plan.Title, plan.Server, plan.Sequential, plan.CreatedAt));
        var user = Assert.Single(plan.Objects);
        Assert.Equal(($"CN=Łucja Żak,{_bed.Sample.NewEmployeesOu}", "Łucja Żak", ObjectKind.User, 1), (user.DistinguishedName, user.DisplayName, user.Kind, user.SourceRow!.Value));
        Assert.Equal(CreationNames.ClassChainOf(ObjectKind.User), user.ObjectClasses);
        Assert.Equal(
            [ChangeAction.Create, ChangeAction.ResetPassword, ChangeAction.Modify, ChangeAction.Modify, ChangeAction.AddMember],
            user.Changes.Select(c => c.Action));
        var create = user.Changes[0].Attributes.ToDictionary(a => a.Attribute, a => a.NewValues);
        Assert.Equal(["user"], create[Attr.ObjectClass]);
        Assert.Equal(["514"], create[Attr.UserAccountControl]);
        Assert.Equal(["kd202"], create[Attr.SamAccountName]);
        Assert.Equal(["Łucja"], create[Attr.GivenName]);
        Assert.False(create.ContainsKey(Attr.Cn));
        Assert.False(create.ContainsKey(Attr.ProxyAddresses));
        Assert.Equal(16, user.Changes[1].Password!.Length);
        var enable = user.Changes[2].Attributes;
        Assert.Equal((Attr.UserAccountControl, "514", "512"), (enable[0].Attribute, enable[0].OldValues.Single(), enable[0].NewValues.Single()));
        Assert.Equal((Attr.PwdLastSet, 0, "0"), (enable[1].Attribute, enable[1].OldValues.Count, enable[1].NewValues.Single()));
        Assert.Equal((Attr.Manager, _bed.Company.PawelLewandowski), (user.Changes[3].Attributes.Single().Attribute, user.Changes[3].Attributes.Single().NewValues.Single()));
        Assert.Equal(_bed.Sample.GroupNew, user.Changes[4].TargetDn);
        Assert.True(plan.ContainsPasswords);
        Assert.Equal(1, plan.ExecutableCount);
    }

    [Fact]
    public async Task Plan_NoCreatedAccountControlEverHasPasswordNotRequired()
    {
        var computation = await _bed.ComputeAsync(_bed.Input(BulkCreateTestBed.GeneratedRows(20)));

        var plan = _bed.Planner.BuildPlan(computation, null, "t");

        Assert.All(plan.Objects.SelectMany(o => o.Changes).SelectMany(c => c.Attributes).Where(a => a.Attribute == Attr.UserAccountControl),
            a => Assert.All(a.NewValues, v => Assert.Equal(0, int.Parse(v, System.Globalization.CultureInfo.InvariantCulture) & 0x20)));
    }

    [Theory]
    [InlineData(true, false, new[] { Attr.UserAccountControl })]
    [InlineData(false, true, new[] { Attr.PwdLastSet })]
    [InlineData(false, false, new string[0])]
    public async Task Plan_EnableAndMustChange_AreOptions(bool enabled, bool mustChange, string[] modified)
    {
        var options = new BulkAccountOptions { MatchAttributes = [], Enabled = enabled, MustChangeAtLogon = mustChange };
        var computation = await _bed.ComputeAsync(_bed.Input(BulkCreateTestBed.People(TwoPeople[..1]), options: options));

        var user = _bed.Planner.BuildPlan(computation, null, "t").Objects[0];

        Assert.Equal(modified, user.Changes.Where(c => c.Action == ChangeAction.Modify).SelectMany(c => c.Attributes).Select(a => a.Attribute));
        Assert.Contains(user.Changes, c => c.Action == ChangeAction.ResetPassword);
    }

    [Fact]
    public async Task Plan_WithoutPassword_TheAccountStaysDisabled()
    {
        var options = new BulkAccountOptions { MatchAttributes = [], PasswordMode = InitialPasswordMode.None, Enabled = false };
        var computation = await _bed.ComputeAsync(_bed.Input(BulkCreateTestBed.People(TwoPeople[..1]), options: options));

        var user = _bed.Planner.BuildPlan(computation, null, "t").Objects[0];

        Assert.Equal([ChangeAction.Create], user.Changes.Select(c => c.Action));
        Assert.False(_bed.Planner.BuildPlan(computation, null, "t").ContainsPasswords);
    }

    [Fact]
    public async Task Plan_ExpiryOption_AndTheRowsCellWins()
    {
        var expires = new DateTimeOffset(2027, 7, 1, 0, 0, 0, TimeSpan.Zero);
        var table = BulkCreateTestBed.Table(["Łucja", "Żak", string.Empty], ["Zofia", "Bąk", "2026-12-31"]);
        var options = new BulkAccountOptions { MatchAttributes = [], ExpiresAt = expires };
        var computation = await _bed.ComputeAsync(_bed.Input(table, mappings: NamesAnd(2, ColumnTargetKind.ExpiresAt), options: options));

        var plan = _bed.Planner.BuildPlan(computation, null, "t");

        string Expiry(int i) => plan.Objects[i].Changes[0].Attributes.Single(a => a.Attribute == Attr.AccountExpires).NewValues.Single();
        Assert.Equal(AdConvert.ToFileTime(expires).ToString(System.Globalization.CultureInfo.InvariantCulture), Expiry(0));
        Assert.Equal(AdConvert.ToFileTime(new DateTimeOffset(2027, 1, 1, 0, 0, 0, TimeSpan.Zero)).ToString(System.Globalization.CultureInfo.InvariantCulture), Expiry(1));
    }

    [Fact]
    public async Task Plan_PrimarySmtpAddress_AsAnOption()
    {
        var computation = await _bed.ComputeAsync(_bed.Input(BulkCreateTestBed.People(TwoPeople[..1]), configure: r => r with { AddPrimarySmtp = true }));

        var create = _bed.Planner.BuildPlan(computation, null, "t").Objects[0].Changes[0];

        Assert.Equal(["SMTP:kd202@contoso.com"], create.Attributes.Single(a => a.Attribute == Attr.ProxyAddresses).NewValues);
    }

    [Fact]
    public async Task Plan_GeneratedPasswords_AreUniqueAndFreeOfNameTokens()
    {
        var computation = await _bed.ComputeAsync(_bed.Input(BulkCreateTestBed.GeneratedRows(60)));

        var passwords = _bed.Planner.BuildPlan(computation, null, "t").Objects
            .Select(o => (Row: computation.Rows.Single(r => r.Number == o.SourceRow), Password: o.Changes.Single(c => c.Action == ChangeAction.ResetPassword).Password!))
            .ToList();

        Assert.Equal(60, passwords.Select(p => p.Password).Distinct(StringComparer.Ordinal).Count());
        Assert.All(passwords, p =>
        {
            Assert.DoesNotContain(p.Row.Value(Attr.SamAccountName)!, p.Password, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(p.Row.Value(Attr.Sn)!, p.Password, StringComparison.OrdinalIgnoreCase);
        });
    }

    [Fact]
    public async Task Plan_SharedPassword_CopiedIntoEveryReset_AndWarnsWhenItContainsTheName()
    {
        var options = new BulkAccountOptions { MatchAttributes = [], PasswordMode = InitialPasswordMode.SameForAll };
        var computation = await _bed.ComputeAsync(_bed.Input(BulkCreateTestBed.People(("Anna", "Nowak"), ("Zofia", "Bąk")), options: options));

        var plan = _bed.Planner.BuildPlan(computation, "Nowak#2026!x", "t");

        Assert.All(plan.Objects, o => Assert.Equal("Nowak#2026!x", o.Changes.Single(c => c.Action == ChangeAction.ResetPassword).Password));
        Assert.Single(plan.Objects[0].Changes.Single(c => c.Action == ChangeAction.ResetPassword).Issues, i => i.Code == BulkCreateIssueCodes.PasswordContainsName);
        Assert.Empty(plan.Objects[1].Changes.Single(c => c.Action == ChangeAction.ResetPassword).Issues);
        Assert.Equal(2, plan.ExecutableCount);
    }

    [Theory]
    [InlineData("Ab1!xy", BulkCreateIssueCodes.PasswordTooShortForDomain)]
    [InlineData("abcdefghijk", BulkCreateIssueCodes.PasswordNotComplex)]
    [InlineData("", IssueCodes.InvalidValue)]
    public async Task Plan_SharedPassword_AgainstTheDomainPolicy_BlocksEveryObject(string password, string code)
    {
        var options = new BulkAccountOptions { MatchAttributes = [], PasswordMode = InitialPasswordMode.SameForAll };
        var computation = await _bed.ComputeAsync(_bed.Input(BulkCreateTestBed.People(TwoPeople), options: options));

        var plan = _bed.Planner.BuildPlan(computation, password, "t");

        Assert.All(plan.Objects, o => Assert.Contains(o.Issues, i => i.Code == code && i.Severity == IssueSeverity.Error));
        Assert.Equal(0, plan.ExecutableCount);
        Assert.False(plan.ContainsPasswords);
    }

    [Fact]
    public async Task Plan_SharedPassword_ThePsoOfAnOptionGroupIsStricter()
    {
        var options = new BulkAccountOptions { MatchAttributes = [], PasswordMode = InitialPasswordMode.SameForAll, GroupDns = [_bed.Sample.GroupWithPso] };
        var computation = await _bed.ComputeAsync(_bed.Input(BulkCreateTestBed.People(TwoPeople), options: options));

        var plan = _bed.Planner.BuildPlan(computation, "Zima#2026!", "t");

        Assert.Equal(["14"], plan.Objects[0].Issues.Single(i => i.Code == BulkCreateIssueCodes.PasswordTooShortForDomain).Arguments);
    }

    [Fact]
    public async Task Plan_ErrorRowsAreKeptWithTheirIssues_ExcludedAndCreatedRowsAreLeftOut()
    {
        var table = BulkCreateTestBed.Table(["Łucja", "Żak", string.Empty], ["Zofia", "Bąk", "jan.kowalski"], ["Ewa", "Nowak", string.Empty], ["Jan", "Bąk", string.Empty]);
        var input = _bed.Input(table, mappings: [.. BulkCreateTestBed.NameMappings, new ColumnMapping(2, ColumnTargetKind.Attribute, Attr.SamAccountName)]) with
        {
            ExcludedRows = [3],
            CreatedRows = new Dictionary<int, string> { [4] = $"CN=Jan Bąk,{_bed.Sample.NewEmployeesOu}" },
        };
        var computation = await _bed.ComputeAsync(input);

        var plan = _bed.Planner.BuildPlan(computation, null, "t");

        Assert.Equal([1, 2], plan.Objects.Select(o => o.SourceRow!.Value));
        Assert.True(plan.Objects[1].HasErrors);
        Assert.Equal([ChangeAction.Create], plan.Objects[1].Changes.Select(c => c.Action));
        Assert.Equal(1, plan.ExecutableCount);
    }

    [Fact]
    public async Task Plan_RunErrors_BlockEveryObject()
    {
        var computation = await _bed.ComputeAsync(_bed.Input(BulkCreateTestBed.People(TwoPeople), container: _bed.Sample.BlockedOu));

        var plan = _bed.Planner.BuildPlan(computation, null, "t");

        Assert.All(plan.Objects, o => Assert.Contains(o.Issues, i => i.Code == IssueCodes.CannotCreateChild));
        Assert.Equal(0, plan.ExecutableCount);
    }

    [Fact]
    public async Task Plan_Computer_DisabledThenARandomMachinePasswordThenEnabled()
    {
        var options = new BulkAccountOptions { MatchAttributes = [], JoinerSid = $"{_bed.Builder.DomainSid}-1500", JoinerName = "Instalatorzy" };
        var input = _bed.Input(BulkCreateTestBed.Table(["pc-nowy-01"]), [new AttributeRule(Attr.SamAccountName, "{cn}")],
            [new ColumnMapping(0, ColumnTargetKind.Attribute, Attr.Cn)], options: options, kind: ObjectKind.Computer);
        var computation = await _bed.ComputeAsync(input);

        var computer = Assert.Single(_bed.Planner.BuildPlan(computation, null, "t").Objects);

        Assert.Equal([ChangeAction.Create, ChangeAction.ModifySecurity, ChangeAction.ResetPassword, ChangeAction.Modify], computer.Changes.Select(c => c.Action));
        var create = computer.Changes[0].Attributes.ToDictionary(a => a.Attribute, a => a.NewValues);
        Assert.Equal(["computer"], create[Attr.ObjectClass]);
        Assert.Equal(["PC-NOWY-01$"], create[Attr.SamAccountName]);
        Assert.Equal(["4098"], create[Attr.UserAccountControl]);
        Assert.Equal($"CN=PC-NOWY-01,{_bed.Sample.NewEmployeesOu}", computer.DistinguishedName);
        Assert.Equal(ResetComputerAccountOperation.GeneratedLength, computer.Changes[2].Password!.Length);
        Assert.Equal(["4098"], computer.Changes[3].Attributes.Single().OldValues);
        Assert.Equal(["4096"], computer.Changes[3].Attributes.Single().NewValues);
        Assert.Equal(4, computer.Changes[1].Acl!.Added.Count);
    }

    [Fact]
    public async Task Plan_Contact_AndGroup()
    {
        var contacts = await _bed.ComputeAsync(_bed.Input(BulkCreateTestBed.Table(["Anna", "Kontakt", "pawel.lewandowski"]),
            [new AttributeRule(Attr.Cn, "{givenName} {sn}"), new AttributeRule(Attr.Mail, "{givenName|nodiacritics|lower}@firma.pl")],
            NamesAnd(2, ColumnTargetKind.Manager), kind: ObjectKind.Contact));
        var groups = await _bed.ComputeAsync(_bed.Input(BulkCreateTestBed.Table(["GG_Zespół_B"]), [new AttributeRule(Attr.SamAccountName, "{cn}")],
            [new ColumnMapping(0, ColumnTargetKind.Attribute, Attr.Cn)],
            options: new BulkAccountOptions { MatchAttributes = [], GroupScope = GroupScope.Universal, SecurityGroup = false }, kind: ObjectKind.Group));

        var contact = _bed.Planner.BuildPlan(contacts, null, "t").Objects.Single();
        var group = _bed.Planner.BuildPlan(groups, null, "t").Objects.Single();

        Assert.Equal([ChangeAction.Create, ChangeAction.Modify], contact.Changes.Select(c => c.Action));
        Assert.Equal(["contact"], contact.Changes[0].Attributes.Single(a => a.Attribute == Attr.ObjectClass).NewValues);
        Assert.DoesNotContain(contact.Changes[0].Attributes, a => a.Attribute is Attr.UserAccountControl or Attr.SamAccountName);
        Assert.Equal([ChangeAction.Create], group.Changes.Select(c => c.Action));
        var create = group.Changes[0].Attributes.ToDictionary(a => a.Attribute, a => a.NewValues);
        Assert.Equal([GroupTypes.Compose(GroupScope.Universal, false).ToString(System.Globalization.CultureInfo.InvariantCulture)], create[Attr.GroupType]);
        Assert.Equal(["GG_Zespół_B"], create[Attr.SamAccountName]);
    }

    /// <summary>The child domain <c>child.contoso.test</c> of the forest: its crossRef in the sample's partitions and its own fake.</summary>
    private FakeDirectoryBuilder AddChildDomain()
    {
        _bed.Session.Seed($"CN=CHILD,{_bed.Builder.PartitionsDn}", new Dictionary<string, object?>
        {
            [Attr.ObjectClass] = new[] { "top", "crossRef" },
            ["systemFlags"] = 3,
            ["dnsRoot"] = "child.contoso.test",
            ["nCName"] = "DC=child,DC=contoso,DC=test",
        });
        var child = new FakeDirectoryBuilder("child.contoso.test", schema: FakeSchema.Create(bulkCreate: true), guidNamespace: 7, domainSid: "S-1-5-21-1-2-3");
        return child;
    }

    private sealed class SynchronousProgress(Action<int> report) : IProgress<int>
    {
        public void Report(int value) => report(value);
    }
}
