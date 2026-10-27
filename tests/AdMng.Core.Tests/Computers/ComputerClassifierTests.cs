using AdMng.Core.ActiveDirectory;
using AdMng.Core.Computers;
using AdMng.Core.GroupPolicy;

namespace AdMng.Core.Tests.Computers;

public class ComputerClassifierTests
{
    [Theory]
    [InlineData(UserAccountControl.ServerTrustAccount | UserAccountControl.TrustedForDelegation, 516, "Windows Server 2025 Datacenter", ComputerType.DomainController)]
    [InlineData(UserAccountControl.WorkstationTrustAccount | UserAccountControl.PartialSecretsAccount, 521, "Windows Server 2022 Standard", ComputerType.ReadOnlyDomainController)]
    [InlineData(UserAccountControl.WorkstationTrustAccount, 521, "Windows Server 2022 Standard", ComputerType.ReadOnlyDomainController)]
    [InlineData(UserAccountControl.ServerTrustAccount, 516, null, ComputerType.DomainController)]
    [InlineData(UserAccountControl.WorkstationTrustAccount, 515, "Windows Server 2019 Standard", ComputerType.Server)]
    [InlineData(UserAccountControl.WorkstationTrustAccount, 515, "Windows 11 Enterprise", ComputerType.Workstation)]
    [InlineData(UserAccountControl.WorkstationTrustAccount, 515, "Ubuntu 24.04", ComputerType.Workstation)]
    [InlineData(UserAccountControl.WorkstationTrustAccount | UserAccountControl.AccountDisable, 515, null, ComputerType.Unknown)]
    [InlineData(UserAccountControl.WorkstationTrustAccount, 515, "  ", ComputerType.Unknown)]
    public void Classify(UserAccountControl uac, int primaryGroup, string? os, ComputerType expected) =>
        Assert.Equal(expected, ComputerClassifier.Classify(uac, primaryGroup, os));

    [Fact]
    public void Roles_MapToTheGroupPolicyRoles()
    {
        Assert.Equal(ComputerRole.DomainController, ComputerType.ReadOnlyDomainController.ToRole());
        Assert.Equal(ComputerRole.DomainController, ComputerType.DomainController.ToRole());
        Assert.Equal(ComputerRole.Server, ComputerType.Server.ToRole());
        Assert.Equal(ComputerRole.Workstation, ComputerType.Unknown.ToRole());
        Assert.True(ComputerType.ReadOnlyDomainController.IsDomainController());
        Assert.False(ComputerType.Server.IsDomainController());
    }
}
