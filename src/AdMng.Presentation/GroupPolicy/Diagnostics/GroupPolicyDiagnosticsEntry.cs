namespace AdMng.Presentation.GroupPolicy.Diagnostics;

/// <summary>The entry points of "Diagnostyka zasad grupy": one request, opened (or selected when open) by the window's shell.</summary>
public static class GroupPolicyDiagnosticsEntry
{
    /// <summary>The request of the diagnostics document (one tab per connection window).</summary>
    public static GroupPolicyDocumentRequest Request { get; } = new(GroupPolicyDocumentKind.Diagnostics);

    /// <summary>
    /// Opens "Diagnostyka zasad grupy" of the window's domain, or selects its tab when it is open (the GPO list's toolbar, the
    /// tree's menu). False when no shell listens.
    /// </summary>
    public static bool OpenDiagnostics(this GroupPolicyActions actions)
    {
        ArgumentNullException.ThrowIfNull(actions);
        return actions.Services.OpenDocument(Request);
    }
}
