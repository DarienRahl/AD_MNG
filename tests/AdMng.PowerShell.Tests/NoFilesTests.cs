using System.Collections.Concurrent;
using System.Globalization;
using System.Text.RegularExpressions;
using AdMng.PowerShell.Probe;

namespace AdMng.PowerShell.Tests;

/// <summary>The "no files" tests run alone: on Windows they compare folders of the user that another test's child could touch.</summary>
[CollectionDefinition(nameof(NoFilesTests), DisableParallelization = true)]
public sealed class NoFilesCollection
{
}

/// <summary>
/// D8, the "no files" test: commands run in a process whose <c>HOME</c>, <c>USERPROFILE</c>, <c>LOCALAPPDATA</c>, <c>APPDATA</c>,
/// <c>TEMP</c> and <c>XDG_*</c> point at an empty folder, with telemetry switched ON in its environment, and the folder is still empty
/// afterwards. The child reports where its engine keeps files of its own (<see cref="EngineFolders"/>: the engine's cache and configuration
/// folders as the engine finds them, on Windows also Windows PowerShell's folders, and the temporary folder), and the test asserts that it
/// watches each of them: on Linux they are all below the empty folder; on Windows the engine finds its folders through the known-folder API,
/// which the variables need not move (audit TI-1), so there the test also compares the user's own folders of the engine before and after
/// the run, and watches the redirected temporary folder for any file made even for a moment: the only ones allowed are the two test files
/// of the lockdown probe (<c>__PSScriptPolicyTest_*.ps1</c> / <c>.psm1</c>, made and deleted by the engine's AppLocker check, audit W-11).
/// A positive control shows, on each system, that the test sees a file the engine wrote where it looks. The same child run measures the
/// first and the second runspace (docs/dev/r1-powershell-window.md 4.2, 9.1).
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
        Assert.Contains("done", result.Report, StringComparison.Ordinal);
        var folders = EngineFolders.Parse(result.Report);
        TestContext.Current.TestOutputHelper?.WriteLine("The child's folders of the engine:\n" + folders.ToReport());
        KnownFolders.AssertWatched(folders, home.Home);
        Assert.True(EngineFolders.IsAtOrBelow(folders.Temp, home.Home), "The child's temporary folder is not the watched folder: " + folders.Temp);
        Assert.Equal(string.Empty, string.Join('\n', home.Files()));
        Assert.Equal(string.Empty, string.Join('\n', KnownFolders.Changes(known, KnownFolders.Take())));
        var made = watcher.Made();
        TestContext.Current.TestOutputHelper?.WriteLine("Files made for a moment in the child's folders: " + (made.Count == 0 ? "none" : string.Join(", ", made)));
        if (OperatingSystem.IsWindows())
        {
            Assert.Equal(string.Empty, string.Join('\n', made.Where(name => !LockdownProbeFile().IsMatch(Path.GetFileName(name)))));
        }
        TestContext.Current.TestOutputHelper?.WriteLine("R1 measurement (child process, " + RuntimeInformation() + "): " + result.Report.Replace('\n', ' '));
        var first = Number(result.Report, "first_ms");
        Assert.InRange(first, 1, 60_000);
    }

    [Fact]
    public async Task The_probe_would_see_a_file_the_engine_wrote_when_it_is_not_set_up()
    {
        // An ordinary runspace of the SDK, telemetry on, in a child whose variables point at the test's folder: the probe writes a file of its
        // own into the engine's cache folder, and when the engine sends telemetry its first command writes the engine's telemetry.uuid there.
        // What is written must be seen - below the test's folder (Linux; Windows when the variables moved the known folders) or in the
        // snapshot of the user's real folders (Windows when they did not; there telemetry.uuid is new only when the user had none). The
        // child reports whether its engine sends telemetry (ApplicationInsightsTelemetry.CanSendTelemetry): on Linux it does with telemetry
        // on; the Windows build also asks Windows' diagnostic data setting, which the CI runner has off (CI of 7e13cff6 and 7cc6191b: no
        // telemetry.uuid, the probe's own file seen) - there the probe's file alone shows that the folder is watched. The test removes what
        // the control made in a real folder.
        using var home = new TempFolder();
        var own = KnownFolders.OwnCache;
        var ownTelemetry = own is null ? null : Path.Combine(own, "telemetry.uuid");
        var hadOwnCache = own is not null && Directory.Exists(own);
        var hadOwnTelemetry = ownTelemetry is not null && File.Exists(ownTelemetry);
        var known = KnownFolders.Take();
        try
        {
            var result = await ChildProcess.RunAsync("control", home.Home, home.ReportPath);

            Assert.True(result.ExitCode == 0, "The child failed:\n" + result.Output);
            Assert.Contains("done", result.Report, StringComparison.Ordinal);
            var folders = EngineFolders.Parse(result.Report);
            TestContext.Current.TestOutputHelper?.WriteLine("The child's folders of the engine:\n" + folders.ToReport());
            KnownFolders.AssertWatched(folders, home.Home);
            List<string> seen = [.. KnownFolders.Changes(known, KnownFolders.Take()), .. home.Files().Select(file => Path.Combine(home.Home, file))];
            TestContext.Current.TestOutputHelper?.WriteLine("Seen: " + string.Join(", ", seen));
            Assert.Contains(seen, file => EngineFolders.IsAtOrBelow(file, folders.Cache) && Path.GetFileName(file).StartsWith(EngineFolders.ControlFilePrefix, StringComparison.Ordinal));
            var sendsTelemetry = SendsTelemetry(result.Report);
            TestContext.Current.TestOutputHelper?.WriteLine("The child's engine sends telemetry: " + sendsTelemetry);
            if (!OperatingSystem.IsWindows())
            {
                Assert.True(sendsTelemetry, "The child's engine does not send telemetry although its environment turns it on:\n" + result.Report);
            }

            var telemetry = Path.Combine(folders.Cache, "telemetry.uuid");
            var telemetryIsNew = EngineFolders.IsAtOrBelow(folders.Cache, home.Home) || !(hadOwnTelemetry && SamePath(telemetry, ownTelemetry!));
            if (sendsTelemetry && telemetryIsNew)
            {
                Assert.Contains(seen, file => SamePath(file, telemetry));
            }
            else if (!sendsTelemetry)
            {
                Assert.DoesNotContain(seen, file => SamePath(file, telemetry));
            }
        }
        finally
        {
            if (own is not null && Directory.Exists(own))
            {
                foreach (var file in Directory.GetFiles(own, EngineFolders.ControlFilePrefix + "*"))
                {
                    File.Delete(file);
                }

                if (!hadOwnTelemetry && File.Exists(ownTelemetry))
                {
                    File.Delete(ownTelemetry!);
                }

                if (!hadOwnCache && !Directory.EnumerateFileSystemEntries(own).Any())
                {
                    Directory.Delete(own);
                }
            }
        }
    }

    /// <summary>What the control reported of its engine's <c>ApplicationInsightsTelemetry.CanSendTelemetry</c>.</summary>
    private static bool SendsTelemetry(string report)
    {
        var line = report.Split('\n').SingleOrDefault(line => line.StartsWith("telemetry=", StringComparison.Ordinal));
        Assert.True(line is not null, "The control's report does not say whether its engine sends telemetry:\n" + report);
        return bool.Parse(line["telemetry=".Length..]);
    }

    private static bool SamePath(string left, string right) =>
        string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

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

        // On Windows the window child's path is NUL\ModuleAnalysisCache, relative to its working folder (the watched one); a cache written to
        // the engine's default place instead would be in the user's cache folder, which the snapshot covers.
        var known = KnownFolders.Take();
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
        Assert.DoesNotContain(KnownFolders.Changes(known, KnownFolders.Take()), file => file.Contains("ModuleAnalysisCache", StringComparison.Ordinal));
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
/// The user's own folders of the engine (<see cref="EngineFolders"/> of the test process) on Windows, where a child's environment
/// variables need not move them: the engine's cache (<c>telemetry.uuid</c>, the module analysis cache, startup data) and configuration
/// folder (Documents\PowerShell), Windows PowerShell's cache, the PSReadLine history and Documents\WindowsPowerShell. Empty on other
/// systems, where the child's variables move every folder of the engine below the test's folder (<see cref="AssertWatched"/> checks it).
/// </summary>
internal static class KnownFolders
{
    private static readonly IReadOnlyList<string> Roots = OperatingSystem.IsWindows() ? EngineFolders.OfThisProcess().Watched : [];

    /// <summary>The engine's cache folder of the user (Windows), where the control's files are removed again; null elsewhere.</summary>
    public static string? OwnCache { get; } = OperatingSystem.IsWindows() ? EngineFolders.OfThisProcess().Cache : null;

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

    /// <summary>Every folder of the child's engine is below the test's folder or one the snapshot covers - else the test would not see a file there.</summary>
    public static void AssertWatched(EngineFolders child, string home)
    {
        foreach (var folder in child.Watched.Append(child.Cache).Append(child.Config))
        {
            Assert.True(
                EngineFolders.IsAtOrBelow(folder, home) || Roots.Any(root => EngineFolders.IsAtOrBelow(folder, root)),
                "The test does not watch a folder of the child's engine: " + folder);
        }
    }
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
