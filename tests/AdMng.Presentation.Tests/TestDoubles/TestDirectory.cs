using System.Globalization;
using AdMng.Core.ActiveDirectory;
using AdMng.Core.Directory;
using AdMng.Core.Model;
using AdMng.Core.Schema;
using static AdMng.Presentation.Tests.TestDoubles.Obj;

namespace AdMng.Presentation.Tests.TestDoubles;

/// <summary>Small schema with the classes and attributes the tests use (Polish display names included).</summary>
internal static class TestSchema
{
    public static SchemaCatalog Create() => new(
        [
            new AttributeDefinition { LdapName = "cn", Syntax = AttributeSyntax.UnicodeString, IsSingleValued = true, RangeUpper = 64 },
            new AttributeDefinition { LdapName = "name", Syntax = AttributeSyntax.UnicodeString, IsSingleValued = true },
            new AttributeDefinition { LdapName = "description", Syntax = AttributeSyntax.UnicodeString },
            new AttributeDefinition { LdapName = "department", Syntax = AttributeSyntax.UnicodeString, IsSingleValued = true },
            new AttributeDefinition { LdapName = "title", Syntax = AttributeSyntax.UnicodeString, IsSingleValued = true },
            new AttributeDefinition { LdapName = "mail", Syntax = AttributeSyntax.UnicodeString, IsSingleValued = true },
            new AttributeDefinition { LdapName = "sAMAccountName", Syntax = AttributeSyntax.UnicodeString, IsSingleValued = true },
            new AttributeDefinition { LdapName = "userAccountControl", Syntax = AttributeSyntax.Integer, IsSingleValued = true },
            new AttributeDefinition { LdapName = "lastLogonTimestamp", Syntax = AttributeSyntax.LargeInteger, IsSingleValued = true },
            new AttributeDefinition { LdapName = "whenCreated", Syntax = AttributeSyntax.GeneralizedTime, IsSingleValued = true },
            new AttributeDefinition { LdapName = "thumbnailPhoto", Syntax = AttributeSyntax.OctetString, IsSingleValued = true },
            new AttributeDefinition { LdapName = "operatingSystem", Syntax = AttributeSyntax.UnicodeString, IsSingleValued = true },
        ],
        [
            new ClassDefinition { LdapName = "top", MustContain = ["objectClass"], MayContain = ["description", "name", "whenCreated", "cn"] },
            new ClassDefinition { LdapName = "person", SubClassOf = "top", MayContain = ["sn"] },
            new ClassDefinition { LdapName = "organizationalPerson", SubClassOf = "person", MayContain = ["department", "title", "mail"] },
            new ClassDefinition { LdapName = "user", SubClassOf = "organizationalPerson", MayContain = ["sAMAccountName", "userAccountControl", "lastLogonTimestamp", "thumbnailPhoto"] },
            new ClassDefinition { LdapName = "computer", SubClassOf = "user", MayContain = ["operatingSystem"] },
            new ClassDefinition { LdapName = "group", SubClassOf = "top", MayContain = ["member", "sAMAccountName"] },
            new ClassDefinition { LdapName = "contact", SubClassOf = "organizationalPerson" },
        ],
        new Dictionary<string, IReadOnlyDictionary<string, string>>
        {
            ["pl"] = new Dictionary<string, string> { ["department"] = "Dział", ["title"] = "Stanowisko" },
            ["en"] = new Dictionary<string, string> { ["department"] = "Department", ["title"] = "Job Title" },
        });
}

/// <summary>
/// Test domain contoso.test with an ADUC-like structure and Polish names (diacritics intended).
/// </summary>
internal static class TestDirectory
{
    public const string Domain = "DC=contoso,DC=test";
    public const string Users = "CN=Users,DC=contoso,DC=test";
    public const string Firma = "OU=Firma,DC=contoso,DC=test";
    public const string It = "OU=IT,OU=Firma,DC=contoso,DC=test";
    public const string Ksiegowosc = "OU=Księgowość,OU=Firma,DC=contoso,DC=test";
    public const string System = "CN=System,DC=contoso,DC=test";
    public const string Lukasz = "CN=Łukasz Żółć,OU=IT,OU=Firma,DC=contoso,DC=test";
    public const string Ewa = "CN=Ewa Kowalska,OU=IT,OU=Firma,DC=contoso,DC=test";
    public const string Jan = "CN=Jan Nowak,OU=IT,OU=Firma,DC=contoso,DC=test";
    public const string Stary = "CN=Stary Zamek,OU=IT,OU=Firma,DC=contoso,DC=test";
    public const string Workstation = "CN=WS-001,OU=IT,OU=Firma,DC=contoso,DC=test";
    public const string ItGroup = "CN=GG-IT,OU=IT,OU=Firma,DC=contoso,DC=test";
    public const string Helpdesk = "CN=Helpdesk,CN=Users,DC=contoso,DC=test";

    /// <summary>wellKnownObjects of the domain head: the default containers of this test domain.</summary>
    public static readonly object[] WellKnownValues =
    [
        $"B:32:{WellKnownGuids.Users}:{Users}",
        $"B:32:{WellKnownGuids.Computers}:CN=Computers,{Domain}",
        $"B:32:{WellKnownGuids.DomainControllers}:OU=Domain Controllers,{Domain}",
        $"B:32:{WellKnownGuids.System}:{System}",
    ];

    /// <summary>Lockout duration of the test domain: 30 minutes.</summary>
    public static readonly TimeSpan LockoutDuration = TimeSpan.FromMinutes(30);

    public static readonly string[] UserClasses = ["top", "person", "organizationalPerson", "user"];

    public static InMemoryDirectorySession Build(DateTimeOffset now, int bulkUsers = 0)
    {
        var s = new InMemoryDirectorySession();
        s.Put(Create(Domain, A(Attr.ObjectClass, "top", "domain", "domainDNS"), A(Attr.ObjectCategory, "domainDNS"),
            A("lockoutDuration", (-LockoutDuration.Ticks).ToString(CultureInfo.InvariantCulture)),
            A("lockoutThreshold", "5"),
            A("maxPwdAge", (-TimeSpan.FromDays(90).Ticks).ToString(CultureInfo.InvariantCulture)),
            A(WellKnownObjects.WellKnownObjectsAttribute, WellKnownValues)));
        s.Put(Container(Users, "Default container for upgraded user accounts"));
        s.Put(Container("CN=Computers,DC=contoso,DC=test", "Default container for upgraded computer accounts"));
        s.Put(Create("CN=Builtin,DC=contoso,DC=test", A(Attr.ObjectClass, "top", "builtinDomain"), A(Attr.ObjectCategory, "builtinDomain")));
        s.Put(Ou("OU=Domain Controllers,DC=contoso,DC=test"));
        s.Put(Container(System, "Builtin system settings", advanced: true));
        s.Put(Ou(Firma));
        s.Put(Ou(It, "Dział IT"));
        s.Put(Ou(Ksiegowosc, "Księgowość i finanse"));
        s.Put(User(Helpdesk, "helpdesk"));
        s.Put(User(Lukasz, "lukasz.zolc", department: "IT", displayName: "Łukasz Żółć", mail: "lukasz.zolc@contoso.test"));
        s.Put(User(Ewa, "ewa.kowalska", disabled: true, department: "IT"));
        s.Put(User(Jan, "jan.nowak", lockoutTime: now.AddMinutes(-5), department: "IT"));
        s.Put(User(Stary, "stary.zamek", lockoutTime: now.AddHours(-2), department: "IT"));
        s.Put(Computer(Workstation, "WS-001$"));
        s.Put(Group(ItGroup, "GG-IT", [Lukasz, Ewa]));
        s.Put(User("CN=Zofia Ćwik,OU=Księgowość,OU=Firma,DC=contoso,DC=test", "zofia.cwik", department: "Księgowość"));
        if (bulkUsers > 0)
        {
            const string bulk = "OU=Masowe,DC=contoso,DC=test";
            s.Put(Ou(bulk));
            for (var i = 0; i < bulkUsers; i++)
            {
                s.Put(User($"CN=Użytkownik {i:0000},{bulk}", $"user{i:0000}"));
            }
        }

        return s;
    }

    public static DirectoryObject Ou(string dn, string? description = null) => Create(
        dn,
        [A(Attr.ObjectClass, "top", "organizationalUnit"), A(Attr.ObjectCategory, "organizationalUnit"), .. Description(description)]);

    public static DirectoryObject Container(string dn, string? description = null, bool advanced = false) => Create(
        dn,
        [
            A(Attr.ObjectClass, "top", "container"), A(Attr.ObjectCategory, "container"), .. Description(description),
            .. advanced ? new[] { A(Attr.ShowInAdvancedViewOnly, "TRUE") } : [],
        ]);

    public static DirectoryObject User(
        string dn,
        string sam,
        bool disabled = false,
        DateTimeOffset? lockoutTime = null,
        string? department = null,
        string? displayName = null,
        string? mail = null)
    {
        var uac = (int)UserAccountControl.NormalAccount | (disabled ? (int)UserAccountControl.AccountDisable : 0);
        var attrs = new List<(string, object[])>
        {
            A(Attr.ObjectClass, UserClasses),
            A(Attr.ObjectCategory, "person"),
            A(Attr.SamAccountType, SamAccountType.UserObject.ToString(CultureInfo.InvariantCulture)),
            A(Attr.SamAccountName, sam),
            A(Attr.UserAccountControl, uac.ToString(CultureInfo.InvariantCulture)),
            A(Attr.WhenCreated, "20240101120000.0Z"),
        };
        if (lockoutTime is { } locked)
        {
            attrs.Add(A(Attr.LockoutTime, AdConvert.ToFileTime(locked).ToString(CultureInfo.InvariantCulture)));
        }

        if (department is not null)
        {
            attrs.Add(A(Attr.Department, department));
        }

        if (displayName is not null)
        {
            attrs.Add(A(Attr.DisplayName, displayName));
        }

        if (mail is not null)
        {
            attrs.Add(A(Attr.Mail, mail));
        }

        return Create(dn, [.. attrs]);
    }

    public static DirectoryObject Computer(string dn, string sam) => Create(
        dn,
        A(Attr.ObjectClass, "top", "person", "organizationalPerson", "user", "computer"),
        A(Attr.ObjectCategory, "computer"),
        A(Attr.SamAccountType, SamAccountType.MachineAccount.ToString(CultureInfo.InvariantCulture)),
        A(Attr.SamAccountName, sam),
        A(Attr.UserAccountControl, ((int)UserAccountControl.WorkstationTrustAccount).ToString(CultureInfo.InvariantCulture)),
        A(Attr.OperatingSystem, "Windows 11 Enterprise"));

    public static DirectoryObject Group(string dn, string sam, string[] members) => Create(
        dn,
        [
            A(Attr.ObjectClass, "top", "group"), A(Attr.ObjectCategory, "group"), A(Attr.SamAccountName, sam),
            A(Attr.GroupType, GroupTypes.Compose(GroupScope.Global, security: true).ToString(CultureInfo.InvariantCulture)),
            .. members.Length > 0 ? new[] { A(Attr.Member, members) } : [],
        ]);

    /// <summary>Copy of an object with one attribute replaced (empty values remove it).</summary>
    public static DirectoryObject WithAttribute(DirectoryObject obj, string attribute, params object[] values) =>
        obj.With([new AttributeValues(attribute, values)]);

    private static (string, object[])[] Description(string? description) =>
        description is null ? [] : [A(Attr.Description, description)];
}
