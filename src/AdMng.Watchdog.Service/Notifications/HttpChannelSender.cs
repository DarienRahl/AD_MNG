using System.Globalization;
using System.Net.Http.Headers;
using AdMng.Watchdog.Contracts.Api;
using AdMng.Watchdog.Service.Hosting;

namespace AdMng.Watchdog.Service.Notifications;

/// <summary>
/// Sends one Teams/webhook POST with the delivery <see cref="HttpClient"/> (no redirects, TLS 1.2/1.3) and classifies the
/// answer (<see cref="HttpDeliveryClassifier"/>). The attempt timeout runs on the injected <see cref="TimeProvider"/>.
/// Only <c>scheme://host/path</c> of the URL is ever logged; response bodies are read (first 4 KiB) for error markers
/// only.
/// </summary>
public sealed partial class HttpChannelSender(IHttpClientFactory httpClients, TimeProvider timeProvider, ILogger<HttpChannelSender> logger)
{
    private const int MaxBodyPrefix = 4096;

    /// <summary>The User-Agent of every delivery.</summary>
    public static readonly string UserAgent = $"AdMng-Watchdog/{ServiceVersion.Current}";

    public async Task<ClassifiedDelivery> SendAsync(HttpChannelRequest request, DeliveryStageRecorder? recorder, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        using var timeout = new CancellationTokenSource(request.Timeout, timeProvider);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        using var message = new HttpRequestMessage(HttpMethod.Post, request.Url)
        {
            Version = System.Net.HttpVersion.Version11,
            VersionPolicy = HttpVersionPolicy.RequestVersionOrLower,
            Content = new ByteArrayContent(request.Body),
        };
        message.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(request.ContentType);
        message.Headers.UserAgent.ParseAdd(UserAgent);
        request.Headers?.Invoke(message.Headers);

        var client = httpClients.CreateClient(DeliveryHttpClient.Name);
        recorder?.Begin();
        HttpResponseMessage response;
        try
        {
            response = await client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or IOException)
        {
            var failure = HttpDeliveryClassifier.Classify(ex, timedOut: timeout.IsCancellationRequested);
            recorder?.Failed(DeliveryStage.HttpRequest, failure.Result.ErrorCode);
            recorder?.AddHints(failure.Hints);
            Log.RequestFailed(logger, request.ChannelId, DeliveryHttpClient.ForLog(request.Url), failure.Result.ErrorCode ?? "unknown");
            return failure;
        }

        using (response)
        {
            recorder?.Succeeded(DeliveryStage.HttpRequest);
            var prefix = await ReadPrefixAsync(response, linked.Token).ConfigureAwait(false);
            var status = (int)response.StatusCode;
            var classified = HttpDeliveryClassifier.Classify(request.ChannelType, status, response.Headers, prefix, timeProvider.GetUtcNow());
            var detail = string.Create(CultureInfo.InvariantCulture, $"HTTP {status}");
            if (classified.Result.Outcome == Core.Model.DeliveryOutcome.Delivered)
            {
                recorder?.Succeeded(DeliveryStage.HttpResponse, detail, status);
            }
            else
            {
                recorder?.Failed(DeliveryStage.HttpResponse, detail, status);
            }

            recorder?.AddHints(classified.Hints);
            Log.Response(logger, request.ChannelId, DeliveryHttpClient.ForLog(request.Url), status);
            return classified;
        }
    }

    private static async Task<string?> ReadPrefixAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            var buffer = new byte[MaxBodyPrefix];
            var read = 0;
            int chunk;
            while (read < buffer.Length && (chunk = await stream.ReadAsync(buffer.AsMemory(read), cancellationToken).ConfigureAwait(false)) > 0)
            {
                read += chunk;
            }

            return System.Text.Encoding.UTF8.GetString(buffer, 0, read);
        }
        catch (Exception ex) when (ex is IOException or HttpRequestException or OperationCanceledException)
        {
            return null;   // the status code decides; markers are optional
        }
    }

    private static partial class Log
    {
        [LoggerMessage(EventId = 2210, Level = LogLevel.Information, Message = "Delivery to channel {ChannelId} ({Url}) answered HTTP {Status}.")]
        public static partial void Response(ILogger logger, string channelId, string url, int status);

        [LoggerMessage(EventId = 2211, Level = LogLevel.Information, Message = "Delivery to channel {ChannelId} ({Url}) failed: {ErrorCode}.")]
        public static partial void RequestFailed(ILogger logger, string channelId, string url, string errorCode);
    }
}
