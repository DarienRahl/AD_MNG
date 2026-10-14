namespace AdMng.Watchdog.IntegrationTests.Infrastructure;

/// <summary>Unique names, so test classes running in parallel (and other users of the shared lab) never collide.</summary>
internal static class TestNames
{
    /// <summary>e.g. <c>Księgowość 3f2a9c1b</c>.</summary>
    public static string Unique(string prefix) => $"{prefix} {Guid.NewGuid().ToString("N")[..8]}";

    /// <summary>A 20 character sAMAccountName (AD limit), recognisable as a watchdog test account.</summary>
    public static string SamAccountName() => "wdit" + Guid.NewGuid().ToString("N")[..16];

    /// <summary>A password that satisfies the default AD complexity rules (with Polish letters).</summary>
    public static string Password() => $"Zażółć-{Guid.NewGuid().ToString("N")[..10]}-Gęślą9!";
}
