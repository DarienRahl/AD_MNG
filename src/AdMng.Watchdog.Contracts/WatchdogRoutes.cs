namespace AdMng.Watchdog.Contracts;

/// <summary>Route templates, API version and header names shared by the service and its client.</summary>
public static class WatchdogRoutes
{
    public const int ApiVersion = 1;
    public const string ApiVersionHeader = "X-AdMng-Watchdog-Api";   // sent by server on every response, by client on every request
    public const string ClientHeader = "X-AdMng-Client";             // CSRF guard, required on POST/PUT/DELETE, value "1"
    public const string Prefix = "/api/v1";
    public const string Me = Prefix + "/me";                         // GET  Viewer
    public const string Status = Prefix + "/status";                 // GET  Viewer
    public const string Catalog = Prefix + "/catalog";               // GET  Viewer  -> EventKindInfo[]
    public const string Events = Prefix + "/events";                 // GET  Viewer  (EventQuery) -> EventPage
    public const string EventById = Prefix + "/events/{id:long}";    // GET  Viewer  -> EventDetails
    public const string EventAcknowledge = Prefix + "/events/{id:long}/ack"; // POST Viewer -> 204
    public const string EventsAcknowledge = Prefix + "/events/ack";  // POST Viewer (BulkAcknowledgeRequest, ≤ 500 ids) -> 204
    public const string Config = Prefix + "/config";                 // GET Viewer (redacted unless Admin) -> ConfigEnvelope; PUT Admin (If-Match: "version")
    public const string ConfigDefaults = Prefix + "/config/defaults";// GET  Viewer  -> WatchdogConfiguration
    public const string ConfigValidate = Prefix + "/config/validate";// POST Admin   -> ConfigValidationResult
    public const string ConfigAudit = Prefix + "/config/audit";      // GET  Admin   -> ConfigAuditPage
    public const string TestEmail = Prefix + "/test/email";          // POST Admin   -> DeliveryTestResult
    public const string TestChannel = Prefix + "/test/channels/{channelId}"; // POST Admin -> DeliveryTestResult
    public const string RulePreview = Prefix + "/rules/preview";     // POST Admin   -> RulePreviewResult
    public const string DigestPreview = Prefix + "/digest/preview";  // GET  Viewer  -> DigestPreviewDto
    public const string DigestSend = Prefix + "/digest/send";        // POST Admin   -> 202
    public const string Outbox = Prefix + "/outbox";                 // GET  Admin   (OutboxQuery) -> OutboxPage
    public const string OutboxRetry = Prefix + "/outbox/{id:long}/retry"; // POST Admin -> 204
    public const string EventPage = "/events/{eventId:guid}";        // GET  Viewer  -> text/html (links in notifications)
    public const string Health = "/healthz";                         // GET  anonymous: loopback, own addresses, MonitoringSubnets -> "ok"/503

    public static string EventByIdPath(long id) => $"{Prefix}/events/{id}";
    public static string EventAcknowledgePath(long id) => $"{Prefix}/events/{id}/ack";
    public static string TestChannelPath(string channelId) => $"{Prefix}/test/channels/{Uri.EscapeDataString(channelId)}";
    public static string OutboxRetryPath(long id) => $"{Prefix}/outbox/{id}/retry";
    public static string EventPagePath(Guid eventId) => $"/events/{eventId:D}";
}
