using System.Globalization;
using System.Management.Automation;
using System.Management.Automation.Host;

namespace AdMng.PowerShell.Hosting;

/// <summary>
/// The host of one window's runspace: it routes text, prompts and progress into the window. It carries nothing of the window's
/// connection - its <c>PrivateData</c> is empty (a parallel runspace of the audit reached the directory session through it, PS-01);
/// AD Manager's commands find their window in <see cref="WindowRegistry"/>.
/// </summary>
internal sealed class WindowHost : PSHost
{
    private readonly WindowHostUserInterface _ui;

    public WindowHost(Version version, CultureInfo culture, WindowHostRawUserInterface raw)
    {
        Version = version;
        CurrentCulture = culture;
        CurrentUICulture = culture;
        _ui = new WindowHostUserInterface(raw);
    }

    public WindowHostUserInterface Output => _ui;

    public override string Name => "AD Manager PowerShell";

    public override Version Version { get; }

    public override Guid InstanceId { get; } = Guid.NewGuid();

    public override PSHostUserInterface UI => _ui;

    public override CultureInfo CurrentCulture { get; }

    public override CultureInfo CurrentUICulture { get; }

    private int _exitCode;
    private int _exited;

    /// <summary>
    /// The script ended with <c>exit</c> (audit PS-12: the event that once took it had no subscriber, and <c>exit</c> ended the run
    /// without a word). The engine calls this on the pipeline thread before the run ends; the session puts the code into the end item
    /// (<see cref="TakeExit"/>) and the window says that the command ended there. The window does not close.
    /// </summary>
    public override void SetShouldExit(int exitCode)
    {
        Volatile.Write(ref _exitCode, exitCode);
        Volatile.Write(ref _exited, 1);
    }

    /// <summary>The exit code of a run that ended with <c>exit</c>, or null; clears it.</summary>
    public int? TakeExit() => Interlocked.Exchange(ref _exited, 0) == 1 ? Volatile.Read(ref _exitCode) : null;

    public override void EnterNestedPrompt() =>
        throw new PSNotSupportedException("Nested prompts are not available in the AD Manager PowerShell window.");

    public override void ExitNestedPrompt()
    {
    }

    public override void NotifyBeginApplication()
    {
    }

    public override void NotifyEndApplication()
    {
    }
}
