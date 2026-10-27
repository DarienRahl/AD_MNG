using AdMng.Core.GroupPolicy;

namespace AdMng.Directory.IntegrationTests.Unit;

/// <summary>
/// The platform-neutral parts of the Windows SYSVOL reader (README U4c): the user and domain for <c>LogonUser</c> with new
/// credentials (a logon session of the run's own, never the operator's) and the Win32 errors of the logon and the first
/// access to SYSVOL.
/// </summary>
public sealed class SysvolLogonTests
{
    [Theory]
    [InlineData("łucja.żółkiewska", "CONTOSO", "łucja.żółkiewska", "CONTOSO")]
    [InlineData("łucja.żółkiewska", "contoso.test", "łucja.żółkiewska", "contoso.test")]
    [InlineData(@"CONTOSO\łucja.żółkiewska", null, "łucja.żółkiewska", "CONTOSO")]
    [InlineData(@"CONTOSO\łucja.żółkiewska", "", "łucja.żółkiewska", "CONTOSO")]
    [InlineData("łucja.żółkiewska@contoso.test", null, "łucja.żółkiewska@contoso.test", null)]
    [InlineData(@"CONTOSO\", null, @"CONTOSO\", null)]
    public void LogonParts_SplitDownLevelNames_KeepUpns(string userName, string? domain, string expectedUser, string? expectedDomain)
    {
        Assert.Equal((expectedUser, expectedDomain), SysvolLogon.SplitForLogon(userName, domain));
    }

    [Fact]
    public void Win32Errors_OfTheLogonAndTheFirstAccess_BecomeTheReasonsOfTheNotice()
    {
        Assert.Equal(SysvolUnavailableReason.LogonFailure, SysvolErrors.ReasonOf(1326));
        Assert.Equal(SysvolUnavailableReason.LogonFailure, SysvolErrors.ReasonOf(1909));
        Assert.Equal(SysvolUnavailableReason.AccessDenied, SysvolErrors.ReasonOf(5));
        Assert.Equal(SysvolUnavailableReason.Unreachable, SysvolErrors.ReasonOf(53));
        Assert.Equal(SysvolUnavailableReason.Unreachable, SysvolErrors.ReasonOf(67));

        // A separate logon session cannot conflict with the operator's SMB sessions: 1219 is no special case any more.
        Assert.Equal(SysvolUnavailableReason.Failed, SysvolErrors.ReasonOf(1219));

        Assert.Equal(1326, SysvolErrors.Win32Error(new IOException("Nieprawidłowa nazwa użytkownika lub hasło.", unchecked((int)0x8007052E))));
        Assert.Equal(53, SysvolErrors.Win32Error(new IOException("Nie można odnaleźć ścieżki sieciowej.", unchecked((int)0x80070035))));
        Assert.Equal(0, SysvolErrors.Win32Error(new IOException("Inny błąd.", unchecked((int)0x80131620))));
    }
}
