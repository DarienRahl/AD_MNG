namespace AdMng.Presentation.Readiness;

/// <summary>
/// The last results of the readiness check of one connection window (design note s-pilot-readiness.md 2), as the problem
/// report lists them (section 3). Scoped per connection window; until the check registers its own implementation the
/// Presentation registers an empty default (TryAddScoped, so a registration made before <c>AddAdMngPresentation</c> wins).
/// </summary>
public interface IReadinessResults
{
    /// <summary>The results of the last run in this window (empty before the first run), in check order.</summary>
    IReadOnlyList<ReadinessResultCode> Last { get; }
}

/// <summary>
/// One readiness result as a stable check code and its status (e.g. <c>Connection.Sealing</c>, <c>Ok</c>). Both are
/// identifiers, never directory values: the problem report copies them verbatim.
/// </summary>
public sealed record ReadinessResultCode(string Code, string Status);
