namespace AdMng.Presentation.Session;

/// <summary>
/// A window tried to read or write in the directory session it was opened in, but that session has ended (disconnect,
/// or a new connection - possibly with another identity). Nothing was sent to the directory: windows never fall over
/// to the session that is current now (see <c>WindowViewModelBase.Session</c>).
/// </summary>
public sealed class SessionEndedException : InvalidOperationException
{
    public SessionEndedException()
        : base("The directory session this window was opened in has ended.")
    {
    }

    public SessionEndedException(string message)
        : base(message)
    {
    }

    public SessionEndedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
