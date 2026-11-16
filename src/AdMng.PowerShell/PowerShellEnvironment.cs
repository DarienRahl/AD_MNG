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
