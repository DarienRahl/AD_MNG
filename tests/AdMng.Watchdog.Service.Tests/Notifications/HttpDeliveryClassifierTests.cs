using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Authentication;
using AdMng.Watchdog.Contracts.Api;
using AdMng.Watchdog.Core.Model;
using AdMng.Watchdog.Service.Notifications;

namespace AdMng.Watchdog.Service.Tests.Notifications;

/// <summary>The HTTP classification table of spec 15.3.</summary>
public sealed class HttpDeliveryClassifierTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 14, 8, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(DeliveryChannelType.Teams, 200, DeliveryOutcome.Delivered)]
    [InlineData(DeliveryChannelType.Teams, 202, DeliveryOutcome.Delivered)]
    [InlineData(DeliveryChannelType.Teams, 301, DeliveryOutcome.ChannelBroken)]
    [InlineData(DeliveryChannelType.Teams, 400, DeliveryOutcome.ChannelBroken)]
    [InlineData(DeliveryChannelType.Teams, 401, DeliveryOutcome.ChannelBroken)]
    [InlineData(DeliveryChannelType.Teams, 403, DeliveryOutcome.ChannelBroken)]
    [InlineData(DeliveryChannelType.Teams, 404, DeliveryOutcome.ChannelBroken)]
    [InlineData(DeliveryChannelType.Teams, 410, DeliveryOutcome.ChannelBroken)]
    [InlineData(DeliveryChannelType.Teams, 413, DeliveryOutcome.Permanent)]
    [InlineData(DeliveryChannelType.Teams, 429, DeliveryOutcome.Transient)]
    [InlineData(DeliveryChannelType.Teams, 504, DeliveryOutcome.Transient)]
    [InlineData(DeliveryChannelType.Webhook, 204, DeliveryOutcome.Delivered)]
    [InlineData(DeliveryChannelType.Webhook, 302, DeliveryOutcome.Permanent)]
    [InlineData(DeliveryChannelType.Webhook, 400, DeliveryOutcome.Permanent)]
    [InlineData(DeliveryChannelType.Webhook, 401, DeliveryOutcome.AuthenticationFailed)]
    [InlineData(DeliveryChannelType.Webhook, 403, DeliveryOutcome.AuthenticationFailed)]
    [InlineData(DeliveryChannelType.Webhook, 404, DeliveryOutcome.ChannelBroken)]
    [InlineData(DeliveryChannelType.Webhook, 408, DeliveryOutcome.Transient)]
    [InlineData(DeliveryChannelType.Webhook, 410, DeliveryOutcome.ChannelBroken)]
    [InlineData(DeliveryChannelType.Webhook, 422, DeliveryOutcome.Permanent)]
    [InlineData(DeliveryChannelType.Webhook, 500, DeliveryOutcome.Transient)]
    public void Statuses(DeliveryChannelType channel, int status, DeliveryOutcome expected)
    {
        var result = HttpDeliveryClassifier.Classify(channel, status, headers: null, bodyPrefix: null, Now);

        Assert.Equal(expected, result.Result.Outcome);
        if (expected != DeliveryOutcome.Delivered)
        {
            Assert.Equal($"http.{status}", result.Result.ErrorCode);
        }
    }

    [Fact]
    public void TeamsAccepted_AlwaysHintsThatTheCardMayNotBePosted()
    {
        var result = HttpDeliveryClassifier.Classify(DeliveryChannelType.Teams, 202, null, null, Now);

        Assert.Equal([DeliveryHints.AcceptedNotDelivered], result.Hints);
        Assert.Null(result.Result.Detail);
    }

    [Fact]
    public void WebhookGone_HasItsHint()
    {
        Assert.Equal([DeliveryHints.WebhookGone], HttpDeliveryClassifier.Classify(DeliveryChannelType.Webhook, 410, null, null, Now).Hints);
    }

    [Theory]
    [InlineData("The request must be authenticated only by Shared Access scheme.")]
    [InlineData("The request must be authenticated only by OAuth authorization scheme.")]
    public void TeamsAuthMismatchTexts_GetTheHint(string body)
    {
        var result = HttpDeliveryClassifier.Classify(DeliveryChannelType.Teams, 401, null, body, Now);

        Assert.Equal(DeliveryHints.TeamsAuthMismatch, result.Result.Detail);
    }

    [Fact]
    public void WorkflowTriggerIsNotEnabled_BreaksTheChannel_EvenOnA2xxOr5xx()
    {
        var body = "{\"error\":{\"code\":\"WorkflowTriggerIsNotEnabled\"}}";

        Assert.Equal(DeliveryOutcome.ChannelBroken, HttpDeliveryClassifier.Classify(DeliveryChannelType.Teams, 400, null, body, Now).Result.Outcome);
        Assert.Equal(DeliveryOutcome.ChannelBroken, HttpDeliveryClassifier.Classify(DeliveryChannelType.Teams, 503, null, body, Now).Result.Outcome);
        Assert.Equal(DeliveryOutcome.Transient, HttpDeliveryClassifier.Classify(DeliveryChannelType.Webhook, 503, null, body, Now).Result.Outcome);
    }

    [Fact]
    public void RetryAfter_DeltaSeconds()
    {
        var headers = Headers(h => h.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(42)));

        var result = HttpDeliveryClassifier.Classify(DeliveryChannelType.Webhook, 429, headers, null, Now);

        Assert.Equal(TimeSpan.FromSeconds(42), result.Result.RetryAfter);
    }

    [Fact]
    public void RetryAfter_HttpDate()
    {
        var headers = Headers(h => h.RetryAfter = new RetryConditionHeaderValue(Now.AddMinutes(5)));

        Assert.Equal(TimeSpan.FromMinutes(5), HttpDeliveryClassifier.Classify(DeliveryChannelType.Teams, 503, headers, null, Now).Result.RetryAfter);
        Assert.Equal(TimeSpan.Zero, HttpDeliveryClassifier.RetryAfter(headers, Now.AddHours(1)));
    }

    [Fact]
    public void Failures_TimeoutTlsSocketAndOther()
    {
        Assert.Equal("timeout", HttpDeliveryClassifier.Classify(new TaskCanceledException(), timedOut: true).Result.ErrorCode);

        var tls = HttpDeliveryClassifier.Classify(new HttpRequestException("ssl", new AuthenticationException("untrusted")), timedOut: false);
        Assert.Equal(DeliveryOutcome.ChannelBroken, tls.Result.Outcome);
        Assert.Equal("tls", tls.Result.ErrorCode);
        Assert.Equal(DeliveryHints.CertificateUntrusted, tls.Result.Detail);

        var socket = HttpDeliveryClassifier.Classify(new HttpRequestException("refused", new SocketException((int)SocketError.ConnectionRefused)), timedOut: false);
        Assert.Equal(DeliveryOutcome.Transient, socket.Result.Outcome);
        Assert.Equal("socket.connectionrefused", socket.Result.ErrorCode);

        var other = HttpDeliveryClassifier.Classify(new HttpRequestException(HttpRequestError.ResponseEnded), timedOut: false);
        Assert.Equal("http.responseended", other.Result.ErrorCode);
    }

    private static HttpResponseHeaders Headers(Action<HttpResponseHeaders> configure)
    {
        using var response = new HttpResponseMessage(HttpStatusCode.OK);
        configure(response.Headers);
        return response.Headers;
    }
}
