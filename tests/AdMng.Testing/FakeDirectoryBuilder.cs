using System.Globalization;
using AdMng.Core.ActiveDirectory;
using AdMng.Core.Directory;
using AdMng.Core.Ldap;
using AdMng.Core.Templates;

namespace AdMng.Testing;

/// <summary>
/// Builds a <see cref="FakeDirectorySession"/> with the structure every AD domain has (domain root, CN=Users,
/// CN=Computers, OU=Domain Controllers, CN=Builtin, Administrator, Guest, krbtgt, Domain Admins/Users/Computers and
/// a domain controller account) plus helpers to add OUs, users, computers, groups and contacts.
/// </summary>
/// <remarks>
/// Extra attributes passed to the helpers override the defaults; a null value removes a default.
/// Values may be strings, string arrays, byte[], numbers, booleans or DateTimeOffset.
/// </remarks>
public sealed partial class FakeDirectoryBuilder
{
    public const string DefaultDomain = "contoso.test";

    /// <summary>Default pwdLastSet of seeded accounts (a fixed point in time, independent of the clock).</summary>
    public static readonly DateTimeOffset DefaultPasswordLastSet = new(2026, 9, 1, 8, 0, 0, TimeSpan.Zero);

    private static readonly TemplateEngine Names = new();
    private int _nextRid = 1103;

    public FakeDirectoryBuilder(string domainDnsName = DefaultDomain, TimeProvider? timeProvider = null, SessionInfo? info = null)
    {
        info ??= FakeDirectorySession.CreateInfo(domainDnsName);
        Session = new FakeDirectorySession(info, FakeSchema.Create(info.SchemaNamingContext), timeProvider);
        DomainDnsName = info.DomainDnsName;
        DomainDn = info.DefaultNamingContext;
        SeedWellKnownObjects();
    }

    public FakeDirectorySession Session { get; }

    public string DomainDnsName { get; }

    public string DomainDn { get; }

    public string DomainSid => Session.DomainSid;

    public string UsersDn => $"CN=Users,{DomainDn}";

    public string ComputersDn => $"CN=Computers,{DomainDn}";

    public string DomainControllersDn => $"OU=Domain Controllers,{DomainDn}";

    public string BuiltinDn => $"CN=Builtin,{DomainDn}";

    public string AdministratorDn => $"CN=Administrator,{UsersDn}";

    public string GuestDn => $"CN=Guest,{UsersDn}";

    public string KrbtgtDn => $"CN=krbtgt,{UsersDn}";

    public string DomainAdminsDn => $"CN=Domain Admins,{UsersDn}";

    public string DomainUsersDn => $"CN=Domain Users,{UsersDn}";

    public string DomainComputersDn => $"CN=Domain Computers,{UsersDn}";

    public string DomainControllerDn => $"CN=DC01,{DomainControllersDn}";

    public FakeDirectorySession Build() => Session;

    /// <summary>Allocates the next free domain SID (S-1-5-21-...-RID).</summary>
    public string NextSid() => $"{DomainSid}-{_nextRid++}";

    /// <summary>
    /// Points a well-known GUID of the domain head (<c>wellKnownObjects</c>, or <c>otherWellKnownObjects</c> for
    /// Managed Service Accounts and Keys) at <paramref name="distinguishedName"/> - what <c>redirusr</c> /
    /// <c>redircmp</c> do; null removes the entry.
    /// </summary>
    public void SetWellKnownContainer(WellKnownContainer container, string? distinguishedName)
    {
        var attribute = container is WellKnownContainer.ManagedServiceAccounts or WellKnownContainer.Keys
            ? WellKnownObjects.OtherWellKnownObjectsAttribute
            : WellKnownObjects.WellKnownObjectsAttribute;
        var guid = WellKnownGuids.Of(container);
        var values = Session.GetValues(DomainDn, attribute).OfType<string>()
            .Where(v => !(WellKnownObjects.TryParseValue(v, out var hex, out _) && hex == guid))
            .ToList();
        if (distinguishedName is not null)
        {
            values.Add($"B:32:{guid}:{distinguishedName}");
        }

        Session.SeedAttributes(DomainDn, new Dictionary<string, object?> { [attribute] = values.ToArray() });
    }

    public string AddOu(string name, string? parentDn = null, IReadOnlyDictionary<string, object?>? attributes = null)
    {
        var dn = Dn("OU", name, parentDn);
        return Seed(dn, new Dictionary<string, object?>
        {
            [Attr.ObjectClass] = new[] { "top", ObjectClasses.OrganizationalUnit },
        }, attributes);
    }

    public string AddContainer(string name, string? parentDn = null, IReadOnlyDictionary<string, object?>? attributes = null)
    {
        var dn = Dn("CN", name, parentDn);
        return Seed(dn, new Dictionary<string, object?>
        {
            [Attr.ObjectClass] = new[] { "top", ObjectClasses.Container },
        }, attributes);
    }

    /// <summary>
    /// Adds an enabled user "givenName surname" with sAMAccountName "givenname.surname" (no diacritics, max 20),
    /// UPN, displayName, primary group Domain Users and a password set on <see cref="DefaultPasswordLastSet"/>.
    /// </summary>
    public string AddUser(string givenName, string surname, string? parentDn = null, IReadOnlyDictionary<string, object?>? attributes = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(givenName);
        ArgumentException.ThrowIfNullOrWhiteSpace(surname);
        var fullName = $"{givenName} {surname}";
        var dn = Dn("CN", fullName, parentDn ?? UsersDn);
        var sam = Names.Render("{givenName|sam|lower}.{sn|sam|lower}", new()
        {
            Attribute = a => a == "givenName" ? [givenName] : [surname],
        });
        sam = sam.Length > TemplateEngine.MaxSamAccountNameLength ? sam[..TemplateEngine.MaxSamAccountNameLength].TrimEnd('.') : sam;
        return Seed(dn, new Dictionary<string, object?>
        {
            [Attr.ObjectClass] = new[] { "top", "person", "organizationalPerson", ObjectClasses.User },
            [Attr.GivenName] = givenName,
            [Attr.Sn] = surname,
            [Attr.DisplayName] = fullName,
            [Attr.SamAccountName] = sam,
            [Attr.UserPrincipalName] = $"{sam}@{DomainDnsName}",
            [Attr.UserAccountControl] = (int)UserAccountControl.NormalAccount,
            [Attr.PwdLastSet] = DefaultPasswordLastSet,
            [Attr.AccountExpires] = AdConvert.FileTimeNever,
            [Attr.PrimaryGroupId] = WellKnownRids.DomainUsers,
            [Attr.ObjectSid] = NextSid(),
            [Attr.SamAccountType] = SamAccountType.UserObject,
        }, attributes);
    }

    /// <summary>Adds a workstation account NAME$ with a DNS host name and Windows 11.</summary>
    public string AddComputer(string name, string? parentDn = null, IReadOnlyDictionary<string, object?>? attributes = null)
    {
        var dn = Dn("CN", name, parentDn ?? ComputersDn);
        return Seed(dn, new Dictionary<string, object?>
        {
            [Attr.ObjectClass] = new[] { "top", "person", "organizationalPerson", ObjectClasses.User, ObjectClasses.Computer },
            [Attr.SamAccountName] = name.ToUpperInvariant() + "$",
            [Attr.DnsHostName] = $"{name.ToLowerInvariant()}.{DomainDnsName}",
            [Attr.UserAccountControl] = (int)UserAccountControl.WorkstationTrustAccount,
            [Attr.OperatingSystem] = "Windows 11 Enterprise",
            [Attr.OperatingSystemVersion] = "10.0 (26100)",
            [Attr.PwdLastSet] = DefaultPasswordLastSet,
            [Attr.AccountExpires] = AdConvert.FileTimeNever,
            [Attr.PrimaryGroupId] = WellKnownRids.DomainComputers,
            [Attr.ObjectSid] = NextSid(),
            [Attr.SamAccountType] = SamAccountType.MachineAccount,
        }, attributes);
    }

    public string AddGroup(
        string name,
        string? parentDn = null,
        GroupScope scope = GroupScope.Global,
        bool security = true,
        IEnumerable<string>? members = null,
        IReadOnlyDictionary<string, object?>? attributes = null)
    {
        var dn = Dn("CN", name, parentDn ?? UsersDn);
        var groupType = GroupTypes.Compose(scope, security);
        var samAccountType = scope is GroupScope.DomainLocal or GroupScope.BuiltinLocal
            ? (security ? SamAccountType.AliasObject : SamAccountType.NonSecurityAliasObject)
            : (security ? SamAccountType.GroupObject : SamAccountType.NonSecurityGroupObject);
        var memberList = members?.ToArray() ?? [];
        return Seed(dn, new Dictionary<string, object?>
        {
            [Attr.ObjectClass] = new[] { "top", ObjectClasses.Group },
            [Attr.SamAccountName] = name,
            [Attr.GroupType] = groupType,
            [Attr.ObjectSid] = NextSid(),
            [Attr.SamAccountType] = samAccountType,
            [Attr.Member] = memberList.Length > 0 ? memberList : null,
        }, attributes);
    }

    public string AddContact(string givenName, string surname, string? parentDn = null, IReadOnlyDictionary<string, object?>? attributes = null)
    {
        var fullName = $"{givenName} {surname}";
        var dn = Dn("CN", fullName, parentDn ?? UsersDn);
        return Seed(dn, new Dictionary<string, object?>
        {
            [Attr.ObjectClass] = new[] { "top", "person", "organizationalPerson", ObjectClasses.Contact },
            [Attr.GivenName] = givenName,
            [Attr.Sn] = surname,
            [Attr.DisplayName] = fullName,
        }, attributes);
    }

    /// <summary>Adds members to a group (raw, like an existing state of the directory).</summary>
    public void AddMembers(string groupDn, params string[] memberDns)
    {
        var current = Session.GetValues(groupDn, Attr.Member);
        Session.SetValues(groupDn, Attr.Member, [.. current.Concat(memberDns).Distinct(StringComparer.OrdinalIgnoreCase)]);
    }

    /// <summary>
    /// A small Polish company: OU=Firma with OU=Pracownicy (IT, Księgowość, Sprzedaż), OU=Komputery and OU=Grupy,
    /// eight users (one disabled, one locked out, one with "password never expires"), three computers and groups.
    /// </summary>
    public SampleCompany AddSampleCompany()
    {
        var company = AddOu("Firma");
        var employees = AddOu("Pracownicy", company);
        var it = AddOu("IT", employees);
        var accounting = AddOu("Księgowość", employees);
        var sales = AddOu("Sprzedaż", employees);
        var computers = AddOu("Komputery", company);
        var groups = AddOu("Grupy", company);

        var jan = AddUser("Jan", "Kowalski", it, Person("IT", "Administrator systemów", "Warszawa", "jan.kowalski"));
        var lukasz = AddUser("Łukasz", "Wiśniewski", it, Person("IT", "Programista", "Kraków", "lukasz.wisniewski"));
        var malgorzata = AddUser("Małgorzata", "Wójcik", accounting, Person("Księgowość", "Główna księgowa", "Warszawa", "malgorzata.wojcik"));
        var grazyna = AddUser("Grażyna", "Dąbrowska", accounting, Person("Księgowość", "Księgowa", "Gdańsk", "grazyna.dabrowska",
            (Attr.UserAccountControl, (int)(UserAccountControl.NormalAccount | UserAccountControl.DontExpirePassword))));
        var zbigniew = AddUser("Zbigniew", "Kaźmierczak", accounting, Person("Księgowość", "Specjalista ds. płac", "Poznań", "zbigniew.kazmierczak",
            (Attr.UserAccountControl, (int)(UserAccountControl.NormalAccount | UserAccountControl.AccountDisable))));
        var pawel = AddUser("Paweł", "Lewandowski", sales, Person("Sprzedaż", "Dyrektor sprzedaży", "Warszawa", "pawel.lewandowski"));
        var zaneta = AddUser("Żaneta", "Szymańska", sales, Person("Sprzedaż", "Handlowiec", "Wrocław", "zaneta.szymanska",
            (Attr.LockoutTime, AdConvert.ToFileTime(new DateTimeOffset(2026, 10, 13, 7, 30, 0, TimeSpan.Zero)).ToString(CultureInfo.InvariantCulture))));
        var bartlomiej = AddUser("Bartłomiej", "Kozłowski", sales, Person("Sprzedaż", "Key Account Manager", "Łódź", "bartlomiej.kozlowski"));

        var pc1 = AddComputer("WAW-PC001", computers, new Dictionary<string, object?> { [Attr.Location] = "Warszawa, pokój 101" });
        var pc2 = AddComputer("WAW-PC002", computers, new Dictionary<string, object?> { [Attr.Location] = "Warszawa, pokój 102" });
        var laptop = AddComputer("KRK-LT001", computers, new Dictionary<string, object?> { [Attr.Location] = "Kraków" });

        var groupIt = AddGroup("GG_IT", groups, members: [jan, lukasz]);
        var groupAccounting = AddGroup("GG_Ksiegowosc", groups, members: [malgorzata, grazyna, zbigniew]);
        var groupSales = AddGroup("GG_Sprzedaz", groups, members: [pawel, zaneta, bartlomiej]);
        var groupAll = AddGroup("DL_Wszyscy", groups, GroupScope.DomainLocal, security: false, members: [groupIt, groupAccounting, groupSales]);

        Session.SetValues(lukasz, Attr.Manager, jan);
        Session.SetValues(zaneta, Attr.Manager, pawel);
        Session.SetValues(bartlomiej, Attr.Manager, pawel);

        return new SampleCompany(
            company, employees, it, accounting, sales, computers, groups,
            jan, lukasz, malgorzata, grazyna, zbigniew, pawel, zaneta, bartlomiej,
            pc1, pc2, laptop,
            groupIt, groupAccounting, groupSales, groupAll);
    }

    private Dictionary<string, object?> Person(string department, string title, string city, string mailbox, params (string Attribute, object? Value)[] extra)
    {
        var attributes = new Dictionary<string, object?>
        {
            [Attr.Department] = department,
            [Attr.Title] = title,
            [Attr.City] = city,
            [Attr.Company] = "Contoso Sp. z o.o.",
            [Attr.Mail] = $"{mailbox}@{DomainDnsName}",
        };
        foreach (var (attribute, value) in extra)
        {
            attributes[attribute] = value;
        }

        return attributes;
    }

    private string Dn(string type, string name, string? parentDn) =>
        DistinguishedName.Parse(parentDn ?? DomainDn).Child(type, name).ToString();

    private string Seed(string dn, Dictionary<string, object?> defaults, IReadOnlyDictionary<string, object?>? overrides)
    {
        if (overrides is not null)
        {
            foreach (var (attribute, value) in overrides)
            {
                var existing = defaults.Keys.FirstOrDefault(k => string.Equals(k, attribute, StringComparison.OrdinalIgnoreCase));
                defaults[existing ?? attribute] = value;
            }
        }

        return Session.Seed(dn, defaults);
    }

    private void SeedWellKnownObjects()
    {
        var critical = new Dictionary<string, object?> { [Attr.IsCriticalSystemObject] = true };
        Session.Seed(DomainDn, new Dictionary<string, object?>
        {
            [Attr.ObjectClass] = new[] { "top", "domain", ObjectClasses.DomainDns },
            [Attr.ObjectSid] = DomainSid,
            [Attr.IsCriticalSystemObject] = true,

            // The default containers of the seeded structure, like every AD domain head lists them.
            [WellKnownObjects.WellKnownObjectsAttribute] = new[]
            {
                $"B:32:{WellKnownGuids.Users}:{UsersDn}",
                $"B:32:{WellKnownGuids.Computers}:{ComputersDn}",
                $"B:32:{WellKnownGuids.DomainControllers}:{DomainControllersDn}",
            },
        });
        AddContainer("Users", DomainDn, critical);
        AddContainer("Computers", DomainDn, critical);
        AddOu("Domain Controllers", DomainDn, critical);
        Seed(BuiltinDn, new Dictionary<string, object?> { [Attr.ObjectClass] = new[] { "top", ObjectClasses.BuiltinDomain } }, critical);

        AddBuiltinUser("Administrator", WellKnownRids.Administrator,
            UserAccountControl.NormalAccount | UserAccountControl.DontExpirePassword, adminCount: true);
        AddBuiltinUser("Guest", WellKnownRids.Guest,
            UserAccountControl.NormalAccount | UserAccountControl.AccountDisable | UserAccountControl.DontExpirePassword | UserAccountControl.PasswordNotRequired,
            adminCount: false);
        AddBuiltinUser("krbtgt", WellKnownRids.Krbtgt, UserAccountControl.NormalAccount | UserAccountControl.AccountDisable, adminCount: true);

        AddGroup("Domain Admins", UsersDn, members: [AdministratorDn], attributes: new Dictionary<string, object?>
        {
            [Attr.ObjectSid] = $"{DomainSid}-{WellKnownRids.DomainAdmins}",
            [Attr.AdminCount] = 1,
            [Attr.IsCriticalSystemObject] = true,
        });
        AddGroup("Domain Users", UsersDn, attributes: new Dictionary<string, object?>
        {
            [Attr.ObjectSid] = $"{DomainSid}-{WellKnownRids.DomainUsers}",
            [Attr.IsCriticalSystemObject] = true,
        });
        AddGroup("Domain Computers", UsersDn, attributes: new Dictionary<string, object?>
        {
            [Attr.ObjectSid] = $"{DomainSid}-{WellKnownRids.DomainComputers}",
            [Attr.IsCriticalSystemObject] = true,
        });
        AddComputer("DC01", DomainControllersDn, new Dictionary<string, object?>
        {
            [Attr.UserAccountControl] = (int)(UserAccountControl.ServerTrustAccount | UserAccountControl.TrustedForDelegation),
            [Attr.PrimaryGroupId] = WellKnownRids.DomainControllers,
            [Attr.ObjectSid] = $"{DomainSid}-1000",
            [Attr.OperatingSystem] = "Windows Server 2025 Datacenter",
            [Attr.IsCriticalSystemObject] = true,
        });
    }

    private void AddBuiltinUser(string name, uint rid, UserAccountControl uac, bool adminCount) =>
        Seed(Dn("CN", name, UsersDn), new Dictionary<string, object?>
        {
            [Attr.ObjectClass] = new[] { "top", "person", "organizationalPerson", ObjectClasses.User },
            [Attr.SamAccountName] = name,
            [Attr.UserAccountControl] = (int)uac,
            [Attr.PwdLastSet] = DefaultPasswordLastSet,
            [Attr.AccountExpires] = AdConvert.FileTimeNever,
            [Attr.PrimaryGroupId] = WellKnownRids.DomainUsers,
            [Attr.ObjectSid] = $"{DomainSid}-{rid}",
            [Attr.SamAccountType] = SamAccountType.UserObject,
            [Attr.IsCriticalSystemObject] = true,
            [Attr.AdminCount] = adminCount ? 1 : null,
        }, null);
}

/// <summary>DNs of the objects created by <see cref="FakeDirectoryBuilder.AddSampleCompany"/>.</summary>
public sealed record SampleCompany(
    string CompanyOu,
    string EmployeesOu,
    string ItOu,
    string AccountingOu,
    string SalesOu,
    string ComputersOu,
    string GroupsOu,
    string JanKowalski,
    string LukaszWisniewski,
    string MalgorzataWojcik,
    string GrazynaDabrowska,
    string ZbigniewKazmierczak,
    string PawelLewandowski,
    string ZanetaSzymanska,
    string BartlomiejKozlowski,
    string Computer1,
    string Computer2,
    string Laptop,
    string GroupIt,
    string GroupAccounting,
    string GroupSales,
    string GroupAll);
