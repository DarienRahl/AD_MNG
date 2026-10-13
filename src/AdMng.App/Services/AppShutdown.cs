using System.Windows;

namespace AdMng.App.Services;

/// <summary>Ends the application. Separate from <see cref="Application"/> so the UI tests, which share one
/// application object for the whole test process, can observe a shutdown instead of performing it.</summary>
public interface IAppShutdown
{
    /// <summary>Shuts the application down with the given exit code.</summary>
    void Shutdown(int exitCode = 0);
}

/// <summary>Production implementation: <see cref="Application.Shutdown(int)"/> of the current application.</summary>
public sealed class ApplicationShutdown : IAppShutdown
{
    public void Shutdown(int exitCode = 0) => Application.Current?.Shutdown(exitCode);
}
