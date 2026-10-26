using System.Globalization;
using AdMng.Core.Filters;
using AdMng.Core.GroupPolicy;
using AdMng.Core.PrivilegedAccess;
using AdMng.Presentation.AccountReview;
using AdMng.Presentation.Filters;
using AdMng.Presentation.Localization;

namespace AdMng.Presentation.Tests.Localization;

/// <summary>The dynamic key families of the "Uprawnienia" tab and the filter builder (README U4) exist in both languages.</summary>
public class PrivilegedAccessLocalizationTests
{
    [Theory]
    [InlineData("en-US")]
    [InlineData("pl-PL")]
    public void DynamicFamilies_AreComplete(string culture)
    {
        var l = new ResxLocalizer(CultureInfo.GetCultureInfo(culture));
        var keys = new List<string>();
        keys.AddRange(Enum.GetValues<PrivilegeLevel>().SelectMany(v => new[] { PrivilegedAccessText.LevelKey(v), PrivilegedAccessText.LevelHintKey(v) }));
        keys.AddRange(Enum.GetValues<PrivilegeSource>().Select(PrivilegedAccessText.SourceKey));
        keys.AddRange(Enum.GetValues<PrivilegeFindingKind>().Select(PrivilegedAccessText.KindKey));
        keys.AddRange(Enum.GetValues<PrivilegeScopeKind>().Select(PrivilegedAccessText.ScopeKey));
        keys.AddRange(Enum.GetValues<PrivilegedAccountType>().Select(PrivilegedAccessText.TypeKey));
        keys.AddRange(Enum.GetValues<PeerDimension>().Select(PrivilegedAccessText.PeerKey));
        keys.AddRange(Enum.GetValues<PrivilegeRuleTarget>().Select(PrivilegedAccessText.TargetKey));
        keys.AddRange(PrivilegedAccessColumns.All.Select(PrivilegedAccessText.ColumnKey));
        keys.AddRange(Enum.GetValues<AclRight>().Select(PrivilegedAccessText.AclRightKey));
        keys.AddRange(PrivilegedGroupCatalog.All.SelectMany(g => new[] { PrivilegedAccessText.GroupKey(g.Id), PrivilegedAccessText.GroupNameKey(g.Id) }));
        keys.AddRange(Enum.GetValues<FilterOperator>().Select(FilterTexts.OperatorKey));
        keys.AddRange(Enum.GetValues<FilterCombinator>().Select(FilterTexts.CombinatorKey));
        keys.AddRange(Enum.GetValues<FilterIssueCode>().Select(FilterTexts.IssueKey));

        // U4c: local rights from GPOs - what each right gives, its name, the mechanisms and the notices of the source.
        keys.AddRange(LocalRightsCatalog.All.Select(d => PrivilegedAccessText.LocalRightKey(d.Id)));
        keys.AddRange(LocalRightsCatalog.All.Select(d => d.Kind == LocalRightKind.LocalGroup
            ? PrivilegedAccessText.GroupNameKey(d.Id)
            : PrivilegedAccessText.UserRightNameKey(d.Id)));
        keys.AddRange(Enum.GetValues<LocalRightMechanism>().Select(PrivilegedAccessText.MechanismKey));
        keys.AddRange(Enum.GetValues<SysvolUnavailableReason>().Select(r => PrivilegedAccessText.NoticeKey(GroupPolicyGrantSource.Notices.SysvolUnavailable(r))));
        keys.AddRange(new[]
        {
            GroupPolicyGrantSource.Notices.FilesUnreadable, GroupPolicyGrantSource.Notices.FilesInvalid, GroupPolicyGrantSource.Notices.UnresolvedPrincipals,
            GroupPolicyGrantSource.Notices.MissingPolicies, GroupPolicyGrantSource.Notices.PathRedirected,
        }.Select(PrivilegedAccessText.NoticeKey));

        Assert.Equal([], keys.Where(k => !l.Contains(k)));
    }

    [Fact]
    public void Polish_NamesTheLocalGroupsAndUserRightsAsPolishWindowsDoes()
    {
        var l = new ResxLocalizer(CultureInfo.GetCultureInfo("pl-PL"));
        var formatter = new PrivilegedAccessFormatter(l, "contoso.test");

        Assert.Equal("Użytkownicy zaawansowani", formatter.RightName(LocalRightsCatalog.Find(LocalRightsCatalog.PowerUsersSid)!));
        Assert.Equal("Użytkownicy pulpitu zdalnego", formatter.RightName(LocalRightsCatalog.Find(LocalRightsCatalog.RemoteDesktopUsersSid)!));
        Assert.Equal("Zezwalanie na logowanie lokalne (SeInteractiveLogonRight)", formatter.RightName(LocalRightsCatalog.Find("SeInteractiveLogonRight")!));
        Assert.Equal("Grupy z ograniczeniami (członek grupy)", l[PrivilegedAccessText.MechanismKey(LocalRightMechanism.RestrictedGroupMemberOf)]);
        Assert.Equal(
            "Nie udało się odczytać plików zasad grupy (GPO: 2): Stacje – Łódź, Serwery – Gdańsk. Ich uprawnienia lokalne nie zostały uwzględnione.",
            formatter.Notice(new PrivilegeSourceNotice(PrivilegeSource.GroupPolicy, GroupPolicyGrantSource.Notices.FilesUnreadable, ["2", "Stacje – Łódź, Serwery – Gdańsk"])));
    }

    [Fact]
    public void Polish_ADomainGroupEnforcedByAGpo_IsNamedAndExplained()
    {
        var l = new ResxLocalizer(CultureInfo.GetCultureInfo("pl-PL"));
        var formatter = new PrivilegedAccessFormatter(l, "contoso.test");
        var definition = LocalRightsCatalog.DomainGroup(PrivilegedGroupCatalog.Find("DomainAdmins")!, "S-1-5-21-1-2-3-512");
        var gpo = new GroupPolicyGrantDetails
        {
            GpoId = Guid.Empty,
            GpoName = "Kontrolery – Utrzymanie członkostwa",
            GpoDn = "CN={00000000-0000-0000-0000-000000000000},CN=Policies,CN=System,DC=contoso,DC=test",
            Mechanism = LocalRightMechanism.RestrictedGroupMemberOf,
            Right = definition.Key,
            Definition = definition,
            LinkDn = "OU=Domain Controllers,DC=contoso,DC=test",
            DomainControllers = 2,
        };

        Assert.Equal("Administratorzy domeny", formatter.RightName(definition));
        Assert.Equal(
            "Pełna kontrola nad wszystkimi obiektami i komputerami domeny. Źródło: Grupy z ograniczeniami (członek grupy) w GPO "
            + "„Kontrolery – Utrzymanie członkostwa”; komputery: 2 (kontrolery domeny: 2, serwery: 0, stacje robocze: 0). "
            + "Na kontrolerach domeny GPO wymusza to członkostwo w grupie domeny: usunięcie z grupy zostanie cofnięte przy "
            + "następnym odświeżeniu zasad – zmień GPO.",
            formatter.LocalRight(gpo));
    }

    [Fact]
    public void Polish_NamesTheBuiltInGroupsAsPolishWindowsDoes()
    {
        var l = new ResxLocalizer(CultureInfo.GetCultureInfo("pl-PL"));

        Assert.Equal("Operatorzy kont", l[PrivilegedAccessText.GroupNameKey("AccountOperators")]);
        Assert.Equal("Twórcy-właściciele zasad grupy", l[PrivilegedAccessText.GroupNameKey("GroupPolicyCreatorOwners")]);
        Assert.Equal("Czytelnicy dziennika zdarzeń", l[PrivilegedAccessText.GroupNameKey("EventLogReaders")]);
        Assert.Equal("Podwyższony", l[PrivilegedAccessText.LevelKey(PrivilegeLevel.Elevated)]);
        Assert.Equal("równa się", l[FilterTexts.OperatorKey(FilterOperator.Equals)]);
        Assert.Equal("I (wszystkie)", l[FilterTexts.CombinatorKey(FilterCombinator.All)]);
        Assert.Equal("Uprawnienia", l[L.PrivilegedAccess.TabPrivileges]);
    }

    [Fact]
    public void Summary_OfAFilter_InTheUiLanguage()
    {
        var l = new ResxLocalizer(CultureInfo.GetCultureInfo("pl-PL"));
        var definition = new FilterDefinition
        {
            Combinator = FilterCombinator.Any,
            Groups =
            [
                new FilterConditionGroup
                {
                    Conditions =
                    [
                        new FilterCondition("department", FilterOperator.Equals, "Księgowość"),
                        new FilterCondition("title", FilterOperator.Present),
                    ],
                },
                new FilterConditionGroup { Conditions = [new FilterCondition(string.Empty, FilterOperator.MemberOf, "CN=GG_Płace,OU=Grupy,{domain}")] },
            ],
        };

        Assert.Equal("(department równa się Księgowość I title istnieje) LUB członek grupy (także pośrednio) GG_Płace", FilterTexts.Summary(l, definition));
        Assert.Equal("(objectClass=user)", FilterTexts.Summary(l, FilterDefinition.Raw("(objectClass=user)")));
    }
}
