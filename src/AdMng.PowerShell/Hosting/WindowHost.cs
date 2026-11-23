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

    /// <summary>The script asked to exit (<c>exit</c>); the window decides what that means (R1: nothing).</summary>
    public event Action<int>? ExitRequested;

    public override void SetShouldExit(int exitCode) => ExitRequested?.Invoke(exitCode);

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
