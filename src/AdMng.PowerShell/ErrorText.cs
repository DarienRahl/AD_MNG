using System.Management.Automation;

namespace AdMng.PowerShell;

/// <summary>The text of a terminating error: its message and the place in the script, in the shape the window shows.</summary>
internal static class ErrorText
{
    public static string Describe(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        var position = (exception as IContainsErrorRecord)?.ErrorRecord?.InvocationInfo?.PositionMessage;
        return string.IsNullOrWhiteSpace(position) ? exception.Message : exception.Message + "\n" + position.Trim();
    }
}
