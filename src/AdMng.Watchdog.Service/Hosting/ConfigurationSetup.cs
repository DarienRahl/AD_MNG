using AdMng.Watchdog.Core.Diagnostics;
using Microsoft.Extensions.Configuration.Json;
using Microsoft.Extensions.FileProviders;

namespace AdMng.Watchdog.Service.Hosting;

/// <summary>
/// Configuration sources of the service (spec 19.1), in increasing precedence: <c>appsettings.json</c> (+ environment
/// file) next to the binary, the bootstrap file <see cref="BootstrapFilePath"/> (optional – the file root / local
/// administrators edit: <c>/etc/admng-watchdog/appsettings.json</c> on Linux, stage B design 14.1;
/// <c>%ProgramData%\AdMng\Watchdog\config\appsettings.json</c> on Windows), the file named by
/// <see cref="ConfigFileVariable"/> (lab/dev only, refused for a managed service – Windows service or systemd unit),
/// environment variables <c>ADMNG_WATCHDOG__…</c> mapped to the "Watchdog" section, the command line.
/// </summary>
public static partial class ConfigurationSetup
{
    public const string ConfigFileVariable = "ADMNG_WATCHDOG_CONFIG";
    public const string EnvironmentPrefix = "ADMNG_WATCHDOG__";

    /// <summary>Warning: an edit of the watched bootstrap file could not be loaded; the previous settings stay in effect.</summary>
    public const int ReloadFailedEventId = 2002;

    /// <summary>The bootstrap file of the Linux package (root:admng-watchdog 0640, stage B design 11, 14.1).</summary>
    public const string LinuxBootstrapFilePath = "/etc/admng-watchdog/appsettings.json";

    /// <summary>The bootstrap file: <see cref="LinuxBootstrapFilePath"/> on Linux, otherwise under %ProgramData% (or the
    /// platform's common application data folder).</summary>
    public static string BootstrapFilePath => OperatingSystem.IsLinux()
        ? LinuxBootstrapFilePath
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData, Environment.SpecialFolderOption.DoNotVerify),
            "AdMng", "Watchdog", "config", "appsettings.json");

    /// <summary>
    /// Adds the sources; returns the extra configuration file when <see cref="ConfigFileVariable"/> is set. The bootstrap
    /// file is watched, so the settings the service reads live (<c>Authorization</c> – <c>DeniedSids</c> is the emergency
    /// revocation –, <c>Notifications</c>, the subnet lists) apply without a restart; <see cref="ServiceOptionsFactory"/>
    /// pins everything the host consumed when it was built. For a managed service (<paramref name="isManagedService"/>:
    /// Windows service or systemd unit) the extra file is not loaded; its path is still returned, so the start-up checks
    /// refuse to start (exit code 6).
    /// </summary>
    public static string? AddSources(ConfigurationManager configuration, IReadOnlyList<string> args, bool isManagedService) =>
        AddSources(configuration, args, isManagedService, BootstrapFilePath, reloadBootstrapFile: true);

    /// <summary><see cref="AddSources(ConfigurationManager, IReadOnlyList{string}, bool)"/> with an explicit bootstrap file
    /// (tests) and optionally without watching it (one-shot commands).</summary>
    internal static string? AddSources(
        ConfigurationManager configuration,
        IReadOnlyList<string> args,
        bool isManagedService,
        string bootstrapFilePath,
        bool reloadBootstrapFile) =>
        AddSources(configuration, args, isManagedService, bootstrapFilePath, reloadBootstrapFile, Environment.GetEnvironmentVariable(ConfigFileVariable));

    /// <summary>The sources with an explicit value of <see cref="ConfigFileVariable"/> (tests); a null
    /// <paramref name="bootstrapFilePath"/> leaves the bootstrap file out (<c>--smoke-test</c>, which must not depend on
    /// the machine's configuration nor need read access to it).</summary>
    internal static string? AddSources(
        ConfigurationManager configuration,
        IReadOnlyList<string> args,
        bool isManagedService,
        string? bootstrapFilePath,
        bool reloadBootstrapFile,
        string? extraConfigFile)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(args);

        if (!string.IsNullOrWhiteSpace(bootstrapFilePath))
        {
            // Watched only when its folder exists (the package / install script creates it): for a missing folder the file
            // provider would watch the nearest existing ancestor (%ProgramData%, /etc) recursively.
            var watch = reloadBootstrapFile && System.IO.Directory.Exists(Path.GetDirectoryName(bootstrapFilePath));
            configuration.AddJsonFile(bootstrapFilePath, optional: true, reloadOnChange: watch);
        }

        string? extraPath = null;
        if (!string.IsNullOrWhiteSpace(extraConfigFile))
        {
            extraPath = Path.GetFullPath(extraConfigFile.Trim());
            if (!isManagedService)
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

    /// <summary>
    /// Reports an edit of the watched bootstrap file that cannot be loaded (e.g. malformed JSON) as warning
    /// <see cref="ReloadFailedEventId"/> – type and code only, never a value of the file. The provider still throws in its
    /// change callback, so the options are not rebuilt and the previous settings stay in effect: an administrator whose
    /// emergency <c>DeniedSids</c> edit has a syntax error must learn that it did not apply. Call after the host was built
    /// – a file that cannot be loaded at start still fails the start (exit code 2, event 1010).
    /// </summary>
    public static void ReportReloadFailures(IConfigurationBuilder configuration, string bootstrapFilePath, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentException.ThrowIfNullOrWhiteSpace(bootstrapFilePath);
        ArgumentNullException.ThrowIfNull(logger);
        var bootstrap = Path.GetFullPath(bootstrapFilePath);
        foreach (var source in configuration.Sources.OfType<JsonConfigurationSource>())
        {
            if (source.ReloadOnChange
                && source.FileProvider is PhysicalFileProvider files
                && source.Path is { Length: > 0 } path
                && string.Equals(Path.GetFullPath(Path.Combine(files.Root, path)), bootstrap, StringComparison.OrdinalIgnoreCase))
            {
                source.OnLoadException = context => Log.ReloadFailed(logger, SafeExceptionLog.ErrorCode(context.Exception));
            }
        }
    }

    private static partial class Log
    {
        [LoggerMessage(EventId = ReloadFailedEventId, Level = LogLevel.Warning,
            Message = "The edited bootstrap file (appsettings.json in the config folder) could not be loaded ({ErrorCode}); the previous settings stay in effect until it is fixed (check the JSON syntax, AdMng.Watchdog.exe --check-config).")]
        public static partial void ReloadFailed(ILogger logger, string errorCode);
    }
}
