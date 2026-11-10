using AdMng.Presentation.Shell;

namespace AdMng.Presentation.Pki;

/// <summary>The W6 documents of <see cref="PkiViewModelFactory"/> (docs/dev/p-certificate-services.md 2.5).</summary>
public sealed partial class PkiViewModelFactory
{
    /// <summary><i>Przegląd bezpieczeństwa AD CS</i> (<see cref="PkiSecurityReviewDocumentViewModel"/>; <see cref="PkiDocumentRequest.FindingCode"/> filters it).</summary>
    private IShellDocument CreateSecurity(PkiDocumentRequest request) => new PkiSecurityReviewDocumentViewModel(Pki, request);

    /// <summary><i>Gotowość na silne mapowanie i NTAuth</i> (<see cref="PkiReadinessDocumentViewModel"/>).</summary>
    private IShellDocument CreateReadiness(PkiDocumentRequest request) => new PkiReadinessDocumentViewModel(Pki);

    /// <summary>
    /// The open tab is the security review or the readiness report of the request (one tab per kind and window; an open review
    /// keeps its filters – <see cref="PkiSecurityReviewDocumentViewModel.ShowCode"/> applies a request's finding code to it).
    /// </summary>
    private static bool MatchesSecurity(IShellDocument document, PkiDocumentRequest request) => request.Kind switch
    {
        PkiDocumentKind.SecurityReview => document is PkiSecurityReviewDocumentViewModel,
        PkiDocumentKind.Readiness => document is PkiReadinessDocumentViewModel,
        _ => false,
    };
}
