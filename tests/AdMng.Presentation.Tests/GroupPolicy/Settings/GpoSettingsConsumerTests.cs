using System.Globalization;
using System.Text;
using AdMng.Core.GroupPolicy.AdministrativeTemplates;
using AdMng.Core.GroupPolicy.Content;
using AdMng.Core.GroupPolicy.Management;
using AdMng.Presentation.Changes;
using AdMng.Presentation.GroupPolicy;
using AdMng.Presentation.Shell;
using AdMng.Presentation.Tests.Dialogs;
using AdMng.Testing;

namespace AdMng.Presentation.Tests.GroupPolicy.Settings;

/// <summary>
/// The consumers of the presenter (K6, design note 20.1): the GPO document's "Ustawienia" tab (GPMC's grouping, search,
/// Ctrl+F, "Edytuj to ustawienie", "Kopiuj", "Raport HTML…", the administrative templates read after the rows are shown) and
/// the change plan preview's readable differences.
/// </summary>
public sealed class GpoSettingsConsumerTests
{
    private static readonly CultureInfo Polish = CultureInfo.GetCultureInfo("pl-PL");

    private const string PasswordPolicy = "Zasady › Ustawienia systemu Windows › Ustawienia zabezpieczeń › Zasady konta › Zasady haseł";

    [Fact]
    public async Task SettingsTab_GroupsLikeGpmc_PoliciesFirst_TheGpoCommentLast()
    {
        var h = await GroupPolicyHarness.CreateAsync(Polish);
        using var document = await h.GpoAsync(h.Sample.WorkstationPasswords, GpoDocumentTab.Settings);

        // GPMC's tree order: password policy before account lockout, the GPO's own comment last.
        Assert.Equal(["Hasło musi spełniać wymagania co do złożoności", "Minimalna długość hasła", "Próg blokady konta", "Komentarz do obiektu GPO"],
            document.Settings.Select(s => s.Name));
        var length = document.Settings.Single(s => s.Name == "Minimalna długość hasła");
        Assert.Equal(("Konfiguracja komputera", PasswordPolicy, "12 znaków"), (length.Configuration, length.Group, length.Value));
        Assert.Equal("Computer/Security/System Access/MinimumPasswordLength", length.Technical);
        Assert.Equal("Machine/Microsoft/Windows NT/SecEdit/GptTmpl.inf", length.Source);
        Assert.True(length.CanEdit);
        Assert.Equal("5 nieudanych prób logowania", document.Settings.Single(s => s.Name == "Próg blokady konta").Value);

        // GPMC's order: the computer's policies, then the GPO's own comment.
        Assert.Equal("Konfiguracja komputera", document.Settings[0].Configuration);
        var comment = document.Settings[^1];
        Assert.Equal(("Obiekt zasad grupy", "Komentarz do obiektu GPO"), (comment.Configuration, comment.Name));
        Assert.False(comment.CanEdit);
        Assert.Null(document.SettingsNote);
    }

    [Fact]
    public async Task SettingsTab_Search_ByNameValueOrTechnicalKey()
    {
        var h = await GroupPolicyHarness.CreateAsync(Polish);
        using var document = await h.GpoAsync(h.Sample.WorkstationPasswords, GpoDocumentTab.Settings);
        Assert.Equal(string.Empty, document.SettingsSummary);

        document.SearchText = "długość";
        Assert.Equal(["Minimalna długość hasła"], document.Settings.Select(s => s.Name));
        Assert.Equal("Pokazano ustawienia: 1 z 4", document.SettingsSummary);

        document.SearchText = "LockoutBadCount";
        Assert.Equal(["Próg blokady konta"], document.Settings.Select(s => s.Name));

        document.SearchText = "łukasz";
        Assert.Equal(["Komentarz do obiektu GPO"], document.Settings.Select(s => s.Name));

        document.SelectedSetting = document.Settings[0];
        document.SearchText = "nic takiego";
        Assert.Empty(document.Settings);
        Assert.Null(document.SelectedSetting);
        Assert.Equal("Żadne ustawienie nie pasuje do wyszukiwania.", document.SettingsSummary);

        document.SearchText = string.Empty;
        Assert.Equal(4, document.Settings.Count);
        Assert.Equal(string.Empty, document.SettingsSummary);
    }

    [Fact]
    public async Task CtrlF_ShowsTheSettingsTab_AndAsksForItsSearchBox()
    {
        var h = await GroupPolicyHarness.CreateAsync(Polish);
        using var document = await h.GpoAsync(h.Sample.WorkstationPasswords);
        var searchable = Assert.IsAssignableFrom<ISearchableDocument>(document);
        var requests = 0;
        searchable.SearchFocusRequested += (_, _) => requests++;
        Assert.Equal(GpoDocumentTab.Scope, document.SelectedTab);

        searchable.FocusSearch();

        Assert.Equal(GpoDocumentTab.Settings, document.SelectedTab);
        Assert.Equal(1, requests);
        Assert.True(document.TakeSearchFocusRequest()); // the box, created with the tab, takes the keyboard once
        Assert.False(document.TakeSearchFocusRequest());
        searchable.FocusSearch(); // already on the tab: the box handles the request itself
        Assert.False(document.TakeSearchFocusRequest());
        Assert.Equal(2, requests);
    }

    [Fact]
    public async Task EditSetting_OpensTheEditorAtTheSetting_AndIsOffForAComment()
    {
        var h = await GroupPolicyHarness.CreateAsync(Polish);
        using var document = await h.GpoAsync(h.Sample.WorkstationPasswords, GpoDocumentTab.Settings);
        var requests = new List<GroupPolicyDocumentRequest>();
        h.Services.DocumentRequested += (_, r) => requests.Add(r);

        Assert.False(document.EditSettingCommand.CanExecute(null));
        document.SelectedSetting = document.Settings.Single(s => s.Name == "Komentarz do obiektu GPO");
        Assert.False(document.EditSettingCommand.CanExecute(null));
        document.SelectedSetting = document.Settings.Single(s => s.Name == "Minimalna długość hasła");
        Assert.True(document.EditSettingCommand.CanExecute(null));

        document.EditSettingCommand.Execute(null);

        var request = Assert.Single(requests);
        Assert.Equal(GroupPolicyDocumentKind.SecuritySettings, request.Kind);
        Assert.Equal(h.Sample.WorkstationPasswords, request.GpoId);
        Assert.Equal(GroupPolicySettingFocus.ForSetting("Computer/Security/System Access/MinimumPasswordLength"), request.Focus);
    }

    [Fact]
    public async Task Copy_PutsTheReadableRowOnTheClipboard()
    {
        var h = await GroupPolicyHarness.CreateAsync(Polish);
        using var document = await h.GpoAsync(h.Sample.WorkstationPasswords, GpoDocumentTab.Settings);
        Assert.False(document.CopySettingCommand.CanExecute(null));
        document.SelectedSetting = document.Settings.Single(s => s.Name == "Minimalna długość hasła");

        document.CopySettingCommand.Execute(null);

        Assert.Equal("Minimalna długość hasła: 12 znaków" + Environment.NewLine + "Konfiguracja komputera › " + PasswordPolicy, h.B2.Clipboard.Text);
        Assert.Contains(h.B2.Notifications.Items, n => n.Message == "Ustawienie skopiowano do schowka.");
    }

    [Fact]
    public async Task HtmlReport_GoesToTheChosenFile_SelfContainedAndEscaped()
    {
        var h = await GroupPolicyHarness.CreateAsync(Polish);
        h.Sysvol.PutText(h.Sample.WorkstationPasswords, "GPO.cmt", "<script>alert('gęślą')</script> & \"jaźń\"");
        using var document = await h.GpoAsync(h.Sample.WorkstationPasswords, GpoDocumentTab.Settings);
        using var folder = new TempFolder();
        var path = Path.Combine(folder.Path, "raport-zażółć.html");
        h.B2.Files.NextSavePath = path;
        Assert.True(document.HtmlReportCommand.CanExecute(null));

        await document.HtmlReportCommand.ExecuteAsync(null);

        Assert.Equal("Stacje robocze – Zasady haseł.html", h.B2.Files.LastDefaultFileName);
        var html = await File.ReadAllTextAsync(path, Encoding.UTF8, TestContext.Current.CancellationToken);
        Assert.StartsWith("<!DOCTYPE html>", html, StringComparison.Ordinal);
        Assert.Contains("<html lang=\"pl\">", html, StringComparison.Ordinal);
        Assert.Contains("<h1>Ustawienia obiektu GPO „Stacje robocze – Zasady haseł”</h1>", html, StringComparison.Ordinal);
        Assert.Contains("<h2>Konfiguracja komputera</h2>", html, StringComparison.Ordinal);
        Assert.Contains("<h3>Zasady › Ustawienia systemu Windows › Ustawienia zabezpieczeń › Zasady konta › Zasady haseł</h3>", html, StringComparison.Ordinal);
        Assert.Contains("<td>Minimalna długość hasła</td><td>12 znaków</td>", html, StringComparison.Ordinal);
        Assert.Contains("&lt;script&gt;alert(&#39;gęślą&#39;)&lt;/script&gt; &amp; &quot;jaźń&quot;", html, StringComparison.Ordinal);
        Assert.DoesNotContain("<script", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("http", html, StringComparison.OrdinalIgnoreCase); // nothing loaded from elsewhere
        Assert.Contains(h.B2.Notifications.Items, n => n.Message.Contains("raport-zażółć.html", StringComparison.Ordinal));

        // Cancelled: nothing is written.
        h.B2.Files.NextSavePath = null;
        File.Delete(path);
        await document.HtmlReportCommand.ExecuteAsync(null);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public async Task AdministrativeTemplates_NameThePoliciesAfterTheRowsAreShown()
    {
        var h = await GroupPolicyHarness.CreateAsync(Polish);
        h.AddCentralStore();
        var set = SampleAdministrativeTemplates.LoadPolish();
        var document = new RegistryPolicyDocument();
        PolicyEngine.Write(set.Find(SampleAdministrativeTemplates.HidePreview)!.Policy, PolicySetting.Enabled(new Dictionary<string, PolicyElementValue>()), document);
        document.Set(@"Software\Firma\Inne", "Wartość", RegistryFormat.RegDword, RegistryPolFile.DwordData(7));
        h.Sysvol.Put(h.Sample.WorkstationPasswords, "Machine/Registry.pol", document.ToBytes());

        using var gpo = await h.GpoAsync(h.Sample.WorkstationPasswords, GpoDocumentTab.Settings);
        await B2Harness.WaitUntilAsync(() => gpo.Settings.Any(s => s.Name == "Wyłącz okienko podglądu"));

        var policy = gpo.Settings.Single(s => s.Name == "Wyłącz okienko podglądu");
        Assert.Equal("Włączone", policy.Value);
        Assert.StartsWith("Zasady › Szablony administracyjne › ", policy.Group, StringComparison.Ordinal);
        Assert.Equal(GroupPolicySettingFocus.ForPolicy(PolicyClass.Machine, SampleAdministrativeTemplates.HidePreview), policy.Text.Focus);
        var extra = gpo.Settings.Single(s => s.Name == @"HKLM\Software\Firma\Inne\Wartość");
        Assert.Equal("Zasady › Szablony administracyjne › Dodatkowe ustawienia rejestru", extra.Group);
        Assert.Null(gpo.DefinitionsNote);
        Assert.Equal(6, gpo.SettingCount);
    }

    [Fact]
    public async Task WithoutTemplates_RegistryValuesKeepTheirPaths_AndAQuietNoteSaysWhy()
    {
        var h = await GroupPolicyHarness.CreateAsync(Polish);
        var document = new RegistryPolicyDocument();
        document.Set(@"Software\Policies\Contoso\Eksplorator", "NoPreview", RegistryFormat.RegDword, RegistryPolFile.DwordData(1));
        h.Sysvol.Put(h.Sample.WorkstationPasswords, "Machine/Registry.pol", document.ToBytes());

        using var gpo = await h.GpoAsync(h.Sample.WorkstationPasswords, GpoDocumentTab.Settings);
        await B2Harness.WaitUntilAsync(() => gpo.DefinitionsNote is not null && !gpo.DefinitionsNote.StartsWith("Wczytywanie", StringComparison.Ordinal));

        Assert.StartsWith("Brak szablonów administracyjnych", gpo.DefinitionsNote, StringComparison.Ordinal);
        var value = gpo.Settings.Single(s => s.Name == @"HKLM\Software\Policies\Contoso\Eksplorator\NoPreview");
        Assert.Equal("1 (REG_DWORD)", value.Value);
        Assert.Equal(GroupPolicyDocumentKind.AdministrativeTemplates, value.Text.Focus?.Editor);
    }

    [Fact]
    public async Task Preview_ShowsSettingsReadably_AChangedSettingAsOneLine()
    {
        var oldTemplate = Encoding.Unicode.GetBytes(FakeDirectoryBuilder.Template("[System Access]", "MinimumPasswordLength = 7", "PasswordComplexity = 1"));
        var newTemplate = Encoding.Unicode.GetBytes(FakeDirectoryBuilder.Template("[System Access]", "MinimumPasswordLength = 12", "PasswordComplexity = 1", "LockoutBadCount = 0"));
        var diff = GpoFileDiff.Compare("Machine/Microsoft/Windows NT/SecEdit/GptTmpl.inf", oldTemplate, newTemplate);
        var localizer = new Presentation.Localization.ResxLocalizer(Polish);

        var lines = await ChangePlanPreviewViewModel.DiffLinesAsync(diff.Lines, localizer, null, CancellationToken.None);

        var changed = Assert.Single(lines, l => l.Kind == PreviewDiffKind.Changed);
        Assert.Equal("Minimalna długość hasła: 7 znaków → 12 znaków", changed.Text);
        Assert.Contains("MinimumPasswordLength = 7", changed.ToolTip, StringComparison.Ordinal);
        Assert.Contains("MinimumPasswordLength = 12", changed.ToolTip, StringComparison.Ordinal);
        Assert.Contains(lines, l => l is { Kind: PreviewDiffKind.Added, Text: "Próg blokady konta: Konto nie jest blokowane" });
        Assert.Contains(lines, l => l is { Kind: PreviewDiffKind.Unchanged, Text: "Hasło musi spełniać wymagania co do złożoności: Włączone" });
        Assert.DoesNotContain(lines, l => l.Kind == PreviewDiffKind.Removed);

        // Text lines of other files stay as they are.
        var text = GpoFileDiff.Compare("Machine/Scripts/Startup/start.cmd", Encoding.UTF8.GetBytes("@echo off\r\n"), Encoding.UTF8.GetBytes("@echo on\r\n"));
        var plain = await ChangePlanPreviewViewModel.DiffLinesAsync(text.Lines, localizer, null, CancellationToken.None);
        Assert.Equal([("@echo off", PreviewDiffKind.Removed), ("@echo on", PreviewDiffKind.Added)], plain.Select(l => (l.Text, l.Kind)));
        Assert.All(plain, l => Assert.Null(l.ToolTip));
    }
}
