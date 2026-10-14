using AdMng.Core.ActiveDirectory;
using AdMng.Core.Audit;
using AdMng.Core.Batch;
using AdMng.Core.Directory;
using AdMng.Core.Security;
using AdMng.Core.Templates;
using AdMng.Testing;

namespace AdMng.Core.Tests.Batch;

public class BatchExecutorTests
{
    private static readonly BatchOptions Fast = new() { InitialRetryDelay = TimeSpan.FromMilliseconds(1) };

    private readonly ManualTimeProvider _time = new();
    private readonly FakeDirectoryBuilder _builder;
    private readonly SampleCompany _company;
    private readonly InMemoryAuditLog _audit = new();
    private readonly ListLogger<BatchExecutor> _logger = new();

    public BatchExecutorTests()
    {
        _builder = new FakeDirectoryBuilder(timeProvider: _time);
        _company = _builder.AddSampleCompany();
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private FakeDirectorySession Session => _builder.Session;

    private BatchExecutor Executor => new(_audit, _time, _logger);

    private Task<ChangePlan> PlanAsync(IReadOnlyList<string> targets, params BatchOperation[] operations) =>
        new ChangePlanner(new TemplateEngine(), new PasswordGenerator(), _time)
            .PlanAsync(Session, new BatchRequest(targets, operations) { Title = "Test" }, null, null, Ct);

    private Task<BatchResult> ExecuteAsync(ChangePlan plan, BatchOptions? options = null, IProgress<BatchProgress>? progress = null, CancellationToken? token = null) =>
        Executor.ExecuteAsync(Session, plan, options ?? Fast, progress, token ?? Ct);

    private async Task<(ChangePlan Plan, BatchResult Result)> RunAsync(IReadOnlyList<string> targets, params BatchOperation[] operations)
    {
        var plan = await PlanAsync(targets, operations);
        Session.ClearOperations();
        return (plan, await ExecuteAsync(plan));
    }

    private string Value(string dn, string attribute) => Assert.Single(Session.GetValues(dn, attribute));

    private List<FakeOperation> Modifies(string dn) =>
        Session.Writes.Where(w => w.Kind == FakeOperationKind.Modify && DistinguishedNameEquals(w.DistinguishedName, dn)).ToList();

    private static bool DistinguishedNameEquals(string a, string b) => AdMng.Core.Ldap.DistinguishedName.AreEqual(a, b);

    [Fact]
    public async Task Modify_UsesCompareAndSwap()
    {
        var (_, result) = await RunAsync([_company.JanKowalski, _company.LukaszWisniewski], new SetAttributeOperation(Attr.Title, "Kierownik"));

        Assert.Equal(2, result.SucceededCount);
        Assert.All(result.Objects, o => Assert.Equal(ObjectResultStatus.Succeeded, o.Status));
        Assert.Equal("Kierownik", Value(_company.JanKowalski, Attr.Title));
        var modify = Assert.Single(Modifies(_company.JanKowalski));
        Assert.Equal(
            [(Attr.Title, ModificationOperation.Delete, "Administrator systemów"), (Attr.Title, ModificationOperation.Add, "Kierownik")],
            modify.Modifications.Select(m => (m.Attribute, m.Operation, (string)Assert.Single(m.Values))));
        Assert.False(modify.ModifyOptions?.Permissive ?? false);
        Assert.Equal("dc01.contoso.test", result.Server);
        Assert.Equal(@"CONTOSO\Administrator", result.Operator);
        Assert.False(result.WasCancelled);
    }

    [Fact]
    public async Task Modify_AddsWhenEmptyAndDeletesWhenClearing()
    {
        await RunAsync([_company.JanKowalski], new SetAttributeOperation(Attr.Info, "Nowa notatka"), new ClearAttributeOperation(Attr.Department));

        var modify = Assert.Single(Modifies(_company.JanKowalski));
        Assert.Equal(
            [(Attr.Info, ModificationOperation.Add), (Attr.Department, ModificationOperation.Delete)],
            modify.Modifications.Select(m => (m.Attribute, m.Operation)));
        Assert.Empty(Session.GetValues(_company.JanKowalski, Attr.Department));
        Assert.Equal("Nowa notatka", Value(_company.JanKowalski, Attr.Info));
    }

    [Fact]
    public async Task Modify_ReportsConcurrentChangeAsConflict()
    {
        var plan = await PlanAsync([_company.JanKowalski], new SetAttributeOperation(Attr.Title, "Kierownik"));
        Session.SetValues(_company.JanKowalski, Attr.Title, "Zmienione przez kogoś innego");

        var result = await ExecuteAsync(plan);

        var objectResult = Assert.Single(result.Objects);
        var change = Assert.Single(objectResult.Changes);
        Assert.Equal(ObjectResultStatus.Failed, objectResult.Status);
        Assert.Equal(DirectoryErrorKind.NoSuchAttribute, change.ErrorKind);
        Assert.StartsWith(BatchExecutor.ConflictMessagePrefix, change.ErrorMessage, StringComparison.Ordinal);
        Assert.Equal("Zmienione przez kogoś innego", Value(_company.JanKowalski, Attr.Title));
        Assert.Equal(1, result.FailedCount);
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData(0x2081, true)] // ERROR_DS_SINGLE_VALUE_CONSTRAINT: the attribute got a value after the preview
    [InlineData(0x2082, false)] // ERROR_DS_RANGE_CONSTRAINT: a plain validation error, not a conflict
    public async Task Modify_ConstraintViolationIsAConflictOnlyForSingleValueConstraint(int? win32Error, bool isConflict)
    {
        var plan = await PlanAsync([_company.JanKowalski], new SetAttributeOperation(Attr.Title, "Kierownik"));
        Session.BeforeOperation = operation =>
        {
            if (operation.Kind == FakeOperationKind.Modify)
            {
                throw new DirectoryException(DirectoryErrorKind.ConstraintViolation, "Constraint violation.") { LdapResultCode = 19, Win32Error = win32Error };
            }
        };

        var result = await ExecuteAsync(plan);

        var change = Assert.Single(Assert.Single(result.Objects).Changes);
        Assert.False(change.Succeeded);
        Assert.Equal(DirectoryErrorKind.ConstraintViolation, change.ErrorKind);
        Assert.Equal(isConflict, change.ErrorMessage!.StartsWith(BatchExecutor.ConflictMessagePrefix, StringComparison.Ordinal));
    }

    [Fact]
    public async Task ClearOfARangedAttribute_RemovesEveryValue()
    {
        Session.MaxValueRange = 2;

        var (_, result) = await RunAsync([_company.GroupAccounting], new ClearAttributeOperation(Attr.Member));

        Assert.Equal(ObjectResultStatus.Succeeded, Assert.Single(result.Objects).Status);
        Assert.Empty(Session.GetValues(_company.GroupAccounting, Attr.Member));
    }

    [Fact]
    public async Task ThrowingAuditSubscriber_DoesNotAbortTheBatch()
    {
        var plan = await PlanAsync([_company.JanKowalski, _company.LukaszWisniewski],
            new SetAttributeOperation(Attr.Title, "Kierownik"),
            new SetAttributeOperation(Attr.Department, "Zarząd"));
        _audit.EntryRecorded += (_, _) => throw new InvalidOperationException("UI thread only");

        var result = await ExecuteAsync(plan);

        Assert.Equal(2, result.SucceededCount);
        Assert.All(result.Objects, o => Assert.NotNull(o));
        Assert.Equal("Kierownik", Value(_company.LukaszWisniewski, Attr.Title));
        Assert.Equal(4, _audit.Entries.Count);
        Assert.Contains(_logger.Entries, e => e.Message.Contains("InvalidOperationException", StringComparison.Ordinal));
        Assert.DoesNotContain("UI thread only", _logger.AllText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InvalidCreateStep_FailsOnlyThatObject()
    {
        var dn = "CN=Anna Nowak," + _company.SalesOu;
        var invalid = new ObjectPlan
        {
            DistinguishedName = dn,
            DisplayName = "Anna Nowak",
            Changes =
            [
                new PlannedChange
                {
                    Action = ChangeAction.Create,
                    Attributes =
                    [
                        new AttributeChange(Attr.ObjectClass, AttributeChangeKind.Replace, [], ["user"]),
                        new AttributeChange(Attr.Department, AttributeChangeKind.Replace, [], ["Sprzedaż"]),
                        new AttributeChange("DEPARTMENT", AttributeChangeKind.Replace, [], ["Zakupy"]),
                    ],
                },
            ],
        };
        var other = (await PlanAsync([_company.JanKowalski], new SetAttributeOperation(Attr.Title, "Kierownik"))).Objects[0];

        var result = await ExecuteAsync(new ChangePlan { Server = Session.Info.Server, Objects = [invalid, other] });

        Assert.Equal([ObjectResultStatus.Failed, ObjectResultStatus.Succeeded], result.Objects.Select(o => o.Status));
        Assert.Equal(DirectoryErrorKind.Unknown, Assert.Single(result.Objects[0].Changes).ErrorKind);
        Assert.False(Session.Exists(dn));
    }

    [Fact]
    public async Task Modify_WithoutOptimisticConcurrencyOverwrites()
    {
        var plan = await PlanAsync([_company.JanKowalski], new SetAttributeOperation(Attr.Title, "Kierownik"));
        Session.SetValues(_company.JanKowalski, Attr.Title, "Zmienione przez kogoś innego");
        Session.ClearOperations();

        var result = await ExecuteAsync(plan, Fast with { OptimisticConcurrency = false });

        Assert.Equal(ObjectResultStatus.Succeeded, Assert.Single(result.Objects).Status);
        Assert.Equal("Kierownik", Value(_company.JanKowalski, Attr.Title));
        Assert.Equal(ModificationOperation.Replace, Assert.Single(Assert.Single(Modifies(_company.JanKowalski)).Modifications).Operation);
    }

    [Fact]
    public async Task Modify_SendsValueAdditionsPermissivelyInASeparateRequest()
    {
        Session.SetValues(_company.JanKowalski, Attr.OtherTelephone, "111", "222");

        await RunAsync([_company.JanKowalski],
            new SetAttributeOperation(Attr.Title, "Kierownik"),
            new AddValuesOperation(Attr.ProxyAddresses, ["smtp:jan@firma.pl"]),
            new RemoveValuesOperation(Attr.OtherTelephone, ["222"]));

        var modifies = Modifies(_company.JanKowalski);
        Assert.Equal(2, modifies.Count);
        Assert.False(modifies[0].ModifyOptions!.Permissive);
        Assert.All(modifies[0].Modifications, m => Assert.Equal(Attr.Title, m.Attribute));
        Assert.True(modifies[1].ModifyOptions!.Permissive);
        Assert.Equal(
            [(Attr.ProxyAddresses, ModificationOperation.Add), (Attr.OtherTelephone, ModificationOperation.Delete)],
            modifies[1].Modifications.Select(m => (m.Attribute, m.Operation)));
        Assert.Equal(["111"], Session.GetValues(_company.JanKowalski, Attr.OtherTelephone));
    }

    [Fact]
    public async Task RejectedValueAddition_AfterAnAppliedReplace_IsReportedAuditedAndRolledBackSeparately()
    {
        // Property sheet of a global group: new description (strict replace) + a member the DC refuses (permissive add).
        var plan = await PlanAsync([_company.GroupIt],
            new SetAttributeOperation(Attr.Description, "Dział IT"),
            new AddValuesOperation(Attr.Member, [_company.GroupAll]));
        Session.ClearOperations();
        Session.BeforeOperation = operation =>
        {
            if (operation.Kind == FakeOperationKind.Modify && operation.ModifyOptions?.Permissive == true)
            {
                throw new DirectoryException(DirectoryErrorKind.ConstraintViolation, "A domain local group cannot be a member of a global group.")
                {
                    LdapResultCode = 19,
                };
            }
        };

        var result = await ExecuteAsync(plan);

        var steps = Assert.Single(plan.Objects).Changes;
        Assert.Equal([ChangeAction.Modify, ChangeAction.Modify], steps.Select(c => c.Action));
        Assert.Equal([Attr.Description], steps[0].Attributes.Select(a => a.Attribute));
        Assert.Equal([Attr.Member], steps[1].Attributes.Select(a => a.Attribute));
        var group = Assert.Single(result.Objects);
        Assert.Equal(ObjectResultStatus.PartiallySucceeded, group.Status);
        Assert.Equal([true, false], group.Changes.Select(c => c.Succeeded));
        Assert.Equal("Dział IT", Value(_company.GroupIt, Attr.Description));
        Assert.Contains(_audit.Entries, e => e is { Attribute: Attr.Description, Succeeded: true, NewValue: "Dział IT" });
        Assert.Contains(_audit.Entries, e => e is { Attribute: Attr.Member, Succeeded: false });

        var rollback = Assert.Single(new RollbackPlanner().CreateRollbackPlan(result).Objects);
        var undo = Assert.Single(rollback.Changes);
        var restore = Assert.Single(undo.Attributes);
        Assert.Equal(Attr.Description, restore.Attribute);
        Assert.Equal(AttributeChangeKind.Clear, restore.Kind);
    }

    [Fact]
    public async Task LostResponse_OfACompareAndSwapModify_IsConfirmedByReadingTheValuesBack()
    {
        var plan = await PlanAsync([_company.JanKowalski], new SetAttributeOperation(Attr.Title, "Kierownik"), new SetAttributeOperation(Attr.Department, "Zarząd"));
        Session.LoseNextResponse(FakeOperationKind.Modify, _company.JanKowalski);

        var result = await ExecuteAsync(plan);

        var jan = Assert.Single(result.Objects);
        var change = Assert.Single(jan.Changes);
        Assert.Equal(ObjectResultStatus.Succeeded, jan.Status);
        Assert.True(change.Succeeded);
        Assert.Null(change.ErrorMessage);
        Assert.Equal(2, change.Attempts);
        Assert.Equal("Kierownik", Value(_company.JanKowalski, Attr.Title));
        Assert.All(_audit.Entries, e => Assert.True(e.Succeeded));
    }

    [Fact]
    public async Task ConflictAfterATimeoutThatWasNotApplied_StaysAConflict()
    {
        var plan = await PlanAsync([_company.JanKowalski], new SetAttributeOperation(Attr.Title, "Kierownik"));
        Session.FailNext(FakeOperationKind.Modify, _company.JanKowalski, DirectoryErrorKind.Timeout);
        var calls = 0;
        Session.BeforeOperation = operation =>
        {
            if (operation.Kind == FakeOperationKind.Modify && ++calls == 2)
            {
                // Somebody else changes the title between the timed-out attempt and the retry.
                Session.SetValues(_company.JanKowalski, Attr.Title, "Zmienione przez kogoś innego");
            }
        };

        var result = await ExecuteAsync(plan);

        var change = Assert.Single(Assert.Single(result.Objects).Changes);
        Assert.False(change.Succeeded);
        Assert.Equal(2, change.Attempts);
        Assert.StartsWith(BatchExecutor.ConflictMessagePrefix, change.ErrorMessage, StringComparison.Ordinal);
        Assert.Equal("Zmienione przez kogoś innego", Value(_company.JanKowalski, Attr.Title));
    }

    [Fact]
    public async Task LostResponse_OfACreate_IsConfirmedAndTheFollowingStepsRun()
    {
        var dn = "CN=Anna Nowak," + _company.SalesOu;
        var plan = NewUserPlan(dn, "anna.nowak");
        Session.LoseNextResponse(FakeOperationKind.Add, dn);

        var result = await ExecuteAsync(plan);

        var anna = Assert.Single(result.Objects);
        Assert.Equal(ObjectResultStatus.Succeeded, anna.Status);
        Assert.Equal([2, 1, 1], anna.Changes.Select(c => c.Attempts));
        Assert.All(anna.Changes, c => Assert.True(c.Succeeded));
        Assert.Single(Session.PasswordResets);
        Assert.Equal("512", Value(dn, Attr.UserAccountControl));
        Assert.Equal(ChangeAction.Delete, Assert.Single(Assert.Single(new RollbackPlanner().CreateRollbackPlan(result).Objects).Changes).Action);
    }

    [Fact]
    public async Task ExistingObjectAfterATimeoutThatWasNotApplied_IsNotTakenForTheCreatedOne()
    {
        var dn = "CN=Anna Nowak," + _company.SalesOu;
        var plan = NewUserPlan(dn, "anna.nowak");
        Session.FailNext(FakeOperationKind.Add, dn, DirectoryErrorKind.Timeout);
        var adds = 0;
        Session.BeforeOperation = operation =>
        {
            if (operation.Kind == FakeOperationKind.Add && ++adds == 2)
            {
                // Somebody else creates an object with the same name between the timed-out attempt and the retry.
                _builder.AddUser("Anna", "Nowak", _company.SalesOu, new Dictionary<string, object?> { [Attr.SamAccountName] = "anna.nowak2" });
            }
        };

        var result = await ExecuteAsync(plan);

        var anna = Assert.Single(result.Objects);
        Assert.Equal(ObjectResultStatus.Failed, anna.Status);
        var create = Assert.Single(anna.Changes);
        Assert.Equal(DirectoryErrorKind.AlreadyExists, create.ErrorKind);
        Assert.Equal(2, create.Attempts);
        Assert.Empty(Session.PasswordResets);
        Assert.Equal("anna.nowak2", Value(dn, Attr.SamAccountName));
    }

    /// <summary>The steps of the New User wizard: create disabled, set the password, enable.</summary>
    private ChangePlan NewUserPlan(string dn, string sam) => new()
    {
        Server = Session.Info.Server,
        Objects =
        [
            new ObjectPlan
            {
                DistinguishedName = dn,
                DisplayName = "Anna Nowak",
                Changes =
                [
                    new PlannedChange
                    {
                        Action = ChangeAction.Create,
                        Attributes =
                        [
                            new AttributeChange(Attr.ObjectClass, AttributeChangeKind.Replace, [], ["user"]),
                            new AttributeChange(Attr.SamAccountName, AttributeChangeKind.Replace, [], [sam]),
                            new AttributeChange(Attr.GivenName, AttributeChangeKind.Replace, [], ["Anna"]),
                            new AttributeChange(Attr.UserAccountControl, AttributeChangeKind.Replace, [], ["514"]),
                        ],
                    },
                    new PlannedChange { Action = ChangeAction.ResetPassword, Password = "Wi0sna!2026xyz" },
                    new PlannedChange
                    {
                        Action = ChangeAction.Modify,
                        Attributes = [new AttributeChange(Attr.UserAccountControl, AttributeChangeKind.Replace, ["514"], ["512"])],
                    },
                ],
            },
        ],
    };

    [Fact]
    public async Task SpecialAttributes_AreAlwaysReplaced()
    {
        var (plan, result) = await RunAsync([_company.ZanetaSzymanska], new ResetPasswordOperation(PasswordMode.GenerateUnique));

        var modify = Assert.Single(Modifies(_company.ZanetaSzymanska));
        Assert.All(modify.Modifications, m => Assert.Equal(ModificationOperation.Replace, m.Operation));
        Assert.Equal("0", Value(_company.ZanetaSzymanska, Attr.PwdLastSet));
        Assert.Equal("0", Value(_company.ZanetaSzymanska, Attr.LockoutTime));
        var reset = Assert.Single(Session.PasswordResets);
        Assert.Equal(plan.Objects[0].Changes[0].Password, reset.Password);
        Assert.Equal(ObjectResultStatus.Succeeded, Assert.Single(result.Objects).Status);
    }

    [Fact]
    public async Task Passwords_NeverReachAuditOrLogs()
    {
        var (plan, _) = await RunAsync([_company.JanKowalski, _company.PawelLewandowski], new ResetPasswordOperation(PasswordMode.GenerateUnique));
        var passwords = plan.Objects.Select(o => o.Changes[0].Password!).ToList();

        var auditText = string.Join("|", _audit.Entries.SelectMany(e => new[] { e.Action, e.Attribute, e.OldValue, e.NewValue, e.Error, e.ToString() }));
        Assert.Contains(_audit.Entries, e => e is { Action: "ResetPassword", Succeeded: true, OldValue: null, NewValue: null });
        Assert.All(passwords, p => Assert.DoesNotContain(p, auditText, StringComparison.Ordinal));
        Assert.All(passwords, p => Assert.DoesNotContain(p, _logger.AllText, StringComparison.Ordinal));
        Assert.DoesNotContain(_company.JanKowalski, _logger.AllText, StringComparison.OrdinalIgnoreCase);
        Assert.NotEmpty(_logger.Entries);
    }

    [Fact]
    public async Task Membership_ModifiesTheGroupPermissively()
    {
        await RunAsync([_company.JanKowalski], new AddToGroupsOperation([_company.GroupSales]), new RemoveFromGroupsOperation([_company.GroupIt]));

        var add = Assert.Single(Modifies(_company.GroupSales));
        var remove = Assert.Single(Modifies(_company.GroupIt));
        Assert.True(add.ModifyOptions!.Permissive);
        Assert.Equal((Attr.Member, ModificationOperation.Add, (object)_company.JanKowalski), (add.Modifications[0].Attribute, add.Modifications[0].Operation, add.Modifications[0].Values[0]));
        Assert.Equal(ModificationOperation.Delete, remove.Modifications[0].Operation);
        Assert.Equal([_company.GroupSales], Session.GetValues(_company.JanKowalski, Attr.MemberOf));
        Assert.Contains(_audit.Entries, e => e is { Action: "AddMember", Attribute: Attr.MemberOf } && e.NewValue == _company.GroupSales);
        Assert.Contains(_audit.Entries, e => e is { Action: "RemoveMember" } && e.OldValue == _company.GroupIt);
    }

    [Fact]
    public async Task RenameAndMove_TrackTheCurrentDn()
    {
        var (_, result) = await RunAsync([_company.JanKowalski],
            new RenameOperation("{givenName} {sn}-Nowak"),
            new MoveOperation(_company.SalesOu),
            new AddToGroupsOperation([_company.GroupSales]));

        var expected = "CN=Jan Kowalski-Nowak," + _company.SalesOu;
        var objectResult = Assert.Single(result.Objects);
        Assert.Equal(ObjectResultStatus.Succeeded, objectResult.Status);
        Assert.Equal(expected, objectResult.FinalDn);
        Assert.True(Session.Exists(expected));
        var moves = Session.Writes.Where(w => w.Kind == FakeOperationKind.Move).ToList();
        Assert.Equal(_company.JanKowalski, moves[0].DistinguishedName);
        Assert.Equal("CN=Jan Kowalski-Nowak", moves[0].NewRdn);
        Assert.Equal("CN=Jan Kowalski-Nowak," + _company.ItOu, moves[1].DistinguishedName);
        Assert.Equal(_company.SalesOu, moves[1].NewParentDn);
        Assert.Contains(expected, Session.GetValues(_company.GroupSales, Attr.Member));
        var move = Assert.Single(_audit.Entries, e => e.Action == "Move");
        Assert.Equal("CN=Jan Kowalski-Nowak," + _company.ItOu, move.OldValue);
        Assert.Equal(expected, move.NewValue);
    }

    [Fact]
    public async Task TransientErrors_AreRetried()
    {
        var plan = await PlanAsync([_company.JanKowalski], new SetAttributeOperation(Attr.Title, "Kierownik"));
        Session.FailNext(FakeOperationKind.Modify, _company.JanKowalski, DirectoryErrorKind.Busy, times: 2);

        var result = await ExecuteAsync(plan);

        var change = Assert.Single(Assert.Single(result.Objects).Changes);
        Assert.True(change.Succeeded);
        Assert.Equal(3, change.Attempts);
        Assert.Equal("Kierownik", Value(_company.JanKowalski, Attr.Title));
        Assert.Equal(2, _logger.Entries.Count(e => e.Message.Contains("Transient Busy", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task TransientErrors_FailAfterMaxRetries()
    {
        var plan = await PlanAsync([_company.JanKowalski], new SetAttributeOperation(Attr.Title, "Kierownik"));
        Session.FailNext(FakeOperationKind.Modify, _company.JanKowalski, DirectoryErrorKind.ServerUnavailable, times: 5);

        var result = await ExecuteAsync(plan, Fast with { MaxRetries = 1 });

        var change = Assert.Single(Assert.Single(result.Objects).Changes);
        Assert.False(change.Succeeded);
        Assert.Equal(DirectoryErrorKind.ServerUnavailable, change.ErrorKind);
        Assert.Equal(2, change.Attempts);
    }

    [Fact]
    public async Task LostResponses_OfRenameAndMove_AreConfirmedAfterTheRetry()
    {
        var plan = await PlanAsync([_company.JanKowalski], new RenameOperation("{givenName} {sn}-Nowak"), new MoveOperation(_company.SalesOu));
        Session.LoseNextResponse(FakeOperationKind.Move, _company.JanKowalski);
        Session.LoseNextResponse(FakeOperationKind.Move, "CN=Jan Kowalski-Nowak," + _company.ItOu, DirectoryErrorKind.ServerUnavailable);

        var result = await ExecuteAsync(plan);

        var expected = "CN=Jan Kowalski-Nowak," + _company.SalesOu;
        var jan = Assert.Single(result.Objects);
        Assert.Equal(ObjectResultStatus.Succeeded, jan.Status);
        Assert.Equal([2, 2], jan.Changes.Select(c => c.Attempts));
        Assert.Equal(expected, jan.FinalDn);
        Assert.True(Session.Exists(expected));
        Assert.All(_audit.Entries, e => Assert.True(e.Succeeded));
    }

    [Fact]
    public async Task LostResponse_OfDelete_IsConfirmedAfterTheRetry()
    {
        var plan = await PlanAsync([_company.JanKowalski], new DeleteOperation());
        Session.LoseNextResponse(FakeOperationKind.Delete, _company.JanKowalski);

        var result = await ExecuteAsync(plan);

        var change = Assert.Single(Assert.Single(result.Objects).Changes);
        Assert.True(change.Succeeded);
        Assert.Equal(2, change.Attempts);
        Assert.False(Session.Exists(_company.JanKowalski));
    }

    [Fact]
    public async Task NoSuchObjectWithoutAnEarlierTransientError_StaysAFailure()
    {
        var plan = await PlanAsync([_company.JanKowalski, _company.LukaszWisniewski], new MoveOperation(_company.SalesOu));
        Session.FailNext(FakeOperationKind.Move, _company.JanKowalski, DirectoryErrorKind.NoSuchObject);
        Session.FailNext(FakeOperationKind.Move, _company.LukaszWisniewski, DirectoryErrorKind.Timeout);
        Session.FailNext(FakeOperationKind.Move, _company.LukaszWisniewski, DirectoryErrorKind.NoSuchObject);

        var result = await ExecuteAsync(plan);

        // Jan: a plain failure. Łukasz: the timed-out attempt was not applied, so the retry's noSuchObject is real.
        Assert.All(result.Objects, o => Assert.Equal(ObjectResultStatus.Failed, o.Status));
        Assert.All(result.Objects, o => Assert.Equal(DirectoryErrorKind.NoSuchObject, Assert.Single(o.Changes).ErrorKind));
        Assert.Equal([1, 2], result.Objects.Select(o => o.Changes[0].Attempts));
        Assert.True(Session.Exists(_company.LukaszWisniewski));
    }

    [Fact]
    public async Task FailedStep_StopsTheObjectAndReportsPartialSuccess()
    {
        var plan = await PlanAsync([_company.JanKowalski, _company.LukaszWisniewski],
            new SetAttributeOperation(Attr.Title, "Kierownik"),
            new AddToGroupsOperation([_company.GroupSales]),
            new MoveOperation(_company.SalesOu));
        Session.FailNext(FakeOperationKind.Modify, _company.GroupSales, DirectoryErrorKind.AccessDenied, times: 1);
        Session.FailNext(FakeOperationKind.Modify, _company.LukaszWisniewski, DirectoryErrorKind.AccessDenied, times: 1);

        var result = await ExecuteAsync(plan, Fast with { MaxDegreeOfParallelism = 1 });

        var jan = result.Objects[0];
        Assert.Equal(ObjectResultStatus.PartiallySucceeded, jan.Status);
        Assert.Equal([true, false], jan.Changes.Select(c => c.Succeeded));
        Assert.Equal(DirectoryErrorKind.AccessDenied, jan.Changes[1].ErrorKind);
        Assert.Equal(1, jan.Changes[1].Attempts);
        Assert.Equal(_company.JanKowalski, jan.FinalDn);
        var lukasz = result.Objects[1];
        Assert.Equal(ObjectResultStatus.Failed, lukasz.Status);
        Assert.Single(lukasz.Changes);
        Assert.True(Session.Exists(_company.LukaszWisniewski));
        Assert.Equal(2, result.FailedCount);
        Assert.Contains(_audit.Entries, e => !e.Succeeded && e.Action == "AddMember" && e.Error!.Contains("Injected", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ObjectsWithErrorsOrNoChanges_AreSkipped()
    {
        var plan = await PlanAsync(
            [_company.JanKowalski, _company.PawelLewandowski, "CN=Nikt," + _company.ItOu],
            new SetAttributeOperation(Attr.Department, "Sprzedaż"));
        Session.ClearOperations();

        var result = await ExecuteAsync(plan);

        Assert.Equal([ObjectResultStatus.Succeeded, ObjectResultStatus.Skipped, ObjectResultStatus.Skipped], result.Objects.Select(o => o.Status));
        Assert.Single(Session.Writes);
        Assert.Equal(2, result.SkippedCount);
    }

    [Fact]
    public async Task StopOnFirstError_SkipsRemainingObjects()
    {
        var plan = await PlanAsync([_company.JanKowalski, _company.LukaszWisniewski, _company.PawelLewandowski], new SetAttributeOperation(Attr.Title, "Nowy"));
        Session.FailNext(FakeOperationKind.Modify, _company.JanKowalski, DirectoryErrorKind.AccessDenied);

        var result = await ExecuteAsync(plan, Fast with { MaxDegreeOfParallelism = 1, StopOnFirstError = true });

        Assert.Equal([ObjectResultStatus.Failed, ObjectResultStatus.Skipped, ObjectResultStatus.Skipped], result.Objects.Select(o => o.Status));
        Assert.Equal("Programista", Value(_company.LukaszWisniewski, Attr.Title));
    }

    [Fact]
    public async Task Cancellation_LetsTheRunningStepFinishAndCancelsTheRest()
    {
        var plan = await PlanAsync([_company.JanKowalski, _company.LukaszWisniewski, _company.PawelLewandowski],
            new SetAttributeOperation(Attr.Title, "Nowy"),
            new MoveOperation(_company.AccountingOu));
        using var cts = new CancellationTokenSource();
        Session.BeforeOperation = operation =>
        {
            if (operation.Kind == FakeOperationKind.Modify)
            {
                cts.Cancel();
            }
        };

        var result = await ExecuteAsync(plan, Fast with { MaxDegreeOfParallelism = 1 }, token: cts.Token);

        Assert.True(result.WasCancelled);
        var jan = result.Objects[0];
        Assert.Equal(ObjectResultStatus.PartiallySucceeded, jan.Status);
        Assert.True(Assert.Single(jan.Changes).Succeeded);
        Assert.Equal("Nowy", Value(_company.JanKowalski, Attr.Title));
        Assert.True(Session.Exists(_company.JanKowalski));
        Assert.All(result.Objects.Skip(1), o => Assert.Equal(ObjectResultStatus.Cancelled, o.Status));
        Assert.Equal("Programista", Value(_company.LukaszWisniewski, Attr.Title));
    }

    [Fact]
    public async Task Progress_IsReportedForEveryObject()
    {
        var plan = await PlanAsync([_company.JanKowalski, _company.LukaszWisniewski, "CN=Nikt," + _company.ItOu], new SetAttributeOperation(Attr.Title, "Nowy"));
        Session.FailNext(FakeOperationKind.Modify, _company.LukaszWisniewski, DirectoryErrorKind.AccessDenied);
        var progress = new SynchronousProgress<BatchProgress>();

        await ExecuteAsync(plan, Fast with { MaxDegreeOfParallelism = 1 }, progress);

        Assert.Equal([1, 2, 3], progress.Values.Select(p => p.Completed).Order());
        Assert.All(progress.Values, p => Assert.Equal(3, p.Total));
        Assert.Equal(1, progress.Values.Max(p => p.Failed));
        Assert.All(progress.Values, p => Assert.NotNull(p.Last));
    }

    [Fact]
    public async Task Objects_RunInParallelWithinTheLimit()
    {
        var targets = new[]
        {
            _company.JanKowalski, _company.LukaszWisniewski, _company.MalgorzataWojcik, _company.GrazynaDabrowska,
            _company.ZbigniewKazmierczak, _company.PawelLewandowski, _company.ZanetaSzymanska, _company.BartlomiejKozlowski,
        };
        var plan = await PlanAsync(targets, new SetAttributeOperation(Attr.Company, "Fabrikam"));
        Session.Latency = TimeSpan.FromMilliseconds(25);
        Session.ResetConcurrencyStatistics();

        var result = await ExecuteAsync(plan, Fast with { MaxDegreeOfParallelism = 3 });

        Assert.Equal(8, result.SucceededCount);
        Assert.InRange(Session.MaxObservedConcurrency, 2, 3);
    }

    [Fact]
    public async Task Parallelism_IsClampedToAtLeastOne()
    {
        var plan = await PlanAsync([_company.JanKowalski, _company.LukaszWisniewski], new SetAttributeOperation(Attr.Title, "Nowy"));

        var result = await ExecuteAsync(plan, Fast with { MaxDegreeOfParallelism = 0, DelayBetweenObjects = TimeSpan.FromMilliseconds(2) });

        Assert.Equal(2, result.SucceededCount);
    }

    [Fact]
    public async Task PlanFromAnotherDomainController_IsRejected()
    {
        var plan = await PlanAsync([_company.JanKowalski], new SetAttributeOperation(Attr.Title, "Nowy"));

        await Assert.ThrowsAsync<InvalidOperationException>(() => ExecuteAsync(plan with { Server = "dc02.contoso.test" }));
    }

    [Fact]
    public async Task Delete_UsesTheSubtreeFlag()
    {
        var (_, result) = await RunAsync([_company.ItOu], new DeleteOperation(IncludeSubtree: true));

        var delete = Assert.Single(Session.Writes);
        Assert.Equal(FakeOperationKind.Delete, delete.Kind);
        Assert.True(delete.Subtree);
        Assert.False(Session.Exists(_company.JanKowalski));
        Assert.Equal(ObjectResultStatus.Succeeded, Assert.Single(result.Objects).Status);
        Assert.Contains(_audit.Entries, e => e is { Action: "Delete", Succeeded: true } && e.DistinguishedName == _company.ItOu);
    }

    [Fact]
    public async Task Create_AddsTheObject()
    {
        var dn = "CN=Anna Nowak," + _company.SalesOu;
        var plan = new ChangePlan
        {
            Server = Session.Info.Server,
            Objects =
            [
                new ObjectPlan
                {
                    DistinguishedName = dn,
                    DisplayName = "Anna Nowak",
                    Changes =
                    [
                        new PlannedChange
                        {
                            Action = ChangeAction.Create,
                            Attributes =
                            [
                                new AttributeChange(Attr.ObjectClass, AttributeChangeKind.Replace, [], ["user"]),
                                new AttributeChange(Attr.SamAccountName, AttributeChangeKind.Replace, [], ["anna.nowak"]),
                                new AttributeChange(Attr.Department, AttributeChangeKind.Replace, [], ["Sprzedaż"]),
                            ],
                        },
                    ],
                },
            ],
        };

        var result = await ExecuteAsync(plan);

        Assert.Equal(ObjectResultStatus.Succeeded, Assert.Single(result.Objects).Status);
        Assert.Equal("Sprzedaż", Value(dn, Attr.Department));
        Assert.Equal(3, _audit.Entries.Count(e => e.Action == "Create"));
    }

    [Fact]
    public async Task Audit_RecordsEveryAttributeChangeWithOldAndNewValues()
    {
        Session.SetValues(_company.JanKowalski, Attr.OtherTelephone, "111", "222");

        await RunAsync([_company.JanKowalski],
            new SetAttributeOperation(Attr.Title, "Kierownik"),
            new AddValuesOperation(Attr.OtherTelephone, ["333"]),
            new SetAccountEnabledOperation(false));

        var entries = _audit.Entries;
        Assert.Equal(3, entries.Count);
        Assert.All(entries, e =>
        {
            Assert.Equal(@"CONTOSO\Administrator", e.Operator);
            Assert.Equal("dc01.contoso.test", e.Server);
            Assert.Equal(_company.JanKowalski, e.DistinguishedName);
            Assert.Equal("Modify", e.Action);
            Assert.True(e.Succeeded);
            Assert.Equal(_time.GetUtcNow(), e.Timestamp);
        });
        Assert.Contains(entries, e => e is { Attribute: Attr.Title, OldValue: "Administrator systemów", NewValue: "Kierownik" });
        Assert.Contains(entries, e => e is { Attribute: Attr.OtherTelephone, OldValue: "111; 222", NewValue: "111; 222; 333" });
        Assert.Contains(entries, e => e is { Attribute: Attr.UserAccountControl, OldValue: "512", NewValue: "514" });
    }
}
