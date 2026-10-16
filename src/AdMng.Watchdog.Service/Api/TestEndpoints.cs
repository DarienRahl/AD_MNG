using AdMng.Watchdog.Contracts;
using AdMng.Watchdog.Contracts.Api;
using AdMng.Watchdog.Core.Abstractions;
using AdMng.Watchdog.Service.Auth;
using AdMng.Watchdog.Service.Hosting;
using AdMng.Watchdog.Service.Notifications;

namespace AdMng.Watchdog.Service.Api;

/// <summary>
/// Test sends (spec 17.2): <c>/test/email</c> (≤ 60 s) and <c>/test/channels/{channelId}</c> (≤ 45 s), Admin only, never
/// queued. Unsaved settings may use a stored secret only when its binding matches – otherwise 400
/// <c>secretRebindRequired</c> BEFORE anything is sent (the <see cref="DestinationGuard"/> refuses it again at send
/// time).
/// </summary>
public static class TestEndpoints
{
    public const int MaxTestRecipients = 10;

    public static void Map(IEndpointRouteBuilder api)
    {
        ArgumentNullException.ThrowIfNull(api);
        api.MapPost(WatchdogRoutes.TestEmail, EmailAsync).RequireAuthorization(WatchdogPolicies.Admin).AddEndpointFilter<ClientHeaderFilter>();
        api.MapPost(WatchdogRoutes.TestChannel, ChannelAsync).RequireAuthorization(WatchdogPolicies.Admin).AddEndpointFilter<ClientHeaderFilter>();
    }

    private static async Task<IResult> EmailAsync(
        HttpContext context,
        IDeliveryTester tester,
        IWatchdogConfigProvider configuration,
        ISecretStore secrets,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var (request, error) = await ApiResults.ReadJsonAsync<TestEmailRequest>(context.Request, allowEmpty: false, cancellationToken).ConfigureAwait(false);
        if (error is not null)
        {
            return error;
        }

        if (request!.Recipients.Count is < 1 or > MaxTestRecipients)
        {
            return ApiResults.Invalid("recipients", "range", "Between 1 and 10 test recipients are required.");
        }

        for (var i = 0; i < request.Recipients.Count; i++)
        {
            try
            {
                MimeMessageFactory.ParseMailbox(request.Recipients[i]);
            }
            catch (FormatException)
            {
                return ApiResults.Invalid($"recipients[{i}]", "invalidAddress", "A test recipient is not a valid e-mail address.");
            }
        }

        if (request.Settings is { } settings)
        {
            var stored = await secrets.ListAsync(cancellationToken).ConfigureAwait(false);
            var candidate = configuration.Current.Configuration with { Smtp = settings, Channels = [] };
            var issues = ConfigSecretsApplier.RebindIssues(candidate, stored, new Dictionary<string, string?>());
            if (issues.Count > 0)
            {
                return ApiResults.Invalid(issues, "A stored secret cannot be used for these settings; enter it again.");
            }
        }

        var token = Linked(context, timeProvider, ApiLimits.EmailTestTimeout);
        return await RunAsync(() => tester.TestEmailAsync(request, token), cancellationToken).ConfigureAwait(false);
    }

    private static async Task<IResult> ChannelAsync(
        string channelId,
        HttpContext context,
        IDeliveryTester tester,
        IWatchdogConfigProvider configuration,
        ConfigSecretsApplier applier,
        ISecretStore secrets,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var (request, error) = await ApiResults.ReadJsonAsync<TestChannelRequest>(context.Request, allowEmpty: true, cancellationToken).ConfigureAwait(false);
        if (error is not null)
        {
            return error;
        }

        request ??= new TestChannelRequest();
        var current = configuration.Current.Configuration;
        if (request.Settings is { } settings)
        {
            if (!string.Equals(settings.Id, channelId, StringComparison.OrdinalIgnoreCase))
            {
                return ApiResults.Invalid("settings.id", "idMismatch", "The channel id of the settings differs from the route.");
            }

            var candidate = current with { Channels = [settings] };
            var stored = await secrets.ListAsync(cancellationToken).ConfigureAwait(false);
            var hosts = await applier.ResolveUrlHostsAsync(candidate, cancellationToken).ConfigureAwait(false);
            var issues = ConfigSecretsApplier.RebindIssues(candidate, stored, hosts).Where(i => i.Path.StartsWith("channels", StringComparison.Ordinal)).ToList();
            if (issues.Count > 0)
            {
                return ApiResults.Invalid(issues, "A stored secret cannot be used for these settings; enter it again.");
            }
        }
        else if (!current.Channels.Any(c => c is not null && string.Equals(c.Id, channelId, StringComparison.OrdinalIgnoreCase)))
        {
            return ApiResults.NotFound("The channel does not exist.");
        }

        var token = Linked(context, timeProvider, ApiLimits.ChannelTestTimeout);
        return await RunAsync(() => tester.TestChannelAsync(channelId, request, token), cancellationToken).ConfigureAwait(false);
    }

    private static async Task<IResult> RunAsync(Func<Task<DeliveryTestResult>> test, CancellationToken requestAborted)
    {
        try
        {
            return ApiResults.Json(await test().ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (!requestAborted.IsCancellationRequested)
        {
            return ApiResults.Problem(StatusCodes.Status504GatewayTimeout, ApiProblem.Unavailable, "The test send did not finish in time.");
        }
        catch (KeyNotFoundException)
        {
            return ApiResults.NotFound("The channel does not exist.");
        }
    }

    private static CancellationToken Linked(HttpContext context, TimeProvider timeProvider, TimeSpan timeout)
    {
        var timer = new CancellationTokenSource(timeout, timeProvider);
        var source = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted, timer.Token);
        context.Response.RegisterForDispose(timer);
        context.Response.RegisterForDispose(source);
        return source.Token;
    }
}
