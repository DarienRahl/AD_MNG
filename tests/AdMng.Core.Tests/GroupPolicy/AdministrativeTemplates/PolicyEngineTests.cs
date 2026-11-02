using System.Text;
using AdMng.Core.GroupPolicy.AdministrativeTemplates;
using AdMng.Core.GroupPolicy.Content;
using AdMng.Testing;
using static AdMng.Core.Tests.GroupPolicy.AdministrativeTemplates.RegistryPolicyDocumentTests;

namespace AdMng.Core.Tests.GroupPolicy.AdministrativeTemplates;

public class PolicyEngineTests
{
    private const string Au = SampleAdministrativeTemplates.UpdatesKey;

    private static readonly PolicyDefinitionSet Set = SampleAdministrativeTemplates.LoadPolish();

    private static AdmxPolicy Policy(string id) => Set.Find(id)!.Policy;

    private static Dictionary<string, PolicyElementValue> Values(params (string Id, PolicyElementValue Value)[] values) =>
        values.ToDictionary(v => v.Id, v => v.Value, StringComparer.OrdinalIgnoreCase);

    [Fact]
    public void Enabled_WritesThePolicyValueAndEveryElement_AndReadsBack()
    {
        var policy = Policy(SampleAdministrativeTemplates.Updates);
        var document = new RegistryPolicyDocument();
        var setting = PolicySetting.Enabled(Values(("Tryb", new EnumElementValue(2)), ("Dzien", new NumberElementValue(3)), ("InneProdukty", new BooleanElementValue(true))));

        PolicyEngine.Write(policy, setting, document);

        Assert.Equal(["NoAutoUpdate=0", "AUOptions=4", "ScheduledInstallDay=3", "AllowMUUpdateService=1"], Describe(document));
        Assert.All(document.Entries, e => Assert.Equal(Au, e.Key));
        Assert.Equal(setting, PolicyEngine.Read(policy, document.View));
    }

    [Fact]
    public void Disabled_WritesTheDisabledValueAndDeletesTheElements()
    {
        var policy = Policy(SampleAdministrativeTemplates.Updates);
        var document = new RegistryPolicyDocument();
        PolicyEngine.Write(policy, PolicySetting.Enabled(Values(("Tryb", new EnumElementValue(0)), ("Dzien", new NumberElementValue(0)))), document);

        PolicyEngine.Write(policy, PolicySetting.Disabled, document);

        // The values keep their places; the elements become deletions (what the Microsoft editor leaves for a disabled policy).
        Assert.Equal(["NoAutoUpdate=1", "**del.AUOptions", "**del.ScheduledInstallDay", "**del.AllowMUUpdateService"], Describe(document));
        Assert.Equal(PolicySetting.Disabled, PolicyEngine.Read(policy, document.View));
    }

    [Fact]
    public void NotConfigured_RemovesEverythingOfThePolicy_AndNothingElse()
    {
        var policy = Policy(SampleAdministrativeTemplates.Updates);
        var document = new RegistryPolicyDocument([Dword(Au, "Obcy", 7)]);
        PolicyEngine.Write(policy, PolicySetting.Disabled, document);

        PolicyEngine.Write(policy, PolicySetting.NotConfigured, document);

        Assert.Equal(["Obcy=7"], Describe(document));
        Assert.Equal(PolicySetting.NotConfigured, PolicyEngine.Read(policy, document.View));
    }

    [Fact]
    public void ACheckBoxWithADeleteFalseValue_IsDeletedWhenCleared()
    {
        var policy = Policy(SampleAdministrativeTemplates.Updates);
        var document = new RegistryPolicyDocument();

        PolicyEngine.Write(policy, PolicySetting.Enabled(Values(("Tryb", new EnumElementValue(1)), ("Dzien", new NumberElementValue(0)), ("InneProdukty", new BooleanElementValue(false)))), document);

        Assert.Equal(["NoAutoUpdate=0", "AUOptions=3", "ScheduledInstallDay=0", "**del.AllowMUUpdateService"], Describe(document));
        var read = PolicyEngine.Read(policy, document.View);
        Assert.Equal(PolicyState.Enabled, read.State);
        Assert.Equal(new BooleanElementValue(false), read.ValueOf("InneProdukty"));
    }

    [Fact]
    public void APolicyWithOnlyAValueName_WritesOneAndDeletesWhenDisabled()
    {
        var policy = Policy(SampleAdministrativeTemplates.HidePreview);
        var document = new RegistryPolicyDocument();

        PolicyEngine.Write(policy, PolicySetting.Enabled(Values()), document);
        Assert.Equal(["NoPreview=1"], Describe(document));
        Assert.Equal(PolicyState.Enabled, PolicyEngine.Read(policy, document.View).State);

        PolicyEngine.Write(policy, PolicySetting.Disabled, document);
        Assert.Equal(["**del.NoPreview"], Describe(document));
        Assert.Equal(PolicyState.Disabled, PolicyEngine.Read(policy, document.View).State);
    }

    [Fact]
    public void AnEnabledList_IsWrittenToItsDefaultKey_AndDeletedWhenDisabled()
    {
        var policy = Policy(SampleAdministrativeTemplates.UpdateServer);
        var document = new RegistryPolicyDocument();

        PolicyEngine.Write(policy, PolicySetting.Enabled(Values(("Adres", new TextElementValue("https://wsus.contoso.test:8531")), ("Statystyki", new TextElementValue("")))), document);

        // The new key of the elements sorts before its subkey AU, where the enabled list went.
        Assert.Equal([(@"Software\Policies\Contoso\Aktualizacje", "WUServer"), (@"Software\Policies\Contoso\Aktualizacje", "WUStatusServer"), (Au, "UseWUServer")],
            document.Entries.Select(e => (e.Key, e.ValueName)));
        Assert.Equal("https://wsus.contoso.test:8531", RegistryPolFile.ReadString(document.Entries.Single(e => e.ValueName == "WUServer").Data));
        var read = PolicyEngine.Read(policy, document.View);
        Assert.Equal(PolicyState.Enabled, read.State);
        Assert.Equal(new TextElementValue(""), read.ValueOf("Statystyki"));

        PolicyEngine.Write(policy, PolicySetting.Disabled, document);
        Assert.Equal(["**del.WUServer", "**del.WUStatusServer", "**del.UseWUServer"], Describe(document));
        Assert.Equal(PolicyState.Disabled, PolicyEngine.Read(policy, document.View).State);
    }

    [Fact]
    public void AListWithAnEmptyPrefix_NamesValuesOneTwoThree_AfterDelvals()
    {
        var policy = Policy(SampleAdministrativeTemplates.TrustedSites);
        const string Key = @"Software\Policies\Contoso\Przegladarka\ZaufaneWitryny";
        var document = new RegistryPolicyDocument([Dword(Key, "stary", 1)]);

        PolicyEngine.Write(policy, PolicySetting.Enabled(Values(("Lista", new ListElementValue([new("", "https://intranet.contoso.test"), new("", "https://łódź.contoso.test")])))), document);

        Assert.Equal(["**delvals.", "1", "2"], document.Entries.Select(e => e.ValueName));
        var read = PolicyEngine.Read(policy, document.View);
        Assert.Equal(PolicyState.Enabled, read.State);
        Assert.Equal(["https://intranet.contoso.test", "https://łódź.contoso.test"], ((ListElementValue)read.ValueOf("Lista")!).Items.Select(i => i.Value));

        PolicyEngine.Write(policy, PolicySetting.Disabled, document);
        Assert.Equal(["**delvals."], document.Entries.Select(e => e.ValueName));
        Assert.Equal(PolicyState.Disabled, PolicyEngine.Read(policy, document.View).State);
    }

    [Fact]
    public void AnExplicitList_UsesTheOperatorsNames_AsExpandableStrings()
    {
        var policy = Policy(SampleAdministrativeTemplates.DriveMappings);
        var document = new RegistryPolicyDocument();
        var setting = PolicySetting.Enabled(Values(("Mapy", new ListElementValue([new("P", @"\\pliki\Wydział Prawa"), new("Z", @"%HOMESHARE%\Dokumenty")]))));

        PolicyEngine.Write(policy, setting, document);

        Assert.Equal(["**delvals.", "P", "Z"], document.Entries.Select(e => e.ValueName));
        Assert.All(document.Entries.Skip(1), e => Assert.Equal(RegistryFormat.RegExpandSz, e.Type));
        Assert.Equal(setting, PolicyEngine.Read(policy, document.View));
    }

    [Fact]
    public void TextsLinesAndNumbers_UseTheirTypes_SoftAsSoft()
    {
        var policy = Policy(SampleAdministrativeTemplates.Message);
        var document = new RegistryPolicyDocument();
        var setting = PolicySetting.Enabled(Values(
            ("Tresc", new MultiTextElementValue(["Dzień dobry", "Zasady: zażółć gęślą jaźń"])),
            ("Sciezka", new TextElementValue(@"%TEMP%\komunikat.log")),
            ("Rozmiar", new NumberElementValue(5_000_000_000)),
            ("Opoznienie", new NumberElementValue(45))));

        PolicyEngine.Write(policy, setting, document);

        Assert.Equal([("Lines", RegistryFormat.RegMultiSz), ("Path", RegistryFormat.RegExpandSz), ("MaxSize", RegistryFormat.RegQword), ("**soft.Delay", RegistryFormat.RegSz)],
            document.Entries.Select(e => (e.ValueName, e.Type)));
        Assert.Equal("45", RegistryPolFile.ReadString(document.Entries[3].Data));
        Assert.Equal(setting, PolicyEngine.Read(policy, document.View));
    }

    [Fact]
    public void CheckBoxListsAndEnumValueLists_SwitchTogether()
    {
        var policy = Policy(SampleAdministrativeTemplates.WorkMode);
        var document = new RegistryPolicyDocument();

        PolicyEngine.Write(policy, PolicySetting.Enabled(Values(("Oszczedny", new BooleanElementValue(true)), ("Profil", new EnumElementValue(1)))), document);
        Assert.Equal(["Enabled=1", "Saver=1", "Dim=1", "Profile", "Volume=80"], Describe(document));
        Assert.Equal(new EnumElementValue(1), PolicyEngine.Read(policy, document.View).ValueOf("Profil"));

        PolicyEngine.Write(policy, PolicySetting.Enabled(Values(("Oszczedny", new BooleanElementValue(false)), ("Profil", new EnumElementValue(0)))), document);
        Assert.Equal(["Enabled=1", "Saver=0", "Dim=0", "Profile", "Volume=20"], Describe(document));
        var read = PolicyEngine.Read(policy, document.View);
        Assert.Equal(new BooleanElementValue(false), read.ValueOf("Oszczedny"));
        Assert.Equal(new EnumElementValue(0), read.ValueOf("Profil"));

        PolicyEngine.Write(policy, PolicySetting.NotConfigured, document);
        Assert.Empty(document.Entries);
    }

    [Fact]
    public void ValuesMatchingNeitherState_AreInconsistent()
    {
        var policy = Policy(SampleAdministrativeTemplates.Updates);
        var view = new RegistryPolicyDocument([Dword(Au, "NoAutoUpdate", 5)]).View;

        Assert.Equal(PolicyState.Inconsistent, PolicyEngine.Read(policy, view).State);
        Assert.Throws<ArgumentException>(() => PolicyEngine.Write(policy, new PolicySetting(PolicyState.Inconsistent, Values()), new RegistryPolicyDocument()));
    }

    [Fact]
    public void Read_OfAFileWrittenByAnotherTool_WithDuplicatesAndTrailingSeparators()
    {
        var policy = Policy(SampleAdministrativeTemplates.Updates);
        var view = new RegistryPolicyDocument([Dword(Au + "\\", "NoAutoUpdate", 1), Dword(Au.ToLowerInvariant(), "noautoupdate", 0), Dword(Au, "AUOptions", 2)]).View;

        var read = PolicyEngine.Read(policy, view);

        Assert.Equal(PolicyState.Enabled, read.State);
        Assert.Equal(new EnumElementValue(0), read.ValueOf("Tryb"));
    }

    [Fact]
    public void Validate_ReportsEveryKindOfProblem()
    {
        var updates = Policy(SampleAdministrativeTemplates.Updates);
        var server = Policy(SampleAdministrativeTemplates.UpdateServer);
        var message = Policy(SampleAdministrativeTemplates.Message);
        var drives = Policy(SampleAdministrativeTemplates.DriveMappings);
        var sites = Policy(SampleAdministrativeTemplates.TrustedSites);

        Assert.Equal([PolicyValueProblemKind.Required, PolicyValueProblemKind.OutOfRange],
            PolicyEngine.Validate(updates, PolicySetting.Enabled(Values(("Dzien", new NumberElementValue(8))))).Select(p => p.Kind));
        Assert.Equal(["0", "7"], PolicyEngine.Validate(updates, PolicySetting.Enabled(Values(("Tryb", new EnumElementValue(0)), ("Dzien", new NumberElementValue(9))))).Single().Arguments);
        Assert.Equal(PolicyValueProblemKind.UnknownItem, PolicyEngine.Validate(updates, PolicySetting.Enabled(Values(("Tryb", new EnumElementValue(3)), ("Dzien", new NumberElementValue(1))))).Single().Kind);
        Assert.Equal(PolicyValueProblemKind.Required, PolicyEngine.Validate(server, PolicySetting.Enabled(Values(("Adres", new TextElementValue(""))))).Single().Kind);
        Assert.Equal(PolicyValueProblemKind.TooLong, PolicyEngine.Validate(server, PolicySetting.Enabled(Values(("Adres", new TextElementValue(new string('a', 201)))))).Single().Kind);
        Assert.Equal(PolicyValueProblemKind.TooManyLines,
            PolicyEngine.Validate(message, PolicySetting.Enabled(Values(("Tresc", new MultiTextElementValue(["1", "2", "3", "4"]))))).Single().Kind);
        Assert.Equal(PolicyValueProblemKind.DuplicateListEntry,
            PolicyEngine.Validate(drives, PolicySetting.Enabled(Values(("Mapy", new ListElementValue([new("P", "a"), new("p", "b")]))))).Single().Kind);
        Assert.Equal(PolicyValueProblemKind.EmptyListEntry,
            PolicyEngine.Validate(drives, PolicySetting.Enabled(Values(("Mapy", new ListElementValue([new("", "a")]))))).Single().Kind);
        Assert.Empty(PolicyEngine.Validate(sites, PolicySetting.Enabled(Values(("Lista", new ListElementValue([new("", "a"), new("", "a")]))))));
        Assert.Empty(PolicyEngine.Validate(updates, PolicySetting.Disabled));
    }

    [Fact]
    public void DefaultValue_ComesFromThePresentation()
    {
        var definition = Set.Find(SampleAdministrativeTemplates.Updates)!;
        var controls = definition.Presentation!.Controls;

        Assert.Equal(new EnumElementValue(1), PolicyEngine.DefaultValue(definition.Policy.Elements[0], controls[0]));
        Assert.Equal(new NumberElementValue(0), PolicyEngine.DefaultValue(definition.Policy.Elements[1], controls[2]));
        Assert.Equal(new BooleanElementValue(true), PolicyEngine.DefaultValue(definition.Policy.Elements[2], controls[3]));
        Assert.Equal(new TextElementValue("https://"), PolicyEngine.DefaultValue(Policy(SampleAdministrativeTemplates.UpdateServer).Elements[1],
            Set.Find(SampleAdministrativeTemplates.UpdateServer)!.Presentation!.Controls[1]));
    }

    [Fact]
    public void OwnValueNames_CoverValuesListsAndElements()
    {
        var names = PolicyEngine.OwnValueNames(Policy(SampleAdministrativeTemplates.WorkMode));

        Assert.Equal(["Dim", "Enabled", "Profile", "Saver", "Volume"], names[@"Software\Policies\Contoso\Tryb"].Order(StringComparer.Ordinal));
        Assert.Equal([@"Software\Policies\Contoso\Przegladarka\ZaufaneWitryny"], PolicyEngine.ListKeys(Policy(SampleAdministrativeTemplates.TrustedSites)));
    }

    [Fact]
    public void AnEnabledListOnlyPolicy_IsDisabledByItsDeletions()
    {
        var file = AdmxParser.Parse("x.admx", Encoding.UTF8.GetBytes("""
            <policyDefinitions><policyNamespaces><target prefix="x" namespace="X" /></policyNamespaces><policies>
              <policy name="P" class="Machine" displayName="P" key="Software\X">
                <enabledList><item valueName="A"><value><decimal value="1" /></value></item><item valueName="B"><value><string>tak</string></value></item></enabledList>
              </policy>
            </policies></policyDefinitions>
            """));
        var policy = file.Policies.Single();
        var document = new RegistryPolicyDocument();

        PolicyEngine.Write(policy, PolicySetting.Disabled, document);

        Assert.Equal(["**del.A", "**del.B"], Describe(document));
        Assert.Equal(PolicyState.Disabled, PolicyEngine.Read(policy, document.View).State);
        PolicyEngine.Write(policy, PolicySetting.Enabled(Values()), document);
        Assert.Equal(PolicyState.Enabled, PolicyEngine.Read(policy, document.View).State);
    }
}
