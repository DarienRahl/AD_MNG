using AdMng.Watchdog.Client;
using AdMng.Watchdog.Contracts.Api;
using AdMng.Watchdog.Contracts.Configuration;
using AdMng.Watchdog.Contracts.Events;
using AdMng.Watchdog.Core.Configuration;

namespace AdMng.Watchdog.Testing.Client;

/// <summary>
/// In-memory <see cref="IWatchdogClient"/> with the API semantics of the service (filters and paging of the event feed,
/// long-poll with <see cref="EventQuery.WaitSeconds"/> on the injected <see cref="TimeProvider"/>, acknowledgements,
/// optimistic concurrency of the configuration, redacted configuration for Viewers, write-only secrets, role checks,
/// test sends with the destination-binding rule, delivery log and retry; a cancelled call throws a
/// <see cref="WatchdogClientException"/> of kind Cancelled like the real client), pre-filled with the Polish sample data of
/// <see cref="SampleEvents"/>, <see cref="SampleConfiguration"/> and <see cref="SampleStatus"/>. Tests script failures
/// per method (<see cref="FailWith"/>, <see cref="FailNext"/>), hold calls (<see cref="Hold"/>) and await calls
/// (<see cref="Calls"/>). Never touches a network or a disk.
/// </summary>
public sealed class FakeWatchdogClient : IWatchdogClient
{
    /// <summary>The address the sample client is configured with.</summary>
    public static readonly Uri SampleAddress = new("https://admng-wd.contoso.com:8443/");

    private const string AllMethods = "*";

    private readonly Lock _gate = new();
    private readonly TimeProvider _time;
    private readonly ConfigRedactor _redactor = new();
    private readonly List<WatchEvent> _events = [];
    private readonly Dictionary<string, WatchdogClientErrorKind> _failures = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Queue<Exception>> _nextFailures = new(StringComparer.Ordinal);
    private readonly Dictionary<string, TaskCompletionSource> _holds = new(StringComparer.Ordinal);
    private readonly List<EventQuery> _eventQueries = [];
    private readonly List<BulkAcknowledgeRequest> _bulkAcknowledgements = [];
    private readonly List<(long Id, AcknowledgeRequest Request)> _acknowledgements = [];
    private readonly List<ConfigEnvelope> _savedEnvelopes = [];
    private readonly List<TestEmailRequest> _emailTests = [];
    private readonly List<(string ChannelId, TestChannelRequest Request)> _channelTests = [];
    private readonly List<RulePreviewRequest> _rulePreviews = [];
    private readonly List<OutboxQuery> _outboxQueries = [];
    private readonly List<UserMessageRequest> _userMessages = [];
    private TaskCompletionSource _newEvent = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private (Func<EventQuery, bool> Match, TaskCompletionSource Release)? _eventHold;
    private WatchdogClientOptions? _options;
    private ConfigEnvelope _config;
    private long _nextId;

    /// <param name="timeProvider">Clock of the long-poll waits and of acknowledgements (default: system clock).</param>
    /// <param name="sampleData">Fill with the Polish sample data (default) or start empty.</param>
    public FakeWatchdogClient(TimeProvider? timeProvider = null, bool sampleData = true)
    {
        _time = timeProvider ?? TimeProvider.System;
        _options = new WatchdogClientOptions { BaseAddress = SampleAddress };
        _config = new ConfigEnvelope
        {
            Version = 7,
            Configuration = sampleData ? SampleConfiguration.Create() : new WatchdogConfiguration(),
            UpdatedAt = SampleEvents.Now.AddMinutes(-30),
            UpdatedBy = SamplePrincipals.JanKowalski.Account,
        };
        Defaults = SampleConfiguration.CreateDefaults();
        Status = SampleStatus.Create();
        DigestPreview = SampleStatus.Digest();
        if (sampleData)
        {
            _events.AddRange(SampleEvents.Create());
            Outbox.AddRange(SampleStatus.Outbox());
            Audit.AddRange(SampleStatus.Audit());
            foreach (var (id, deliveries) in SampleStatus.Deliveries())
            {
                Deliveries[id] = deliveries;
            }
        }

        _nextId = _events.Count == 0 ? 1 : _events.Max(e => e.Id) + 1;
    }

    /// <summary>Calls by method name (nameof(IWatchdogClient.GetEventsAsync) …); awaitable.</summary>
    public FakeCallRecorder Calls { get; } = new();

    /// <summary>The caller as <c>GET /me</c> returns it (default: <see cref="SampleStatus.Admin"/>).</summary>
    public MeDto Me { get; set; } = SampleStatus.Admin;

    /// <summary>Switches <see cref="Me"/> between the sample Admin and the sample Viewer.</summary>
    public bool IsAdmin
    {
        get => Me.Roles.Contains(WatchdogRole.Admin);
        set => Me = value ? SampleStatus.Admin : SampleStatus.Viewer;
    }

    public StatusDto Status { get; set; }

    public IReadOnlyList<EventKindInfo> Catalog { get; set; } = EventKindCatalog.All;

    /// <summary>Result of <c>GET /config/defaults</c>.</summary>
    public WatchdogConfiguration Defaults { get; set; }

    public DigestPreviewDto DigestPreview { get; set; }

    /// <summary>The delivery log, newest first.</summary>
    public List<OutboxItemDto> Outbox { get; } = [];

    /// <summary>Configuration audit entries, newest first (a successful save adds one).</summary>
    public List<ConfigAuditEntry> Audit { get; } = [];

    /// <summary>Deliveries shown in the event details, by event id.</summary>
    public Dictionary<long, List<EventDeliveryDto>> Deliveries { get; } = [];

    /// <summary>Server-side validation of <c>PUT /config</c> and <c>POST /config/validate</c> (default: no issues).</summary>
    public Func<WatchdogConfiguration, IReadOnlyList<ConfigIssue>>? Validator { get; set; }

    /// <summary>Result of a test e-mail (default: every SMTP stage succeeds).</summary>
    public Func<TestEmailRequest, DeliveryTestResult>? EmailTestHandler { get; set; }

    /// <summary>Result of a channel test (default: Teams 202 with the "accepted, not delivered" hint, webhook 200).</summary>
    public Func<string, TestChannelRequest, DeliveryTestResult>? ChannelTestHandler { get; set; }

    /// <summary>Result of <c>GET /user-messages/status</c> (default: like a fresh service – the SMTP channel
    /// smtp.contoso.com:587 is configured, messages to users are switched off; inline images (U1c) are supported with the
    /// service's limits, see <see cref="WithoutInlineImages"/> for an older service).</summary>
    public UserMessagingStatusDto UserMessagingStatus { get; set; } = new()
    {
        Enabled = false,
        SmtpConfigured = true,
        Channel = "smtp.contoso.com:587",
        Sender = "watchdog@contoso.com",
        MaxMessagesPerHour = 300,
        MaxMessagesPerDay = 2000,
        Capabilities = [UserMessagingCapabilities.InlineImages],
        MaxInlineImages = UserMessageLimits.MaxInlineImages,
        MaxInlineImageBytes = UserMessageLimits.MaxInlineImageBytes,
        MaxInlineImagesTotalBytes = UserMessageLimits.MaxInlineImagesTotalBytes,
        MaxHtmlLength = UserMessageLimits.MaxHtmlLength,
        MaxStoredImageBytes = 256L * 1024 * 1024,
    };

    /// <summary>The status of an older (U1b) service, or one with <c>userMessages.allowInlineImages</c> off: no
    /// capabilities, no image limits, HTML up to 100 000 characters.</summary>
    public static UserMessagingStatusDto WithoutInlineImages(UserMessagingStatusDto status)
    {
        ArgumentNullException.ThrowIfNull(status);
        return status with
        {
            Capabilities = [],
            MaxInlineImages = 0,
            MaxInlineImageBytes = 0,
            MaxInlineImagesTotalBytes = 0,
            MaxHtmlLength = 100_000,
            StoredImageBytes = 0,
            MaxStoredImageBytes = 0,
        };
    }

    /// <summary>How the fake answers a message with images while <see cref="UserMessagingStatus"/> lacks the
    /// <c>inlineImages</c> capability: true (default) = like an older service (400 <c>invalidJson</c>, the member is
    /// unknown), false = like a new service with images switched off (400 <c>imagesDisabled</c>).</summary>
    public bool RejectImagesAsOlderService { get; set; } = true;

    /// <summary>Result of <c>POST /user-messages</c> per request (default: Delivered, 250). A repeated id answers the first
    /// result with <see cref="UserMessageResult.Duplicate"/> like the service.</summary>
    public Func<UserMessageRequest, UserMessageResult>? UserMessageHandler { get; set; }

    /// <summary>Messages received by <see cref="SendUserMessageAsync"/> (repeated ids included).</summary>
    public IReadOnlyList<UserMessageRequest> UserMessages => Snapshot(_userMessages);

    /// <summary>Number of <c>POST /digest/send</c> calls that succeeded.</summary>
    public int DigestSends { get; private set; }

    /// <summary>
    /// A self-signed service certificate (stage B design 18 item 1): while set, every call fails like the real client –
    /// CertificateUntrusted without a pin, CertificateChanged with another pin – unless the configured
    /// <see cref="WatchdogClientOptions.PinnedCertificateSha256"/> is its fingerprint.
    /// </summary>
    public WatchdogServerCertificate? ServerCertificate { get; set; }

    /// <summary>The options of the last <see cref="Configure"/> call (null = not configured).</summary>
    public WatchdogClientOptions? Options
    {
        get
        {
            lock (_gate)
            {
                return _options;
            }
        }
    }

    public bool IsConfigured => Options is not null;

    public Uri? BaseAddress => Options?.BaseAddress;

    /// <summary>The stored configuration as an Admin sees it.</summary>
    public ConfigEnvelope StoredConfig
    {
        get
        {
            lock (_gate)
            {
                return _config;
            }
        }

        set
        {
            ArgumentNullException.ThrowIfNull(value);
            lock (_gate)
            {
                _config = value;
            }
        }
    }

    /// <summary>The events in the store (ascending ids).</summary>
    public IReadOnlyList<WatchEvent> Events
    {
        get
        {
            lock (_gate)
            {
                return [.. _events];
            }
        }
    }

    public IReadOnlyList<EventQuery> EventQueries => Snapshot(_eventQueries);

    public IReadOnlyList<(long Id, AcknowledgeRequest Request)> Acknowledgements => Snapshot(_acknowledgements);

    public IReadOnlyList<BulkAcknowledgeRequest> BulkAcknowledgements => Snapshot(_bulkAcknowledgements);

    /// <summary>Envelopes received by <see cref="SaveConfigAsync"/> (as sent, with NewValue/Clear).</summary>
    public IReadOnlyList<ConfigEnvelope> SavedEnvelopes => Snapshot(_savedEnvelopes);

    public IReadOnlyList<TestEmailRequest> EmailTests => Snapshot(_emailTests);

    public IReadOnlyList<(string ChannelId, TestChannelRequest Request)> ChannelTests => Snapshot(_channelTests);

    public IReadOnlyList<RulePreviewRequest> RulePreviews => Snapshot(_rulePreviews);

    public IReadOnlyList<OutboxQuery> OutboxQueries => Snapshot(_outboxQueries);

    /// <summary>Every following call of <paramref name="method"/> (null = every method) fails with <paramref name="kind"/>.</summary>
    public void FailWith(WatchdogClientErrorKind kind, string? method = null)
    {
        lock (_gate)
        {
            _failures[method ?? AllMethods] = kind;
        }
    }

    /// <summary>The next call of <paramref name="method"/> throws <paramref name="exception"/> (queued, one per call).</summary>
    public void FailNext(string method, Exception exception)
    {
        ArgumentNullException.ThrowIfNull(method);
        ArgumentNullException.ThrowIfNull(exception);
        lock (_gate)
        {
            if (!_nextFailures.TryGetValue(method, out var queue))
            {
                _nextFailures[method] = queue = new Queue<Exception>();
            }

            queue.Enqueue(exception);
        }
    }

    /// <summary>Removes every scripted failure.</summary>
    public void ClearFailures()
    {
        lock (_gate)
        {
            _failures.Clear();
            _nextFailures.Clear();
        }
    }

    /// <summary>Calls of <paramref name="method"/> wait (after being recorded) until <see cref="Release"/>.</summary>
    public void Hold(string method)
    {
        ArgumentNullException.ThrowIfNull(method);
        lock (_gate)
        {
            _holds.TryAdd(method, new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
        }
    }

    /// <summary>Lets the held calls of <paramref name="method"/> continue.</summary>
    public void Release(string method)
    {
        TaskCompletionSource? hold;
        lock (_gate)
        {
            _holds.Remove(method, out hold);
        }

        hold?.TrySetResult();
    }

    /// <summary>
    /// Calls of <see cref="GetEventsAsync"/> whose query matches <paramref name="match"/> wait (after being recorded) until
    /// <see cref="ReleaseEvents"/> – e.g. only the "load older" pages while the live long-poll keeps running.
    /// </summary>
    public void HoldEvents(Func<EventQuery, bool> match)
    {
        ArgumentNullException.ThrowIfNull(match);
        lock (_gate)
        {
            _eventHold = (match, new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
        }
    }

    /// <summary>Lets the calls held by <see cref="HoldEvents"/> continue.</summary>
    public void ReleaseEvents()
    {
        TaskCompletionSource? release;
        lock (_gate)
        {
            release = _eventHold?.Release;
            _eventHold = null;
        }

        release?.TrySetResult();
    }

    /// <summary>Appends an event (id assigned) and wakes long-polls. Returns the stored event.</summary>
    public WatchEvent AddEvent(WatchEvent watchEvent)
    {
        ArgumentNullException.ThrowIfNull(watchEvent);
        TaskCompletionSource signal;
        WatchEvent stored;
        lock (_gate)
        {
            stored = watchEvent with { Id = _nextId++ };
            _events.Add(stored);
            signal = _newEvent;
            _newEvent = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        signal.TrySetResult();
        return stored;
    }

    /// <summary>Changes a stored event in place (late attribution, acknowledgement by someone else).</summary>
    public void UpdateEvent(long id, Func<WatchEvent, WatchEvent> change)
    {
        ArgumentNullException.ThrowIfNull(change);
        lock (_gate)
        {
            var index = _events.FindIndex(e => e.Id == id);
            if (index < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(id), id, "No such event.");
            }

            _events[index] = change(_events[index]);
        }
    }

    public void Configure(WatchdogClientOptions? options)
    {
        Calls.Record(nameof(Configure));
        if (options is not null)
        {
            var address = options.BaseAddress;
            var loopback = address.IsLoopback && options.AllowInsecureLoopback && address.Scheme == Uri.UriSchemeHttp;
            if (!address.IsAbsoluteUri || (address.Scheme != Uri.UriSchemeHttps && !loopback))
            {
                throw new ArgumentException("The watchdog service address must be https://.", nameof(options));
            }
        }

        lock (_gate)
        {
            _options = options;
        }
    }

    public async Task<MeDto> GetMeAsync(CancellationToken cancellationToken)
    {
        await EnterAsync(nameof(GetMeAsync), cancellationToken).ConfigureAwait(false);
        return Me;
    }

    public async Task<StatusDto> GetStatusAsync(CancellationToken cancellationToken)
    {
        await EnterAsync(nameof(GetStatusAsync), cancellationToken).ConfigureAwait(false);
        return Status with { Now = _time.GetUtcNow() };
    }

    public async Task<IReadOnlyList<EventKindInfo>> GetCatalogAsync(CancellationToken cancellationToken)
    {
        await EnterAsync(nameof(GetCatalogAsync), cancellationToken).ConfigureAwait(false);
        return Catalog;
    }

    public async Task<EventPage> GetEventsAsync(EventQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        // The long-poll deadline counts from the arrival of the request (before it is recorded), so a test that advances
        // the clock as soon as it sees the call can never miss the timer.
        var deadline = _time.GetUtcNow() + TimeSpan.FromSeconds(Math.Clamp(query.WaitSeconds, 0, 30));
        lock (_gate)
        {
            _eventQueries.Add(query);
        }

        await EnterAsync(nameof(GetEventsAsync), cancellationToken).ConfigureAwait(false);
        Task? held;
        lock (_gate)
        {
            held = _eventHold is { } hold && hold.Match(query) ? hold.Release.Task : null;
        }

        if (held is not null)
        {
            await WaitHeldAsync(held, cancellationToken).ConfigureAwait(false);
        }

        if (query.After is { } after && query.WaitSeconds > 0)
        {
            await WaitForNewerAsync(after, deadline, cancellationToken).ConfigureAwait(false);
        }

        lock (_gate)
        {
            var limit = Math.Clamp(query.Limit, 1, 500);
            var matching = _events.Where(e => FakeEventFilter.Matches(e, query));
            var ordered = query.After is { } from
                ? matching.Where(e => e.Id > from).OrderBy(e => e.Id)
                : matching.Where(e => query.Before is not { } before || e.Id < before).OrderByDescending(e => e.Id);
            var page = ordered.Take(limit + 1).ToList();
            return new EventPage
            {
                Items = [.. page.Take(limit)],
                HasMore = page.Count > limit,
                LatestId = _events.Count == 0 ? 0 : _events.Max(e => e.Id),
            };
        }
    }

    public async Task<EventDetails> GetEventAsync(long id, CancellationToken cancellationToken)
    {
        await EnterAsync(nameof(GetEventAsync), cancellationToken).ConfigureAwait(false);
        lock (_gate)
        {
            var watchEvent = _events.FirstOrDefault(e => e.Id == id) ?? throw Error(WatchdogClientErrorKind.NotFound, 404, "Event not found.");
            return new EventDetails
            {
                Event = watchEvent,
                RelatedEvents = [.. _events.Where(e => e.Id != id && e.ChangeSetId != Guid.Empty && e.ChangeSetId == watchEvent.ChangeSetId)],
                Deliveries = Deliveries.TryGetValue(id, out var deliveries) ? [.. deliveries] : [],
            };
        }
    }

    public async Task AcknowledgeAsync(long id, AcknowledgeRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        await EnterAsync(nameof(AcknowledgeAsync), cancellationToken).ConfigureAwait(false);
        CheckComment(request.Comment);
        lock (_gate)
        {
            if (_events.FindIndex(e => e.Id == id) < 0)
            {
                throw Error(WatchdogClientErrorKind.NotFound, 404, "Event not found.");
            }

            _acknowledgements.Add((id, request));
            Acknowledge(id, request.Comment);
        }
    }

    public async Task AcknowledgeManyAsync(BulkAcknowledgeRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        await EnterAsync(nameof(AcknowledgeManyAsync), cancellationToken).ConfigureAwait(false);
        CheckComment(request.Comment);
        if (request.Ids.Count is < 1 or > 500)
        {
            throw Validation("range", "ids", "1 to 500 event ids per request.");
        }

        lock (_gate)
        {
            _bulkAcknowledgements.Add(request);
            foreach (var id in request.Ids)
            {
                Acknowledge(id, request.Comment);
            }
        }
    }

    public async Task<ConfigEnvelope> GetConfigAsync(CancellationToken cancellationToken)
    {
        await EnterAsync(nameof(GetConfigAsync), cancellationToken).ConfigureAwait(false);
        var config = StoredConfig;
        return IsAdmin ? config : _redactor.Redact(config);
    }

    public async Task<WatchdogConfiguration> GetDefaultConfigAsync(CancellationToken cancellationToken)
    {
        await EnterAsync(nameof(GetDefaultConfigAsync), cancellationToken).ConfigureAwait(false);
        return Defaults;
    }

    public async Task<ConfigEnvelope> SaveConfigAsync(ConfigEnvelope envelope, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        await EnterAsync(nameof(SaveConfigAsync), cancellationToken).ConfigureAwait(false);
        RequireAdmin();
        lock (_gate)
        {
            _savedEnvelopes.Add(envelope);
        }

        if (envelope.IsRedacted)
        {
            throw Validation("redactedConfig", string.Empty, "A redacted configuration cannot be saved.");
        }

        var issues = Validator?.Invoke(envelope.Configuration) ?? [];
        if (issues.Any(i => i.Severity == IssueSeverity.Error))
        {
            throw new WatchdogClientException(WatchdogClientErrorKind.Validation, "The configuration is not valid.") { StatusCode = 400, Issues = issues };
        }

        lock (_gate)
        {
            if (envelope.Version != _config.Version)
            {
                throw Error(WatchdogClientErrorKind.VersionConflict, 412, "The configuration was changed by someone else.");
            }

            var now = _time.GetUtcNow();
            _config = new ConfigEnvelope
            {
                Version = _config.Version + 1,
                Configuration = FakeSecretApplier.Apply(envelope.Configuration),
                UpdatedAt = now,
                UpdatedBy = Me.Account,
            };
            Audit.Insert(0, new ConfigAuditEntry
            {
                Id = (Audit.Count == 0 ? 0 : Audit.Max(a => a.Id)) + 1,
                At = now,
                BySid = Me.Sid,
                ByAccount = Me.Account,
                Summary = "configuration saved",
                Version = _config.Version,
            });
            return _config;
        }
    }

    public async Task<ConfigValidationResult> ValidateConfigAsync(WatchdogConfiguration configuration, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        await EnterAsync(nameof(ValidateConfigAsync), cancellationToken).ConfigureAwait(false);
        RequireAdmin();
        return new ConfigValidationResult { Issues = Validator?.Invoke(configuration) ?? [] };
    }

    public async Task<ConfigAuditPage> GetConfigAuditAsync(long? before, int limit, CancellationToken cancellationToken)
    {
        await EnterAsync(nameof(GetConfigAuditAsync), cancellationToken).ConfigureAwait(false);
        RequireAdmin();
        lock (_gate)
        {
            var page = Audit.Where(a => before is not { } b || a.Id < b).OrderByDescending(a => a.Id).Take(limit + 1).ToList();
            return new ConfigAuditPage { Items = [.. page.Take(limit)], HasMore = page.Count > limit };
        }
    }

    public async Task<DeliveryTestResult> SendTestEmailAsync(TestEmailRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        await EnterAsync(nameof(SendTestEmailAsync), cancellationToken).ConfigureAwait(false);
        RequireAdmin();
        lock (_gate)
        {
            _emailTests.Add(request);
        }

        var stored = StoredConfig.Configuration.Smtp;
        if (request.Settings is { } settings
            && stored.Password.IsSet
            && settings.Password.NewValue is null
            && !settings.Password.Clear
            && (!string.Equals(settings.Host, stored.Host, StringComparison.OrdinalIgnoreCase) || settings.Port != stored.Port
                || !string.Equals(settings.UserName, stored.UserName, StringComparison.OrdinalIgnoreCase)))
        {
            throw Validation("secretRebindRequired", "smtp.password", "The stored password is bound to another destination.");
        }

        return EmailTestHandler?.Invoke(request) ?? Result(
            true,
            [
                Stage(DeliveryStage.Resolve, "10.10.0.25"),
                Stage(DeliveryStage.Connect, "poczta.contoso.com:587"),
                Stage(DeliveryStage.Tls, "TLS 1.3 TLS_AES_256_GCM_SHA384"),
                Stage(DeliveryStage.Authenticate, "235 2.7.0 Authentication successful"),
                Stage(DeliveryStage.Sender, "250 2.1.0 Sender OK"),
                Stage(DeliveryStage.Recipients, "250 2.1.5 Recipient OK"),
                Stage(DeliveryStage.Data, "250 2.0.0 OK"),
            ],
            []);
    }

    public async Task<DeliveryTestResult> SendTestChannelAsync(string channelId, TestChannelRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(channelId);
        ArgumentNullException.ThrowIfNull(request);
        await EnterAsync(nameof(SendTestChannelAsync), cancellationToken).ConfigureAwait(false);
        RequireAdmin();
        lock (_gate)
        {
            _channelTests.Add((channelId, request));
        }

        var stored = StoredConfig.Configuration.Channels.FirstOrDefault(c => string.Equals(c.Id, channelId, StringComparison.OrdinalIgnoreCase));
        var channel = request.Settings ?? stored ?? throw Error(WatchdogClientErrorKind.NotFound, 404, "Channel not found.");
        if (stored?.Webhook is { SigningSecret.IsSet: true } && channel.Webhook is { } webhook
            && webhook.SigningSecret.NewValue is null && !webhook.SigningSecret.Clear
            && channel.Url.NewValue is { } newUrl
            && !string.Equals(FakeSecretApplier.HostOf(newUrl), FakeSecretApplier.HostOf(stored.Url.Hint), StringComparison.OrdinalIgnoreCase))
        {
            var index = StoredConfig.Configuration.Channels.ToList().IndexOf(stored);
            throw Validation("secretRebindRequired", $"channels[{index}].webhook.signingSecret", "The stored signing secret is bound to another host.");
        }

        if (ChannelTestHandler is { } handler)
        {
            return handler(channelId, request);
        }

        return channel.Type == ChannelType.Teams
            ? Result(
                true,
                [
                    Stage(DeliveryStage.Resolve, "52.112.0.10"),
                    Stage(DeliveryStage.Connect, "default1a2b3c4d.12.environment.api.powerplatform.com:443"),
                    Stage(DeliveryStage.Tls, "TLS 1.2"),
                    Stage(DeliveryStage.HttpRequest, "POST"),
                    Stage(DeliveryStage.HttpResponse, "HTTP 202", 202),
                ],
                [DeliveryHints.AcceptedNotDelivered])
            : Result(
                true,
                [
                    Stage(DeliveryStage.Resolve, "10.10.7.20"),
                    Stage(DeliveryStage.Connect, "siem.contoso.com:443"),
                    Stage(DeliveryStage.Tls, "TLS 1.3"),
                    Stage(DeliveryStage.HttpRequest, "POST"),
                    Stage(DeliveryStage.HttpResponse, "HTTP 200", 200),
                ],
                []);
    }

    public async Task<RulePreviewResult> PreviewRuleAsync(RulePreviewRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        await EnterAsync(nameof(PreviewRuleAsync), cancellationToken).ConfigureAwait(false);
        RequireAdmin();
        lock (_gate)
        {
            _rulePreviews.Add(request);
            var rule = request.Rule;
            var since = _time.GetUtcNow().AddDays(-Math.Clamp(request.Days, 1, 90));
            var matched = _events
                .Where(e => e.DetectedAt >= since && e.Kind != WatchEventKind.Test)
                .Where(e => rule.Kinds.Count == 0 || rule.Kinds.Contains(e.Kind))
                .Where(e => rule.ObjectKinds.Count == 0 || rule.ObjectKinds.Contains(e.Object.Kind))
                .Where(e => rule.MinSeverity is not { } min || e.Severity >= min)
                .Where(e => rule.RequiredTags.All(t => e.Tags.Contains(t, StringComparer.OrdinalIgnoreCase)))
                .Where(e => rule.AnyTags.Count == 0 || rule.AnyTags.Any(t => e.Tags.Contains(t, StringComparer.OrdinalIgnoreCase)))
                .OrderByDescending(e => e.Id)
                .ToList();
            var perDay = matched
                .GroupBy(e => DateOnly.FromDateTime(e.DetectedAt.UtcDateTime))
                .OrderBy(g => g.Key)
                .Select(g => new DailyCount(g.Key, g.Count(), rule.Delivery.Immediate ? Math.Min(g.Count(), 3) : 0))
                .ToList();
            return new RulePreviewResult
            {
                MatchedEvents = matched.Count,
                EstimatedImmediateMessages = perDay.Sum(d => d.Messages),
                PerDay = perDay,
                Samples = [.. matched.Take(20)],
            };
        }
    }

    public async Task<DigestPreviewDto> PreviewDigestAsync(CancellationToken cancellationToken)
    {
        await EnterAsync(nameof(PreviewDigestAsync), cancellationToken).ConfigureAwait(false);
        return DigestPreview;
    }

    public async Task SendDigestNowAsync(CancellationToken cancellationToken)
    {
        await EnterAsync(nameof(SendDigestNowAsync), cancellationToken).ConfigureAwait(false);
        RequireAdmin();
        lock (_gate)
        {
            DigestSends++;
        }
    }

    public async Task<OutboxPage> GetOutboxAsync(OutboxQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        lock (_gate)
        {
            _outboxQueries.Add(query);
        }

        await EnterAsync(nameof(GetOutboxAsync), cancellationToken).ConfigureAwait(false);
        RequireAdmin();
        lock (_gate)
        {
            var limit = Math.Clamp(query.Limit, 1, 500);
            var page = Outbox
                .Where(o => query.Status is not { } status || o.Status == status)
                .Where(o => query.ChannelId is not { } channel || string.Equals(o.ChannelId, channel, StringComparison.OrdinalIgnoreCase))
                .Where(o => query.Before is not { } before || o.Id < before)
                .OrderByDescending(o => o.Id)
                .Take(limit + 1)
                .ToList();
            return new OutboxPage { Items = [.. page.Take(limit)], HasMore = page.Count > limit };
        }
    }

    public async Task RetryOutboxAsync(long id, CancellationToken cancellationToken)
    {
        await EnterAsync(nameof(RetryOutboxAsync), cancellationToken).ConfigureAwait(false);
        RequireAdmin();
        lock (_gate)
        {
            var index = Outbox.FindIndex(o => o.Id == id);
            if (index < 0)
            {
                throw Error(WatchdogClientErrorKind.NotFound, 404, "Delivery not found.");
            }

            if (Outbox[index].Status != OutboxStatus.Dead)
            {
                throw Error(WatchdogClientErrorKind.ServerError, 409, "Only dead deliveries can be retried.");
            }

            Outbox[index] = Outbox[index] with { Status = OutboxStatus.Pending, NextAttemptAt = _time.GetUtcNow(), LastError = null, LastErrorCode = null };
        }
    }

    public async Task<UserMessagingStatusDto> GetUserMessagingStatusAsync(CancellationToken cancellationToken)
    {
        await EnterAsync(nameof(GetUserMessagingStatusAsync), cancellationToken).ConfigureAwait(false);
        return UserMessagingStatus;
    }

    public async Task<UserMessageResult> SendUserMessageAsync(UserMessageRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        await EnterAsync(nameof(SendUserMessageAsync), cancellationToken).ConfigureAwait(false);
        lock (_gate)
        {
            var repeated = _userMessages.Any(m => m.Id == request.Id);
            _userMessages.Add(request);
            if (!UserMessagingStatus.Available)
            {
                throw new WatchdogClientException(WatchdogClientErrorKind.Validation, "Messages to users are switched off.")
                {
                    StatusCode = 409,
                    ProblemType = ApiProblem.UserMessagesUnavailable,
                    Issues = [new ConfigIssue { Path = "userMessages.enabled", Code = UserMessageIssueCodes.Disabled, Severity = IssueSeverity.Error, Message = "off" }],
                };
            }

            if (request.InlineImages.Count > 0 && !UserMessagingStatus.Capabilities.Contains(UserMessagingCapabilities.InlineImages))
            {
                var (path, code) = RejectImagesAsOlderService ? ("$", "invalidJson") : ("inlineImages", UserMessageIssueCodes.ImagesDisabled);
                throw new WatchdogClientException(WatchdogClientErrorKind.Validation, "The request was rejected.")
                {
                    StatusCode = 400,
                    ProblemType = ApiProblem.Validation,
                    Issues = [new ConfigIssue { Path = path, Code = code, Severity = IssueSeverity.Error, Message = code }],
                };
            }

            var result = UserMessageHandler?.Invoke(request)
                         ?? new UserMessageResult { Id = request.Id, State = UserMessageState.Delivered, Code = "ok", SmtpStatus = 250 };
            return repeated ? result with { Duplicate = true } : result;
        }
    }

    public void Dispose() => Calls.Record(nameof(Dispose));

    private static DeliveryTestResult Result(bool success, IReadOnlyList<DeliveryStageResult> stages, IReadOnlyList<string> hints) => new()
    {
        Success = success,
        Stages = stages,
        Hints = hints,
        Duration = TimeSpan.FromMilliseconds(stages.Count * 120),
    };

    private static DeliveryStageResult Stage(DeliveryStage stage, string detail, int? statusCode = null) => new()
    {
        Stage = stage,
        Success = true,
        Detail = detail,
        StatusCode = statusCode,
        Duration = TimeSpan.FromMilliseconds(120),
    };

    private static WatchdogClientException Error(WatchdogClientErrorKind kind, int? statusCode, string message) =>
        new(kind, message) { StatusCode = statusCode };

    private static WatchdogClientException Validation(string code, string path, string message) =>
        new(WatchdogClientErrorKind.Validation, message)
        {
            StatusCode = 400,
            Issues = [new ConfigIssue { Path = path, Code = code, Severity = IssueSeverity.Error, Message = message }],
        };

    private static void CheckComment(string? comment)
    {
        if (comment is { Length: > 500 })
        {
            throw Validation("range", "comment", "The comment is longer than 500 characters.");
        }
    }

    private static int? StatusCodeOf(WatchdogClientErrorKind kind) => kind switch
    {
        WatchdogClientErrorKind.Unauthorized => 401,
        WatchdogClientErrorKind.Forbidden => 403,
        WatchdogClientErrorKind.NotFound => 404,
        WatchdogClientErrorKind.Validation => 400,
        WatchdogClientErrorKind.VersionConflict => 412,
        WatchdogClientErrorKind.ServerError => 500,
        _ => null,
    };

    private IReadOnlyList<T> Snapshot<T>(List<T> list)
    {
        lock (_gate)
        {
            return [.. list];
        }
    }

    private void Acknowledge(long id, string? comment)
    {
        var index = _events.FindIndex(e => e.Id == id);
        if (index >= 0 && _events[index].AcknowledgedAt is null)
        {
            _events[index] = _events[index] with
            {
                AcknowledgedAt = _time.GetUtcNow(),
                AcknowledgedBy = Me.Account,
                AcknowledgeComment = comment,
            };
        }
    }

    private void RequireAdmin()
    {
        if (!IsAdmin)
        {
            throw Error(WatchdogClientErrorKind.Forbidden, 403, "The Admin role is required.");
        }
    }

    /// <summary>Records the call, waits while it is held, then throws a scripted failure or "not configured".</summary>
    private async Task EnterAsync(string method, CancellationToken cancellationToken)
    {
        Calls.Record(method);
        Task? hold;
        lock (_gate)
        {
            hold = _holds.TryGetValue(method, out var completion) ? completion.Task : null;
        }

        if (hold is not null)
        {
            await WaitHeldAsync(hold, cancellationToken).ConfigureAwait(false);
        }

        if (cancellationToken.IsCancellationRequested)
        {
            throw Cancelled(null);
        }

        Exception? failure = null;
        lock (_gate)
        {
            if (_nextFailures.TryGetValue(method, out var queue) && queue.TryDequeue(out var next))
            {
                failure = next;
            }
            else if (_failures.TryGetValue(method, out var kind) || _failures.TryGetValue(AllMethods, out kind))
            {
                failure = new WatchdogClientException(kind, $"Scripted failure: {kind}.") { StatusCode = StatusCodeOf(kind) };
            }
            else if (_options is null)
            {
                failure = new WatchdogClientException(WatchdogClientErrorKind.NotConfigured, "The watchdog service address is not configured.");
            }
            else if (ServerCertificate is { } certificate
                     && WatchdogServerCertificate.NormalizeSha256(_options.PinnedCertificateSha256) is var pin
                     && !string.Equals(pin, certificate.Sha256, StringComparison.Ordinal))
            {
                failure = pin is null
                    ? new WatchdogClientException(WatchdogClientErrorKind.CertificateUntrusted, "Scripted failure: CertificateUntrusted.") { Certificate = certificate }
                    : new WatchdogClientException(WatchdogClientErrorKind.CertificateChanged, "Scripted failure: CertificateChanged.")
                    {
                        Certificate = certificate,
                        PinnedCertificateSha256 = pin,
                    };
            }
        }

        if (failure is not null)
        {
            throw failure;
        }
    }

    /// <summary>Long-poll: returns when an event newer than <paramref name="after"/> exists or <paramref name="deadline"/> passed.</summary>
    private async Task WaitForNewerAsync(long after, DateTimeOffset deadline, CancellationToken cancellationToken)
    {
        Task signal;
        lock (_gate)
        {
            if (_events.Count > 0 && _events.Max(e => e.Id) > after)
            {
                return;
            }

            signal = _newEvent.Task;
        }

        var wait = deadline - _time.GetUtcNow();
        if (wait <= TimeSpan.Zero)
        {
            return;
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var delay = Task.Delay(wait, _time, timeout.Token);

        // A test may advance the clock between reading it and starting the timer: the deadline still counts.
        if (_time.GetUtcNow() >= deadline)
        {
            await timeout.CancelAsync().ConfigureAwait(false);
            return;
        }

        var winner = await Task.WhenAny(signal, delay).ConfigureAwait(false);
        await timeout.CancelAsync().ConfigureAwait(false);
        if (cancellationToken.IsCancellationRequested || (winner == delay && delay.IsCanceled))
        {
            throw Cancelled(null);
        }
    }

    /// <summary>
    /// The caller cancelled the request: like <see cref="WatchdogHttpClient"/> (<c>WatchdogClientErrors.FromException</c>)
    /// the fake reports it as a <see cref="WatchdogClientException"/> of kind Cancelled, not as an
    /// <see cref="OperationCanceledException"/> – view models must treat both as "superseded", never as an error.
    /// </summary>
    private static WatchdogClientException Cancelled(Exception? inner) =>
        new(WatchdogClientErrorKind.Cancelled, "The request was cancelled.", inner);

    /// <summary>Waits for a held call to be released; the caller's cancellation is reported like the real client does.</summary>
    private static async Task WaitHeldAsync(Task hold, CancellationToken cancellationToken)
    {
        try
        {
            await hold.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException ex) when (cancellationToken.IsCancellationRequested)
        {
            throw Cancelled(ex);
        }
    }
}
