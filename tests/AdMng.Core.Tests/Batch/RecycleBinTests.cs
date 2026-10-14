using AdMng.Core.ActiveDirectory;
using AdMng.Core.Audit;
using AdMng.Core.Batch;
using AdMng.Core.Csv;
using AdMng.Core.Directory;
using AdMng.Core.Ldap;
using AdMng.Core.Model;
using AdMng.Testing;

namespace AdMng.Core.Tests.Batch;

/// <summary>Recycle Bin: listing deleted objects, parent-first restore planning and execution through the pipeline.</summary>
public class RecycleBinTests
{
    private static readonly BatchOptions Fast = new() { InitialRetryDelay = TimeSpan.FromMilliseconds(1), MaxDegreeOfParallelism = 8 };

    private readonly ManualTimeProvider _time = new();
    private readonly InMemoryAuditLog _audit = new();
    private readonly FakeDirectoryBuilder _builder;
    private readonly SampleCompany _company;

    public RecycleBinTests()
    {
        _builder = new FakeDirectoryBuilder(timeProvider: _time);
        _company = _builder.AddSampleCompany();
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private FakeDirectorySession Session => _builder.Session;

    private string DeletedObjects => $"CN=Deleted Objects,{_builder.DomainDn}";

    // ---------------------------------------------------------------- filters and helpers

    [Fact]
    public void BuildFilter_EscapesAndCombinesTheConditions()
    {
        var filter = RecycleBin.BuildFilter(new DeletedObjectFilter
        {
            Name = " Kow*(alski) ",
            Kind = ObjectKind.User,
            DeletedAfter = new DateTimeOffset(2026, 10, 1, 2, 0, 0, TimeSpan.FromHours(2)),
            LastKnownParent = _company.ItOu,
        });

        Assert.Equal(
            "(&(isDeleted=TRUE)(&(objectClass=user)(!(objectClass=computer)))"
            + @"(|(msDS-LastKnownRDN=*Kow\2a\28alski\29*)(name=Kow\2a\28alski\29*)(displayName=Kow\2a\28alski\29*)(sAMAccountName=Kow\2a\28alski\29*))"
            + "(whenChanged>=20261001000000.0Z)(lastKnownParent=OU=IT,OU=Pracownicy,OU=Firma,DC=contoso,DC=test))",
            filter);
        Assert.Equal("(isDeleted=TRUE)", RecycleBin.BuildFilter(null));
        Assert.Equal("(isDeleted=TRUE)", RecycleBin.BuildFilter(new DeletedObjectFilter { Name = "  ", Kind = ObjectKind.Unknown }));
        Assert.Null(LdapFilter.Validate(filter));
    }

    [Theory]
    [InlineData(ObjectKind.Computer, "(objectClass=computer)")]
    [InlineData(ObjectKind.Group, "(objectClass=group)")]
    [InlineData(ObjectKind.Contact, "(objectClass=contact)")]
    [InlineData(ObjectKind.OrganizationalUnit, "(objectClass=organizationalUnit)")]
    [InlineData(ObjectKind.InetOrgPerson, "(objectClass=inetOrgPerson)")]
    public void KindFilter_UsesObjectClass_BecauseDeletedObjectsLoseObjectCategory(ObjectKind kind, string expected)
    {
        Assert.Equal(expected, RecycleBin.KindFilter(kind));
        foreach (var each in Enum.GetValues<ObjectKind>())
        {
            Assert.DoesNotContain("objectCategory", RecycleBin.KindFilter(each), StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("sAMAccountType", RecycleBin.KindFilter(each), StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void DeletedDnHelpers_RecognizeTheMangledNames()
    {
        const string Deleted = @"OU=Sprzedaż\0ADEL:6bd26b58-7864-4fa5-b01c-222585815124,CN=Deleted Objects,DC=contoso,DC=test";

        Assert.Equal("CN=Deleted Objects,DC=contoso,DC=test", RecycleBin.GetDeletedObjectsContainer("DC=contoso,DC=test"));
        Assert.True(RecycleBin.IsDeletedObjectDn(Deleted));
        Assert.True(RecycleBin.IsDeletedObjectDn("CN=Deleted Objects,DC=contoso,DC=test"));
        Assert.False(RecycleBin.IsDeletedObjectDn(_company.SalesOu));
        Assert.False(RecycleBin.IsDeletedObjectDn(null));
        Assert.False(RecycleBin.IsDeletedObjectDn("nie DN"));
        Assert.Equal("Sprzedaż", RecycleBin.RemoveDeletedMarker(DistinguishedName.Parse(Deleted).Name));
        Assert.Equal("Jan Kowalski", RecycleBin.RemoveDeletedMarker("Jan Kowalski"));
    }

    [Fact]
    public async Task Search_ListsDeletedObjects_WithServerSideFilters()
    {
        await Session.DeleteAsync(_company.JanKowalski, subtree: false, Ct);
        _time.Advance(TimeSpan.FromDays(2));
        var after = _time.GetUtcNow();
        await Session.DeleteAsync(_company.Laptop, subtree: false, Ct);
        await Session.DeleteAsync(_company.GroupIt, subtree: false, Ct);
        await Session.DeleteAsync(_company.LukaszWisniewski, subtree: false, Ct);

        var all = await ListAsync();
        var jan = Assert.Single(all, o => o.Name == "Jan Kowalski");

        Assert.Equal(4, all.Count);
        Assert.Equal(ObjectKind.User, jan.Kind);
        Assert.Equal(_company.ItOu, jan.LastKnownParent);
        Assert.False(jan.IsParentDeleted);
        Assert.Equal(_company.JanKowalski, jan.OriginalDn);
        Assert.True(jan.IsDeleted);
        Assert.False(jan.IsRecycled);
        Assert.Equal("CN", jan.RdnType);
        Assert.NotNull(jan.ObjectGuid);
        Assert.NotNull(jan.ObjectSid);
        Assert.Equal(["Jan Kowalski"], (await ListAsync(new DeletedObjectFilter { Name = "kowal" })).Select(o => o.Name));
        Assert.Equal(["Jan Kowalski"], (await ListAsync(new DeletedObjectFilter { Name = "jan.kow" })).Select(o => o.Name));
        Assert.Equal(["KRK-LT001"], (await ListAsync(new DeletedObjectFilter { Kind = ObjectKind.Computer })).Select(o => o.Name));
        Assert.Equal(["Łukasz Wiśniewski"], (await ListAsync(new DeletedObjectFilter { Kind = ObjectKind.User, DeletedAfter = after })).Select(o => o.Name));
        Assert.Equal(["GG_IT"], (await ListAsync(new DeletedObjectFilter { Kind = ObjectKind.Group })).Select(o => o.Name));
        Assert.Equal(2, (await ListAsync(new DeletedObjectFilter { LastKnownParent = _company.ItOu })).Count);
    }

    [Fact]
    public async Task DeletedObjectInfo_FallsBackToTheMangledName_ForTombstones()
    {
        var builder = new FakeDirectoryBuilder(info: FakeDirectorySession.CreateInfo() with { RecycleBinEnabled = false });
        var company = builder.AddSampleCompany();
        await builder.Session.DeleteAsync(company.ZanetaSzymanska, subtree: false, Ct);

        var tombstone = Assert.Single(await ListAsync(builder.Session));

        Assert.Equal("Żaneta Szymańska", tombstone.Name);
        Assert.True(tombstone.IsRecycled);
        Assert.Equal(company.SalesOu, tombstone.LastKnownParent);
        Assert.Equal(ObjectKind.User, tombstone.Kind);
    }

    [Fact]
    public async Task ReadLifetime_ReadsTheConfiguredDays()
    {
        Assert.Null(await RecycleBin.ReadLifetimeAsync(Session, Ct));
        var configuration = _builder.AddContainer("Configuration", _builder.DomainDn);
        Assert.Equal(Session.Info.ConfigurationNamingContext, configuration);
        var windowsNt = _builder.AddContainer("Windows NT", _builder.AddContainer("Services", configuration));
        var directoryService = _builder.AddContainer("Directory Service", windowsNt, new Dictionary<string, object?> { [Attr.TombstoneLifetime] = 180 });

        var tombstoneOnly = await RecycleBin.ReadLifetimeAsync(Session, Ct);
        Session.SetValues(directoryService, Attr.MsDsDeletedObjectLifetime, "365");
        var both = await RecycleBin.ReadLifetimeAsync(Session, Ct);

        Assert.Equal(new DeletedObjectLifetime(null, 180), tombstoneOnly);
        Assert.Equal(180, tombstoneOnly!.RestorableDays(recycleBinEnabled: true));
        Assert.Equal(365, both!.RestorableDays(recycleBinEnabled: true));
        Assert.Equal(180, both.RestorableDays(recycleBinEnabled: false));
    }

    // ---------------------------------------------------------------- planning

    [Fact]
    public async Task PlanRestore_ToTheOriginalLocation()
    {
        await Session.DeleteAsync(_company.JanKowalski, subtree: false, Ct);
        var deleted = Assert.Single(await ListAsync());

        var plan = await PlanAsync([deleted.DistinguishedName]);

        var obj = Assert.Single(plan.Objects);
        Assert.Equal("Przywróć", plan.Title);
        Assert.Equal("dc01.contoso.test", plan.Server);
        Assert.Equal(_time.GetUtcNow(), plan.CreatedAt);
        Assert.Equal(deleted.DistinguishedName, obj.DistinguishedName);
        Assert.Equal("Jan Kowalski", obj.DisplayName);
        Assert.Equal(ObjectKind.User, obj.Kind);
        Assert.Equal(deleted.ObjectGuid, obj.ObjectGuid);
        Assert.Empty(obj.Issues);
        var step = Assert.Single(obj.Changes);
        Assert.Equal(ChangeAction.Restore, step.Action);
        Assert.Equal(_company.JanKowalski, step.TargetDn);
        Assert.Equal(1, plan.ExecutableCount);
    }

    [Fact]
    public async Task PlanRestore_OrdersParentsFirst_AndPointsChildrenAtTheRestoredParent()
    {
        await Session.DeleteAsync(_company.EmployeesOu, subtree: true, Ct);
        var deleted = await ListAsync();
        string Dn(string name) => deleted.Single(o => o.Name == name).DistinguishedName;

        // Selected children first: the plan still starts with the top container.
        var plan = await PlanAsync([Dn("Paweł Lewandowski"), Dn("Sprzedaż"), Dn("Pracownicy"), Dn("Jan Kowalski"), Dn("IT")]);

        Assert.Equal(["Pracownicy", "Sprzedaż", "IT", "Paweł Lewandowski", "Jan Kowalski"], plan.Objects.Select(o => o.DisplayName));
        Assert.Equal(
            [_company.EmployeesOu, _company.SalesOu, _company.ItOu, _company.PawelLewandowski, _company.JanKowalski],
            plan.Objects.Select(o => o.Changes.Single().TargetDn));
        Assert.All(plan.Objects, o => Assert.False(o.HasErrors));
    }

    [Fact]
    public async Task ExecuteRestore_OfAnOuWithChildren_RestoresTheWholeSubtree()
    {
        await Session.DeleteAsync(_company.SalesOu, subtree: true, Ct);
        var deleted = await ListAsync();
        Assert.Equal(4, deleted.Count);
        Assert.All(deleted.Where(o => o.Kind == ObjectKind.User), o => Assert.True(o.IsParentDeleted));
        Session.Latency = TimeSpan.FromMilliseconds(20);

        // Children before the parent in the selection; eight objects may run in parallel.
        var plan = await PlanAsync([.. deleted.Select(o => o.DistinguishedName).Reverse()]);
        var result = await new BatchExecutor(_audit, _time).ExecuteAsync(Session, plan, Fast, null, Ct);

        Assert.Equal(4, result.SucceededCount);
        Assert.Equal("Sprzedaż", plan.Objects[0].DisplayName);
        Assert.True(Session.Exists(_company.SalesOu));
        Assert.True(Session.Exists(_company.PawelLewandowski));
        Assert.True(Session.Exists(_company.ZanetaSzymanska));
        Assert.True(Session.Exists(_company.BartlomiejKozlowski));
        Assert.Equal(_company.PawelLewandowski, result.Objects.Single(o => o.Plan.DisplayName == "Paweł Lewandowski").FinalDn);
        Assert.Contains(_company.PawelLewandowski, Session.GetValues(_company.GroupSales, Attr.Member));
        Assert.Equal("Sprzedaż", Session.GetValues(_company.PawelLewandowski, Attr.Department).Single());
        Assert.Empty(Session.DeletedDistinguishedNames);

        // The container was restored before any of its children was attempted.
        var restores = Session.Operations.Where(o => o.Kind == FakeOperationKind.RestoreDeleted).ToList();
        Assert.Equal(4, restores.Count);
        Assert.All(restores, o => Assert.True(o.Succeeded));
        Assert.Equal(_company.SalesOu, restores[0].TargetDn);

        var audit = _audit.Entries.Single(e => DistinguishedName.AreEqual(e.DistinguishedName, _company.PawelLewandowski));
        Assert.Equal("Restore", audit.Action);
        Assert.True(audit.Succeeded);
        Assert.Equal(_company.PawelLewandowski, audit.NewValue);
        Assert.Contains("CN=Deleted Objects", audit.OldValue, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PlanRestore_ChildOfADeletedParent_NeedsTheParentOrAnotherContainer()
    {
        await Session.DeleteAsync(_company.SalesOu, subtree: true, Ct);
        var pawel = (await ListAsync()).Single(o => o.Name == "Paweł Lewandowski");

        var original = await PlanAsync([pawel.DistinguishedName]);
        var elsewhere = await PlanAsync([pawel.DistinguishedName], _company.AccountingOu);

        var blocked = Assert.Single(original.Objects);
        Assert.Equal(IssueCodes.ParentDeleted, Assert.Single(blocked.Issues).Code);
        Assert.Contains("Sprzedaż", blocked.Issues[0].Message, StringComparison.Ordinal);
        Assert.Empty(blocked.Changes);
        Assert.Equal(0, original.ExecutableCount);
        Assert.Equal("CN=Paweł Lewandowski," + _company.AccountingOu, Assert.Single(Assert.Single(elsewhere.Objects).Changes).TargetDn);
        Assert.Equal(1, elsewhere.ExecutableCount);
    }

    [Fact]
    public async Task PlanRestore_ToAnotherContainer_KeepsTheStructureOfTheSelectedSubtree()
    {
        await Session.DeleteAsync(_company.SalesOu, subtree: true, Ct);
        var deleted = await ListAsync();
        var selection = deleted.Where(o => o.Name is "Sprzedaż" or "Żaneta Szymańska").Select(o => o.DistinguishedName).ToList();

        var plan = await PlanAsync(selection, _company.ComputersOu);
        var result = await new BatchExecutor(_audit, _time).ExecuteAsync(Session, plan, Fast, null, Ct);

        var newOu = "OU=Sprzedaż," + _company.ComputersOu;
        Assert.Equal([newOu, "CN=Żaneta Szymańska," + newOu], plan.Objects.Select(o => o.Changes.Single().TargetDn));
        Assert.Equal(2, result.SucceededCount);
        Assert.True(Session.Exists("CN=Żaneta Szymańska," + newOu));
        Assert.False(Session.Exists(_company.SalesOu));
    }

    [Fact]
    public async Task PlanRestore_NameTakenInTheTarget_IsFoundByALiveCheck()
    {
        await Session.DeleteAsync(_company.JanKowalski, subtree: false, Ct);
        var deletedDn = Assert.Single(Session.DeletedDistinguishedNames);
        _builder.AddUser("Jan", "Kowalski", _company.ItOu, new Dictionary<string, object?> { [Attr.SamAccountName] = "jan.kowalski2" });

        var plan = await PlanAsync([deletedDn]);
        var elsewhere = await PlanAsync([deletedDn], _company.SalesOu);

        var blocked = Assert.Single(plan.Objects);
        Assert.Equal(IssueCodes.AlreadyExists, Assert.Single(blocked.Issues).Code);
        Assert.Equal(_company.JanKowalski, blocked.Changes.Single().TargetDn);
        Assert.True(blocked.HasErrors);
        Assert.False(Assert.Single(elsewhere.Objects).HasErrors);
    }

    [Fact]
    public async Task PlanRestore_TwoObjectsWithTheSameTarget_IsADuplicateInTheBatch()
    {
        await Session.DeleteAsync(_company.JanKowalski, subtree: false, Ct);
        var second = _builder.AddUser("Jan", "Kowalski", _company.ItOu, new Dictionary<string, object?> { [Attr.SamAccountName] = "jan.kowalski2" });
        await Session.DeleteAsync(second, subtree: false, Ct);

        var plan = await PlanAsync(Session.DeletedDistinguishedNames);

        Assert.Equal(2, plan.Objects.Count);
        Assert.False(plan.Objects[0].HasErrors);
        var duplicate = Assert.Single(plan.Objects[1].Issues);
        Assert.Equal(IssueCodes.DuplicateInBatch, duplicate.Code);
        Assert.Equal(Attr.Name, duplicate.Attribute);
    }

    [Fact]
    public async Task PlanRestore_AParentThatCannotBeRestored_BlocksItsChildren()
    {
        await Session.DeleteAsync(_company.SalesOu, subtree: true, Ct);
        _builder.AddOu("Sprzedaż", _company.EmployeesOu);
        var deleted = await ListAsync();

        var plan = await PlanAsync([.. deleted.Where(o => o.Name is "Sprzedaż" or "Paweł Lewandowski").Select(o => o.DistinguishedName)]);

        Assert.Equal(IssueCodes.AlreadyExists, Assert.Single(plan.Objects[0].Issues).Code);
        Assert.Equal(IssueCodes.ParentNotRestored, Assert.Single(plan.Objects[1].Issues).Code);
        Assert.Equal(0, plan.ExecutableCount);
    }

    [Fact]
    public async Task PlanRestore_ReportsMissingInvalidLiveAndRecycledObjects()
    {
        await Session.DeleteAsync(_company.JanKowalski, subtree: false, Ct);
        var recycled = Assert.Single(Session.DeletedDistinguishedNames);
        Session.Recycle(recycled);
        var missing = $@"CN=Nikt\0ADEL:00000000-0000-0000-0000-000000000001,{DeletedObjects}";

        var plan = await PlanAsync([missing, "to nie jest DN", _company.LukaszWisniewski, recycled, missing]);

        Assert.Equal(4, plan.Objects.Count);
        Assert.Equal(
            [IssueCodes.NotFound, IssueCodes.InvalidValue, IssueCodes.NotApplicable, IssueCodes.Recycled],
            plan.Objects.Select(o => Assert.Single(o.Issues).Code));
        Assert.Equal("Nikt", plan.Objects[0].DisplayName);
        Assert.Equal("Jan Kowalski", plan.Objects[3].DisplayName);
        Assert.Equal(4, plan.ErrorCount);
    }

    [Fact]
    public async Task PlanRestore_ALiveContainerInTheSelection_IsNoRestoredParent()
    {
        await Session.DeleteAsync(_company.JanKowalski, subtree: false, Ct);

        var plan = await PlanAsync([_company.ItOu, .. Session.DeletedDistinguishedNames]);

        Assert.Equal(IssueCodes.NotApplicable, Assert.Single(plan.Objects.Single(o => o.DisplayName == "IT").Issues).Code);
        var jan = plan.Objects.Single(o => o.DisplayName == "Jan Kowalski");
        Assert.False(jan.HasErrors);
        Assert.Equal(_company.JanKowalski, jan.Changes.Single().TargetDn);
    }

    [Fact]
    public async Task PlanRestore_ToAMissingContainer_IsAnError()
    {
        await Session.DeleteAsync(_company.JanKowalski, subtree: false, Ct);
        var deletedDn = Assert.Single(Session.DeletedDistinguishedNames);

        var missing = await PlanAsync([deletedDn], "OU=Nie istnieje," + _builder.DomainDn);
        var deletedTarget = await PlanAsync([deletedDn], DeletedObjects);

        Assert.Equal(IssueCodes.TargetNotFound, Assert.Single(Assert.Single(missing.Objects).Issues).Code);
        Assert.Equal(IssueCodes.TargetNotFound, Assert.Single(Assert.Single(deletedTarget.Objects).Issues).Code);
    }

    [Fact]
    public async Task PlanRestore_WithoutRecycleBin_WarnsAboutTombstoneReanimation()
    {
        var builder = new FakeDirectoryBuilder(timeProvider: _time, info: FakeDirectorySession.CreateInfo() with { RecycleBinEnabled = false });
        var company = builder.AddSampleCompany();
        await builder.Session.DeleteAsync(company.JanKowalski, subtree: false, Ct);

        var plan = await RecycleBin.PlanRestoreAsync(builder.Session, builder.Session.DeletedDistinguishedNames, cancellationToken: Ct);
        var result = await new BatchExecutor(_audit, _time).ExecuteAsync(builder.Session, plan, Fast, null, Ct);

        var obj = Assert.Single(plan.Objects);
        Assert.Equal(IssueCodes.TombstoneReanimation, Assert.Single(obj.Issues).Code);
        Assert.Equal(IssueSeverity.Warning, obj.Issues[0].Severity);
        Assert.Equal("Jan Kowalski", obj.DisplayName);
        Assert.Equal(1, result.SucceededCount);
        Assert.True(builder.Session.Exists(company.JanKowalski));
        Assert.Empty(builder.Session.GetValues(company.JanKowalski, Attr.Department));
    }

    [Fact]
    public async Task PlanRestore_RejectsNullArguments()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(() => RecycleBin.PlanRestoreAsync(null!, [], cancellationToken: Ct));
        await Assert.ThrowsAsync<ArgumentNullException>(() => RecycleBin.PlanRestoreAsync(Session, null!, cancellationToken: Ct));
        await Assert.ThrowsAsync<ArgumentNullException>(() => RecycleBin.PlanRestoreAsync(Session, [null!], cancellationToken: Ct));
    }

    // ---------------------------------------------------------------- execution

    [Fact]
    public async Task Execute_AChildOfAParentThatFailed_IsNotAttempted()
    {
        await Session.DeleteAsync(_company.SalesOu, subtree: true, Ct);
        var deleted = await ListAsync();
        var sales = deleted.Single(o => o.Name == "Sprzedaż");
        var plan = await PlanAsync([.. deleted.Where(o => o.Name is "Sprzedaż" or "Paweł Lewandowski").Select(o => o.DistinguishedName)]);
        Session.FailNext(FakeOperationKind.RestoreDeleted, sales.DistinguishedName, DirectoryErrorKind.AccessDenied);

        var result = await new BatchExecutor(_audit, _time).ExecuteAsync(Session, plan, Fast, null, Ct);

        Assert.Equal(ObjectResultStatus.Failed, result.Objects[0].Status);
        Assert.Equal(DirectoryErrorKind.AccessDenied, result.Objects[0].Changes.Single().ErrorKind);
        var child = result.Objects[1];
        Assert.Equal(ObjectResultStatus.Failed, child.Status);
        var failure = Assert.Single(child.Changes);
        Assert.Equal(BatchExecutor.ParentNotRestoredMessage, failure.ErrorMessage);
        Assert.Equal(0, failure.Attempts);
        Assert.Single(Session.Operations, o => o.Kind == FakeOperationKind.RestoreDeleted);
        Assert.Equal(2, Session.DeletedDistinguishedNames.Count(dn => dn.Contains("Sprzedaż", StringComparison.Ordinal) || dn.Contains("Paweł", StringComparison.Ordinal)));
        Assert.DoesNotContain(_audit.Entries, e => DistinguishedName.AreEqual(e.DistinguishedName, _company.PawelLewandowski));
    }

    [Fact]
    public async Task Execute_ARestoreWhoseResponseWasLost_IsConfirmedByReading()
    {
        await Session.DeleteAsync(_company.JanKowalski, subtree: false, Ct);
        var deletedDn = Assert.Single(Session.DeletedDistinguishedNames);
        var plan = await PlanAsync([deletedDn]);
        Session.LoseNextResponse(FakeOperationKind.RestoreDeleted, deletedDn);

        var result = await new BatchExecutor(_audit, _time).ExecuteAsync(Session, plan, Fast, null, Ct);

        var obj = Assert.Single(result.Objects);
        Assert.Equal(ObjectResultStatus.Succeeded, obj.Status);
        Assert.Equal(2, obj.Changes.Single().Attempts);
        Assert.Equal(_company.JanKowalski, obj.FinalDn);
        Assert.True(Session.Exists(_company.JanKowalski));
    }

    [Fact]
    public async Task Execute_ARestoreStepWithoutTarget_FailsWithoutARequest()
    {
        var plan = new ChangePlan
        {
            Objects = [new ObjectPlan { DistinguishedName = $@"CN=X\0ADEL:1,{DeletedObjects}", DisplayName = "X", Changes = [new PlannedChange { Action = ChangeAction.Restore }] }],
        };

        var result = await new BatchExecutor(_audit, _time).ExecuteAsync(Session, plan, Fast, null, Ct);

        Assert.Equal(ObjectResultStatus.Failed, Assert.Single(result.Objects).Status);
        Assert.DoesNotContain(Session.Operations, o => o.Kind == FakeOperationKind.RestoreDeleted);
    }

    // ---------------------------------------------------------------- rollback, PowerShell, report

    [Fact]
    public async Task Rollback_OfARestore_IsNotReversible()
    {
        await Session.DeleteAsync(_company.JanKowalski, subtree: false, Ct);
        var plan = await PlanAsync(Session.DeletedDistinguishedNames);
        var result = await new BatchExecutor(_audit, _time).ExecuteAsync(Session, plan, Fast, null, Ct);

        var rollback = new RollbackPlanner().CreateRollbackPlan(result);

        var obj = Assert.Single(rollback.Objects);
        Assert.Equal(_company.JanKowalski, obj.DistinguishedName);
        Assert.Empty(obj.Changes);
        Assert.Equal(IssueCodes.NotReversible, Assert.Single(obj.Issues).Code);
        Assert.Equal(0, rollback.ExecutableCount);
    }

    [Fact]
    public void PowerShell_UsesRestoreADObject_ByGuid_OrByTheDeletedDn()
    {
        var guid = new Guid("6bd26b58-7864-4fa5-b01c-222585815124");
        var deletedDn = $@"CN=Jan O'Brien\0ADEL:{guid:D},CN=Deleted Objects,DC=contoso,DC=test";
        var plan = new ChangePlan
        {
            Title = "Przywróć",
            Server = "dc01.contoso.test",
            Objects =
            [
                new ObjectPlan
                {
                    DistinguishedName = deletedDn,
                    DisplayName = "Jan O'Brien",
                    ObjectGuid = guid,
                    Changes = [new PlannedChange { Action = ChangeAction.Restore, TargetDn = "CN=Jan O'Brien,OU=IT,DC=contoso,DC=test" }],
                },
                new ObjectPlan
                {
                    DistinguishedName = deletedDn,
                    DisplayName = "Jan O'Brien",
                    Changes = [new PlannedChange { Action = ChangeAction.Restore, TargetDn = @"CN=Kowalski\, Jan,OU=Sprzedaż,DC=contoso,DC=test" }],
                },
                new ObjectPlan
                {
                    DistinguishedName = deletedDn,
                    DisplayName = "Zły",
                    Changes = [new PlannedChange { Action = ChangeAction.Restore, TargetDn = "DC=test" }],
                },
            ],
        };

        var script = new PowerShellExporter().Export(plan, null);
        var lines = script.Split("\r\n");

        Assert.Contains(
            "Restore-ADObject -Identity '6bd26b58-7864-4fa5-b01c-222585815124' -NewName 'Jan O''Brien' -TargetPath 'OU=IT,DC=contoso,DC=test' -Server $server",
            lines);
        Assert.Contains("$dn = 'CN=Jan O''Brien,OU=IT,DC=contoso,DC=test'", lines);
        Assert.Contains(
            "Get-ADObject -Identity $dn -IncludeDeletedObjects -Server $server | Restore-ADObject -NewName 'Kowalski, Jan' -TargetPath 'OU=Sprzedaż,DC=contoso,DC=test' -Server $server",
            lines);
        Assert.Contains("# Cannot restore the object: its target DN is invalid.", lines);
    }

    [Fact]
    public async Task Report_ShowsWhereTheObjectIsRestoredFrom_AndTo()
    {
        await Session.DeleteAsync(_company.JanKowalski, subtree: false, Ct);
        var deletedDn = Assert.Single(Session.DeletedDistinguishedNames);
        var plan = await PlanAsync([deletedDn]);
        var result = await new BatchExecutor(_audit, _time).ExecuteAsync(Session, plan, Fast, null, Ct);
        var csv = new CsvCodec();
        var exporter = new BatchReportExporter(csv);

        var planTable = Read(csv, writer => exporter.Write(writer, plan));
        var resultTable = Read(csv, writer => exporter.Write(writer, result));

        Assert.Equal(
            [BatchReportExporter.StatusPlanned, "Jan Kowalski", deletedDn, "Restore", string.Empty, deletedDn, _company.JanKowalski],
            planTable.Rows.Single().Take(7));
        Assert.Equal(
            [BatchReportExporter.StatusSucceeded, "Jan Kowalski", _company.JanKowalski, "Restore", string.Empty, deletedDn, _company.JanKowalski],
            resultTable.Rows.Single().Take(7));
    }

    private static CsvTable Read(CsvCodec csv, Action<TextWriter> write)
    {
        using var writer = new StringWriter();
        write(writer);
        return csv.Read(new StringReader(writer.ToString()));
    }

    private Task<ChangePlan> PlanAsync(IReadOnlyList<string> deletedDns, string? target = null) =>
        RecycleBin.PlanRestoreAsync(Session, deletedDns, target, new RestorePlanningOptions { Title = "Przywróć", TimeProvider = _time }, Ct);

    private Task<List<DeletedObjectInfo>> ListAsync(DeletedObjectFilter? filter = null) => ListAsync(Session, filter);

    private static async Task<List<DeletedObjectInfo>> ListAsync(FakeDirectorySession session, DeletedObjectFilter? filter = null)
    {
        var result = new List<DeletedObjectInfo>();
        await foreach (var obj in session.SearchAsync(RecycleBin.CreateSearch(session.Info.DefaultNamingContext, filter), Ct))
        {
            result.Add(DeletedObjectInfo.From(obj));
        }

        return result;
    }
}
