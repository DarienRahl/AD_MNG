using AdMng.Presentation.Dialogs;

namespace AdMng.Presentation.Correspondence;

/// <summary>
/// Creates the Korespondencja window's view model with the services of the connection window (scoped, design note 6.2).
/// </summary>
public sealed class CorrespondenceViewModelFactory(DialogServices services, CorrespondenceServices correspondence)
{
    /// <summary>The window's services (dialogs, settings, session) of the connection window.</summary>
    public DialogServices Services { get; } = services ?? throw new ArgumentNullException(nameof(services));

    public CorrespondenceServices Correspondence { get; } = correspondence ?? throw new ArgumentNullException(nameof(correspondence));

    /// <summary>A new Korespondencja window for <paramref name="request"/>.</summary>
    public CorrespondenceViewModel Create(CorrespondenceRequest request) => new(Services, Correspondence, request);
}
