namespace AdMng.Core.Audit;

/// <summary>
/// One change performed by the operator in this session. The session log only records what the operator did
/// (who, when, which DC, which object, which attribute, old -> new) - it is not a copy of the directory.
/// Passwords are never recorded.
/// </summary>
public sealed record AuditEntry(
    DateTimeOffset Timestamp,
    string Operator,
    string Server,
    string DistinguishedName,
    string Action,
    string? Attribute,
    string? OldValue,
    string? NewValue,
    bool Succeeded,
    string? Error);

/// <summary>
/// In-memory log of changes made in the current session. Kept only for the lifetime of the application;
/// the operator can export it (CSV) explicitly. Thread-safe.
/// </summary>
public interface IAuditLog
{
    IReadOnlyList<AuditEntry> Entries { get; }

    void Record(AuditEntry entry);

    /// <summary>Raised after an entry was recorded (may be raised on a background thread).</summary>
    event EventHandler<AuditEntry>? EntryRecorded;

    void Clear();
}

/// <summary>Default thread-safe <see cref="IAuditLog"/> implementation.</summary>
public sealed class InMemoryAuditLog : IAuditLog
{
    private readonly List<AuditEntry> _entries = [];
    private readonly Lock _gate = new();

    public IReadOnlyList<AuditEntry> Entries
    {
        get
        {
            lock (_gate)
            {
                return [.. _entries];
            }
        }
    }

    public event EventHandler<AuditEntry>? EntryRecorded;

    public void Record(AuditEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        lock (_gate)
        {
            _entries.Add(entry);
        }

        EntryRecorded?.Invoke(this, entry);
    }

    public void Clear()
    {
        lock (_gate)
        {
            _entries.Clear();
        }
    }
}
