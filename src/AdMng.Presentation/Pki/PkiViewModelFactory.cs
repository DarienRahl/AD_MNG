using AdMng.Presentation.Shell;

namespace AdMng.Presentation.Pki;

/// <summary>
/// Creates the PKI documents of one connection window (scoped, docs/dev/p-certificate-services.md 2.5): the overview and
/// templates documents (W5) and, through <c>PkiViewModelFactory.Security.cs</c>, the security review and the readiness
/// report (W6). <see cref="Matches"/> tells the shell whether an open tab is the document a request names (one tab per
/// kind per window).
/// </summary>
/// <param name="pki">The window's PKI services (the node and the tree use them too).</param>
public sealed partial class PkiViewModelFactory(PkiServices pki)
{
    /// <summary>The window's PKI services.</summary>
    public PkiServices Pki { get; } = pki ?? throw new ArgumentNullException(nameof(pki));

    /// <summary>A new document for <paramref name="request"/> (not loaded yet: the shell calls its <c>LoadAsync</c>).</summary>
    public IShellDocument Create(PkiDocumentRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return request.Kind switch
        {
            PkiDocumentKind.SecurityReview => CreateSecurity(request),
            PkiDocumentKind.Readiness => CreateReadiness(request),
            _ => throw new NotImplementedException("P1 W5: PkiViewModelFactory.Create"),
        };
    }

    /// <summary>The open tab <paramref name="document"/> is the document <paramref name="request"/> names.</summary>
    public static bool Matches(IShellDocument document, PkiDocumentRequest request)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(request);
        return request.Kind is PkiDocumentKind.SecurityReview or PkiDocumentKind.Readiness
            ? MatchesSecurity(document, request)
            : throw new NotImplementedException("P1 W5: PkiViewModelFactory.Matches");
    }
}
