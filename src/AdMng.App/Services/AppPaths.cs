using System.IO;
using AdMng.Presentation.Services;

namespace AdMng.App.Services;

/// <summary>
/// Well-known folders: settings live in <c>%APPDATA%\AdMng</c> (roaming profile: UI layout, profiles without passwords,
/// saved views - never directory data, CLAUDE.md rule 1). Tests and the smoke test pass their own folder.
/// </summary>
public sealed class AppPaths : IAppPaths
{
    /// <summary>Name of the application folder under %APPDATA%.</summary>
    public const string FolderName = "AdMng";

    public AppPaths(string? settingsDirectory = null)
    {
        SettingsDirectory = string.IsNullOrWhiteSpace(settingsDirectory)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), FolderName)
            : Path.GetFullPath(settingsDirectory);
    }

    public string SettingsDirectory { get; }
}
