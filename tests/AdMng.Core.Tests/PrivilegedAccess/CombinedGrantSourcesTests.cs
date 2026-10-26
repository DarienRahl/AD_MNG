using AdMng.Core.Directory;
using AdMng.Core.PrivilegedAccess;
using AdMng.Testing;

namespace AdMng.Core.Tests.PrivilegedAccess;

/// <summary>
/// U4b and U4c in one run, as the app registers them: the ACL delegations and the local rights from GPOs on one fake DC
/// (the Polish sample company with <see cref="FakeDirectoryBuilder.AddAclDelegationSample"/> and
/// <see cref="FakeDirectoryBuilder.AddGroupPolicySample"/>). Each source keeps its findings, an account collects both,
/// and the GPO source's security filtering reads the descriptors the ACL sample set on the GPOs.
/// </summary>
public class CombinedGrantSourcesTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 26, 10, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task BothSources_InOneRun_EachKeepsItsFindings_AndAnAccountCollectsBoth()
    {
        var builder = new FakeDirectoryBuilder();
        var company = builder.AddSampleCompany();
        var sample = builder.AddPrivilegedAccessSample(company);
        builder.AddAclDelegationSample(company, sample);
        var sysvol = new FakeSysvolFileReader();
        var gpo = builder.AddGroupPolicySample(company, sample, sysvol);
        var options = new PrivilegedAccessOptions { DomainSid = builder.DomainSid, RootDomainSid = builder.DomainSid };
        var scanner = new PrivilegedAccessScanner(builder.Session, options, [new DirectoryAclGrantSource(), new GroupPolicyGrantSource(sysvol)]);

        var evaluator = await scanner.PrepareAsync(Now, Ct);
        var results = new Dictionary<string, AccountPrivileges>(StringComparer.Ordinal);
        await foreach (var account in scanner.ScanAsync(evaluator, builder.DomainDn, SearchScope.Subtree, null, null, Ct))
        {
            results[PrivilegedAccessScanner.KeyOf(account.DistinguishedName)] = account;
        }

        // Henryk Żuławski: reset password on the users of OU=Firma (ACL, through GG_Helpdesk) and local Administrators on
        // the workstations (GPP, through GG_Wsparcie_Stacji) - one account, both sources, each with its own path.
        var henryk = results[PrivilegedAccessScanner.KeyOf(sample.HenrykZulawski)].Findings;
        var reset = Assert.Single(henryk, f => f.Source == PrivilegeSource.DirectoryAcl && f.Values.Contains("ResetPassword:user"));
        Assert.Equal((PrivilegeLevel.High, company.CompanyOu, sample.Helpdesk), (reset.Level, reset.ScopeDn, reset.DirectGroupDn));
        var admin = Assert.Single(henryk, f => f.Source == PrivilegeSource.GroupPolicy);
        Assert.Equal((LocalRightsCatalog.AdministratorsSid, company.ComputersOu, gpo.WorkstationSupportGroup),
            (admin.GroupPolicy!.Right, admin.ScopeDn, admin.DirectGroupDn));
        Assert.Equal(PrivilegeLevel.Elevated, admin.Level);

        // Ewa Błaszczyk: full control of OU=IT (ACL) and Administrators on the servers (Restricted Groups).
        var ewa = results[PrivilegedAccessScanner.KeyOf(sample.EwaBlaszczyk)].Findings;
        Assert.Contains(ewa, f => f.Source == PrivilegeSource.DirectoryAcl && f.ScopeDn == company.ItOu && f.Values.Contains("GenericAll"));
        Assert.Contains(ewa, f => f.Source == PrivilegeSource.GroupPolicy && f.ScopeDn == sample.ServersOu
            && f.GroupPolicy!.Mechanism == LocalRightMechanism.RestrictedGroupMembers);

        // The Default Domain Controllers Policy carries the descriptor of the ACL sample (Apply Group Policy for
        // Authenticated Users): the security filtering lets it reach the DC, so SeDebugPrivilege stays Tier 0.
        var lukasz = Assert.Single(results[PrivilegedAccessScanner.KeyOf(company.LukaszWisniewski)].Findings,
            f => f.Source == PrivilegeSource.GroupPolicy);
        Assert.Equal(("SeDebugPrivilege", PrivilegeLevel.Tier0, 1), (lukasz.GroupPolicy!.Right, lukasz.Level, lukasz.GroupPolicy.DomainControllers));

        // DCSync of Usługa SQL (ACL) is untouched by the GPO source; the only notice is the GPP name the domain lacks.
        var sql = results[PrivilegedAccessScanner.KeyOf(sample.SqlService)].Findings;
        Assert.Contains(sql, f => f.Source == PrivilegeSource.DirectoryAcl && f.Level == PrivilegeLevel.Tier0 && f.Values.Contains("ReplicationGetChangesAll"));
        Assert.DoesNotContain(sql, f => f.Source == PrivilegeSource.GroupPolicy);
        Assert.Equal([GroupPolicyGrantSource.Notices.UnresolvedPrincipals], scanner.Notices.Select(n => n.Code));
        Assert.Single(sysvol.OpenedOn);
        Assert.Equal(0, sysvol.OpenConnections);
    }
}
