using System.Text;
using AdMng.Core.GroupPolicy;
using AdMng.Core.GroupPolicy.AdministrativeTemplates;
using AdMng.Core.GroupPolicy.Management;
using AdMng.Core.GroupPolicy.Modeling;
using AdMng.Core.Security;
using AdMng.Testing;

namespace AdMng.Core.Tests.GroupPolicy;

/// <summary>
/// Group Policy modelling (K5): the chain of each part with every reason a GPO does not apply, the precedence numbers,
/// MS16-072's read check of the user part, loopback, the winning settings and policies, cumulative scripts, the account
/// policy of a DC, preference items with targeting and the warnings.
/// </summary>
public sealed class GroupPolicyModelerTests
{
    private const string Domain = "DC=contoso,DC=test";
    private const string Firma = "OU=Firma," + Domain;
    private const string Stacje = "OU=Stacje robocze,OU=Firma," + Domain;
    private const string Pracownicy = "OU=Pracownicy,OU=Firma," + Domain;
    private const string Site = "CN=Warszawa-Śródmieście,CN=Sites,CN=Configuration," + Domain;
    private const string DomainSid = "S-1-5-21-1000-2000-3000";
    private const string DomainComputers = DomainSid + "-515";
    private const string DomainUsers = DomainSid + "-513";
    private const string Ksiegowosc = DomainSid + "-1201";
    private const string Laptopy = DomainSid + "-1202";
    private const string Machine = "Machine/";
    private const string GptTmpl = "Machine/Microsoft/Windows NT/SecEdit/GptTmpl.inf";

    private static readonly Guid DomainPolicy = Guid.Parse("31B2F340-016D-11D2-945F-00C04FB984F9");
    private static readonly Guid SitePolicy = new("B0000000-0000-0000-0000-000000000001");
    private static readonly Guid FirmaPolicy = new("B0000000-0000-0000-0000-000000000002");
    private static readonly Guid FirmaEnforced = new("B0000000-0000-0000-0000-000000000003");
    private static readonly Guid StacjePolicy = new("B0000000-0000-0000-0000-000000000004");
    private static readonly Guid DisabledLink = new("B0000000-0000-0000-0000-000000000005");
    private static readonly Guid Missing = new("B0000000-0000-0000-0000-000000000006");
    private static readonly Guid ComputerOff = new("B0000000-0000-0000-0000-000000000007");
    private static readonly Guid EmptyComputer = new("B0000000-0000-0000-0000-000000000008");
    private static readonly Guid OnlyLaptops = new("B0000000-0000-0000-0000-000000000009");
    private static readonly Guid WithWmi = new("B0000000-0000-0000-0000-00000000000A");
    private static readonly Guid PracownicyPolicy = new("B0000000-0000-0000-0000-00000000000B");
    private static readonly Guid Loopback = new("B0000000-0000-0000-0000-00000000000C");

    [Fact]
    public void Chain_ListsTheAppliedGposByPrecedence_AndWhyTheOthersDoNotApply()
    {
        var input = Input(
            [
                Som(Site, GroupPolicySomKind.Site, false, Link(SitePolicy, 0, 1)),
                Som(Domain, GroupPolicySomKind.Domain, false, Link(DisabledLink, 1, 2), Link(DomainPolicy, 0, 1)),
                Som(Firma, GroupPolicySomKind.OrganizationalUnit, false, Link(FirmaPolicy, 0, 2), Link(FirmaEnforced, 2, 1)),
                Som(Stacje, GroupPolicySomKind.OrganizationalUnit, true, Link(StacjePolicy, 0, 2), Link(FirmaPolicy, 0, 1)),
            ],
            [Gpo(SitePolicy, "Lokacja"), Gpo(DomainPolicy, "Default Domain Policy"), Gpo(DisabledLink, "Wyłączone łącze"),
             Gpo(FirmaPolicy, "Firma – wspólne"), Gpo(FirmaEnforced, "Firma – wymuszone"), Gpo(StacjePolicy, "Stacje robocze")],
            computer: Computer(Stacje));

        var part = GroupPolicyModeler.Build(input, Contents()).Computer!;

        // Stacje robocze blocks inheritance: only its own links and the enforced link of Firma apply; the enforced one wins.
        Assert.Equal(["Firma – wymuszone", "Firma – wspólne", "Stacje robocze"], part.Applied.Select(g => g.Name));
        Assert.Equal([1, 2, 3], part.Applied.Select(g => g.Precedence!.Value));
        Assert.Equal(Stacje, part.Applied.Single(g => g.GpoId == FirmaPolicy).Container.Dn);
        Assert.True(part.Applied.First().Link.Enforced);

        var excluded = part.Gpos.Where(g => !g.Applies).ToList();
        Assert.Equal(ModeledGpoOutcome.Blocked, excluded.Single(g => g.GpoId == SitePolicy).Outcome);
        Assert.Equal(Stacje, excluded.Single(g => g.GpoId == SitePolicy).BlockedBy);
        Assert.Equal(ModeledGpoOutcome.Blocked, excluded.Single(g => g.GpoId == DomainPolicy).Outcome);
        Assert.Equal(ModeledGpoOutcome.LinkDisabled, excluded.Single(g => g.GpoId == DisabledLink).Outcome);
        Assert.Equal(ModeledGpoOutcome.Blocked, excluded.Single(g => g.GpoId == FirmaPolicy).Outcome); // its link at Firma
        Assert.All(excluded, g => Assert.Null(g.Precedence));
        Assert.Equal(part.Gpos.Count(g => g.Applies), part.Gpos.TakeWhile(g => g.Applies).Count()); // applied first
    }

    [Fact]
    public void Chain_AGpoLinkedTwice_CountsAtItsWinningLink_TheOtherIsADuplicate()
    {
        var input = Input(
            [Som(Domain, GroupPolicySomKind.Domain, false, Link(FirmaPolicy, 0, 1)), Som(Stacje, GroupPolicySomKind.OrganizationalUnit, false, Link(FirmaPolicy, 0, 1))],
            [Gpo(FirmaPolicy, "Firma – wspólne")],
            computer: Computer(Stacje));

        var part = GroupPolicyModeler.Build(input, Contents()).Computer!;

        var applied = Assert.Single(part.Applied);
        Assert.Equal(Stacje, applied.Container.Dn);
        var duplicate = Assert.Single(part.Gpos, g => g.Outcome == ModeledGpoOutcome.Duplicate);
        Assert.Equal(Domain, duplicate.Container.Dn);
    }

    [Fact]
    public void Outcomes_Missing_DisabledPart_Empty_SecurityFiltering_AndWmiFilters()
    {
        var gpos = new[]
        {
            Gpo(ComputerOff, "Tylko użytkownik", flags: ModelingGpo.ComputerSettingsDisabledFlag),
            Gpo(EmptyComputer, "Puste dla komputera", version: new GpoVersion(3, 0)),
            Gpo(OnlyLaptops, "Laptopy", filter: Filter(apply: [Laptopy], read: [GroupPolicySecurityFilter.AuthenticatedUsersSid, Laptopy])),
            Gpo(WithWmi, "Z filtrem WMI", wmi: "Tylko Windows 11"),
            Gpo(DomainPolicy, "Default Domain Policy"),
        };
        var containers = new[]
        {
            Som(Domain, GroupPolicySomKind.Domain, false,
                Link(Missing, 0, 6), Link(ComputerOff, 0, 5), Link(EmptyComputer, 0, 4), Link(OnlyLaptops, 0, 3), Link(WithWmi, 0, 2), Link(DomainPolicy, 0, 1)),
        };

        var assumedTrue = GroupPolicyModeler.Build(Input(containers, gpos, computer: Computer(Stacje)), Contents()).Computer!;

        Assert.Equal(ModeledGpoOutcome.NotFound, Outcome(assumedTrue, Missing));
        Assert.Equal(SysvolPaths.FolderName(Missing), assumedTrue.Gpos.Single(g => g.GpoId == Missing).Name);
        Assert.Equal(ModeledGpoOutcome.PartDisabled, Outcome(assumedTrue, ComputerOff));
        Assert.Equal(ModeledGpoOutcome.Empty, Outcome(assumedTrue, EmptyComputer));
        Assert.Equal(ModeledGpoOutcome.SecurityDenied, Outcome(assumedTrue, OnlyLaptops));
        Assert.Equal(ModeledGpoOutcome.AppliedIfWmiFilter, Outcome(assumedTrue, WithWmi));
        Assert.Equal("Tylko Windows 11", assumedTrue.Gpos.Single(g => g.GpoId == WithWmi).WmiFilterName);
        Assert.Equal([DomainPolicy, WithWmi], assumedTrue.Applied.Select(g => g.GpoId));

        // A laptop gets the filtered GPO; with the WMI filters assumed false the filtered GPO does not apply.
        var laptop = GroupPolicyModeler.Build(Input(containers, gpos, computer: Computer(Stacje, Laptopy)) with
        {
            Options = new ModelingOptions { WmiFilters = WmiFilterAssumption.AssumeFalse },
        }, Contents()).Computer!;
        Assert.Equal(ModeledGpoOutcome.Applied, Outcome(laptop, OnlyLaptops));
        Assert.Equal(ModeledGpoOutcome.WmiFilterFalse, Outcome(laptop, WithWmi));
        Assert.Equal([DomainPolicy, OnlyLaptops], laptop.Applied.Select(g => g.GpoId));

        // The user part: the computer configuration's switch does not matter there.
        var user = GroupPolicyModeler.Build(Input(containers, gpos, user: User(Pracownicy)), Contents()).User!;
        Assert.Equal(ModeledGpoOutcome.Applied, Outcome(user, ComputerOff));
        Assert.Equal(ModeledGpoOutcome.Applied, Outcome(user, EmptyComputer));
    }

    [Fact]
    public void UserPart_TheComputerMustReadTheGpo_SinceMs16072()
    {
        var gpos = new[]
        {
            Gpo(FirmaPolicy, "Księgowość – stary filtr", filter: Filter(apply: [Ksiegowosc], read: [Ksiegowosc])),
            Gpo(PracownicyPolicy, "Księgowość", filter: Filter(apply: [Ksiegowosc], read: [Ksiegowosc, GroupPolicySecurityFilter.AuthenticatedUsersSid])),
        };
        var containers = new[] { Som(Pracownicy, GroupPolicySomKind.OrganizationalUnit, false, Link(FirmaPolicy, 0, 2), Link(PracownicyPolicy, 0, 1)) };

        var model = GroupPolicyModeler.Build(Input(containers, gpos, computer: Computer(Stacje), user: User(Pracownicy, Ksiegowosc)), Contents());

        Assert.Equal(ModeledGpoOutcome.ComputerCannotRead, Outcome(model.User!, FirmaPolicy));
        Assert.Equal(ModeledGpoOutcome.Applied, Outcome(model.User!, PracownicyPolicy));
        Assert.DoesNotContain(model.User!.Warnings, w => w.Kind == ModelingWarningKind.NoComputer);

        // Without a computer the read check is not made, and the model says so.
        var userOnly = GroupPolicyModeler.Build(Input(containers, gpos, user: User(Pracownicy, Ksiegowosc)), Contents());
        Assert.Equal(ModeledGpoOutcome.Applied, Outcome(userOnly.User!, FirmaPolicy));
        Assert.Contains(userOnly.User!.Warnings, w => w.Kind == ModelingWarningKind.NoComputer);
        Assert.Equal(LoopbackSource.NotApplicable, userOnly.LoopbackSource);
    }

    [Fact]
    public void Loopback_FromTheComputersGpo_ReplaceTakesTheComputersChain_MergeAppendsIt()
    {
        var containers = new[]
        {
            Som(Domain, GroupPolicySomKind.Domain, false, Link(DomainPolicy, 0, 1)),
            Som(Stacje, GroupPolicySomKind.OrganizationalUnit, false, Link(Loopback, 0, 2), Link(StacjePolicy, 0, 1)),
            Som(Pracownicy, GroupPolicySomKind.OrganizationalUnit, false, Link(PracownicyPolicy, 0, 1)),
        };
        var gpos = new[] { Gpo(DomainPolicy, "Default Domain Policy"), Gpo(Loopback, "Pętla zwrotna"), Gpo(StacjePolicy, "Stacje robocze"), Gpo(PracownicyPolicy, "Pracownicy") };
        var input = Input(containers, gpos, computer: Computer(Stacje), user: User(Pracownicy));

        var replace = GroupPolicyModeler.Build(input, Contents((Loopback, Folder((Machine + "Registry.pol", LoopbackPol(2))))));
        Assert.Equal(LoopbackMode.Replace, replace.Loopback);
        Assert.Equal(LoopbackSource.Settings, replace.LoopbackSource);
        Assert.Equal("Pętla zwrotna", replace.LoopbackSetBy!.GpoName);
        Assert.Equal(["Stacje robocze", "Pętla zwrotna", "Default Domain Policy"], replace.User!.Applied.Select(g => g.Name));
        Assert.All(replace.User.Applied, g => Assert.True(g.FromLoopback));
        Assert.DoesNotContain(replace.User.Gpos, g => g.GpoId == PracownicyPolicy);

        var merge = GroupPolicyModeler.Build(input, Contents((Loopback, Folder((Machine + "Registry.pol", LoopbackPol(1))))));
        Assert.Equal(LoopbackMode.Merge, merge.Loopback);
        Assert.Equal(["Stacje robocze", "Pętla zwrotna", "Default Domain Policy", "Pracownicy"], merge.User!.Applied.Select(g => g.Name));
        Assert.False(merge.User.Applied.Single(g => g.GpoId == PracownicyPolicy).FromLoopback);
        Assert.True(merge.User.Applied.Single(g => g.GpoId == DomainPolicy).FromLoopback); // in both chains: counted at its last position
        Assert.Contains(merge.User.Gpos, g => g.GpoId == DomainPolicy && g.Outcome == ModeledGpoOutcome.Duplicate && !g.FromLoopback);

        var off = GroupPolicyModeler.Build(input with { Options = new ModelingOptions { Loopback = LoopbackChoice.Off } },
            Contents((Loopback, Folder((Machine + "Registry.pol", LoopbackPol(2))))));
        Assert.Equal((LoopbackMode.None, LoopbackSource.Operator), (off.Loopback, off.LoopbackSource));
        Assert.Equal(["Pracownicy", "Default Domain Policy"], off.User!.Applied.Select(g => g.Name));

        var notConfigured = GroupPolicyModeler.Build(input, Contents());
        Assert.Equal((LoopbackMode.None, LoopbackSource.NotConfigured), (notConfigured.Loopback, notConfigured.LoopbackSource));
    }

    [Fact]
    public void Settings_HighestPrecedenceWins_ScriptsAddUp_AndADcTakesAccountPolicyFromTheDomainOnly()
    {
        var containers = new[]
        {
            Som(Domain, GroupPolicySomKind.Domain, false, Link(DomainPolicy, 0, 1)),
            Som(Stacje, GroupPolicySomKind.OrganizationalUnit, false, Link(StacjePolicy, 0, 1)),
        };
        var gpos = new[] { Gpo(DomainPolicy, "Default Domain Policy"), Gpo(StacjePolicy, "Stacje robocze") };
        var contents = Contents(
            (DomainPolicy, Folder(
                (GptTmpl, Utf8("[System Access]\r\nMinimumPasswordLength = 12\r\n[Privilege Rights]\r\nSeDebugPrivilege = *S-1-5-32-544\r\n")),
                (Machine + "Scripts/scripts.ini", Utf8("[Startup]\r\n0CmdLine=inwentarz.cmd\r\n0Parameters=\r\n")))),
            (StacjePolicy, Folder(
                (GptTmpl, Utf8("[System Access]\r\nMinimumPasswordLength = 8\r\n[Privilege Rights]\r\nSeDebugPrivilege = *S-1-5-32-544,*" + Laptopy + "\r\n")),
                (Machine + "Scripts/scripts.ini", Utf8("[Startup]\r\n0CmdLine=drukarki.cmd\r\n0Parameters=\r\n")))));

        var member = GroupPolicyModeler.Build(Input(containers, gpos, computer: Computer(Stacje)), contents).Computer!;

        var debug = Setting(member, "Computer/Security/Privilege Rights/SeDebugPrivilege");
        Assert.Equal("Stacje robocze", debug.Winner.Source.GpoName);
        Assert.Equal(1, debug.Winner.Source.Precedence);
        Assert.Equal(["Default Domain Policy"], debug.Overridden.Select(o => o.Source.GpoName));
        Assert.Equal("8", Setting(member, "Computer/Security/System Access/MinimumPasswordLength").Winner.Setting.Value);
        var startup = Setting(member, "Computer/Scripts/Startup/0");
        Assert.True(startup.Cumulative);
        Assert.Equal(["drukarki.cmd", "inwentarz.cmd"], new[] { startup.Winner }.Concat(startup.Overridden).Select(s => s.Setting.Value));

        // On a DC the password policy of a GPO linked to an OU does not reach the domain's accounts.
        var dc = GroupPolicyModeler.Build(Input(containers, gpos, computer: Computer(Stacje) with { IsDomainController = true }), contents).Computer!;
        var length = Setting(dc, "Computer/Security/System Access/MinimumPasswordLength");
        Assert.Equal(("Default Domain Policy", "12"), (length.Winner.Source.GpoName, length.Winner.Setting.Value));
        Assert.Empty(length.Overridden);
        Assert.Contains(dc.Warnings, w => w.Kind == ModelingWarningKind.AccountPolicyBelowDomain && w.GpoId == StacjePolicy);
        Assert.Equal("Stacje robocze", Setting(dc, "Computer/Security/Privilege Rights/SeDebugPrivilege").Winner.Source.GpoName);
    }

    [Fact]
    public void Policies_TheHighestPrecedenceGpoThatConfiguresOneWins_ValuesNoTemplateDescribesAreSettings()
    {
        var definitions = SampleAdministrativeTemplates.LoadPolish();
        var containers = new[]
        {
            Som(Domain, GroupPolicySomKind.Domain, false, Link(DomainPolicy, 0, 1)),
            Som(Stacje, GroupPolicySomKind.OrganizationalUnit, false, Link(StacjePolicy, 0, 1)),
        };
        var gpos = new[] { Gpo(DomainPolicy, "Default Domain Policy"), Gpo(StacjePolicy, "Stacje robocze") };
        // The templates' own rules write the policy: enabled in the domain's GPO, disabled in the OU's.
        var hidePreview = definitions.Find(SampleAdministrativeTemplates.HidePreview)!.Policy;
        var enabled = new RegistryPolicyDocument();
        PolicyEngine.Write(hidePreview, PolicySetting.Enabled(new Dictionary<string, PolicyElementValue>()), enabled);
        enabled.Set(@"Software\Firma\Inne", "Wartosc", 4, Dword(5));
        var disabled = new RegistryPolicyDocument();
        PolicyEngine.Write(hidePreview, PolicySetting.Disabled, disabled);
        var contents = Contents(
            (DomainPolicy, Folder((Machine + "Registry.pol", enabled.ToBytes()))),
            (StacjePolicy, Folder((Machine + "Registry.pol", disabled.ToBytes()))));
        var input = Input(containers, gpos, computer: Computer(Stacje)) with { Definitions = definitions };

        var part = GroupPolicyModeler.Build(input, contents).Computer!;

        var preview = Assert.Single(part.Policies, p => p.Definition.Id == SampleAdministrativeTemplates.HidePreview);
        Assert.Equal(("Stacje robocze", PolicyState.Disabled), (preview.Winner.Source.GpoName, preview.Winner.Setting.State));
        Assert.Equal(PolicyState.Enabled, Assert.Single(preview.Overridden).Setting.State);
        Assert.DoesNotContain(part.Settings, s => s.Key.Contains("NoPreview", StringComparison.Ordinal));
        Assert.Equal("Default Domain Policy", Setting(part, @"Computer/Registry/HKLM\Software\Firma\Inne!Wartosc").Winner.Source.GpoName);
        Assert.DoesNotContain(part.Warnings, w => w.Kind == ModelingWarningKind.NoDefinitions);

        // Without templates every value is a setting, and the model says why.
        var bare = GroupPolicyModeler.Build(input with { Definitions = PolicyDefinitionSet.Empty }, contents).Computer!;
        Assert.Empty(bare.Policies);
        Assert.Equal("Stacje robocze", Setting(bare, @"Computer/Registry/HKLM\Software\Policies\Contoso\Eksplorator!NoPreview").Winner.Source.GpoName);
        Assert.Contains(bare.Warnings, w => w.Kind == ModelingWarningKind.NoDefinitions);
    }

    [Fact]
    public void PreferenceItems_WithItemLevelTargeting_AreConditional()
    {
        const string groups = """
            <?xml version="1.0" encoding="utf-8"?>
            <Groups clsid="{3125E937-EB16-4b4c-9934-544FC6D24D26}">
              <Group clsid="{6D4A79E4-529C-4481-ABD0-F5BD7EA93BA7}" name="Administratorzy (wbudowane)" uid="{11111111-2222-3333-4444-555555555555}">
                <Properties action="U" groupName="Administratorzy (wbudowane)" groupSid="S-1-5-32-544">
                  <Members><Member name="CONTOSO\Serwis" action="ADD" sid="S-1-5-21-1000-2000-3000-1203" /></Members>
                </Properties>
                <Filters><FilterGroup bool="AND" not="0" name="CONTOSO\Laptopy" sid="S-1-5-21-1000-2000-3000-1202" userContext="0" primaryGroup="0" localGroup="0" /></Filters>
              </Group>
              <Group clsid="{6D4A79E4-529C-4481-ABD0-F5BD7EA93BA7}" name="Użytkownicy pulpitu zdalnego" uid="{66666666-2222-3333-4444-555555555555}">
                <Properties action="U" groupName="Użytkownicy pulpitu zdalnego" groupSid="S-1-5-32-555" />
              </Group>
            </Groups>
            """;
        var input = Input([Som(Stacje, GroupPolicySomKind.OrganizationalUnit, false, Link(StacjePolicy, 0, 1))], [Gpo(StacjePolicy, "Stacje robocze")], computer: Computer(Stacje));

        var part = GroupPolicyModeler.Build(input, Contents((StacjePolicy, Folder((Machine + "Preferences/Groups/Groups.xml", Utf8(groups)))))).Computer!;

        var targeted = part.Settings.Where(s => s.Key.Contains("{11111111-2222-3333-4444-555555555555}", StringComparison.Ordinal)).ToList();
        var plain = part.Settings.Where(s => s.Key.Contains("{66666666-2222-3333-4444-555555555555}", StringComparison.Ordinal)).ToList();
        Assert.NotEmpty(targeted);
        Assert.NotEmpty(plain);
        Assert.All(targeted, s => Assert.True(s.Winner.Conditional));
        Assert.All(plain, s => Assert.False(s.Winner.Conditional));
    }

    [Fact]
    public void Warnings_UnreadableFolderAndFile_NoSite_IncompleteToken()
    {
        var input = Input(
            [Som(Domain, GroupPolicySomKind.Domain, false, Link(DomainPolicy, 0, 2), Link(StacjePolicy, 0, 1))],
            [Gpo(DomainPolicy, "Default Domain Policy"), Gpo(StacjePolicy, "Stacje robocze")],
            computer: Computer(Stacje) with { TokenIncomplete = true }) with { SiteDn = null };
        var contents = new Dictionary<Guid, ModelingGpoContent>
        {
            [DomainPolicy] = new ModelingGpoContent { Unreadable = true },
            [StacjePolicy] = Folder((GptTmpl, [0xFE, 0xFF, 0x00, 0x41])),
        };

        var part = GroupPolicyModeler.Build(input, contents).Computer!;

        Assert.Contains(part.Warnings, w => w.Kind == ModelingWarningKind.ContentUnreadable && w.GpoName == "Default Domain Policy");
        Assert.Contains(part.Warnings, w => w.Kind == ModelingWarningKind.FileUnreadable && w.GpoId == StacjePolicy && w.Detail == GptTmpl);
        Assert.Contains(part.Warnings, w => w.Kind == ModelingWarningKind.NoSite);
        Assert.Contains(part.Warnings, w => w.Kind == ModelingWarningKind.TokenIncomplete);
        Assert.Empty(part.Settings);
    }

    [Fact]
    public void GposToRead_AreTheExistingGposOfBothChains()
    {
        var input = Input(
            [
                Som(Domain, GroupPolicySomKind.Domain, false, Link(DomainPolicy, 0, 1)),
                Som(Stacje, GroupPolicySomKind.OrganizationalUnit, false, Link(StacjePolicy, 0, 2), Link(Missing, 0, 1)),
                Som(Pracownicy, GroupPolicySomKind.OrganizationalUnit, false, Link(PracownicyPolicy, 1, 1)),
                Som(Firma, GroupPolicySomKind.OrganizationalUnit, false, Link(FirmaPolicy, 0, 1)),
            ],
            [Gpo(DomainPolicy, "Default Domain Policy"), Gpo(StacjePolicy, "Stacje robocze"), Gpo(PracownicyPolicy, "Pracownicy"), Gpo(FirmaPolicy, "Firma")],
            computer: Computer(Stacje), user: User(Pracownicy));

        Assert.Equal(new[] { DomainPolicy, FirmaPolicy, StacjePolicy }.Order(), GroupPolicyModeler.GposToRead(input));
        Assert.Throws<ArgumentException>(() => GroupPolicyModeler.Build(input with { Computer = null, User = null }, Contents()));
    }

    [Fact]
    public void SecurityFilter_FromADescriptor_KnowsWhoMayReadTheGpo()
    {
        const int read = 0x20094; // GPMC's Read: read control, list, read property, list object
        var descriptor = SecurityDescriptorWriter.Build(
            SecurityDescriptorWriter.SelfRelative | SecurityDescriptorWriter.DaclPresent,
            null,
            null,
            [
                SecurityDescriptorWriter.EncodeAce(0x00, 0, read, GroupPolicySecurityFilter.AuthenticatedUsersSid, null, null),
                SecurityDescriptorWriter.EncodeAce(0x05, 0, 0x100, Ksiegowosc, GroupPolicySecurityFilter.ApplyGroupPolicyRight, null),
                SecurityDescriptorWriter.EncodeAce(0x01, 0, read, Laptopy, null, null),
                SecurityDescriptorWriter.EncodeAce(0x00, SecurityDescriptorAce.InheritOnlyFlag, read, DomainUsers, null, null),
            ]);

        var filter = GroupPolicySecurityFilter.FromDescriptor(descriptor)!;

        Assert.True(filter.CanRead(Token(DomainComputers)));
        Assert.False(filter.CanRead(Token(DomainComputers, Laptopy)));
        Assert.False(filter.CanRead(new HashSet<string> { DomainUsers }));
        Assert.True(filter.Applies(Token(Ksiegowosc)));
        Assert.False(filter.Applies(Token(DomainUsers)));
    }

    // ---- Helpers -------------------------------------------------------------------------------------------------------

    private static ModeledGpoOutcome Outcome(ModeledPart part, Guid id) => part.Gpos.First(g => g.GpoId == id).Outcome;

    private static ModeledSetting Setting(ModeledPart part, string key) => Assert.Single(part.Settings, s => s.Key == key);

    private static GroupPolicyModelInput Input(IReadOnlyList<GroupPolicySom> containers, IReadOnlyList<ModelingGpo> gpos, ModelingTarget? computer = null, ModelingTarget? user = null) => new()
    {
        DomainDn = Domain,
        Containers = containers,
        Gpos = gpos.ToDictionary(g => g.Id),
        Computer = computer,
        User = user,
        SiteDn = Site,
    };

    private static GroupPolicySom Som(string dn, GroupPolicySomKind kind, bool blocks, params GroupPolicyLink[] links) => new(dn, kind, links, blocks);

    private static GroupPolicyLink Link(Guid id, int options, int order) =>
        new($"CN={SysvolPaths.FolderName(id)},CN=Policies,CN=System,{Domain}", options, order);

    private static ModelingGpo Gpo(Guid id, string name, int flags = 0, GpoVersion? version = null, string? wmi = null, GroupPolicySecurityFilter? filter = null) => new()
    {
        Id = id,
        DisplayName = name,
        Flags = flags,
        Version = version ?? new GpoVersion(2, 2),
        HasWmiFilter = wmi is not null,
        WmiFilterName = wmi,
        SecurityFilter = filter ?? Filter(apply: [GroupPolicySecurityFilter.AuthenticatedUsersSid], read: [GroupPolicySecurityFilter.AuthenticatedUsersSid]),
    };

    private static GroupPolicySecurityFilter Filter(string[] apply, string[] read) =>
        new(new HashSet<string>(apply, StringComparer.OrdinalIgnoreCase), new HashSet<string>())
        {
            ReadAllowed = new HashSet<string>(read, StringComparer.OrdinalIgnoreCase),
        };

    private static HashSet<string> Token(params string[] groups) =>
        new(new[] { GroupPolicySecurityFilter.EveryoneSid, GroupPolicySecurityFilter.AuthenticatedUsersSid, GroupPolicySecurityFilter.ThisOrganizationSid }.Concat(groups),
            StringComparer.OrdinalIgnoreCase);

    private static ModelingTarget Computer(string container, params string[] groups) =>
        new() { ContainerDn = container, AccountDn = "CN=PC-0042," + container, Token = Token([DomainSid + "-1100", DomainComputers, .. groups]) };

    private static ModelingTarget User(string container, params string[] groups) =>
        new() { ContainerDn = container, AccountDn = "CN=Zofia Wójcik," + container, Token = Token([DomainSid + "-1300", DomainUsers, .. groups]) };

    private static Dictionary<Guid, ModelingGpoContent> Contents(params (Guid Id, ModelingGpoContent Content)[] contents) =>
        contents.ToDictionary(c => c.Id, c => c.Content);

    private static ModelingGpoContent Folder(params (string Path, byte[] Bytes)[] files) =>
        GroupPolicyModelReader.FromFolder(new GpoFolderContent([], files.ToDictionary(f => f.Path, f => f.Bytes, StringComparer.OrdinalIgnoreCase)));

    private static byte[] Utf8(string text) => Encoding.UTF8.GetBytes(text);

    private static byte[] Dword(uint value) => BitConverter.GetBytes(value);

    private static byte[] Pol(params (string Key, string Name, byte[] Data)[] values)
    {
        var document = new RegistryPolicyDocument();
        foreach (var (key, name, data) in values)
        {
            document.Set(key, name, 4, data);
        }

        return document.ToBytes();
    }

    private static byte[] LoopbackPol(uint mode) => Pol((GroupPolicyModeler.LoopbackKey, GroupPolicyModeler.LoopbackValueName, Dword(mode)));
}
