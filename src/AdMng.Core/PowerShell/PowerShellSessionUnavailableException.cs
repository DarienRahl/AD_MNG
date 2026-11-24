namespace AdMng.Core.PowerShell;

/// <summary>
/// The session of a window has no runspace any more and runs no command (docs/dev/r1-powershell-window.md 4.1): the new runspace it
/// needed - after the trusted mode was turned off, or after it left a command that Stop could not end - could not be made.
/// <see cref="Exception.InnerException"/> is why. The window says that PowerShell could not be started; a new PowerShell window
/// makes a new session. The message names nothing of the directory.
/// </summary>
public sealed class PowerShellSessionUnavailableException : InvalidOperationException
{
    public PowerShellSessionUnavailableException()
        : this(DefaultMessage)
    {
    }

    public PowerShellSessionUnavailableException(string message)
        : base(message)
    {
    }

    public PowerShellSessionUnavailableException(string message, Exception? innerException)
        : base(message, innerException)
    {
    }

    /// <summary>The engine's own words (English, like its other messages); the window shows its translation of the cause.</summary>
    public const string DefaultMessage = "The PowerShell session of the window could not be started again; open a new PowerShell window.";
}
