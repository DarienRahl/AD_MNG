using AdMng.Presentation.Shell;

namespace AdMng.Presentation.Pki;

/// <summary>The W6 documents of <see cref="PkiViewModelFactory"/> (docs/dev/p-certificate-services.md 2.5).</summary>
public sealed partial class PkiViewModelFactory
{
    /// <summary><i>Przegląd bezpieczeństwa AD CS</i> (<c>PkiSecurityReviewDocumentViewModel</c>; <see cref="PkiDocumentRequest.FindingCode"/> filters it).</summary>
    private IShellDocument CreateSecurity(PkiDocumentRequest request) =>
        throw new NotImplementedException("P1 W6: PkiViewModelFactory.CreateSecurity");

    /// <summary><i>Gotowość na silne mapowanie i NTAuth</i> (<c>PkiReadinessDocumentViewModel</c>).</summary>
    private IShellDocument CreateReadiness(PkiDocumentRequest request) =>
        throw new NotImplementedException("P1 W6: PkiViewModelFactory.CreateReadiness");

    /// <summary>The open tab is the security review or the readiness report of the request.</summary>
    private static bool MatchesSecurity(IShellDocument document, PkiDocumentRequest request) =>
        throw new NotImplementedException("P1 W6: PkiViewModelFactory.MatchesSecurity");
}
