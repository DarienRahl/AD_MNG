using System.Diagnostics;
using AdMng.Core.ActiveDirectory;
using AdMng.Core.Directory;
using AdMng.Core.Ldap;
using AdMng.Core.PrivilegedAccess;
using AdMng.Directory.IntegrationTests.Infrastructure;
using AdMng.Presentation.Session;

namespace AdMng.Directory.IntegrationTests.Integration;

/// <summary>
/// The ACL delegations source of the privileged access review (README U4b) against the Samba lab: rights delegated with
/// <c>samba-tool dsacl</c> the way an administrator does it - reset password on the users of this run's OU for a group,
/// member write on its groups for an account, full control of a single user (deep scan only) and DCSync on the domain head
/// for a test account (removed again in any case: the domain head is shared). The lab's own defaults (schema
/// defaultSecurityDescriptor, Account/Print Operators on CN=Users and CN=Computers, the default trustees) are not reported,
/// inherited ACEs only once at their source.
/// </summary>
[Collection(SambaToolCollection.Name)]
public sealed class AclDelegationTests(LabFixture lab)
{
    private const string ForceChangePassword = "00299570-246d-11d0-a768-00aa006e0529";
    private const string Member = "bf9679c0-0de6-11d0-a285-00aa003049e2";
    private const string UserClass = "bf967aba-0de6-11d0-a285-00aa003049e2";
    private const string GroupClass = "bf967a9c-0de6-11d0-a285-00aa003049e2";
    private const string GetChanges = "1131f6aa-9c07-11d1-f79f-00c04fc2dcd2";
    private const string GetChangesAll = "1131f6ad-9c07-11d1-f79f-00c04fc2dcd2";

    private static string? _sambaPython;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Source_FindsTheRightsDelegatedWithSambaTool_OnceAtTheirSource()
    {
        LabSettings.RequireLab();
        Assert.SkipUnless(SambaPython() is not null, "samba-tool is not usable (no Python with the samba module).");
        var admin = lab.Admin;
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var ou = await lab.CreateOuAsync(TestNames.Unique("U4B Delegacje"));
        var child = await lab.CreateOuAsync("U4B Podrzędna", ou);
        var policy = DomainPolicy.FromDomainObject((await admin.GetAsync(LabSettings.DomainDn, DomainPolicy.Attributes, Ct))!);
        var helena = await lab.CreateUserAsync(ou, "Helena Resetująca");
        var marek = await lab.CreateUserAsync(ou, "Marek Członkowski");
        var szymon = await lab.CreateUserAsync(ou, "Szymon Synchronizujący");
        var ewelina = await lab.CreateUserAsync(child, "Ewelina Celowa");
        var group = await CreateGroupAsync(ou, $"GG_U4B_Hasła_{suffix}", helena);
        var groupSid = await SidAsync(group);
        var marekSid = await SidAsync(marek);
        var szymonSid = await SidAsync(szymon);

        // Delegations as an administrator would set them (dsacl adds the ACEs; Samba propagates the inheritable ones).
        await DsaclAsync("set", ou,
            $"(OA;CIIO;CR;{ForceChangePassword};{UserClass};{groupSid})(OA;CIIO;WP;{Member};{GroupClass};{marekSid})");
        await DsaclAsync("set", ewelina, $"(A;;GA;;;{marekSid})");
        var dcsync = $"(OA;;CR;{GetChanges};;{szymonSid})(OA;;CR;{GetChangesAll};;{szymonSid})";
        await DsaclAsync("set", LabSettings.DomainDn, dcsync);
        try
        {
            var options = new PrivilegedAccessOptions { DomainSid = policy.DomainSid, RootDomainSid = policy.DomainSid };
            var grants = await CollectAsync(options);

            var reset = Assert.Single(grants, g => Same(g.ScopeDn, ou) && g.PrincipalSid == groupSid);
            Assert.Equal(["ResetPassword:user"], reset.Values);
            Assert.Equal(PrivilegeLevel.High, reset.Level);
            Assert.Equal(PrivilegeScopeKind.Container, reset.Scope);
            var members = Assert.Single(grants, g => Same(g.ScopeDn, ou) && g.PrincipalSid == marekSid);
            Assert.Equal(["WriteMember:group"], members.Values);
            Assert.Equal(PrivilegeLevel.High, members.Level);

            // The inherited copies in the child OU and the user below are reported once, at the OU above.
            Assert.DoesNotContain(grants, g => Same(g.ScopeDn, child));
            Assert.DoesNotContain(grants, g => Same(g.ScopeDn, ewelina));

            var sync = Assert.Single(grants, g => Same(g.ScopeDn, LabSettings.DomainDn) && g.PrincipalSid == szymonSid);
            Assert.Equal(PrivilegeLevel.Tier0, sync.Level);
            Assert.Equal(["ReplicationGetChangesAll", "ReplicationGetChanges"], sync.Values);

            // The lab's defaults: Account and Print Operators (class defaults of OUs, provisioning of CN=Users and
            // CN=Computers), Administrators, SYSTEM, the domain controllers - nothing of it is a delegation.
            Assert.DoesNotContain(grants, g => g.PrincipalSid is "S-1-5-32-548" or "S-1-5-32-550" or "S-1-5-32-544" or "S-1-5-18" or "S-1-5-9");
            Assert.DoesNotContain(grants, g => g.PrincipalSid == $"{policy.DomainSid}-512" || g.PrincipalSid == $"{policy.DomainSid}-516");

            // The deep scan reads single users too.
            var deep = await CollectAsync(options with { DeepAclScan = true });
            var full = Assert.Single(deep, g => Same(g.ScopeDn, ewelina));
            Assert.Equal(marekSid, full.PrincipalSid);
            Assert.Equal(["GenericAll"], full.Values);
            Assert.Equal(PrivilegeLevel.Elevated, full.Level);

            // Through the scanner: the members of the trustee group get the right with the path.
            var scanner = new PrivilegedAccessScanner(admin, options, [new DirectoryAclGrantSource()]);
            var evaluator = await scanner.PrepareAsync(DateTimeOffset.UtcNow, Ct);
            var results = new Dictionary<string, AccountPrivileges>(StringComparer.OrdinalIgnoreCase);
            await foreach (var account in scanner.ScanAsync(evaluator, ou, SearchScope.Subtree, null, null, Ct))
            {
                results[account.Account.Name] = account;
            }

            var helenaRight = Assert.Single(results["Helena Resetująca"].Findings);
            Assert.Equal(PrivilegeFindingKind.AclRight, helenaRight.Kind);
            Assert.Equal(PrivilegeSource.DirectoryAcl, helenaRight.Source);
            Assert.Equal(["Helena Resetująca", $"GG_U4B_Hasła_{suffix}"], helenaRight.Path.Select(s => s.Name));
            Assert.True(Same(helenaRight.ScopeDn, ou));
            Assert.Equal(PrivilegeLevel.Tier0, results["Szymon Synchronizujący"].Level);
            Assert.Equal(PrivilegeLevel.High, results["Marek Członkowski"].Level);
            Assert.False(results["Ewelina Celowa"].HasFindings);
        }
        finally
        {
            // The domain head is shared by every run: the test account's DCSync goes in any case.
            await DsaclAsync("delete", LabSettings.DomainDn, dcsync);
        }
    }

    private async Task<IReadOnlyList<PrivilegeGrant>> CollectAsync(PrivilegedAccessOptions options)
    {
        var graph = await GroupGraph.ReadAsync(lab.Admin, null, options.PageSize, Ct);
        return await new DirectoryAclGrantSource().CollectAsync(
            new PrivilegeScanContext { Session = lab.Admin, Groups = graph, Options = options, Now = DateTimeOffset.UtcNow }, Ct);
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

    private async Task<string> SidAsync(string dn) =>
        (await lab.Admin.GetAsync(dn, [Attr.ObjectSid], Ct))?.ObjectSid ?? throw new InvalidOperationException($"{dn} has no objectSid.");

    private static bool Same(string? a, string b) => DistinguishedName.AreEqual(a, b);

    /// <summary><c>samba-tool dsacl set|delete</c> as the lab administrator (the password goes through PASSWD, not the command line).</summary>
    private static async Task DsaclAsync(string verb, string objectDn, string sddl)
    {
        var start = new ProcessStartInfo(SambaPython()!)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var argument in new[] { "/usr/bin/samba-tool", "dsacl", verb, "-H", "ldap://" + LabSettings.Server, "-U", LabSettings.AdminUser, "--objectdn", objectDn, "--sddl", sddl })
        {
            start.ArgumentList.Add(argument);
        }

        start.Environment["PASSWD"] = LabSettings.AdminPassword;
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
        var error = process.StandardError.ReadToEndAsync(CancellationToken.None);
        await process.WaitForExitAsync(CancellationToken.None);
        Assert.True(process.ExitCode == 0, $"samba-tool dsacl {verb} {objectDn} failed: {await output}{await error}");
    }

    private static string? SambaPython() => _sambaPython ??= FindSambaPython();

    private static string? FindSambaPython()
    {
        if (!OperatingSystem.IsLinux() || !File.Exists("/usr/bin/samba-tool"))
        {
            return null;
        }

        foreach (var candidate in new[] { "/usr/bin/python3.12", "/usr/bin/python3" })
        {
            if (!File.Exists(candidate))
            {
                continue;
            }

            var start = new ProcessStartInfo(candidate) { UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true };
            start.ArgumentList.Add("-c");
            start.ArgumentList.Add("import samba");
            using var process = Process.Start(start)!;
            process.WaitForExit();
            if (process.ExitCode == 0)
            {
                return candidate;
            }
        }

        return null;
    }
}
