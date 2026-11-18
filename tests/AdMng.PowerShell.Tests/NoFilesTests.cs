using System.Globalization;

namespace AdMng.PowerShell.Tests;

/// <summary>
/// D8, the "no files" test: commands run in a process whose <c>HOME</c>, <c>LOCALAPPDATA</c>, <c>TEMP</c> and <c>XDG_*</c>
/// point at an empty folder, with telemetry switched ON in its environment, and the folder is still empty afterwards. A
/// positive control shows the probe would see a file the engine wrote. The same child run measures the first and the second
/// runspace (docs/dev/r1-powershell-window.md 4.2, 9.1).
/// </summary>
public sealed class NoFilesTests
{
    [Fact]
    public async Task Working_in_the_window_leaves_no_file_behind()
    {
        using var home = new TempFolder();

        var result = await ChildProcess.RunAsync("files", home.Home, home.ReportPath);

        Assert.True(result.ExitCode == 0, "The child failed:\n" + result.Output);
        Assert.Equal(string.Empty, string.Join('\n', home.Files()));
        Assert.Contains("done", result.Report, StringComparison.Ordinal);
        TestContext.Current.TestOutputHelper?.WriteLine("R1 measurement (child process, " + RuntimeInformation() + "): " + result.Report.Replace('\n', ' '));
        var first = Number(result.Report, "first_ms");
        Assert.InRange(first, 1, 60_000);
    }

    [Fact]
    public async Task The_probe_would_see_a_file_the_engine_wrote_when_it_is_not_set_up()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "On Windows the engine's files go to known folders that an environment variable does not move.");
        using var home = new TempFolder();

        var result = await ChildProcess.RunAsync("control", home.Home, home.ReportPath);

        Assert.True(result.ExitCode == 0, "The child failed:\n" + result.Output);
        Assert.Contains("done", result.Report, StringComparison.Ordinal);
        Assert.Contains(home.Files(), file => file.EndsWith("telemetry.uuid", StringComparison.Ordinal));
    }

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
