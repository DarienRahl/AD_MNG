using System.DirectoryServices.Protocols;

namespace AdMng.Directory;

/// <summary>A request in flight.</summary>
/// <param name="Response">What the caller awaits; cancelled as soon as the caller's token is cancelled.</param>
/// <param name="Completion">
/// Completes when the connection is free again (the request finished or was abandoned). Can outlive
/// <paramref name="Response"/> on platforms where a request cannot be interrupted.
/// </param>
internal readonly record struct PendingResponse<TResponse>(Task<TResponse> Response, Task Completion)
    where TResponse : DirectoryResponse;

/// <summary>Task based sending of LDAP requests with real cancellation.</summary>
/// <remarks>
/// <para>
/// Windows: the APM API of <see cref="LdapConnection"/> (WinLDAP collects responses in the background);
/// cancellation sends an LDAP abandon via <see cref="LdapConnection.Abort"/>.
/// </para>
/// <para>
/// Other platforms: S.DS.P's asynchronous path polls OpenLDAP's <c>ldap_result</c> with a zero timeout, which
/// reads a single message per poll and backs off up to 100 ms between polls - a page of 500 entries would take
/// tens of seconds. Searches therefore use the blocking <see cref="LdapConnection.SendRequest(DirectoryRequest, TimeSpan)"/>
/// on a worker thread (OpenLDAP then reads the whole response at socket speed). Such a request cannot be
/// interrupted: cancellation completes <see cref="PendingResponse{TResponse}.Response"/> immediately while the
/// request finishes in the background (bounded by the request and server time limits), signalled by
/// <see cref="PendingResponse{TResponse}.Completion"/>. Single-message operations keep the APM path.
/// </para>
/// </remarks>
internal static class LdapAsync
{
    /// <summary>Sends one request. See <see cref="PendingResponse{TResponse}"/>.</summary>
    /// <remarks>
    /// Exceptions thrown while sending through the APM path are wrapped in <see cref="RequestNotSentException"/>
    /// so callers know the request never reached the server.
    /// </remarks>
    public static PendingResponse<TResponse> Send<TResponse>(
        this LdapConnection connection,
        DirectoryRequest request,
        TimeSpan timeout,
        CancellationToken cancellationToken)
        where TResponse : DirectoryResponse
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(request);
        if (cancellationToken.IsCancellationRequested)
        {
            var cancelled = Task.FromCanceled<TResponse>(cancellationToken);
            return new PendingResponse<TResponse>(cancelled, Task.CompletedTask);
        }

        return request is SearchRequest && !OperatingSystem.IsWindows()
            ? SendBlocking<TResponse>(connection, request, timeout, cancellationToken)
            : SendWithAbort<TResponse>(connection, request, timeout, cancellationToken);
    }

    /// <summary>Sends one request on a connection nobody else uses concurrently.</summary>
    public static Task<TResponse> SendAsync<TResponse>(
        this LdapConnection connection,
        DirectoryRequest request,
        TimeSpan timeout,
        CancellationToken cancellationToken)
        where TResponse : DirectoryResponse =>
        connection.Send<TResponse>(request, timeout, cancellationToken).Response;

    private static PendingResponse<TResponse> SendBlocking<TResponse>(
        LdapConnection connection,
        DirectoryRequest request,
        TimeSpan timeout,
        CancellationToken cancellationToken)
        where TResponse : DirectoryResponse
    {
        var work = Task.Factory.StartNew(
            static state =>
            {
                var (c, r, t) = ((LdapConnection, DirectoryRequest, TimeSpan))state!;
                return (TResponse)c.SendRequest(r, t);
            },
            (connection, request, timeout),
            CancellationToken.None,
            TaskCreationOptions.DenyChildAttach,
            TaskScheduler.Default);

        // Observe a late failure of a request the caller stopped waiting for.
        var completion = work.ContinueWith(
            static t => _ = t.Exception,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
        var response = cancellationToken.CanBeCanceled ? work.WaitAsync(cancellationToken) : work;
        return new PendingResponse<TResponse>(response, completion);
    }

    private static PendingResponse<TResponse> SendWithAbort<TResponse>(
        LdapConnection connection,
        DirectoryRequest request,
        TimeSpan timeout,
        CancellationToken cancellationToken)
        where TResponse : DirectoryResponse
    {
        var pending = new PendingRequest<TResponse>(connection, cancellationToken);
        IAsyncResult asyncResult;
        try
        {
            asyncResult = connection.BeginSendRequest(
                request,
                timeout,
                PartialResultProcessing.NoPartialResultSupport,
                static ar => ((PendingRequest<TResponse>)ar.AsyncState!).Complete(ar),
                pending);
        }
        catch (Exception ex) when (ex is LdapException or DirectoryOperationException)
        {
            var failed = Task.FromException<TResponse>(new RequestNotSentException(ex));
            return new PendingResponse<TResponse>(failed, Task.CompletedTask);
        }

        pending.RegisterCancellation(asyncResult);
        return new PendingResponse<TResponse>(pending.Completion, pending.Completion);
    }

    private sealed class PendingRequest<TResponse>
        where TResponse : DirectoryResponse
    {
        private readonly LdapConnection _connection;
        private readonly CancellationToken _cancellationToken;
        private readonly TaskCompletionSource<TResponse> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public PendingRequest(LdapConnection connection, CancellationToken cancellationToken)
        {
            _connection = connection;
            _cancellationToken = cancellationToken;
        }

        public Task<TResponse> Completion => _completion.Task;

        public void RegisterCancellation(IAsyncResult asyncResult)
        {
            if (!_cancellationToken.CanBeCanceled || _completion.Task.IsCompleted)
            {
                return;
            }

            var registration = _cancellationToken.Register(
                static state =>
                {
                    var (self, ar) = ((PendingRequest<TResponse>, IAsyncResult))state!;
                    self.Abort(ar);
                },
                (this, asyncResult));

            // Released when the request completes in any way (also when it completed before this line).
            _completion.Task.ContinueWith(
                static (_, state) => ((CancellationTokenRegistration)state!).Dispose(),
                registration,
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }

        public void Complete(IAsyncResult asyncResult)
        {
            try
            {
                var response = _connection.EndSendRequest(asyncResult);
                _completion.TrySetResult((TResponse)response);
            }
            catch (ArgumentException) when (_cancellationToken.IsCancellationRequested)
            {
                // Abort raced with the completion callback and already removed the request.
                _completion.TrySetCanceled(_cancellationToken);
            }
            catch (Exception ex)
            {
                _completion.TrySetException(ex);
            }
        }

        private void Abort(IAsyncResult asyncResult)
        {
            try
            {
                // Sends LDAP abandon; S.DS.P suppresses the completion callback after this call.
                _connection.Abort(asyncResult);
            }
            catch (Exception ex) when (ex is ArgumentException or ObjectDisposedException or LdapException)
            {
                // Already completed, connection disposed or abandon failed - nothing left to cancel.
            }

            _completion.TrySetCanceled(_cancellationToken);
        }
    }
}
