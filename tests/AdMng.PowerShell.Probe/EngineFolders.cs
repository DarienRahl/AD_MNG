using System.Reflection;

namespace AdMng.PowerShell.Probe;

/// <summary>
/// Where the engine of a process keeps files of its own, as that engine finds them: its cache folder (<c>telemetry.uuid</c>, the
/// default module analysis cache, startup data) and its user configuration folder (the user's <c>powershell.config.json</c>,
/// profiles, modules), read from the engine's own <c>Platform.CacheDirectory</c> and <c>Platform.ConfigDirectory</c>, so the "no
/// files" test cannot drift from the engine; on Windows also the Windows PowerShell folders a PowerShell host is known to write (its
/// module analysis cache, the PSReadLine history, its Documents folder), found the way the engine finds a folder
/// (<see cref="Environment.SpecialFolderOption.DoNotVerify"/>); and the temporary folder. The child process reports its folders and
/// the parent checks that each one is a folder it watches (audit TI-1: on Windows the engine finds its folders through the
/// known-folder API, and whether the child's environment variables move them is up to that API, not to the test).
/// </summary>
public sealed record EngineFolders(string Cache, string Config, string Temp, IReadOnlyList<string> Watched)
{
    /// <summary>The name the probe's control gives its own file in the engine's cache folder (the parent removes it from a real folder).</summary>
    public const string ControlFilePrefix = "admng-no-files-control-";

    private const string Prefix = "engine.";

    /// <summary>The folders of the engine in this process.</summary>
    public static EngineFolders OfThisProcess()
    {
        var platform = typeof(System.Management.Automation.PSObject).Assembly.GetType("System.Management.Automation.Platform", throwOnError: true)!;
        var cache = EngineField(platform, "CacheDirectory");
        var config = EngineField(platform, "ConfigDirectory");
        List<string> watched = [cache, config];
        if (OperatingSystem.IsWindows())
        {
            watched.Add(Below(Environment.SpecialFolder.LocalApplicationData, "Microsoft", "Windows", "PowerShell"));
            watched.Add(Below(Environment.SpecialFolder.ApplicationData, "Microsoft", "Windows", "PowerShell"));
            watched.Add(Below(Environment.SpecialFolder.MyDocuments, "WindowsPowerShell"));
        }

        return new EngineFolders(cache, config, Path.GetTempPath(), [.. watched.Where(folder => folder.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase)]);
    }

    /// <summary>The lines a child adds to its report.</summary>
    public string ToReport() =>
        string.Concat(
            $"{Prefix}cache={Cache}\n{Prefix}config={Config}\n{Prefix}temp={Temp}\n",
            string.Concat(Watched.Select(folder => $"{Prefix}watch={folder}\n")));

    /// <summary>The folders a child reported (<see cref="ToReport"/>).</summary>
    public static EngineFolders Parse(string report)
    {
        string? cache = null;
        string? config = null;
        string? temp = null;
        List<string> watched = [];
        foreach (var line in report.Split('\n'))
        {
            var separator = line.IndexOf('=', StringComparison.Ordinal);
            if (!line.StartsWith(Prefix, StringComparison.Ordinal) || separator < 0)
            {
                continue;
            }

            var value = line[(separator + 1)..];
            switch (line[Prefix.Length..separator])
            {
                case "cache":
                    cache = value;
                    break;
                case "config":
                    config = value;
                    break;
                case "temp":
                    temp = value;
                    break;
                case "watch":
                    watched.Add(value);
                    break;
            }
        }

        return new EngineFolders(
            cache ?? throw new InvalidOperationException("The report names no cache folder of the engine:\n" + report),
            config ?? throw new InvalidOperationException("The report names no configuration folder of the engine:\n" + report),
            temp ?? throw new InvalidOperationException("The report names no temporary folder:\n" + report),
            watched);
    }

    /// <summary>True when <paramref name="path"/> is <paramref name="folder"/> or below it (case-insensitive where the system is).</summary>
    public static bool IsAtOrBelow(string path, string folder)
    {
        var relative = Path.GetRelativePath(folder, path);
        return relative == "." || (!Path.IsPathRooted(relative) && relative != ".." && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal));
    }

    private static string EngineField(Type platform, string name) =>
        platform.GetField(name, BindingFlags.NonPublic | BindingFlags.Static)?.GetValue(null) as string
        ?? throw new InvalidOperationException($"The engine has no Platform.{name} any more: the \"no files\" test must learn its new folders.");

    private static string Below(Environment.SpecialFolder folder, params string[] parts)
    {
        var root = Environment.GetFolderPath(folder, Environment.SpecialFolderOption.DoNotVerify);
        return root.Length == 0 ? string.Empty : Path.Join([root, .. parts]);
    }
}
