namespace AdMng.Watchdog.Core.Notifications.Digest;

/// <summary>One row of a digest section. Rows carry raw stored data; the formatters sanitize and mask it for the
/// recipient's detail level (<c>DigestRowFormatter</c>).</summary>
public abstract record DigestRow
{
    /// <summary>The time the row is sorted and shown by.</summary>
    public abstract DateTimeOffset? Time { get; }

    /// <summary>Events the row stands for (outbox links, "Pokazano X z Y").</summary>
    public virtual IReadOnlyList<long> EventIds => [];
}
