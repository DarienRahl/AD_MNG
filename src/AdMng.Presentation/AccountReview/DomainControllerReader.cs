using AdMng.Core.Directory;
using AdMng.Core.Model;
using AdMng.Presentation.Connection;
using AdMng.Presentation.Errors;
using AdMng.Presentation.Infrastructure;
using AdMng.Presentation.Lists;

namespace AdMng.Presentation.AccountReview;

/// <summary>What one domain controller returned for <see cref="DomainControllerReader.ReadAsync"/>.</summary>
/// <param name="Server">DNS host name of the DC.</param>
/// <param name="Objects">The objects found, keyed by DnKey (empty when the DC failed).</param>
/// <param name="Error">Localized reason when the DC could not be read, else null.</param>
public sealed record DomainControllerRead(string Server, IReadOnlyDictionary<string, DirectoryObject> Objects, string? Error)
{
    public bool Succeeded => Error is null;

    /// <summary>The object with <paramref name="distinguishedName"/>, or null.</summary>
    public DirectoryObject? Find(string distinguishedName) =>
        Objects.TryGetValue(DnKey.For(distinguishedName), out var obj) ? obj : null;
}

/// <summary>
/// Reads non-replicated values (lastLogon, the lock state of a DC) on every domain controller of the domain. The DC of
/// <c>session</c> is read through it; every other DC through its own short-lived session opened with
/// <see cref="IDirectoryConnector.ConnectToServerAsync"/> (same credential in memory) and closed right after the read -
/// each session stays on one DC (CLAUDE.md rule 4). Values are kept only in the returned result.
/// </summary>
public sealed class DomainControllerReader(IDirectoryConnector? connector, ErrorTranslator errors)
{
    /// <summary>Other DCs read at the same time.</summary>
    public const int MaxParallel = 4;

    private readonly ErrorTranslator _errors = errors ?? throw new ArgumentNullException(nameof(errors));

    /// <summary>Sessions to other DCs can be opened (without a connector only the current DC is read).</summary>
    public bool CanReachOtherDomainControllers => connector is not null;

    /// <summary>
    /// Reads <paramref name="attributes"/> of <paramref name="distinguishedNames"/> on every DC of the domain (the current
    /// one first). A DC that cannot be listed, reached or read is reported with its error; the others still answer.
    /// </summary>
    /// <exception cref="OperationCanceledException">Cancelled.</exception>
    public async Task<IReadOnlyList<DomainControllerRead>> ReadAsync(
        IDirectorySession session,
        IReadOnlyCollection<string> distinguishedNames,
        IReadOnlyList<string> attributes,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(distinguishedNames);
        ArgumentNullException.ThrowIfNull(attributes);
        var current = session.Info.Server;
        IReadOnlyList<string> others = [];
        if (connector is not null)
        {
            try
            {
                var controllers = await DomainControllerCatalog.ReadAsync(session, cancellationToken).ConfigureAwait(false);
                others = [.. controllers
                    .Select(c => c.HostName)
                    .Where(h => !string.IsNullOrWhiteSpace(h) && !DomainControllerCatalog.IsSameHost(h, current))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Order(StringComparer.OrdinalIgnoreCase)];
            }
            catch (Exception ex) when (ex is DirectoryException or InvalidOperationException or ObjectDisposedException)
            {
                // No list of DCs (configuration not readable): the current DC still answers.
                others = [];
            }
        }

        var results = new DomainControllerRead[others.Count + 1];
        results[0] = await ReadOneAsync(current, () => Task.FromResult(session), dispose: false).ConfigureAwait(false);
        using var gate = new SemaphoreSlim(MaxParallel);
        await Task.WhenAll(others.Select(async (host, index) =>
        {
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                results[index + 1] = await ReadOneAsync(host, () => connector!.ConnectToServerAsync(session, host, cancellationToken), dispose: true).ConfigureAwait(false);
            }
            finally
            {
                gate.Release();
            }
        })).ConfigureAwait(false);
        return results;

        async Task<DomainControllerRead> ReadOneAsync(string server, Func<Task<IDirectorySession>> open, bool dispose)
        {
            IDirectorySession? opened = null;
            try
            {
                opened = await open().ConfigureAwait(false);
                var objects = await DirectoryLookup.ReadManyAsync(opened, distinguishedNames, attributes, cancellationToken).ConfigureAwait(false);
                return new DomainControllerRead(server, objects, null);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                return new DomainControllerRead(server, new Dictionary<string, DirectoryObject>(), _errors.Translate(ex));
            }
            finally
            {
                if (dispose && opened is not null)
                {
                    await opened.DisposeAsync().ConfigureAwait(false);
                }
            }
        }
    }
}
