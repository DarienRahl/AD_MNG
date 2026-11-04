using AdMng.Presentation.Infrastructure;
using AdMng.Presentation.Localization;
using AdMng.Presentation.Readiness;
using CommunityToolkit.Mvvm.Input;

namespace AdMng.Presentation.Shell;

/// <summary>
/// "Gotowość środowiska" (stage S, design note docs/dev/s-pilot-readiness.md 2): <i>Narzędzia → Sprawdź gotowość
/// środowiska…</i>, the same entry in <i>Pomoc</i> and the palette entry <c>tools.readiness</c> open the tab of this window
/// (one per connection window) or select it when it is open. A new tab of a connected window starts the quick check.
/// </summary>
public sealed partial class ShellViewModel
{
    /// <summary>The readiness check can be opened in this window (the host registered its services).</summary>
    public bool IsReadinessAvailable => _readinessFactory is not null;

    private IEnumerable<CommandDescriptor> ReadinessCommandDescriptors() =>
    [
        new("tools.readiness", L.Readiness.Command, L.CommandCategories.Tools, string.Empty, ShowReadinessCommand),
    ];

    /// <summary>Opens the readiness tab (or selects it); null when the window has no readiness services.</summary>
    public ReadinessDocumentViewModel? OpenReadiness()
    {
        if (_readinessFactory is null || _disposed)
        {
            return null;
        }

        var document = Documents.OfType<ReadinessDocumentViewModel>().FirstOrDefault();
        if (document is null)
        {
            document = _readinessFactory();
            Documents.Add(document);
            if (document.HasSession)
            {
                document.CheckAsync(ReadinessDepth.Quick).Forget();
            }
        }

        SelectedTab = document;
        return document;
    }

    /// <summary>"Sprawdź gotowość środowiska…".</summary>
    [RelayCommand(CanExecute = nameof(CanShowReadiness))]
    private void ShowReadiness() => OpenReadiness();

    private bool CanShowReadiness() => _readinessFactory is not null;
}
