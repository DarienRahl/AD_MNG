using System.Collections.Concurrent;
using System.Globalization;
using System.Text.RegularExpressions;

namespace AdMng.PowerShell.Tests;

/// <summary>The "no files" tests run alone: on Windows they compare folders of the user that another test's child could touch.</summary>
[CollectionDefinition(nameof(NoFilesTests), DisableParallelization = true)]
public sealed class NoFilesCollection
{
}

/// <summary>
/// D8, the "no files" test: commands run in a process whose <c>HOME</c>, <c>LOCALAPPDATA</c>, <c>TEMP</c> and <c>XDG_*</c>
/// point at an empty folder, with telemetry switched ON in its environment, and the folder is still empty afterwards. On Windows the
/// engine finds its folders through the known-folder API, which those variables do not move (audit TI-1), so there the test also
/// compares the user's real PowerShell folders (<see cref="KnownFolders"/>) before and after the run, and watches the redirected
/// temporary folder for any file made even for a moment: the only ones allowed are the two empty test files of the lockdown probe
/// (<c>__PSScriptPolicyTest_*.ps1</c> / <c>.psm1</c>, made and deleted by the engine's AppLocker check, audit W-11). A positive control
/// shows, on each system, that the test would see a file written where it looks. The same child run measures the first and the
/// second runspace (docs/dev/r1-powershell-window.md 4.2, 9.1).
/// </summary>
[Collection(nameof(NoFilesTests))]
public sealed partial class NoFilesTests
{
    [Fact]
    public async Task Working_in_the_window_leaves_no_file_behind()
    {
        using var home = new TempFolder();
        using var watcher = new FolderWatcher(home.Home);
        var known = KnownFolders.Take();

        var result = await ChildProcess.RunAsync("files", home.Home, home.ReportPath);

        Assert.True(result.ExitCode == 0, "The child failed:\n" + result.Output);
        Assert.Equal(string.Empty, string.Join('\n', home.Files()));
        Assert.Equal(string.Empty, string.Join('\n', KnownFolders.Changes(known, KnownFolders.Take())));
        var made = watcher.Made();
        TestContext.Current.TestOutputHelper?.WriteLine("Files made for a moment in the child's folders: " + (made.Count == 0 ? "none" : string.Join(", ", made)));
        if (OperatingSystem.IsWindows())
        {
            Assert.Equal(string.Empty, string.Join('\n', made.Where(name => !LockdownProbeFile().IsMatch(Path.GetFileName(name)))));
        }
        Assert.Contains("done", result.Report, StringComparison.Ordinal);
        TestContext.Current.TestOutputHelper?.WriteLine("R1 measurement (child process, " + RuntimeInformation() + "): " + result.Report.Replace('\n', ' '));
        var first = Number(result.Report, "first_ms");
        Assert.InRange(first, 1, 60_000);
    }

    [Fact]
    public async Task The_probe_would_see_a_file_the_engine_wrote_when_it_is_not_set_up()
    {
        using var home = new TempFolder();
        if (!OperatingSystem.IsWindows())
        {
            var result = await ChildProcess.RunAsync("control", home.Home, home.ReportPath);

            Assert.True(result.ExitCode == 0, "The child failed:\n" + result.Output);
            Assert.Contains("done", result.Report, StringComparison.Ordinal);
            Assert.Contains(home.Files(), file => file.EndsWith("telemetry.uuid", StringComparison.Ordinal));
            return;
        }

        // Windows: an ordinary runspace of the SDK, telemetry on, in a child whose variables point at the test's folder, writes into the
        // user's real cache folder of PowerShell - the snapshot sees it there although the redirected folder stays empty (audit TI-1). The
        // control also leaves the engine's own telemetry.uuid there when the user had none; the test removes what the control made.
        var cache = KnownFolders.EngineCacheFolder;
        var hadCache = Directory.Exists(cache);
        var telemetry = Path.Combine(cache, "telemetry.uuid");
        var hadTelemetry = File.Exists(telemetry);
        var known = KnownFolders.Take();
        try
        {
            var result = await ChildProcess.RunAsync("control-known-folders", home.Home, home.ReportPath);

            Assert.True(result.ExitCode == 0, "The child failed:\n" + result.Output);
            var changes = KnownFolders.Changes(known, KnownFolders.Take());
            Assert.Contains(changes, file => Path.GetFileName(file).StartsWith(KnownFolders.ControlFilePrefix, StringComparison.Ordinal));
            if (!hadTelemetry)
            {
                Assert.Contains(changes, file => Path.GetFileName(file) == "telemetry.uuid");
            }

            Assert.Empty(home.Files());
        }
        finally
        {
            foreach (var file in Directory.Exists(cache) ? Directory.GetFiles(cache, KnownFolders.ControlFilePrefix + "*") : [])
            {
                File.Delete(file);
            }

            if (!hadTelemetry && File.Exists(telemetry))
            {
                File.Delete(telemetry);
            }

            if (!hadCache && Directory.Exists(cache) && !Directory.EnumerateFileSystemEntries(cache).Any())
            {
                Directory.Delete(cache);
            }
        }
    }

    [GeneratedRegex(@"^__PSScriptPolicyTest_[A-Za-z0-9]{8}\.[A-Za-z0-9]{3}\.(ps1|psm1)$")]
    private static partial Regex LockdownProbeFile();

    [Fact]
    public async Task The_module_analysis_cache_is_never_written_but_would_be_with_auto_loading_on()
    {
        Assert.SkipUnless(
            Environment.GetEnvironmentVariable("ADMNG_POWERSHELL_SLOW_TESTS") == "1",
            "Slow (the engine writes the cache about 12 s after module discovery): set ADMNG_POWERSHELL_SLOW_TESTS=1 to run it.");
        using var control = new TempFolder();
        using var window = new TempFolder();
        foreach (var folder in new[] { control, window })
        {
            Directory.CreateDirectory(Path.Combine(folder.Home, "Modules", "FakeMod"));
            await File.WriteAllTextAsync(
                Path.Combine(folder.Home, "Modules", "FakeMod", "FakeMod.psd1"),
                "@{ ModuleVersion = '1.0'; RootModule = 'FakeMod.psm1'; FunctionsToExport = @('Get-FakeThing'); GUID = '11111111-2222-3333-4444-555555555555' }",
                TestContext.Current.CancellationToken);
            await File.WriteAllTextAsync(Path.Combine(folder.Home, "Modules", "FakeMod", "FakeMod.psm1"), "function Get-FakeThing { 'fake' }", TestContext.Current.CancellationToken);
        }

        var controlRun = ChildProcess.RunAsync(
            "cache-control",
            control.Home,
            control.ReportPath,
            new Dictionary<string, string?> { ["PSModulePath"] = Path.Combine(control.Home, "Modules"), ["PSModuleAnalysisCachePath"] = Path.Combine(control.Home, "cache", "ModuleAnalysisCache") },
            TimeSpan.FromMinutes(2));
        var windowRun = ChildProcess.RunAsync(
            "cache-window",
            window.Home,
            window.ReportPath,
            new Dictionary<string, string?> { ["PSModulePath"] = Path.Combine(window.Home, "Modules") },
            TimeSpan.FromMinutes(2));
        var results = await Task.WhenAll(controlRun, windowRun);

        Assert.True(results[0].ExitCode == 0, "The control child failed:\n" + results[0].Output);
        Assert.True(results[1].ExitCode == 0, "The window child failed:\n" + results[1].Output);
        Assert.Contains(control.Files(), file => file.Contains("ModuleAnalysisCache", StringComparison.Ordinal));
        Assert.DoesNotContain(window.Files(), file => file.Contains("ModuleAnalysisCache", StringComparison.Ordinal));
    }

    private static long Number(string report, string key)
    {
        foreach (var line in report.Split('\n'))
        {
            if (line.StartsWith(key + "=", StringComparison.Ordinal))
            {
                return long.Parse(line[(key.Length + 1)..], CultureInfo.InvariantCulture);
            }
        }

        return -1;
    }

    private static string RuntimeInformation() =>
        System.Runtime.InteropServices.RuntimeInformation.OSDescription + ", " + System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription;
}

/// <summary>
/// The folders of the user where the engine keeps its own files on Windows, found through the known-folder API as the engine finds them
/// (an environment variable does not move them): the cache (<c>telemetry.uuid</c>, the module analysis cache, startup data), Windows
/// PowerShell's cache, the PSReadLine history, and the profile and module folders in Documents. Empty on other systems, where the child's
/// variables move every folder the engine uses.
/// </summary>
internal static class KnownFolders
{
    /// <summary>The name the positive control gives its file in the cache folder.</summary>
    public const string ControlFilePrefix = "admng-no-files-control-";

    public static string EngineCacheFolder { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "PowerShell");

    private static readonly string[] Roots = OperatingSystem.IsWindows()
        ? [
            EngineCacheFolder,
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "Windows", "PowerShell"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Microsoft", "Windows", "PowerShell"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "PowerShell"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "WindowsPowerShell"),
        ]
        : [];

    /// <summary>Every file below the folders, with its length and the time it was last written.</summary>
    public static Dictionary<string, (long Length, DateTime Written)> Take()
    {
        var files = new Dictionary<string, (long, DateTime)>(StringComparer.OrdinalIgnoreCase);
        var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = 0 };
        foreach (var root in Roots.Where(Directory.Exists))
        {
            foreach (var file in new DirectoryInfo(root).EnumerateFiles("*", options))
            {
                files[file.FullName] = (file.Length, file.LastWriteTimeUtc);
            }
        }

        return files;
    }

    /// <summary>The files that are new or were written since <paramref name="before"/>.</summary>
    public static IReadOnlyList<string> Changes(Dictionary<string, (long Length, DateTime Written)> before, Dictionary<string, (long Length, DateTime Written)> after) =>
        [.. after.Where(entry => !before.TryGetValue(entry.Key, out var old) || old != entry.Value).Select(entry => entry.Key).Order(StringComparer.Ordinal)];
}

/// <summary>Every file made below a folder while it is watched, also one deleted at once (a snapshot afterwards does not see it).</summary>
internal sealed class FolderWatcher : IDisposable
{
    private readonly ConcurrentQueue<string> _made = new();
    private readonly FileSystemWatcher _watcher;

    public FolderWatcher(string folder)
    {
        _watcher = new FileSystemWatcher(folder) { IncludeSubdirectories = true, NotifyFilter = NotifyFilters.FileName };
        _watcher.Created += (_, e) => _made.Enqueue(e.Name ?? e.FullPath);
        _watcher.Renamed += (_, e) => _made.Enqueue(e.Name ?? e.FullPath);
        _watcher.EnableRaisingEvents = true;
    }

    /// <summary>The names, relative to the folder, in the order they were seen.</summary>
    public IReadOnlyList<string> Made() => [.. _made];

    public void Dispose() => _watcher.Dispose();
}

/// <summary>A folder the test watches, and a report file beside it.</summary>
internal sealed class TempFolder : IDisposable
{
    private readonly string _container = Path.Combine(Path.GetTempPath(), "admng-ps-" + Guid.NewGuid().ToString("N"));

    public TempFolder()
    {
        Directory.CreateDirectory(_container);
        Home = Path.Combine(_container, "home");
        Directory.CreateDirectory(Home);
        ReportPath = Path.Combine(_container, "report.txt");
    }

    /// <summary>The folder the child's environment points at; it must stay empty.</summary>
    public string Home { get; }

    public string ReportPath { get; }

    /// <summary>Every entry below the folder that is not a directory (a socket or a pipe left behind counts), relative.</summary>
    public IReadOnlyList<string> Files() =>
        [.. Directory.EnumerateFileSystemEntries(Home, "*", SearchOption.AllDirectories)
            .Where(entry => !Directory.Exists(entry))
            .Select(entry => Path.GetRelativePath(Home, entry))
            .Order(StringComparer.Ordinal)];

    public void Dispose()
    {
        try
        {
            Directory.Delete(_container, recursive: true);
        }
        catch (IOException)
        {
            // A file still held by a process that is ending; the temp folder is the test's own.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
