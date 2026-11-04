namespace AdMng.Presentation.EventLogs;

/// <summary>Which event log document the shell opens (or selects when it is open).</summary>
public enum EventLogDocumentKind
{
    /// <summary>"Dzienniki zdarzeń": the reader, one tab per connection window (a computer adds itself as a source).</summary>
    Reader,

    /// <summary>"Skąd blokada konta – &lt;konto&gt;": one tab per account per connection window.</summary>
    Lockout,

    /// <summary>"Nieudane logowania": one tab per connection window.</summary>
    FailedLogons,
}

/// <summary>A request to open an event log document in the connection window's shell (design note e-event-logs.md 5).</summary>
/// <param name="Kind">The document.</param>
public sealed record EventLogDocumentRequest(EventLogDocumentKind Kind)
{
    /// <summary><see cref="EventLogDocumentKind.Reader"/>: a computer (DN) whose log the reader shows (its context menu, its "Stan" page).</summary>
    public string? ComputerDn { get; init; }

    /// <summary><see cref="EventLogDocumentKind.Reader"/>: a host name to read (an FQDN, e.g. the source of a lockout).</summary>
    public string? Host { get; init; }

    /// <summary><see cref="EventLogDocumentKind.Lockout"/>: the user account (DN).</summary>
    public string? UserDn { get; init; }

    /// <summary>The name shown until the document has loaded (the account's name).</summary>
    public string? Name { get; init; }

    /// <summary>The DN only (directory values are never logged).</summary>
    public override string ToString() => Kind.ToString();
}

/// <summary>
/// The entry points of the event log documents (track E): the property sheets (the "Konto" tab's lock state, a computer's
/// "Stan" page), the account review and the documents themselves ask the connection window's shell to open a document
/// through <see cref="Requested"/>. One per connection window (scoped); without a listening shell nothing opens.
/// </summary>
public sealed class EventLogLauncher
{
    /// <summary>A document was requested (the shell of this connection window opens it).</summary>
    public event EventHandler<EventLogDocumentRequest>? Requested;

    /// <summary>A shell listens: the entry points are offered.</summary>
    public bool IsAvailable => Requested is not null;

    /// <summary>Asks the shell to open <paramref name="request"/>. False when no shell listens.</summary>
    public bool Open(EventLogDocumentRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (Requested is not { } handler)
        {
            return false;
        }

        handler(this, request);
        return true;
    }

    /// <summary>"Dzienniki zdarzeń" (optionally preset to one computer).</summary>
    public bool OpenReader(string? computerDn = null, string? host = null) =>
        Open(new EventLogDocumentRequest(EventLogDocumentKind.Reader) { ComputerDn = computerDn, Host = host });

    /// <summary>"Skąd blokada konta" of <paramref name="userDn"/>.</summary>
    public bool OpenLockout(string userDn, string? name = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userDn);
        return Open(new EventLogDocumentRequest(EventLogDocumentKind.Lockout) { UserDn = userDn, Name = name });
    }

    /// <summary>"Nieudane logowania".</summary>
    public bool OpenFailedLogons() => Open(new EventLogDocumentRequest(EventLogDocumentKind.FailedLogons));
}
