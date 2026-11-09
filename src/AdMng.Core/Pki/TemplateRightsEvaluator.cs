using AdMng.Core.Directory;
using AdMng.Core.PrivilegedAccess;

namespace AdMng.Core.Pki;

/// <summary>
/// "Kto może się zarejestrować / zmienić" (decision 5, docs/dev/p-certificate-services.md 2.3) – a separate pure Core
/// evaluator, not U4b: AccessCheck semantics over a template DACL, each right decided by the first ACE in DACL order that
/// applies to the token and covers that right (deny ACEs honoured), then one streamed account pass per window.
/// </summary>
public static class TemplateRightsEvaluator
{
    /// <summary>
    /// Decodes a template's security descriptor into atomic rights in DACL order (inherit-only ACEs and ACEs inherited for
    /// another class skipped, the mask normalized); unparseable → <see cref="TemplateAcl.IsEvaluated"/> false.
    /// </summary>
    public static TemplateAcl Evaluate(ReadOnlySpan<byte> securityDescriptor, AclObjectTypes types) =>
        throw new NotImplementedException("P1 C3: TemplateRightsEvaluator.Evaluate");

    /// <summary>
    /// AccessCheck for one right: the first ACE in DACL order whose SID is in <paramref name="tokenSids"/> and whose right is
    /// <paramref name="right"/> decides (deny → false, allow → true); no such ACE → false.
    /// </summary>
    public static bool Decide(IReadOnlySet<string> tokenSids, TemplateAcl acl, TemplateRight right) =>
        throw new NotImplementedException("P1 C3: TemplateRightsEvaluator.Decide");

    /// <summary>The trustee level, immediate and synchronous: per template and right the trustees that hold it (counts -1).</summary>
    public static IReadOnlyList<TemplateRights> TrusteeLevel(IReadOnlyList<CertificateTemplate> templates, AclObjectTypes types, PrincipalClassifier classifier) =>
        throw new NotImplementedException("P1 C3: TemplateRightsEvaluator.TrusteeLevel");

    /// <summary>
    /// The one account pass (<c>PrivilegedAccessScanner.AccountFilter</c>, <see cref="PkiAttributes.AccountAttributes"/>):
    /// tokens through the graph, decisions memoised per distinct key, counts + the first accounts per right, the mapped
    /// accounts; cancellable, progress every 500 accounts.
    /// </summary>
    public static Task<PkiEnrollmentExpansion> ExpandAsync(
        IDirectorySession session,
        IReadOnlyList<CertificateTemplate> templates,
        PkiExpansionContext context,
        IProgress<PkiExpansionProgress>? progress,
        CancellationToken cancellationToken) =>
        throw new NotImplementedException("P1 C3: TemplateRightsEvaluator.ExpandAsync");
}
