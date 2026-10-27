using System.Globalization;
using AdMng.Core.ActiveDirectory;
using AdMng.Core.Directory;
using AdMng.Core.Filters;
using AdMng.Core.Ldap;
using AdMng.Core.PrivilegedAccess;
using AdMng.Directory.IntegrationTests.Infrastructure;
using AdMng.Presentation.Filters;
using AdMng.Presentation.Session;

namespace AdMng.Directory.IntegrationTests.Integration;

/// <summary>
/// The privileged access review (README U4a) against the Samba lab, in this run's own OU: nested membership of a built-in
/// group recognised by SID (through two groups of the OU), a minor built-in group, a user with an SPN, unconstrained and
/// constrained delegation with protocol transition towards the DC, an orphaned adminCount, no pre-authentication - and the
/// filters of the filter builder accepted by the server (all operators, member-of nested), with the client check of a
/// constructed attribute. The groups of the OU are removed with it, which also removes them from the built-in groups.
/// </summary>
public sealed class PrivilegedAccessTests(LabFixture lab)
{
    /// <summary>Minor built-in groups; the first one the lab has is used.</summary>
    private static readonly string[] MinorBuiltinSids = ["S-1-5-32-573", "S-1-5-32-558", "S-1-5-32-559", "S-1-5-32-562", "S-1-5-32-556"];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Scan_FindsNestedBuiltInGroups_AndTheRiskySettings_OfTheLabAccounts()
    {
        LabSettings.RequireLab();
        var admin = lab.Admin;
        var ou = await lab.CreateOuAsync(TestNames.Unique("Uprawnienia"));
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var policy = DomainPolicy.FromDomainObject((await admin.GetAsync(LabSettings.DomainDn, DomainPolicy.Attributes, Ct))!);
        Assert.NotNull(policy.DomainSid);
        var accountOperators = await FindBySidAsync(admin, "S-1-5-32-548");
        var minor = await FirstBySidAsync(admin, MinorBuiltinSids);
        var dc = await FirstDomainControllerAsync(admin);

        // Ludwik → GG_Upr_A → GG_Upr_B → Account Operators; Ewelina directly in a minor built-in group.
        var ludwik = await lab.CreateUserAsync(ou, "Ludwik Zagnieżdżony", new Dictionary<string, string> { [Attr.Department] = "Księgowość" });
        var ewelina = await lab.CreateUserAsync(ou, "Ewelina Dziennikowa");
        var groupA = await CreateGroupAsync(ou, $"GG_Upr_A_{suffix}", ludwik);
        var groupB = await CreateGroupAsync(ou, $"GG_Upr_B_{suffix}", groupA);
        await AddMemberAsync(accountOperators.Dn, groupB);
        await AddMemberAsync(minor.Dn, ewelina);

        // The risky settings.
        var szymon = await lab.CreateUserAsync(ou, "Szymon Usługowy");
        await AddValueAsync(szymon, Attr.ServicePrincipalName, $"HTTP/app-{suffix}.{LabSettings.DomainDns}");
        var daria = await lab.CreateUserAsync(ou, "Daria Delegująca");
        await ReplaceAsync(daria, Attr.UserAccountControl, Uac(UserAccountControl.NormalAccount | UserAccountControl.AccountDisable | UserAccountControl.TrustedForDelegation));
        var tomasz = await lab.CreateUserAsync(ou, "Tomasz Ograniczony");
        await ReplaceAsync(tomasz, Attr.AllowedToDelegateTo, $"ldap/{dc.HostName}");
        await ReplaceAsync(tomasz, Attr.UserAccountControl, Uac(UserAccountControl.NormalAccount | UserAccountControl.AccountDisable | UserAccountControl.TrustedToAuthForDelegation));
        var adam = await lab.CreateUserAsync(ou, "Adam Osierocony");
        await ReplaceAsync(adam, Attr.AdminCount, "1");
        var paulina = await lab.CreateUserAsync(ou, "Paulina Bezwstępna");
        await ReplaceAsync(paulina, Attr.UserAccountControl, Uac(UserAccountControl.NormalAccount | UserAccountControl.AccountDisable | UserAccountControl.DontRequirePreauth));
        var zwykly = await lab.CreateUserAsync(ou, "Zenon Zwykły");

        var options = new PrivilegedAccessOptions
        {
            DomainSid = policy.DomainSid,
            RootDomainSid = policy.DomainSid,
            Rules =
            [
                new PrivilegeConditionRule("Księgowość w OU", FilterDefinition.Of(new FilterCondition(Attr.Department, FilterOperator.Equals, "Księgowość")), PrivilegeLevel.Minor, PrivilegeRuleTarget.Accounts),
                new PrivilegeConditionRule("Grupa B", FilterDefinition.Of(new FilterCondition(Attr.Name, FilterOperator.StartsWith, "GG_Upr_B_")), PrivilegeLevel.High, PrivilegeRuleTarget.Groups),
            ],
        };
        var scanner = new PrivilegedAccessScanner(admin, options);
        var evaluator = await scanner.PrepareAsync(DateTimeOffset.UtcNow, Ct);
        Assert.Empty(scanner.SkippedRules);
        var results = new Dictionary<string, AccountPrivileges>(StringComparer.OrdinalIgnoreCase);
        await foreach (var account in scanner.ScanAsync(evaluator, ou, SearchScope.Subtree, null, null, Ct))
        {
            results[account.Account.Name] = account;
        }

        Assert.Equal(8, results.Count);
        Assert.False(results["Zenon Zwykły"].HasFindings);

        var nested = Assert.Single(results["Ludwik Zagnieżdżony"].Findings, f => f.Kind == PrivilegeFindingKind.BuiltInGroup);
        Assert.Equal("AccountOperators", nested.CatalogId);
        Assert.Equal(PrivilegeLevel.Tier0, nested.Level);
        Assert.Equal(["Ludwik Zagnieżdżony", $"GG_Upr_A_{suffix}", $"GG_Upr_B_{suffix}", accountOperators.Name], nested.Path.Select(s => s.Name));
        Assert.True(DistinguishedName.AreEqual(groupA, nested.DirectGroupDn));
        Assert.Contains(results["Ludwik Zagnieżdżony"].Findings, f => f.Kind == PrivilegeFindingKind.ConditionGroup && f.Level == PrivilegeLevel.High);
        Assert.Contains(results["Ludwik Zagnieżdżony"].Findings, f => f.Kind == PrivilegeFindingKind.ConditionAccount && f.Note == "Księgowość w OU");

        var reader = Assert.Single(results["Ewelina Dziennikowa"].Findings);
        Assert.Equal(PrivilegeLevel.Minor, reader.Level);
        Assert.Equal(PrivilegedGroupCatalog.Match(minor.Sid, null, policy.DomainSid, policy.DomainSid)!.Id, reader.CatalogId);

        Assert.Equal(PrivilegeFindingKind.Kerberoastable, Assert.Single(results["Szymon Usługowy"].Findings).Kind);
        var unconstrained = Assert.Single(results["Daria Delegująca"].Findings);
        Assert.Equal(PrivilegeFindingKind.UnconstrainedDelegation, unconstrained.Kind);
        Assert.Equal(PrivilegeLevel.Tier0, unconstrained.Level);
        var transition = Assert.Single(results["Tomasz Ograniczony"].Findings);
        Assert.Equal(PrivilegeFindingKind.ProtocolTransition, transition.Kind);
        Assert.Equal(PrivilegeLevel.Tier0, transition.Level);
        Assert.Equal(PrivilegeFindingKind.OrphanedAdminCount, Assert.Single(results["Adam Osierocony"].Findings).Kind);
        Assert.Equal(PrivilegeFindingKind.PreauthNotRequired, Assert.Single(results["Paulina Bezwstępna"].Findings).Kind);

        // The lab's own DC is Tier 0 through its primary group (the domain-wide graph and DC query were accepted).
        var dcAccount = evaluator.Evaluate((await admin.GetAsync(dc.Dn, evaluator.AccountAttributes, Ct))!);
        Assert.NotNull(dcAccount);
        Assert.Equal(PrivilegedAccountType.DomainController, dcAccount.Type);
        Assert.Equal(PrivilegeLevel.Tier0, dcAccount.Level);
    }

    [Fact]
    public async Task SavedFilters_EveryOperator_IsAcceptedByTheServer_AndTheClientChecksConstructedAttributes()
    {
        LabSettings.RequireLab();
        var admin = lab.Admin;
        var ou = await lab.CreateOuAsync(TestNames.Unique("Filtry"));
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var grazyna = await lab.CreateUserAsync(ou, "Grażyna Filtrowa", new Dictionary<string, string>
        {
            [Attr.Department] = "Księgowość (Kraków)",
            [Attr.Description] = "Konto administracyjne * test",
            [Attr.Title] = "Główna księgowa",
        });
        var other = await lab.CreateUserAsync(ou, "Bogdan Pozafiltrowy", new Dictionary<string, string> { [Attr.Department] = "IT", [Attr.Mail] = $"b-{suffix}@contoso.test" });
        var inner = await CreateGroupAsync(ou, $"GG_Filtr_A_{suffix}", grazyna);
        var outer = await CreateGroupAsync(ou, $"GG_Filtr_B_{suffix}", inner);
        var context = new FilterContext { Now = DateTimeOffset.UtcNow, DefaultNamingContext = LabSettings.DomainDn, Schema = admin.Schema };
        var portableOuter = Presentation.Views.SavedView.ToPortableDn(outer, LabSettings.DomainDn);

        var definition = new FilterDefinition
        {
            Groups =
            [
                new FilterConditionGroup
                {
                    Conditions =
                    [
                        new FilterCondition(Attr.Department, FilterOperator.Equals, "Księgowość (Kraków)"),
                        new FilterCondition(Attr.Description, FilterOperator.Contains, "administracyjne *"),
                        new FilterCondition(Attr.Title, FilterOperator.StartsWith, "Główna"),
                        new FilterCondition(Attr.Title, FilterOperator.EndsWith, "księgowa"),
                        new FilterCondition(Attr.Mail, FilterOperator.NotPresent),
                        new FilterCondition(Attr.Department, FilterOperator.Present),
                        new FilterCondition(Attr.UserAccountControl, FilterOperator.BitSet, "2"),
                        new FilterCondition(Attr.UserAccountControl, FilterOperator.BitNotSet, "0x10000"),
                        new FilterCondition(Attr.WhenCreated, FilterOperator.NewerThanDays, "1"),
                        new FilterCondition(Attr.WhenCreated, FilterOperator.GreaterOrEqual, "2020-01-01"),
                        new FilterCondition(Attr.PwdLastSet, FilterOperator.LessOrEqual, "0"),
                        new FilterCondition(string.Empty, FilterOperator.MemberOf, portableOuter),
                        new FilterCondition(Attr.Department, FilterOperator.NotEquals, "IT"),
                    ],
                },
            ],
        };
        var ldap = FilterCompiler.ToLdap(definition, context);
        Assert.Null(LdapFilter.Validate(ldap));
        Assert.Equal(["Grażyna Filtrowa"], await NamesAsync(admin, ou, ldap));

        // LUB between groups; a raw filter; the not-member-of and older-than operators.
        var any = new FilterDefinition
        {
            Combinator = FilterCombinator.Any,
            Groups =
            [
                new FilterConditionGroup { Conditions = [new FilterCondition(Attr.Department, FilterOperator.Equals, "IT")] },
                new FilterConditionGroup { Conditions = [new FilterCondition(string.Empty, FilterOperator.MemberOf, outer)] },
            ],
        };
        Assert.Equal(["Bogdan Pozafiltrowy", "Grażyna Filtrowa"], await NamesAsync(admin, ou, FilterCompiler.ToLdap(any, context)));
        Assert.Equal(["Bogdan Pozafiltrowy"], await NamesAsync(admin, ou, FilterCompiler.ToLdap(FilterDefinition.Of(
            new FilterCondition(string.Empty, FilterOperator.NotMemberOf, outer),
            new FilterCondition(Attr.WhenCreated, FilterOperator.OlderThanDays, "0"),
            new FilterCondition(Attr.ObjectClass, FilterOperator.Equals, "user")), context)));
        Assert.Equal(["Grażyna Filtrowa"], await NamesAsync(admin, ou, FilterCompiler.ToLdap(FilterDefinition.Raw("(&(objectClass=user)(title=Główna*))"), context)));

        // A constructed attribute: the server gets the department only, the client checks the lock state.
        var plan = SavedFilterPlan.Create(
            [FilterDefinition.Of(new FilterCondition(Attr.Department, FilterOperator.Present), new FilterCondition(Attr.UserAccountControlComputed, FilterOperator.BitNotSet, "16"))],
            context);
        Assert.True(plan.NeedsClientCheck);
        Assert.Equal("(department=*)", plan.ServerFilter);
        var matched = new List<string>();
        await foreach (var obj in admin.SearchAsync(new DirectorySearch { BaseDn = ou, Filter = plan.ServerFilter!, Attributes = [Attr.Name, .. plan.Attributes] }, Ct))
        {
            if (plan.Matches(obj))
            {
                matched.Add(obj.Name);
            }
        }

        Assert.Equal(["Bogdan Pozafiltrowy", "Grażyna Filtrowa"], matched.Order(StringComparer.Ordinal));
        _ = other;
    }

    private async Task<string> CreateGroupAsync(string ou, string name, string member)
    {
        var dn = DistinguishedName.Parse(ou).Child("CN", name).ToString();
        await lab.Admin.AddAsync(dn, new Dictionary<string, IReadOnlyList<object>>
        {
            [Attr.ObjectClass] = ["top", ObjectClasses.Group],
            [Attr.SamAccountName] = [TestNames.SamAccountName()],
            [Attr.Member] = [member],
        }, Ct);
        return dn;
    }

    private Task AddMemberAsync(string groupDn, string memberDn) => AddValueAsync(groupDn, Attr.Member, memberDn);

    private Task AddValueAsync(string dn, string attribute, string value) =>
        lab.Admin.ModifyAsync(dn, [AttributeModification.Add(attribute, value)], null, Ct);

    private Task ReplaceAsync(string dn, string attribute, string value) =>
        lab.Admin.ModifyAsync(dn, [AttributeModification.Replace(attribute, value)], null, Ct);

    private static async Task<(string Dn, string Name, string Sid)> FindBySidAsync(IDirectorySession session, string sid)
    {
        await foreach (var group in session.SearchAsync(new DirectorySearch
        {
            BaseDn = LabSettings.DomainDn,
            Filter = LdapFilter.Equal(Attr.ObjectSid, sid),
            Attributes = [Attr.Name, Attr.ObjectSid],
        }, Ct))
        {
            return (group.DistinguishedName, group.Name, sid);
        }

        throw new InvalidOperationException($"The lab has no group {sid}.");
    }

    private static async Task<(string Dn, string Name, string Sid)> FirstBySidAsync(IDirectorySession session, IEnumerable<string> sids)
    {
        foreach (var sid in sids)
        {
            try
            {
                return await FindBySidAsync(session, sid);
            }
            catch (InvalidOperationException)
            {
                // Not provisioned in this lab: try the next one.
            }
        }

        throw new InvalidOperationException("The lab has none of the minor built-in groups.");
    }

    private static async Task<(string Dn, string HostName)> FirstDomainControllerAsync(IDirectorySession session)
    {
        await foreach (var dc in session.SearchAsync(new DirectorySearch
        {
            BaseDn = LabSettings.DomainDn,
            Filter = PrivilegedAccessScanner.DomainControllerFilter,
            Attributes = [Attr.DnsHostName],
        }, Ct))
        {
            return (dc.DistinguishedName, dc.GetString(Attr.DnsHostName) ?? throw new InvalidOperationException("The DC has no dNSHostName."));
        }

        throw new InvalidOperationException("No domain controller found.");
    }

    private static async Task<IReadOnlyList<string>> NamesAsync(IDirectorySession session, string baseDn, string filter)
    {
        var names = new List<string>();
        await foreach (var obj in session.SearchAsync(new DirectorySearch { BaseDn = baseDn, Filter = LdapFilter.And(PrivilegedAccessScanner.AccountFilter, filter), Attributes = [Attr.Name] }, Ct))
        {
            names.Add(obj.Name);
        }

        return [.. names.Order(StringComparer.Ordinal)];
    }

    private static string Uac(UserAccountControl flags) => ((int)flags).ToString(CultureInfo.InvariantCulture);
}
