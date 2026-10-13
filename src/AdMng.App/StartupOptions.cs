namespace AdMng.App;

/// <summary>Command line options of AdMng.exe.</summary>
/// <param name="SmokeTest"><c>--smoke-test</c>: build the real host, render the windows off-screen and exit (CI).</param>
/// <param name="Language"><c>--lang pl|en</c>: UI language for this run (overrides the settings, not persisted).</param>
/// <param name="SettingsDirectory">
/// Folder for settings.json instead of %APPDATA%\AdMng (<c>--settings-dir</c>; used by the smoke test and UI tests so
/// they never touch the operator's profile).
/// </param>
public sealed record StartupOptions(bool SmokeTest = false, string? Language = null, string? SettingsDirectory = null)
{
    /// <summary>Parses the command line. Unknown arguments are ignored (the shell may pass extra ones).</summary>
    public static StartupOptions Parse(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);
        var options = new StartupOptions();
        for (var i = 0; i < args.Count; i++)
        {
            var arg = args[i];
            if (IsOption(arg, "smoke-test"))
            {
                options = options with { SmokeTest = true };
            }
            else if (IsOption(arg, "lang") && i + 1 < args.Count)
            {
                var language = args[++i].Trim().ToLowerInvariant();
                options = options with { Language = language is "pl" or "en" ? language : null };
            }
            else if (IsOption(arg, "settings-dir") && i + 1 < args.Count)
            {
                options = options with { SettingsDirectory = args[++i] };
            }
        }

        return options;
    }

    private static bool IsOption(string arg, string name) =>
        string.Equals(arg, "--" + name, StringComparison.OrdinalIgnoreCase)
        || string.Equals(arg, "/" + name, StringComparison.OrdinalIgnoreCase);
}
