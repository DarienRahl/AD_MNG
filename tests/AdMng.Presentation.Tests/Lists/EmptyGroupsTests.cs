using AdMng.Core.ActiveDirectory;
using AdMng.Core.Ldap;
using AdMng.Presentation.Lists;
using AdMng.Presentation.Tests.TestDoubles;
using AdMng.Presentation.Views;
using static AdMng.Presentation.Tests.TestDoubles.TestDirectory;

namespace AdMng.Presentation.Tests.Lists;

/// <summary>
/// Members of a primary group are linked by primaryGroupID, not member: the default primary groups (Domain Users,
/// Domain Computers...) must not be reported as empty.
/// </summary>
public class EmptyGroupsTests
{
    private const string DomainSid = "S-1-5-21-1004336348-1177238915-682003330";

    [Fact]
    public void Filter_ExcludesTheDefaultPrimaryGroupsBySid_WhenTheDomainSidIsKnown()
    {
        var filter = QuickFilters.EmptyGroups(DomainSid);

        Assert.Null(LdapFilter.Validate(filter));
        Assert.Contains("(!(member=*))", filter, StringComparison.Ordinal);
        foreach (var rid in new[] { 513, 514, 515, 516, 521 })
        {
            Assert.Contains($"(objectSid={DomainSid}-{rid})", filter, StringComparison.Ordinal);
        }

        Assert.Contains($"(!(|(objectSid={DomainSid}-513)", filter, StringComparison.Ordinal);
        Assert.Equal(filter, QuickFilters.Build(QuickFilterKind.EmptyGroups, TestHarness.Now, 90, DomainSid));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("S-1-5-32")]
    [InlineData("S-1-5-21-1-2-3-4")]
    [InlineData("S-1-5-21-1-2-3)(objectClass=*")]
    [InlineData("S-1-5-21-1-2-x")]
    public void Filter_WithoutAValidDomainSid_ListsGroupsWithoutMembers(string? domainSid)
    {
        var filter = QuickFilters.EmptyGroups(domainSid);

        Assert.Equal(LdapFilter.And(QuickFilters.Groups, LdapFilter.NotPresent(Attr.Member)), filter);
    }

    [Fact]
    public async Task BuiltInView_DoesNotListTheDefaultPrimaryGroups()
    {
        var harness = await ConnectedWithPrimaryGroupsAsync();
        var list = harness.Lists.CreateList();
        var view = BuiltInViews.All.Single(v => v.Kind == BuiltInViewKind.EmptyGroups);

        await list.NavigateAsync(view.ToLocation(harness.Localizer, Domain, TestHarness.Now, 90));

        Assert.Equal(["GG-Pusta"], list.Items.Select(r => r.Name));
        Assert.Contains($"(objectSid={DomainSid}-513)", list.LastSearch!.Filter, StringComparison.Ordinal);
        Assert.Equal("Empty groups", list.Title);
    }

    [Fact]
    public async Task Chip_DoesNotListTheDefaultPrimaryGroups()
    {
        var harness = await ConnectedWithPrimaryGroupsAsync();
        var list = await harness.OpenContainerAsync(Users);

        list.FilterChips.Single(c => c.Kind == QuickFilterKind.EmptyGroups).IsActive = true;
        await list.CurrentLoad;

        Assert.Equal(["GG-Pusta"], list.Items.Select(r => r.Name));
    }

    [Fact]
    public void SavedCopyOfTheBuiltInView_KeepsTheChip_SoItIsBuiltWithTheSidOfTheDomainItRunsIn()
    {
        var view = BuiltInViews.All.Single(v => v.Kind == BuiltInViewKind.EmptyGroups);

        var (filter, chip) = view.ToSavedDefinition(TestHarness.Now, 90);

        Assert.Equal(QuickFilters.Groups, filter);
        Assert.Equal(QuickFilterKind.EmptyGroups, chip);
    }

    /// <summary>The domain has a SID; CN=Users holds the default primary groups (no member values) and one really empty group.</summary>
    private static async Task<TestHarness> ConnectedWithPrimaryGroupsAsync()
    {
        var harness = await TestHarness.ConnectedAsync();
        var directory = harness.Directory;
        directory.Put(WithAttribute(directory.Find(Domain)!, Attr.ObjectSid, AdConvert.SidToBytes(DomainSid)));
        (string Name, uint Rid)[] groups =
        [
            ("Domain Users", WellKnownRids.DomainUsers),
            ("Domain Guests", WellKnownRids.DomainGuests),
            ("Domain Computers", WellKnownRids.DomainComputers),
            ("Domain Controllers", WellKnownRids.DomainControllers),
            ("Read-only Domain Controllers", WellKnownRids.ReadOnlyDomainControllers),
            ("GG-Pusta", 1105),
        ];
        foreach (var (name, rid) in groups)
        {
            var group = Group($"CN={name},{Users}", name, []);
            directory.Put(WithAttribute(group, Attr.ObjectSid, AdConvert.SidToBytes($"{DomainSid}-{rid}")));
        }

        return harness;
    }
}
