using System.Globalization;
using System.Resources;
using AdMng.Presentation.GroupPolicy;
using AdMng.Presentation.GroupPolicy.Search;
using AdMng.Presentation.Localization;
using AdMng.Presentation.Tests.Dialogs;

namespace AdMng.Presentation.Tests.GroupPolicy.Search;

/// <summary>
/// The entry points of "Wyszukiwarka ustawień GPO" (K7, 21.4): the palette entry <c>gpo.searchSettings</c>, the GPO list's
/// toolbar, "Znajdź we wszystkich GPO" of a GPO's "Ustawienia" tab (with the tab's search text, else the selected setting) and
/// the query handed to the open tab; F5; the localization families.
/// </summary>
public sealed class GpoSettingsSearchEntryTests
{
    private static readonly CultureInfo Polish = CultureInfo.GetCultureInfo("pl-PL");

    [Fact]
    public async Task EntryPoints_OpenOneTabPerWindow_AndHandTheQueryToTheOpenTab()
    {
        var h = await GroupPolicyHarness.CreateAsync(Polish);
        using var shell = h.CreateShell();
        await shell.InitializeAsync();

        var descriptor = Assert.Single(shell.CommandDescriptors, d => d.Id == "gpo.searchSettings");
        Assert.Equal("Wyszukaj ustawienie we wszystkich GPO…", h.B2.Localizer[descriptor.TitleKey]);
        Assert.Equal(L.CommandCategories.Tools, descriptor.CategoryKey);
        Assert.True(descriptor.Command.CanExecute(null));
        descriptor.Command.Execute(null);
        var document = Assert.IsType<GpoSettingsSearchDocumentViewModel>(shell.SelectedTab);
        await B2Harness.WaitUntilAsync(() => document.HasIndex && !document.IsLoading);
        Assert.Equal(string.Empty, document.SearchText);

        // The GPO list's toolbar selects the same tab.
        var objects = Assert.IsType<GroupPolicyObjectsDocumentViewModel>(shell.OpenGroupPolicyDocument(new GroupPolicyDocumentRequest(GroupPolicyDocumentKind.Objects)));
        objects.SearchSettingsCommand.Execute(null);
        await B2Harness.WaitUntilAsync(() => ReferenceEquals(shell.SelectedTab, document));
        shell.OpenGroupPolicySearchCommand.Execute(null);
        Assert.Same(document, shell.SelectedTab);

        // "Znajdź we wszystkich GPO" of a GPO's settings tab: the tab's search text, handed to the open search tab.
        var gpo = Assert.IsType<GroupPolicyObjectDocumentViewModel>(shell.OpenGroupPolicyDocument(
            new GroupPolicyDocumentRequest(GroupPolicyDocumentKind.Object) { GpoId = h.Sample.WorkstationPasswords, Tab = GpoDocumentTab.Settings }));
        await B2Harness.WaitUntilAsync(() => !gpo.IsLoading && gpo.AllSettings.Count > 0);
        gpo.SearchText = "  długość  ";
        Assert.Equal("długość", gpo.SearchAllText);
        gpo.SearchAllGposCommand.Execute(null);
        await B2Harness.WaitUntilAsync(() => ReferenceEquals(shell.SelectedTab, document) && document.SearchText == "długość");
        await document.SearchCommand.ExecuteAsync(null);
        Assert.Contains(document.Groups, g => g.Name == "Minimalna długość hasła");
        Assert.Single(shell.Documents.OfType<GpoSettingsSearchDocumentViewModel>());

        // Without a search text: the selected setting's name.
        gpo.SearchText = string.Empty;
        gpo.SelectedSetting = gpo.Settings.Single(s => s.Name == "Próg blokady konta");
        Assert.Equal("Próg blokady konta", gpo.SearchAllText);
        gpo.SearchAllGposCommand.Execute(null);
        await B2Harness.WaitUntilAsync(() => ReferenceEquals(shell.SelectedTab, document) && document.SearchText == "Próg blokady konta");

        // F5 re-reads the changed GPOs and searches again.
        var view = document.View;
        await shell.RefreshCommand.ExecuteAsync(null);
        await B2Harness.WaitUntilAsync(() => !document.IsLoading && !ReferenceEquals(view, document.View));
        Assert.Contains(document.Groups, g => g.Name == "Próg blokady konta");
    }

    [Fact]
    public async Task ANewTab_OpenedWithAQuery_SearchesItOnceTheIndexIsRead()
    {
        var h = await GroupPolicyHarness.CreateAsync(Polish);
        using var shell = h.CreateShell();
        await shell.InitializeAsync();
        var requests = new List<GroupPolicyDocumentRequest>();
        h.Services.DocumentRequested += (_, r) => requests.Add(r);

        Assert.True(h.Actions.OpenSettingsSearch("hasła"));
        var request = Assert.Single(requests);
        Assert.Equal((GroupPolicyDocumentKind.SettingsSearch, "hasła"), (request.Kind, request.SearchText));
        var document = await B2HarnessWait(shell);
        Assert.Equal("hasła", document.SearchText);
        await B2Harness.WaitUntilAsync(() => document.HasIndex && !document.IsLoading && !document.IsSummaryShown);
        Assert.NotNull(document.LastQuery);

        // The search tab is the window's active GPO when a row is selected (palette: edit templates / security settings).
        document.SelectedRow = document.Rows[0];
        Assert.Equal((document.Rows[0].GpoId, document.Rows[0].GpoName), shell.ActiveGpo());
    }

    [Fact]
    public void LocalizationFamilies_AreComplete_InBothLanguages()
    {
        var resources = new ResourceManager(ResxLocalizer.ResourceBaseName, typeof(ResxLocalizer).Assembly);
        var en = resources.GetResourceSet(CultureInfo.InvariantCulture, true, false)!;
        var pl = resources.GetResourceSet(CultureInfo.GetCultureInfo("pl"), true, false)!;
        var keys = L.GpoSearch.AllKeys().ToList();

        Assert.Equal([], keys.Where(k => en.GetString(k) is null));
        Assert.Equal([], keys.Where(k => pl.GetString(k) is null));
        Assert.Equal(Enum.GetValues<GpoSearchField>().Length + Enum.GetValues<GpoSearchArea>().Length + Enum.GetValues<GpoValueAgreement>().Length, keys.Count);
        var l = new ResxLocalizer(Polish);
        Assert.Equal("Wyszukiwarka ustawień GPO", l[L.GpoSearch.Title]);
        Assert.Equal("Znajdź we wszystkich GPO", l[L.GpoSearch.FindInAll]);
        Assert.Equal("ustawione w 3 GPO – wartości różne", l.Format(L.GpoSearch.AgreementKey(GpoValueAgreement.Different), 3));
        Assert.Equal("Klucz techniczny", l[L.GpoSearch.FieldKey(GpoSearchField.Technical)]);
    }

    private static async Task<GpoSettingsSearchDocumentViewModel> B2HarnessWait(Presentation.Shell.ShellViewModel shell)
    {
        await B2Harness.WaitUntilAsync(() => shell.SelectedTab is GpoSettingsSearchDocumentViewModel);
        return (GpoSettingsSearchDocumentViewModel)shell.SelectedTab!;
    }
}
