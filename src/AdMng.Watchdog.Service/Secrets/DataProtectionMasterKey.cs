using System.Security.Cryptography;

namespace AdMng.Watchdog.Service.Secrets;

/// <summary>
/// The 256-bit key that encrypts the Data Protection key ring on Linux (stage B design 10). systemd decrypts the
/// encrypted credential <c>/etc/admng-watchdog/credentials/dp-key.cred</c> (<c>LoadCredentialEncrypted=dp-key:…</c>) into
/// <c>$CREDENTIALS_DIRECTORY/dp-key</c> – a non-swappable ramfs private to the unit; the service reads it once into a
/// pinned array that is zeroed on dispose. The key never appears in logs, exceptions or <see cref="ToString"/>; only its
/// id (<see cref="KeyId"/>, the first 8 bytes of its SHA-256) does.
/// </summary>
public sealed class DataProtectionMasterKey : IDisposable
{
    /// <summary>The credential name (<c>LoadCredentialEncrypted=dp-key:…</c>).</summary>
    public const string CredentialName = "dp-key";

    /// <summary>The directory systemd passes the decrypted credentials in.</summary>
    public const string CredentialsDirectoryVariable = "CREDENTIALS_DIRECTORY";

    /// <summary>AES-256 key length in bytes.</summary>
    public const int KeyLength = 32;

    private readonly byte[] _key;
    private bool _disposed;

    private DataProtectionMasterKey(byte[] pinnedKey)
    {
        _key = pinnedKey;
        KeyId = ComputeKeyId(pinnedKey);
    }

    /// <summary>Lower-case hex of the first 8 bytes of SHA-256(key): names the key in the encrypted XML (<c>kid</c>), so a
    /// key ring written with another credential is recognised as such.</summary>
    public string KeyId { get; }

    /// <summary>The key bytes (encryptor, decryptor).</summary>
    internal ReadOnlySpan<byte> Key
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _key;
        }
    }

    /// <summary>A key from <paramref name="key"/> (copied into a pinned array; tests and the credential reader).</summary>
    /// <exception cref="ArgumentException">Not <see cref="KeyLength"/> bytes.</exception>
    public static DataProtectionMasterKey FromBytes(ReadOnlySpan<byte> key)
    {
        if (key.Length != KeyLength)
        {
            throw new ArgumentException($"The key must be {KeyLength} bytes.", nameof(key));
        }

        var pinned = GC.AllocateArray<byte>(KeyLength, pinned: true);
        key.CopyTo(pinned);
        return new DataProtectionMasterKey(pinned);
    }

    /// <summary>
    /// Reads <c>&lt;<paramref name="credentialsDirectory"/>&gt;/dp-key</c>. Null when the directory is not set or the
    /// credential is missing (the process does not run under the unit).
    /// </summary>
    /// <exception cref="InvalidDataException">The credential does not hold exactly <see cref="KeyLength"/> bytes.</exception>
    public static DataProtectionMasterKey? FromCredentialsDirectory(string? credentialsDirectory)
    {
        if (string.IsNullOrWhiteSpace(credentialsDirectory))
        {
            return null;
        }

        var path = Path.Combine(credentialsDirectory, CredentialName);
        if (!File.Exists(path))
        {
            return null;
        }

        // Unbuffered reads straight into a pinned buffer: no copy of the key is left in a FileStream buffer.
        var buffer = GC.AllocateArray<byte>(KeyLength + 1, pinned: true);
        try
        {
            var read = 0;
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 0))
            {
                int count;
                while (read < buffer.Length && (count = stream.Read(buffer, read, buffer.Length - read)) > 0)
                {
                    read += count;
                }
            }

            if (read != KeyLength)
            {
                throw new InvalidDataException($"The credential {CredentialName} must hold exactly {KeyLength} bytes.");
            }

            return FromBytes(buffer.AsSpan(0, KeyLength));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(buffer);
        }
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _disposed = true;
            CryptographicOperations.ZeroMemory(_key);
        }
    }

    public override string ToString() => $"DataProtectionMasterKey(kid={KeyId})";

    private static string ComputeKeyId(ReadOnlySpan<byte> key)
    {
        Span<byte> hash = stackalloc byte[SHA256.HashSizeInBytes];
        SHA256.HashData(key, hash);
        return Convert.ToHexStringLower(hash[..8]);
    }
}
