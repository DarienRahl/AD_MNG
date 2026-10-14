using AdMng.Watchdog.Core.Model;

namespace AdMng.Watchdog.Core.Abstractions;

/// <summary>Channel credentials, encrypted at rest and bound to their destination (section 18).</summary>
public interface ISecretStore
{
    Task<StoredSecret?> GetAsync(string name, CancellationToken cancellationToken);
    /// <summary>Tests and DR only – production writes go through IConfigStore.SaveAsync.</summary>
    Task SetAsync(string name, SecretValue value, string binding, CancellationToken cancellationToken);
    Task RemoveAsync(string name, CancellationToken cancellationToken);
    /// <summary>Stored secret names with their bindings (validation input).</summary>
    Task<IReadOnlyDictionary<string, string>> ListAsync(CancellationToken cancellationToken);
}
