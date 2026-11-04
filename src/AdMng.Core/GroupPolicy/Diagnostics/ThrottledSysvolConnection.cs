namespace AdMng.Core.GroupPolicy.Diagnostics;

/// <summary>
/// A SYSVOL connection that lets at most <c>limit</c> calls run at a time, so that several folders read in parallel (each
/// reading its files in parallel) never put more than that many requests on the DC. It does not own the inner connection:
/// disposing it releases its own gate only.
/// </summary>
internal sealed class ThrottledSysvolConnection(ISysvolWriteConnection inner, int limit) : ISysvolWriteConnection
{
    private readonly SemaphoreSlim _gate = new(limit, limit);

    public string Server => inner.Server;

    public Task<SysvolFileResult> ReadGpoFileAsync(Guid gpoId, string relativePath, long maxBytes, CancellationToken cancellationToken) =>
        RunAsync(() => inner.ReadGpoFileAsync(gpoId, relativePath, maxBytes, cancellationToken), cancellationToken);

    public Task<IReadOnlyList<SysvolEntry>?> ListPolicyDefinitionsAsync(int maxEntries, CancellationToken cancellationToken) =>
        RunAsync(() => inner.ListPolicyDefinitionsAsync(maxEntries, cancellationToken), cancellationToken);

    public Task<SysvolFileResult> ReadPolicyDefinitionsFileAsync(string relativePath, long maxBytes, CancellationToken cancellationToken) =>
        RunAsync(() => inner.ReadPolicyDefinitionsFileAsync(relativePath, maxBytes, cancellationToken), cancellationToken);

    public Task<IReadOnlyList<SysvolEntry>> ListGpoFolderAsync(Guid gpoId, SysvolListOptions options, CancellationToken cancellationToken) =>
        RunAsync(() => inner.ListGpoFolderAsync(gpoId, options, cancellationToken), cancellationToken);

    public Task<bool> GpoFolderExistsAsync(Guid gpoId, CancellationToken cancellationToken) =>
        RunAsync(() => inner.GpoFolderExistsAsync(gpoId, cancellationToken), cancellationToken);

    public Task<byte[]?> GetGpoFolderSecurityAsync(Guid gpoId, CancellationToken cancellationToken) =>
        RunAsync(() => inner.GetGpoFolderSecurityAsync(gpoId, cancellationToken), cancellationToken);

    public Task<byte[]?> GetPoliciesFolderSecurityAsync(CancellationToken cancellationToken) =>
        RunAsync(() => inner.GetPoliciesFolderSecurityAsync(cancellationToken), cancellationToken);

    public Task CreateGpoFolderAsync(Guid gpoId, CancellationToken cancellationToken) =>
        RunAsync(() => inner.CreateGpoFolderAsync(gpoId, cancellationToken), cancellationToken);

    public Task CreateFolderAsync(Guid gpoId, string relativeFolder, CancellationToken cancellationToken) =>
        RunAsync(() => inner.CreateFolderAsync(gpoId, relativeFolder, cancellationToken), cancellationToken);

    public Task WriteTempFileAsync(Guid gpoId, string tempRelativePath, ReadOnlyMemory<byte> content, CancellationToken cancellationToken) =>
        RunAsync(() => inner.WriteTempFileAsync(gpoId, tempRelativePath, content, cancellationToken), cancellationToken);

    public Task ReplaceAsync(Guid gpoId, string tempRelativePath, string targetRelativePath, CancellationToken cancellationToken) =>
        RunAsync(() => inner.ReplaceAsync(gpoId, tempRelativePath, targetRelativePath, cancellationToken), cancellationToken);

    public Task DeleteFileAsync(Guid gpoId, string relativePath, CancellationToken cancellationToken) =>
        RunAsync(() => inner.DeleteFileAsync(gpoId, relativePath, cancellationToken), cancellationToken);

    public Task DeleteEmptyFolderAsync(Guid gpoId, string relativeFolder, CancellationToken cancellationToken) =>
        RunAsync(() => inner.DeleteEmptyFolderAsync(gpoId, relativeFolder, cancellationToken), cancellationToken);

    public Task DeleteGpoFolderAsync(Guid gpoId, CancellationToken cancellationToken) =>
        RunAsync(() => inner.DeleteGpoFolderAsync(gpoId, cancellationToken), cancellationToken);

    public Task SetGpoFolderSecurityAsync(Guid gpoId, byte[] descriptor, CancellationToken cancellationToken) =>
        RunAsync(() => inner.SetGpoFolderSecurityAsync(gpoId, descriptor, cancellationToken), cancellationToken);

    public Task WritePolicyDefinitionsTempFileAsync(string tempRelativePath, ReadOnlyMemory<byte> content, CancellationToken cancellationToken) =>
        RunAsync(() => inner.WritePolicyDefinitionsTempFileAsync(tempRelativePath, content, cancellationToken), cancellationToken);

    public Task ReplacePolicyDefinitionsFileAsync(string tempRelativePath, string targetRelativePath, CancellationToken cancellationToken) =>
        RunAsync(() => inner.ReplacePolicyDefinitionsFileAsync(tempRelativePath, targetRelativePath, cancellationToken), cancellationToken);

    public Task DeletePolicyDefinitionsFileAsync(string relativePath, CancellationToken cancellationToken) =>
        RunAsync(() => inner.DeletePolicyDefinitionsFileAsync(relativePath, cancellationToken), cancellationToken);

    public Task DeleteEmptyPolicyDefinitionsFolderAsync(string relativeFolder, CancellationToken cancellationToken) =>
        RunAsync(() => inner.DeleteEmptyPolicyDefinitionsFolderAsync(relativeFolder, cancellationToken), cancellationToken);

    /// <summary>Releases the gate; the inner connection stays open (its opener disposes it).</summary>
    public ValueTask DisposeAsync()
    {
        _gate.Dispose();
        return ValueTask.CompletedTask;
    }

    private async Task<T> RunAsync<T>(Func<Task<T>> call, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await call().ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task RunAsync(Func<Task> call, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await call().ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }
}
