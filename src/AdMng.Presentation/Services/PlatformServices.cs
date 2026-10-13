using AdMng.Presentation.Settings;

namespace AdMng.Presentation.Services;

/// <summary>One entry of a file dialog filter, e.g. <c>new("CSV (*.csv)", "*.csv")</c>.</summary>
public sealed record FileFilter(string Description, string Pattern);

/// <summary>Open/save file dialogs. Returns the chosen path or null when cancelled.</summary>
public interface IFileDialogService
{
    Task<string?> OpenFileAsync(string title, IReadOnlyList<FileFilter> filters);

    Task<string?> SaveFileAsync(string title, string defaultFileName, IReadOnlyList<FileFilter> filters);
}

/// <summary>System clipboard.</summary>
public interface IClipboardService
{
    void SetText(string text);
}

/// <summary>Severity of a toast notification.</summary>
public enum NotificationSeverity
{
    Info,
    Success,
    Warning,
    Error,
}

/// <summary>Non-modal toast notifications (bottom right of the main window). May be called from any thread.</summary>
public interface INotificationService
{
    void Show(NotificationSeverity severity, string message);
}

/// <summary>Applies the light/dark Fluent theme.</summary>
public interface IThemeService
{
    void Apply(ThemePreference preference);
}

/// <summary>Well-known folders of the application.</summary>
public interface IAppPaths
{
    /// <summary>Folder for settings.json (e.g. %APPDATA%\AD_MNG). Created on demand by the settings store.</summary>
    string SettingsDirectory { get; }
}

/// <summary>
/// Gives the connect view model access to the password typed in the view (WPF PasswordBox) only at the
/// moment of connecting. The view model never stores the password in a property or field.
/// </summary>
public interface IPasswordInput
{
    /// <summary>Returns the current password. Callers must not keep the value longer than the bind call.</summary>
    string GetPassword();

    /// <summary>Clears the password box (after a successful connect).</summary>
    void Clear();
}
