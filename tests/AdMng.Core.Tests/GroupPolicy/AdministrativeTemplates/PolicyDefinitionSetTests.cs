using System.Globalization;
using System.Text;
using AdMng.Core.GroupPolicy.AdministrativeTemplates;
using AdMng.Testing;

namespace AdMng.Core.Tests.GroupPolicy.AdministrativeTemplates;

public class PolicyDefinitionSetTests
{
    [Fact]
    public void AdmlParser_ReadsStringsAndEveryControl_SkipsUnknownOnes()
    {
        var adml = AdmlParser.Parse("contoso.adml", "en-US", SampleAdministrativeTemplates.Utf8(SampleAdministrativeTemplates.ContosoAdmlEn));

        Assert.Equal("Configure automatic updates", adml.Strings["konfiguracja"]);
        var updates = adml.Presentations["Konfiguracja"].Controls;
        Assert.Equal(1, Assert.IsType<DropDownListPresentation>(updates[0]).DefaultItem);
        Assert.IsType<TextPresentation>(updates[1]);
        Assert.Equal(0UL, Assert.IsType<NumberBoxPresentation>(updates[2]).DefaultValue);
        Assert.True(Assert.IsType<CheckBoxPresentation>(updates[3]).DefaultChecked);
        var server = adml.Presentations["Serwer"].Controls;
        Assert.Equal(("Update server:", "https://"), (server[0].Label, ((TextBoxPresentation)server[0]).DefaultValue));
        Assert.Equal(["https://wsus.contoso.test"], Assert.IsType<ComboBoxPresentation>(server[1]).Suggestions);
        var message = adml.Presentations["Komunikat"].Controls;
        Assert.Equal(4, message.Count); // the unknown control is skipped
        Assert.Equal(4, Assert.IsType<MultiTextBoxPresentation>(message[0]).DefaultHeight);
        Assert.False(Assert.IsType<NumberBoxPresentation>(message[3]).Spin);
    }

    [Fact]
    public async Task Load_ResolvesCategoriesAcrossFilesAndTextsInPolish()
    {
        var (set, exists) = await PolicyDefinitionLoader.LoadAsync(SampleAdministrativeTemplates.Source(), ["pl-PL", "en-US"], TestContext.Current.CancellationToken);

        Assert.True(exists);
        Assert.Empty(set.Issues);
        Assert.Equal(2, set.FileCount);
        Assert.Equal(["Składniki systemu Windows", "System"], set.Roots.Select(r => r.DisplayName));
        var updates = set.Find(SampleAdministrativeTemplates.Updates)!;
        Assert.Equal("Konfiguruj aktualizacje automatyczne", updates.DisplayName);
        Assert.Equal(["Składniki systemu Windows", "Aktualizacje Contoso"], updates.Category!.PathNames());
        Assert.Equal("Co najmniej Windows 10", updates.SupportedOnText);
        Assert.Contains("Dotyczy stacji roboczych", updates.ExplainText, StringComparison.Ordinal);
        Assert.Equal("Konfigurowanie aktualizacji automatycznych:", updates.Presentation!.Controls[0].Label);
        var choice = (EnumElement)updates.Policy.Elements[0];
        Assert.Equal("3 – Pobierz automatycznie i powiadom", updates.Text(choice.Items[1].DisplayName));
    }

    [Fact]
    public async Task Load_FallsBackToEnglishPerFile()
    {
        var files = SampleAdministrativeTemplates.Files();
        files.Remove("pl-PL/contoso.adml");

        var (set, _) = await PolicyDefinitionLoader.LoadAsync(new InMemoryPolicyDefinitionSource("x", files), ["pl-PL", "en-US"], TestContext.Current.CancellationToken);

        Assert.Equal("Configure automatic updates", set.Find(SampleAdministrativeTemplates.Updates)!.DisplayName);
        Assert.Equal("Składniki systemu Windows", set.Roots[0].DisplayName);
        Assert.Empty(set.Issues);
    }

    [Fact]
    public async Task Load_WithoutAnyAdml_ShowsIdsAndSaysSo()
    {
        var files = SampleAdministrativeTemplates.Files(polish: false);
        files.Remove("en-US/contoso.adml");

        var (set, _) = await PolicyDefinitionLoader.LoadAsync(new InMemoryPolicyDefinitionSource("x", files), ["pl-PL", "en-US"], TestContext.Current.CancellationToken);

        Assert.Equal("Konfiguracja", set.Find(SampleAdministrativeTemplates.Updates)!.DisplayName);
        Assert.Contains(set.Issues, i => i.Kind == PolicyDefinitionIssueKind.LanguageMissing && i.FileName == "contoso.admx");
    }

    [Fact]
    public async Task Load_ReportsBrokenFilesCollisionsAndOutdatedLanguages_KeepsTheRest()
    {
        var files = SampleAdministrativeTemplates.Files();
        files["zepsuty.admx"] = Encoding.UTF8.GetBytes("<policyDefinitions><nie");
        files["zzz-kopia.admx"] = files["contoso.admx"];
        files["pl-PL/windows.adml"] = SampleAdministrativeTemplates.Utf8(SampleAdministrativeTemplates.BaseAdmlPl.Replace("revision=\"1.0\"", "revision=\"0.9\"", StringComparison.Ordinal));

        var (set, _) = await PolicyDefinitionLoader.LoadAsync(new InMemoryPolicyDefinitionSource("x", files), ["pl-PL"], TestContext.Current.CancellationToken);

        Assert.Contains(set.Issues, i => i.Kind == PolicyDefinitionIssueKind.FileUnreadable && i.FileName == "zepsuty.admx");
        Assert.Contains(set.Issues, i => i.Kind == PolicyDefinitionIssueKind.NamespaceCollision && i.FileName == "zzz-kopia.admx");
        Assert.Contains(set.Issues, i => i.Kind == PolicyDefinitionIssueKind.LanguageOutdated && i.FileName == "pl-PL/windows.adml");
        Assert.Equal(7, set.PolicyCount);
    }

    [Fact]
    public async Task Load_WithoutTheBaseFile_PutsCategoriesAtTheRootAndSaysSo()
    {
        var files = SampleAdministrativeTemplates.Files();
        files.Remove("windows.admx");

        var (set, _) = await PolicyDefinitionLoader.LoadAsync(new InMemoryPolicyDefinitionSource("x", files), ["pl-PL"], TestContext.Current.CancellationToken);

        Assert.Contains(set.Roots, r => r.DisplayName == "Aktualizacje Contoso");
        Assert.Contains(set.Issues, i => i.Kind == PolicyDefinitionIssueKind.UnknownCategory && i.Detail == "windows:WindowsComponents");
        Assert.Equal(["Tryb pracy", "Wyłącz okienko podglądu"], set.Uncategorized.Select(p => p.DisplayName));
        Assert.Null(set.Find(SampleAdministrativeTemplates.Updates)!.SupportedOnText);
    }

    [Fact]
    public async Task Load_OfAMissingStore_IsEmpty()
    {
        var (set, exists) = await PolicyDefinitionLoader.LoadAsync(new MissingSource(), ["pl-PL"], TestContext.Current.CancellationToken);

        Assert.False(exists);
        Assert.Same(PolicyDefinitionSet.Empty, set);
    }

    [Fact]
    public void Build_CutsACategoryLoop()
    {
        var file = AdmxParser.Parse("p.admx", Encoding.UTF8.GetBytes("""
            <policyDefinitions><policyNamespaces><target prefix="p" namespace="P" /></policyNamespaces>
              <categories>
                <category name="A" displayName="A"><parentCategory ref="B" /></category>
                <category name="B" displayName="B"><parentCategory ref="A" /></category>
              </categories>
            </policyDefinitions>
            """));

        var set = PolicyDefinitionSet.Build([(file, null)], [], CultureInfo.InvariantCulture);

        Assert.Single(set.Roots);
        Assert.Single(set.Roots[0].Children);
        Assert.Contains(set.Issues, i => i.Kind == PolicyDefinitionIssueKind.CategoryLoop);
    }

    [Fact]
    public void Build_SortsInTheCultureOfTheTexts()
    {
        var file = AdmxParser.Parse("p.admx", Encoding.UTF8.GetBytes("""
            <policyDefinitions><policyNamespaces><target prefix="p" namespace="P" /></policyNamespaces>
              <categories>
                <category name="A" displayName="Łączność" />
                <category name="B" displayName="Logowanie" />
                <category name="C" displayName="Zasady" />
                <category name="D" displayName="Mapy" />
              </categories>
            </policyDefinitions>
            """));

        var set = PolicyDefinitionSet.Build([(file, null)], [], CultureInfo.GetCultureInfo("pl-PL"));

        Assert.Equal(["Logowanie", "Łączność", "Mapy", "Zasady"], set.Roots.Select(r => r.DisplayName));
    }

    [Fact]
    public void Categories_KnowWhichScopeTheyHold()
    {
        var set = SampleAdministrativeTemplates.LoadPolish();
        var desktop = set.Find(SampleAdministrativeTemplates.DriveMappings)!.Category!;

        Assert.True(desktop.HasPolicies(PolicyClass.User));
        Assert.True(desktop.HasPolicies(PolicyClass.Machine)); // the "Both" policy
        Assert.Equal(3, set.PoliciesOf(PolicyClass.User).Count());
        Assert.Equal(5, set.PoliciesOf(PolicyClass.Machine).Count());
    }

    [Fact]
    public void CulturesFor_AddsEnglish()
    {
        Assert.Equal(["pl-PL", "en-US"], PolicyDefinitionLoader.CulturesFor(CultureInfo.GetCultureInfo("pl-PL")));
        Assert.Equal(["en-US"], PolicyDefinitionLoader.CulturesFor(CultureInfo.GetCultureInfo("en-US")));
    }

    [Theory]
    [InlineData("1.0", "1.2", true)]
    [InlineData("1.2", "1.2", false)]
    [InlineData("2", "1.10", false)]
    [InlineData(null, "1.0", false)]
    [InlineData("abc", "1.0", false)]
    public void IsOlder_ComparesRevisions(string? revision, string? required, bool older) =>
        Assert.Equal(older, PolicyDefinitionLoader.IsOlder(revision, required));

    private sealed class MissingSource : IPolicyDefinitionSource
    {
        public string Description => "brak";

        public Task<IReadOnlyList<PolicyDefinitionFile>?> ListAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<PolicyDefinitionFile>?>(null);

        public Task<byte[]?> ReadAsync(string relativePath, long maxBytes, CancellationToken cancellationToken) => Task.FromResult<byte[]?>(null);
    }
}
