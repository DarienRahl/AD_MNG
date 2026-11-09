using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace AdMng.Core.Pki.Tls;

/// <summary>
/// The pure checks of <see cref="DcCertificateEvaluation"/> and <see cref="KdcCertificateEvaluation"/>
/// (docs/dev/p-certificate-services.md 2.1): host-name matching, the chain to the AD roots (custom root trust, nothing
/// downloaded, no revocation check) and the issuer in NTAuth. Every certificate loaded here is disposed here.
/// </summary>
internal static class CertificateChecks
{
    public const string ServerAuthentication = "1.3.6.1.5.5.7.3.1";
    public const string SmartCardLogon = "1.3.6.1.4.1.311.20.2.2";
    public const string KdcAuthentication = "1.3.6.1.5.2.3.5";

    private const string CommonNameOid = "2.5.4.3";

    /// <summary>Loads a DER certificate, or null when it is malformed.</summary>
    public static X509Certificate2? TryLoad(ReadOnlySpan<byte> der)
    {
        if (PkiDer.IsPlaceholder(der))
        {
            return null;
        }

        try
        {
            return X509CertificateLoader.LoadCertificate(der);
        }
        catch (CryptographicException)
        {
            return null;
        }
    }

    /// <summary>The most specific common name of the subject, or null.</summary>
    public static string? CommonName(X509Certificate2 certificate)
    {
        foreach (var rdn in certificate.SubjectName.EnumerateRelativeDistinguishedNames())
        {
            if (!rdn.HasMultipleElements && rdn.GetSingleElementType().Value == CommonNameOid && rdn.GetSingleElementValue() is { Length: > 0 } value)
            {
                return value;
            }
        }

        return null;
    }

    /// <summary>
    /// RFC 6125 6.4: a SAN DNS entry matches <paramref name="host"/> (the subject CN only when the certificate has no SAN
    /// DNS entry), case-insensitively, without a trailing dot; a wildcard only as the whole left-most label
    /// (<c>*.contoso.test</c> matches <c>dc01.contoso.test</c>, not <c>contoso.test</c> or <c>a.b.contoso.test</c>).
    /// </summary>
    public static bool NameMatches(IReadOnlyList<string> sanDns, string? commonName, string host)
    {
        var target = Normalize(host);
        if (target.Length == 0)
        {
            return false;
        }

        return sanDns.Count > 0 ? sanDns.Any(name => DnsMatches(name, target)) : commonName is not null && DnsMatches(commonName, target);
    }

    /// <summary>
    /// Whether the certificate's issuer is in <paramref name="store"/> (NTAuth): a certificate there whose subject is the
    /// leaf's issuer (as encoded), whose Subject Key Identifier is the leaf's Authority Key Identifier (when both have one –
    /// a CA certificate without SKI, as older third-party CAs write, is decided by the signature) and whose key verifies
    /// the leaf's signature (when that can be checked).
    /// </summary>
    public static bool IssuerIn(X509Certificate2 leaf, CertificateSummary leafSummary, IReadOnlyList<byte[]> store)
    {
        var issuer = leaf.IssuerName.RawData;
        foreach (var der in store)
        {
            using var candidate = TryLoad(der);
            if (candidate is null || !candidate.SubjectName.RawData.AsSpan().SequenceEqual(issuer))
            {
                continue;
            }

            if (leafSummary.AuthorityKeyIdentifier is { } aki
                && SubjectKeyIdentifier(candidate) is { } ski
                && !string.Equals(ski, aki, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (PkiDer.VerifySignature(leaf.RawDataMemory, candidate) is false)
            {
                continue;
            }

            return true;
        }

        return false;
    }

    /// <summary>
    /// Builds the chain of <paramref name="leaf"/> with <c>CustomRootTrust</c> = <see cref="PkiTrustMaterial.Roots"/>, the
    /// extra store = <paramref name="presentedIntermediates"/> + the AD intermediates + NTAuth, <c>NoCheck</c>, downloads
    /// disabled and the verification time <paramref name="now"/>. It chains when the chain ends at one of the AD roots and
    /// no element reports a problem – except the leaf's own validity period, which <c>IsTimeValid</c> reports apart (an
    /// expired intermediate does break the chain). Also returns the <c>X509ChainStatusFlags</c> names of the build.
    /// </summary>
    public static (bool ChainsToRoot, IReadOnlyList<string> Status) BuildChain(
        X509Certificate2 leaf,
        IEnumerable<byte[]> presentedIntermediates,
        PkiTrustMaterial trust,
        DateTimeOffset now)
    {
        var roots = LoadAll(trust.Roots);
        var extra = LoadAll(presentedIntermediates.Concat(trust.Intermediates).Concat(trust.NtAuth));
        var chain = new X509Chain();
        try
        {
            var policy = chain.ChainPolicy;
            policy.TrustMode = X509ChainTrustMode.CustomRootTrust;
            policy.CustomTrustStore.AddRange(roots.ToArray());
            policy.ExtraStore.AddRange(extra.ToArray());
            policy.RevocationMode = X509RevocationMode.NoCheck;
            policy.DisableCertificateDownloads = true;
            policy.VerificationTime = now.UtcDateTime;
            policy.VerificationTimeIgnored = false;
            try
            {
                chain.Build(leaf);
            }
            catch (CryptographicException)
            {
                return (false, [nameof(X509ChainStatusFlags.PartialChain)]);
            }

            var status = chain.ChainStatus
                .Select(s => s.Status)
                .Where(s => s != X509ChainStatusFlags.NoError)
                .Distinct()
                .Select(s => s.ToString())
                .ToList();
            var elements = chain.ChainElements;
            if (roots.Count == 0 || elements.Count == 0)
            {
                return (false, status);
            }

            var rootHashes = trust.Roots.Select(r => PkiDer.Sha256Hex(r)).ToHashSet(StringComparer.Ordinal);
            var anchored = rootHashes.Contains(PkiDer.Sha256Hex(elements[^1].Certificate.RawDataMemory.Span));
            var clean = true;
            for (var i = 0; i < elements.Count && clean; i++)
            {
                foreach (var elementStatus in elements[i].ChainElementStatus)
                {
                    var flag = elementStatus.Status;
                    if (flag == X509ChainStatusFlags.NoError || (i == 0 && flag is X509ChainStatusFlags.NotTimeValid or X509ChainStatusFlags.NotTimeNested))
                    {
                        continue;
                    }

                    clean = false;
                }
            }

            return (anchored && clean, status);
        }
        finally
        {
            foreach (var element in chain.ChainElements)
            {
                if (!ReferenceEquals(element.Certificate, leaf))
                {
                    element.Certificate.Dispose();
                }
            }

            chain.Dispose();
            DisposeAll(roots);
            DisposeAll(extra);
        }
    }

    private static string? SubjectKeyIdentifier(X509Certificate2 certificate)
    {
        try
        {
            return certificate.Extensions.OfType<X509SubjectKeyIdentifierExtension>().FirstOrDefault()?.SubjectKeyIdentifier;
        }
        catch (CryptographicException)
        {
            return null;
        }
    }

    private static List<X509Certificate2> LoadAll(IEnumerable<byte[]> values)
    {
        var certificates = new List<X509Certificate2>();
        foreach (var der in values)
        {
            if (TryLoad(der) is { } certificate)
            {
                certificates.Add(certificate);
            }
        }

        return certificates;
    }

    private static void DisposeAll(List<X509Certificate2> certificates)
    {
        foreach (var certificate in certificates)
        {
            certificate.Dispose();
        }
    }

    private static string Normalize(string name) => name.Trim().TrimEnd('.');

    private static bool DnsMatches(string pattern, string host)
    {
        var name = Normalize(pattern);
        if (string.Equals(name, host, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (!name.StartsWith("*.", StringComparison.Ordinal))
        {
            return false;
        }

        var suffix = name[2..];
        var dot = host.IndexOf('.', StringComparison.Ordinal);
        return suffix.Contains('.', StringComparison.Ordinal) && !suffix.Contains('*', StringComparison.Ordinal)
            && dot > 0 && string.Equals(host[(dot + 1)..], suffix, StringComparison.OrdinalIgnoreCase);
    }
}
