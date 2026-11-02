using AdMng.Core.GroupPolicy.AdministrativeTemplates;
using AdMng.Presentation.GroupPolicy.AdministrativeTemplates;

namespace AdMng.Presentation.Tests.GroupPolicy;

/// <summary>
/// A store of templates for the tests of <see cref="PolicyDefinitionsService"/> and the editor: the files in memory, listed
/// lengths that can be overridden (a file "too large"), reads that fail once (an SMB failure), and a first listing that is
/// held until the test releases it.
/// </summary>
internal sealed class ScriptedPolicyDefinitionSource(IReadOnlyDictionary<string, byte[]> files) : IPolicyDefinitionSource
{
    private readonly TaskCompletionSource _listEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _listReleased = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _lists;

    public string Description => "Magazyn testowy";

    /// <summary>Listed lengths that differ from the content (path → length).</summary>
    public Dictionary<string, long> Lengths { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Reads of these paths return no content once.</summary>
    public HashSet<string> FailOnce { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The first listing waits for <see cref="ReleaseList"/>.</summary>
    public bool HoldFirstList { get; init; }

    /// <summary>Completes when the first listing started.</summary>
    public Task ListEntered => _listEntered.Task;

    /// <summary>How often the store was listed (a load of the store each).</summary>
    public int ListCount => Volatile.Read(ref _lists);

    public void ReleaseList() => _listReleased.TrySetResult();

    public async Task<IReadOnlyList<PolicyDefinitionFile>?> ListAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.Increment(ref _lists) == 1 && HoldFirstList)
        {
            _listEntered.TrySetResult();
            await _listReleased.Task.WaitAsync(cancellationToken);
        }

        return [.. files.Select(f => new PolicyDefinitionFile(f.Key, Lengths.TryGetValue(f.Key, out var length) ? length : f.Value.Length))];
    }

    public Task<byte[]?> ReadAsync(string relativePath, long maxBytes, CancellationToken cancellationToken)
    {
        lock (FailOnce)
        {
            if (FailOnce.Remove(relativePath))
            {
                return Task.FromResult<byte[]?>(null);
            }
        }

        return Task.FromResult(files.TryGetValue(relativePath, out var content) && content.Length <= maxBytes ? content : null);
    }
}
