using AdMng.Watchdog.Contracts.Api;

namespace AdMng.Watchdog.Service.Secrets;

/// <summary>
/// Where the Linux service gets its <see cref="DataProtectionMasterKey"/> (stage B design 10): the systemd credential, read
/// once on first use. Development and the tests register an explicit key (<see cref="FromKey"/>) or none.
/// The start-up checks (<see cref="Hosting.StartupChecks"/>) refuse a Production start without a key (exit code 9).
/// </summary>
public sealed class DataProtectionKeySource : IDisposable
{
    /// <summary>The file next to the bootstrap file that names how <c>dp-key.cred</c> is sealed (one of
    /// <see cref="HostCredentialKeys"/>, written by <c>postinst</c> / <c>reset-secrets-key</c>; root:root 0644, no
    /// secret).</summary>
    public const string DefaultVariantFile = "/etc/admng-watchdog/dp-key.variant";

    private readonly Lazy<Loaded> _loaded;

    /// <summary>A source that loads the key with <paramref name="load"/> on first use.</summary>
    public DataProtectionKeySource(Func<DataProtectionMasterKey?> load, string? variantFile = null)
    {
        ArgumentNullException.ThrowIfNull(load);
        _loaded = new Lazy<Loaded>(() => Load(load), LazyThreadSafetyMode.ExecutionAndPublication);
        VariantFile = variantFile;
    }

    /// <summary>The systemd credential of the unit (<c>$CREDENTIALS_DIRECTORY/dp-key</c>).</summary>
    public static DataProtectionKeySource FromEnvironment() =>
        new(() => DataProtectionMasterKey.FromCredentialsDirectory(Environment.GetEnvironmentVariable(DataProtectionMasterKey.CredentialsDirectoryVariable)),
            DefaultVariantFile);

    /// <summary>An explicit key (tests, development).</summary>
    public static DataProtectionKeySource FromKey(DataProtectionMasterKey key)
    {
        ArgumentNullException.ThrowIfNull(key);
        return new DataProtectionKeySource(() => key);
    }

    /// <summary>No key (development without a credential: the key ring stays unencrypted, never in Production).</summary>
    public static DataProtectionKeySource None() => new(() => null);

    /// <summary>The key, or null when it is missing or unusable (<see cref="Problem"/>).</summary>
    public DataProtectionMasterKey? Key => _loaded.Value.Key;

    /// <summary>Why <see cref="Key"/> is null: <c>missing</c> or <c>invalid</c>; null when the key is usable.</summary>
    public string? Problem => _loaded.Value.Problem;

    /// <summary>The variant file read by <see cref="ReadVariant"/>; null = not known.</summary>
    public string? VariantFile { get; }

    /// <summary>The sealing variant of the credential (<see cref="HostCredentialKeys"/>) or null when not recorded.</summary>
    public string? ReadVariant()
    {
        if (VariantFile is null)
        {
            return null;
        }

        try
        {
            var text = File.Exists(VariantFile) ? File.ReadAllText(VariantFile).Trim() : null;
            return text is not null && HostCredentialKeys.All.Contains(text, StringComparer.Ordinal) ? text : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public void Dispose()
    {
        if (_loaded.IsValueCreated)
        {
            _loaded.Value.Key?.Dispose();
        }
    }

    private static Loaded Load(Func<DataProtectionMasterKey?> load)
    {
        try
        {
            return load() is { } key ? new Loaded(key, null) : new Loaded(null, "missing");
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException or ArgumentException)
        {
            return new Loaded(null, "invalid");
        }
    }

    private sealed record Loaded(DataProtectionMasterKey? Key, string? Problem);
}
