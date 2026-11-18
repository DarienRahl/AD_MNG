using System.Diagnostics;
using System.Globalization;
using System.Management.Automation.Runspaces;
using AdMng.Core.PowerShell;
using AdMng.PowerShell.Commands;
using AdMng.Testing;
using SmaPowerShell = System.Management.Automation.PowerShell;

namespace AdMng.PowerShell.Probe;

/// <summary>
/// The child process of the engine's file tests. <c>Probe.dll &lt;mode&gt; &lt;report file&gt;</c>; the parent sets the
/// environment (HOME, TEMP, XDG_*, the user's own PowerShell settings) and looks at the folders afterwards. Modes:
/// <c>files</c> (the window's session, a script that touches the formatter, module discovery, completion, syntax and the
/// history policy, then a measurement), <c>control</c> (an ordinary runspace of the SDK: the parent expects it to leave
/// telemetry.uuid), <c>cache-control</c> and <c>cache-window</c> (the module analysis cache, about 40 s each).
/// </summary>
internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        if (args.Length != 2)
        {
            await Console.Error.WriteLineAsync("usage: probe <files|control|cache-control|cache-window> <report file>");
            return 2;
        }

        try
        {
            var report = args[1];
            switch (args[0])
            {
                case "files":
                    await FilesAsync(report);
                    break;
                case "control":
                    Control();
                    await File.WriteAllTextAsync(report, "done\n");
                    break;
                case "cache-control":
                    await CacheControlAsync();
                    await File.WriteAllTextAsync(report, "done\n");
                    break;
                case "cache-window":
                    await CacheWindowAsync();
                    await File.WriteAllTextAsync(report, "done\n");
                    break;
                default:
                    await Console.Error.WriteLineAsync("unknown mode " + args[0]);
                    return 2;
            }

            return 0;
        }
        catch (Exception ex)
        {
            await Console.Error.WriteLineAsync(ex.ToString());
            return 1;
        }
    }

    private static async Task FilesAsync(string report)
    {
        var before = GC.GetTotalMemory(forceFullCollection: true);
        var workingSetBefore = Environment.WorkingSet;
        var watch = Stopwatch.StartNew();
        await using var first = await CreateAsync();
        var firstMs = watch.ElapsedMilliseconds;
        var managed = GC.GetTotalMemory(forceFullCollection: true) - before;
        var workingSet = Environment.WorkingSet - workingSetBefore;
        watch.Restart();
        await using var second = await CreateAsync();
        var secondMs = watch.ElapsedMilliseconds;

        await RunAsync(first, "Get-Date; Get-Command *-Adm* | Out-Null; 1..200 | ForEach-Object { \"line $_\" } | Out-String | Out-Null; Get-Date | Format-List; Write-Warning 'w'; Get-AdmVersion");

        // Module discovery as the engine does it for an explicit listing. Not on Windows: there the engine's files go to known
        // folders that this test cannot watch, and a listing of a runner's installed modules (hundreds) takes from 20 s to minutes.
        if (!OperatingSystem.IsWindows())
        {
            await RunAsync(first, "Get-Module -ListAvailable | Out-Null");
        }

        await first.CompleteAsync("Get-Ad", 6, CancellationToken.None);
        await first.CompleteAsync("$PS", 3, CancellationToken.None);
        first.Tokenize("Get-AdmVersion -Verbose | Where-Object { $_ }");
        first.ShouldRemember("Get-AdmVersion");
        await RunAsync(second, "Get-Date");

        await File.WriteAllTextAsync(
            report,
            string.Create(
                CultureInfo.InvariantCulture,
                $"first_ms={firstMs}\nsecond_ms={secondMs}\nmanaged_kb={managed / 1024}\nworking_set_mb={workingSet / (1024 * 1024)}\ndone\n"));
    }

    private static void Control()
    {
        // No AdMng code: an ordinary runspace of the SDK with telemetry on, as a plain pwsh-like host would start.
        using var runspace = RunspaceFactory.CreateRunspace();
        runspace.Open();
        using var ps = SmaPowerShell.Create();
        ps.Runspace = runspace;
        ps.AddScript("Get-Date | Out-Null").Invoke();
    }

    private static async Task CacheControlAsync()
    {
        // An ordinary runspace, auto-loading on, the cache pointed at a file of the test: module discovery writes it.
        using var runspace = RunspaceFactory.CreateRunspace();
        runspace.Open();
        using (var ps = SmaPowerShell.Create())
        {
            ps.Runspace = runspace;
            ps.AddScript("Get-Command Get-Fake* | Out-Null; Get-Module -ListAvailable | Out-Null").Invoke();
        }

        await WaitForCacheAsync(TimeSpan.FromSeconds(40), expectFile: true);
    }

    private static async Task CacheWindowAsync()
    {
        // The window's session: auto-loading off and the cache unwritable; discovery as hard as an operator can make it.
        await using var session = await CreateAsync();
        await RunAsync(session, "Get-Command Get-Fake* | Out-Null; Get-Module -ListAvailable | Out-Null; Get-Command | Out-Null");
        await session.CompleteAsync("Get-", 4, CancellationToken.None);
        await WaitForCacheAsync(TimeSpan.FromSeconds(40), expectFile: false);
    }

    private static Task<PowerShellSession> CreateAsync() =>
        PowerShellSession.CreateAsync(new PowerShellSessionOptions { Context = new AdmWindowContext(new FakeDirectorySession(), "probe") });

    private static async Task RunAsync(PowerShellSession session, string script)
    {
        await foreach (var item in session.RunAsync(script, CancellationToken.None))
        {
            if (item is PowerShellPromptItem prompt)
            {
                prompt.Cancel();
            }
        }
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

            await Task.Delay(500);
        }
    }
}
