using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using AdMng.Watchdog.Contracts;
using AdMng.Watchdog.Contracts.Api;
using AdMng.Watchdog.Contracts.Configuration;
using AdMng.Watchdog.Contracts.Events;
using Microsoft.Extensions.Logging;

namespace AdMng.Watchdog.Client;

/// <summary>
/// HTTPS + Negotiate implementation of <see cref="IWatchdogClient"/> (spec 5.5): HTTP/1.1 only, the handler of
/// <see cref="WatchdogHttpHandlerFactory"/> (credentials for the configured origin only), a per-call timeout on the
/// <see cref="TimeProvider"/> (<see cref="WatchdogClientOptions.RequestTimeout"/>; long-poll <c>wait + 15 s</c>; test sends
/// their server limit + 15 s), <c>X-AdMng-Watchdog-Api: 1</c> and <c>X-AdMng-Client: 1</c> on every request, contracts
/// through <see cref="WatchdogJson.Options"/>, failures as <see cref="WatchdogClientException"/>
/// (<see cref="WatchdogClientErrors"/>). Logs only method, path and status.
/// </summary>
public sealed partial class WatchdogHttpClient : IWatchdogClient
{
    private static readonly TimeSpan LongPollMargin = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan EmailTestTimeout = TimeSpan.FromSeconds(75);
    private static readonly TimeSpan ChannelTestTimeout = TimeSpan.FromSeconds(60);

    /// <summary>The service waits up to 20 s for the first SMTP attempt of a message to a user.</summary>
    private static readonly TimeSpan UserMessageTimeout = TimeSpan.FromSeconds(45);

    private readonly TimeProvider _timeProvider;
    private readonly ILogger<WatchdogHttpClient> _logger;
    private readonly Lock _gate = new();
    private HttpClient? _client;
    private WatchdogClientOptions? _options;
    private WatchdogCertificateCapture? _capture;

    public WatchdogHttpClient(TimeProvider timeProvider, ILogger<WatchdogHttpClient> logger)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);
        _timeProvider = timeProvider;
        _logger = logger;
    }

    public bool IsConfigured
    {
        get
        {
            lock (_gate)
            {
                return _client is not null;
            }
        }
    }

    public Uri? BaseAddress
    {
        get
        {
            lock (_gate)
            {
                return _options?.BaseAddress;
            }
        }
    }

    public void Configure(WatchdogClientOptions? options)
    {
        HttpClient? client = null;
        WatchdogCertificateCapture? capture = null;
        if (options is not null)
        {
            capture = new WatchdogCertificateCapture();
            var handler = WatchdogHttpHandlerFactory.Create(options, capture);
            var baseAddress = options.BaseAddress.AbsoluteUri.EndsWith('/') ? options.BaseAddress : new Uri(options.BaseAddress.AbsoluteUri + "/");
            client = new HttpClient(handler, disposeHandler: true)
            {
                BaseAddress = baseAddress,
                Timeout = Timeout.InfiniteTimeSpan,
                DefaultRequestVersion = HttpVersion.Version11,
                DefaultVersionPolicy = HttpVersionPolicy.RequestVersionExact,
            };
            client.DefaultRequestHeaders.Add(WatchdogRoutes.ApiVersionHeader, WatchdogRoutes.ApiVersion.ToString(CultureInfo.InvariantCulture));
            client.DefaultRequestHeaders.Add(WatchdogRoutes.ClientHeader, "1");
            client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        }

        HttpClient? previous;
        lock (_gate)
        {
            previous = _client;
            _client = client;
            _options = options;
            _capture = capture;
        }

        previous?.Dispose();
    }

    public Task<MeDto> GetMeAsync(CancellationToken cancellationToken) => GetAsync<MeDto>(WatchdogRoutes.Me, cancellationToken);

    public Task<StatusDto> GetStatusAsync(CancellationToken cancellationToken) => GetAsync<StatusDto>(WatchdogRoutes.Status, cancellationToken);

    public Task<IReadOnlyList<EventKindInfo>> GetCatalogAsync(CancellationToken cancellationToken) =>
        GetAsync<IReadOnlyList<EventKindInfo>>(WatchdogRoutes.Catalog, cancellationToken);

    public async Task<EventPage> GetEventsAsync(EventQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        var path = WithQuery(WatchdogRoutes.Events, query.ToQueryString());
        var timeout = query.After is not null && query.WaitSeconds > 0 ? TimeSpan.FromSeconds(query.WaitSeconds) + LongPollMargin : (TimeSpan?)null;
        return (await SendAsync<EventPage>(HttpMethod.Get, path, body: null, timeout, cancellationToken).ConfigureAwait(false))!;
    }

    public Task<EventDetails> GetEventAsync(long id, CancellationToken cancellationToken) =>
        GetAsync<EventDetails>(WatchdogRoutes.EventByIdPath(id), cancellationToken);

    public Task AcknowledgeAsync(long id, AcknowledgeRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return SendAsync<object>(HttpMethod.Post, WatchdogRoutes.EventAcknowledgePath(id), Json(request), null, cancellationToken);
    }

    public Task AcknowledgeManyAsync(BulkAcknowledgeRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return SendAsync<object>(HttpMethod.Post, WatchdogRoutes.EventsAcknowledge, Json(request), null, cancellationToken);
    }

    public Task<ConfigEnvelope> GetConfigAsync(CancellationToken cancellationToken) => GetAsync<ConfigEnvelope>(WatchdogRoutes.Config, cancellationToken);

    public Task<WatchdogConfiguration> GetDefaultConfigAsync(CancellationToken cancellationToken) =>
        GetAsync<WatchdogConfiguration>(WatchdogRoutes.ConfigDefaults, cancellationToken);

    public async Task<ConfigEnvelope> SaveConfigAsync(ConfigEnvelope envelope, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        var etag = new EntityTagHeaderValue(string.Create(CultureInfo.InvariantCulture, $"\"{envelope.Version}\""));
        return (await SendAsync<ConfigEnvelope>(HttpMethod.Put, WatchdogRoutes.Config, Json(envelope), null, cancellationToken, etag).ConfigureAwait(false))!;
    }

    public async Task<ConfigValidationResult> ValidateConfigAsync(WatchdogConfiguration configuration, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        return (await SendAsync<ConfigValidationResult>(HttpMethod.Post, WatchdogRoutes.ConfigValidate, Json(configuration), null, cancellationToken).ConfigureAwait(false))!;
    }

    public Task<ConfigAuditPage> GetConfigAuditAsync(long? before, int limit, CancellationToken cancellationToken)
    {
        var query = before is { } value
            ? string.Create(CultureInfo.InvariantCulture, $"before={value}&limit={limit}")
            : string.Create(CultureInfo.InvariantCulture, $"limit={limit}");
        return GetAsync<ConfigAuditPage>(WithQuery(WatchdogRoutes.ConfigAudit, query), cancellationToken);
    }

    public async Task<DeliveryTestResult> SendTestEmailAsync(TestEmailRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return (await SendAsync<DeliveryTestResult>(HttpMethod.Post, WatchdogRoutes.TestEmail, Json(request), EmailTestTimeout, cancellationToken).ConfigureAwait(false))!;
    }

    public async Task<DeliveryTestResult> SendTestChannelAsync(string channelId, TestChannelRequest request, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(channelId);
        ArgumentNullException.ThrowIfNull(request);
        return (await SendAsync<DeliveryTestResult>(HttpMethod.Post, WatchdogRoutes.TestChannelPath(channelId), Json(request), ChannelTestTimeout, cancellationToken)
            .ConfigureAwait(false))!;
    }

    public async Task<RulePreviewResult> PreviewRuleAsync(RulePreviewRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return (await SendAsync<RulePreviewResult>(HttpMethod.Post, WatchdogRoutes.RulePreview, Json(request), null, cancellationToken).ConfigureAwait(false))!;
    }

    public Task<DigestPreviewDto> PreviewDigestAsync(CancellationToken cancellationToken) => GetAsync<DigestPreviewDto>(WatchdogRoutes.DigestPreview, cancellationToken);

    public Task SendDigestNowAsync(CancellationToken cancellationToken) =>
        SendAsync<object>(HttpMethod.Post, WatchdogRoutes.DigestSend, body: null, null, cancellationToken);

    public Task<OutboxPage> GetOutboxAsync(OutboxQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        return GetAsync<OutboxPage>(WithQuery(WatchdogRoutes.Outbox, query.ToQueryString()), cancellationToken);
    }

    public Task RetryOutboxAsync(long id, CancellationToken cancellationToken) =>
        SendAsync<object>(HttpMethod.Post, WatchdogRoutes.OutboxRetryPath(id), body: null, null, cancellationToken);

    public Task<UserMessagingStatusDto> GetUserMessagingStatusAsync(CancellationToken cancellationToken) =>
        GetAsync<UserMessagingStatusDto>(WatchdogRoutes.UserMessagesStatus, cancellationToken);

    public async Task<UserMessageResult> SendUserMessageAsync(UserMessageRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return (await SendAsync<UserMessageResult>(HttpMethod.Post, WatchdogRoutes.UserMessages, Json(request), UserMessageTimeout, cancellationToken).ConfigureAwait(false))!;
    }

    public void Dispose()
    {
        HttpClient? client;
        lock (_gate)
        {
            client = _client;
            _client = null;
            _options = null;
        }

        client?.Dispose();
    }

    private static HttpContent Json<T>(T value)
    {
        var content = new ByteArrayContent(JsonSerializer.SerializeToUtf8Bytes(value, WatchdogJson.Options));
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
        return content;
    }

    private static string WithQuery(string path, string query) => string.IsNullOrEmpty(query) ? path : path + "?" + query;

    private async Task<T> GetAsync<T>(string path, CancellationToken cancellationToken) =>
        (await SendAsync<T>(HttpMethod.Get, path, body: null, null, cancellationToken).ConfigureAwait(false))!;

    private async Task<T?> SendAsync<T>(
        HttpMethod method,
        string path,
        HttpContent? body,
        TimeSpan? timeout,
        CancellationToken cancellationToken,
        EntityTagHeaderValue? ifMatch = null)
    {
        HttpClient client;
        TimeSpan requestTimeout;
        WatchdogCertificateCapture? capture;
        string? pin;
        lock (_gate)
        {
            client = _client ?? throw new WatchdogClientException(WatchdogClientErrorKind.NotConfigured, "The watchdog service address is not configured.");
            requestTimeout = timeout ?? _options!.RequestTimeout;
            capture = _capture;
            pin = _options!.PinnedCertificateSha256;
        }

        using var timer = new CancellationTokenSource(requestTimeout, _timeProvider);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timer.Token);
        using var request = new HttpRequestMessage(method, path.TrimStart('/')) { Content = body };
        if (ifMatch is not null)
        {
            request.Headers.IfMatch.Add(ifMatch);
        }

        var route = path.Split('?', 2)[0];
        try
        {
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, linked.Token).ConfigureAwait(false);
            Log.Response(_logger, method.Method, route, (int)response.StatusCode);
            if (await WatchdogClientErrors.FromResponseAsync(response, linked.Token).ConfigureAwait(false) is { } error)
            {
                throw error;
            }

            if (typeof(T) == typeof(object) || response.StatusCode == HttpStatusCode.NoContent)
            {
                return default;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(linked.Token).ConfigureAwait(false);
            return await JsonSerializer.DeserializeAsync<T>(stream, WatchdogJson.Options, linked.Token).ConfigureAwait(false)
                   ?? throw new WatchdogClientException(WatchdogClientErrorKind.ServerError, "The watchdog service sent an empty response.");
        }
        catch (JsonException ex)
        {
            throw new WatchdogClientException(WatchdogClientErrorKind.ApiVersionMismatch, "The response of the watchdog service could not be read.", ex);
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or IOException)
        {
            Log.Failed(_logger, method.Method, route, ex.GetType().Name);
            throw WatchdogClientErrors.FromException(ex, cancellationToken.IsCancellationRequested, capture?.Rejected, pin);
        }
    }

    private static partial class Log
    {
        [LoggerMessage(EventId = 3000, Level = LogLevel.Debug, Message = "Watchdog API {Method} {Path} answered {Status}.")]
        public static partial void Response(ILogger logger, string method, string path, int status);

        [LoggerMessage(EventId = 3001, Level = LogLevel.Information, Message = "Watchdog API {Method} {Path} failed: {ErrorType}.")]
        public static partial void Failed(ILogger logger, string method, string path, string errorType);
    }
}
