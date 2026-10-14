using AdMng.Watchdog.Contracts.Events;

namespace AdMng.Watchdog.Testing.Client;

/// <summary>
/// Actors and directory objects of the Polish sample data (domain contoso.test, the OU layout of the AdMng.Testing
/// sample company: OU=Firma › Pracownicy › IT / Księgowość / Sprzedaż, Komputery, Grupy).
/// </summary>
public static class SamplePrincipals
{
    public const string Domain = "contoso.test";
    public const string DomainDn = "DC=contoso,DC=test";
    public const string DomainSid = "S-1-5-21-1004336348-1177238915-682003330";

    public const string ItOu = "OU=IT,OU=Pracownicy,OU=Firma," + DomainDn;
    public const string AccountingOu = "OU=Księgowość,OU=Pracownicy,OU=Firma," + DomainDn;
    public const string SalesOu = "OU=Sprzedaż,OU=Pracownicy,OU=Firma," + DomainDn;
    public const string ComputersOu = "OU=Komputery,OU=Firma," + DomainDn;
    public const string GroupsOu = "OU=Grupy,OU=Firma," + DomainDn;
    public const string ServiceAccountsOu = "OU=Konta serwisowe,OU=Firma," + DomainDn;
    public const string DomainControllersOu = "OU=Domain Controllers," + DomainDn;

    public static PrincipalRef JanKowalski { get; } = Principal("jan.kowalski", "Jan Kowalski", 1105, "CN=Jan Kowalski," + ItOu);

    public static PrincipalRef AnnaNowak { get; } = Principal("anna.nowak", "Anna Nowak", 1121, "CN=Anna Nowak," + ItOu);

    public static PrincipalRef AdminKowalski { get; } =
        Principal("adm.kowalski", "Jan Kowalski (konto administracyjne)", 1106, "CN=adm.kowalski,OU=Administratorzy,OU=Firma," + DomainDn);

    public static PrincipalRef Administrator { get; } = Principal("Administrator", "Administrator", 500, "CN=Administrator,CN=Users," + DomainDn);

    public static PrincipalRef HrSync { get; } = Principal("svc-hr-sync", "Synchronizacja kadr", 1150, "CN=svc-hr-sync," + ServiceAccountsOu);

    public static PrincipalRef MarekZajac { get; } = Principal("marek.zajac", "Marek Zając", 1188, "CN=Marek Zając," + SalesOu);

    public static PrincipalRef GrazynaDabrowska { get; } =
        Principal("grazyna.dabrowska", "Grażyna Dąbrowska", 1112, "CN=Grażyna Dąbrowska," + AccountingOu);

    public static PrincipalRef LocalSystem { get; } = new() { Sid = "S-1-5-18", Account = @"NT AUTHORITY\SYSTEM", DisplayName = "SYSTEM" };

    public static ObjectRef AgnieszkaZielinska { get; } = User("Agnieszka Zielińska", "agnieszka.zielinska", 1190, ItOu, "4a8e1f42-3c51-4f0e-9b7a-2d1c6e5f8a01");

    public static ObjectRef LukaszWisniewski { get; } = User("Łukasz Wiśniewski", "lukasz.wisniewski", 1107, ItOu, "4a8e1f42-3c51-4f0e-9b7a-2d1c6e5f8a02");

    public static ObjectRef MalgorzataWojcik { get; } = User("Małgorzata Wójcik", "malgorzata.wojcik", 1110, AccountingOu, "4a8e1f42-3c51-4f0e-9b7a-2d1c6e5f8a03");

    public static ObjectRef Grazyna { get; } = User("Grażyna Dąbrowska", "grazyna.dabrowska", 1112, AccountingOu, "4a8e1f42-3c51-4f0e-9b7a-2d1c6e5f8a04");

    public static ObjectRef ZbigniewKazmierczak { get; } = User("Zbigniew Kaźmierczak", "zbigniew.kazmierczak", 1113, AccountingOu, "4a8e1f42-3c51-4f0e-9b7a-2d1c6e5f8a05");

    public static ObjectRef PawelLewandowski { get; } = User("Paweł Lewandowski", "pawel.lewandowski", 1114, SalesOu, "4a8e1f42-3c51-4f0e-9b7a-2d1c6e5f8a06");

    public static ObjectRef ZanetaSzymanska { get; } = User("Żaneta Szymańska", "zaneta.szymanska", 1115, SalesOu, "4a8e1f42-3c51-4f0e-9b7a-2d1c6e5f8a07");

    public static ObjectRef BartlomiejKozlowski { get; } = User("Bartłomiej Kozłowski", "bartlomiej.kozlowski", 1116, SalesOu, "4a8e1f42-3c51-4f0e-9b7a-2d1c6e5f8a08");

    public static ObjectRef AdminAccount { get; } = new()
    {
        ObjectGuid = Guid.Parse("4a8e1f42-3c51-4f0e-9b7a-2d1c6e5f8a09"),
        Dn = AdminKowalski.Dn!,
        Name = "adm.kowalski",
        DisplayName = AdminKowalski.DisplayName,
        SamAccountName = "adm.kowalski",
        Sid = AdminKowalski.Sid,
        Kind = WatchObjectKind.User,
    };

    public static ObjectRef SqlService { get; } = new()
    {
        ObjectGuid = Guid.Parse("4a8e1f42-3c51-4f0e-9b7a-2d1c6e5f8a0a"),
        Dn = "CN=svc-sql," + ServiceAccountsOu,
        Name = "svc-sql",
        DisplayName = "Konto usługi SQL Server",
        SamAccountName = "svc-sql",
        Sid = DomainSid + "-1160",
        Kind = WatchObjectKind.User,
    };

    public static ObjectRef BackupService { get; } = new()
    {
        ObjectGuid = Guid.Parse("4a8e1f42-3c51-4f0e-9b7a-2d1c6e5f8a0b"),
        Dn = "CN=svc-kopie,OU=Konta serwisowe,OU=Firma," + DomainDn,
        Name = "svc-kopie",
        DisplayName = "Kopie zapasowe",
        SamAccountName = "svc-kopie",
        Sid = DomainSid + "-1161",
        Kind = WatchObjectKind.User,
    };

    public static ObjectRef Workstation { get; } = Computer("WAW-PC001", ComputersOu, 1120, "4a8e1f42-3c51-4f0e-9b7a-2d1c6e5f8b01");

    public static ObjectRef Laptop { get; } = Computer("KRK-LT001", ComputersOu, 1122, "4a8e1f42-3c51-4f0e-9b7a-2d1c6e5f8b02");

    public static ObjectRef AppServer { get; } = Computer("WAW-SRV-APP01", ComputersOu, 1123, "4a8e1f42-3c51-4f0e-9b7a-2d1c6e5f8b03");

    public static ObjectRef NewComputer { get; } = Computer("KTW-PC007", "CN=Computers," + DomainDn, 1195, "4a8e1f42-3c51-4f0e-9b7a-2d1c6e5f8b04");

    public static ObjectRef Dc02 { get; } = new()
    {
        ObjectGuid = Guid.Parse("4a8e1f42-3c51-4f0e-9b7a-2d1c6e5f8c02"),
        Dn = "CN=DC02," + DomainControllersOu,
        Name = "DC02",
        SamAccountName = "DC02$",
        Sid = DomainSid + "-1002",
        Kind = WatchObjectKind.DomainController,
    };

    public static ObjectRef Dc03 { get; } = new()
    {
        ObjectGuid = Guid.Parse("4a8e1f42-3c51-4f0e-9b7a-2d1c6e5f8c03"),
        Dn = "CN=DC03," + DomainControllersOu,
        Name = "DC03",
        SamAccountName = "DC03$",
        Sid = DomainSid + "-1003",
        Kind = WatchObjectKind.DomainController,
    };

    public static ObjectRef DomainAdmins { get; } = Group("Domain Admins", "CN=Users," + DomainDn, DomainSid + "-512", "4a8e1f42-3c51-4f0e-9b7a-2d1c6e5f8d01");

    public static ObjectRef ProtectedUsers { get; } = Group("Protected Users", "CN=Users," + DomainDn, DomainSid + "-525", "4a8e1f42-3c51-4f0e-9b7a-2d1c6e5f8d02");

    public static ObjectRef ServerAdmins { get; } = Group("GG_Administratorzy_Serwerów", GroupsOu, DomainSid + "-1170", "4a8e1f42-3c51-4f0e-9b7a-2d1c6e5f8d03");

    public static ObjectRef SalesGroup { get; } = Group("GG_Sprzedaż_Krajowa", GroupsOu, DomainSid + "-1171", "4a8e1f42-3c51-4f0e-9b7a-2d1c6e5f8d04");

    public static ObjectRef DomainHead { get; } = new()
    {
        ObjectGuid = Guid.Parse("4a8e1f42-3c51-4f0e-9b7a-2d1c6e5f8e01"),
        Dn = DomainDn,
        Name = "contoso",
        Sid = DomainSid,
        Kind = WatchObjectKind.Domain,
    };

    public static ObjectRef ComputersOuRef { get; } = new()
    {
        ObjectGuid = Guid.Parse("4a8e1f42-3c51-4f0e-9b7a-2d1c6e5f8e02"),
        Dn = ComputersOu,
        Name = "Komputery",
        Kind = WatchObjectKind.OrganizationalUnit,
    };

    /// <summary>The watchdog itself (health events, configuration changes).</summary>
    public static ObjectRef WatchdogService { get; } = new() { Dn = string.Empty, Name = "AD Watchdog", Kind = WatchObjectKind.Watchdog };

    /// <summary>As <paramref name="obj"/> appears as an actor (e.g. a user changing her own password).</summary>
    public static PrincipalRef AsPrincipal(ObjectRef obj)
    {
        ArgumentNullException.ThrowIfNull(obj);
        return new PrincipalRef { Sid = obj.Sid, Account = $@"CONTOSO\{obj.SamAccountName}", DisplayName = obj.DisplayName ?? obj.Name, Dn = obj.Dn };
    }

    private static PrincipalRef Principal(string sam, string displayName, int rid, string dn) => new()
    {
        Sid = $"{DomainSid}-{rid}",
        Account = $@"CONTOSO\{sam}",
        DisplayName = displayName,
        Dn = dn,
    };

    private static ObjectRef User(string name, string sam, int rid, string parent, string guid) => new()
    {
        ObjectGuid = Guid.Parse(guid),
        Dn = $"CN={name},{parent}",
        Name = name,
        DisplayName = name,
        SamAccountName = sam,
        Sid = $"{DomainSid}-{rid}",
        Kind = WatchObjectKind.User,
    };

    private static ObjectRef Computer(string name, string parent, int rid, string guid) => new()
    {
        ObjectGuid = Guid.Parse(guid),
        Dn = $"CN={name},{parent}",
        Name = name,
        SamAccountName = name + "$",
        Sid = $"{DomainSid}-{rid}",
        Kind = WatchObjectKind.Computer,
    };

    private static ObjectRef Group(string name, string parent, string sid, string guid) => new()
    {
        ObjectGuid = Guid.Parse(guid),
        Dn = $"CN={name},{parent}",
        Name = name,
        SamAccountName = name,
        Sid = sid,
        Kind = WatchObjectKind.Group,
    };
}
