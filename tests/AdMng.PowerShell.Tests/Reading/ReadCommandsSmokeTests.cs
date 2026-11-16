using AdMng.Core.PowerShell;

namespace AdMng.PowerShell.Tests.Reading;

public sealed class ReadCommandsSmokeTests
{
    [Fact]
    public async Task Get_AdmUser_by_identity_returns_a_typed_object_with_the_distinguished_name()
    {
        await using var harness = await ReadHarness.CreateAsync();

        var result = await harness.RunAsync("Get-AdmUser -Identity jan.kowalski");

        Assert.Equal(PowerShellRunState.Completed, result.End.State);
        Assert.Equal(string.Empty, result.Errors);
        var user = result.Single();
        Assert.Contains("AdMng.User", user.TypeNames);
        Assert.Equal(harness.Company.JanKowalski, (string)user.Properties["DistinguishedName"].Value, ignoreCase: true);
        Assert.Equal("jan.kowalski", user.Properties["sAMAccountName"].Value);
        Assert.Equal(true, user.Properties["Enabled"].Value);
    }
}
