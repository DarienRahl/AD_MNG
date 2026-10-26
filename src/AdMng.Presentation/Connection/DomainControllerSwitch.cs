using AdMng.Core.Directory;
using AdMng.Core.Ldap;

namespace AdMng.Presentation.Connection;

/// <summary>Outcome of "Change domain controller" (<see cref="Session.ISessionManager.SwitchDomainControllerAsync"/>).</summary>
/// <param name="PreviousServer">The DC the replaced session was pinned to.</param>
/// <param name="Session">The new current session.</param>
public sealed record DomainControllerSwitch(string PreviousServer, IDirectorySession Session)
{
    /// <summary><see cref="Core.Audit.AuditEntry.Action"/> of the session log entry recording a switch.</summary>
    public const string SessionLogAction = "ChangeDomainController";

    /// <summary>
    /// True when <paramref name="next"/> is the same connection as <paramref name="previous"/> moved to another DC:
    /// same domain (default naming context) and bound identity, another server. The shell then keeps its tabs and
    /// windows of the previous session say they belong to the previous DC.
    /// </summary>
    public static bool IsSwitch(IDirectorySession? previous, IDirectorySession? next) =>
        previous is not null && next is not null && !ReferenceEquals(previous, next)
        && DistinguishedName.AreEqual(previous.Info.DefaultNamingContext, next.Info.DefaultNamingContext)
        && string.Equals(previous.Info.BoundIdentity, next.Info.BoundIdentity, StringComparison.OrdinalIgnoreCase)
        && !string.Equals(previous.Info.Server, next.Info.Server, StringComparison.OrdinalIgnoreCase);
}

/// <summary>The chosen server is a domain controller of another domain; the session was not changed.</summary>
public sealed class DomainControllerMismatchException : InvalidOperationException
{
    public DomainControllerMismatchException()
        : this(string.Empty, string.Empty, string.Empty)
    {
    }

    public DomainControllerMismatchException(string message)
        : base(message)
    {
        Server = ServerDomain = ExpectedDomain = string.Empty;
    }

    public DomainControllerMismatchException(string message, Exception innerException)
        : base(message, innerException)
    {
        Server = ServerDomain = ExpectedDomain = string.Empty;
    }

    /// <param name="server">The DC that answered.</param>
    /// <param name="serverDomain">DNS name of its domain.</param>
    /// <param name="expectedDomain">DNS name of the session's domain.</param>
    public DomainControllerMismatchException(string server, string serverDomain, string expectedDomain)
        : base($"{server} is a domain controller of {serverDomain}, not of {expectedDomain}.")
    {
        Server = server;
        ServerDomain = serverDomain;
        ExpectedDomain = expectedDomain;
    }

    public string Server { get; }

    public string ServerDomain { get; }

    public string ExpectedDomain { get; }
}

/// <summary>The chosen name (an alias or address) leads to the DC the session already works with; nothing was changed.</summary>
public sealed class SameDomainControllerException : InvalidOperationException
{
    public SameDomainControllerException()
        : this(string.Empty)
    {
    }

    /// <param name="server">The current DC.</param>
    public SameDomainControllerException(string server)
        : base($"The session already works with {server}.")
    {
        Server = server;
    }

    public SameDomainControllerException(string server, Exception innerException)
        : base($"The session already works with {server}.", innerException)
    {
        Server = server;
    }

    public string Server { get; }
}
