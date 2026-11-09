using System.Globalization;
using AdMng.Core.Directory;

namespace AdMng.Core.Pki;

/// <summary>How <see cref="PkiOverviewReader.ReadAsync"/> reads.</summary>
public sealed record PkiReadOptions
{
    /// <summary>The page size of the subtree search.</summary>
    public int PageSize { get; init; } = 1000;

    /// <summary>Look up the computer objects of the enterprise CA hosts in the window's domain (off for the Watchdog later).</summary>
    public bool ResolveCaComputers { get; init; } = true;

    /// <summary>The clock of <see cref="PkiSnapshot.ReadAt"/>.</summary>
    public TimeProvider Time { get; init; } = TimeProvider.System;

    /// <summary>The options only.</summary>
    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"{nameof(PkiReadOptions)}(page {PageSize}, CA computers {ResolveCaComputers})");
}

/// <summary>Progress of <see cref="PkiOverviewReader.ReadAsync"/>.</summary>
/// <param name="Objects">PKI objects read so far.</param>
public sealed record PkiReadProgress(int Objects)
{
    /// <summary>The count only.</summary>
    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"{Objects} objects");
}

/// <summary>
/// Reads the forest PKI (docs/dev/p-certificate-services.md 2.1): one paged subtree search of <c>CN=Public Key
/// Services,CN=Services,&lt;config&gt;</c> over <see cref="PkiClasses.All"/> with <see cref="PkiAttributes.OverviewAttributes"/>,
/// the security descriptors and extended DNs, all on the session's DC (rule 4), plus one search per enterprise CA host
/// of the window's domain for its computer object. Never throws for a missing or denied base: the snapshot then carries
/// <see cref="PkiReadNoticeCode.BaseNotFound"/> / <see cref="PkiReadNoticeCode.BaseAccessDenied"/>.
/// </summary>
public static class PkiOverviewReader
{
    /// <summary>Reads the snapshot (cancellable, progress per page).</summary>
    public static Task<PkiSnapshot> ReadAsync(IDirectoryReader reader, PkiReadOptions options, IProgress<PkiReadProgress>? progress, CancellationToken cancellationToken) =>
        throw new NotImplementedException("P1 C1: PkiOverviewReader.ReadAsync");

    /// <summary>
    /// Reads the window domain's domain controllers (<c>PrivilegedAccessScanner.DomainControllerFilter</c>;
    /// <see cref="PkiAttributes.DomainControllerAttributes"/>) with the certificates their objects publish.
    /// </summary>
    public static Task<IReadOnlyList<PkiDomainController>> ReadDomainControllersAsync(IDirectoryReader reader, CancellationToken cancellationToken) =>
        throw new NotImplementedException("P1 C1: PkiOverviewReader.ReadDomainControllersAsync");
}
