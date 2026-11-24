using System.Text.RegularExpressions;

namespace AdMng.PowerShell;

/// <summary>
/// The engine's process-wide environment (docs/dev/r1-powershell-window.md 4.2, ADR-0010 decision 8): set once, before the
/// first runspace of the process, and over what the user's own environment says - a variable of the user must not weaken a
/// privacy setting. Every public entry point of the engine calls <see cref="Apply"/>. What each setting prevents was observed
/// with Microsoft.PowerShell.SDK 7.6.10 (the dev note has the table): without <c>POWERSHELL_TELEMETRY_OPTOUT</c> the first
/// command writes a telemetry identifier file; with module auto-loading on (the default) and discovery, the module analysis
/// cache is written to the user's cache folder about 12 s later; <c>Get-Module -ListAvailable</c> writes it even with
/// auto-loading off unless <c>PSModuleAnalysisCachePath</c> is unwritable.
/// </summary>
public static class PowerShellEnvironment
{
    /// <summary>The name of the setting .NET reads for the default time limit of a regular expression.</summary>
    private const string RegexTimeoutKey = "REGEX_DEFAULT_MATCH_TIMEOUT";

    private static readonly object Gate = new();
    private static bool _applied;

    /// <summary>The null device of this platform: <c>/dev/null</c>, or <c>NUL</c> on Windows.</summary>
    public static string NullDevice => OperatingSystem.IsWindows() ? "NUL" : "/dev/null";

    /// <summary>
    /// The path the module analysis cache is pointed at: a file "inside" the null device. Creating its folder fails with an
    /// ordinary I/O error, which the engine swallows (a cache that cannot be written is common: a service account, a
    /// read-only profile) - the same path as the unwritable <c>/proc/nonexistent/cache</c> that was observed on Linux, and
    /// no device name used bare as a path, whose handling on Windows was not observed.
    /// </summary>
    public static string UnwritableCachePath => Path.Combine(NullDevice, "ModuleAnalysisCache");

    /// <summary>The variables <see cref="Apply"/> sets, with their values.</summary>
    public static IReadOnlyDictionary<string, string> Variables { get; } = new Dictionary<string, string>
    {
        // No telemetry identifier file, no telemetry.
        ["POWERSHELL_TELEMETRY_OPTOUT"] = "1",

        // The diagnostics channel (Enter-PSHostProcess): another process of the same user must not attach to AD Manager.
        ["POWERSHELL_DIAGNOSTICS_OPTOUT"] = "1",

        // Nothing ever looks for an update (a console host feature; set so that it never will).
        ["POWERSHELL_UPDATECHECK"] = "Off",

        // The module analysis cache is never written (an unwritable path is swallowed by the engine).
        ["PSModuleAnalysisCachePath"] = UnwritableCachePath,
    };

    /// <summary>
    /// The folder the engine calls <c>$PSHOME</c>: where it looks for its all-users <c>powershell.config.json</c> and its modules. It is the
    /// folder of <c>System.Management.Automation.dll</c>; in a single-file program the assembly is bundled, has no location, and the engine
    /// takes the folder of the program (observed with a self-contained single-file publish: <c>$PSHOME</c> was the program's folder).
    /// </summary>
    public static string EngineHome
    {
        get
        {
#pragma warning disable IL3000 // Location is empty for an assembly bundled in a single-file app: handled below, as the engine itself does.
            var location = typeof(System.Management.Automation.PSObject).Assembly.Location;
#pragma warning restore IL3000
            return string.IsNullOrEmpty(location) ? AppContext.BaseDirectory : Path.GetDirectoryName(location) ?? AppContext.BaseDirectory;
        }
    }

    /// <summary>
    /// The longest one match of a regular expression may take in a process that hosts the engine (<see cref="LimitRegularExpressions"/>).
    /// Shorter than the grace period of Stop (5 s), so that Stop during a match that backtracks ends the command, not only the window's
    /// wait for it; long enough for any pattern that does not backtrack, on any text the program handles.
    /// </summary>
    public static TimeSpan RegexMatchTimeout { get; } = TimeSpan.FromSeconds(2);

    /// <summary>
    /// True when a regular expression made without its own time limit gets <see cref="RegexMatchTimeout"/> in this process, i.e. when
    /// <see cref="LimitRegularExpressions"/> ran before the first regular expression of the process.
    /// </summary>
    public static bool RegularExpressionsLimited => new Regex(string.Empty).MatchTimeout == RegexMatchTimeout;

    /// <summary>
    /// Gives every regular expression of the process that has no time limit of its own the limit <see cref="RegexMatchTimeout"/>: a match
    /// of .NET's regular expression engine is not interrupted by Stop, and <c>-match</c>, <c>-replace</c>, <c>-split</c>,
    /// <c>switch -regex</c>, <c>Select-String</c> and <c>[regex]</c> - all of ConstrainedLanguage - use it, so one pattern that backtracks
    /// ('^(a+)+$' against thirty a's and a '!') held the window's runspace and a processor core until the program ended. With the limit
    /// the match fails with <see cref="RegexMatchTimeoutException"/>, an error of its statement. .NET reads the setting once, when the
    /// first regular expression of the process is made (observed with .NET 10: a later call changes nothing, and the same setting given
    /// as text in the runtime configuration makes every regular expression throw), so a process that hosts the engine calls this before
    /// any other code of its own runs - the program and the engine's tests in their module initializers. The limit applies to the whole
    /// process; a regular expression made with its own time limit keeps it. Idempotent.
    /// </summary>
    public static void LimitRegularExpressions() => AppContext.SetData(RegexTimeoutKey, RegexMatchTimeout);

    /// <summary>True once <see cref="Apply"/> ran in this process.</summary>
    public static bool IsApplied
    {
        get
        {
            lock (Gate)
            {
                return _applied;
            }
        }
    }

    /// <summary>Sets <see cref="Variables"/> in the process environment. Idempotent and thread-safe.</summary>
    public static void Apply()
    {
        lock (Gate)
        {
            if (_applied)
            {
                return;
            }

            foreach (var (name, value) in Variables)
            {
                Environment.SetEnvironmentVariable(name, value);
            }

            _applied = true;
        }
    }
}
