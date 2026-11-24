using System.Management.Automation;
using System.Management.Automation.Host;

namespace AdMng.PowerShell.Hosting;

/// <summary>
/// <c>Clear-Host</c> (<c>cls</c>) of the window (audit PS-12): clears the window's output, like Ctrl+L; the history of commands stays.
/// The engine's own <c>Clear-Host</c> is a function that on Windows fills the console buffer through the host's raw user interface - the
/// window has no buffer, so it did nothing - and on other systems writes terminal codes to the process's console. This command asks the
/// host to clear its whole buffer (the rectangle -1, -1, -1, -1), which the window's raw user interface turns into a
/// <see cref="AdMng.Core.PowerShell.PowerShellClearItem"/>. Compiled, so it works the same in ConstrainedLanguage.
/// </summary>
[Cmdlet(VerbsCommon.Clear, "Host")]
internal sealed class WindowClearHostCommand : PSCmdlet
{
    protected override void EndProcessing()
    {
        var raw = Host.UI.RawUI;
        raw.SetBufferContents(new Rectangle(-1, -1, -1, -1), new BufferCell(' ', raw.ForegroundColor, raw.BackgroundColor, BufferCellType.Complete));
    }
}
