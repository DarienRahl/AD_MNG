namespace AdMng.Watchdog.IntegrationTests.Infrastructure;

/// <summary>A user created by a test (the password only lives in memory of the test run).</summary>
public sealed record LabUser(string Dn, string SamAccountName, string UserPrincipalName, string? Password)
{
    public override string ToString() => $"LabUser({SamAccountName})";
}
