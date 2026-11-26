using AdMng.Core.GroupPolicy;

namespace AdMng.Core.Tests.GroupPolicy.Search;

/// <summary>
/// Wraps a SYSVOL connection and records how many calls ran at the same time (each call yields and waits
/// <see cref="Delay"/>, so that parallel callers really overlap). Test helper of the K7 folder reader and index tests.
/// </summary>
internal sealed class CountingSysvolConnection(ISysvolWriteConnection inner) : ISysvolWriteConnection
{
    private int _inFlight;
    private int _max;
    private int _calls;

    public TimeSpan Delay { get; init; } = TimeSpan.FromMilliseconds(2);

    public int MaxConcurrent => Volatile.Read(ref _max);

    public int CallCount => Volatile.Read(ref _calls);

    public string Server => inner.Server;

    public Task<SysvolFileResult> ReadGpoFileAsync(Guid gpoId, string relativePath, long maxBytes, CancellationToken cancellationToken) =>
        RunAsync(() => inner.ReadGpoFileAsync(gpoId, relativePath, maxBytes, cancellationToken));

    public Task<IReadOnlyList<SysvolEntry>?> ListPolicyDefinitionsAsync(int maxEntries, CancellationToken cancellationToken) =>
        RunAsync(() => inner.ListPolicyDefinitionsAsync(maxEntries, cancellationToken));

    public Task<SysvolFileResult> ReadPolicyDefinitionsFileAsync(string relativePath, long maxBytes, CancellationToken cancellationToken) =>
        RunAsync(() => inner.ReadPolicyDefinitionsFileAsync(relativePath, maxBytes, cancellationToken));

    public Task<IReadOnlyList<SysvolEntry>> ListGpoFolderAsync(Guid gpoId, SysvolListOptions options, CancellationToken cancellationToken) =>
        RunAsync(() => inner.ListGpoFolderAsync(gpoId, options, cancellationToken));

    public Task<bool> GpoFolderExistsAsync(Guid gpoId, CancellationToken cancellationToken) =>
        RunAsync(() => inner.GpoFolderExistsAsync(gpoId, cancellationToken));

    public Task<byte[]?> GetGpoFolderSecurityAsync(Guid gpoId, CancellationToken cancellationToken) =>
        RunAsync(() => inner.GetGpoFolderSecurityAsync(gpoId, cancellationToken));

    public Task CreateGpoFolderAsync(Guid gpoId, CancellationToken cancellationToken) => throw new NotSupportedException();

    public Task CreateFolderAsync(Guid gpoId, string relativeFolder, CancellationToken cancellationToken) => throw new NotSupportedException();

    public Task WriteTempFileAsync(Guid gpoId, string tempRelativePath, ReadOnlyMemory<byte> content, CancellationToken cancellationToken) => throw new NotSupportedException();

    public Task ReplaceAsync(Guid gpoId, string tempRelativePath, string targetRelativePath, CancellationToken cancellationToken) => throw new NotSupportedException();

    public Task DeleteFileAsync(Guid gpoId, string relativePath, CancellationToken cancellationToken) => throw new NotSupportedException();

    public Task DeleteEmptyFolderAsync(Guid gpoId, string relativeFolder, CancellationToken cancellationToken) => throw new NotSupportedException();

    public Task DeleteGpoFolderAsync(Guid gpoId, CancellationToken cancellationToken) => throw new NotSupportedException();

    public Task SetGpoFolderSecurityAsync(Guid gpoId, byte[] descriptor, CancellationToken cancellationToken) => throw new NotSupportedException();

    public Task WritePolicyDefinitionsTempFileAsync(string tempRelativePath, ReadOnlyMemory<byte> content, CancellationToken cancellationToken) => throw new NotSupportedException();

    public Task ReplacePolicyDefinitionsFileAsync(string tempRelativePath, string targetRelativePath, CancellationToken cancellationToken) => throw new NotSupportedException();

    public Task DeletePolicyDefinitionsFileAsync(string relativePath, CancellationToken cancellationToken) => throw new NotSupportedException();

    public Task DeleteEmptyPolicyDefinitionsFolderAsync(string relativeFolder, CancellationToken cancellationToken) => throw new NotSupportedException();

    public ValueTask DisposeAsync() => inner.DisposeAsync();

    private async Task<T> RunAsync<T>(Func<Task<T>> call)
    {
        var now = Interlocked.Increment(ref _inFlight);
        Interlocked.Increment(ref _calls);
        int seen;
        while (now > (seen = Volatile.Read(ref _max)) && Interlocked.CompareExchange(ref _max, now, seen) != seen)
        {
        }

        try
        {
            await Task.Delay(Delay).ConfigureAwait(false);
            return await call().ConfigureAwait(false);
        }
        finally
        {
            Interlocked.Decrement(ref _inFlight);
        }
    }
}
