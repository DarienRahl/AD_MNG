using System.Collections;
using System.Diagnostics;
using System.Globalization;
using System.Management.Automation;
using System.Management.Automation.Runspaces;
using AdMng.Core.PowerShell;
using AdMng.PowerShell.Commands;
using AdMng.Testing;
using Microsoft.Extensions.Time.Testing;
using SmaPowerShell = System.Management.Automation.PowerShell;

namespace AdMng.PowerShell.Probe;

/// <summary>
/// The child process of the engine's file tests. <c>Probe.dll &lt;mode&gt; &lt;report file&gt;</c>; the parent sets the
/// environment (HOME, TEMP, XDG_*, the user's own PowerShell settings) and looks at the folders afterwards. Modes:
/// <c>files</c> (the window's session, a script that touches the formatter, module discovery, completion, syntax and the
/// history policy, then a measurement), <c>control</c> (an ordinary runspace of the SDK: the parent expects it to leave
/// telemetry.uuid), <c>cache-control</c> and <c>cache-window</c> (the module analysis cache, about 40 s each), <c>left-run</c> (a
/// run the session left: the process still ends), <c>leftover-callback</c> and <c>leftover-callback-control</c> (a script block
/// that trusted-mode code left behind runs after its window closed: the process survives it, the control crashes), <c>deep-input:&lt;case&gt;</c>
/// (a line nested deeper than the engine can walk, checked, highlighted, completed and run from a thread with the stack of the window's UI
/// thread: the process survives it and the window runs the next command), <c>parallel-job-open</c> (the window ends a parallel job of the
/// trusted mode while the engine still opens its child's runspace: the process survives it), <c>parallel-job-children</c> (the same for a
/// job of several children: while the engine opens the runspace of a later child, and when a child ends by itself at the moment the job
/// is stopped), <c>environment</c> (the runtime's crash dump and crash report settings the child got).
/// </summary>
internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        if (args.Length != 2)
        {
            await Console.Error.WriteLineAsync("usage: probe <files|control|cache-control|cache-window|left-run|leftover-callback|leftover-callback-control|deep-input:<case>|parallel-job-open|parallel-job-children|environment> <report file>");
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
                case "left-run":
                    await File.WriteAllTextAsync(report, await LeftRunAsync());
                    break;
                case "leftover-callback":
                    await File.WriteAllTextAsync(report, await LeftoverCallbackAsync(contained: true));
                    break;
                case "leftover-callback-control":
                    await File.WriteAllTextAsync(report, await LeftoverCallbackAsync(contained: false));
                    break;
                case var deep when deep.StartsWith("deep-input:", StringComparison.Ordinal):
                    DeepInput(deep["deep-input:".Length..], report);
                    break;
                case "parallel-job-open":
                    await ParallelJobOpenAsync(report);
                    break;
                case "parallel-job-children":
                    await ParallelJobChildrenAsync(report);
                    break;
                case "environment":
                    await File.WriteAllTextAsync(report, CrashSettings());
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

        // An explicit module listing, as an operator would type it. Get-Module is not in the window's command list (CommandGuards), so
        // the line fails and only shows that trying writes nothing; discovery runs through Get-Command and completion above and below.
        // Not on Windows: there the engine's files go to known folders that this test cannot watch.
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
        // The window's session: auto-loading off and the cache unwritable; discovery as hard as an operator can make it (Get-Module is
        // not in the window, so its line only fails; Get-Command and completion discover).
        await using var session = await CreateAsync();
        await RunAsync(session, "Get-Command Get-Fake* | Out-Null; Get-Module -ListAvailable | Out-Null; Get-Command | Out-Null");
        await session.CompleteAsync("Get-", 4, CancellationToken.None);
        await WaitForCacheAsync(TimeSpan.FromSeconds(40), expectFile: false);
    }

    /// <summary>
    /// A run that Stop cannot end - a regular expression with an infinite time limit of its own, allowed in ConstrainedLanguage - is left
    /// by the session, the session is disposed, and <c>Main</c> returns: the parent checks that the process ends although the left
    /// thread never returns (second red team of R1).
    /// </summary>
    private static async Task<string> LeftRunAsync()
    {
        await using var session = await PowerShellSession.CreateAsync(new PowerShellSessionOptions
        {
            Context = new AdmWindowContext(new FakeDirectorySession(), "probe"),
            StopGracePeriod = TimeSpan.FromSeconds(1),
        });
        var end = "none";
        var script = "'started'; [regex]::new('^(a+)+$', 'None', [regex]::InfiniteMatchTimeout).IsMatch('" + new string('a', 40) + "!')";
        await foreach (var item in session.RunAsync(script, CancellationToken.None))
        {
            if (item is PowerShellTextItem { Text: "started\n" })
            {
                await Task.Delay(300);
                _ = session.StopAsync();
            }
            else if (item is PowerShellRunEndItem runEnd)
            {
                end = runEnd.State.ToString();
            }
        }

        return "end=" + end + "\ndone\n";
    }

    /// <summary>
    /// The second red team of R1 (goal d): in the trusted mode a script leaves a timer whose callback is a script block; the window closes
    /// before it fires, and the script block then runs on a timer thread without a runspace and throws - unhandled, which ends the
    /// process and every window of the program with it. <paramref name="contained"/> sets up the process the way the program does
    /// (src/AdMng.App/ProcessStart.cs); the control does not, and the parent expects it to end with the crash.
    /// </summary>
    private static async Task<string> LeftoverCallbackAsync(bool contained)
    {
        if (contained)
        {
            PowerShellEnvironment.ContainLeftoverScriptBlocks();
        }

        await using (var session = await CreateAsync())
        {
            await session.SetLanguageModeAsync(PowerShellLanguageMode.Full, CancellationToken.None);
            var late = Path.Combine(Environment.CurrentDirectory, "late.txt").Replace("'", "''", StringComparison.Ordinal);
            await RunAsync(session, "$global:tmr = [System.Threading.Timer]::new([System.Threading.TimerCallback]{ [System.IO.File]::WriteAllText('" + late + "', 'x') }, $null, 1500, -1); 'started'");
        }

        await Task.Delay(TimeSpan.FromSeconds(4));
        return "survived\ndone\n";
    }

    /// <summary>
    /// CI run 45315307313: a ForEach-Object -Parallel job of the trusted mode was ended by the window - when its command ended, and when
    /// the window closed while its command waited - while the engine still opened the runspace of its child on a pool thread of its own;
    /// closing that runspace there made <c>InitialSessionState.BindRunspace</c> fail with a NullReferenceException that nothing catches,
    /// which ends the process (in the test run: xUnit's "catastrophic failure"). The gate holds the runspace at that point for each case:
    /// the probe notes the job's state once the window has ended the command (and, in the second case, the session), lets the runspace
    /// open, and notes whether the job then ended and whether it wrote its file (its script stops on its first error, as the job scripts of
    /// EscapeTests do: a stop during the load of Start-Sleep's module would let the write run). Set up as the program is
    /// (src/AdMng.App/ProcessStart.cs).
    /// Each line goes to the report as soon as it is known, so a crash shows how far the probe got.
    /// </summary>
    private static async Task ParallelJobOpenAsync(string report)
    {
        PowerShellEnvironment.LimitRegularExpressions();
        PowerShellEnvironment.ContainLeftoverScriptBlocks();
        PowerShellEnvironment.Apply();
        await File.WriteAllTextAsync(report, string.Empty);
        using var gate = ParallelRunspaceGate.Install();
        foreach (var closeWindow in new[] { false, true })
        {
            var name = closeWindow ? "window-closed" : "command-ended";
            var file = Path.Combine(Environment.CurrentDirectory, name + ".txt");
            gate.Arm();
            var session = await CreateAsync();
            await session.SetLanguageModeAsync(PowerShellLanguageMode.Full, CancellationToken.None);
            var script = "$j = & ([System.Management.Automation.CmdletInfo]::new('ForEach-Object', [Microsoft.PowerShell.Commands.ForEachObjectCommand])) "
                + "-InputObject 1 -AsJob -Parallel { $ErrorActionPreference = 'Stop'; Start-Sleep -Milliseconds 1500; [System.IO.File]::WriteAllText('" + file.Replace("'", "''", StringComparison.Ordinal) + "', 'job') }; "
                + "[AdMng.PowerShell.Probe.ParallelRunspaceGate]::WaitHeld(); $j"
                + (closeWindow ? "; Read-Host 'waiting'" : string.Empty);
            Job? job = null;
            await foreach (var item in session.RunObjectsAsync(script))
            {
                switch (item)
                {
                    case PowerShellObjectItem { Value: PSObject { BaseObject: Job started } }:
                        job = started;
                        break;
                    case PowerShellPromptItem:
                        await session.DisposeAsync();
                        break;
                    case PowerShellTextItem { Stream: PowerShellStream.Error } error:
                        await File.AppendAllTextAsync(report, name + " error=" + error.Text);
                        break;
                }
            }

            if (job is null)
            {
                await File.AppendAllTextAsync(report, name + " job=none\n");
                gate.Release();
                await session.DisposeAsync();
                continue;
            }

            // The window ends the job on a pool thread once the run (and, in the second case, the session) has ended.
            var whileOpening = await WaitForJobAsync(job, state => state != JobState.Running, TimeSpan.FromSeconds(2));
            await File.AppendAllTextAsync(report, name + " while-opening=" + whileOpening + "\n");
            gate.Release();
            var end = await WaitForJobAsync(job, state => state is JobState.Completed or JobState.Failed or JobState.Stopped, TimeSpan.FromSeconds(30));
            await File.AppendAllTextAsync(report, name + " ended=" + (end is JobState.Completed or JobState.Failed or JobState.Stopped) + "\n");
            await File.AppendAllTextAsync(report, name + " written=" + File.Exists(file) + "\n");
            await session.DisposeAsync();
        }

        await File.AppendAllTextAsync(report, "survived\ndone\n");
    }

    /// <summary>
    /// The race of <see cref="ParallelJobOpenAsync"/> with a job of three children, each with a file of its own, in a session whose clock
    /// is a fake that the probe moves on by an hour every 20 ms - whatever the window waits for on its own clock has passed:
    /// <list type="bullet">
    /// <item><c>later-child</c>: the gate holds the runspace of the second child (the first one runs) when the command ends.</item>
    /// <item><c>child-ends-at-stop</c>: with a throttle limit of 1 the first child waits and the other two wait for its place; when the
    /// window stops the job, the first child ends by itself (<see cref="ParallelJobChild.EndFirstChildAtStop"/>) - which lets the
    /// engine open a runspace for the second one if it still hands out children - and the gate holds that runspace.</item>
    /// </list>
    /// The probe notes the job's state while the second child's runspace is held (<c>later-child</c>) or what the first child's end met
    /// (<c>child-ends-at-stop</c>), lets the runspace go, and notes the job's end, whether a file was written and the children's states.
    /// Set up as the program is (src/AdMng.App/ProcessStart.cs); each line goes to the report as soon as it is known.
    /// </summary>
    private static async Task ParallelJobChildrenAsync(string report)
    {
        PowerShellEnvironment.LimitRegularExpressions();
        PowerShellEnvironment.ContainLeftoverScriptBlocks();
        PowerShellEnvironment.Apply();
        await File.WriteAllTextAsync(report, string.Empty);
        using var gate = ParallelRunspaceGate.Install();
        foreach (var name in new[] { "later-child", "child-ends-at-stop" })
        {
            var ending = name == "child-ends-at-stop";
            var folder = Environment.CurrentDirectory.Replace("'", "''", StringComparison.Ordinal);
            var write = "[System.IO.File]::WriteAllText([System.IO.Path]::Combine('" + folder + "', \"" + name + "-$_.txt\"), 'job')";
            var body = ending
                ? "$ErrorActionPreference = 'Stop'; if ($_ -eq 1) { [AdMng.PowerShell.Probe.ParallelJobChild]::Wait() } else { Start-Sleep -Seconds 30; " + write + " }"
                : "$ErrorActionPreference = 'Stop'; Start-Sleep -Seconds 30; " + write;
            var script = "$j = 1..3 | & ([System.Management.Automation.CmdletInfo]::new('ForEach-Object', [Microsoft.PowerShell.Commands.ForEachObjectCommand])) "
                + (ending ? "-ThrottleLimit 1 -UseNewRunspace " : string.Empty) + "-AsJob -Parallel { " + body + " }; "
                + (ending
                    ? "[AdMng.PowerShell.Probe.ParallelJobChild]::EndFirstChildAtStop($j); [AdMng.PowerShell.Probe.ParallelJobChild]::WaitUntilStarted($j); "
                    : "[AdMng.PowerShell.Probe.ParallelRunspaceGate]::WaitHeld(); ")
                + "$j";
            var time = new FakeTimeProvider();
            gate.Arm(skip: 1);
            var session = await CreateAsync(time);
            await session.SetLanguageModeAsync(PowerShellLanguageMode.Full, CancellationToken.None);
            Job? job = null;
            await foreach (var item in session.RunObjectsAsync(script))
            {
                switch (item)
                {
                    case PowerShellObjectItem { Value: PSObject { BaseObject: Job started } }:
                        job = started;
                        break;
                    case PowerShellTextItem { Stream: PowerShellStream.Error } error:
                        await File.AppendAllTextAsync(report, name + " error=" + error.Text);
                        break;
                }
            }

            if (job is null)
            {
                await File.AppendAllTextAsync(report, name + " job=none\n");
                gate.Release();
                await session.DisposeAsync();
                continue;
            }

            if (ending)
            {
                // The window stops the job on a pool thread once the run has ended; the first child ends during that stop.
                await WaitForJobAsync(job, Finished, TimeSpan.FromSeconds(30), time);
                await File.AppendAllTextAsync(report, name + " at-stop=" + ParallelJobChild.AtStop(TimeSpan.FromSeconds(30)) + "\n");
            }
            else
            {
                var whileOpening = await WaitForJobAsync(job, state => state != JobState.Running, TimeSpan.FromSeconds(2), time);
                await File.AppendAllTextAsync(report, name + " while-opening=" + whileOpening + "\n");
            }

            gate.Release();
            var end = await WaitForJobAsync(job, Finished, TimeSpan.FromSeconds(30), time);
            var written = Directory.EnumerateFiles(Environment.CurrentDirectory, name + "-*.txt").Any();
            await File.AppendAllTextAsync(
                report,
                name + " ended=" + Finished(end) + "\n" + name + " written=" + written + "\n"
                + name + " children=" + string.Join(",", job.ChildJobs.Select(child => child.JobStateInfo.State)) + "\n");
            await session.DisposeAsync();
        }

        await File.AppendAllTextAsync(report, "survived\ndone\n");
    }

    private static bool Finished(JobState state) => state is JobState.Completed or JobState.Failed or JobState.Stopped;

    private static async Task<JobState> WaitForJobAsync(Job job, Func<JobState, bool> done, TimeSpan timeout, FakeTimeProvider? time = null)
    {
        var watch = Stopwatch.StartNew();
        while (!done(job.JobStateInfo.State) && watch.Elapsed < timeout)
        {
            time?.Advance(TimeSpan.FromHours(1));
            await Task.Delay(20);
        }

        return job.JobStateInfo.State;
    }

    /// <summary>The runtime's crash dump and crash report settings in the child's environment, one <c>name=value</c> line each, sorted.</summary>
    private static string CrashSettings() =>
        string.Concat(Environment.GetEnvironmentVariables()
            .Cast<DictionaryEntry>()
            .Select(entry => (Name: (string)entry.Key, Value: entry.Value as string))
            .Where(entry => entry.Name.Contains("MiniDump", StringComparison.OrdinalIgnoreCase) || entry.Name.Contains("CrashReport", StringComparison.OrdinalIgnoreCase))
            .OrderBy(entry => entry.Name, StringComparer.Ordinal)
            .Select(entry => entry.Name + "=" + entry.Value + "\n"))
        + "done\n";

    /// <summary>
    /// The lines of the third red team of R1 (goal h) and its neighbours. A chain of operators is parsed without the parser's own
    /// recursion guard, but every walk over its syntax tree afterwards recurses once per operator: about 16 000 terms ended a parse on a
    /// 1 MB stack, about 9 300 ended the engine's compiler when the command ran, about 3 000 ended the history filter on a secret's
    /// name. A .NET stack overflow cannot be caught - it ends the process, on a workstation with every window of the program. The
    /// <c>-max</c> cases are as deep as a text just below the length limit can be (InputParser.MaxLength): they are parsed on the parser's
    /// own thread and must be refused, not end the process.
    /// </summary>
    private static string DeepLine(string name) => name switch
    {
        "sum-12000" => "$x = 1" + string.Concat(Enumerable.Repeat("+1", 12_000)) + "; 'sum ' + $x",
        "sum-40000" => "$x = 1" + string.Concat(Enumerable.Repeat("+1", 40_000)) + "; 'sum ' + $x",
        "secret-4000" => "$password = 'x'" + string.Concat(Enumerable.Repeat("+'x'", 4_000)),
        "sum-max" => "$x = 1" + string.Concat(Enumerable.Repeat("+1", 131_000)),
        "member-max" => "$x = 'a'" + string.Concat(Enumerable.Repeat(".a", 131_000)),
        "not-max" => "$x = " + new string('!', 262_000) + "1",
        "paren-max" => "$x = " + new string('(', 131_000) + "1" + new string(')', 131_000),
        "sum-400" => "$x = 1" + string.Concat(Enumerable.Repeat("+1", 400)) + "; 'sum ' + $x",
        "secret-400" => "$password = 'x'" + string.Concat(Enumerable.Repeat("+'x'", 400)) + "; $password.Length",
        _ => throw new ArgumentException("unknown case " + name, nameof(name)),
    };

    /// <summary>
    /// Asks the session everything the window asks about a line - complete, remember, highlight, complete at the end, run - from a thread
    /// with 1 MB of stack (the default of a thread on Windows, the window's UI thread among them), then runs <c>'alive'</c>. Each answer
    /// is written to the report as soon as it is known, so a crash shows how far the case got.
    /// </summary>
    private static void DeepInput(string name, string report)
    {
        var line = DeepLine(name);
        File.WriteAllText(report, name + "\n");
        Exception? failure = null;
        var thread = new Thread(
            () =>
            {
                try
                {
                    DeepInputOnThread(line, report);
                }
                catch (Exception ex)
                {
                    failure = ex;
                }
            },
            1024 * 1024);
        thread.Start();
        thread.Join();
        if (failure is not null)
        {
            throw new InvalidOperationException("The deep input case failed.", failure);
        }

        File.AppendAllText(report, "done\n");
    }

    private static void DeepInputOnThread(string line, string report)
    {
        void Write(string text) => File.AppendAllText(report, text + "\n");
        var session = CreateAsync().GetAwaiter().GetResult();
        try
        {
            Write("complete=" + session.IsInputComplete(line));
            Write("remember=" + session.ShouldRemember(line));
            Write("highlighted=" + (session.Tokenize(line).Count > 0));
            Write("completions=" + session.CompleteAsync(line, line.Length, CancellationToken.None).AsTask().GetAwaiter().GetResult().Matches.Count);
            Write("run=" + Collect(session, line));
            Write("next=" + Collect(session, "'alive'"));
        }
        finally
        {
            session.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }

    /// <summary>The run's first line of output or error and its end, e.g. <c>Failed: The command is too long…</c>.</summary>
    private static string Collect(PowerShellSession session, string script)
    {
        var first = string.Empty;
        var end = "none";
        var items = session.RunAsync(script, CancellationToken.None).GetAsyncEnumerator(CancellationToken.None);
        try
        {
            while (items.MoveNextAsync().AsTask().GetAwaiter().GetResult())
            {
                switch (items.Current)
                {
                    case PowerShellTextItem text when first.Length == 0:
                        first = text.Text.Split('\n')[0];
                        break;
                    case PowerShellRunEndItem runEnd:
                        end = runEnd.State.ToString();
                        break;
                }
            }
        }
        finally
        {
            items.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }

        return end + ": " + first;
    }

    private static Task<PowerShellSession> CreateAsync(TimeProvider? time = null) =>
        PowerShellSession.CreateAsync(new PowerShellSessionOptions
        {
            Context = new AdmWindowContext(new FakeDirectorySession(), "probe"),
            TimeProvider = time ?? TimeProvider.System,
        });

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
