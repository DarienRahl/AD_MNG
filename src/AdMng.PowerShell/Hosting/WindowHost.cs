using System.Globalization;
using System.Management.Automation;
using System.Management.Automation.Host;
using AdMng.PowerShell.Commands;

namespace AdMng.PowerShell.Hosting;

/// <summary>
/// The host of one window's runspace. It is also how AD Manager's commands reach the window's services without a global:
/// the command asks its <c>Host</c> (the engine's wrapper of this object) for <see cref="PrivateData"/>, which holds the
/// window's context in an object with no public member, so no variable, parameter or public member of the session hands
/// it to a script (in ConstrainedLanguage; in FullLanguage reflection can - the boundary ADR-0010 states).
/// </summary>
internal sealed class WindowHost : PSHost
{
    private readonly WindowHostUserInterface _ui;
    private readonly PSObject _privateData;

    public WindowHost(IAdmWindowContext? context, Version version, CultureInfo culture, WindowHostRawUserInterface raw)
    {
        Context = context;
        _privateData = PSObject.AsPSObject(new WindowPrivateData(context));
        Version = version;
        CurrentCulture = culture;
        CurrentUICulture = culture;
        _ui = new WindowHostUserInterface(raw);
    }

    public IAdmWindowContext? Context { get; }

    public WindowHostUserInterface Output => _ui;

    public override string Name => "AD Manager PowerShell";

    public override Version Version { get; }

    public override Guid InstanceId { get; } = Guid.NewGuid();

    public override PSHostUserInterface UI => _ui;

    /// <summary>
    /// How a command finds its window: <c>PSCmdlet.Host</c> is the engine's wrapper of this host, and the wrapper hands
    /// out this host's private data. The object has no public member, so a script sees nothing to read.
    /// </summary>
    public override PSObject PrivateData => _privateData;

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

/// <summary>Carries the window's context to the commands; deliberately without a public member.</summary>
internal sealed class WindowPrivateData
{
    public WindowPrivateData(IAdmWindowContext? context) => Context = context;

    internal IAdmWindowContext? Context { get; }

    public override string ToString() => "AD Manager PowerShell window";
}
