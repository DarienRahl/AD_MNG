using System.Windows;
using AdMng.Presentation.Correspondence;
using Microsoft.Extensions.DependencyInjection;

namespace AdMng.App.Services;

/// <summary>The Korespondencja window of a connection window (U5): one from the Tools menu.</summary>
public sealed partial class ToolWindows
{
    /// <summary>Singleton key of the Korespondencja window opened from the Tools menu (one per connection window).</summary>
    public const string CorrespondenceKey = "tools:correspondence";

    private CorrespondenceViewModelFactory CorrespondenceFactory => _services.GetRequiredService<CorrespondenceViewModelFactory>();

    /// <summary>"Korespondencja (Outlook)…" (modeless; an open one is activated).</summary>
    public Window ShowCorrespondence() =>
        ShowSingleton(CorrespondenceKey, () => CorrespondenceFactory.Create(new CorrespondenceRequest([])));
}
