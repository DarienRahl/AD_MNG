using AdMng.Core.ActiveDirectory;
using AdMng.Core.Batch;
using AdMng.Core.Csv;
using AdMng.Core.Directory;
using AdMng.Core.Ldap;
using AdMng.Core.Model;
using AdMng.Testing;

namespace AdMng.Core.Tests.Batch;

public class CsvImportPlannerTests
{
    private readonly ManualTimeProvider _time = new();
    private readonly FakeDirectoryBuilder _builder;
    private readonly SampleCompany _company;
    private readonly CsvCodec _csv = new();

    public CsvImportPlannerTests()
    {
        _builder = new FakeDirectoryBuilder(timeProvider: _time);
        _company = _builder.AddSampleCompany();
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private FakeDirectorySession Session => _builder.Session;

    private CsvTable Table(string text) => _csv.Read(new StringReader(text));

    private static CsvImportMapping Mapping(string keyColumn, string keyAttribute, params (string Column, string Attribute)[] columns) => new()
    {
        KeyColumn = keyColumn,
        KeyAttribute = keyAttribute,
        Columns = columns.ToDictionary(c => c.Column, c => c.Attribute),
    };

    private Task<ChangePlan> PlanAsync(CsvTable table, CsvImportMapping mapping, PlanningOptions? options = null, IProgress<int>? progress = null) =>
        new CsvImportPlanner(_time).PlanAsync(Session, table, mapping, options, progress, Ct);

    private static AttributeChange Change(ObjectPlan plan, string attribute) =>
        Assert.Single(Assert.Single(plan.Changes).Attributes, a => a.Attribute == attribute);

    private static IEnumerable<ValidationIssue> AllIssues(ObjectPlan plan) => plan.Issues.Concat(plan.Changes.SelectMany(c => c.Issues));

    [Fact]
    public async Task Rows_AreResolvedBySamAccountNameAndCompared()
    {
        var table = Table("login;Dział;Telefon\njan.kowalski;Zarząd;+48 22 111\n zaneta.szymanska ;;+48 22 222\nnieistnieje;X;\n");

        var plan = await PlanAsync(table, Mapping("login", Attr.SamAccountName, ("Dział", Attr.Department), ("Telefon", Attr.TelephoneNumber)));

        Assert.Equal("CSV import", plan.Title);
        Assert.Equal(Session.Info.Server, plan.Server);
        Assert.Equal(_time.GetUtcNow(), plan.CreatedAt);
        Assert.Equal([1, 2, 3], plan.Objects.Select(o => o.SourceRow!.Value));
        var jan = plan.Objects[0];
        Assert.Equal(_company.JanKowalski, jan.DistinguishedName);
        Assert.Equal(["IT"], Change(jan, Attr.Department).OldValues);
        Assert.Equal(["Zarząd"], Change(jan, Attr.Department).NewValues);
        Assert.Equal(["+48 22 111"], Change(jan, Attr.TelephoneNumber).NewValues);
        var zaneta = plan.Objects[1];
        Assert.Equal(Attr.TelephoneNumber, Assert.Single(Assert.Single(zaneta.Changes).Attributes).Attribute);
        var missing = plan.Objects[2];
        Assert.Equal(IssueCodes.NotFound, Assert.Single(missing.Issues).Code);
        Assert.Equal("nieistnieje", missing.DisplayName);
        var search = Assert.Single(Session.Operations, o => o.Kind == FakeOperationKind.Search && o.Search!.Filter.Contains("sAMAccountName=", StringComparison.Ordinal));
        Assert.Equal("(|(sAMAccountName=jan.kowalski)(sAMAccountName=zaneta.szymanska)(sAMAccountName=nieistnieje))", search.Search!.Filter);
    }

    [Fact]
    public async Task EmptyCells_CanClearValues()
    {
        var table = Table("login;Dział\nzaneta.szymanska;\n");

        var ignore = await PlanAsync(table, Mapping("login", Attr.SamAccountName, ("Dział", Attr.Department)));
        var clear = await PlanAsync(table, Mapping("login", Attr.SamAccountName, ("Dział", Attr.Department)) with { EmptyCells = EmptyCellBehavior.Clear });

        Assert.True(Assert.Single(ignore.Objects).IsNoOp);
        var change = Change(Assert.Single(clear.Objects), Attr.Department);
        Assert.Equal(AttributeChangeKind.Clear, change.Kind);
        Assert.Equal(["Sprzedaż"], change.OldValues);
    }

    [Fact]
    public async Task Lookups_AreBatchedInGroupsOfFifty()
    {
        var ou = _builder.AddOu("Import", _company.CompanyOu);
        for (var i = 1; i <= 120; i++)
        {
            _builder.AddUser("Użytkownik", $"Testowy{i:000}", ou, new Dictionary<string, object?> { [Attr.EmployeeId] = $"E{i:0000}" });
        }

        var csv = "employeeID;title\n" + string.Concat(Enumerable.Range(1, 120).Select(i => $"E{i:0000};Tester\n"));
        var progress = new SynchronousProgress<int>();

        var plan = await PlanAsync(Table(csv), Mapping("employeeID", Attr.EmployeeId, ("title", Attr.Title)), new PlanningOptions { CheckPermissions = false }, progress);

        var searches = Session.Operations.Where(o => o.Kind == FakeOperationKind.Search && o.Search!.Filter.Contains("employeeID=", StringComparison.Ordinal)).ToList();
        Assert.Equal(3, searches.Count);
        Assert.Equal([50, 50, 20], searches.Select(s => s.Search!.Filter.Split("(employeeID=").Length - 1));
        Assert.Equal(120, plan.ExecutableCount);
        Assert.Equal(120, progress.Values.Max());
    }

    [Fact]
    public async Task AmbiguousAndEmptyKeys_AreRowErrors()
    {
        Session.SetValues(_company.JanKowalski, Attr.EmployeeId, "E0001");
        Session.SetValues(_company.PawelLewandowski, Attr.EmployeeId, "e0001");
        var table = Table("id;title\nE0001;Kierownik\n;Brak klucza\n");

        var plan = await PlanAsync(table, Mapping("id", Attr.EmployeeId, ("title", Attr.Title)));

        var ambiguous = Assert.Single(plan.Objects[0].Issues);
        Assert.Equal(IssueCodes.Ambiguous, ambiguous.Code);
        Assert.Contains(_company.JanKowalski, ambiguous.Message, StringComparison.Ordinal);
        Assert.Contains(_company.PawelLewandowski, ambiguous.Message, StringComparison.Ordinal);
        var empty = Assert.Single(plan.Objects[1].Issues);
        Assert.Equal(IssueCodes.InvalidValue, empty.Code);
        Assert.Equal("The key column 'id' is empty.", empty.Message);
        Assert.Equal(2, plan.ErrorCount);
    }

    [Fact]
    public async Task DistinguishedNameKeys_AreReadDirectly()
    {
        var table = Table($"dn;title\n\"{_company.JanKowalski.ToUpperInvariant()}\";Kierownik\nnie jest DN;x\n\"CN=Nikt,{_company.ItOu}\";x\n");

        var plan = await PlanAsync(table, Mapping("dn", Attr.DistinguishedName, ("title", Attr.Title)));

        Assert.Equal(_company.JanKowalski, plan.Objects[0].DistinguishedName);
        Assert.Equal(["Kierownik"], Change(plan.Objects[0], Attr.Title).NewValues);
        Assert.Equal(IssueCodes.InvalidValue, Assert.Single(plan.Objects[1].Issues).Code);
        Assert.Equal(IssueCodes.NotFound, Assert.Single(plan.Objects[2].Issues).Code);
        Assert.DoesNotContain(Session.Operations, o => o.Kind == FakeOperationKind.Search && o.Search!.Filter.Contains("distinguishedName", StringComparison.Ordinal));
    }

    [Fact]
    public async Task GuidKeys_UseBinaryFilters()
    {
        var guid = Session.GetValues(_company.JanKowalski, Attr.ObjectGuid).Single();
        var table = Table($"guid;title\n{{{guid.ToUpperInvariant()}}};Kierownik\nnie-guid;x\n");

        var plan = await PlanAsync(table, Mapping("guid", Attr.ObjectGuid, ("title", Attr.Title)));

        Assert.Equal(_company.JanKowalski, plan.Objects[0].DistinguishedName);
        Assert.Equal(IssueCodes.InvalidValue, Assert.Single(plan.Objects[1].Issues).Code);
        var search = Assert.Single(Session.Operations, o => o.Kind == FakeOperationKind.Search && o.Search!.Filter.Contains("objectGUID", StringComparison.Ordinal));
        Assert.Equal(LdapFilter.EqualGuid(Attr.ObjectGuid, Guid.Parse(guid)), search.Search!.Filter);
    }

    [Fact]
    public async Task KindRestriction_AvoidsMatchingContacts()
    {
        _builder.AddContact("Jan", "Kowalski", _company.SalesOu, new Dictionary<string, object?> { [Attr.Mail] = "jan.kowalski@contoso.test" });
        var table = Table("mail;title\njan.kowalski@contoso.test;Kierownik\n");

        var any = await PlanAsync(table, Mapping("mail", Attr.Mail, ("title", Attr.Title)));
        var users = await PlanAsync(table, Mapping("mail", Attr.Mail, ("title", Attr.Title)) with { Kind = ObjectKind.User });

        Assert.Equal(IssueCodes.Ambiguous, Assert.Single(any.Objects[0].Issues).Code);
        Assert.Equal(_company.JanKowalski, users.Objects[0].DistinguishedName);
        Assert.False(users.Objects[0].HasErrors);
    }

    [Fact]
    public async Task MultiValuedCells_AreSplit()
    {
        var table = Table("login;telefony;opis\njan.kowalski;111 | 222||;Opis | z kreską\n");

        var plan = await PlanAsync(table, Mapping("login", Attr.SamAccountName, ("telefony", Attr.OtherTelephone), ("opis", Attr.Info)));

        Assert.Equal(["111", "222"], Change(plan.Objects[0], Attr.OtherTelephone).NewValues);
        Assert.Equal(["Opis | z kreską"], Change(plan.Objects[0], Attr.Info).NewValues);
    }

    [Fact]
    public async Task FormulaProtectionOfExportedFiles_IsRemovedOnImport()
    {
        // A file exported by the application (formula protection on), edited and imported again.
        using var writer = new StringWriter();
        _csv.Write(writer, ["login", "telefon", "opis"], [["jan.kowalski", "+48 22 333", "-"]]);

        var plan = await PlanAsync(Table(writer.ToString()), Mapping("login", Attr.SamAccountName, ("telefon", Attr.TelephoneNumber), ("opis", Attr.Info)));

        Assert.Contains("'+48 22 333", writer.ToString(), StringComparison.Ordinal);
        Assert.Equal(["+48 22 333"], Change(plan.Objects[0], Attr.TelephoneNumber).NewValues);
        Assert.Equal(["-"], Change(plan.Objects[0], Attr.Info).NewValues);
    }

    [Fact]
    public async Task RangedValues_AreComparedCompletely()
    {
        Session.SetValues(_company.JanKowalski, Attr.OtherTelephone, "111", "222", "333");
        Session.MaxValueRange = 2;
        var table = Table("login;telefony\njan.kowalski;111|222|333\nlukasz.wisniewski;444|555|666\n");

        var plan = await PlanAsync(table, Mapping("login", Attr.SamAccountName, ("telefony", Attr.OtherTelephone)));

        Assert.True(plan.Objects[0].IsNoOp);
        Assert.Empty(plan.Objects[0].Changes);
        Assert.Equal(["444", "555", "666"], Change(plan.Objects[1], Attr.OtherTelephone).NewValues);
    }

    [Fact]
    public async Task Rows_AreValidatedLikeBatchChanges()
    {
        var longValue = new string('x', 65);
        var table = Table($"login;dzial;mail;nieznany\njan.kowalski;{longValue};wspolny@contoso.test;a\nlukasz.wisniewski;IT;wspolny@contoso.test;\npawel.lewandowski;Sprzedaż;malgorzata.wojcik@contoso.test;\n");

        var plan = await PlanAsync(table, Mapping("login", Attr.SamAccountName, ("dzial", Attr.Department), ("mail", Attr.Mail), ("nieznany", "noSuchAttribute")));

        var jan = AllIssues(plan.Objects[0]).ToList();
        Assert.Contains(jan, i => i.Code == IssueCodes.TooLong && i.Attribute == Attr.Department);
        Assert.Contains(jan, i => i.Code == IssueCodes.UnknownAttribute && i.Severity == IssueSeverity.Error);
        Assert.Contains(jan, i => i.Code == IssueCodes.DuplicateInBatch);
        Assert.Contains(AllIssues(plan.Objects[1]), i => i.Code == IssueCodes.DuplicateInBatch);
        Assert.Contains(AllIssues(plan.Objects[2]), i => i.Code == IssueCodes.Duplicate && i.Message.Contains(_company.MalgorzataWojcik, StringComparison.Ordinal));
    }

    [Fact]
    public async Task RowsForTheSameObject_AreReported()
    {
        var table = Table("login;title\njan.kowalski;A\nJAN.KOWALSKI;B\n");

        var plan = await PlanAsync(table, Mapping("login", Attr.SamAccountName, ("title", Attr.Title)));

        Assert.False(plan.Objects[0].HasErrors);
        var duplicate = Assert.Single(plan.Objects[1].Issues);
        Assert.Equal(IssueCodes.DuplicateInBatch, duplicate.Code);
        Assert.Equal("Row 1 already refers to this object.", duplicate.Message);
    }

    [Fact]
    public async Task UnchangedRows_AreNoOpsAndProtectedObjectsWarn()
    {
        var table = Table("login;dzial;opis\njan.kowalski;IT;\nAdministrator;;Konto wbudowane\n");

        var plan = await PlanAsync(table, Mapping("login", Attr.SamAccountName, ("dzial", Attr.Department), ("opis", Attr.Description)));

        Assert.True(plan.Objects[0].IsNoOp);
        Assert.Contains(plan.Objects[0].Issues, i => i.Code == IssueCodes.NoChange);
        Assert.Contains(plan.Objects[1].Issues, i => i.Code == IssueCodes.AdminSdHolder);
        Assert.False(plan.Objects[1].HasErrors);
    }

    [Fact]
    public async Task UserAccountControlColumns_CannotDisableProtectedAccountsOrChangeTheAccountType()
    {
        var table = Table("login;uac\nAdministrator;514\njan.kowalski;514\nWAW-PC001$;8192\n");

        var plan = await PlanAsync(table, Mapping("login", Attr.SamAccountName, ("uac", Attr.UserAccountControl)));

        var administrator = Assert.Single(AllIssues(plan.Objects[0]), i => i.Code == IssueCodes.ProtectedObject);
        Assert.Equal(IssueSeverity.Error, administrator.Severity);
        Assert.True(plan.Objects[0].HasErrors);
        Assert.False(plan.Objects[1].HasErrors);
        Assert.Equal(["514"], Change(plan.Objects[1], Attr.UserAccountControl).NewValues);
        Assert.Contains(AllIssues(plan.Objects[2]), i => i is { Code: IssueCodes.InvalidValue, Severity: IssueSeverity.Error, Attribute: Attr.UserAccountControl });
        Assert.Equal(1, plan.ExecutableCount);
    }

    [Fact]
    public async Task GroupSamAccountNames_MayBeLongerThanTwentyCharacters()
    {
        var table = Table("login;nazwa\nGG_Ksiegowosc;Dzial-Ksiegowosci-Krakow\njan.kowalski;jan.kowalski.dlugi.21\n");

        var plan = await PlanAsync(table, Mapping("login", Attr.SamAccountName, ("nazwa", Attr.SamAccountName)));

        Assert.False(plan.Objects[0].HasErrors);
        Assert.Equal(["Dzial-Ksiegowosci-Krakow"], Change(plan.Objects[0], Attr.SamAccountName).NewValues);
        Assert.Contains(AllIssues(plan.Objects[1]), i => i is { Code: IssueCodes.TooLong, Severity: IssueSeverity.Error });
    }

    [Fact]
    public async Task Permissions_AreChecked()
    {
        Session.SetPermissions(_company.JanKowalski, new EffectivePermissions
        {
            IsKnown = true,
            WritableAttributes = new HashSet<string>([Attr.Department], StringComparer.OrdinalIgnoreCase),
        });

        var plan = await PlanAsync(Table("login;title\njan.kowalski;Kierownik\n"), Mapping("login", Attr.SamAccountName, ("title", Attr.Title)));

        Assert.Contains(AllIssues(plan.Objects[0]), i => i.Code == IssueCodes.NotWritable && i.Attribute == Attr.Title);
    }

    [Fact]
    public async Task SearchBaseAndKind_AreUsed()
    {
        var plan = await PlanAsync(
            Table("login;title\njan.kowalski;Kierownik\npawel.lewandowski;Prezes\n"),
            Mapping("login", Attr.SamAccountName, ("title", Attr.Title)) with { SearchBaseDn = _company.ItOu, Kind = ObjectKind.User });

        var search = Assert.Single(Session.Operations, o => o.Kind == FakeOperationKind.Search && o.Search!.Filter.Contains("sAMAccountName=", StringComparison.Ordinal));
        Assert.Equal(_company.ItOu, search.Search!.BaseDn);
        Assert.StartsWith("(&(sAMAccountType=805306368)(|", search.Search.Filter, StringComparison.Ordinal);
        Assert.False(plan.Objects[0].HasErrors);
        Assert.Equal(IssueCodes.NotFound, Assert.Single(plan.Objects[1].Issues).Code);
    }

    [Theory]
    [InlineData("brak", Attr.SamAccountName, "title", Attr.Title)]
    [InlineData("login", Attr.SamAccountName, "brak", Attr.Title)]
    [InlineData("login", "bad attribute", "title", Attr.Title)]
    [InlineData("login", Attr.SamAccountName, "title", "bad attribute")]
    public async Task InvalidMappings_Throw(string keyColumn, string keyAttribute, string column, string attribute)
    {
        var table = Table("login;title\njan.kowalski;x\n");

        await Assert.ThrowsAsync<ArgumentException>(() => PlanAsync(table, Mapping(keyColumn, keyAttribute, (column, attribute))));
    }

    [Fact]
    public async Task SameAttributeFromTwoColumns_Throws()
    {
        var table = Table("login;a;b\njan.kowalski;x;y\n");

        await Assert.ThrowsAsync<ArgumentException>(() => PlanAsync(table, Mapping("login", Attr.SamAccountName, ("a", Attr.Title), ("b", "TITLE"))));
    }
}
