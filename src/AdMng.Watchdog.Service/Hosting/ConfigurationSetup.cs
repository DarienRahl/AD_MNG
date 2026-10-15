namespace AdMng.Watchdog.Service.Hosting;

/// <summary>
/// Configuration sources of the service (spec 19.1), in increasing precedence: <c>appsettings.json</c> (+ environment
/// file) next to the binary, <c>%ProgramData%\AdMng\Watchdog\config\appsettings.json</c> (optional – the bootstrap file
/// local administrators edit), the file named by <see cref="ConfigFileVariable"/> (lab/dev only, refused for the Windows
/// service), environment variables <c>ADMNG_WATCHDOG__…</c> mapped to the "Watchdog" section, the command line.
/// </summary>
public static class ConfigurationSetup
{
    public const string ConfigFileVariable = "ADMNG_WATCHDOG_CONFIG";
    public const string EnvironmentPrefix = "ADMNG_WATCHDOG__";

    /// <summary>The bootstrap file under %ProgramData% (or the platform's common application data folder).</summary>
    public static string BootstrapFilePath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData, Environment.SpecialFolderOption.DoNotVerify),
            "AdMng", "Watchdog", "config", "appsettings.json");

    /// <summary>
    /// Adds the sources; returns the extra configuration file when <see cref="ConfigFileVariable"/> is set. The bootstrap
    /// file is watched, so the settings the service reads live (<c>Authorization</c> – <c>DeniedSids</c> is the emergency
    /// revocation –, <c>Notifications</c>, the subnet lists) apply without a restart; <see cref="ServiceOptionsFactory"/>
    /// pins everything the host consumed when it was built.
    /// </summary>
    public static string? AddSources(ConfigurationManager configuration, IReadOnlyList<string> args, bool isWindowsService) =>
        AddSources(configuration, args, isWindowsService, BootstrapFilePath, reloadBootstrapFile: true);

    /// <summary><see cref="AddSources(ConfigurationManager, IReadOnlyList{string}, bool)"/> with an explicit bootstrap file
    /// (tests) and optionally without watching it (one-shot commands).</summary>
    internal static string? AddSources(
        ConfigurationManager configuration,
        IReadOnlyList<string> args,
        bool isWindowsService,
        string bootstrapFilePath,
        bool reloadBootstrapFile)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(args);
        ArgumentException.ThrowIfNullOrWhiteSpace(bootstrapFilePath);

        // Watched only when config\ exists (the install script creates it): for a missing folder the file provider would
        // watch the nearest existing ancestor (%ProgramData%) recursively.
        var watch = reloadBootstrapFile && System.IO.Directory.Exists(Path.GetDirectoryName(bootstrapFilePath));
        configuration.AddJsonFile(bootstrapFilePath, optional: true, reloadOnChange: watch);
        var extra = Environment.GetEnvironmentVariable(ConfigFileVariable);
        string? extraPath = null;
        if (!string.IsNullOrWhiteSpace(extra))
        {
            extraPath = Path.GetFullPath(extra.Trim());
            if (!isWindowsService)
            {
                configuration.AddJsonFile(extraPath, optional: false, reloadOnChange: false);
            }
        }

        var environment = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (System.Collections.DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            if (entry.Key is string key && key.StartsWith(EnvironmentPrefix, StringComparison.OrdinalIgnoreCase) && key.Length > EnvironmentPrefix.Length)
            {
                environment[ServiceOptions.SectionName + ":" + key[EnvironmentPrefix.Length..].Replace("__", ":", StringComparison.Ordinal)] = entry.Value as string;
            }
        }

        configuration.AddInMemoryCollection(environment);
        configuration.AddCommandLine([.. args]);
        return extraPath;
    }
}
