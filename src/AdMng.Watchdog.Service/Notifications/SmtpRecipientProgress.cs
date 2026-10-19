namespace AdMng.Watchdog.Service.Notifications;

/// <summary>
/// The recipients an outbox e-mail already reached (spec 15.6): one outbox message can become several SMTP messages
/// (recipient chunks) and a recipient can be rejected temporarily while the others accept – a retry of the outbox message
/// then goes only to the recipients that did not get it yet, so nobody receives a security alert twice. Kept in memory
/// by the singleton transport (the outbox row has no per-recipient state): after a service restart a retry reaches
/// everybody again (a duplicate, never a loss). Entries end when the message is complete and expire after
/// <see cref="Retention"/>; addresses are kept only in memory and never logged.
/// </summary>
internal sealed class SmtpRecipientProgress
{
    /// <summary>How long the progress of an unfinished message is kept (longer than any retry schedule plus a manual
    /// retry of the dead letter).</summary>
    public static readonly TimeSpan Retention = TimeSpan.FromDays(14);

    /// <summary>Most unfinished messages kept (the oldest are forgotten first).</summary>
    public const int MaxMessages = 10_000;

    private readonly Lock _gate = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);

    /// <summary>The addresses (lower case) that already received <paramref name="messageId"/>.</summary>
    public IReadOnlySet<string> Delivered(string messageId)
    {
        ArgumentException.ThrowIfNullOrEmpty(messageId);
        lock (_gate)
        {
            return _entries.TryGetValue(messageId, out var entry) ? new HashSet<string>(entry.Addresses, StringComparer.Ordinal) : [];
        }
    }

    /// <summary>Records that <paramref name="addresses"/> received <paramref name="messageId"/>.</summary>
    public void Add(string messageId, IEnumerable<string> addresses, DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrEmpty(messageId);
        ArgumentNullException.ThrowIfNull(addresses);
        lock (_gate)
        {
            if (!_entries.TryGetValue(messageId, out var entry))
            {
                Prune(now);
                entry = new Entry();
                _entries[messageId] = entry;
            }

            foreach (var address in addresses)
            {
                entry.Addresses.Add(Key(address));
            }

            entry.UpdatedAt = now;
        }
    }

    /// <summary>Forgets a message that needs no further attempt.</summary>
    public void Complete(string messageId)
    {
        ArgumentException.ThrowIfNullOrEmpty(messageId);
        lock (_gate)
        {
            _entries.Remove(messageId);
        }
    }

    /// <summary>The comparison key of an address.</summary>
    public static string Key(string address) => address.Trim().ToLowerInvariant();

    private void Prune(DateTimeOffset now)
    {
        foreach (var (id, _) in _entries.Where(e => now - e.Value.UpdatedAt > Retention).ToList())
        {
            _entries.Remove(id);
        }

        if (_entries.Count >= MaxMessages)
        {
            foreach (var (id, _) in _entries.OrderBy(e => e.Value.UpdatedAt).Take(_entries.Count - MaxMessages + 1).ToList())
            {
                _entries.Remove(id);
            }
        }
    }

    private sealed class Entry
    {
        public HashSet<string> Addresses { get; } = new(StringComparer.Ordinal);

        public DateTimeOffset UpdatedAt { get; set; }
    }
}
