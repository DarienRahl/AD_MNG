using AdMng.Core.Directory;
using AdMng.Core.Ldap;
using AdMng.Presentation.Lists;
using AdMng.Presentation.Tests.TestDoubles;
using AdMng.Presentation.Views;
using static AdMng.Presentation.Tests.TestDoubles.TestDirectory;

namespace AdMng.Presentation.Tests.Views;

public class ViewsTests
{
    [Fact]
    public void BuiltInViews_HaveValidFilters_AndStableIds()
    {
        foreach (var view in BuiltInViews.All)
        {
            var filter = view.BuildFilter(TestHarness.Now, 90);
            Assert.Null(LdapFilter.Validate(filter));
            Assert.StartsWith(BuiltInViewDefinition.IdPrefix, view.Id, StringComparison.Ordinal);
            Assert.Same(view, BuiltInViews.Find(view.Id));
        }

        Assert.Equal(Enum.GetValues<BuiltInViewKind>().Length, BuiltInViews.All.Count);
    }

    [Fact]
    public void InactiveFilter_UsesThresholdsComputedFromNow()
    {
        var filter = QuickFilters.Inactive(TestHarness.Now, 90);
        var threshold = TestHarness.Now.AddDays(-90);

        Assert.Contains($"(lastLogonTimestamp<={threshold.ToFileTime()})", filter, StringComparison.Ordinal);
        Assert.Contains("(whenCreated<=20260715120000.0Z)", filter, StringComparison.Ordinal);
        Assert.Contains("(sAMAccountType=805306368)", filter, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(BuiltInViewKind.LockedUsers, new[] { "Jan Nowak" })]
    [InlineData(BuiltInViewKind.DisabledUsers, new[] { "Ewa Kowalska" })]
    [InlineData(BuiltInViewKind.EmptyGroups, new string[0])]
    public async Task BuiltInViews_QueryTheDirectoryLive(BuiltInViewKind kind, string[] expected)
    {
        var harness = await TestHarness.ConnectedAsync();
        var list = harness.Lists.CreateList();
        var view = BuiltInViews.All.Single(v => v.Kind == kind);

        await list.NavigateAsync(view.ToLocation(harness.Localizer, Domain, TestHarness.Now, 90));

        Assert.Equal(expected, list.Items.Select(r => r.Name).Order(StringComparer.Ordinal));
        Assert.Equal(SearchScope.Subtree, list.LastSearch!.Scope);
    }

    [Fact]
    public void SavedView_UsesPortableDomainPlaceholder()
    {
        Assert.Equal("OU=IT,OU=Firma,{domain}", SavedView.ToPortableDn(It, Domain));
        Assert.Equal("{domain}", SavedView.ToPortableDn(Domain, Domain));
        Assert.Equal("OU=X,DC=fabrikam,DC=com", SavedView.ToPortableDn("OU=X,DC=fabrikam,DC=com", Domain));

        var view = new SavedView { Name = "IT", BaseDn = "OU=IT,OU=Firma,{domain}", Scope = SearchScope.OneLevel, SortAttribute = "name", QuickFilters = [QuickFilterKind.Disabled] };
        var location = view.ToLocation(Domain);

        Assert.Equal(It, location.BaseDn);
        Assert.Equal(new SortKey("name"), location.Sort);
        Assert.Equal(SavedView.LayoutKey(view.Id), location.ColumnLayoutKey);
        Assert.Equal([QuickFilterKind.Disabled], location.QuickFilters);
    }

    [Fact]
    public async Task SavedViewQuickFilters_AreAppliedAsChips()
    {
        var harness = await TestHarness.ConnectedAsync();
        var list = harness.Lists.CreateList();
        var view = new SavedView { Name = "IT wyłączeni", BaseDn = "OU=IT,OU=Firma,{domain}", QuickFilters = [QuickFilterKind.Disabled] };

        await list.NavigateAsync(view.ToLocation(Domain));

        Assert.True(list.FilterChips.Single(c => c.Kind == QuickFilterKind.Disabled).IsActive);
        Assert.Equal(["Ewa Kowalska"], list.Items.Select(r => r.Name));
        Assert.Single(harness.Directory.Searches, s => s.BaseDn == It);
    }

    [Fact]
    public async Task Editor_ValidatesFilter_TestsLive_AndSaves()
    {
        var harness = await TestHarness.ConnectedAsync();
        harness.Dialogs.PickedContainers.Enqueue(It);
        var editor = new SavedViewEditorViewModel(harness.Sessions, harness.Dialogs, harness.Localizer, harness.Errors);
        bool? closed = null;
        editor.CloseRequested += (_, saved) => closed = saved;

        editor.Name = "Działowcy IT";
        editor.Filter = "(&(objectCategory=person)(department=IT)";
        Assert.NotNull(editor.FilterError);
        Assert.False(editor.SaveCommand.CanExecute(null));

        editor.Filter = "(&(objectCategory=person)(department=IT))";
        await editor.BrowseBaseDnCommand.ExecuteAsync(null);
        await editor.TestCommand.ExecuteAsync(null);

        Assert.Null(editor.FilterError);
        Assert.Equal("OU=IT,OU=Firma,{domain}", editor.BaseDn);
        Assert.Equal("contoso.test/Firma/IT", editor.BaseDnDisplay);
        Assert.StartsWith("Matching objects: 4", editor.TestResult, StringComparison.Ordinal);

        editor.SaveCommand.Execute(null);
        Assert.True(closed);
        Assert.Equal("Działowcy IT", editor.Result!.Name);
        Assert.Equal(SearchScope.Subtree, editor.Result.Scope);
        Assert.True(editor.IsNew);
    }

    [Fact]
    public async Task Library_ExportImportRoundTrip_RejectsInvalid_AndNeverOverwrites()
    {
        var settings = new InMemorySettingsStore();
        var library = new ViewLibrary(settings);
        await library.SaveViewAsync(new SavedView { Id = "a", Name = "Zablokowani – Kraków", Filter = "(lockoutTime>=1)", Columns = ["name", "l"] });
        await library.SaveViewAsync(new SavedView { Id = "b", Name = "Druga" });
        var json = library.ExportJson();

        var parsed = ViewLibrary.ParseImport(json);
        var added = await library.ImportAsync(parsed.Views);

        Assert.Equal(0, parsed.Rejected);
        Assert.Equal(2, added);
        Assert.Equal(4, library.SavedViews.Count);
        Assert.Equal(4, library.SavedViews.Select(v => v.Id).Distinct().Count());
        Assert.Equal(["name", "l"], library.SavedViews[2].Columns);
        Assert.DoesNotContain("password", json, StringComparison.OrdinalIgnoreCase);

        var invalid = library.ExportJson([new SavedView { Name = "zły", Filter = "(lockoutTime>=1" }, new SavedView { Name = " " }]);
        Assert.Equal(2, ViewLibrary.ParseImport(invalid).Rejected);
        Assert.Contains("Zablokowani – Kraków", json, StringComparison.Ordinal);
        Assert.Throws<FormatException>(() => ViewLibrary.ParseImport("{\"format\":\"other\",\"views\":[]}"));
    }

    [Fact]
    public async Task Library_ReordersAndDeletesViews_AndPinsContainersOnce()
    {
        var settings = new InMemorySettingsStore();
        var library = new ViewLibrary(settings);
        var changed = 0;
        library.Changed += (_, _) => changed++;
        await library.SaveViewAsync(new SavedView { Id = "a", Name = "A" });
        await library.SaveViewAsync(new SavedView { Id = "b", Name = "B" });
        settings.Current.ColumnLayouts[SavedView.LayoutKey("a")] = [];

        await library.MoveViewAsync("b", -1);
        Assert.Equal(["B", "A"], library.SavedViews.Select(v => v.Name));
        await library.DeleteViewAsync("a");
        Assert.Equal(["B"], library.SavedViews.Select(v => v.Name));
        Assert.False(settings.Current.ColumnLayouts.ContainsKey(SavedView.LayoutKey("a")));

        await library.PinAsync(It, "IT");
        await library.PinAsync(It.ToUpperInvariant(), "IT");
        Assert.Single(library.PinnedContainers);
        Assert.True(library.IsPinned(It));
        Assert.True(changed >= 5);
    }
}
