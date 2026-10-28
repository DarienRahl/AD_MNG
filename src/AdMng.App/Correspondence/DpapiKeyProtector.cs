using System.Security.Cryptography;
using System.Text;
using AdMng.Presentation.Correspondence;

namespace AdMng.App.Correspondence;

/// <summary>
/// Protects the Korespondencja journal key with DPAPI (<c>ProtectedData</c>, CurrentUser, entropy
/// <see cref="Entropy"/>; design note 7.6, 9.4): only the same Windows user on the same machine (or with a roaming
/// profile, the same user) can read the key back, so the keyed address hashes of the journal cannot be reversed by a
/// dictionary of addresses by anybody else. Never a pass-through. Application singleton; the constructor touches nothing.
/// </summary>
public sealed class DpapiKeyProtector : ICorrespondenceKeyProtector
{
    /// <summary>The DPAPI entropy of the journal key.</summary>
    public const string Entropy = "AdMng.CorrespondenceJournal";

    private static readonly byte[] EntropyBytes = Encoding.UTF8.GetBytes(Entropy);

    /// <summary>The DPAPI blob of <paramref name="data"/> (CurrentUser scope, with the entropy).</summary>
    /// <exception cref="CryptographicException">DPAPI refused (no user profile loaded).</exception>
    public byte[] Protect(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        return ProtectedData.Protect(data, EntropyBytes, DataProtectionScope.CurrentUser);
    }

    /// <summary>The data of a blob made by <see cref="Protect"/>.</summary>
    /// <exception cref="CryptographicException">The blob is damaged, was made by another user or with another entropy
    /// (the journal then starts with a new key).</exception>
    public byte[] Unprotect(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        return ProtectedData.Unprotect(data, EntropyBytes, DataProtectionScope.CurrentUser);
    }

    public override string ToString() => nameof(DpapiKeyProtector);
}
