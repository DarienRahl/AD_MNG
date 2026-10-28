using AdMng.Presentation.Correspondence;

namespace AdMng.App.Correspondence;

/// <summary>
/// Protects the Korespondencja journal key with DPAPI (<c>ProtectedData</c>, CurrentUser, entropy
/// <see cref="Entropy"/>; design note 9.4). Application singleton; the constructor touches nothing.
/// </summary>
public sealed class DpapiKeyProtector : ICorrespondenceKeyProtector
{
    /// <summary>The DPAPI entropy of the journal key.</summary>
    public const string Entropy = "AdMng.CorrespondenceJournal";

    // Scaffold: the App track implements DPAPI (System.Security.Cryptography.ProtectedData). Never a pass-through: an
    // unprotected key would make the address hashes of the journal reversible by a dictionary.
    public byte[] Protect(byte[] data) => throw new NotImplementedException("U5 T7a app");

    public byte[] Unprotect(byte[] data) => throw new NotImplementedException("U5 T7a app");

    public override string ToString() => nameof(DpapiKeyProtector);
}
