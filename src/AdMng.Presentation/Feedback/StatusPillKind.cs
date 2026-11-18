namespace AdMng.Presentation.Feedback;

/// <summary>
/// What a status pill tells about one row or one item - the result of a check, the health of a part, the state of a
/// channel (track V, stage V2b-5b; docs/dev/v2-status-controls.md). It is the state of the <em>item</em>; what a
/// <em>page</em> is doing or has done is a <see cref="PageStatusKind"/> in its status line. The pill shows the picture of
/// the kind and a short word, so the meaning is never told by colour alone.
/// </summary>
public enum StatusPillKind
{
    /// <summary>Not checked, not known, not reported, not switched on (the default).</summary>
    Unknown,

    /// <summary>Healthy, passed, done.</summary>
    Ok,

    /// <summary>Information that is neither good nor bad.</summary>
    Info,

    /// <summary>Works, but something needs attention.</summary>
    Attention,

    /// <summary>Does not work, failed.</summary>
    Error,
}
