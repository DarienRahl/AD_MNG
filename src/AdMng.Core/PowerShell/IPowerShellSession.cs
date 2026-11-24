namespace AdMng.Core.PowerShell;

/// <summary>
/// One PowerShell window's engine session (docs/dev/r1-powershell-window.md 4.1): one runspace, never shared between
/// windows, disposed with the window's scope. The view model talks to it only through this contract; the implementation is
/// <c>AdMng.PowerShell.PowerShellSession</c> (AdMng.App only). Everything here lives in memory.
/// </summary>
public interface IPowerShellSession : IAsyncDisposable
{
    /// <summary>The language mode now: <see cref="PowerShellLanguageMode.Constrained"/> until the operator turns the trusted mode on.</summary>
    PowerShellLanguageMode LanguageMode { get; }

    /// <summary>Why the trusted mode cannot be turned on (None when it can).</summary>
    PowerShellTrustedModeBlock TrustedModeBlock { get; }

    /// <summary>True while a run is active (completion and the language switch are refused meanwhile).</summary>
    bool IsRunning { get; }

    /// <summary>The width, in characters, the engine's formatting uses for tables and lists.</summary>
    int OutputWidth { get; set; }

    /// <summary>
    /// Runs <paramref name="script"/> and streams what it produces: text of all streams (formatted by the engine's own
    /// formatting, the way the console host does it), progress, prompts, and a last <see cref="PowerShellRunEndItem"/>.
    /// Cancelling the token stops the pipeline; the enumeration still ends with the end item. A run that does not end within the grace
    /// period of a stop ends as <see cref="PowerShellRunState.Abandoned"/>: the session left it to its thread and runs the next command
    /// in a new runspace.
    /// </summary>
    /// <exception cref="InvalidOperationException">A run is already active.</exception>
    /// <exception cref="PowerShellSessionUnavailableException">The session has no runspace any more (a new one could not be made).</exception>
    IAsyncEnumerable<PowerShellOutputItem> RunAsync(string script, CancellationToken cancellationToken);

    /// <summary>Stops the active run (no effect when idle); completes when the session is idle again: the run ended, or the session left it.</summary>
    Task StopAsync();

    /// <summary>Completion of the input at the cursor on the idle runspace; no file system matches; empty while a run is active.</summary>
    ValueTask<PowerShellCompletionResult> CompleteAsync(string input, int cursorIndex, CancellationToken cancellationToken);

    /// <summary>Highlight spans of the input (pure, from the parser).</summary>
    IReadOnlyList<PowerShellSyntaxSpan> Tokenize(string input);

    /// <summary>True unless the input ends inside an open quote, bracket or here-string: decides Enter versus a new line.</summary>
    bool IsInputComplete(string input);

    /// <summary>False for a line that must not enter the history (empty, or a plain-text password).</summary>
    bool ShouldRemember(string line);

    /// <summary>
    /// Switches the language mode; refused (throws, nothing changes) under a lockdown or while a run is active. Turning the trusted mode
    /// on keeps the runspace and its variables. Turning it off makes a new runspace in ConstrainedLanguage (audit PS-02): nothing the
    /// trusted mode defined - variables, functions, aliases, script blocks, modules, members of the host - is in it, so none of it runs
    /// in FullLanguage afterwards; the lockdown is asked again. It takes as long as a new runspace (a fraction of a second).
    /// </summary>
    /// <exception cref="InvalidOperationException">A run is active, or a lockdown forbids the trusted mode.</exception>
    /// <exception cref="PowerShellSessionUnavailableException">
    /// The new runspace could not be made: the session is constrained, has no runspace and runs no command any more.
    /// </exception>
    Task SetLanguageModeAsync(PowerShellLanguageMode mode, CancellationToken cancellationToken);
}
