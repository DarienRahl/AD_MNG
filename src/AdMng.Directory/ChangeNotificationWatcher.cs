using System.DirectoryServices.Protocols;
using System.Threading.Channels;
using AdMng.Core.Directory;
using Microsoft.Extensions.Logging;

namespace AdMng.Directory;

/// <summary>
/// One LDAP change notification subscription (LDAP_SERVER_NOTIFICATION_OID) on a dedicated connection.
/// The server keeps the one-level search open and sends an entry for every object created, changed, moved or
/// deleted below the container. Callbacks run sequentially on a background task, never on the S.DS.P
/// results thread.
/// </summary>
internal sealed class ChangeNotificationWatcher : IAsyncDisposable
{
    /// <summary>Partial-results requests time out client side; notifications must stay open indefinitely.</summary>
    private static readonly TimeSpan NotificationTimeout = TimeSpan.FromDays(3650);

    private readonly LdapConnection _connection;
    private readonly Action<string> _onChanged;
    private readonly ILogger _logger;
    private readonly Channel<string> _changes = Channel.CreateUnbounded<string>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });

    private readonly Task _dispatcher;
    private IAsyncResult? _request;
    private int _disposed;

    private ChangeNotificationWatcher(LdapConnection connection, Action<string> onChanged, ILogger logger)
    {
        _connection = connection;
        _onChanged = onChanged;
        _logger = logger;
        _dispatcher = Task.Run(DispatchAsync);
    }

    /// <summary>Starts the notification search. The watcher owns <paramref name="connection"/> from now on.</summary>
    public static ChangeNotificationWatcher Start(LdapConnection connection, string containerDn, Action<string> onChanged, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentException.ThrowIfNullOrWhiteSpace(containerDn);
        ArgumentNullException.ThrowIfNull(onChanged);
        ArgumentNullException.ThrowIfNull(logger);

        // AD requires: filter (objectClass=*), base or one-level scope.
        var request = new SearchRequest(containerDn, "(objectClass=*)", System.DirectoryServices.Protocols.SearchScope.OneLevel, "objectClass");
        request.Controls.Add(new DirectoryNotificationControl());

        var watcher = new ChangeNotificationWatcher(connection, onChanged, logger);
        try
        {
            watcher._request = connection.BeginSendRequest(
                request,
                NotificationTimeout,
                PartialResultProcessing.ReturnPartialResultsAndNotifyCallback,
                watcher.OnResults,
                null);
        }
        catch
        {
            watcher._changes.Writer.TryComplete();
            connection.Dispose();
            throw;
        }

        Log.WatchStarted(logger, containerDn);
        return watcher;
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }

        if (_request is { } request)
        {
            try
            {
                _connection.Abort(request);
            }
            catch (Exception ex) when (ex is ArgumentException or ObjectDisposedException or LdapException)
            {
                // The search already ended.
            }
        }

        _connection.Dispose();
        _changes.Writer.TryComplete();
        await _dispatcher.ConfigureAwait(false);
        Log.WatchStopped(_logger);
    }

    private void OnResults(IAsyncResult asyncResult)
    {
        if (Volatile.Read(ref _disposed) == 1)
        {
            return;
        }

        try
        {
            foreach (var item in _connection.GetPartialResults(asyncResult))
            {
                if (item is SearchResultEntry entry)
                {
                    _changes.Writer.TryWrite(entry.DistinguishedName);
                }
            }

            if (asyncResult.IsCompleted)
            {
                // The server ended the search without an error (e.g. DC shutting down).
                _connection.EndSendRequest(asyncResult);
                Fail(DirectoryErrorKind.ServerUnavailable, null);
            }
        }
        catch (Exception ex) when (Volatile.Read(ref _disposed) == 1 && ex is ObjectDisposedException or ArgumentException)
        {
            // Disposal raced with a delivery.
        }
        catch (Exception ex)
        {
            // Runs on the S.DS.P results thread, which would swallow the exception: end the subscription visibly.
            var error = LdapErrors.Translate(ex, OperationKind.Watch, null);
            Fail(error?.Kind ?? DirectoryErrorKind.Unknown, error?.LdapResultCode);
        }
    }

    private void Fail(DirectoryErrorKind kind, int? resultCode)
    {
        Log.WatchFailed(_logger, kind, resultCode);
        _changes.Writer.TryComplete();
    }

    private async Task DispatchAsync()
    {
        await foreach (var dn in _changes.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            try
            {
                _onChanged(dn);
            }
            catch (Exception ex)
            {
                // A faulty subscriber must not stop the notification stream.
                Log.WatchCallbackFailed(_logger, ex.GetType().Name);
            }
        }
    }
}
