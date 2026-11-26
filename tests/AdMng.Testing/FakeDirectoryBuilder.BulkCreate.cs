using System.Globalization;
using AdMng.Core.ActiveDirectory;
using AdMng.Core.Directory;

namespace AdMng.Testing;

/// <summary>
/// The bulk creation sample (F3, docs/dev/phase3-bulk-create.md 2.13, Polish data) on top of <see cref="AddSampleCompany"/>:
/// the target OU <em>Nowi pracownicy</em> under <em>Pracownicy</em> with a child OU <em>Księgowość</em> (a group named so
/// collides on <c>name</c>), the numbered accounts kd201, kd203, kd205 and a deleted kd207, a deleted <em>Jan Kowalski</em>
/// of the OU (<c>lastKnownParent</c>), an external contact holding <c>jan.kowalski@…</c> in <c>proxyAddresses</c>, two
/// <em>Piotr Zieliński</em> (an ambiguous manager), <em>Ewa Wiśniewska</em> (a possible existing account), the option groups
/// (Global, Domain Local, Universal and one with a fine-grained password policy), an OU where users cannot be created, the
/// forest reads (the domain's crossRef, <c>uPNSuffixes</c> contoso.com and firma.pl), the domain policy (<c>minPwdLength</c>
/// 7, complexity) and a RID set on the DC. Works with the default and the bulk-create schema
/// (<see cref="FakeSchema.Create(string, bool)"/>); the SAM rules of the fake are switched on unless asked otherwise.
/// </summary>
public sealed partial class FakeDirectoryBuilder
{
    /// <summary>The UPN suffixes of the sample forest (<c>uPNSuffixes</c> of CN=Partitions).</summary>
    public static IReadOnlyList<string> BulkCreateUpnSuffixes { get; } = ["contoso.com", "firma.pl"];

    /// <summary>
    /// Adds the bulk creation sample (call once, after <see cref="AddSampleCompany"/>; seeds the watchdog infrastructure
    /// when it is missing). The seeding operations are cleared from <see cref="FakeDirectorySession.Operations"/>, so query
    /// budgets count the planner's reads only.
    /// </summary>
    /// <param name="company">The sample company.</param>
    /// <param name="samRules">Switches <see cref="FakeDirectorySession.SamRules"/> on (default).</param>
    public BulkCreateSample AddBulkCreateSample(SampleCompany company, bool samRules = true)
    {
        ArgumentNullException.ThrowIfNull(company);
        if (PdcNtdsSettingsDn is null)
        {
            AddWatchdogInfrastructure();
        }

        var newEmployees = AddOu("Nowi pracownicy", company.EmployeesOu);
        var accountingChild = AddOu("Księgowość", newEmployees);
        var blocked = AddOu("Zablokowana", company.EmployeesOu);
        Session.SetPermissions(blocked, new EffectivePermissions
        {
            IsKnown = true,
            CreatableChildClasses = new HashSet<string>(["contact", "group", "organizationalUnit"], StringComparer.OrdinalIgnoreCase),
        });

        // Numbered accounts of an earlier batch: kd201, kd203, kd205 live, kd207 deleted (in the AD Recycle Bin).
        var kd201 = AddNumberedUser("Adam", "Nowicki", "kd201", newEmployees);
        var kd203 = AddNumberedUser("Beata", "Zając", "kd203", newEmployees);
        var kd205 = AddNumberedUser("Cezary", "Mazur", "kd205", newEmployees);
        var kd207 = AddNumberedUser("Dorota", "Krawczyk", "kd207", newEmployees);
        var deletedKd207 = DeleteForSample(kd207);

        // A deleted "Jan Kowalski" of the target OU: a new "Jan Kowalski" there warns NameInRecycleBin.
        var deletedJanSource = AddUser("Jan", "Kowalski", newEmployees, new Dictionary<string, object?>
        {
            [Attr.SamAccountName] = "jkowalski",
            [Attr.UserPrincipalName] = $"jkowalski@{DomainDnsName}",
        });
        var deletedJan = DeleteForSample(deletedJanSource);

        // An external contact holding the address of Jan Kowalski (contoso.test and contoso.com).
        var contact = AddContact("Jan", "Kowalski (zewnętrzny)", company.CompanyOu, new Dictionary<string, object?>
        {
            [Attr.Sn] = "Kowalski",
            [Attr.DisplayName] = "Jan Kowalski (zewnętrzny)",
            [Attr.Mail] = "jan.kowalski@contoso.com",
            [Attr.ProxyAddresses] = new[] { "SMTP:jan.kowalski@contoso.com", $"smtp:jan.kowalski@{DomainDnsName}" },
        });

        // Two "Piotr Zieliński" (an ambiguous manager) and Ewa Wiśniewska (a possible existing account).
        var piotr1 = AddUser("Piotr", "Zieliński", company.ItOu, Person("IT", "Kierownik projektu", "Warszawa", "piotr.zielinski"));
        var piotr2 = AddUser("Piotr", "Zieliński", company.SalesOu, new Dictionary<string, object?>
        {
            [Attr.SamAccountName] = "pzielinski",
            [Attr.UserPrincipalName] = $"pzielinski@{DomainDnsName}",
            [Attr.Department] = "Sprzedaż",
            [Attr.Mail] = $"pzielinski@{DomainDnsName}",
        });
        var ewa = AddUser("Ewa", "Wiśniewska", company.AccountingOu, new Dictionary<string, object?>
        {
            [Attr.Mail] = $"ewa.wisniewska@{DomainDnsName}",
            [Attr.EmployeeId] = "E-1001",
            [Attr.Mobile] = "+48 600 100 200",
            [Attr.Department] = "Księgowość",
        });

        // The option groups.
        var groupNew = AddGroup("GG_Nowi", company.GroupsOu, GroupScope.Global, security: true);
        var groupPrinters = AddGroup("DL_Drukarki", company.GroupsOu, GroupScope.DomainLocal, security: true);
        var groupProjects = AddGroup("UG_Projekty", company.GroupsOu, GroupScope.Universal, security: true);
        var groupPrivileged = AddGroup("GG_Uprzywilejowani", company.GroupsOu, GroupScope.Global, security: true);
        var pso = AddPasswordSettings("PSO_Uprzywilejowani", 10, TimeSpan.FromMinutes(30), [groupPrivileged]);
        Session.SetValues(groupPrivileged, "msDS-PSOApplied", pso);

        // The forest: the domain's crossRef (systemFlags 3) and the UPN suffixes; the domain policy; the RID set of the DC.
        var crossRef = Seed($"CN={DomainDnsName.Split('.')[0].ToUpperInvariant()},{PartitionsDn}", new Dictionary<string, object?>
        {
            [Attr.ObjectClass] = new[] { "top", "crossRef" },
            ["systemFlags"] = 3,
            ["dnsRoot"] = DomainDnsName,
            ["nCName"] = DomainDn,
        }, null);
        Session.SetValues(PartitionsDn, "uPNSuffixes", [.. BulkCreateUpnSuffixes]);
        Session.SetValues(DomainDn, "minPwdLength", "7");
        Session.SetValues(DomainDn, "pwdProperties", "1");
        var ridSet = Seed($"CN=RID Set,{DomainControllerDn}", new Dictionary<string, object?>
        {
            [Attr.ObjectClass] = new[] { "top", "rIDSet" },
            ["rIDAllocationPool"] = RidPool(1600, 2099).ToString(CultureInfo.InvariantCulture),
            ["rIDNextRID"] = 1650,
        }, null);
        Session.SetValues(DomainControllerDn, "rIDSetReferences", ridSet);

        Session.SamRules = samRules;
        Session.ClearOperations();
        return new BulkCreateSample(
            company, newEmployees, accountingChild, blocked,
            kd201, kd203, kd205, deletedKd207, deletedJan, contact, piotr1, piotr2, ewa,
            groupNew, groupPrinters, groupProjects, groupPrivileged, pso,
            crossRef, ridSet);
    }

    /// <summary><c>rIDAllocationPool</c>: the last RID in the high 32 bits, the first in the low 32 bits.</summary>
    public static long RidPool(int first, int last) => ((long)last << 32) | (uint)first;

    private string AddNumberedUser(string givenName, string surname, string login, string parentDn) =>
        AddUser(givenName, surname, parentDn, new Dictionary<string, object?>
        {
            [Attr.SamAccountName] = login,
            [Attr.UserPrincipalName] = $"{login}@contoso.com",
            [Attr.Mail] = $"{login}@contoso.com",
        });

    /// <summary>Deletes a seeded object like the directory does (Recycle Bin: a deleted object with lastKnownParent).</summary>
    private string DeleteForSample(string distinguishedName)
    {
        var before = Session.DeletedDistinguishedNames.ToHashSet(StringComparer.OrdinalIgnoreCase);
        // On the thread pool: the fake yields, and a test's synchronization context must not be blocked by the wait.
        Task.Run(() => Session.DeleteAsync(distinguishedName, subtree: false, CancellationToken.None)).GetAwaiter().GetResult();
        return Session.DeletedDistinguishedNames.Single(dn => !before.Contains(dn));
    }
}

/// <summary>The DNs of <see cref="FakeDirectoryBuilder.AddBulkCreateSample"/> (deleted objects by their current DN in CN=Deleted Objects).</summary>
public sealed record BulkCreateSample(
    SampleCompany Company,
    string NewEmployeesOu,
    string AccountingChildOu,
    string BlockedOu,
    string Kd201,
    string Kd203,
    string Kd205,
    string DeletedKd207,
    string DeletedJanKowalski,
    string ExternalContact,
    string PiotrZielinski1,
    string PiotrZielinski2,
    string EwaWisniewska,
    string GroupNew,
    string GroupDomainLocal,
    string GroupUniversal,
    string GroupWithPso,
    string PasswordSettings,
    string DomainCrossRef,
    string RidSet)
{
    /// <summary>The OU names only – DNs are directory data.</summary>
    public override string ToString() => "Bulk create sample (Nowi pracownicy)";
}
