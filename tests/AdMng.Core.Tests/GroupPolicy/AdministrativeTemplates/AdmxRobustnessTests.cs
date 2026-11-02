using System.Text;
using AdMng.Core.GroupPolicy;
using AdMng.Core.GroupPolicy.AdministrativeTemplates;

namespace AdMng.Core.Tests.GroupPolicy.AdministrativeTemplates;

/// <summary>
/// Templates as vendors and attackers write them: schema defaults the files leave out, a repeated element id (BIS-F),
/// control characters in names, categories chained deeper than any real store.
/// </summary>
public class AdmxRobustnessTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Admx(string categories, string policies, string usingNamespace = "") => $$"""
        <?xml version="1.0" encoding="utf-8"?>
        <policyDefinitions revision="1.0" schemaVersion="1.0" xmlns="http://schemas.microsoft.com/GroupPolicy/2006/07/PolicyDefinitions">
          <policyNamespaces>
            <target prefix="test" namespace="Contoso.Policies.Odpornosc" />
            {{usingNamespace}}
          </policyNamespaces>
          <resources minRequiredRevision="1.0" />
          <categories>{{categories}}</categories>
          <policies>{{policies}}</policies>
        </policyDefinitions>
        """;

    private const string Adml = """
        <?xml version="1.0" encoding="utf-8"?>
        <policyDefinitionResources revision="1.0" schemaVersion="1.0" xmlns="http://schemas.microsoft.com/GroupPolicy/2006/07/PolicyDefinitions">
          <displayName>Odporność</displayName>
          <description>Szablon testowy</description>
          <resources>
            <stringTable>
              <string id="Uspienie">Limit czasu uśpienia (zasilanie sieciowe)</string>
            </stringTable>
            <presentationTable>
              <presentation id="Uspienie">
                <decimalTextBox refId="Sekundy">Sekundy:</decimalTextBox>
              </presentation>
            </presentationTable>
          </resources>
        </policyDefinitionResources>
        """;

    [Fact]
    public async Task ANumberBoxWithoutADefaultValue_StartsAtTheSchemaDefaultOne()
    {
        var admx = Admx("""<category name="Zasilanie" displayName="Zasilanie" />""", """
            <policy name="Uspienie" class="Machine" displayName="$(string.Uspienie)" key="Software\Policies\Contoso\Zasilanie" presentation="$(presentation.Uspienie)">
              <parentCategory ref="Zasilanie" />
              <elements><decimal id="Sekundy" valueName="ACSettingIndex" required="true" maxValue="4294967295" /></elements>
            </policy>
            """);
        var (set, _) = await PolicyDefinitionLoader.LoadAsync(new InMemoryPolicyDefinitionSource("test", new Dictionary<string, byte[]>
        {
            ["odpornosc.admx"] = Encoding.UTF8.GetBytes(admx),
            ["pl-PL/odpornosc.adml"] = Encoding.UTF8.GetBytes(Adml),
        }), ["pl-PL"], Ct);

        var definition = Assert.Single(set.Policies);
        var element = Assert.Single(definition.Policy.Elements);
        var control = Assert.IsType<NumberBoxPresentation>(definition.Presentation!.Controls.Single());
        Assert.Equal(1ul, control.DefaultValue);
        Assert.Equal(new NumberElementValue(1), PolicyEngine.DefaultValue(element, control));
    }

    [Fact]
    public void ARepeatedElementId_KeepsTheFirstDefinition_AndTheFileLoads()
    {
        var admx = Admx("""<category name="Obraz" displayName="Obraz" />""", """
            <policy name="Eksport" class="Machine" displayName="Eksport" key="Software\Policies\Contoso\Obraz">
              <parentCategory ref="Obraz" />
              <elements>
                <text id="Sciezka" valueName="SciezkaEksportu" />
                <text id="Sciezka" valueName="SciezkaEksportu" />
                <text id="sciezka" valueName="Inna" />
              </elements>
            </policy>
            """);

        var file = AdmxParser.Parse("obraz.admx", Encoding.UTF8.GetBytes(admx));

        var element = Assert.Single(Assert.Single(file.Policies).Elements);
        Assert.Equal("SciezkaEksportu", element.ValueName);
    }

    [Theory]
    [InlineData("""key="Software\Policies\Contoso\X&#0;Y" valueName="Wartosc" """)]
    [InlineData("""key="Software\Policies\Contoso\X" valueName="v&#0;w" """)]
    [InlineData("""key="Software\Policies\Contoso\X" valueName="v&#10;w" """)]
    public void ControlCharactersInKeysOrValueNames_MakeTheFileUnreadable(string attributes)
    {
        var admx = Admx("""<category name="Zle" displayName="Zle" />""", $"""
            <policy name="Zla" class="Machine" displayName="Zla" {attributes}>
              <parentCategory ref="Zle" />
            </policy>
            """);

        Assert.Throws<GpoParseException>(() => AdmxParser.Parse("zle.admx", Encoding.UTF8.GetBytes(admx)));
    }

    [Fact]
    public void ANulInAStringValue_MakesTheFileUnreadable()
    {
        var admx = Admx("""<category name="Zle" displayName="Zle" />""", """
            <policy name="Zla" class="Machine" displayName="Zla" key="Software\Policies\Contoso\X" valueName="Tryb">
              <parentCategory ref="Zle" />
              <enabledValue><string>a&#0;b</string></enabledValue>
            </policy>
            """);

        Assert.Throws<GpoParseException>(() => AdmxParser.Parse("zle.admx", Encoding.UTF8.GetBytes(admx)));
    }

    [Fact]
    public async Task CategoriesChainedThousandsDeep_AreCutBelowTheMaximumDepth_WithoutOverflowingTheStack()
    {
        // 4000 categories, each the parent of the next, listed child first (each link sees a parent without ancestors).
        const int Depth = 4_000;
        var categories = new StringBuilder();
        for (var i = Depth - 1; i >= 0; i--)
        {
            categories.Append(i == 0
                ? """<category name="K0" displayName="K0" />"""
                : $"""<category name="K{i}" displayName="K{i}"><parentCategory ref="K{i - 1}" /></category>""");
        }

        var admx = Admx(categories.ToString(), $"""
            <policy name="Najglebsza" class="Machine" displayName="Najgłębsza" key="Software\Policies\Contoso\Glebia" valueName="Wlaczone">
              <parentCategory ref="K{Depth - 1}" />
            </policy>
            """);
        var (set, _) = await PolicyDefinitionLoader.LoadAsync(new InMemoryPolicyDefinitionSource("test", new Dictionary<string, byte[]>
        {
            ["glebia.admx"] = Encoding.UTF8.GetBytes(admx),
        }), ["pl-PL"], Ct);

        Assert.All(set.Roots, root => Assert.True(MaxDepth(root) <= PolicyDefinitionSet.MaxCategoryDepth));
        Assert.Contains(set.Issues, i => i.Kind == PolicyDefinitionIssueKind.CategoryTooDeep);
        Assert.Single(set.Roots, r => r.HasPolicies(PolicyClass.Machine));
        Assert.Equal("K0", set.Roots.Single(r => r.Category.Name == "K0").DisplayName);
        var policy = Assert.Single(set.Policies);
        Assert.True(policy.Category!.PathNames().Count <= PolicyDefinitionSet.MaxCategoryDepth);
    }

    [Fact]
    public async Task RealDepths_AreKept()
    {
        var categories = """
            <category name="A" displayName="A" />
            <category name="B" displayName="B"><parentCategory ref="A" /></category>
            <category name="C" displayName="C"><parentCategory ref="B" /></category>
            """;
        var admx = Admx(categories, """<policy name="P" class="User" displayName="P" key="Software\Policies\Contoso\P" valueName="V"><parentCategory ref="C" /></policy>""");
        var (set, _) = await PolicyDefinitionLoader.LoadAsync(new InMemoryPolicyDefinitionSource("test", new Dictionary<string, byte[]>
        {
            ["plytka.admx"] = Encoding.UTF8.GetBytes(admx),
        }), ["pl-PL"], Ct);

        Assert.DoesNotContain(set.Issues, i => i.Kind == PolicyDefinitionIssueKind.CategoryTooDeep);
        Assert.Equal(["A", "B", "C"], Assert.Single(set.Policies).Category!.PathNames());
    }

    private static int MaxDepth(PolicyCategoryNode root)
    {
        var deepest = 0;
        var stack = new Stack<(PolicyCategoryNode Node, int Depth)>([(root, 1)]);
        while (stack.Count > 0)
        {
            var (node, depth) = stack.Pop();
            deepest = Math.Max(deepest, depth);
            foreach (var child in node.Children)
            {
                stack.Push((child, depth + 1));
            }
        }

        return deepest;
    }
}
