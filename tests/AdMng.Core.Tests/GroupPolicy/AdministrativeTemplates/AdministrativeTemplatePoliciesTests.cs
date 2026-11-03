using AdMng.Core.GroupPolicy;
using AdMng.Core.GroupPolicy.AdministrativeTemplates;
using AdMng.Core.GroupPolicy.Content;
using AdMng.Core.GroupPolicy.Management;
using AdMng.Core.GroupPolicy.Modeling;
using AdMng.Testing;

namespace AdMng.Core.Tests.GroupPolicy.AdministrativeTemplates;

/// <summary>
/// <see cref="AdministrativeTemplatePolicies.Configured"/>: the policies a Registry.pol configures, the entries each owns, what
/// stays uncovered – and that the K5 modeller, which uses it, sees exactly the same.
/// </summary>
public sealed class AdministrativeTemplatePoliciesTests
{
    private const string Domain = "DC=contoso,DC=test";
    private const string Stacje = "OU=Stacje robocze,OU=Firma," + Domain;
    private const string Pracownicy = "OU=Pracownicy,OU=Firma," + Domain;
    private const string Au = SampleAdministrativeTemplates.UpdatesKey;
    private const string Sites = @"Software\Policies\Contoso\Przegladarka\ZaufaneWitryny";

    private static readonly Guid Policy = new("C0000000-0000-0000-0000-000000000001");
    private static readonly PolicyDefinitionSet Set = SampleAdministrativeTemplates.LoadPolish();

    [Fact]
    public void ConfiguredPolicies_TheirEntries_AndTheUncoveredRest()
    {
        var document = MachineDocument();

        var result = AdministrativeTemplatePolicies.Configured(document, Set, PolicyClass.Machine);

        Assert.Equal(
            new[] { SampleAdministrativeTemplates.Updates, SampleAdministrativeTemplates.UpdateServer, SampleAdministrativeTemplates.TrustedSites, SampleAdministrativeTemplates.HidePreview }
                .Order(StringComparer.Ordinal),
            result.Policies.Select(p => p.Definition.Id).Order(StringComparer.Ordinal));
        var updates = Configured(result, SampleAdministrativeTemplates.Updates);
        Assert.Equal(PolicyState.Enabled, updates.Setting.State);
        Assert.Equal(new EnumElementValue(1), updates.Setting.ValueOf("Tryb"));
        Assert.Equal(["NoAutoUpdate", "AUOptions", "ScheduledInstallDay", "AllowMUUpdateService"], updates.Entries.Select(e => e.ValueName));
        // The server policy owns its own values and its enabled list's value in the updates key.
        Assert.Equal(["UseWUServer", "WUServer", "WUStatusServer"], Configured(result, SampleAdministrativeTemplates.UpdateServer).Entries.Select(e => e.ValueName).Order(StringComparer.Ordinal));
        // A list owns every value of its key, a '!' in a name included.
        Assert.Equal(["**delvals.", "https://intranet.contoso.test/#!/start", "https://łódź.contoso.test"],
            Configured(result, SampleAdministrativeTemplates.TrustedSites).Entries.Select(e => e.ValueName));
        Assert.Equal(PolicyState.Enabled, Configured(result, SampleAdministrativeTemplates.TrustedSites).Setting.State);
        Assert.Equal(PolicyState.Disabled, Configured(result, SampleAdministrativeTemplates.HidePreview).Setting.State);
        Assert.All(result.Policies, p => Assert.Equal(document.Entries.Where(p.Entries.Contains), p.Entries)); // file order

        // A value only a user policy describes, and a value no template knows, are extra registry values.
        Assert.Equal([(@"Software\Firma\Inne", "Wartość"), (@"Software\Policies\Contoso\Komunikat", "Path")],
            result.Uncovered.Select(e => (e.Key, e.ValueName)).OrderBy(e => e.Key, StringComparer.Ordinal));
        Assert.Equal(document.Entries.Where(result.Uncovered.Contains), result.Uncovered); // file order
        Assert.All(document.Entries, e => Assert.Equal(!result.Uncovered.Contains(e), result.Covers(e)));
        Assert.Equal(document.Entries.Count, result.Policies.Sum(p => p.Entries.Count) + result.Uncovered.Count);

        // Logs see no values.
        Assert.Equal("4 policies, 2 uncovered entries", result.ToString());
        Assert.Equal(SampleAdministrativeTemplates.UpdateServer + " Enabled", Configured(result, SampleAdministrativeTemplates.UpdateServer).ToString());
    }

    [Fact]
    public void TheUserClass_SeesOnlyItsPolicies()
    {
        var document = new RegistryPolicyDocument();
        PolicyEngine.Write(Definition(SampleAdministrativeTemplates.Message).Policy,
            PolicySetting.Enabled(new Dictionary<string, PolicyElementValue> { ["Tresc"] = new MultiTextElementValue(["Dzień dobry", "Zażółć gęślą jaźń"]) }), document);
        document.Set(Sites, "https://intranet.contoso.test", RegistryFormat.RegSz, RegistryPolFile.StringData("1"));

        var user = AdministrativeTemplatePolicies.Configured(document, Set, PolicyClass.User);
        var machine = AdministrativeTemplatePolicies.Configured(document, Set, PolicyClass.Machine);

        Assert.Equal([SampleAdministrativeTemplates.Message, SampleAdministrativeTemplates.TrustedSites], user.Policies.Select(p => p.Definition.Id).Order(StringComparer.Ordinal));
        Assert.Empty(user.Uncovered);
        Assert.Equal([SampleAdministrativeTemplates.TrustedSites], machine.Policies.Select(p => p.Definition.Id));
        Assert.All(machine.Uncovered, e => Assert.Equal(@"Software\Policies\Contoso\Komunikat", e.Key));
    }

    [Fact]
    public void NoTemplates_OrNoEntries_CoverNothing()
    {
        var document = MachineDocument();

        var bare = AdministrativeTemplatePolicies.Configured(document, PolicyDefinitionSet.Empty, PolicyClass.Machine);
        var empty = AdministrativeTemplatePolicies.Configured(new RegistryPolicyDocument(), Set, PolicyClass.Machine);

        Assert.Empty(bare.Policies);
        Assert.Equal(document.Entries, bare.Uncovered);
        Assert.All(document.Entries, e => Assert.False(bare.Covers(e)));
        Assert.False(bare.Covers($@"Computer/Registry/HKLM\{Au}!NoAutoUpdate"));
        Assert.Empty(empty.Policies);
        Assert.Empty(empty.Uncovered);
        Assert.Empty(ConfiguredPolicies.None.Policies);
        Assert.False(ConfiguredPolicies.None.Covers($@"Computer/Registry/HKLM\{Au}!NoAutoUpdate"));
    }

    [Fact]
    public void SettingKeys_AreCoveredLikeTheirEntries()
    {
        var document = MachineDocument();
        var result = AdministrativeTemplatePolicies.Configured(document, Set, PolicyClass.Machine);

        var keys = RegistrySettings(document.ToBytes(), GpoScope.Computer).Where(s => result.Covers(s.Key)).Select(s => s.Key).ToList();

        Assert.Contains($@"Computer/Registry/HKLM\{Au}!NoAutoUpdate", keys);
        Assert.Contains($@"Computer/Registry/HKLM\{Sites}!https://intranet.contoso.test/#!/start", keys);
        Assert.Contains($@"Computer/Registry/HKLM\{Sites}!**delvals.", keys);
        Assert.DoesNotContain(@"Computer/Registry/HKLM\Software\Policies\Contoso\Komunikat!Path", keys);
        Assert.DoesNotContain(@"Computer/Registry/HKLM\Software\Firma\Inne!Wartość", keys);
        // Keys without a value and hidden names are never a template's value.
        Assert.False(result.Covers($@"Computer/Registry/HKLM\{Au}"));
        Assert.False(result.Covers($@"Computer/Registry/HKLM\{Au}!*"));
        Assert.True(result.Covers($@"User/Registry/HKCU\{Au}!AUOptions"));
    }

    [Fact]
    public void TheModeller_ShowsTheSamePolicies_AndKeepsTheUncoveredValuesAsSettings()
    {
        var machine = MachineDocument();
        var userDocument = new RegistryPolicyDocument();
        PolicyEngine.Write(Definition(SampleAdministrativeTemplates.DriveMappings).Policy,
            PolicySetting.Enabled(new Dictionary<string, PolicyElementValue> { ["Mapy"] = new ListElementValue([new ListElementItem("Z", @"\\fs01\dane – Łódź")]) }), userDocument);
        userDocument.Set(@"Software\Policies\Contoso\Eksplorator", "NoPreview", RegistryFormat.RegDword, RegistryPolFile.DwordData(1));
        var input = new GroupPolicyModelInput
        {
            DomainDn = Domain,
            Containers = [new GroupPolicySom(Domain, GroupPolicySomKind.Domain, [new GroupPolicyLink($"CN={SysvolPaths.FolderName(Policy)},CN=Policies,CN=System,{Domain}", 0, 1)], false)],
            Gpos = new Dictionary<Guid, ModelingGpo> { [Policy] = Gpo() },
            Computer = Target(Stacje, "-515"),
            User = Target(Pracownicy, "-513"),
            SiteDn = "CN=Kraków,CN=Sites,CN=Configuration," + Domain,
            Definitions = Set,
            Options = new ModelingOptions { Loopback = LoopbackChoice.Off },
        };
        var folder = GroupPolicyModelReader.FromFolder(new GpoFolderContent([], new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["Machine/Registry.pol"] = machine.ToBytes(),
            ["User/Registry.pol"] = userDocument.ToBytes(),
        }));

        var model = GroupPolicyModeler.Build(input, new Dictionary<Guid, ModelingGpoContent> { [Policy] = folder });

        foreach (var (part, document, scope, policyClass) in new[]
        {
            (model.Computer!, machine, GpoScope.Computer, PolicyClass.Machine),
            (model.User!, userDocument, GpoScope.User, PolicyClass.User),
        })
        {
            var configured = AdministrativeTemplatePolicies.Configured(document, Set, policyClass);
            Assert.NotEmpty(configured.Policies);
            Assert.Equal(
                configured.Policies.Select(p => (p.Definition.Id, p.Setting)).OrderBy(p => p.Id, StringComparer.Ordinal),
                part.Policies.Select(p => (p.Definition.Id, p.Winner.Setting)).OrderBy(p => p.Id, StringComparer.Ordinal));
            Assert.Equal(
                RegistrySettings(document.ToBytes(), scope).Where(s => !configured.Covers(s.Key)).Select(s => s.Key).Order(StringComparer.OrdinalIgnoreCase),
                part.Settings.Where(s => s.Key.Contains("/Registry/", StringComparison.Ordinal)).Select(s => s.Key).Order(StringComparer.OrdinalIgnoreCase));
        }

        // The user part: the computer policy's value in HKCU is an extra value there.
        Assert.Contains(model.User!.Settings, s => s.Key == @"User/Registry/HKCU\Software\Policies\Contoso\Eksplorator!NoPreview");
    }

    /// <summary>
    /// The computer part's Registry.pol: the updates policy enabled with its server, the trusted sites list, the preview
    /// policy disabled, a user policy's value and a value no template knows.
    /// </summary>
    private static RegistryPolicyDocument MachineDocument()
    {
        var document = new RegistryPolicyDocument();
        PolicyEngine.Write(Definition(SampleAdministrativeTemplates.Updates).Policy, PolicySetting.Enabled(new Dictionary<string, PolicyElementValue>
        {
            ["Tryb"] = new EnumElementValue(1),
            ["Dzien"] = new NumberElementValue(3),
            ["InneProdukty"] = new BooleanElementValue(true),
        }), document);
        PolicyEngine.Write(Definition(SampleAdministrativeTemplates.UpdateServer).Policy, PolicySetting.Enabled(new Dictionary<string, PolicyElementValue>
        {
            ["Adres"] = new TextElementValue("https://wsus.contoso.test:8531"),
            ["Statystyki"] = new TextElementValue("https://wsus.contoso.test:8531"),
        }), document);
        // The trusted sites list as another tool writes it: the values named by their data, all values cleared first.
        document.Set(Sites, "https://intranet.contoso.test/#!/start", RegistryFormat.RegSz, RegistryPolFile.StringData("1"));
        document.Set(Sites, "https://łódź.contoso.test", RegistryFormat.RegSz, RegistryPolFile.StringData("1"));
        document.ClearValues(Sites);
        PolicyEngine.Write(Definition(SampleAdministrativeTemplates.HidePreview).Policy, PolicySetting.Disabled, document);
        document.Set(@"Software\Policies\Contoso\Komunikat", "Path", RegistryFormat.RegSz, RegistryPolFile.StringData(@"C:\Dane"));
        document.Set(@"Software\Firma\Inne", "Wartość", RegistryFormat.RegDword, RegistryPolFile.DwordData(5));
        return document;
    }

    private static PolicyDefinition Definition(string id) => Set.Find(id)!;

    private static ConfiguredPolicy Configured(ConfiguredPolicies result, string id) => Assert.Single(result.Policies, p => p.Definition.Id == id);

    private static IReadOnlyList<GpoSetting> RegistrySettings(byte[] content, GpoScope scope)
    {
        var sink = new GpoSettingSink(scope == GpoScope.User ? "User/Registry.pol" : "Machine/Registry.pol");
        RegistryPolParser.Parse(content, scope, sink);
        return sink.Settings;
    }

    private static ModelingGpo Gpo() => new()
    {
        Id = Policy,
        DisplayName = "Szablony – Łódź",
        Version = new GpoVersion(2, 2),
        SecurityFilter = new GroupPolicySecurityFilter(
            new HashSet<string>([GroupPolicySecurityFilter.AuthenticatedUsersSid], StringComparer.OrdinalIgnoreCase), new HashSet<string>())
        {
            ReadAllowed = new HashSet<string>([GroupPolicySecurityFilter.AuthenticatedUsersSid], StringComparer.OrdinalIgnoreCase),
        },
    };

    private static ModelingTarget Target(string container, string rid) => new()
    {
        ContainerDn = container,
        AccountDn = "CN=Zofia Wójcik," + container,
        Token = new HashSet<string>(
            [GroupPolicySecurityFilter.EveryoneSid, GroupPolicySecurityFilter.AuthenticatedUsersSid, "S-1-5-21-1000-2000-3000" + rid],
            StringComparer.OrdinalIgnoreCase),
    };
}
