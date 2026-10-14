using AdMng.Watchdog.Service.Hosting;
using Microsoft.AspNetCore.DataProtection;

namespace AdMng.Watchdog.Service.Secrets;

/// <summary>
/// The key ring that protects stored secrets (spec 18): application name <c>AdMng.Watchdog</c>, keys in
/// <c>&lt;data&gt;\keys</c>, on Windows encrypted with DPAPI-NG for the service identity's SID (the gMSA – keys survive a
/// move to another host running the same gMSA). Elsewhere (lab/dev only) the keys are not encrypted at rest:
/// <see cref="StartupChecks"/> warns, and refuses in Production. Tests replace the provider with an ephemeral one.
/// </summary>
public static class DataProtectionSetup
{
    public const string ApplicationName = "AdMng.Watchdog";

    public static void Configure(IServiceCollection services, DataDirectory dataDirectory)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(dataDirectory);
        var builder = services.AddDataProtection()
            .SetApplicationName(ApplicationName)
            .PersistKeysToFileSystem(new DirectoryInfo(dataDirectory.KeysPath));
        if (OperatingSystem.IsWindows())
        {
            builder.ProtectKeysWithDpapiNG();
        }

        services.AddSingleton(provider => new SecretProtector(provider.GetRequiredService<IDataProtectionProvider>()));
    }
}
