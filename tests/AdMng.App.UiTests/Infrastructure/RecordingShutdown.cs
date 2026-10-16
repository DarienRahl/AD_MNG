using AdMng.App.Services;

namespace AdMng.App.UiTests.Infrastructure;

/// <summary>Records shutdown requests instead of ending the application object shared by all UI tests.</summary>
internal sealed class RecordingShutdown : IAppShutdown
{
    private readonly List<int> _exitCodes = [];

    /// <summary>Exit codes of the shutdown requests so far.</summary>
    public IReadOnlyList<int> ExitCodes => _exitCodes;

    public void Shutdown(int exitCode = 0) => _exitCodes.Add(exitCode);
}
