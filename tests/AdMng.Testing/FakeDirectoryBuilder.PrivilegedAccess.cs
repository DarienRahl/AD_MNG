using AdMng.Core.ActiveDirectory;

namespace AdMng.Testing;

/// <summary>The sample company extended for the privileged access review (README U4, "Uprawnienia").</summary>
public sealed partial class FakeDirectoryBuilder
{
    /// <summary>A SID of a domain the company migrated from (sIDHistory of Stanisław Łęcki).</summary>
    public const string OldDomainSid = "S-1-5-21-1111111111-2222222222-3333333333";

    /// <summary>
    /// Extends <see cref="AddSampleCompany"/> with privileged and unusual accounts: the built-in groups with their Polish
    /// names (recognised by SID only), Domain Controllers, Group Policy Creator Owners and DnsAdmins, a nested path to
    /// Account Operators, a membership cycle, a distribution group inside Domain Admins (no rights), service accounts with
    /// SPN and delegation, a gMSA among the backup operators, servers with unconstrained and resource-based delegation,
    /// adminCount without a protected group, no pre-authentication, SID history, and four more accountants of whom one
    /// holds two groups no other accountant holds.
    /// </summary>
    public PrivilegedSample AddPrivilegedAccessSample(SampleCompany company)
    {
        ArgumentNullException.ThrowIfNull(company);
        var serviceOu = AddOu("Konta serwisowe", company.CompanyOu);
        var serversOu = AddOu("Serwery", company.CompanyOu);
        var fspContainer = AddContainer("ForeignSecurityPrincipals", DomainDn);
        var authenticatedUsers = Session.Seed($"CN=S-1-5-11,{fspContainer}", new Dictionary<string, object?>
        {
            [Attr.ObjectClass] = new[] { "top", "foreignSecurityPrincipal" },
            [Attr.ObjectSid] = "S-1-5-11",
        });

        // People of the IT department with extra rights, and the service accounts.
        var henryk = AddUser("Henryk", "Żuławski", company.ItOu, Person("IT", "Specjalista helpdesku", "Warszawa", "henryk.zulawski"));
        var ewa = AddUser("Ewa", "Błaszczyk", company.ItOu, Person("IT", "Administratorka aplikacji", "Kraków", "ewa.blaszczyk"));
        var wojciech = AddUser("Wojciech", "Ząbek", company.ItOu, Person("IT", "Administrator sieci", "Gdańsk", "wojciech.zabek",
            (Attr.EmployeeType, "IT-Admin")));
        var ignacy = AddUser("Ignacy", "Wróblewski", company.ItOu, Person("IT", "Były administrator", "Poznań", "ignacy.wroblewski",
            (Attr.AdminCount, 1)));
        var jolanta = AddUser("Jolanta", "Ćwik", company.ItOu, Person("IT", "Testerka", "Łódź", "jolanta.cwik",
            (Attr.UserAccountControl, (int)(UserAccountControl.NormalAccount | UserAccountControl.DontRequirePreauth))));
        var stanislaw = AddUser("Stanisław", "Łęcki", company.ItOu, Person("IT", "Analityk", "Wrocław", "stanislaw.lecki",
            (Attr.SidHistory, $"{OldDomainSid}-1180")));
        var sql = AddUser("Usługa", "SQL", serviceOu, new Dictionary<string, object?>
        {
            [Attr.SamAccountName] = "svc_sql",
            [Attr.Description] = "Konto usługi SQL Server",
            [Attr.ServicePrincipalName] = new[] { $"MSSQLSvc/sql01.{DomainDnsName}:1433", $"MSSQLSvc/sql01.{DomainDnsName}" },
            [Attr.AllowedToDelegateTo] = new[] { $"cifs/fs01.{DomainDnsName}" },
            [Attr.UserAccountControl] = (int)(UserAccountControl.NormalAccount | UserAccountControl.DontExpirePassword),
        });
        var web = AddUser("Usługa", "WWW", serviceOu, new Dictionary<string, object?>
        {
            [Attr.SamAccountName] = "svc_www",
            [Attr.AllowedToDelegateTo] = new[] { $"ldap/dc01.{DomainDnsName}", "ldap/DC01" },
            [Attr.UserAccountControl] = (int)(UserAccountControl.NormalAccount | UserAccountControl.TrustedToAuthForDelegation),
        });
        var gmsa = AddComputer("gmsa-kopie", serviceOu, new Dictionary<string, object?>
        {
            [Attr.ObjectClass] = new[] { "top", "person", "organizationalPerson", ObjectClasses.User, ObjectClasses.Computer, ObjectClasses.GroupManagedServiceAccount },
            [Attr.OperatingSystem] = null,
            [Attr.OperatingSystemVersion] = null,
            [Attr.DnsHostName] = null,
        });
        var appServer = AddComputer("SRV-APP01", serversOu, new Dictionary<string, object?>
        {
            [Attr.UserAccountControl] = (int)(UserAccountControl.WorkstationTrustAccount | UserAccountControl.TrustedForDelegation),
            [Attr.OperatingSystem] = "Windows Server 2022 Standard",
        });
        var webServer = AddComputer("SRV-WEB01", serversOu, new Dictionary<string, object?>
        {
            [Attr.AllowedToActOnBehalfOfOtherIdentity] = new byte[] { 1, 0, 4, 128, 20, 0, 0, 0 },
            [Attr.OperatingSystem] = "Windows Server 2022 Standard",
        });

        // Four more accountants; Dorota alone also holds GG_Kadry_Płace and GG_Serwis_RDP.
        var bozena = AddUser("Bożena", "Gałązka", company.AccountingOu, Person("Księgowość", "Księgowa", "Warszawa", "bozena.galazka"));
        var halina = AddUser("Halina", "Źrebiec", company.AccountingOu, Person("Księgowość", "Księgowa", "Gdańsk", "halina.zrebiec"));
        var krystyna = AddUser("Krystyna", "Ślusarczyk", company.AccountingOu, Person("Księgowość", "Księgowa", "Poznań", "krystyna.slusarczyk"));
        var dorota = AddUser("Dorota", "Łukasiewicz", company.AccountingOu, Person("Księgowość", "Księgowa", "Kraków", "dorota.lukasiewicz"));
        AddMembers(company.GroupAccounting, bozena, halina, krystyna, dorota);
        AddMembers(company.GroupIt, henryk, ewa, wojciech, ignacy, jolanta, stanislaw);

        // Well-known groups of the domain (localized names: recognised by RID, DnsAdmins by its sAMAccountName).
        var domainControllers = AddGroup("Kontrolery domeny", UsersDn, attributes: new Dictionary<string, object?>
        {
            [Attr.ObjectSid] = $"{DomainSid}-{WellKnownRids.DomainControllers}",
            [Attr.AdminCount] = 1,
        });
        var policyCreators = AddGroup("Twórcy-właściciele zasad grupy", UsersDn, members: [company.JanKowalski], attributes: new Dictionary<string, object?>
        {
            [Attr.ObjectSid] = $"{DomainSid}-{WellKnownRids.GroupPolicyCreatorOwners}",
        });
        var dnsAdmins = AddGroup("DnsAdmins", UsersDn, GroupScope.DomainLocal, members: [wojciech]);
        AddMembers(DomainAdminsDn, company.JanKowalski);
        Session.SetValues(company.JanKowalski, Attr.AdminCount, "1");

        // Groups of the company: a nested path to Account Operators, a cycle, a distribution group in Domain Admins.
        var helpdesk = AddGroup("GG_Helpdesk", company.GroupsOu, members: [henryk]);
        var support = AddGroup("GG_Wsparcie_IT", company.GroupsOu, members: [helpdesk]);
        var rdp = AddGroup("GG_Serwis_RDP", company.GroupsOu, members: [ewa, dorota]);
        var payroll = AddGroup("GG_Kadry_Płace", company.GroupsOu, members: [dorota]);
        var erpAdmins = AddGroup("GG_ERP_Administratorzy", company.GroupsOu, members: [ewa], attributes: new Dictionary<string, object?>
        {
            [Attr.Description] = "Uprzywilejowana: administratorzy systemu ERP",
        });
        var cycleA = AddGroup("GG_Cykl_A", company.GroupsOu, members: [company.LukaszWisniewski]);
        var cycleB = AddGroup("GG_Cykl_B", company.GroupsOu, members: [cycleA]);
        AddMembers(cycleA, cycleB);
        var distribution = AddGroup("DL_Administratorzy", company.GroupsOu, security: false, members: [company.PawelLewandowski]);
        AddMembers(DomainAdminsDn, distribution);

        // The built-in groups of CN=Builtin, with their Polish names.
        var administrators = Builtin("Administratorzy", "S-1-5-32-544", DomainAdminsDn);
        var accountOperators = Builtin("Operatorzy kont", "S-1-5-32-548", support);
        var backupOperators = Builtin("Operatorzy kopii zapasowych", "S-1-5-32-551", gmsa);
        var remoteDesktop = Builtin("Użytkownicy pulpitu zdalnego", "S-1-5-32-555", rdp);
        var eventLogReaders = Builtin("Czytelnicy dziennika zdarzeń", "S-1-5-32-573", cycleB);
        var users = Builtin("Użytkownicy", "S-1-5-32-545", authenticatedUsers, DomainUsersDn);

        return new PrivilegedSample(
            serviceOu, serversOu, henryk, ewa, wojciech, ignacy, jolanta, stanislaw, sql, web, gmsa, appServer, webServer,
            bozena, halina, krystyna, dorota, domainControllers, policyCreators, dnsAdmins, helpdesk, support, rdp, payroll, erpAdmins,
            cycleA, cycleB, distribution, administrators, accountOperators, backupOperators, remoteDesktop, eventLogReaders, users,
            authenticatedUsers);
    }

    private string Builtin(string name, string sid, params string[] members) => AddGroup(
        name,
        BuiltinDn,
        GroupScope.BuiltinLocal,
        members: members,
        attributes: new Dictionary<string, object?>
        {
            [Attr.ObjectSid] = sid,
            [Attr.SamAccountName] = name,
            [Attr.IsCriticalSystemObject] = true,
        });
}

/// <summary>DNs of the objects created by <see cref="FakeDirectoryBuilder.AddPrivilegedAccessSample"/>.</summary>
public sealed record PrivilegedSample(
    string ServiceAccountsOu,
    string ServersOu,
    string HenrykZulawski,
    string EwaBlaszczyk,
    string WojciechZabek,
    string IgnacyWroblewski,
    string JolantaCwik,
    string StanislawLecki,
    string SqlService,
    string WebService,
    string Gmsa,
    string AppServer,
    string WebServer,
    string BozenaGalazka,
    string HalinaZrebiec,
    string KrystynaSlusarczyk,
    string DorotaLukasiewicz,
    string DomainControllers,
    string PolicyCreators,
    string DnsAdmins,
    string Helpdesk,
    string Support,
    string RemoteDesktopGroup,
    string Payroll,
    string ErpAdmins,
    string CycleA,
    string CycleB,
    string Distribution,
    string Administrators,
    string AccountOperators,
    string BackupOperators,
    string RemoteDesktopUsers,
    string EventLogReaders,
    string Users,
    string AuthenticatedUsers);
