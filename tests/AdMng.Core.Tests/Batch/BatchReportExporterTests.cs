using AdMng.Core.ActiveDirectory;
using AdMng.Core.Audit;
using AdMng.Core.Batch;
using AdMng.Core.Csv;
using AdMng.Core.Directory;
using AdMng.Core.Schema;
using AdMng.Core.Security;
using AdMng.Core.Templates;
using AdMng.Testing;

namespace AdMng.Core.Tests.Batch;

public class BatchReportExporterTests
{
    private readonly ManualTimeProvider _time = new();
    private readonly FakeDirectoryBuilder _builder;
    private readonly SampleCompany _company;
    private readonly CsvCodec _csv = new();

    public BatchReportExporterTests()
    {
        _builder = new FakeDirectoryBuilder(timeProvider: _time);
        _company = _builder.AddSampleCompany();
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private BatchReportExporter Exporter => new(_csv);

    private Task<ChangePlan> PlanAsync(IReadOnlyList<string> targets, params BatchOperation[] operations) =>
        new ChangePlanner(new TemplateEngine(), new PasswordGenerator(), _time)
            .PlanAsync(_builder.Session, new BatchRequest(targets, operations), null, null, Ct);

    private CsvTable Export(Action<TextWriter> write)
    {
        using var writer = new StringWriter();
        write(writer);
        return _csv.Read(new StringReader(writer.ToString()));
    }

    private static string Cell(CsvTable table, int row, string column) => table.Cell(row, table.IndexOf(column));

    private static string[] Column(CsvTable table, string column) => [.. Enumerable.Range(0, table.Rows.Count).Select(r => Cell(table, r, column))];

    [Fact]
    public async Task Plan_IsWrittenOneRowPerChange()
    {
        var plan = await PlanAsync(
            [_company.JanKowalski, _company.PawelLewandowski, "CN=Nikt," + _company.ItOu],
            new SetAttributeOperation(Attr.Department, "Sprzedaż"),
            new SetAttributeOperation(Attr.Title, "Dyrektor sprzedaży"),
            new AddToGroupsOperation([_company.GroupSales]),
            new MoveOperation(_company.SalesOu));

        var table = Export(w => Exporter.Write(w, plan));

        Assert.Equal(["Status", "Name", "DN", "Action", "Attribute", "Old value", "New value", "Message"], table.Headers);
        Assert.Equal(';', table.Delimiter);
        var jan = Enumerable.Range(0, table.Rows.Count).Where(r => Cell(table, r, "DN") == _company.JanKowalski).ToList();
        Assert.Equal(["Modify", "Modify", "AddMember", "Move"], jan.Select(r => Cell(table, r, "Action")));
        Assert.All(jan, r => Assert.Equal(BatchReportExporter.StatusPlanned, Cell(table, r, "Status")));
        Assert.Equal("IT", Cell(table, jan[0], "Old value"));
        Assert.Equal("Sprzedaż", Cell(table, jan[0], "New value"));
        Assert.Equal(_company.GroupSales, Cell(table, jan[2], "New value"));
        Assert.Equal(_company.SalesOu, Cell(table, jan[3], "New value"));
        var pawel = Enumerable.Range(0, table.Rows.Count).Single(r => Cell(table, r, "DN") == _company.PawelLewandowski);
        Assert.Equal(BatchReportExporter.StatusNoChange, Cell(table, pawel, "Status"));
        Assert.Contains(IssueCodes.TargetIsSameContainer, Cell(table, pawel, "Message"), StringComparison.Ordinal);
        var missing = table.Rows.Count - 1;
        Assert.Equal(BatchReportExporter.StatusError, Cell(table, missing, "Status"));
        Assert.Contains("[Error: NotFound]", Cell(table, missing, "Message"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Passwords_AreOnlyWrittenWhenRequested()
    {
        var plan = await PlanAsync([_company.JanKowalski], new ResetPasswordOperation(PasswordMode.GenerateUnique));
        var password = plan.Objects[0].Changes[0].Password!;

        var safe = Export(w => Exporter.Write(w, plan));
        var withPasswords = Export(w => Exporter.Write(w, plan, includePasswords: true, delimiter: ','));

        Assert.DoesNotContain("Password", safe.Headers);
        Assert.All(safe.Rows, r => Assert.DoesNotContain(password, r));
        Assert.Equal("Password", withPasswords.Headers[^1]);
        Assert.Equal(',', withPasswords.Delimiter);
        var resetRow = Enumerable.Range(0, withPasswords.Rows.Count).Single(r => Cell(withPasswords, r, "Action") == "ResetPassword");
        Assert.Equal(password, Cell(withPasswords, resetRow, "Password"));
        Assert.All(Enumerable.Range(0, withPasswords.Rows.Count).Where(r => r != resetRow), r => Assert.Equal(string.Empty, Cell(withPasswords, r, "Password")));
    }

    [Fact]
    public async Task Result_ShowsStepOutcomes()
    {
        var plan = await PlanAsync(
            [_company.JanKowalski, _company.LukaszWisniewski, "CN=Nikt," + _company.ItOu],
            new SetAttributeOperation(Attr.Title, "=Kierownik"),
            new MoveOperation(_company.SalesOu));
        _builder.Session.FailNext(FakeOperationKind.Move, _company.JanKowalski, DirectoryErrorKind.AccessDenied);
        var result = await new BatchExecutor(new InMemoryAuditLog(), _time)
            .ExecuteAsync(_builder.Session, plan, new BatchOptions { MaxDegreeOfParallelism = 1 }, null, Ct);

        using var writer = new StringWriter();
        Exporter.Write(writer, result);
        var text = writer.ToString();
        var table = _csv.Read(new StringReader(text));

        var jan = Enumerable.Range(0, table.Rows.Count).Where(r => Cell(table, r, "Name") == "Jan Kowalski").ToList();
        Assert.Equal([BatchReportExporter.StatusSucceeded, BatchReportExporter.StatusFailed], jan.Select(r => Cell(table, r, "Status")));
        Assert.Contains("Injected AccessDenied failure.", Cell(table, jan[1], "Message"), StringComparison.Ordinal);
        Assert.Equal("'=Kierownik", Cell(table, jan[0], "New value"));
        Assert.Contains(";'=Kierownik;", text, StringComparison.Ordinal);
        var lukasz = Enumerable.Range(0, table.Rows.Count).Where(r => Cell(table, r, "Name") == "Łukasz Wiśniewski").ToList();
        Assert.All(lukasz, r => Assert.Equal(BatchReportExporter.StatusSucceeded, Cell(table, r, "Status")));
        Assert.Equal("CN=Łukasz Wiśniewski," + _company.SalesOu, Cell(table, lukasz[0], "DN"));
        Assert.Equal(nameof(ObjectResultStatus.Skipped), Cell(table, table.Rows.Count - 1, "Status"));
    }

    [Fact]
    public async Task Result_ExplainsWhyAnObjectWasSkipped()
    {
        _builder.Session.SetPermissions(_company.LukaszWisniewski, new EffectivePermissions { IsKnown = true });
        var plan = await PlanAsync([_company.JanKowalski, _company.LukaszWisniewski], new SetAttributeOperation(Attr.Title, "Nowy"));
        var result = await new BatchExecutor(new InMemoryAuditLog(), _time).ExecuteAsync(_builder.Session, plan, null, null, Ct);

        var table = Export(w => Exporter.Write(w, result));

        var lukasz = Enumerable.Range(0, table.Rows.Count).Single(r => Cell(table, r, "DN") == _company.LukaszWisniewski);
        Assert.Equal(nameof(ObjectResultStatus.Skipped), Cell(table, lukasz, "Status"));
        Assert.Contains("[Error: NotWritable]", Cell(table, lukasz, "Message"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Result_MarksStepsThatDidNotRun()
    {
        var plan = await PlanAsync([_company.JanKowalski], new SetAttributeOperation(Attr.Title, "Nowy"), new MoveOperation(_company.SalesOu));
        _builder.Session.FailNext(FakeOperationKind.Modify, _company.JanKowalski, DirectoryErrorKind.AccessDenied);
        var result = await new BatchExecutor(new InMemoryAuditLog(), _time).ExecuteAsync(_builder.Session, plan, null, null, Ct);

        var table = Export(w => Exporter.Write(w, result));

        Assert.Equal([BatchReportExporter.StatusFailed, BatchReportExporter.StatusNotExecuted], table.Rows.Select(r => r[0]));
    }

    [Fact]
    public async Task Result_ContainsOnlyThePasswordsOfResetsThatSucceeded()
    {
        var plan = await PlanAsync([_company.JanKowalski, _company.LukaszWisniewski], new ResetPasswordOperation(PasswordMode.GenerateUnique));
        var passwords = plan.Objects.ToDictionary(o => o.DistinguishedName, o => o.Changes.Single(c => c.Action == ChangeAction.ResetPassword).Password!);
        _builder.Session.FailNext(FakeOperationKind.ResetPassword, _company.LukaszWisniewski, DirectoryErrorKind.AccessDenied);
        var result = await new BatchExecutor(new InMemoryAuditLog(), _time)
            .ExecuteAsync(_builder.Session, plan, new BatchOptions { MaxDegreeOfParallelism = 1, MaxRetries = 0 }, null, Ct);

        using var writer = new StringWriter();
        Exporter.Write(writer, result, includePasswords: true);
        var text = writer.ToString();
        var table = _csv.Read(new StringReader(text));

        int ResetRow(string dn) => Enumerable.Range(0, table.Rows.Count).Single(r => Cell(table, r, "DN") == dn && Cell(table, r, "Action") == "ResetPassword");
        var jan = ResetRow(_company.JanKowalski);
        var lukasz = ResetRow(_company.LukaszWisniewski);
        Assert.Equal(BatchReportExporter.StatusSucceeded, Cell(table, jan, "Status"));
        Assert.Equal(passwords[_company.JanKowalski], Cell(table, jan, "Password"));
        Assert.Equal(BatchReportExporter.StatusFailed, Cell(table, lukasz, "Status"));
        Assert.Equal(string.Empty, Cell(table, lukasz, "Password"));
        Assert.DoesNotContain(passwords[_company.LukaszWisniewski], text, StringComparison.Ordinal);
        Assert.True(BatchReportExporter.ContainsSecrets(result));
    }

    [Fact]
    public void Result_DoesNotContainThePasswordOfAResetThatNeverRan()
    {
        const string Password = "Nigdy!Uzyte2026";
        var modify = new PlannedChange
        {
            Action = ChangeAction.Modify,
            Attributes = [new AttributeChange(Attr.Title, AttributeChangeKind.Replace, [], ["Kierownik"])],
        };
        var reset = new PlannedChange { Action = ChangeAction.ResetPassword, Password = Password };
        var objectPlan = new ObjectPlan { DistinguishedName = _company.JanKowalski, DisplayName = "Jan Kowalski", Changes = [modify, reset] };
        var plan = new ChangePlan { Objects = [objectPlan] };
        var result = new BatchResult(
            plan,
            [new ObjectResult(objectPlan, ObjectResultStatus.Failed, [new ChangeResult(modify, false, DirectoryErrorKind.AccessDenied, "Denied.")],
                _company.JanKowalski, _time.GetUtcNow(), _time.GetUtcNow())],
            "dc01.contoso.test",
            "admin",
            _time.GetUtcNow(),
            _time.GetUtcNow(),
            WasCancelled: false);

        using var writer = new StringWriter();
        Exporter.Write(writer, result, includePasswords: true);
        var table = _csv.Read(new StringReader(writer.ToString()));

        Assert.DoesNotContain(Password, writer.ToString(), StringComparison.Ordinal);
        var row = Enumerable.Range(0, table.Rows.Count).Single(r => Cell(table, r, "Action") == "ResetPassword");
        Assert.Equal(BatchReportExporter.StatusNotExecuted, Cell(table, row, "Status"));
        Assert.False(BatchReportExporter.ContainsSecrets(result));
    }

    [Fact]
    public void SecretAttributeValues_AreMaskedUnlessPasswordsAreIncluded()
    {
        var schema = new SchemaCatalog(
            [new AttributeDefinition { LdapName = "contoso-Pin", Syntax = AttributeSyntax.UnicodeString, IsConfidential = true }], []);
        var modify = new PlannedChange
        {
            Action = ChangeAction.Modify,
            Attributes =
            [
                new AttributeChange("ms-Mcs-AdmPwd", AttributeChangeKind.Replace, ["Stare!Haslo"], ["Nowe!Haslo"]),
                new AttributeChange("contoso-Pin", AttributeChangeKind.Replace, ["1234"], ["Zażółć-9876"]),
                new AttributeChange("msLAPS-Password", AttributeChangeKind.Clear, [], []),
                new AttributeChange(Attr.Description, AttributeChangeKind.Replace, ["stary"], ["nowy"]),
            ],
        };
        var objectPlan = new ObjectPlan { DistinguishedName = _company.Computer1, DisplayName = "PC-01", Changes = [modify] };
        var plan = new ChangePlan { Objects = [objectPlan] };
        var result = new BatchResult(
            plan,
            [new ObjectResult(objectPlan, ObjectResultStatus.Succeeded, [new ChangeResult(modify, true)], _company.Computer1, _time.GetUtcNow(), _time.GetUtcNow())],
            "dc01.contoso.test",
            "admin",
            _time.GetUtcNow(),
            _time.GetUtcNow(),
            WasCancelled: false);

        var preview = Export(w => Exporter.Write(w, plan, schema: schema));
        var masked = Export(w => Exporter.Write(w, result, schema: schema));
        var revealed = Export(w => Exporter.Write(w, result, includePasswords: true, schema: schema));

        foreach (var table in new[] { preview, masked })
        {
            Assert.All(table.Rows, r => Assert.DoesNotContain(r, c => c.Contains("Haslo", StringComparison.Ordinal) || c.Contains("1234", StringComparison.Ordinal)
                || c.Contains("9876", StringComparison.Ordinal)));
            Assert.Equal([SecretAttributes.Mask, SecretAttributes.Mask, string.Empty, "stary"], Column(table, "Old value"));
            Assert.Equal([SecretAttributes.Mask, SecretAttributes.Mask, string.Empty, "nowy"], Column(table, "New value"));
        }

        Assert.Equal(["Stare!Haslo", "1234", string.Empty, "stary"], Column(revealed, "Old value"));
        Assert.Equal(["Nowe!Haslo", "Zażółć-9876", string.Empty, "nowy"], Column(revealed, "New value"));
        Assert.True(BatchReportExporter.ContainsSecrets(plan, schema));
        Assert.True(BatchReportExporter.ContainsSecrets(result, schema));
        Assert.False(BatchReportExporter.ContainsSecrets(plan with { Objects = [objectPlan with { Changes = [modify with { Attributes = [modify.Attributes[^1]] }] }] }, schema));
    }
}
