using System.Globalization;
using System.Text;
using AdMng.Core.ActiveDirectory;
using AdMng.Core.Audit;
using AdMng.Core.Batch;
using AdMng.Core.Directory;
using AdMng.Core.GroupPolicy;
using AdMng.Core.GroupPolicy.Management;
using AdMng.Core.GroupPolicy.Modeling;
using AdMng.Core.Ldap;
using AdMng.Directory.IntegrationTests.Infrastructure;

namespace AdMng.Directory.IntegrationTests.Integration;

/// <summary>
/// Group Policy modelling (K5) against the Samba lab: an OU with a common and an enforced GPO and, below it, an OU that
/// blocks inheritance with its own GPO, a GPO filtered (by K2's planner, as GPMC does) to a group without the computer, a
/// GPO with its computer configuration disabled and a disabled link. The model of a computer of the lower OU – read like the
/// document reads it (LDAP with tokenGroups, SYSVOL over smbclient) – applies the same GPOs of this run as Samba's own
/// <c>samba-tool gpo list</c> for the account (which checks Read only, with the token Samba builds for the account – so the
/// filtering here is by Authenticated Users, not by a group of the computer), and the enforced GPO's password length wins. The lab is shared: the GPOs are
/// named <c>K5T-&lt;run&gt; …</c> and deleted at the end; the OUs, the computer and the group live in the run's OU.
/// </summary>
[Collection(SambaToolCollection.Name)]
public sealed class GroupPolicyModelLabTests(LabFixture lab)
{
    private static readonly BatchOptions Options = new() { InitialRetryDelay = TimeSpan.FromMilliseconds(50) };

    private readonly GroupPolicyPlanner _planner = new(new GroupPolicyReader(), TimeProvider.System);
    private readonly SmbClientSysvolFileWriter _writer = new(SambaTools.Address, LabSettings.AdminUser, LabSettings.AdminPassword);
    private readonly InMemoryAuditLog _audit = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Model_OfALabComputer_AppliesWhatSambaToolLists_AndTheEnforcedSettingWins()
    {
        SambaTools.Require();
        var admin = lab.Admin;
        var run = Guid.NewGuid().ToString("N")[..6];
        var ou = await lab.CreateOuAsync(TestNames.Unique("K5T-GPO"));
        var stations = await lab.CreateOuAsync("Stacje – Łódź", ou);
        var computerName = "K5T" + run.ToUpperInvariant();
        var computer = await CreateComputerAsync(stations, computerName);
        var group = await CreateGroupAsync(ou, $"K5T {run} Stacje", computer);
        var groupSid = (await admin.GetAsync(group, [Attr.ObjectSid], Ct))!.ObjectSid!;
        var board = await CreateGroupAsync(ou, $"K5T {run} Zarząd", null);
        var boardSid = (await admin.GetAsync(board, [Attr.ObjectSid], Ct))!.ObjectSid!;
        using var tools = SambaTools.Create();
        var gpos = new List<Guid>();
        await using var sysvol = await _writer.OpenForWriteAsync(admin, Ct);
        try
        {
            async Task<Guid> CreateAsync(string name)
            {
                var id = await tools.CreateGpoAsync($"K5T-{run} {name}", Ct);
                gpos.Add(id);
                return id;
            }

            var common = await CreateAsync("Firma – wspólne");
            var enforced = await CreateAsync("Firma – wymuszone");
            var station = await CreateAsync("Stacje – Łódź");
            var denied = await CreateAsync("Stacje – tylko zarząd");
            var userOnly = await CreateAsync("Stacje – tylko użytkownik");
            var disabledLink = await CreateAsync("Stacje – wyłączone łącze");

            await tools.LinkAsync(ou, common, enforced: false, Ct);
            await tools.LinkAsync(ou, enforced, enforced: true, Ct);
            await tools.LinkAsync(stations, station, enforced: false, Ct);
            await tools.LinkAsync(stations, denied, enforced: false, Ct);
            await tools.LinkAsync(stations, userOnly, enforced: false, Ct);
            await tools.BlockInheritanceAsync(stations, Ct);
            await ExecuteOkAsync(await _planner.PlanLinkAsync(admin, stations, disabledLink, Ct, enabled: false));
            await ExecuteOkAsync(await _planner.PlanStatusAsync(admin, userOnly, GpoStatus.ComputerSettingsDisabled, Ct));

            // Security filtering as GPMC does it: Authenticated Users out, the board in – the computer can neither read nor
            // apply the GPO.
            await ExecuteOkAsync(await _planner.PlanRemoveDelegationAsync(admin, sysvol, denied, GroupPolicySecurityFilter.AuthenticatedUsersSid, null, Ct));
            await ExecuteOkAsync(await _planner.PlanAddSecurityFilterAsync(admin, sysvol, denied, boardSid, null, Ct));

            // Two GPOs set the minimum password length; every GPO gets a version, so no computer configuration is empty.
            await tools.PutAsync(enforced, SecurityTemplate.RelativePath, Template("MinimumPasswordLength = 10"), Ct);
            await tools.PutAsync(station, SecurityTemplate.RelativePath, Template("MinimumPasswordLength = 14"), Ct);
            foreach (var gpo in gpos)
            {
                await admin.ModifyAsync(GpoDn(gpo),
                    [AttributeModification.Replace(GroupPolicyAttributes.VersionNumber, new GpoVersion(1, 1).ToInt32().ToString(CultureInfo.InvariantCulture))], null, Ct);
            }

            var directory = await GroupPolicyModelReader.ReadDirectoryAsync(admin, Ct);
            var target = await GroupPolicyModelReader.ReadAccountAsync(admin, computer, directory.DomainSid, Ct);
            Assert.False(target.TokenIncomplete, "Samba computes tokenGroups.");
            Assert.Contains(groupSid, target.Token);
            Assert.True(DistinguishedName.AreEqual(stations, target.ContainerDn));
            var input = new GroupPolicyModelInput { DomainDn = directory.DomainDn, Containers = directory.Containers, Gpos = directory.Gpos, Computer = target };
            var contents = await GroupPolicyModelReader.ReadContentsAsync(sysvol, GroupPolicyModeler.GposToRead(input), Ct);

            var part = GroupPolicyModeler.Build(input, contents).Computer!;

            var ours = gpos.ToHashSet();
            Assert.Equal([enforced, station], part.Applied.Select(g => g.GpoId).Where(ours.Contains)); // precedence order
            ModeledGpo Row(Guid id) => part.Gpos.First(g => g.GpoId == id);
            Assert.Equal(ModeledGpoOutcome.Blocked, Row(common).Outcome);
            Assert.True(DistinguishedName.AreEqual(stations, Row(common).BlockedBy!));
            Assert.Equal(ModeledGpoOutcome.SecurityDenied, Row(denied).Outcome);
            Assert.Equal(ModeledGpoOutcome.PartDisabled, Row(userOnly).Outcome);
            Assert.Equal(ModeledGpoOutcome.LinkDisabled, Row(disabledLink).Outcome);
            var length = Assert.Single(part.Settings, s => s.Key == "Computer/Security/System Access/MinimumPasswordLength");
            Assert.Equal((enforced, "10"), (length.Winner.Source.GpoId, length.Winner.Setting.Value));
            Assert.Equal((station, "14"), (Assert.Single(length.Overridden).Source.GpoId, length.Overridden[0].Setting.Value));
            Assert.DoesNotContain(part.Warnings, w => w.Kind is ModelingWarningKind.ContentUnreadable or ModelingWarningKind.FileUnreadable);

            // Samba's own list for the account: the same GPOs of this run. A Samba that takes the computer for a user skips the
            // GPOs with the user configuration disabled instead, so the computer-only one is not compared there.
            var (asComputer, listed) = await tools.GpoListAsync(computerName, Ct);
            var compared = asComputer ? ours : ours.Where(id => id != userOnly).ToHashSet();
            var expected = part.Applied.Select(g => g.GpoId).Where(compared.Contains).ToHashSet();
            Assert.Equal(expected, listed.Where(compared.Contains).ToHashSet());
        }
        finally
        {
            foreach (var gpo in gpos)
            {
                await tools.DeleteGpoAsync(gpo);
            }
        }
    }

    private static string GpoDn(Guid id) =>
        DistinguishedName.Parse(LabSettings.DomainDn).Child("CN", "System").Child("CN", "Policies").Child("CN", SysvolPaths.FolderName(id)).ToString();

    private static byte[] Template(params string[] systemAccess) =>
        [0xFF, 0xFE, .. Encoding.Unicode.GetBytes(string.Join("\r\n",
            ["[Unicode]", "Unicode=yes", "[System Access]", .. systemAccess, "[Version]", "signature=\"$CHICAGO$\"", "Revision=1"]) + "\r\n")];

    private async Task<string> CreateComputerAsync(string parentDn, string name)
    {
        var dn = DistinguishedName.Parse(parentDn).Child("CN", name).ToString();
        await lab.Admin.AddAsync(dn, new Dictionary<string, IReadOnlyList<object>>
        {
            [Attr.ObjectClass] = ["top", "person", "organizationalPerson", ObjectClasses.User, ObjectClasses.Computer],
            [Attr.SamAccountName] = [name + "$"],
            [Attr.UserAccountControl] = [((int)UserAccountControl.WorkstationTrustAccount).ToString(CultureInfo.InvariantCulture)],
            [Attr.OperatingSystem] = ["Windows 11 Enterprise"],
        }, Ct);
        return dn;
    }

    private async Task<string> CreateGroupAsync(string parentDn, string cn, string? member)
    {
        var dn = DistinguishedName.Parse(parentDn).Child("CN", cn).ToString();
        var attributes = new Dictionary<string, IReadOnlyList<object>>
        {
            [Attr.ObjectClass] = ["top", ObjectClasses.Group],
            [Attr.SamAccountName] = [TestNames.SamAccountName()],
        };
        if (member is not null)
        {
            attributes[Attr.Member] = [member];
        }

        await lab.Admin.AddAsync(dn, attributes, Ct);
        return dn;
    }

    private async Task<BatchResult> ExecuteOkAsync(ChangePlan plan)
    {
        Assert.Equal(0, plan.ErrorCount);
        var result = await new BatchExecutor(_audit, TimeProvider.System, null, _writer).ExecuteAsync(lab.Admin, plan, Options, null, Ct);
        var failures = result.Objects.SelectMany(o => o.Changes).Where(c => !c.Succeeded).Select(c => $"{c.Change.Action}: {c.ErrorMessage}").ToList();
        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
        return result;
    }
}
