using AdMng.Watchdog.Service.Hosting;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AdMng.Watchdog.Service.Secrets;

/// <summary>
/// The key ring that protects stored secrets (spec 18): application name <c>AdMng.Watchdog</c>, keys in
/// <c>&lt;data&gt;/keys</c>. On Windows encrypted with DPAPI-NG for the service identity's SID (the gMSA – keys survive a
/// move to another host running the same gMSA). On Linux (stage B design 10) encrypted with
/// <see cref="SystemdCredentialXmlEncryptor"/> under the key of the systemd credential <c>dp-key</c>
/// (<see cref="DataProtectionKeySource"/>); without the credential the key ring stays unencrypted, which
/// <see cref="StartupChecks"/> refuses in Production (exit code 9) and warns about otherwise. <c>ISecretStore</c>
/// is unchanged. Tests replace the provider with an ephemeral one.
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
        else
        {
            // Registered as a factory so the credential is read when first needed (start-up checks, first key use), not
            // while the host is composed – and only once.
            services.TryAddSingleton(_ => DataProtectionKeySource.FromEnvironment());
            services.AddOptions<KeyManagementOptions>().Configure<DataProtectionKeySource>((options, keys) =>
            {
                if (keys.Key is { } key)
                {
                    options.XmlEncryptor = new SystemdCredentialXmlEncryptor(key);
                }
            });
        }

        services.AddSingleton(provider => new SecretProtector(provider.GetRequiredService<IDataProtectionProvider>()));
    }
}
