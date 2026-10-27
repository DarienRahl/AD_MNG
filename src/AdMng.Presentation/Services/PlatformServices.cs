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

/// <summary>
/// What the clipboard holds for "Wklej sformatowaną treść" (U1c, design note u1c-u1d section 2): the raw bytes of the
/// <c>HTML Format</c> (CF_HTML, UTF-8 with byte offsets), or – when the raw read failed – the string the platform gave for it
/// (parsed in text mode), a PNG (a bitmap converted to PNG by the platform) and the Unicode text. Read on the UI thread;
/// everything else runs off it. <see cref="ToString"/> never shows the content.
/// </summary>
public sealed record ClipboardContent(byte[]? CfHtml, string? CfHtmlText, byte[]? Png, string? Text)
{
    public bool IsEmpty => CfHtml is not { Length: > 0 } && string.IsNullOrEmpty(CfHtmlText) && Png is not { Length: > 0 } && string.IsNullOrEmpty(Text);

    public override string ToString() =>
        $"ClipboardContent(html {CfHtml?.Length ?? CfHtmlText?.Length ?? 0}, png {Png?.Length ?? 0}, text {Text?.Length ?? 0})";
}

/// <summary>System clipboard.</summary>
public interface IClipboardService
{
    /// <summary>
    /// The formatted content of the clipboard (U1c), or null when it cannot be read or holds nothing usable. The default
    /// (scopeless windows, test doubles) has nothing.
    /// </summary>
    ClipboardContent? ReadFormatted() => null;

    /// <summary>
    /// Puts text on the clipboard. Returns false when the clipboard could not be written (another process held it; the
    /// operator was warned) - callers then do not confirm the copy.
    /// </summary>
    bool SetText(string text);

    /// <summary>
    /// Puts a secret (a password) on the clipboard: excluded from clipboard history, cloud clipboard and clipboard
    /// monitors, and cleared after a short time if the clipboard still holds it. Returns false when the clipboard could
    /// not be written (the operator is warned) - the secret was then not handed out.
    /// </summary>
    bool SetSecret(string secret);
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
