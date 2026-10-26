using System.Globalization;
using AdMng.Core.ActiveDirectory;
using AdMng.Core.Filters;
using AdMng.Presentation.Filters;
using AdMng.Presentation.Lists;
using AdMng.Presentation.Settings;
using AdMng.Presentation.Tests.Dialogs;
using AdMng.Presentation.Views;

namespace AdMng.Presentation.Tests.Filters;

/// <summary>
/// The filter builder and the saved filters (README U4): the editor, the library with its JSON export/import, the window,
/// and the uses of a saved filter - chips of the object lists (also in saved views).
/// </summary>
public class SavedFiltersTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Editor_BuildsTheFilterThroughLdapFilter_ShowsProblemsInPolish_AndSaves()
    {
        var harness = await B2Harness.CreateAsync(CultureInfo.GetCultureInfo("pl-PL"));
        var editor = Editor(harness);

        Assert.Equal("Nowy filtr", editor.WindowTitle);
        Assert.Contains(Attr.Department, editor.Attributes);
        Assert.DoesNotContain(Attr.ObjectSid, editor.Attributes);
        var first = Assert.Single(Assert.Single(editor.Groups).Conditions);
        Assert.Equal("Grupa 1, warunek 1: Wybierz atrybut.", editor.Error);
        Assert.Equal("Wybierz atrybut.", first.Issue);
        Assert.False(editor.SaveCommand.CanExecute(null));

        first.Attribute = Attr.Department;
        first.Value = "Księgowość (Kraków)*";
        Assert.Null(editor.Error);
        Assert.Equal(@"(department=Księgowość \28Kraków\29\2a)", editor.Preview);

        // A second condition in the group (LUB) on a constructed attribute, a second group with "member of".
        editor.Groups[0].Combinator = editor.Groups[0].Combinators.Single(c => c.Value == FilterCombinator.Any);
        editor.AddConditionCommand.Execute(editor.Groups[0]);
        var locked = editor.Groups[0].Conditions[1];
        locked.Attribute = Attr.UserAccountControlComputed;
        locked.Operator = locked.Operators.Single(o => o.Value == FilterOperator.BitSet);
        locked.Value = "16";
        Assert.Contains("msDS-User-Account-Control-Computed", editor.ClientCheckNote, StringComparison.Ordinal);
        editor.AddGroupCommand.Execute(null);
        var member = editor.Groups[1].Conditions[0];
        member.Operator = member.Operators.Single(o => o.Value == FilterOperator.MemberOf);
        Assert.True(member.IsMembership);
        Assert.False(member.NeedsAttribute);
        harness.Dialogs.PickedObjects.Enqueue([harness.Company.GroupAccounting]);
        await editor.PickGroupCommand.ExecuteAsync(member);
        Assert.Equal("CN=GG_Ksiegowosc,OU=Grupy,OU=Firma,{domain}", member.Value);
        Assert.Equal(
            @"(&(|(department=Księgowość \28Kraków\29\2a)(msDS-User-Account-Control-Computed:1.2.840.113556.1.4.803:=16))(memberOf:1.2.840.113556.1.4.1941:=CN=GG_Ksiegowosc,OU=Grupy,OU=Firma,DC=contoso,DC=test))",
            editor.Preview);

        // An invalid value is reported where it is.
        member.Value = "nie DN";
        Assert.Equal("Grupa 2, warunek 1: Wybierz grupę (poprawny DN).", editor.Error);
        member.Value = "CN=GG_Ksiegowosc,OU=Grupy,OU=Firma,{domain}";

        // The test counts on the DC: the server part, then the client check (nobody locked out).
        await editor.TestCommand.ExecuteAsync(null);
        Assert.StartsWith("Pasujące obiekty: 0", editor.TestResult, StringComparison.Ordinal);

        editor.Name = "Księgowość zablokowana";
        editor.SaveCommand.Execute(null);
        var result = Assert.IsType<SavedFilter>(editor.Result);
        Assert.Equal("Księgowość zablokowana", result.Name);
        Assert.Equal(2, result.Definition.Groups.Count);
        Assert.Equal(FilterCombinator.Any, result.Definition.Groups[0].Combinator);

        // Raw LDAP: starts from the generated filter and is validated.
        var raw = Editor(harness, result);
        Assert.Equal("Edytuj filtr", raw.WindowTitle);
        raw.IsRaw = true;
        Assert.Equal(editor.Preview, raw.RawFilter);
        raw.RawFilter = "(&(objectClass=user)";
        Assert.Equal("Filtr LDAP jest niepoprawny (nawiasy, sekwencje ucieczki).", raw.Error);
        raw.RawFilter = "objectClass=user";
        Assert.Null(raw.Error);
        Assert.Equal("(objectClass=user)", raw.Preview);
        raw.SaveCommand.Execute(null);
        Assert.Equal("objectClass=user", raw.Result!.Definition.RawLdap);
        Assert.Equal(result.Id, raw.Result.Id);
    }

    [Fact]
    public async Task Library_DeletesEveryUse_ExportsAndImportsTextOnly()
    {
        var harness = await B2Harness.CreateAsync();
        var library = new FilterLibrary(harness.Settings);
        var filter = new SavedFilter { Id = "it", Name = "Dział IT", Definition = FilterDefinition.Of(new FilterCondition(Attr.Department, FilterOperator.Equals, "IT")) };
        await library.SaveAsync(filter);
        await harness.Views.SaveViewAsync(new SavedView { Id = "v", Name = "Widok", SavedFilters = ["it", "other"] });
        await harness.Settings.UpdateAsync(s =>
        {
            s.AccountReview.Scope = s.AccountReview.Scope with { FilterId = "it" };
            s.AccountReview.Privileges.Rules.Add(new PrivilegedRuleSetting { FilterId = "it" });
        }, Ct);

        var json = library.ExportJson();
        Assert.Contains("\"format\": \"AdMng.Filters\"", json, StringComparison.Ordinal);
        Assert.Contains("Dział IT", json, StringComparison.Ordinal);

        await library.DeleteAsync("it");
        Assert.Empty(library.Filters);
        Assert.Equal(["other"], harness.Views.FindView("v")!.SavedFilters);
        Assert.Null(harness.Settings.Current.AccountReview.Scope.FilterId);
        Assert.Empty(harness.Settings.Current.AccountReview.Privileges.Rules);

        var parsed = FilterLibrary.ParseImport(json.Replace("\"filters\": [", "\"filters\": [ { \"id\": \"x\", \"name\": \"\" }, { \"id\": \"y\", \"name\": \"Zły\", \"definition\": { \"rawLdap\": \"(a=\" } },", StringComparison.Ordinal));
        Assert.Equal(2, parsed.Rejected);
        Assert.Equal(1, await library.ImportAsync(parsed.Filters));
        Assert.Equal(1, await library.ImportAsync(parsed.Filters));
        Assert.Equal(2, library.Filters.Count);
        Assert.Single(library.Filters, f => f.Id == "it");
        Assert.Throws<FormatException>(() => FilterLibrary.ParseImport("{ \"format\": \"AdMng.Views\", \"views\": [] }"));
    }

    [Fact]
    public async Task Window_NewEditDelete_ThroughTheEditor_AndExportImportFiles()
    {
        var harness = await B2Harness.CreateAsync();
        using var window = harness.Factory.CreateSavedFilters();
        Assert.True(window.IsEmpty);

        harness.Dialogs.FilterEditorHandler = editor =>
        {
            editor.Name = "Bez działu";
            var condition = editor.Groups[0].Conditions[0];
            condition.Attribute = Attr.Department;
            condition.Operator = condition.Operators.Single(o => o.Value == FilterOperator.NotPresent);
            editor.SaveCommand.Execute(null);
            return true;
        };
        await window.NewCommand.ExecuteAsync(null);
        var item = Assert.Single(window.Items);
        Assert.Equal("Bez działu", item.Name);
        Assert.Equal("department does not exist", item.Summary);
        Assert.Same(item, window.SelectedItem);
        Assert.Contains(harness.Notifications.Items, m => m.Message == "The filter Bez działu was saved.");

        harness.Dialogs.FilterEditorHandler = editor =>
        {
            Assert.Equal("Edit filter", editor.WindowTitle);
            editor.Description = "Konta bez działu";
            editor.SaveCommand.Execute(null);
            return true;
        };
        await window.EditCommand.ExecuteAsync(null);
        Assert.Equal("Konta bez działu", Assert.Single(window.Items).Description);

        var path = Path.Combine(Path.GetTempPath(), $"admng-filters-{Guid.NewGuid():N}.json");
        try
        {
            harness.Files.NextSavePath = path;
            await window.ExportCommand.ExecuteAsync(null);
            Assert.Contains("Konta bez działu", await File.ReadAllTextAsync(path, Ct), StringComparison.Ordinal);

            harness.Dialogs.ConfirmAnswers.Enqueue(true);
            await window.DeleteCommand.ExecuteAsync(null);
            Assert.True(window.IsEmpty);

            harness.Files.NextOpenPath = path;
            await window.ImportCommand.ExecuteAsync(null);
            Assert.Equal("Bez działu", Assert.Single(window.Items).Name);

            await File.WriteAllTextAsync(path, "nie JSON", Ct);
            await window.ImportCommand.ExecuteAsync(null);
            Assert.Equal("The file is not an AD Manager filters file.", harness.Dialogs.Errors[^1].Message);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task ObjectList_SavedFilterChip_AndsTheServerPart_ChecksConstructedAttributesOnTheClient_AndSavedViewsKeepIt()
    {
        var harness = await B2Harness.CreateAsync();
        var library = new FilterLibrary(harness.Settings);
        await library.SaveAsync(new SavedFilter
        {
            Id = "warszawa",
            Name = "Warszawa",
            Definition = FilterDefinition.Of(new FilterCondition(Attr.City, FilterOperator.Equals, "Warszawa")),
        });
        await library.SaveAsync(new SavedFilter
        {
            Id = "locked",
            Name = "Zablokowane",
            Definition = FilterDefinition.Of(new FilterCondition(Attr.UserAccountControlComputed, FilterOperator.BitSet, "16")),
        });
        using var list = harness.Lists.CreateList();
        await list.NavigateAsync(ListLocation.ForQuery("Firma", harness.Company.CompanyOu, Core.Directory.SearchScope.Subtree, "(objectClass=user)"));
        Assert.Equal(["Warszawa", "Zablokowane"], list.SavedFilterChips.Select(c => c.Title));
        var all = list.Items.Count;

        list.SavedFilterChips.Single(c => c.Id == "warszawa").IsActive = true;
        await WaitLoadedAsync(list);
        Assert.Contains("(l=Warszawa)", list.LastSearch!.Filter, StringComparison.Ordinal);
        Assert.Equal(["Jan Kowalski", "Małgorzata Wójcik", "Paweł Lewandowski"], list.Items.Select(r => r.Name).Order(StringComparer.Ordinal));

        list.SavedFilterChips.Single(c => c.Id == "warszawa").IsActive = false;
        list.SavedFilterChips.Single(c => c.Id == "locked").IsActive = true;
        await WaitLoadedAsync(list);
        Assert.DoesNotContain(Attr.UserAccountControlComputed, list.LastSearch!.Filter, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(Attr.UserAccountControlComputed, list.LastSearch.Attributes);
        Assert.Equal(["Żaneta Szymańska"], list.Items.Select(r => r.Name));
        Assert.True(all > 1);

        // "Zapisz jako widok" keeps the active saved filter; the view shows it as an active chip again.
        var shell = harness.CreateShell();
        harness.Dialogs.ViewEditorHandler = editor =>
        {
            editor.Name = "Zablokowani w firmie";
            editor.SaveCommand.Execute(null);
            return true;
        };
        var view = new SavedView { Id = "v", Name = "Zablokowani", BaseDn = "OU=Firma,{domain}", Filter = "(objectClass=user)", SavedFilters = ["locked"] };
        await harness.Views.SaveViewAsync(view);
        using var viewList = harness.Lists.CreateList();
        await viewList.NavigateAsync(view.ToLocation(harness.Builder.DomainDn));
        Assert.True(viewList.SavedFilterChips.Single(c => c.Id == "locked").IsActive);
        Assert.Equal(["Żaneta Szymańska"], viewList.Items.Select(r => r.Name));
        _ = shell;

        // A deleted filter disappears from the chips and the list is read again without it.
        await library.DeleteAsync("locked");
        await WaitLoadedAsync(viewList);
        Assert.Equal(["Warszawa"], viewList.SavedFilterChips.Select(c => c.Title));
        Assert.True(viewList.Items.Count > 1);
    }

    private static FilterEditorViewModel Editor(B2Harness harness, SavedFilter? existing = null) =>
        new(harness.Sessions, harness.Dialogs, harness.Localizer, harness.Errors, harness.Time, existing);

    private static Task WaitLoadedAsync(ObjectListViewModel list) => list.CurrentLoad;
}
