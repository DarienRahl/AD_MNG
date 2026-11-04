using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Text;
using AdMng.Core.ActiveDirectory;
using AdMng.Core.Audit;
using AdMng.Core.Batch;
using AdMng.Core.Batch.Planning;
using AdMng.Core.Computers;
using AdMng.Core.Directory;
using AdMng.Core.Ldap;
using AdMng.Core.Model;
using AdMng.Core.Security;
using AdMng.Core.Templates;
using AdMng.Directory.IntegrationTests.Infrastructure;
using AdMng.Presentation.AccountReview;
using AdMng.Presentation.Computers;
using AdMng.Presentation.Errors;
using AdMng.Presentation.Localization;
using AdMng.Testing;

namespace AdMng.Directory.IntegrationTests.Integration;

/// <summary>
/// Computers (K1) against the Samba lab. The lab is shared: every object lives in an OU <c>K1T-…</c> below this run's
/// OU (removed with it) – the seeded lab computers are only read.
/// </summary>
public sealed class ComputerLabTests(LabFixture lab)
{
    private static readonly BatchOptions Options = new() { InitialRetryDelay = TimeSpan.FromMilliseconds(50) };

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ModifySecurity_AddsAndRemovesAcesWithTheSdFlagsControl_AndKeepsTheOwner()
    {
        LabSettings.RequireLab();
        var admin = lab.Admin;
        var ou = await lab.CreateOuAsync(TestNames.Unique("K1T-ACL"));
        var computer = await CreateComputerAsync(ou, "K1TACL" + Suffix());
        var trustee = await lab.CreateUserAsync(ou, "Łukasz Dołączający");
        var trusteeSid = (await admin.GetAsync(trustee, [Attr.ObjectSid], Ct))!.ObjectSid!;
        var ownerBefore = await OwnerAsync(computer);
        var wanted = new AclChange(
            [
                AceSpec.Allow(trusteeSid, 0x100, ExtendedRightIds.ResetPassword),
                AceSpec.Allow(trusteeSid, 0x30, ExtendedRightIds.AccountRestrictions),
                AceSpec.Allow(trusteeSid, 0x08, ExtendedRightIds.ValidatedWriteDnsHostName),
                AceSpec.Allow(trusteeSid, 0x08, ExtendedRightIds.ValidatedWriteServicePrincipalName),
            ],
            []);

        var step = await AclPlanning.PlanAsync(admin, computer, wanted, Ct);
        Assert.NotNull(step);
        Assert.Equal(4, step!.Acl!.Added.Count);
        var plan = Plan(admin, computer, step);
        var audit = new InMemoryAuditLog();
        var result = await new BatchExecutor(audit, TimeProvider.System).ExecuteAsync(admin, plan, Options, null, Ct);

        Assert.Equal(ObjectResultStatus.Succeeded, Assert.Single(result.Objects).Status);
        var aces = await ExplicitAcesAsync(computer);
        Assert.All(wanted.Added, ace => Assert.Contains(aces, a => a.SameAce(ace)));
        Assert.Equal(ownerBefore, await OwnerAsync(computer));
        Assert.Null(await AclPlanning.PlanAsync(admin, computer, wanted, Ct));

        // Rollback removes exactly the four ACEs.
        var rollback = new RollbackPlanner().CreateRollbackPlan(result);
        var undone = await new BatchExecutor(audit, TimeProvider.System).ExecuteAsync(admin, rollback, Options, null, Ct);
        Assert.Equal(1, undone.SucceededCount);
        var after = await ExplicitAcesAsync(computer);
        Assert.DoesNotContain(after, a => string.Equals(a.TrusteeSid, trusteeSid, StringComparison.OrdinalIgnoreCase));
        Assert.Equal(ownerBefore, await OwnerAsync(computer));
    }

    /// <summary>
    /// The inventory query of the review (every computer account, managed service accounts excluded, the facts' attributes)
    /// over the seeded lab computers equals an ldapsearch of the same filter and attributes – the Linux stand-in for
    /// Get-ADComputer – and every account gets a type.
    /// </summary>
    [Fact]
    public async Task Inventory_OfTheLabComputers_EqualsLdapsearch()
    {
        LabSettings.RequireLab();
        Assert.SkipUnless(File.Exists("/usr/bin/ldapsearch"), "ldapsearch (ldap-utils) is not installed.");
        var admin = lab.Admin;
        var scope = LabSettings.CompanyOu;
        var ours = new Dictionary<string, ComputerFacts>(StringComparer.OrdinalIgnoreCase);
        await foreach (var obj in admin.SearchAsync(new DirectorySearch
        {
            BaseDn = scope,
            Filter = ComputerReports.ComputersFilter,
            Attributes = [.. ComputerFacts.Attributes(admin.Schema)],
            PageSize = 500,
        }, Ct))
        {
            ours[DistinguishedName.Parse(obj.DistinguishedName).ToString()] = ComputerFacts.From(obj);
        }

        var theirs = await LdapsearchAsync(scope, ComputerReports.ComputersFilter, [Attr.OperatingSystem, Attr.OperatingSystemVersion, Attr.DnsHostName, Attr.UserAccountControl]);

        Assert.True(ours.Count >= 250, $"{ours.Count} computers in {scope}");
        Assert.Equal(theirs.Keys.Order(StringComparer.OrdinalIgnoreCase), ours.Keys.Order(StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase);
        foreach (var (dn, facts) in ours)
        {
            var entry = theirs[dn];
            Assert.Equal(entry.GetValueOrDefault(Attr.OperatingSystem.ToLowerInvariant()), facts.OperatingSystem);
            Assert.Equal(entry.GetValueOrDefault(Attr.OperatingSystemVersion.ToLowerInvariant()), facts.OperatingSystemVersionText);
            Assert.Equal(entry.GetValueOrDefault(Attr.DnsHostName.ToLowerInvariant()), facts.DnsHostName, StringComparer.OrdinalIgnoreCase);
            Assert.NotEqual(ComputerType.DomainController, facts.Type);
        }

        Assert.Contains(ours.Values, f => f.Type == ComputerType.Workstation);
        Assert.Contains(ours.Values, f => f.Type == ComputerType.Server);

        // The lab DC itself is a domain controller.
        var dcs = new List<ComputerFacts>();
        await foreach (var obj in admin.SearchAsync(new DirectorySearch
        {
            BaseDn = DistinguishedName.Parse(LabSettings.DomainDn).Child("OU", "Domain Controllers").ToString(),
            Filter = ComputerReports.ComputersFilter,
            Attributes = [.. ComputerFacts.Attributes(admin.Schema)],
        }, Ct))
        {
            dcs.Add(ComputerFacts.From(obj));
        }

        Assert.Contains(dcs, f => f.Type == ComputerType.DomainController);
    }

    /// <summary>"Dokładne ostatnie logowanie": lastLogon of seeded computers on every DC of the lab (the session's own).</summary>
    [Fact]
    public async Task ExactLastLogon_IsReadOnTheSessionsDomainController()
    {
        LabSettings.RequireLab();
        var admin = lab.Admin;
        var computers = new List<string>();
        await foreach (var obj in admin.SearchAsync(new DirectorySearch
        {
            BaseDn = LabSettings.CompanyOu,
            Filter = ComputerReports.ComputersFilter,
            Attributes = [Attr.Name],
            SizeLimit = 5,
        }, Ct))
        {
            computers.Add(obj.DistinguishedName);
        }

        var reader = new DomainControllerReader(new LdapDirectoryConnector(), new ErrorTranslator(new ResxLocalizer(CultureInfo.GetCultureInfo("pl-PL"))));
        var reads = await reader.ReadAsync(admin, computers, [Attr.LastLogon], Ct);

        var read = Assert.Single(reads);
        Assert.True(read.Succeeded, read.Error);
        Assert.Equal(admin.Info.Server, read.Server);
        Assert.All(computers, dn => Assert.NotNull(read.Find(dn)));
    }

    /// <summary>
    /// "Ustal lokację": a site and a subnet of this run (K1T-…, 10.250.n.0/24) in the configuration partition, read by
    /// SiteTopology and matched for a computer whose DNS name resolves into the subnet; both are removed afterwards.
    /// </summary>
    [Fact]
    public async Task Site_OfAComputer_IsTheSiteOfTheLongestMatchingSubnet()
    {
        LabSettings.RequireLab();
        var admin = lab.Admin;
        var suffix = Suffix();
        var octet = Random.Shared.Next(1, 255);
        var sites = DistinguishedName.Parse(admin.Info.ConfigurationNamingContext).Child("CN", "Sites");
        var siteName = "K1T-" + suffix;
        var siteDn = sites.Child("CN", siteName).ToString();
        var cidr = string.Create(CultureInfo.InvariantCulture, $"10.250.{octet}.0/24");
        var subnetDn = sites.Child("CN", "Subnets").Child("CN", cidr).ToString();
        var ou = await lab.CreateOuAsync(TestNames.Unique("K1T-SITE"));
        var computer = await CreateComputerAsync(ou, "K1TSITE" + suffix, new Dictionary<string, string> { [Attr.DnsHostName] = $"k1tsite{suffix}.{LabSettings.DomainDns}".ToLowerInvariant() });
        try
        {
            await admin.AddAsync(siteDn, new Dictionary<string, IReadOnlyList<object>> { [Attr.ObjectClass] = ["top", "site"] }, Ct);
            await admin.AddAsync(subnetDn, new Dictionary<string, IReadOnlyList<object>>
            {
                [Attr.ObjectClass] = ["top", "subnet"],
                ["siteObject"] = [siteDn],
                [Attr.Description] = [$"K1T lab test {suffix} – usuwana po teście"],
            }, Ct);

            var topology = await SiteTopology.ReadAsync(admin, Ct);
            Assert.Contains(siteName, topology.Sites);
            var address = IPAddress.Parse(string.Create(CultureInfo.InvariantCulture, $"10.250.{octet}.17"));
            var match = topology.Subnets.Match(address);
            Assert.Equal(cidr, match.Subnet?.Name);
            Assert.Equal(siteName, match.SiteName);

            var resolver = new FakeHostAddressResolver().Add($"k1tsite{suffix}.{LabSettings.DomainDns}".ToLowerInvariant(), address.ToString());
            var facts = ComputerFacts.From((await admin.GetAsync(computer, [.. ComputerFacts.Attributes(admin.Schema)], Ct))!);
            var resolved = await ComputerSites.ResolveAsync(resolver, await ComputerSites.ReadTopologyAsync(admin, Ct), [facts], Ct);
            var site = Assert.Single(resolved).Value;
            Assert.Equal((SiteResolutionState.Resolved, cidr, siteName), (site.State, site.Subnet, site.Site));
        }
        finally
        {
            await DeleteIfPresentAsync(subnetDn);
            await DeleteIfPresentAsync(siteDn);
        }
    }

    /// <summary>
    /// Prestaging (K1.3): the create step and the join right for helpdesk.test in one plan, as "Nowy → Komputer" builds
    /// it; the four ACEs are read back from the new account.
    /// </summary>
    [Fact]
    public async Task Prestage_WithTheJoinRightForHelpdesk_WritesTheFourAces()
    {
        LabSettings.RequireLab();
        var admin = lab.Admin;
        var ou = await lab.CreateOuAsync(TestNames.Unique("K1T-PRESTAGE"));
        var helpdesk = await FindBySamAccountNameAsync("helpdesk.test");
        var helpdeskSid = helpdesk.ObjectSid!;
        var name = "K1TPRE" + Suffix();
        var dn = DistinguishedName.Parse(ou).Child("CN", name).ToString();
        var create = new PlannedChange
        {
            Action = ChangeAction.Create,
            Attributes =
            [
                new(Attr.ObjectClass, AttributeChangeKind.Replace, [], [ObjectClasses.Computer]),
                new(Attr.SamAccountName, AttributeChangeKind.Replace, [], [name + "$"]),
                new(Attr.UserAccountControl, AttributeChangeKind.Replace, [], [((int)UserAccountControl.WorkstationTrustAccount).ToString(CultureInfo.InvariantCulture)]),
                new(Attr.Location, AttributeChangeKind.Replace, [], ["Łódź, pokój 12 – recepcja"]),
            ],
        };
        var grant = AclPlanning.ForNewObject(ComputerJoinRights.Grant(helpdeskSid, @"CONTOSO\helpdesk.test"));

        var result = await new BatchExecutor(new InMemoryAuditLog(), TimeProvider.System).ExecuteAsync(admin, Plan(admin, dn, create, grant), Options, null, Ct);

        Assert.Equal(ObjectResultStatus.Succeeded, Assert.Single(result.Objects).Status);
        var aces = await ExplicitAcesAsync(dn);
        var granted = aces.Where(a => string.Equals(a.TrusteeSid, helpdeskSid, StringComparison.OrdinalIgnoreCase)).ToList();
        Assert.All(ComputerJoinRights.For(helpdeskSid), wanted => Assert.Contains(granted, a => a.SameAce(wanted)));
        Assert.Equal(["Łódź, pokój 12 – recepcja"], (await admin.GetAsync(dn, [Attr.Location], Ct))!.GetStrings(Attr.Location));
    }

    /// <summary>"Resetuj konto": a random 120-character password through LDAPS; pwdLastSet moves, the password is never read back.</summary>
    [Fact]
    public async Task ResetComputerAccount_SetsANewMachinePassword()
    {
        LabSettings.RequireLab();
        var ou = await lab.CreateOuAsync(TestNames.Unique("K1T-RESET"));
        var computer = await CreateComputerAsync(ou, "K1TRST" + Suffix());
        var before = (await lab.Admin.GetAsync(computer, [Attr.PwdLastSet], Ct))!.GetString(Attr.PwdLastSet);
        await using var secure = await LabFixture.ConnectAsync(LabSettings.Tls(LabSettings.AdminUser), LabSettings.AdminPassword, cancellationToken: Ct);
        var plan = await Planner().PlanAsync(secure, new BatchRequest([computer], [new ResetComputerAccountOperation()]) { Title = "Resetuj konto" }, null, null, Ct);
        var step = Assert.Single(Assert.Single(plan.Objects).Changes);
        Assert.Equal(ChangeAction.ResetPassword, step.Action);
        Assert.Equal(ResetComputerAccountOperation.GeneratedLength, step.Password!.Length);
        Assert.Contains(step.Issues, i => i.Code == IssueCodes.ComputerTrustLost);

        var result = await new BatchExecutor(new InMemoryAuditLog(), TimeProvider.System).ExecuteAsync(secure, plan, Options, null, Ct);

        Assert.Equal(1, result.SucceededCount);
        var after = (await lab.Admin.GetAsync(computer, [Attr.PwdLastSet], Ct))!.GetString(Attr.PwdLastSet);
        Assert.NotEqual(before, after);
    }

    /// <summary>Disable, move and delete in this run's OU through the planner; the lab DC's account is refused.</summary>
    [Fact]
    public async Task DisableMoveDelete_GoThroughThePlan_AndTheDomainControllerIsRefused()
    {
        LabSettings.RequireLab();
        var admin = lab.Admin;
        var ou = await lab.CreateOuAsync(TestNames.Unique("K1T-LIFE"));
        var target = await lab.CreateOuAsync("Docelowa – Wrocław", ou);
        var computer = await CreateComputerAsync(ou, "K1TLIF" + Suffix(), new Dictionary<string, string>
        {
            [Attr.UserAccountControl] = ((int)UserAccountControl.WorkstationTrustAccount).ToString(CultureInfo.InvariantCulture),
        });
        var planner = Planner();
        var executor = new BatchExecutor(new InMemoryAuditLog(), TimeProvider.System);

        var disable = await planner.PlanAsync(admin, new BatchRequest([computer], [new SetAccountEnabledOperation(false)]), null, null, Ct);
        Assert.Equal(1, (await executor.ExecuteAsync(admin, disable, Options, null, Ct)).SucceededCount);
        Assert.True((await admin.GetAsync(computer, [Attr.UserAccountControl], Ct))!.IsDisabled);

        var move = await planner.PlanAsync(admin, new BatchRequest([computer], [new MoveOperation(target)]), null, null, Ct);
        Assert.Equal(1, (await executor.ExecuteAsync(admin, move, Options, null, Ct)).SucceededCount);
        var moved = DistinguishedName.Parse(target).Child("CN", DistinguishedName.Parse(computer).Name).ToString();
        Assert.NotNull(await admin.GetAsync(moved, [Attr.Name], Ct));

        var delete = await planner.PlanAsync(admin, new BatchRequest([moved], [new DeleteOperation()]), null, null, Ct);
        Assert.Equal(1, (await executor.ExecuteAsync(admin, delete, Options, null, Ct)).SucceededCount);
        Assert.Null(await admin.GetAsync(moved, [Attr.Name], Ct));

        // The lab DC's own account: disabling it is an error of the plan (nothing is executed).
        string? dc = null;
        await foreach (var obj in admin.SearchAsync(new DirectorySearch
        {
            BaseDn = DistinguishedName.Parse(LabSettings.DomainDn).Child("OU", "Domain Controllers").ToString(),
            Filter = LdapFilter.And(ComputerReports.ComputersFilter, LdapFilter.BitAnd(Attr.UserAccountControl, (long)UserAccountControl.ServerTrustAccount)),
            Attributes = [Attr.Name],
            SizeLimit = 1,
        }, Ct))
        {
            dc = obj.DistinguishedName;
        }

        Assert.NotNull(dc);
        var refused = await planner.PlanAsync(admin, new BatchRequest([dc!], [new SetAccountEnabledOperation(false)]), null, null, Ct);
        var issues = refused.Objects[0].Issues.Concat(refused.Objects[0].Changes.SelectMany(c => c.Issues)).ToList();
        Assert.Contains(issues, i => i.Code == IssueCodes.DomainControllerAccount && i.Severity == IssueSeverity.Error);
    }

    internal static string Suffix() => Guid.NewGuid().ToString("N")[..6].ToUpperInvariant();

    private static ChangePlanner Planner() => new(new TemplateEngine(), new PasswordGenerator(), TimeProvider.System);

    private async Task<DirectoryObject> FindBySamAccountNameAsync(string sam)
    {
        await foreach (var obj in lab.Admin.SearchAsync(new DirectorySearch
        {
            BaseDn = LabSettings.DomainDn,
            Filter = LdapFilter.Equal(Attr.SamAccountName, sam),
            Attributes = [Attr.ObjectSid, Attr.Name],
            SizeLimit = 1,
        }, Ct))
        {
            return obj;
        }

        Assert.Fail($"{sam} is not in the lab (tools/testlab/README.md).");
        return null!;
    }

    private async Task DeleteIfPresentAsync(string dn)
    {
        try
        {
            await lab.Admin.DeleteAsync(dn, subtree: false, Ct);
        }
        catch (DirectoryException ex) when (ex.Kind == DirectoryErrorKind.NoSuchObject)
        {
            // Not created.
        }
    }

    /// <summary>
    /// ldapsearch -x over the lab's plain LDAP (paged), the password on stdin (never on the command line); entries by
    /// normalized DN, attribute names lower case, first values only (base64 values decoded, folded lines joined).
    /// </summary>
    private static async Task<Dictionary<string, Dictionary<string, string>>> LdapsearchAsync(string baseDn, string filter, IReadOnlyList<string> attributes)
    {
        var start = new ProcessStartInfo("/usr/bin/ldapsearch")
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            StandardOutputEncoding = Encoding.UTF8,
        };
        foreach (var argument in (string[])["-x", "-LLL", "-o", "ldif-wrap=no", "-H", "ldap://" + (LabSettings.Server ?? "127.0.0.1"),
            "-D", LabSettings.AdminUser, "-y", "/dev/stdin", "-E", "pr=500/noprompt", "-b", baseDn, filter, .. attributes])
        {
            start.ArgumentList.Add(argument);
        }

        using var process = Process.Start(start)!;
        await process.StandardInput.WriteAsync(LabSettings.AdminPassword);
        process.StandardInput.Close();
        var output = process.StandardOutput.ReadToEndAsync(Ct);
        var error = process.StandardError.ReadToEndAsync(Ct);
        await process.WaitForExitAsync(Ct);
        Assert.True(process.ExitCode == 0, $"ldapsearch exited with {process.ExitCode}: {await error}");

        var entries = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, string>? current = null;
        foreach (var raw in (await output).Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.Length == 0 || line.StartsWith('#'))
            {
                current = null;
                continue;
            }

            var colon = line.IndexOf(':', StringComparison.Ordinal);
            if (colon <= 0)
            {
                continue;
            }

            var name = line[..colon].ToLowerInvariant();
            var value = line.Length > colon + 1 && line[colon + 1] == ':'
                ? Encoding.UTF8.GetString(Convert.FromBase64String(line[(colon + 2)..].Trim()))
                : line[(colon + 1)..].TrimStart();
            if (name == "dn")
            {
                current = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                entries[DistinguishedName.Parse(value).ToString()] = current;
            }
            else if (current is not null)
            {
                current.TryAdd(name, value);
            }
        }

        return entries;
    }

    internal static ChangePlan Plan(IDirectorySession session, string dn, params PlannedChange[] steps) => new()
    {
        Title = "K1 lab",
        Server = session.Info.Server,
        CreatedAt = DateTimeOffset.UtcNow,
        Objects = [new ObjectPlan { DistinguishedName = dn, DisplayName = DistinguishedName.Parse(dn).Name, Changes = steps }],
    };

    /// <summary>A workstation account (sAMAccountName NAME$, WORKSTATION_TRUST_ACCOUNT) in <paramref name="parentDn"/>.</summary>
    internal async Task<string> CreateComputerAsync(string parentDn, string name, IReadOnlyDictionary<string, string>? extra = null)
    {
        var dn = DistinguishedName.Parse(parentDn).Child("CN", name).ToString();
        var attributes = new Dictionary<string, IReadOnlyList<object>>
        {
            [Attr.ObjectClass] = ["top", "person", "organizationalPerson", ObjectClasses.User, ObjectClasses.Computer],
            [Attr.SamAccountName] = [name.ToUpperInvariant() + "$"],
            [Attr.UserAccountControl] = [((int)(UserAccountControl.WorkstationTrustAccount | UserAccountControl.AccountDisable)).ToString(System.Globalization.CultureInfo.InvariantCulture)],
        };
        foreach (var (attribute, value) in extra ?? new Dictionary<string, string>())
        {
            attributes[attribute] = [value];
        }

        await lab.Admin.AddAsync(dn, attributes, Ct);
        return dn;
    }

    private async Task<IReadOnlyList<AceSpec>> ExplicitAcesAsync(string dn)
    {
        var descriptor = (await lab.Admin.GetAsync(dn, [Attr.NtSecurityDescriptor], Ct))!.GetBytes(Attr.NtSecurityDescriptor);
        Assert.NotNull(descriptor);
        Assert.True(DaclEditor.TryReadExplicit(descriptor, out var aces));
        return aces;
    }

    private async Task<string?> OwnerAsync(string dn)
    {
        var descriptor = (await lab.Admin.GetAsync(dn, [Attr.NtSecurityDescriptor], Ct))!.GetBytes(Attr.NtSecurityDescriptor);
        Assert.True(SecurityDescriptorSummary.TryParse(descriptor, out var summary));
        return summary!.Owner;
    }
}
