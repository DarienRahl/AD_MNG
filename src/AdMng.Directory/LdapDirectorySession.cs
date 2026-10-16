using System.Collections.Concurrent;
using System.DirectoryServices.Protocols;
using System.Runtime.CompilerServices;
using System.Text;
using AdMng.Core.ActiveDirectory;
using AdMng.Core.Directory;
using AdMng.Core.Ldap;
using AdMng.Core.Model;
using AdMng.Core.Schema;
using Microsoft.Extensions.Logging;
using DirectoryException = AdMng.Core.Directory.DirectoryException;
using SdsSearchScope = System.DirectoryServices.Protocols.SearchScope;
using SdsSortKey = System.DirectoryServices.Protocols.SortKey;
using SearchScope = AdMng.Core.Directory.SearchScope;

namespace AdMng.Directory;

/// <summary>
/// <see cref="IDirectorySession"/> over System.DirectoryServices.Protocols. Every request goes to the DC the
/// session is pinned to; results are converted to in-memory <see cref="DirectoryObject"/> snapshots and never
/// cached. Thread-safe; requests run concurrently over a small connection pool.
/// </summary>
internal sealed class LdapDirectorySession : IDirectorySession
{
    private const string AllObjectsFilter = "(objectClass=*)";
    private const string NoAttributes = "1.1";

    private static readonly string[] EffectivePermissionAttributes =
        [Attr.AllowedAttributesEffective, Attr.AllowedChildClassesEffective, Attr.SdRightsEffective];

    private readonly LdapConnectionPool _pool;
    private readonly ILogger _logger;
    private readonly TimeProvider _timeProvider;
    private readonly bool _channelEncrypted;
    private readonly ConcurrentDictionary<ChangeNotificationWatcher, byte> _watchers = new();
    private int _disposed;

    public LdapDirectorySession(
        SessionInfo info,
        LdapConnectionPool pool,
        bool channelEncrypted,
        ILogger logger,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(info);
        ArgumentNullException.ThrowIfNull(pool);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(timeProvider);
        Info = info;
        _pool = pool;
        _channelEncrypted = channelEncrypted;
        _logger = logger;
        _timeProvider = timeProvider;
    }

    public SessionInfo Info { get; private set; }

    public ISchemaCatalog Schema { get; private set; } = SchemaCatalog.Empty;

    /// <summary>True when the traffic is encrypted (TLS or Negotiate sealing) - password writes allowed.</summary>
    internal bool IsChannelEncrypted => _channelEncrypted;

    internal LdapConnectionPool Pool => _pool;

    private TimeSpan RequestTimeout => _pool.Factory.Settings.Options.RequestTimeout;

    /// <summary>
    /// Attempts for a request that keeps hitting dead connections: after a DC restart every pooled connection is
    /// dead, each failed attempt drops one, and the last attempt runs on a freshly opened connection.
    /// </summary>
    private int MaxConnectionAttempts => _pool.MaxConnections + 1;

    private TimeSpan AbandonTimeout => RequestTimeout < TimeSpan.FromSeconds(15) ? RequestTimeout : TimeSpan.FromSeconds(15);

    /// <summary>Completes the session set-up (called by the connector before the session is handed out).</summary>
    internal void Initialize(SessionInfo info, ISchemaCatalog schema)
    {
        ArgumentNullException.ThrowIfNull(info);
        ArgumentNullException.ThrowIfNull(schema);
        Info = info;
        Schema = schema;
    }

    public IAsyncEnumerable<DirectoryObject> SearchAsync(DirectorySearch search, CancellationToken cancellationToken)
    {
        ValidateSearch(search);
        ThrowIfDisposed();
        return SearchCoreAsync(search, cancellationToken);
    }

    public async Task<VirtualListPage> GetVirtualListPageAsync(DirectorySearch search, VirtualListRequest window, CancellationToken cancellationToken)
    {
        ValidateSearch(search);
        ArgumentNullException.ThrowIfNull(window);
        if (search.Sort is null)
        {
            throw new ArgumentException("A virtual list view requires a sort key.", nameof(search));
        }

        ArgumentOutOfRangeException.ThrowIfNegative(window.Offset, nameof(window));
        ArgumentOutOfRangeException.ThrowIfLessThan(window.Count, 1, nameof(window));
        ArgumentOutOfRangeException.ThrowIfNegative(window.EstimatedCount, nameof(window));

        var request = BuildSearchRequest(search, sortIsCritical: true);
        var vlv = window.SeekValue is { } seek
            ? new VlvRequestControl(0, window.Count - 1, seek)
            : new VlvRequestControl(0, window.Count - 1, window.Offset + 1);
        vlv.EstimateCount = window.EstimatedCount;
        request.Controls.Add(vlv);

        // A server context is only valid on the connection that produced it.
        int? preferredConnection = VlvContext.TryDecode(window.ContextId, _pool.Id, out var contextConnection, out var serverContext)
            ? contextConnection
            : null;
        var usedConnection = 0;
        SearchResponse response;
        try
        {
            response = await ExecuteAsync(
                OperationKind.VirtualList,
                search.BaseDn,
                ExecuteFlags.Idempotent,
                preferredConnection,
                (lease, ct) =>
                {
                    // Also reset on a retry that moved to another connection; an empty context is not sent.
                    usedConnection = lease.ConnectionId;
                    vlv.ContextId = lease.ConnectionId == preferredConnection ? serverContext : [];
                    return lease.SendAsync<SearchResponse>(request, RequestTimeout, ct);
                },
                cancellationToken).ConfigureAwait(false);
        }
        catch (DirectoryException ex) when (ex.LdapResultCode is 60 or 61 or 76)
        {
            // sortControlMissing / offsetRangeError / virtualListViewError: VLV cannot serve this list.
            throw new DirectoryException(
                ex.LdapResultCode == 61 ? DirectoryErrorKind.ConstraintViolation : DirectoryErrorKind.UnsupportedControl,
                ex.Message,
                ex)
            {
                LdapResultCode = ex.LdapResultCode,
                ServerMessage = ex.ServerMessage,
                DistinguishedName = ex.DistinguishedName,
            };
        }

        var vlvResponse = response.Controls.OfType<VlvResponseControl>().FirstOrDefault()
            ?? throw new DirectoryException(
                DirectoryErrorKind.UnsupportedControl,
                "The server did not return a virtual list view response.")
            {
                DistinguishedName = search.BaseDn,
            };

        if (vlvResponse.Result != ResultCode.Success)
        {
            throw new DirectoryException(
                DirectoryErrorKind.UnsupportedControl,
                LdapErrors.Describe(OperationKind.VirtualList, (int)vlvResponse.Result, null, DirectoryErrorKind.UnsupportedControl))
            {
                LdapResultCode = (int)vlvResponse.Result,
                DistinguishedName = search.BaseDn,
            };
        }

        var schema = Schema;
        var items = new List<DirectoryObject>(response.Entries.Count);
        foreach (SearchResultEntry entry in response.Entries)
        {
            items.Add(EntryReader.ToDirectoryObject(entry, schema, search.ExtendedDn));
        }

        var offset = Math.Max(0, vlvResponse.TargetPosition - 1);
        var contextId = vlvResponse.ContextId is { Length: > 0 } id ? VlvContext.Encode(_pool.Id, usedConnection, id) : null;
        return new VirtualListPage(items, offset, vlvResponse.ContentCount, contextId);
    }

    public async Task<DirectoryObject?> GetAsync(string distinguishedName, IReadOnlyList<string> attributes, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(distinguishedName);
        ArgumentNullException.ThrowIfNull(attributes);
        var request = new SearchRequest(distinguishedName, AllObjectsFilter, SdsSearchScope.Base, AttributeList(attributes));
        if (attributes.Contains(Attr.NtSecurityDescriptor, StringComparer.OrdinalIgnoreCase))
        {
            request.Controls.Add(SecurityDescriptorFlags());
        }

        var response = await ExecuteAsync(
            OperationKind.Read,
            distinguishedName,
            ExecuteFlags.Idempotent | ExecuteFlags.NotFoundIsExpected,
            (lease, ct) => lease.SendAsync<SearchResponse>(request, RequestTimeout, ct),
            cancellationToken).ConfigureAwait(false);
        return response is { Entries.Count: > 0 } ? EntryReader.ToDirectoryObject(response.Entries[0], Schema) : null;
    }

    public IAsyncEnumerable<string> GetAllValuesAsync(string distinguishedName, string attribute, CancellationToken cancellationToken) =>
        GetAllValuesAsync(distinguishedName, attribute, chunkSize: null, AttributeReadOptions.Default, cancellationToken);

    /// <summary>Ranged retrieval with options: the Extended DN control (<see cref="AttributeReadOptions.ExtendedDn"/>)
    /// and the Show Deleted control (<see cref="AttributeReadOptions.IncludeDeleted"/>) are sent with every ranged
    /// request.</summary>
    public IAsyncEnumerable<string> GetAllValuesAsync(
        string distinguishedName,
        string attribute,
        AttributeReadOptions options,
        CancellationToken cancellationToken) =>
        GetAllValuesAsync(distinguishedName, attribute, chunkSize: null, options, cancellationToken);

    /// <summary>Ranged retrieval with an explicit chunk size (null = as many values as the server returns).</summary>
    internal IAsyncEnumerable<string> GetAllValuesAsync(string distinguishedName, string attribute, int? chunkSize, CancellationToken cancellationToken) =>
        GetAllValuesAsync(distinguishedName, attribute, chunkSize, AttributeReadOptions.Default, cancellationToken);

    /// <summary>Ranged retrieval with an explicit chunk size (null = as many values as the server returns) and options.</summary>
    internal IAsyncEnumerable<string> GetAllValuesAsync(
        string distinguishedName,
        string attribute,
        int? chunkSize,
        AttributeReadOptions options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(distinguishedName);
        ArgumentException.ThrowIfNullOrWhiteSpace(attribute);
        ArgumentNullException.ThrowIfNull(options);
        if (chunkSize is { } size)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(size, 1, nameof(chunkSize));
        }

        ThrowIfDisposed();
        var semicolon = attribute.IndexOf(';', StringComparison.Ordinal);
        var baseName = semicolon < 0 ? attribute : attribute[..semicolon];
        return GetAllValuesCoreAsync(distinguishedName, baseName, chunkSize, options, cancellationToken);
    }

    public async Task<EffectivePermissions> GetEffectivePermissionsAsync(string distinguishedName, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(distinguishedName);
        var request = new SearchRequest(distinguishedName, AllObjectsFilter, SdsSearchScope.Base, EffectivePermissionAttributes);
        var response = await ExecuteAsync(
            OperationKind.EffectivePermissions,
            distinguishedName,
            ExecuteFlags.Idempotent,
            (lease, ct) => lease.SendAsync<SearchResponse>(request, RequestTimeout, ct),
            cancellationToken).ConfigureAwait(false);
        if (response.Entries.Count == 0)
        {
            throw new DirectoryException(DirectoryErrorKind.NoSuchObject, "EffectivePermissions failed: the object was not returned.")
            {
                DistinguishedName = distinguishedName,
            };
        }

        var entry = EntryReader.ToDirectoryObject(response.Entries[0], Schema);
        return new EffectivePermissions
        {
            WritableAttributes = new HashSet<string>(entry.GetStrings(Attr.AllowedAttributesEffective), StringComparer.OrdinalIgnoreCase),
            CreatableChildClasses = new HashSet<string>(entry.GetStrings(Attr.AllowedChildClassesEffective), StringComparer.OrdinalIgnoreCase),
            SecurityDescriptorRights = (SecurityDescriptorRights)(entry.GetInt32(Attr.SdRightsEffective) ?? 0),
            IsKnown = true,
        };
    }

    public async Task ModifyAsync(
        string distinguishedName,
        IReadOnlyList<AttributeModification> modifications,
        ModifyOptions? options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(distinguishedName);
        ArgumentNullException.ThrowIfNull(modifications);
        if (modifications.Count == 0)
        {
            throw new ArgumentException("At least one modification is required.", nameof(modifications));
        }

        if (modifications.Any(m => IsPasswordAttribute(m.Attribute)))
        {
            EnsureEncryptedChannel(OperationKind.Modify, distinguishedName);
        }

        var request = new ModifyRequest(distinguishedName);
        foreach (var modification in modifications)
        {
            request.Modifications.Add(ToDirectoryModification(modification));
        }

        var permissive = options?.Permissive == true;
        if (permissive)
        {
            request.Controls.Add(new PermissiveModifyControl());
        }

        // Replace (and permissive add/delete) can be repeated safely after a lost connection.
        var idempotent = permissive || modifications.All(m => m.Operation == ModificationOperation.Replace);
        await ExecuteAsync(
            OperationKind.Modify,
            distinguishedName,
            idempotent ? ExecuteFlags.Idempotent : ExecuteFlags.None,
            (lease, ct) => lease.SendAsync<ModifyResponse>(request, RequestTimeout, ct),
            cancellationToken).ConfigureAwait(false);
    }

    public async Task AddAsync(string distinguishedName, IReadOnlyDictionary<string, IReadOnlyList<object>> attributes, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(distinguishedName);
        ArgumentNullException.ThrowIfNull(attributes);
        var request = new AddRequest(distinguishedName);
        foreach (var (name, values) in attributes)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(name, nameof(attributes));
            if (values is null || values.Count == 0)
            {
                // No value = attribute not set; an empty attribute is a protocol error in an add request.
                continue;
            }

            if (IsPasswordAttribute(name))
            {
                EnsureEncryptedChannel(OperationKind.Add, distinguishedName);
            }

            var attribute = new DirectoryAttribute { Name = name };
            foreach (var value in values)
            {
                AddValue(attribute, value, name);
            }

            request.Attributes.Add(attribute);
        }

        if (request.Attributes.Count == 0)
        {
            throw new ArgumentException("At least one attribute (objectClass) is required.", nameof(attributes));
        }

        await ExecuteAsync(
            OperationKind.Add,
            distinguishedName,
            ExecuteFlags.None,
            (lease, ct) => lease.SendAsync<AddResponse>(request, RequestTimeout, ct),
            cancellationToken).ConfigureAwait(false);
    }

    public async Task DeleteAsync(string distinguishedName, bool subtree, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(distinguishedName);
        var request = new DeleteRequest(distinguishedName);
        if (subtree)
        {
            request.Controls.Add(new TreeDeleteControl());
        }

        await ExecuteAsync(
            OperationKind.Delete,
            distinguishedName,
            ExecuteFlags.None,
            (lease, ct) => lease.SendAsync<DeleteResponse>(request, RequestTimeout, ct),
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<string> MoveAsync(string distinguishedName, string? newParentDn, string? newRdn, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(distinguishedName);
        if (newParentDn is null && newRdn is null)
        {
            throw new ArgumentException("Either a new parent or a new RDN is required.", nameof(newParentDn));
        }

        // TryParse, not Parse: messages of argument errors must not quote DNs (they may end up in logs).
        if (!DistinguishedName.TryParse(distinguishedName, out var current) || current.IsRoot)
        {
            throw new ArgumentException("The object to move must be given by a valid, non-empty DN.", nameof(distinguishedName));
        }

        Rdn leaf;
        if (newRdn is not null)
        {
            if (!DistinguishedName.TryParse(newRdn, out var parsed) || parsed.Rdns.Count != 1)
            {
                throw new ArgumentException("The new RDN must consist of exactly one RDN, e.g. CN=Name.", nameof(newRdn));
            }

            leaf = parsed.Leaf;
        }
        else
        {
            leaf = current.Leaf;
        }

        DistinguishedName parent;
        if (newParentDn is null)
        {
            parent = current.Parent!;
        }
        else if (!DistinguishedName.TryParse(newParentDn, out parent) || parent.IsRoot)
        {
            throw new ArgumentException("The new parent must be a valid, non-empty DN.", nameof(newParentDn));
        }

        var newDn = parent.Child(leaf.Type, leaf.Value).ToString();
        var request = new ModifyDNRequest(distinguishedName, newParentDn, leaf.ToString())
        {
            DeleteOldRdn = true,
        };

        await ExecuteAsync(
            OperationKind.Move,
            distinguishedName,
            ExecuteFlags.None,
            (lease, ct) => lease.SendAsync<ModifyDNResponse>(request, RequestTimeout, ct),
            cancellationToken).ConfigureAwait(false);
        return newDn;
    }

    public async Task ResetPasswordAsync(string distinguishedName, string newPassword, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(distinguishedName);
        ArgumentNullException.ThrowIfNull(newPassword);
        EnsureEncryptedChannel(OperationKind.ResetPassword, distinguishedName);

        var encoded = AdConvert.EncodeUnicodePwd(newPassword);
        try
        {
            var request = new ModifyRequest(distinguishedName, DirectoryAttributeOperation.Replace, Attr.UnicodePwd, encoded);
            await ExecuteAsync(
                OperationKind.ResetPassword,
                distinguishedName,
                ExecuteFlags.None,
                (lease, ct) => lease.SendAsync<ModifyResponse>(request, RequestTimeout, ct),
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            Array.Clear(encoded);
        }
    }

    public async Task RestoreDeletedAsync(string deletedObjectDn, string targetDn, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deletedObjectDn);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetDn);
        var request = new ModifyRequest(deletedObjectDn);
        request.Modifications.Add(new DirectoryAttributeModification
        {
            Name = Attr.IsDeleted,
            Operation = DirectoryAttributeOperation.Delete,
        });
        var newDn = new DirectoryAttributeModification
        {
            Name = Attr.DistinguishedName,
            Operation = DirectoryAttributeOperation.Replace,
        };
        newDn.Add(targetDn);
        request.Modifications.Add(newDn);
        request.Controls.Add(new ShowDeletedControl());

        await ExecuteAsync(
            OperationKind.Restore,
            deletedObjectDn,
            ExecuteFlags.None,
            (lease, ct) => lease.SendAsync<ModifyResponse>(request, RequestTimeout, ct),
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<IAsyncDisposable?> WatchAsync(string containerDn, Action<string> onChanged, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(containerDn);
        ArgumentNullException.ThrowIfNull(onChanged);
        ThrowIfDisposed();
        if (!Info.Supports(LdapControls.Notification))
        {
            Log.WatchNotSupported(_logger);
            return null;
        }

        LdapConnection connection;
        try
        {
            // Notifications occupy a connection for their lifetime - never one of the pooled ones.
            connection = await _pool.Factory.OpenAsync(probeReachability: false, cancellationToken).ConfigureAwait(false);
        }
        catch (DirectoryException ex)
        {
            LogFailure(OperationKind.Watch, containerDn, ex);
            throw;
        }

        ChangeNotificationWatcher watcher;
        try
        {
            watcher = ChangeNotificationWatcher.Start(connection, containerDn, onChanged, _logger);
        }
        catch (Exception ex) when (LdapErrors.Translate(ex, OperationKind.Watch, containerDn) is { } error)
        {
            LogFailure(OperationKind.Watch, containerDn, error);
            throw error;
        }

        _watchers.TryAdd(watcher, 0);
        if (Volatile.Read(ref _disposed) == 1 && _watchers.TryRemove(watcher, out _))
        {
            await watcher.DisposeAsync().ConfigureAwait(false);
            throw new ObjectDisposedException(nameof(LdapDirectorySession));
        }

        return new WatchSubscription(this, watcher);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }

        foreach (var watcher in _watchers.Keys)
        {
            if (_watchers.TryRemove(watcher, out _))
            {
                await watcher.DisposeAsync().ConfigureAwait(false);
            }
        }

        await _pool.DisposeAsync().ConfigureAwait(false);
        _pool.Factory.Settings.ClearCredential();
    }

    /// <summary>"Who am I?" extended operation (RFC 4532), e.g. <c>u:CONTOSO\jan.kowalski</c>.</summary>
    internal async Task<string?> WhoAmIAsync(CancellationToken cancellationToken)
    {
        var request = new ExtendedRequest(RootDse.WhoAmIOid);
        var response = await ExecuteAsync(
            OperationKind.WhoAmI,
            null,
            ExecuteFlags.Idempotent,
            (lease, ct) => lease.SendAsync<ExtendedResponse>(request, RequestTimeout, ct),
            cancellationToken).ConfigureAwait(false);
        return response.ResponseValue is { Length: > 0 } value ? Encoding.UTF8.GetString(value) : null;
    }

    private static void ValidateSearch(DirectorySearch search)
    {
        ArgumentNullException.ThrowIfNull(search);
        ArgumentNullException.ThrowIfNull(search.BaseDn, nameof(search));
        ArgumentException.ThrowIfNullOrWhiteSpace(search.Filter, nameof(search));
        ArgumentNullException.ThrowIfNull(search.Attributes, nameof(search));
        if (search.PageSize < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(search), search.PageSize, "The page size must be at least 1.");
        }

        if (search.SizeLimit < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(search), search.SizeLimit, "The size limit cannot be negative.");
        }

        if (search.AttributeScopedQuery is not null && search.Scope != SearchScope.Base)
        {
            throw new ArgumentException("An attribute scoped query requires the base scope.", nameof(search));
        }
    }

    private static string[] AttributeList(IReadOnlyList<string> attributes) =>
        attributes.Count == 0 ? [NoAttributes] : [.. attributes];

    private static SecurityDescriptorFlagControl SecurityDescriptorFlags() =>
        new(SecurityMasks.Owner | SecurityMasks.Group | SecurityMasks.Dacl);

    private static bool IsPasswordAttribute(string attribute) =>
        string.Equals(attribute, Attr.UnicodePwd, StringComparison.OrdinalIgnoreCase)
        || string.Equals(attribute, "userPassword", StringComparison.OrdinalIgnoreCase);

    private static DirectoryAttributeModification ToDirectoryModification(AttributeModification modification)
    {
        ArgumentNullException.ThrowIfNull(modification);
        ArgumentException.ThrowIfNullOrWhiteSpace(modification.Attribute, nameof(modification));
        ArgumentNullException.ThrowIfNull(modification.Values, nameof(modification));
        if (modification.Operation == ModificationOperation.Add && modification.Values.Count == 0)
        {
            throw new ArgumentException($"Adding to '{modification.Attribute}' requires at least one value.", nameof(modification));
        }

        var result = new DirectoryAttributeModification
        {
            Name = modification.Attribute,
            Operation = modification.Operation switch
            {
                ModificationOperation.Add => DirectoryAttributeOperation.Add,
                ModificationOperation.Delete => DirectoryAttributeOperation.Delete,
                ModificationOperation.Replace => DirectoryAttributeOperation.Replace,
                _ => throw new ArgumentOutOfRangeException(nameof(modification), modification.Operation, "Unknown operation."),
            },
        };

        foreach (var value in modification.Values)
        {
            AddValue(result, value, modification.Attribute);
        }

        return result;
    }

    private static void AddValue(DirectoryAttribute attribute, object value, string name)
    {
        switch (value)
        {
            case string text:
                attribute.Add(text);
                break;
            case byte[] bytes:
                attribute.Add(bytes);
                break;
            default:
                throw new ArgumentException(
                    $"Values of '{name}' must be string or byte[], not {value?.GetType().Name ?? "null"}.",
                    nameof(value));
        }
    }

    private async IAsyncEnumerable<DirectoryObject> SearchCoreAsync(
        DirectorySearch search,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var started = _timeProvider.GetTimestamp();
        if (_logger.IsEnabled(LogLevel.Trace))
        {
            Log.SearchFilter(_logger, Log.MaskFilter(search.Filter));
        }

        var request = BuildSearchRequest(search, sortIsCritical: false);
        PageResultRequestControl? paging = null;
        if (Info.Supports(LdapControls.PagedResults))
        {
            paging = new PageResultRequestControl(PageSizeFor(search, 0));
            request.Controls.Add(paging);
        }
        else if (search.SizeLimit > 0)
        {
            request.SizeLimit = search.SizeLimit;
        }

        var schema = Schema;
        var returned = 0;
        var pages = 0;
        byte[]? openCookie = null;
        var outcome = SearchOutcome.Abandoned;
        var kind = paging is null ? LeaseKind.Request : LeaseKind.PagedSearch;
        using var holder = new LeaseHolder(
            await AcquireAsync(OperationKind.Search, search.BaseDn, kind, preferredConnection: null, cancellationToken).ConfigureAwait(false),
            kind);
        try
        {
            while (true)
            {
                if (paging is not null)
                {
                    paging.PageSize = PageSizeFor(search, returned);
                }

                var response = await SendSearchPageAsync(request, holder, search.BaseDn, allowRetry: pages == 0, cancellationToken)
                    .ConfigureAwait(false);
                pages++;
                var cookie = paging is null ? null : response.Controls.OfType<PageResultResponseControl>().FirstOrDefault()?.Cookie;
                openCookie = cookie is { Length: > 0 } ? cookie : null;
                Log.SearchPageReceived(_logger, pages, response.Entries.Count);

                var limitReached = false;
                foreach (SearchResultEntry entry in response.Entries)
                {
                    returned++;
                    yield return EntryReader.ToDirectoryObject(entry, schema, search.ExtendedDn);
                    if (search.SizeLimit > 0 && returned >= search.SizeLimit)
                    {
                        limitReached = true;
                        break;
                    }
                }

                if (limitReached || openCookie is null)
                {
                    break;
                }

                paging!.Cookie = openCookie;
            }

            outcome = SearchOutcome.Completed;
        }
        finally
        {
            if (openCookie is not null && paging is not null)
            {
                // The abandon request takes over the lease, so the result set slot stays accounted for until
                // the server released it.
                _ = AbandonPagedSearchAsync(holder.Detach(), request, paging, openCookie);
            }

            var elapsed = (long)_timeProvider.GetElapsedTime(started).TotalMilliseconds;
            if (outcome == SearchOutcome.Abandoned && cancellationToken.IsCancellationRequested)
            {
                Log.OperationCancelled(_logger, OperationKind.Search, elapsed);
            }
            else if (!holder.Failed)
            {
                Log.SearchCompleted(_logger, search.Scope, returned, pages, elapsed);
                Log.SearchCompletedBelow(_logger, search.BaseDn, search.Scope, returned, pages, elapsed);
            }
        }
    }

    private async IAsyncEnumerable<string> GetAllValuesCoreAsync(
        string distinguishedName,
        string attribute,
        int? chunkSize,
        AttributeReadOptions options,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var start = 0;
        while (true)
        {
            var request = new SearchRequest(
                distinguishedName,
                AllObjectsFilter,
                SdsSearchScope.Base,
                AttributeRange.Request(attribute, start, chunkSize));
            if (options.IncludeDeleted)
            {
                request.Controls.Add(new ShowDeletedControl());
            }

            if (options.ExtendedDn)
            {
                request.Controls.Add(new ExtendedDNControl(ExtendedDNFlag.StandardString));
            }

            var response = await ExecuteAsync(
                OperationKind.RangedRead,
                distinguishedName,
                ExecuteFlags.Idempotent,
                (lease, ct) => lease.SendAsync<SearchResponse>(request, RequestTimeout, ct),
                cancellationToken).ConfigureAwait(false);
            if (response.Entries.Count == 0)
            {
                yield break;
            }

            DirectoryAttribute? values = null;
            AttributeRange? range = null;
            foreach (DirectoryAttribute candidate in response.Entries[0].Attributes.Values)
            {
                if (AttributeRange.TryParse(candidate.Name, out var parsed)
                    && string.Equals(parsed.AttributeName, attribute, StringComparison.OrdinalIgnoreCase))
                {
                    values = candidate;
                    range = parsed;
                    break;
                }

                if (string.Equals(candidate.Name, attribute, StringComparison.OrdinalIgnoreCase))
                {
                    // The server ignored the range option and returned every value.
                    values = candidate;
                }
            }

            if (values is null)
            {
                yield break;
            }

            foreach (var value in EntryReader.Strings(values))
            {
                yield return value;
            }

            if (range is not { End: { } end } || end < start)
            {
                yield break;
            }

            start = end + 1;
        }
    }

    private SearchRequest BuildSearchRequest(DirectorySearch search, bool sortIsCritical)
    {
        var request = new SearchRequest(search.BaseDn, search.Filter, ToSearchScope(search.Scope), AttributeList(search.Attributes))
        {
            Aliases = DereferenceAlias.Never,
            TimeLimit = RequestTimeout,
        };

        if (search.IncludeRecycled)
        {
            request.Controls.Add(new ShowDeletedControl());
            request.Controls.Add(new DirectoryControl(LdapControls.ShowRecycled, null, true, true));
        }
        else if (search.IncludeDeleted)
        {
            request.Controls.Add(new ShowDeletedControl());
        }

        if (search.AttributeScopedQuery is { } asq)
        {
            request.Controls.Add(new AsqRequestControl(asq));
        }

        if (search.ExtendedDn)
        {
            // String form (flag 1): <GUID=…>;<SID=…>;dn. The entry DN comes back extended too and is stripped by
            // EntryReader; DN-syntax attribute values keep the extended form (parsed with ExtendedDistinguishedName).
            request.Controls.Add(new ExtendedDNControl(ExtendedDNFlag.StandardString));
        }

        if (search.ReadSecurityDescriptor)
        {
            request.Controls.Add(SecurityDescriptorFlags());
        }

        if (search.Sort is { } sort)
        {
            request.Controls.Add(new SortRequestControl(new SdsSortKey(sort.Attribute, null, sort.Descending))
            {
                IsCritical = sortIsCritical,
            });
        }

        if (search.Scope == SearchScope.Subtree && !Info.IsGlobalCatalog && Info.Supports(LdapControls.DomainScope))
        {
            // No continuation references to other naming contexts - they are never chased anyway.
            request.Controls.Add(new DomainScopeControl());
        }

        return request;
    }

    private static int PageSizeFor(DirectorySearch search, int returned) =>
        search.SizeLimit > 0 ? Math.Min(search.PageSize, search.SizeLimit - returned) : search.PageSize;

    private static SdsSearchScope ToSearchScope(SearchScope scope) => scope switch
    {
        SearchScope.Base => SdsSearchScope.Base,
        SearchScope.OneLevel => SdsSearchScope.OneLevel,
        SearchScope.Subtree => SdsSearchScope.Subtree,
        _ => throw new ArgumentOutOfRangeException(nameof(scope), scope, "Unknown search scope."),
    };

    /// <summary>
    /// Tells the server to release a paged result set (RFC 2696: page size 0 with the cookie). Best effort - the
    /// result is irrelevant; the lease is released afterwards.
    /// </summary>
    private async Task AbandonPagedSearchAsync(
        LdapConnectionPool.ConnectionLease? lease,
        SearchRequest request,
        PageResultRequestControl paging,
        byte[] cookie)
    {
        if (lease is null)
        {
            return;
        }

        try
        {
            paging.PageSize = 0;
            paging.Cookie = cookie;
            await lease.SendAsync<SearchResponse>(request, AbandonTimeout, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (LdapErrors.IsClientTimeout(ex))
        {
            // No answer at all: the connection is unusable (and the result set is gone with it).
            lease.MarkBroken("no answer to an abandon request");
        }
        catch (Exception ex) when (ex is DirectoryOperationException or LdapException or RequestNotSentException
            or ObjectDisposedException or InvalidOperationException)
        {
            // The server or the session is gone - nothing left to release.
        }
        finally
        {
            lease.Dispose();
        }
    }

    private async Task<SearchResponse> SendSearchPageAsync(
        SearchRequest request,
        LeaseHolder holder,
        string baseDn,
        bool allowRetry,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return await holder.Lease.SendAsync<SearchResponse>(request, RequestTimeout, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception ex) when (allowRetry && attempt + 1 < MaxConnectionAttempts && LdapErrors.IsConnectionBroken(ex))
            {
                // Paging cookies are bound to the connection, so only the first page can move to a new one.
                holder.Lease.MarkBroken("server down");
                Log.RetryingOnNewConnection(_logger, OperationKind.Search);
                holder.Replace(await AcquireAsync(OperationKind.Search, baseDn, holder.Kind, preferredConnection: null, cancellationToken).ConfigureAwait(false));
            }
            catch (Exception ex) when (TranslateAndLog(OperationKind.Search, baseDn, ex, holder.Lease, out var error))
            {
                holder.Failed = true;
                throw error;
            }
        }
    }

    private Task<T> ExecuteAsync<T>(
        OperationKind operation,
        string? distinguishedName,
        ExecuteFlags flags,
        Func<LdapConnectionPool.ConnectionLease, CancellationToken, Task<T>> action,
        CancellationToken cancellationToken) =>
        ExecuteAsync(operation, distinguishedName, flags, preferredConnection: null, action, cancellationToken);

    /// <summary>
    /// Runs one request on a pooled connection: logging, error translation and - for idempotent requests or
    /// requests that provably were not sent - transparent retries on another connection when one broke.
    /// </summary>
    private async Task<T> ExecuteAsync<T>(
        OperationKind operation,
        string? distinguishedName,
        ExecuteFlags flags,
        int? preferredConnection,
        Func<LdapConnectionPool.ConnectionLease, CancellationToken, Task<T>> action,
        CancellationToken cancellationToken)
    {
        var idempotent = (flags & ExecuteFlags.Idempotent) != 0;
        var notFoundIsExpected = (flags & ExecuteFlags.NotFoundIsExpected) != 0;
        ThrowIfDisposed();
        var started = _timeProvider.GetTimestamp();
        using var holder = new LeaseHolder(
            await AcquireAsync(operation, distinguishedName, LeaseKind.Request, preferredConnection, cancellationToken).ConfigureAwait(false),
            LeaseKind.Request);
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                var result = await action(holder.Lease, cancellationToken).ConfigureAwait(false);
                LogCompleted(operation, distinguishedName, started);
                return result;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                Log.OperationCancelled(_logger, operation, (long)_timeProvider.GetElapsedTime(started).TotalMilliseconds);
                throw;
            }
            catch (Exception ex) when (attempt + 1 < MaxConnectionAttempts
                && LdapErrors.IsConnectionBroken(ex)
                && (idempotent || LdapErrors.WasNotSent(ex)))
            {
                holder.Lease.MarkBroken("server down");
                Log.RetryingOnNewConnection(_logger, operation);
                holder.Replace(await AcquireAsync(operation, distinguishedName, LeaseKind.Request, preferredConnection: null, cancellationToken).ConfigureAwait(false));
            }
            catch (Exception ex) when (notFoundIsExpected && IsNoSuchObject(ex))
            {
                LogCompleted(operation, distinguishedName, started);
                return default!;
            }
            catch (Exception ex) when (TranslateAndLog(operation, distinguishedName, ex, holder.Lease, out var error))
            {
                throw error;
            }
        }
    }

    private async Task<LdapConnectionPool.ConnectionLease> AcquireAsync(
        OperationKind operation,
        string? distinguishedName,
        LeaseKind kind,
        int? preferredConnection,
        CancellationToken cancellationToken)
    {
        try
        {
            return await _pool.AcquireAsync(kind, preferredConnection, cancellationToken).ConfigureAwait(false);
        }
        catch (DirectoryException ex)
        {
            LogFailure(operation, distinguishedName, ex);
            throw;
        }
    }

    private static bool IsNoSuchObject(Exception exception) =>
        exception is DirectoryOperationException { Response.ResultCode: ResultCode.NoSuchObject };

    private bool TranslateAndLog(
        OperationKind operation,
        string? distinguishedName,
        Exception exception,
        LdapConnectionPool.ConnectionLease lease,
        out DirectoryException error)
    {
        if (LdapErrors.IsConnectionBroken(exception))
        {
            lease.MarkBroken("server down");
        }
        else if (LdapErrors.IsClientTimeout(exception))
        {
            // Do not let the next requests queue behind a connection that stopped answering.
            lease.MarkBroken("no answer within the request timeout");
        }

        var translated = LdapErrors.Translate(exception, operation, distinguishedName);
        if (translated is null)
        {
            error = null!;
            return false;
        }

        LogFailure(operation, distinguishedName, translated);
        error = translated;
        return true;
    }

    private void LogCompleted(OperationKind operation, string? distinguishedName, long started)
    {
        var elapsed = (long)_timeProvider.GetElapsedTime(started).TotalMilliseconds;
        Log.OperationCompleted(_logger, operation, elapsed);
        if (distinguishedName is not null)
        {
            Log.OperationCompletedOn(_logger, operation, distinguishedName, elapsed);
        }
    }

    private void LogFailure(OperationKind operation, string? distinguishedName, DirectoryException error)
    {
        Log.OperationFailed(_logger, operation, error.Kind, error.LdapResultCode);
        if (distinguishedName is not null)
        {
            Log.OperationFailedOn(_logger, operation, distinguishedName, error.Kind, error.LdapResultCode);
        }
    }

    private void EnsureEncryptedChannel(OperationKind operation, string distinguishedName)
    {
        if (_channelEncrypted)
        {
            return;
        }

        var error = new DirectoryException(
            DirectoryErrorKind.StrongAuthRequired,
            $"{operation} refused: passwords are only sent over an encrypted connection (TLS or Kerberos sealing).")
        {
            DistinguishedName = distinguishedName,
        };
        LogFailure(operation, distinguishedName, error);
        throw error;
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) == 1, this);

    [Flags]
    private enum ExecuteFlags
    {
        None = 0,

        /// <summary>Safe to repeat on a new connection when the old one broke mid-request.</summary>
        Idempotent = 1,

        /// <summary>noSuchObject is a normal outcome (returns default, not logged as a failure).</summary>
        NotFoundIsExpected = 2,
    }

    private enum SearchOutcome
    {
        Abandoned,
        Completed,
    }

    /// <summary>The current lease of an operation (replaced when the connection broke).</summary>
    private sealed class LeaseHolder(LdapConnectionPool.ConnectionLease lease, LeaseKind kind) : IDisposable
    {
        private LdapConnectionPool.ConnectionLease? _lease = lease;

        public LdapConnectionPool.ConnectionLease Lease => _lease ?? throw new ObjectDisposedException(nameof(LeaseHolder));

        public LeaseKind Kind { get; } = kind;

        /// <summary>A failure was already logged.</summary>
        public bool Failed { get; set; }

        public void Replace(LdapConnectionPool.ConnectionLease next)
        {
            _lease?.Dispose();
            _lease = next;
        }

        /// <summary>Hands the lease to another owner (it is no longer disposed by this holder).</summary>
        public LdapConnectionPool.ConnectionLease? Detach()
        {
            var lease = _lease;
            _lease = null;
            return lease;
        }

        public void Dispose() => _lease?.Dispose();
    }

    /// <summary>Handle returned by <see cref="WatchAsync"/>; disposing stops the notifications.</summary>
    private sealed class WatchSubscription(LdapDirectorySession session, ChangeNotificationWatcher watcher) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            if (session._watchers.TryRemove(watcher, out _))
            {
                await watcher.DisposeAsync().ConfigureAwait(false);
            }
        }
    }
}
