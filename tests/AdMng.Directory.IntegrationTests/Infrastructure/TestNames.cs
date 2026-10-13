namespace AdMng.Directory.IntegrationTests.Infrastructure;

/// <summary>Unique names so that test classes running in parallel never collide.</summary>
internal static class TestNames
{
    /// <summary>e.g. <c>Użytkownik 3f2a9c1b</c>.</summary>
    public static string Unique(string prefix) => $"{prefix} {Guid.NewGuid().ToString("N")[..8]}";

    /// <summary>A 20 character sAMAccountName (AD limit).</summary>
    public static string SamAccountName() => "t" + Guid.NewGuid().ToString("N")[..19];

    /// <summary>A password that satisfies the default AD complexity rules (with Polish letters).</summary>
    public static string Password() => $"Zażółć-{Guid.NewGuid().ToString("N")[..10]}-Gęślą9!";
}
