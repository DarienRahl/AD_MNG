using System.Globalization;
using AdMng.Core.ActiveDirectory;
using AdMng.Core.Model;
using AdMng.Presentation.Changes;
using AdMng.Presentation.Lists;
using AdMng.Presentation.Tests.TestDoubles;
using static AdMng.Presentation.Tests.TestDoubles.TestDirectory;

namespace AdMng.Presentation.Tests.Lists;

/// <summary>Live updates of a locally sorted list put rows in place instead of sorting every row on the UI thread.</summary>
public class SortedLiveUpdateTests
{
    private const string SortOu = "OU=Sort,DC=contoso,DC=test";

    [Fact]
    public async Task RefreshedRows_AreMergedLikeAStableSort_EqualKeysKeepTheirOrder_NewRowsFollowExistingOnes()
    {
        var harness = await TestHarness.ConnectedAsync();
        harness.Directory.Put(Ou(SortOu));
        harness.Directory.Put(User(Dn("U0"), "u0", department: "Alpha"));
        harness.Directory.Put(User(Dn("U1"), "u1", department: "Sales"));
        harness.Directory.Put(User(Dn("U2"), "u2", department: "Sales"));
        harness.Directory.Put(User(Dn("U3"), "u3", department: "Zeta"));
        var list = await harness.OpenContainerAsync(SortOu);
        await list.ApplyColumnsAsync(["name", "department"]);
        await list.SortCommand.ExecuteAsync(list.Columns.Single(c => c.Attribute == Attr.Department));
        Assert.Equal(["U0", "U1", "U2", "U3"], list.Items.Select(r => r.Name));

        // U0 is deleted, U1 moves behind everything, U2 changes without changing its key, U4 is new with an existing key.
        harness.Directory.Remove(Dn("U0"));
        harness.Directory.Put(User(Dn("U1"), "u1", department: "Zulu"));
        harness.Directory.Put(User(Dn("U2"), "u2", department: "Sales", mail: "u2@contoso.test"));
        harness.Directory.Put(User(Dn("U4"), "u4", department: "Sales"));
        await list.RefreshObjectsAsync([Dn("U0"), Dn("U1"), Dn("U2"), Dn("U4")], TestContext.Current.CancellationToken);

        // Exactly what a stable sort of [U1, U2, U3, U4] by department gives.
        Assert.Equal(["U2", "U4", "U3", "U1"], list.Items.Select(r => r.Name));
        Assert.Equal(list.Items, list.AllRows);
        Assert.Equal(4, list.LoadedCount);
    }

    [Fact]
    public async Task RowsChangedOnly_MoveWhenTheirKeyLeavesTheOrder()
    {
        var harness = await TestHarness.ConnectedAsync();
        harness.Directory.Put(Ou(SortOu));
        harness.Directory.Put(User(Dn("U1"), "u1", department: "Alpha"));
        harness.Directory.Put(User(Dn("U2"), "u2", department: "Beta"));
        harness.Directory.Put(User(Dn("U3"), "u3", department: "Gamma"));
        var list = await harness.OpenContainerAsync(SortOu);
        await list.ApplyColumnsAsync(["name", "department"]);
        await list.SortCommand.ExecuteAsync(list.Columns.Single(c => c.Attribute == Attr.Department));

        harness.Directory.Put(User(Dn("U1"), "u1", department: "Omega"));
        await list.HandleChangesAsync([new DirectoryChange(DirectoryChangeKind.Modified, Dn("U1"), ObjectKind: ObjectKind.User)]);

        Assert.Equal(["U2", "U3", "U1"], list.Items.Select(r => r.Name));
        Assert.Equal(list.Items, list.AllRows);
    }

    [Fact]
    public async Task LargeSortedList_LiveInsertsAndRemovals_KeepTheExactSortOrder()
    {
        var harness = await TestHarness.ConnectedAsync(bulkUsers: 2500);
        const string bulk = "OU=Masowe,DC=contoso,DC=test";
        var list = await harness.OpenContainerAsync(bulk);
        var name = list.Columns.Single(c => c.Attribute == Attr.Name);
        await list.SortCommand.ExecuteAsync(name);
        await list.SortCommand.ExecuteAsync(name);
        Assert.Equal("Użytkownik 2499", list.Items[0].Name);

        string[] created = [$"CN=Użytkownik 1250a,{bulk}", $"CN=Aaa Pierwszy,{bulk}", $"CN=Żaneta Ostatnia,{bulk}"];
        string[] deleted = [$"CN=Użytkownik 0007,{bulk}", $"CN=Użytkownik 2000,{bulk}"];
        foreach (var dn in created)
        {
            harness.Directory.Put(User(dn, dn[3..8]));
        }

        foreach (var dn in deleted)
        {
            harness.Directory.Remove(dn);
        }

        await list.HandleChangesAsync([
            .. created.Select(dn => new DirectoryChange(DirectoryChangeKind.Created, dn, ObjectKind: ObjectKind.User)),
            .. deleted.Select(dn => new DirectoryChange(DirectoryChangeKind.Modified, dn, ObjectKind: ObjectKind.User)),
        ]);

        Assert.Equal(2501, list.Items.Count);
        Assert.All(created, dn => Assert.Contains(list.Items, r => r.Dn == dn));
        Assert.All(deleted, dn => Assert.DoesNotContain(list.Items, r => r.Dn == dn));
        var resorted = RowSorting.Sort(list.AllRows, Attr.Name, descending: true, CultureInfo.GetCultureInfo("en-US"), harness.Directory.Schema);
        Assert.Equal(resorted, list.AllRows);
        Assert.Equal(list.AllRows, list.Items);
        Assert.Equal("Żaneta Ostatnia", list.Items[0].Name);
        Assert.Equal("Aaa Pierwszy", list.Items[^1].Name);
    }

    [Fact]
    public async Task LanguageSwitch_DuringABackgroundSort_TheOrderOfTheNewCollationWins()
    {
        var harness = await TestHarness.ConnectedAsync(bulkUsers: 2000);
        const string bulk = "OU=Masowe,DC=contoso,DC=test";

        // English collation sorts Ć like C (Ćma before Cyna), Polish after C (Cyna before Ćma).
        harness.Directory.Put(User($"CN=Ćma,{bulk}", "cma"));
        harness.Directory.Put(User($"CN=Cyna,{bulk}", "cyna"));
        var list = await harness.OpenContainerAsync(bulk);
        Assert.NotEqual("Cyna", list.Items[0].Name);
        var english = new TaskCompletionSource();
        var polish = new TaskCompletionSource();
        var calls = 0;
        list.BackgroundSort = async sort =>
        {
            var call = Interlocked.Increment(ref calls);
            await (call == 1 ? english.Task : call == 2 ? polish.Task : Task.CompletedTask);
            return sort();
        };

        // An English sort runs when the language switches; the switch sorts again in Polish, and that one ends first.
        var sorting = list.SortCommand.ExecuteAsync(list.Columns.Single(c => c.Attribute == Attr.Name));
        harness.Localizer.SetCulture(CultureInfo.GetCultureInfo("pl-PL"));
        Assert.Equal(2, calls);
        polish.SetResult();
        await ObjectListViewModelTests.WaitUntilAsync(() => list.Items[0].Name == "Cyna");
        english.SetResult();
        await sorting;

        // The English snapshot finished last: it must not replace the Polish order (inserts rely on it).
        Assert.Equal(["Cyna", "Ćma"], list.Items.Take(2).Select(r => r.Name));
        var polishOrder = RowSorting.Sort(list.AllRows, Attr.Name, descending: false, CultureInfo.GetCultureInfo("pl-PL"), harness.Directory.Schema);
        Assert.Equal(polishOrder, list.AllRows);
        Assert.Equal(list.AllRows, list.Items);
    }

    private static string Dn(string name) => $"CN={name},{SortOu}";
}
