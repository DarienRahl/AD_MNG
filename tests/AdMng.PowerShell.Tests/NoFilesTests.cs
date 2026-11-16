using System.Diagnostics;
using System.Globalization;
using System.Management.Automation.Runspaces;

namespace AdMng.PowerShell.Tests;

/// <summary>
/// D8, the "no files" test: commands run in a process whose <c>HOME</c>, <c>LOCALAPPDATA</c>, <c>TEMP</c> and <c>XDG_*</c>
/// point at an empty folder, with telemetry switched ON in its environment, and the folder is still empty afterwards. A
/// positive control shows the probe would see a file the engine wrote. The same child run measures the first and the second
/// runspace (docs/dev/r1-powershell-window.md 4.2, 9.1).
/// </summary>
public sealed class NoFilesTests
{
    private const string ChildClass = "AdMng.PowerShell.Tests.NoFilesChildren";

    [Fact]
    public async Task Working_in_the_window_leaves_no_file_behind()
    {
        using var home = new TempFolder();

        var result = await ChildProcess.RunAsync("files", $"{ChildClass}.Scenario", home.Home, home.ReportPath);

        Assert.True(result.ExitCode == 0, "The child failed:\n" + result.Output);
        Assert.Equal(string.Empty, string.Join('\n', home.Files()));
        Assert.Contains("done", result.Report, StringComparison.Ordinal);
        TestContext.Current.SendDiagnosticMessage("R1 measurement (child process, " + RuntimeInformation() + "): " + result.Report.Replace('\n', ' '));
        var first = Number(result.Report, "first_ms");
        Assert.InRange(first, 1, 60_000);
    }

    [Fact]
    public async Task The_probe_would_see_a_file_the_engine_wrote_when_it_is_not_set_up()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "On Windows the engine's files go to known folders that an environment variable does not move.");
        using var home = new TempFolder();

        var result = await ChildProcess.RunAsync("control", $"{ChildClass}.Control", home.Home, home.ReportPath);

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
            $"{ChildClass}.CacheControl",
            control.Home,
            control.ReportPath,
            new Dictionary<string, string?> { ["PSModulePath"] = Path.Combine(control.Home, "Modules"), ["PSModuleAnalysisCachePath"] = Path.Combine(control.Home, "cache", "ModuleAnalysisCache") },
            TimeSpan.FromMinutes(2));
        var windowRun = ChildProcess.RunAsync(
            "cache-window",
            $"{ChildClass}.CacheWindow",
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

/// <summary>The methods the child processes run; in an ordinary test run they return at once.</summary>
public sealed class NoFilesChildren
{
    [Fact]
    public async Task Scenario()
    {
        if (ChildProcess.Mode != "files")
        {
            return;
        }

        var before = GC.GetTotalMemory(forceFullCollection: true);
        var workingSetBefore = Environment.WorkingSet;
        var watch = Stopwatch.StartNew();
        await using var first = await TestSessions.CreateAsync();
        var firstMs = watch.ElapsedMilliseconds;
        var managed = GC.GetTotalMemory(forceFullCollection: true) - before;
        var workingSet = Environment.WorkingSet - workingSetBefore;
        watch.Restart();
        await using var second = await TestSessions.CreateAsync();
        var secondMs = watch.ElapsedMilliseconds;

        await TestSessions.RunAsync(first, "Get-Date; Get-Command *-Adm* | Out-Null; Get-Module -ListAvailable | Out-Null; 1..200 | ForEach-Object { \"line $_\" } | Out-String | Out-Null; Get-Process | Select-Object -First 3 | Format-Table; Write-Warning 'w'; Get-AdmVersion");
        await first.CompleteAsync("Get-Ad", 6, TestContext.Current.CancellationToken);
        await first.CompleteAsync("$PS", 3, TestContext.Current.CancellationToken);
        first.Tokenize("Get-AdmVersion -Verbose | Where-Object { $_ }");
        first.ShouldRemember("Get-AdmVersion");
        await TestSessions.RunAsync(second, "Get-Date");

        await File.WriteAllTextAsync(
            ChildProcess.ReportPath!,
            $"first_ms={firstMs}\nsecond_ms={secondMs}\nmanaged_kb={managed / 1024}\nworking_set_mb={workingSet / (1024 * 1024)}\ndone\n",
            TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Control()
    {
        if (ChildProcess.Mode != "control")
        {
            return;
        }

        // No AdMng code: an ordinary runspace of the SDK with telemetry on, as a plain pwsh-like host would start.
        using var runspace = RunspaceFactory.CreateRunspace();
        runspace.Open();
        using (var ps = System.Management.Automation.PowerShell.Create())
        {
            ps.Runspace = runspace;
            ps.AddScript("Get-Date | Out-Null").Invoke();
        }

        await File.WriteAllTextAsync(ChildProcess.ReportPath!, "done\n", TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task CacheControl()
    {
        if (ChildProcess.Mode != "cache-control")
        {
            return;
        }

        // An ordinary runspace, auto-loading on, the cache pointed at a file of the test: module discovery writes it.
        using var runspace = RunspaceFactory.CreateRunspace();
        runspace.Open();
        using (var ps = System.Management.Automation.PowerShell.Create())
        {
            ps.Runspace = runspace;
            ps.AddScript("Get-Command Get-Fake* | Out-Null; Get-Module -ListAvailable | Out-Null").Invoke();
        }

        await WaitForCacheAsync(TimeSpan.FromSeconds(40), expectFile: true);
        await File.WriteAllTextAsync(ChildProcess.ReportPath!, "done\n", TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task CacheWindow()
    {
        if (ChildProcess.Mode != "cache-window")
        {
            return;
        }

        // The window's session: auto-loading off and the cache on the null device; discovery as hard as an operator can make it.
        await using var session = await TestSessions.CreateAsync();
        await TestSessions.RunAsync(session, "Get-Command Get-Fake* | Out-Null; Get-Module -ListAvailable | Out-Null; Get-Command | Out-Null");
        await session.CompleteAsync("Get-", 4, TestContext.Current.CancellationToken);
        await WaitForCacheAsync(TimeSpan.FromSeconds(40), expectFile: false);
        await File.WriteAllTextAsync(ChildProcess.ReportPath!, "done\n", TestContext.Current.CancellationToken);
    }

    /// <summary>The engine saves the cache on a timer about 12 s after discovery (no flush at exit): wait for it, or for the window to pass.</summary>
    private static async Task WaitForCacheAsync(TimeSpan window, bool expectFile)
    {
        var cache = Environment.GetEnvironmentVariable("PSModuleAnalysisCachePath");
        var watch = Stopwatch.StartNew();
        while (watch.Elapsed < window)
        {
            if (expectFile && cache is not null && File.Exists(cache))
            {
                return;
            }

            await Task.Delay(500, TestContext.Current.CancellationToken);
        }
    }
}
