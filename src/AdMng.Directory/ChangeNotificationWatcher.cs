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

    /// <summary>How long <see cref="DisposeAsync"/> waits for S.DS.P to release the subscription.</summary>
    private static readonly TimeSpan ReleaseWait = TimeSpan.FromSeconds(2);

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

    /// <remarks>
    /// Never blocks the caller for long: on Linux/macOS <see cref="LdapConnection.Abort"/> waits for S.DS.P's
    /// shared partial-results thread, which can be stuck polling a quiet connection (see
    /// <see cref="OpenLdapTls.TryShutdownSocket"/>). The socket is shut down first to wake it, and the abort runs on
    /// its own thread; no change is delivered after this method returned.
    /// </remarks>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }

        _changes.Writer.TryComplete();
        if (!OperatingSystem.IsWindows())
        {
            // Also ends the server side search: the DC sees the connection go away.
            OpenLdapTls.TryShutdownSocket(_connection);
        }

        var release = Task.Factory.StartNew(
            static state => ((ChangeNotificationWatcher)state!).ReleaseConnection(),
            this,
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);
        await Task.WhenAny(release, Task.Delay(ReleaseWait)).ConfigureAwait(false);
        await _dispatcher.ConfigureAwait(false);
        Log.WatchStopped(_logger);
    }

    private void ReleaseConnection()
    {
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
