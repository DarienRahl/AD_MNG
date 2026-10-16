using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace AdMng.Watchdog.Service.Hosting;

/// <summary><see cref="ICertificateStore"/> over <see cref="X509Store"/> (read-only). On Linux LocalMachine\My does not
/// exist – the store is empty there (lab/dev runs use HTTP on loopback or a file-less development setup).</summary>
public sealed class SystemCertificateStore : ICertificateStore
{
    public IReadOnlyList<X509Certificate2> GetCertificates(StoreName storeName, StoreLocation location)
    {
        try
        {
            using var store = new X509Store(storeName, location);
            store.Open(OpenFlags.ReadOnly | OpenFlags.OpenExistingOnly);
            return [.. store.Certificates];
        }
        catch (CryptographicException)
        {
            return [];
        }
        catch (PlatformNotSupportedException)
        {
            return [];
        }
    }
}
