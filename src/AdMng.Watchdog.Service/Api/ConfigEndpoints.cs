using System.Globalization;
using AdMng.Watchdog.Contracts;
using AdMng.Watchdog.Contracts.Api;
using AdMng.Watchdog.Contracts.Configuration;
using AdMng.Watchdog.Core.Abstractions;
using AdMng.Watchdog.Core.Configuration;
using AdMng.Watchdog.Service.Auth;
using AdMng.Watchdog.Service.Hosting;
using Microsoft.Extensions.Options;

namespace AdMng.Watchdog.Service.Api;

/// <summary>
/// The runtime configuration (spec 17.2): GET (Admin full with secrets as <c>IsSet</c>/<c>Hint</c>, Viewer redacted by
/// <see cref="ConfigRedactor"/>; <c>ETag: "&lt;version&gt;"</c>), PUT (Admin, <c>If-Match</c>; the 6-step algorithm),
/// defaults, validate (no side effects), audit.
/// </summary>
public static class ConfigEndpoints
{
    public static void Map(IEndpointRouteBuilder api)
    {
        ArgumentNullException.ThrowIfNull(api);
        api.MapGet(WatchdogRoutes.Config, Get);
        api.MapPut(WatchdogRoutes.Config, PutAsync).RequireAuthorization(WatchdogPolicies.Admin).AddEndpointFilter<ClientHeaderFilter>();
        api.MapGet(WatchdogRoutes.ConfigDefaults, (IDefaultConfigurationFactory defaults) => ApiResults.Json(defaults.Create()));
        api.MapPost(WatchdogRoutes.ConfigValidate, ValidateAsync).RequireAuthorization(WatchdogPolicies.Admin).AddEndpointFilter<ClientHeaderFilter>();
        api.MapGet(WatchdogRoutes.ConfigAudit, AuditAsync).RequireAuthorization(WatchdogPolicies.Admin);
    }

    /// <summary>The ETag of a configuration version.</summary>
    public static string ETag(long version) => string.Create(CultureInfo.InvariantCulture, $"\"{version}\"");

    private static IResult Get(HttpContext context, IWatchdogConfigProvider configuration, ConfigRedactor redactor, IOptionsMonitor<ServiceOptions> options)
    {
        var envelope = configuration.Current;
        var body = CallerIdentity.IsAdmin(context.User, options.CurrentValue) ? envelope : redactor.Redact(envelope);
        context.Response.Headers.ETag = ETag(envelope.Version);
        return ApiResults.Json(body);
    }

    private static async Task<IResult> PutAsync(
        HttpContext context,
        IConfigStore store,
        IWatchdogConfigProvider provider,
        IDefaultConfigurationFactory defaults,
        ConfigValidationService validation,
        ConfigChangeAuditor auditor,
        ConfigChangeNotifier notifier,
        IWatchdogSignals signals,
        WatchdogDomain domain,
        CancellationToken cancellationToken)
    {
        var (envelope, error) = await ApiResults.ReadJsonAsync<ConfigEnvelope>(context.Request, allowEmpty: false, cancellationToken).ConfigureAwait(false);
        if (error is not null)
        {
            return error;
        }

        if (envelope!.IsRedacted)
        {
            return ApiResults.Invalid("isRedacted", "redactedConfig", "A redacted configuration cannot be saved.");
        }

        if (!TryExpectedVersion(context.Request, envelope, out var expected))
        {
            return ApiResults.Invalid("If-Match", "invalidVersion", "If-Match must be the quoted configuration version.");
        }

        var incoming = envelope.Configuration;
        var previous = (await store.GetAsync(cancellationToken).ConfigureAwait(false))?.Configuration ?? defaults.Create();
        var outcome = await validation.ValidateAsync(incoming, previous, cancellationToken).ConfigureAwait(false);
        if (!outcome.Result.IsValid)
        {
            return ApiResults.Invalid(outcome.Result.Issues, "The configuration is not valid.");
        }

        var secrets = ConfigSecretsApplier.Apply(incoming, previous, outcome.StoredBindings, outcome.UrlHosts);
        var audit = auditor.Compare(previous, incoming);
        var caller = CallerIdentity.Principal(context.User);
        ConfigEnvelope saved;
        try
        {
            saved = await store.SaveAsync(
                secrets.Configuration,
                expected,
                caller,
                ConfigAuditSummarizer.Summarize(audit, secrets.Changes),
                secrets.Changes,
                cancellationToken).ConfigureAwait(false);
        }
        catch (ConfigVersionConflictException)
        {
            return ApiResults.Problem(StatusCodes.Status412PreconditionFailed, ApiProblem.VersionConflict, "The configuration was changed by someone else; reload it.");
        }

        await notifier.NotifyAsync(
            audit,
            caller,
            saved.Version,
            await domain.GetAsync(cancellationToken).ConfigureAwait(false),
            context.Connection.RemoteIpAddress?.ToString(),
            previous,
            cancellationToken).ConfigureAwait(false);
        var current = await provider.ReloadAsync(cancellationToken).ConfigureAwait(false);
        signals.Raise(WatchdogSignal.ConfigChanged);
        context.Response.Headers.ETag = ETag(saved.Version);
        return ApiResults.Json(current.Version == saved.Version ? current : saved);
    }

    private static async Task<IResult> ValidateAsync(
        HttpContext context,
        IConfigStore store,
        ConfigValidationService validation,
        CancellationToken cancellationToken)
    {
        var (configuration, error) = await ApiResults.ReadJsonAsync<WatchdogConfiguration>(context.Request, allowEmpty: false, cancellationToken).ConfigureAwait(false);
        if (error is not null)
        {
            return error;
        }

        var previous = (await store.GetAsync(cancellationToken).ConfigureAwait(false))?.Configuration;
        var outcome = await validation.ValidateAsync(configuration!, previous, cancellationToken).ConfigureAwait(false);
        return ApiResults.Json(outcome.Result);
    }

    private static async Task<IResult> AuditAsync(HttpContext context, IConfigStore store, CancellationToken cancellationToken)
    {
        long? before = null;
        var limit = 100;
        if (context.Request.Query["before"].ToString() is { Length: > 0 } beforeText)
        {
            if (!long.TryParse(beforeText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
            {
                return ApiResults.Invalid("before", "invalidNumber", "before must be an integer.");
            }

            before = parsed;
        }

        if (context.Request.Query["limit"].ToString() is { Length: > 0 } limitText
            && (!int.TryParse(limitText, NumberStyles.Integer, CultureInfo.InvariantCulture, out limit) || limit is < 1 or > ApiLimits.MaxPageSize))
        {
            return ApiResults.Invalid("limit", "range", "limit must be between 1 and 500.");
        }

        return ApiResults.Json(await store.GetAuditAsync(before, limit, cancellationToken).ConfigureAwait(false));
    }

    private static bool TryExpectedVersion(HttpRequest request, ConfigEnvelope envelope, out long version)
    {
        var ifMatch = request.Headers.IfMatch.ToString();
        if (string.IsNullOrWhiteSpace(ifMatch))
        {
            version = envelope.Version;
            return true;
        }

        var text = ifMatch.Trim();
        if (text.StartsWith("W/", StringComparison.Ordinal))
        {
            text = text[2..];
        }

        return long.TryParse(text.Trim('"'), NumberStyles.None, CultureInfo.InvariantCulture, out version);
    }
}
