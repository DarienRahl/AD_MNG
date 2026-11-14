namespace AdMng.Presentation.GroupPolicy.Cleanup;

/// <summary>
/// K7 contract (docs/dev/k-computers-and-gpo.md 21.6) – the document itself: W5. Opens "Porządki w GPO" (one tab per
/// connection window).
/// </summary>
public static class GpoCleanupEntry
{
    /// <summary>The request of the cleanup document.</summary>
    public static GroupPolicyDocumentRequest Request { get; } = new(GroupPolicyDocumentKind.Cleanup);

    /// <summary>Opens "Porządki w GPO" (or selects its tab). False when no shell listens. Until W5 registers the document the factory refuses the kind.</summary>
    public static bool OpenCleanup(this GroupPolicyActions actions)
    {
        ArgumentNullException.ThrowIfNull(actions);
        return actions.Services.OpenDocument(Request);
    }
}
