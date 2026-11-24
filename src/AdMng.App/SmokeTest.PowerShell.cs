using System.Globalization;
using System.IO;
using System.Text;
using AdMng.App.Infrastructure;
using AdMng.App.PowerShell;
using AdMng.App.Services;
using AdMng.Core.PowerShell;
using AdMng.PowerShell;
using AdMng.PowerShell.Commands;
using AdMng.PowerShell.Guards;
using AdMng.Presentation.Dialogs;
using Microsoft.Extensions.DependencyInjection;

namespace AdMng.App;

/// <summary>Track R, stage R1 (docs/dev/r1-powershell-window.md): the PowerShell engine and window in the smoke test.</summary>
internal static partial class SmokeTest
{
    /// <summary>
    /// Proves that the published <c>AdMng.exe</c> - a single file, self-contained - starts the embedded PowerShell engine (ADR-0010
    /// decision 1): a runspace is created, <c>1 + 1</c> and <c>Get-AdmVersion</c> run, the window starts in ConstrainedLanguage with the
    /// execution policy Restricted, the ten commands of AD Manager are registered, no write command exists, the help of a
    /// command (the file beside the program) is found, the session has exactly the commands of the allow-list, the window's
    /// ForEach-Object (without -Parallel) works, a regular expression that backtracks ends with an error (the time limit that
    /// <c>ProcessStart</c> sets before the first regular expression of the process) and a timer the trusted mode leaves behind does not
    /// end the program when it fires after its window closed. No directory is involved: the commands that need one are covered by the UI tests.
    /// <c>$PSHOME</c> is the folder the program reads the engine's configuration from - in the single file the program's folder with its
    /// <c>powershell.config.json</c> - and the policy note's reads run once on the machine. What it found goes to the report (the version,
    /// <c>$PSHOME</c>, the time of the first runspace), so that the log of a CI run says how the single-file layout behaves.
    /// </summary>
    private static async Task CheckPowerShellEngineAsync()
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        await using var session = await PowerShellSession.CreateAsync(new PowerShellSessionOptions { Culture = CultureInfo.InvariantCulture }, CancellationToken.None).ConfigureAwait(true);
        var first = clock.Elapsed;

        var sum = await RunPowerShellAsync(session, "1 + 1").ConfigureAwait(true);
        Expect(sum == "2", $"'1 + 1' gave '{sum}'.");

        var mode = await RunPowerShellAsync(session, "$ExecutionContext.SessionState.LanguageMode").ConfigureAwait(true);
        Expect(mode == "ConstrainedLanguage", $"The window's language mode is '{mode}', not ConstrainedLanguage.");

        var policy = await RunPowerShellAsync(session, "Get-ExecutionPolicy").ConfigureAwait(true);
        Expect(policy == "Restricted", $"The execution policy is '{policy}', not Restricted.");

        var version = await RunPowerShellAsync(session, "Get-AdmVersion | Out-String").ConfigureAwait(true);
        Expect(version.Contains("EngineVersion", StringComparison.Ordinal) && version.Contains("Constrained", StringComparison.Ordinal), $"Get-AdmVersion gave '{version}'.");

        var commands = await RunPowerShellAsync(session, "(Get-Command -Name *-Adm* | Sort-Object Name | ForEach-Object { $_.Name }) -join ' '").ConfigureAwait(true);
        var expected = string.Join(' ', AdmCommands.Names.Order(StringComparer.OrdinalIgnoreCase));
        Expect(string.Equals(commands, expected, StringComparison.OrdinalIgnoreCase), $"The commands of AD Manager are '{commands}', expected '{expected}'.");

        var help = await RunPowerShellAsync(session, "Get-Help Get-AdmUser -Examples | Out-String").ConfigureAwait(true);
        Expect(help.Contains("Get-AdmUser -Department", StringComparison.Ordinal), "The help of Get-AdmUser has no example: its file is not beside the program.");

        var refused = await RunPowerShellAsync(session, "Import-Module Microsoft.PowerShell.Utility 2>&1 | Out-String").ConfigureAwait(true);
        Expect(refused.Contains("limited", StringComparison.OrdinalIgnoreCase), $"Import-Module was not refused: '{refused}'.");

        // The allow-list of the session and the window's ForEach-Object (its bridge to the engine's cmdlet) in the single file.
        var count = await RunPowerShellAsync(session, "(Get-Command -CommandType Cmdlet, Function | Measure-Object).Count").ConfigureAwait(true);
        var allowed = CommandGuards.EngineCmdlets.Count + CommandGuards.EngineFunctions.Count + CommandGuards.Replacements.Count
            + CommandGuards.WindowCommands.Count + CommandGuards.Refusals.Count + AdmCommands.Names.Count;
        Expect(count == allowed.ToString(CultureInfo.InvariantCulture), $"The session has {count} commands, the allow-list {allowed}.");
        var doubled = await RunPowerShellAsync(session, "(1, 2 | ForEach-Object { $_ * 2 }) -join ','").ConfigureAwait(true);
        Expect(doubled == "2,4", $"ForEach-Object gave '{doubled}'.");
        var syntax = await RunPowerShellAsync(session, "Get-Command ForEach-Object -Syntax | Out-String").ConfigureAwait(true);
        Expect(!syntax.Contains("Parallel", StringComparison.Ordinal), "ForEach-Object of the window has -Parallel.");

        // The time limit of regular expressions (ProcessStart, docs/dev/r1-powershell-window.md 4.2): without it this match holds the
        // window and a processor core for hours.
        Expect(PowerShellEnvironment.RegularExpressionsLimited, "Regular expressions have no time limit: ProcessStart did not run before the first one.");
        var limited = await RunPowerShellAsync(session, "try { $null = 'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaa!' -match '^(a+)+$'; 'matched' } catch { 'limited' }").ConfigureAwait(true);
        Expect(limited == "limited", $"A match that backtracks gave '{limited}'.");

        // A script block the trusted mode leaves behind fires after its window closed (ProcessStart, docs/dev/r1-powershell-window.md
        // 12.8): without the containment its error ends this process, and the smoke test with it.
        Expect(PowerShellEnvironment.LeftoverScriptBlocksContained, "A script block left behind by the trusted mode would end the program: ProcessStart did not contain it.");
        await LeaveATimerBehindAsync().ConfigureAwait(true);

        var home = await RunPowerShellAsync(session, "$PSHOME").ConfigureAwait(true);
        CheckEngineHome(home);
        var recording = CheckPolicies();
        var second = await TimeASecondRunspaceAsync().ConfigureAwait(true);
        Console.Error.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"smoke test: the PowerShell engine {PowerShellEnvironment.EngineHome} ($PSHOME '{home}', single file: {IsSingleFile}), policies read (recording: {recording}), the first runspace {first.TotalMilliseconds:0} ms, the second {second.TotalMilliseconds:0} ms"));
    }

    /// <summary>The engine's assembly is bundled in the program (the published single file): it has no location of its own.</summary>
#pragma warning disable IL3000 // Location is empty for an assembly bundled in a single-file app: that is what is asked.
    private static bool IsSingleFile => string.IsNullOrEmpty(typeof(System.Management.Automation.PSObject).Assembly.Location);
#pragma warning restore IL3000

    /// <summary>
    /// <c>$PSHOME</c> is where the program reads the engine's all-users <c>powershell.config.json</c> (<see cref="PowerShellEnvironment.EngineHome"/>),
    /// and in the published single file it is the folder of <c>AdMng.exe</c>, where the publish puts the program's file (audit W-12: the
    /// smoke test printed both and asserted neither). A build that is not a single file keeps the engine in <c>runtimes\</c>; only the
    /// first rule holds there.
    /// </summary>
    private static void CheckEngineHome(string home)
    {
        static string Folder(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

        Expect(
            string.Equals(Folder(home), Folder(PowerShellEnvironment.EngineHome), StringComparison.OrdinalIgnoreCase),
            $"$PSHOME is '{home}', the program reads the engine's configuration from '{PowerShellEnvironment.EngineHome}'.");
        if (IsSingleFile)
        {
            Expect(
                string.Equals(Folder(home), Folder(AppContext.BaseDirectory), StringComparison.OrdinalIgnoreCase),
                $"$PSHOME is '{home}', not the folder of the program '{AppContext.BaseDirectory}'.");
            Expect(
                File.Exists(Path.Combine(home, WindowsPowerShellPolicySource.ConfigFileName)),
                $"The program's {WindowsPowerShellPolicySource.ConfigFileName} is not in $PSHOME '{home}'.");
        }
    }

    /// <summary>
    /// The policy note's reads on this machine (audit W-12: the published program never ran them): they must not throw, and in the
    /// single file the program's <c>powershell.config.json</c> decides every recording policy that no Group Policy sets, also over a
    /// user's file that turns them on (simulated in memory, nothing is written). Returns whether anything records (no path, no value).
    /// </summary>
    private static bool CheckPolicies()
    {
        var source = new WindowsPowerShellPolicySource();
        var policies = new Presentation.PowerShell.PowerShellPolicyReader(source).Read();
        if (IsSingleFile)
        {
            var simulated = new Presentation.PowerShell.PowerShellPolicyReader(new UserFileSource(source,
                """{ "PowerShellPolicies": { "Transcription": { "EnableTranscripting": true }, "ScriptBlockLogging": { "EnableScriptBlockLogging": true }, "ModuleLogging": { "EnableModuleLogging": true } } }""")).Read();
            foreach (var (name, origin) in new[]
            {
                ("transcription", simulated.Transcription.Origin),
                ("script block logging", simulated.ScriptBlockLogging.Origin),
                ("module logging", simulated.ModuleLogging.Origin),
            })
            {
                Expect(
                    origin is { Store: not Presentation.PowerShell.PowerShellPolicyStore.ConfigFile } or { Scope: Presentation.PowerShell.PowerShellPolicyScope.Computer },
                    $"The {name} of a user's powershell.config.json wins over the program's file (origin {origin}).");
            }
        }

        return policies.IsRecording;
    }

    /// <summary>The machine's policies with a user's <c>powershell.config.json</c> given in memory.</summary>
    private sealed class UserFileSource(Presentation.PowerShell.IPowerShellPolicySource machine, string userFile) : Presentation.PowerShell.IPowerShellPolicySource
    {
        public object? GetValue(Presentation.PowerShell.PowerShellPolicyScope scope, string subKey, string valueName) => machine.GetValue(scope, subKey, valueName);

        public IReadOnlyList<string>? GetValueNames(Presentation.PowerShell.PowerShellPolicyScope scope, string subKey) => machine.GetValueNames(scope, subKey);

        public string? ReadConfigFile(Presentation.PowerShell.PowerShellPolicyScope scope) =>
            scope == Presentation.PowerShell.PowerShellPolicyScope.User ? userFile : machine.ReadConfigFile(scope);

        public PowerShellLockdownState GetLockdownState() => machine.GetLockdownState();
    }

    private static async Task LeaveATimerBehindAsync()
    {
        await using (var trusted = await PowerShellSession.CreateAsync(new PowerShellSessionOptions { Culture = CultureInfo.InvariantCulture }, CancellationToken.None).ConfigureAwait(true))
        {
            await trusted.SetLanguageModeAsync(PowerShellLanguageMode.Full, CancellationToken.None).ConfigureAwait(true);
            var started = await RunPowerShellAsync(trusted, "$global:timer = [System.Threading.Timer]::new([System.Threading.TimerCallback]{ 'late' }, $null, 300, -1); 'started'").ConfigureAwait(true);
            Expect(started == "started", $"The timer of the trusted mode gave '{started}'.");
        }

        await Task.Delay(TimeSpan.FromSeconds(1.5)).ConfigureAwait(true);
    }

    private static async Task<TimeSpan> TimeASecondRunspaceAsync()
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        await using var session = await PowerShellSession.CreateAsync(new PowerShellSessionOptions { Culture = CultureInfo.InvariantCulture }, CancellationToken.None).ConfigureAwait(true);
        return clock.Elapsed;
    }

    /// <summary>
    /// The PowerShell window as the factory makes it for a connection that is not there (it says so and starts no engine: the window
    /// of a connected domain is covered by the UI tests), and the grid of <c>Out-AdmGrid</c> over a sample table.
    /// </summary>
    private static void RenderPowerShellWindows(IServiceProvider services, IWindowService windows)
    {
        var factory = services.GetRequiredService<IDialogViewModelFactory>();
        UiTestSupport.ShowOffScreen(windows.CreateWindow(factory.CreatePowerShell()));
        UiTestSupport.ShowOffScreen(windows.CreateWindow(factory.CreatePowerShellGrid(new PowerShellGridTable(
            ["DistinguishedName", "Name", "Count"],
            [["CN=Jan Kowalski,OU=Firma,DC=contoso,DC=test", "Jan Kowalski", "10"], ["CN=Łukasz Żółć,OU=Firma,DC=contoso,DC=test", "Łukasz Żółć", null]],
            "Sample",
            "DistinguishedName"))));
    }

    private static async Task<string> RunPowerShellAsync(IPowerShellSession session, string script)
    {
        var text = new StringBuilder();
        await foreach (var item in session.RunAsync(script, CancellationToken.None).ConfigureAwait(true))
        {
            if (item is PowerShellTextItem output)
            {
                text.Append(output.Text);
            }
            else if (item is PowerShellRunEndItem { State: not PowerShellRunState.Completed } end)
            {
                throw new InvalidOperationException($"'{script}' ended as {end.State}: {text}");
            }
        }

        return text.ToString().Trim();
    }

    private static void Expect(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException("PowerShell engine: " + message);
        }
    }
}
