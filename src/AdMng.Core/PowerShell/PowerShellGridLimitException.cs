namespace AdMng.Core.PowerShell;

/// <summary>
/// <c>Out-AdmGrid</c> was refused a grid window: <see cref="Limit"/> grid windows are open already, the most the PowerShell window
/// of a connection may have at a time (owner answer 12 of the 0.16.1 plan, like the ten property windows of one
/// <c>Open-AdmObject</c>) - a loop that calls the command a thousand times is not a thousand windows. The program's window actions
/// throw it; the command turns it into an error in the window's language. Closing a grid frees its place.
/// </summary>
public sealed class PowerShellGridLimitException : InvalidOperationException
{
    public PowerShellGridLimitException()
        : this(0)
    {
    }

    public PowerShellGridLimitException(int limit)
        : base($"{limit} grid windows are open, the most there may be at a time; close one of them to open another.")
    {
        Limit = limit;
    }

    public PowerShellGridLimitException(string message)
        : base(message)
    {
    }

    public PowerShellGridLimitException(string message, Exception? innerException)
        : base(message, innerException)
    {
    }

    /// <summary>The most grid windows open at a time.</summary>
    public int Limit { get; }
}
