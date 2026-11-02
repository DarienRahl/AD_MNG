using AdMng.Core.GroupPolicy;
using AdMng.Core.GroupPolicy.Modeling;

namespace AdMng.Core.Tests.GroupPolicy;

/// <summary>
/// The reads of a Group Policy model on the fake directory and SYSVOL with the GPO management sample (K5): the GPOs with
/// versions, flags, WMI filter names and DACLs, the linked containers, the accounts' tokens and the GPOs' folders, put
/// through the modeller the way the document does.
/// </summary>
public sealed class GroupPolicyModelReaderTests : GroupPolicyTestBed
{
    [Fact]
    public async Task TheSample_AComputerAndAUser_GetWhatGpmcWouldModel()
    {
        Session.ComputesTokenGroups = true;
        var directory = await GroupPolicyModelReader.ReadDirectoryAsync(Session, Ct);
        var computer = await GroupPolicyModelReader.ReadAccountAsync(Session, Company.Computer1, directory.DomainSid, Ct);
        var user = await GroupPolicyModelReader.ReadAccountAsync(Session, Company.JanKowalski, directory.DomainSid, Ct);
        var site = Assert.Single(directory.Containers, c => c.Kind == GroupPolicySomKind.Site).Dn;
        var input = new GroupPolicyModelInput
        {
            DomainDn = directory.DomainDn,
            Containers = directory.Containers,
            Gpos = directory.Gpos,
            Computer = computer,
            User = user,
            SiteDn = site,
        };
        await using var sysvol = await OpenSysvolAsync();
        var contents = await GroupPolicyModelReader.ReadContentsAsync(sysvol, GroupPolicyModeler.GposToRead(input), Ct);

        var model = GroupPolicyModeler.Build(input, contents);

        // The computer: the enforced servers' GPO wins, the passwords' GPO applies if its WMI filter holds; the site's
        // printers have no computer settings and the remote desktop link is disabled.
        var part = model.Computer!;
        Assert.Equal(["Serwery – Zabezpieczenia", "Stacje robocze – Zasady haseł", "Default Domain Policy"], part.Applied.Select(g => g.Name));
        var passwords = part.Applied.Single(g => g.GpoId == Sample.WorkstationPasswords);
        Assert.Equal((ModeledGpoOutcome.AppliedIfWmiFilter, "Tylko stacje robocze"), (passwords.Outcome, passwords.WmiFilterName));
        Assert.Equal(ModeledGpoOutcome.Empty, part.Gpos.Single(g => g.GpoId == Sample.SitePrinters).Outcome);
        Assert.Equal(ModeledGpoOutcome.LinkDisabled, part.Gpos.Single(g => g.GpoId == Sample.WorkstationRemoteDesktop).Outcome);
        var length = Assert.Single(part.Settings, s => s.Key == "Computer/Security/System Access/MinimumPasswordLength");
        Assert.Equal(("Stacje robocze – Zasady haseł", "12"), (length.Winner.Source.GpoName, length.Winner.Setting.Value));
        Assert.Equal(("Default Domain Policy", "7"), (length.Overridden[0].Source.GpoName, length.Overridden[0].Setting.Value));
        Assert.Equal("Serwery – Zabezpieczenia", Assert.Single(part.Settings, s => s.Key == "Computer/Security/Privilege Rights/SeDebugPrivilege").Winner.Source.GpoName);
        Assert.DoesNotContain(part.Warnings, w => w.Kind is ModelingWarningKind.ContentUnreadable or ModelingWarningKind.NoSite);

        // The user (OU=IT below OU=Pracownicy, which blocks inheritance): the drive mappings only; the enforced servers'
        // GPO has its user configuration disabled, the others are blocked.
        var users = model.User!;
        Assert.Equal(["Użytkownicy – Mapowanie dysków"], users.Applied.Select(g => g.Name));
        Assert.Equal(ModeledGpoOutcome.PartDisabled, users.Gpos.Single(g => g.GpoId == Sample.ServerSecurity).Outcome);
        var blocked = users.Gpos.Single(g => g.GpoId == Sample.DefaultDomainPolicy);
        Assert.Equal((ModeledGpoOutcome.Blocked, Company.EmployeesOu), (blocked.Outcome, blocked.BlockedBy));
        Assert.Equal(ModeledGpoOutcome.Blocked, users.Gpos.Single(g => g.GpoId == Sample.SitePrinters).Outcome);
        Assert.Contains(users.Settings, s => s.Key.StartsWith("User/Preferences/Drives/", StringComparison.Ordinal) && s.Winner.Source.GpoId == Sample.DriveMappings);
        Assert.Equal((LoopbackMode.None, LoopbackSource.NotConfigured), (model.Loopback, model.LoopbackSource));
    }

    [Fact]
    public async Task Tokens_AreTheNestedSecurityGroups_OrThePrimaryGroupWhenTokenGroupsIsUnreadable()
    {
        Session.ComputesTokenGroups = true;
        var admins = Builder.AddGroup("GG_Administratorzy_Stacji", Company.GroupsOu, members: [Company.GroupIt]);
        var domainSid = Builder.SidOf(Builder.DomainDn);

        var jan = await GroupPolicyModelReader.ReadAccountAsync(Session, Company.JanKowalski, domainSid, Ct);

        Assert.Equal(Company.ItOu, jan.ContainerDn);
        Assert.Equal(Company.JanKowalski, jan.AccountDn);
        Assert.False(jan.TokenIncomplete);
        Assert.Contains(Builder.SidOf(Company.JanKowalski), jan.Token);
        Assert.Contains(Builder.SidOf(Company.GroupIt), jan.Token);
        Assert.Contains(Builder.SidOf(admins), jan.Token); // nested
        Assert.DoesNotContain(Builder.SidOf(Company.GroupAll), jan.Token); // a distribution group is in no token
        Assert.Contains(domainSid + "-513", jan.Token);
        Assert.Contains(GroupPolicySecurityFilter.AuthenticatedUsersSid, jan.Token);
        Assert.False(jan.IsDomainController);

        Session.ComputesTokenGroups = false;
        var bare = await GroupPolicyModelReader.ReadAccountAsync(Session, Company.JanKowalski, domainSid, Ct);
        Assert.True(bare.TokenIncomplete);
        Assert.Contains(domainSid + "-513", bare.Token);
        Assert.DoesNotContain(Builder.SidOf(Company.GroupIt), bare.Token);

        var container = GroupPolicyModelReader.ContainerTarget(Company.ComputersOu, ModelingPart.Computer, domainSid);
        Assert.Null(container.AccountDn);
        Assert.Contains(domainSid + "-515", container.Token);
        Assert.Contains(GroupPolicySecurityFilter.EveryoneSid, container.Token);
        await Assert.ThrowsAsync<AdMng.Core.Directory.DirectoryException>(() =>
            GroupPolicyModelReader.ReadAccountAsync(Session, "CN=Nie ma,OU=IT,OU=Pracownicy,OU=Firma," + Builder.DomainDn, domainSid, Ct));
    }
}
