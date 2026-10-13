using AdMng.Core.Batch;

namespace AdMng.Presentation.Changes;

/// <summary>The equivalent PowerShell of one executed plan.</summary>
/// <param name="Title">Title of the change.</param>
/// <param name="ExecutedAt">When the execution finished.</param>
/// <param name="Server">Domain controller.</param>
/// <param name="ObjectCount">Objects executed.</param>
/// <param name="Script">PowerShell (ActiveDirectory module); passwords are never embedded (Read-Host).</param>
public sealed record PowerShellHistoryEntry(string Title, DateTimeOffset ExecutedAt, string Server, int ObjectCount, string Script);

/// <summary>
/// In-memory history of the equivalent PowerShell of every plan executed through <see cref="IChangeCoordinator"/>
/// in this session (for review, documentation or automation). Never written to disk unless the operator exports
/// or copies a script. Thread-safe.
/// </summary>
public sealed class PowerShellHistory(IPowerShellExporter exporter)
{
    /// <summary>Entries kept (oldest are dropped).</summary>
    public const int Capacity = 200;

    private readonly IPowerShellExporter _exporter = exporter ?? throw new ArgumentNullException(nameof(exporter));
    private readonly List<PowerShellHistoryEntry> _entries = [];
    private readonly Lock _gate = new();

    /// <summary>Raised after an entry was added (on the thread that executed the plan).</summary>
    public event EventHandler<PowerShellHistoryEntry>? EntryAdded;

    /// <summary>Oldest first.</summary>
    public IReadOnlyList<PowerShellHistoryEntry> Entries
    {
        get
        {
            lock (_gate)
            {
                return [.. _entries];
            }
        }
    }

    /// <summary>Records the objects of the result that were executed (succeeded, failed or partially executed).</summary>
    public void Record(BatchResult result, string title)
    {
        ArgumentNullException.ThrowIfNull(result);
        var executed = result.Objects.Where(o => o.Changes.Count > 0).Select(o => o.Plan).ToList();
        if (executed.Count == 0)
        {
            return;
        }

        string script;
        try
        {
            script = _exporter.Export(result.Plan with { Objects = executed }, result.Server);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // The history is a convenience; it must never fail the execution that already happened.
            return;
        }

        var entry = new PowerShellHistoryEntry(title ?? string.Empty, result.FinishedAt, result.Server, executed.Count, script);
        lock (_gate)
        {
            _entries.Add(entry);
            if (_entries.Count > Capacity)
            {
                _entries.RemoveRange(0, _entries.Count - Capacity);
            }
        }

        EntryAdded?.Invoke(this, entry);
    }

    public void Clear()
    {
        lock (_gate)
        {
            _entries.Clear();
        }
    }
}
