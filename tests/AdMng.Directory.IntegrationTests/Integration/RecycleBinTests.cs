using AdMng.Core.ActiveDirectory;
using AdMng.Core.Audit;
using AdMng.Core.Batch;
using AdMng.Core.Ldap;
using AdMng.Core.Model;
using AdMng.Directory.IntegrationTests.Infrastructure;

namespace AdMng.Directory.IntegrationTests.Integration;

/// <summary>The Recycle Bin helpers of AdMng.Core against a real DC: listing deleted objects and restoring a subtree.</summary>
public sealed class RecycleBinTests(LabFixture lab)
{
    [Fact]
    public async Task DeletedSubtree_IsListed_AndRestoredParentFirst()
    {
        LabSettings.RequireLab();
        var ct = TestContext.Current.CancellationToken;
        var parent = await lab.CreateOuAsync(TestNames.Unique("Kosz drzewo"));
        var ou = await lab.CreateOuAsync("Dział Łódź", parent);
        var team = await lab.CreateOuAsync("Zespół Ąę", ou);
        var contact = await lab.CreateContactAsync(team, "Kontakt Żółw");
        var user = await lab.CreateUserAsync(ou, "Użytkownik Kosza");
        var deletedAfter = DateTimeOffset.UtcNow.AddMinutes(-5);
        await lab.Admin.DeleteAsync(ou, subtree: true, ct);

        // Everything deleted from below the test parent: its child OU and, through the lastKnownParent chain, the rest.
        var recent = new List<DeletedObjectInfo>();
        var search = RecycleBin.CreateSearch(LabSettings.DomainDn, new DeletedObjectFilter { DeletedAfter = deletedAfter });
        await foreach (var obj in lab.Admin.SearchAsync(search, ct))
        {
            recent.Add(DeletedObjectInfo.From(obj));
        }

        var root = Assert.Single(recent, o => DistinguishedName.AreEqual(o.LastKnownParent, parent));
        var selection = new List<DeletedObjectInfo> { root };
        for (var added = true; added;)
        {
            var next = recent.Where(o => !selection.Contains(o) && selection.Any(s => DistinguishedName.AreEqual(o.LastKnownParent, s.DistinguishedName))).ToList();
            selection.AddRange(next);
            added = next.Count > 0;
        }

        Assert.Equal("Dział Łódź", root.Name);
        Assert.Equal(ObjectKind.OrganizationalUnit, root.Kind);
        Assert.Equal(4, selection.Count);
        Assert.All(selection.Skip(1), o => Assert.True(o.IsParentDeleted));
        Assert.Contains(selection, o => o.Name == "Kontakt Żółw" && o.Kind == ObjectKind.Contact);

        // Selected children first: the plan restores the OU first and points the children at it.
        var plan = await RecycleBin.PlanRestoreAsync(lab.Admin, [.. selection.Select(o => o.DistinguishedName).Reverse()], cancellationToken: ct);
        Assert.Equal(0, plan.ErrorCount);
        Assert.Equal("Dział Łódź", plan.Objects[0].DisplayName);
        Assert.Equal(ou, plan.Objects[0].Changes.Single().TargetDn, ignoreCase: true);
        if (!lab.Admin.Info.RecycleBinEnabled)
        {
            Assert.All(plan.Objects, o => Assert.Contains(o.Issues, i => i.Code == IssueCodes.TombstoneReanimation));
        }

        var result = await new BatchExecutor(new InMemoryAuditLog(), TimeProvider.System)
            .ExecuteAsync(lab.Admin, plan, new BatchOptions { MaxDegreeOfParallelism = 4 }, null, ct);

        Assert.Equal(4, result.SucceededCount);
        foreach (var dn in new[] { ou, team, contact, user })
        {
            Assert.NotNull(await lab.Admin.GetAsync(dn, [Attr.ObjectGuid], ct));
        }
    }
}
