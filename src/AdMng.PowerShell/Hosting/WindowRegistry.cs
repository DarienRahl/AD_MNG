using System.Management.Automation;
using System.Management.Automation.Runspaces;
using System.Runtime.CompilerServices;
using AdMng.PowerShell.Commands;

namespace AdMng.PowerShell.Hosting;

/// <summary>
/// How a command finds the window it runs in (docs/dev/r1-powershell-window.md 4.3): a table inside the engine, keyed by the runspace
/// the command runs in (<see cref="Runspace.DefaultRunspace"/> on the pipeline thread). Nothing of the session hands it to a script:
/// no variable, no parameter, no member of <c>$Host</c> (its <c>PrivateData</c> is empty), and a script in ConstrainedLanguage cannot
/// read a static member of a type that is not a core type. In the trusted mode (FullLanguage) .NET reflection can read anything in the
/// process, this table included - the boundary ADR-0010 states.
/// </summary>
internal static class WindowRegistry
{
    private static readonly ConditionalWeakTable<Runspace, WindowEntry> Entries = [];

    public static void Register(Runspace runspace, WindowEntry entry) => Entries.AddOrUpdate(runspace, entry);

    public static void Remove(Runspace runspace) => Entries.Remove(runspace);

    /// <summary>The entry of <paramref name="runspace"/>, or null for a runspace no window created.</summary>
    public static WindowEntry? Find(Runspace? runspace) => runspace is not null && Entries.TryGetValue(runspace, out var entry) ? entry : null;

    /// <summary>The entry of the runspace the calling pipeline thread runs.</summary>
    public static WindowEntry? Current() => Find(Runspace.DefaultRunspace);
}

/// <summary>What the commands of one window need: its services and the bridge its replacement commands drive the engine's cmdlets through.</summary>
/// <param name="Context">The window's services; null for a session without a window.</param>
/// <param name="Bridge">
/// <c>param($command, $parameters) &amp; $command @parameters</c>, made while the session was in FullLanguage (<see cref="WindowSetupCommand"/>),
/// so that a steppable pipeline can be built from it in a constrained session. It never runs a script's text: it only binds the engine's
/// cmdlet object and the parameters a replacement command was given, and the cmdlet then runs in the caller's language mode.
/// </param>
internal sealed record WindowEntry(IAdmWindowContext? Context, ScriptBlock Bridge);

/// <summary>
/// Runs once, while a new session is set up and before anything of the operator runs: makes the bridge of <see cref="WindowEntry"/> (a
/// script block takes the language mode of the session it is made in) and hands over the session's job repository, so that the session
/// can end every job a command leaves behind. It runs on the runspace's own pipeline thread (<c>PSThreadOptions.ReuseThread</c>: every
/// command of the runspace runs there) and makes it a background thread - the engine makes it a foreground thread, and one stuck in .NET
/// code that never returns (a run the session left) would keep the program's process alive after its last window closed. Never in the
/// session's command list; invoked through its <see cref="CmdletInfo"/>.
/// </summary>
[Cmdlet(VerbsData.Initialize, "AdmWindowSession")]
internal sealed class WindowSetupCommand : PSCmdlet
{
    internal const string BridgeText = "param($command, $parameters) & $command @parameters";

    protected override void EndProcessing()
    {
        Thread.CurrentThread.IsBackground = true;
        WriteObject(new WindowSetup(ScriptBlock.Create(BridgeText), JobRepository));
    }
}

/// <summary>What <see cref="WindowSetupCommand"/> returns.</summary>
internal sealed record WindowSetup(ScriptBlock Bridge, JobRepository Jobs);
