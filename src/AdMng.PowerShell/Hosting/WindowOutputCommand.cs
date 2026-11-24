using System.Management.Automation;

namespace AdMng.PowerShell.Hosting;

/// <summary>
/// The window's pipeline is <c>script | Write-AdmWindowOutput | Out-Default</c>: this command hands every object (and every error
/// merged into the output) on unchanged. It is there for console programs (audit PS-06): the engine starts a program that is last in
/// its pipeline with the process's own standard output and error when the next command downstream is <c>Out-Default</c> - in
/// AdMng.exe on Windows a hidden console, so <c>whoami</c> or <c>nltest /dsgetdc:</c> showed nothing. With this command downstream
/// the engine captures what the program writes, as it does for <c>whoami | Out-String</c>: its lines become text of the window and
/// its error output becomes errors of the window. Compiled, never in the session state (the operator cannot call it), so it works the
/// same in ConstrainedLanguage.
/// </summary>
[Cmdlet(VerbsCommunications.Write, "AdmWindowOutput")]
internal sealed class WindowOutputCommand : PSCmdlet
{
    /// <summary>The command's name: added to the pipeline through its <see cref="CmdletInfo"/>, not looked up.</summary>
    public const string Name = "Write-AdmWindowOutput";

    [Parameter(ValueFromPipeline = true)]
    public PSObject? InputObject { get; set; }

    protected override void ProcessRecord()
    {
        if (InputObject is not null)
        {
            WriteObject(InputObject);
        }
    }
}
