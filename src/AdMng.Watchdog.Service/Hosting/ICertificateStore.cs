using System.Security.Cryptography.X509Certificates;

namespace AdMng.Watchdog.Service.Hosting;

/// <summary>Reads a Windows certificate store (LocalMachine\My in production, in-memory in tests).</summary>
public interface ICertificateStore
{
    /// <summary>Every certificate of the store (callers dispose those they do not keep). Empty when the store cannot be
    /// opened on this platform.</summary>
    IReadOnlyList<X509Certificate2> GetCertificates(StoreName storeName, StoreLocation location);
}
