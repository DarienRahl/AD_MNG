using AdMng.Core.ActiveDirectory;
using AdMng.Core.Audit;
using AdMng.Core.Batch;
using AdMng.Core.Directory;
using AdMng.Core.Model;
using AdMng.Core.Security;
using AdMng.Core.Templates;
using AdMng.Testing;

namespace AdMng.Core.Tests.Batch;

/// <summary>
/// logonHours through the whole change pipeline: the binary value travels as hex (<see cref="PipelineValues"/>) through
/// the planner, the executor (bytes on the wire, compare-and-swap), the audit log, the rollback and the PowerShell export.
/// </summary>
public class LogonHoursPipelineTests
{
    private static readonly BatchOptions Fast = new() { InitialRetryDelay = TimeSpan.FromMilliseconds(1) };

    private static readonly DayOfWeek[] Workdays = [DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday];

    private readonly ManualTimeProvider _time = new();
    private readonly FakeDirectoryBuilder _builder;
    private readonly SampleCompany _company;
    private readonly InMemoryAuditLog _audit = new();

    public LogonHoursPipelineTests()
    {
        _builder = new FakeDirectoryBuilder(timeProvider: _time);
        _company = _builder.AddSampleCompany();
    }

    /// <summary>Monday–Friday 07:00–15:00 UTC (08:00–16:00 in Warsaw in winter).</summary>
    private static LogonHours Office { get; } = Utc((day, hour) => Workdays.Contains(day) && hour is >= 7 and < 15);

    /// <summary>Monday–Friday 15:00–23:00 UTC (the evening shift).</summary>
    private static LogonHours Evening { get; } = Utc((day, hour) => Workdays.Contains(day) && hour is >= 15 and < 23);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private FakeDirectorySession Session => _builder.Session;

    private static LogonHours Utc(Func<DayOfWeek, int, bool> permitted) => LogonHours.FromLocalGrid(LogonHoursGrid.Create(permitted), 0);

    private Task<ChangePlan> PlanAsync(IReadOnlyList<string> targets, params BatchOperation[] operations) =>
        new ChangePlanner(new TemplateEngine(), new PasswordGenerator(), _time)
            .PlanAsync(Session, new BatchRequest(targets, operations) { Title = "Godziny logowania" }, null, null, Ct);

    private async Task<ObjectPlan> PlanOneAsync(string target, params BatchOperation[] operations) =>
        Assert.Single((await PlanAsync([target], operations)).Objects);

    private Task<BatchResult> ExecuteAsync(ChangePlan plan) =>
        new BatchExecutor(_audit, _time).ExecuteAsync(Session, plan, Fast, null, Ct);

    private static AttributeChange LogonHoursChange(ObjectPlan plan) =>
        Assert.Single(Assert.Single(plan.Changes, c => c.Action == ChangeAction.Modify).Attributes, a => a.Attribute == Attr.LogonHours);

    private static IEnumerable<ValidationIssue> AllIssues(ObjectPlan plan) => plan.Issues.Concat(plan.Changes.SelectMany(c => c.Issues));

    /// <summary>The stored value as the DC holds it (must be binary, never the hex text).</summary>
    private byte[]? Stored(string dn)
    {
        var snapshot = Session.Snapshot(dn)!;
        return snapshot.TryGet(Attr.LogonHours, out var values) ? Assert.IsType<byte[]>(Assert.Single(values)) : null;
    }

    // ------------------------------------------------------------------ pipeline values

    [Fact]
    public void PipelineValues_EncodeOnlyLogonHoursAsHex()
    {
        var obj = new DirectoryObject("CN=Jan,DC=contoso,DC=test",
        [
            new AttributeValues(Attr.LogonHours, [Office.ToBytes()]),
            new AttributeValues(Attr.ThumbnailPhoto, [new byte[] { 0x41, 0x42 }]),
            new AttributeValues(Attr.Department, ["IT"]),
        ]);

        Assert.True(PipelineValues.IsHexEncoded("LOGONHOURS"));
        Assert.False(PipelineValues.IsHexEncoded(Attr.ThumbnailPhoto));
        Assert.Equal([Office.ToHex()], PipelineValues.Read(obj, Attr.LogonHours));
        Assert.Equal(["AB"], PipelineValues.Read(obj, Attr.ThumbnailPhoto));
        Assert.Equal(["IT"], PipelineValues.Read(obj, Attr.Department));
        Assert.Empty(PipelineValues.Read(obj, Attr.Title));
        Assert.Equal(Office.ToBytes(), Assert.IsType<byte[]>(PipelineValues.ToWire(Attr.LogonHours, Office.ToHex().ToUpperInvariant())));
        Assert.Equal("IT", PipelineValues.ToWire(Attr.Department, "IT"));
        Assert.Equal(Office.ToHex(), PipelineValues.Normalize(Attr.LogonHours, "  " + Office.ToHex().ToUpperInvariant()));
        Assert.Equal("Abc ", PipelineValues.Normalize(Attr.Department, "Abc "));
    }

    // ------------------------------------------------------------------ planner

    [Fact]
    public async Task Planner_WritesTheScheduleAsHex()
    {
        var plan = await PlanOneAsync(_company.JanKowalski, new SetLogonHoursOperation(Office));

        var change = LogonHoursChange(plan);
        Assert.False(plan.HasErrors);
        Assert.Equal(AttributeChangeKind.Replace, change.Kind);
        Assert.Empty(change.OldValues);
        Assert.Equal([Office.ToHex()], change.NewValues);
    }

    [Fact]
    public async Task Planner_AbsentAttributeEqualsAllHours_SoPermittingEverythingIsANoOp()
    {
        var absent = await PlanOneAsync(_company.JanKowalski, new SetLogonHoursOperation(LogonHours.All));
        Session.SetValues(_company.LukaszWisniewski, Attr.LogonHours, LogonHours.All.ToBytes());
        var allOnes = await PlanOneAsync(_company.LukaszWisniewski, new SetLogonHoursOperation(LogonHours.All));
        Session.SetValues(_company.MalgorzataWojcik, Attr.LogonHours, Office.ToBytes());
        var same = await PlanOneAsync(_company.MalgorzataWojcik, new SetLogonHoursOperation(Office));

        Assert.True(absent.IsNoOp);
        Assert.True(allOnes.IsNoOp);
        Assert.True(same.IsNoOp);
        Assert.Contains(absent.Issues, i => i.Code == IssueCodes.NoChange);
    }

    [Fact]
    public async Task Planner_PermittingEverythingOnARestrictedAccount_WritesAllOnes_LikeAduc()
    {
        Session.SetValues(_company.JanKowalski, Attr.LogonHours, Office.ToBytes());

        var plan = await PlanOneAsync(_company.JanKowalski, new SetLogonHoursOperation(LogonHours.All));

        var change = LogonHoursChange(plan);
        Assert.Equal(AttributeChangeKind.Replace, change.Kind);
        Assert.Equal([Office.ToHex()], change.OldValues);
        Assert.Equal([new string('f', 42)], change.NewValues);
    }

    [Fact]
    public async Task Planner_ReplacesAnInvalidStoredValue()
    {
        Session.SetValues(_company.JanKowalski, Attr.LogonHours, new byte[] { 0xFF, 0x00, 0xFF });

        var plan = await PlanOneAsync(_company.JanKowalski, new SetLogonHoursOperation(LogonHours.All));

        var change = LogonHoursChange(plan);
        Assert.Equal(["ff00ff"], change.OldValues);
        Assert.Equal([LogonHours.All.ToHex()], change.NewValues);
    }

    [Fact]
    public async Task Planner_AppliesOnlyToUserAccounts()
    {
        var plan = await PlanAsync([_company.Computer1, _company.GroupIt, _company.JanKowalski], new SetLogonHoursOperation(LogonHours.None));

        var computer = plan.Objects[0];
        var group = plan.Objects[1];
        Assert.True(computer.IsNoOp);
        Assert.Contains(computer.Issues, i => i.Code == IssueCodes.NotApplicable && i.Severity == IssueSeverity.Warning);
        Assert.True(group.IsNoOp);
        Assert.Contains(group.Issues, i => i.Code == IssueCodes.NotApplicable);
        Assert.Equal([LogonHours.None.ToHex()], LogonHoursChange(plan.Objects[2]).NewValues);
    }

    [Fact]
    public async Task Planner_ChecksThePermissionToWriteLogonHours()
    {
        Session.SetPermissions(_company.JanKowalski, new EffectivePermissions
        {
            IsKnown = true,
            WritableAttributes = new HashSet<string>([Attr.Description], StringComparer.OrdinalIgnoreCase),
        });

        var plan = await PlanOneAsync(_company.JanKowalski, new SetLogonHoursOperation(Office));

        Assert.True(plan.HasErrors);
        Assert.Contains(AllIssues(plan), i => i.Code == IssueCodes.NotWritable && i.Attribute == Attr.LogonHours);
    }

    [Fact]
    public async Task Planner_AcceptsHexValuesForLogonHours_ButNoOtherBinaryAttribute()
    {
        var spaced = string.Join(' ', Office.ToBytes().Select(b => b.ToString("X2", System.Globalization.CultureInfo.InvariantCulture)));
        var valid = await PlanOneAsync(_company.JanKowalski, new SetValuesOperation(Attr.LogonHours, [spaced]));
        var invalid = await PlanOneAsync(_company.JanKowalski, new SetValuesOperation(Attr.LogonHours, ["ffff"]));
        var photo = await PlanOneAsync(_company.JanKowalski, new SetValuesOperation(Attr.ThumbnailPhoto, ["ffff"]));

        Assert.False(valid.HasErrors);
        Assert.Equal([Office.ToHex()], LogonHoursChange(valid).NewValues);
        Assert.Contains(AllIssues(invalid), i => i.Code == IssueCodes.InvalidValue && i.Attribute == Attr.LogonHours && i.Severity == IssueSeverity.Error);
        Assert.Contains(AllIssues(photo), i => i.Code == IssueCodes.BinaryNotSupported);
    }

    // ------------------------------------------------------------------ executor, audit, rollback

    [Fact]
    public async Task Executor_WritesTheBytes_WithCompareAndSwap_AndAuditsHex()
    {
        Session.SetValues(_company.JanKowalski, Attr.LogonHours, Office.ToBytes());
        var plan = await PlanAsync([_company.JanKowalski], new SetLogonHoursOperation(Evening));
        Session.ClearOperations();

        var result = await ExecuteAsync(plan);

        Assert.Equal(1, result.SucceededCount);
        Assert.Equal(Evening.ToBytes(), Stored(_company.JanKowalski));
        var modify = Assert.Single(Session.Writes, w => w.Kind == FakeOperationKind.Modify);
        Assert.Equal(
            [(ModificationOperation.Delete, Office.ToHex()), (ModificationOperation.Add, Evening.ToHex())],
            modify.Modifications.Select(m => (m.Operation, Convert.ToHexStringLower(Assert.IsType<byte[]>(Assert.Single(m.Values))))));
        var entry = Assert.Single(_audit.Entries, e => e.Attribute == Attr.LogonHours);
        Assert.Equal(Office.ToHex(), entry.OldValue);
        Assert.Equal(Evening.ToHex(), entry.NewValue);
        Assert.True(entry.Succeeded);
    }

    [Fact]
    public async Task Executor_ReportsAConflict_WhenSomebodyChangedTheHoursAfterThePreview()
    {
        Session.SetValues(_company.JanKowalski, Attr.LogonHours, Office.ToBytes());
        var plan = await PlanAsync([_company.JanKowalski], new SetLogonHoursOperation(Evening));
        Session.SetValues(_company.JanKowalski, Attr.LogonHours, LogonHours.None.ToBytes());

        var result = await ExecuteAsync(plan);

        var change = Assert.Single(Assert.Single(result.Objects).Changes);
        Assert.False(change.Succeeded);
        Assert.StartsWith(BatchExecutor.ConflictMessagePrefix, change.ErrorMessage, StringComparison.Ordinal);
        Assert.Equal(LogonHours.None.ToBytes(), Stored(_company.JanKowalski));
    }

    [Fact]
    public async Task Executor_ConfirmsALostResponse_ByReadingTheBytesBack()
    {
        Session.SetValues(_company.JanKowalski, Attr.LogonHours, Office.ToBytes());
        var plan = await PlanAsync([_company.JanKowalski], new SetLogonHoursOperation(Evening));
        Session.LoseNextResponse(FakeOperationKind.Modify, _company.JanKowalski);

        var result = await ExecuteAsync(plan);

        var change = Assert.Single(Assert.Single(result.Objects).Changes);
        Assert.True(change.Succeeded);
        Assert.Equal(2, change.Attempts);
        Assert.Equal(Evening.ToBytes(), Stored(_company.JanKowalski));
    }

    [Fact]
    public async Task Rollback_RestoresThePreviousBytes_OrRemovesTheAttribute()
    {
        Session.SetValues(_company.JanKowalski, Attr.LogonHours, Office.ToBytes());
        var plan = await PlanAsync([_company.JanKowalski, _company.LukaszWisniewski], new SetLogonHoursOperation(LogonHours.None));
        var result = await ExecuteAsync(plan);
        Assert.Equal(LogonHours.None.ToBytes(), Stored(_company.JanKowalski));
        Assert.Equal(LogonHours.None.ToBytes(), Stored(_company.LukaszWisniewski));

        var rollback = new RollbackPlanner().CreateRollbackPlan(result);
        var undone = await ExecuteAsync(rollback);

        Assert.Equal(2, undone.SucceededCount);
        Assert.Equal(Office.ToBytes(), Stored(_company.JanKowalski));
        Assert.Null(Stored(_company.LukaszWisniewski));
    }

    [Fact]
    public async Task PowerShellExport_WritesAByteArrayLiteral()
    {
        var plan = await PlanAsync([_company.JanKowalski], new SetLogonHoursOperation(Office));

        var script = new PowerShellExporter().Export(plan, null, Session.Schema);

        var bytes = string.Join(",", Office.ToBytes().Select(b => "0x" + b.ToString("x2", System.Globalization.CultureInfo.InvariantCulture)));
        Assert.Contains($"Set-ADObject -Identity $dn -Replace @{{logonHours=[byte[]]({bytes})}}", script, StringComparison.Ordinal);
    }

    [Fact]
    public void Operation_PrintsTheSchedule()
    {
        Assert.Equal($"SetLogonHoursOperation {{ Hours = {Office.ToHex()} }}", new SetLogonHoursOperation(Office).ToString());
    }
}
