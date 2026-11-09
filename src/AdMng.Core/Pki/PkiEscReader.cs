using AdMng.Core.PrivilegedAccess;

namespace AdMng.Core.Pki;

/// <summary>
/// Builds the <see cref="PkiEscSnapshot"/> (2.4): takes the precomputed expansion (no second account pass), calls
/// <c>DirectoryAclGrantSource.CollectPublicKeyAsync</c> with the snapshot's CA computer DNs, classifies every grant
/// trustee and resolves the OID group links against <see cref="PrivilegeScanContext.Groups"/>. All reads on the session's DC.
/// </summary>
public static class PkiEscReader
{
    /// <summary>Reads what the analyzer needs (cancellable, with progress).</summary>
    public static Task<PkiEscSnapshot> ReadAsync(
        PrivilegeScanContext context,
        PkiSnapshot snapshot,
        PkiEnrollmentExpansion expansion,
        PkiEscReadOptions options,
        IProgress<PkiEscProgress>? progress,
        CancellationToken cancellationToken) =>
        throw new NotImplementedException("P1 C4: PkiEscReader.ReadAsync");
}
