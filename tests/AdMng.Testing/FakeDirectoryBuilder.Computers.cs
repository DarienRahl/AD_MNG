using System.Globalization;
using AdMng.Core.ActiveDirectory;

namespace AdMng.Testing;

/// <summary>
/// The computer inventory sample (K1 design note 10) on top of <see cref="AddSampleCompany"/>: DCs (one RODC), servers with
/// and without owners, workstations of the release catalogue's rows, a prestaged account nobody joined, computers in
/// <c>CN=Computers</c> and in wrong OUs, a disabled one, a Linux host; sites with subnets (one invalid) and the DCs'
/// server objects. Reference time: <see cref="ComputerInventorySample.Now"/> (27 October 2026).
/// </summary>
public sealed partial class FakeDirectoryBuilder
{
    /// <summary>Adds the computer inventory sample (call after <see cref="AddSampleCompany"/>).</summary>
    public ComputerInventorySample AddComputerInventorySample(SampleCompany company)
    {
        ArgumentNullException.ThrowIfNull(company);
        var now = ComputerInventorySample.Now;
        string Ft(DateTimeOffset value) => AdConvert.ToFileTime(value).ToString(CultureInfo.InvariantCulture);
        string Gt(DateTimeOffset value) => AdConvert.ToGeneralizedTime(value);
        Dictionary<string, object?> Os(string os, string version, int lastLogonDaysAgo, string? location = null, int createdDaysAgo = 700)
        {
            var attributes = new Dictionary<string, object?>
            {
                [Attr.OperatingSystem] = os,
                [Attr.OperatingSystemVersion] = version,
                [Attr.LastLogonTimestamp] = Ft(now.AddDays(-lastLogonDaysAgo)),
                [Attr.WhenCreated] = Gt(now.AddDays(-createdDaysAgo)),
            };
            if (location is not null)
            {
                attributes[Attr.Location] = location;
            }

            return attributes;
        }

        // Give new objects a descriptor (prestaging writes the join right on it).
        Session.NewObjectSecurityDescriptor ??= _ => DefaultSecurityDescriptor();

        // Domain controllers: DC01 (sample), DC02 and the RODC of Gdańsk.
        Session.SetValues(DomainControllerDn, Attr.DnsHostName, $"dc01.{DomainDnsName}");
        Session.SetValues(DomainControllerDn, Attr.OperatingSystemVersion, "10.0 (26100)");
        Session.SetValues(DomainControllerDn, Attr.LastLogonTimestamp, Ft(now.AddDays(-1)));
        var dc02 = AddComputer("DC02", DomainControllersDn, new Dictionary<string, object?>
        {
            [Attr.UserAccountControl] = (int)(UserAccountControl.ServerTrustAccount | UserAccountControl.TrustedForDelegation),
            [Attr.PrimaryGroupId] = WellKnownRids.DomainControllers,
            [Attr.OperatingSystem] = "Windows Server 2022 Datacenter",
            [Attr.OperatingSystemVersion] = "10.0 (20348)",
            [Attr.LastLogonTimestamp] = Ft(now.AddDays(-2)),
            [Attr.IsCriticalSystemObject] = true,
        });
        var rodc = AddComputer("RODC-GDA01", DomainControllersDn, new Dictionary<string, object?>
        {
            [Attr.UserAccountControl] = (int)(UserAccountControl.WorkstationTrustAccount | UserAccountControl.PartialSecretsAccount),
            [Attr.PrimaryGroupId] = WellKnownRids.ReadOnlyDomainControllers,
            [Attr.OperatingSystem] = "Windows Server 2016 Standard",
            [Attr.OperatingSystemVersion] = "10.0 (14393)",
            [Attr.LastLogonTimestamp] = Ft(now.AddDays(-3)),
        });

        // Servers: with an owner, without one, with a disabled owner; one in the wrong OU.
        var servers = AddOu("Serwery", company.CompanyOu);
        var fileServer = AddComputer("SRV-PLIKI01", servers, Os("Windows Server 2022 Standard", "10.0 (20348)", 1, "Warszawa, serwerownia"));
        Session.SetValues(fileServer, Attr.ManagedBy, company.JanKowalski);
        Session.SetValues(fileServer, Attr.Description, "Serwer plików – księgowość i kadry");
        var appServer = AddComputer("SRV-APP02", servers, Os("Windows Server 2019 Standard", "10.0 (17763)", 2, "Kraków"));
        var sqlServer = AddComputer("SRV-SQL03", servers, Os("Windows Server 2016 Standard", "10.0 (14393)", 5, "Łódź"));
        Session.SetValues(sqlServer, Attr.ManagedBy, company.ZbigniewKazmierczak);
        var oldServer = AddComputer("SRV-STARY04", servers, Os("Windows Server 2012 R2 Standard", "6.3 (9600)", 3, "Gdańsk"));
        Session.SetValues(oldServer, Attr.ManagedBy, company.LukaszWisniewski);
        var misplacedServer = AddComputer("SRV-ZLY05", DomainControllersDn, Os("Windows Server 2025 Standard", "10.0 (26100)", 1));

        // Workstations of the catalogue's rows (OU=Komputery of the sample company).
        var computers = company.ComputersOu;
        var pro24H2 = AddComputer("KRK-PC101", computers, Os("Windows 11 Pro", "10.0 (26100)", 3, "Kraków, pokój 12"));
        var ent23H2 = AddComputer("KRK-PC102", computers, Os("Windows 11 Enterprise", "10.0 (22631)", 4, "Kraków, pokój 14"));
        var win10 = AddComputer("GDA-PC103", computers, Os("Windows 10 Pro", "10.0 (19045)", 6, "Gdańsk, recepcja"));
        var ltsc = AddComputer("GDA-PC104", computers, Os("Windows 10 Enterprise LTSC", "10.0 (19044)", 2, "Gdańsk, magazyn"));
        var pro25H2 = AddComputer("WRO-PC105", computers, Os("Windows 11 Pro", "10.0 (26200)", 1, "Wrocław"));
        var early11 = AddComputer("POZ-PC106", computers, Os("Windows 10 Enterprise", "10.0 (22000)", 8, "Poznań"));
        var win7 = AddComputer("LOD-PC107", computers, Os("Windows 7 Professional", "6.1 (7601)", 200, "Łódź, hala"));
        var inactive = AddComputer("WAW-PC108", computers, Os("Windows 11 Enterprise", "10.0 (26200)", 150, "Warszawa, pokój 210"));
        var linux = AddComputer("LNX-SRV01", servers, Os("Ubuntu 24.04 LTS", "6.8", 1, "Warszawa"));
        Session.SetValues(linux, Attr.OperatingSystemVersion, "6.8");

        // Prestaged, never joined (created by Łukasz with the machine account quota).
        var lukaszSid = Session.GetValues(company.LukaszWisniewski, Attr.ObjectSid)[0];
        var prestaged = AddComputer("PREST-PC201", computers, new Dictionary<string, object?>
        {
            [Attr.OperatingSystem] = null,
            [Attr.OperatingSystemVersion] = null,
            [Attr.DnsHostName] = null,
            [Attr.WhenCreated] = Gt(now.AddDays(-200)),
            [Attr.Description] = "Laptop dla nowej księgowej – Żaneta",
            ["mS-DS-CreatorSID"] = lukaszSid,
        });

        // Joined and never moved out of CN=Computers; disabled one.
        var notMoved = AddComputer("NEW-PC301", ComputersDn, Os("Windows 11 Pro", "10.0 (26200)", 2));
        var disabled = AddComputer("OLD-PC401", computers, Os("Windows 10 Pro", "10.0 (19045)", 400, "Magazyn – do utylizacji"));
        Session.SetValues(disabled, Attr.UserAccountControl, ((int)(UserAccountControl.WorkstationTrustAccount | UserAccountControl.AccountDisable)).ToString(CultureInfo.InvariantCulture));

        // The sample company's computers: logged on recently.
        foreach (var dn in new[] { company.Computer1, company.Computer2, company.Laptop })
        {
            Session.SetValues(dn, Attr.LastLogonTimestamp, Ft(now.AddDays(-3)));
            Session.SetValues(dn, Attr.WhenCreated, Gt(now.AddDays(-500)));
        }

        // Sites, subnets and the DCs' server objects.
        var configuration = Session.Info.ConfigurationNamingContext;
        EnsureContainer(configuration, "configuration");
        var sites = EnsureContainer(Dn("CN", "Sites", configuration), "sitesContainer");
        var subnets = EnsureContainer(Dn("CN", "Subnets", sites), "subnetContainer");
        var warszawa = EnsureContainer(Dn("CN", "Warszawa", sites), "site");
        var krakow = EnsureContainer(Dn("CN", "Kraków", sites), "site");
        void Subnet(string cn, string? site) => Session.Seed(Dn("CN", cn, subnets), new Dictionary<string, object?>
        {
            [Attr.ObjectClass] = new[] { "top", "subnet" },
            [Attr.Cn] = cn,
            ["siteObject"] = site,
        });
        Subnet("10.10.0.0/16", warszawa);
        Subnet("10.20.0.0/16", krakow);
        Subnet("10.20.5.0/24", krakow);
        Subnet("10.99.0.0/33", null);
        void Server(string site, string host)
        {
            var serversDn = EnsureContainer(Dn("CN", "Servers", site), "serversContainer");
            var server = Session.Seed(Dn("CN", host.Split('.')[0].ToUpperInvariant(), serversDn), new Dictionary<string, object?>
            {
                [Attr.ObjectClass] = new[] { "top", "server" },
                [Attr.DnsHostName] = host,
            });
            Session.Seed(Dn("CN", "NTDS Settings", server), new Dictionary<string, object?>
            {
                [Attr.ObjectClass] = new[] { "top", "applicationSettings", "nTDSDSA" },
                [Attr.ObjectCategory] = $"CN=NTDS-DSA,{Session.Info.SchemaNamingContext}",
                ["options"] = "1",
                ["msDS-HasDomainNCs"] = DomainDn,
            });
        }

        Server(warszawa, $"dc01.{DomainDnsName}");
        Server(krakow, $"dc02.{DomainDnsName}");

        return new ComputerInventorySample(
            dc02, rodc, servers, fileServer, appServer, sqlServer, oldServer, misplacedServer, pro24H2, ent23H2, win10, ltsc,
            pro25H2, early11, win7, inactive, linux, prestaged, notMoved, disabled);
    }

    private string EnsureContainer(string dn, string objectClass) =>
        Session.Exists(dn) ? dn : Session.Seed(dn, new Dictionary<string, object?> { [Attr.ObjectClass] = new[] { "top", objectClass } });
}

/// <summary>DNs of <see cref="FakeDirectoryBuilder.AddComputerInventorySample"/>.</summary>
public sealed record ComputerInventorySample(
    string Dc02,
    string Rodc,
    string ServersOu,
    string FileServer,
    string AppServer,
    string SqlServer,
    string OldServer,
    string MisplacedServer,
    string Pro24H2,
    string Enterprise23H2,
    string Windows10,
    string Ltsc2021,
    string Pro25H2,
    string EarlyWindows11,
    string Windows7,
    string Inactive,
    string Linux,
    string Prestaged,
    string NotMoved,
    string Disabled)
{
    /// <summary>Tuesday 27 October 2026, 09:00 UTC (10:00 in Warsaw).</summary>
    public static readonly DateTimeOffset Now = new(2026, 10, 27, 9, 0, 0, TimeSpan.Zero);
}
