using CommunityToolkit.Mvvm.Input;

namespace AdMng.Presentation.Shell;

/// <summary>"Pomoc → Zgłoś problem…" (palette <c>help.reportProblem</c>, design note s-pilot-readiness.md 3).</summary>
public sealed partial class ShellViewModel
{
    /// <summary>The value-free problem report of this window (also while not connected: it then says so).</summary>
    [RelayCommand]
    private Task ShowProblemReportAsync() => _dialogs.ShowProblemReportAsync();
}
