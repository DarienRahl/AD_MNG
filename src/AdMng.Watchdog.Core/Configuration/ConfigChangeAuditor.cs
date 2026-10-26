using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;
using AdMng.Watchdog.Contracts;
using AdMng.Watchdog.Contracts.Configuration;
using AdMng.Watchdog.Contracts.Events;

namespace AdMng.Watchdog.Core.Configuration;

/// <summary>
/// Diffs the previous and the new runtime configuration of a save (<c>PUT /api/v1/config</c>, section 17.2 step 5) into
/// the data of the <see cref="WatchEventKind.WatchdogConfigurationChanged"/> event: severity (section 7), changed
/// setting paths and a culture-neutral summary. Never reads or emits values – only paths, change kinds and counts.
/// </summary>
/// <remarks>
/// <para>Pass the new configuration <b>as received</b> (before <c>ConfigSecretsApplier</c> strips
/// <see cref="SecretField.NewValue"/>/<see cref="SecretField.Clear"/>): a NewValue is reported as <c>replaced</c>
/// (<c>set</c> when nothing was set), Clear as <c>cleared</c>; on stripped documents only IsSet transitions are visible.
/// The comparison covers every serialized property (additive contract changes are picked up automatically); rules,
/// channels and recipient groups are matched by id, exclusions are reported as counts per rule only. A nested list
/// stored as null counts as an empty list.</para>
/// <para>Severity (maximum over the changes, at least Medium):</para>
/// <list type="bullet">
/// <item><b>Critical</b> – disabling or weakening <c>tier0-*</c> / <c>watchdog-self</c> / <c>health</c> delivery or matching (rule removed or
/// disabled; kinds, object kinds, scope, attributes or tags narrowed; group filter, MinSeverity raised, Severity override
/// changed to anything but Critical; actor exclusions added; exclusions added or changed; immediate/digest/all-channels
/// switched off; recipient groups or channels removed from it; hours weakened; aggregation window or repeat suppression
/// set/increased; hourly cap set/lowered; <c>locked</c> cleared on any rule); the global defaults of those
/// (<c>notifications.aggregationWindow</c> / <c>highSeverityFlushDelay</c> increased,
/// <c>notifications.maxMessagesPerRulePerHour</c> lowered); additions to
/// <c>directory.ignoredAttributes</c>, <c>directory.disabledKinds</c>, <c>directory.excludedSubtrees</c>; removals from
/// <c>directory.criticalObjects</c> and from the privileged group lists (except <c>watchListDns</c>); a Tier-0 detection
/// source switched off (configuration/schema partition watching, <c>directory.trackDeletedObjects</c>,
/// <c>attribution.enabled</c> or <c>attribution.source</c> = None, <c>scans.passwordSpray.enabled</c>) or a burst
/// detector made less sensitive (<c>scans.massLockout</c>/<c>massDeletion</c>/<c>massComputerDeletion</c> threshold or
/// percentage raised or window shortened, <c>scans.passwordSpray.distinctAccounts</c> raised or window shortened);
/// <c>smtp.enabled</c> switched off; a channel or recipient group used by a
/// <c>tier0-*</c>/<c>watchdog-self</c>/<c>health</c> rule removed or disabled, or addresses removed from such a group;
/// any decrease of a numeric <c>retention</c> value (stage B design 14.2: events, DC log and logon records, backups,
/// outbox, snapshots, audit, size cap – shorter history is deleted at the next maintenance); a whole section
/// removed.</item>
/// <item><b>High</b> – any other change of recipients (<c>recipientGroups</c>, rule and digest recipient/channel
/// references, <c>notifyManager</c>, a whole rule added/removed that sends somewhere or carries exclusions),
/// <c>channels</c>, <c>smtp</c>, <c>attribution</c>, exclusions; <c>digest.enabled</c>; <c>userMessages.enabled</c> and
/// <c>userMessages.allowedSenders</c>.</item>
/// <item><b>Medium</b> – everything else.</item>
/// </list>
/// </remarks>
public sealed class ConfigChangeAuditor
{
    /// <summary>Items listed in <see cref="ConfigChangeAudit.Summary"/> before "(+N more)".</summary>
    public const int MaxSummaryItems = 40;

    private static readonly JsonTypeInfo<WatchdogConfiguration> ConfigurationTypeInfo =
        (JsonTypeInfo<WatchdogConfiguration>)WatchdogJson.Options.GetTypeInfo(typeof(WatchdogConfiguration));

    private readonly TimeProvider _timeProvider;

    public ConfigChangeAuditor(TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);
        _timeProvider = timeProvider;
    }

    /// <summary>Compares <paramref name="previous"/> (the stored configuration) with <paramref name="current"/> (the
    /// configuration being saved, ideally still carrying NewValue/Clear).</summary>
    public ConfigChangeAudit Compare(WatchdogConfiguration previous, WatchdogConfiguration current)
    {
        ArgumentNullException.ThrowIfNull(previous);
        ArgumentNullException.ThrowIfNull(current);

        var walker = new Walker(new Context(previous, current));
        walker.Diff([], JsonSerializer.SerializeToNode(previous, ConfigurationTypeInfo), JsonSerializer.SerializeToNode(current, ConfigurationTypeInfo));
        var items = walker.Items.OrderByDescending(i => i.Severity).ToArray();
        var severity = items.Length == 0 ? Severity.Medium : (Severity)Math.Max((int)Severity.Medium, (int)items[0].Severity);
        return new ConfigChangeAudit { Severity = severity, Items = items, Summary = Summarize(items) };
    }

    /// <summary>
    /// The <see cref="WatchEventKind.WatchdogConfigurationChanged"/> event of a save (not stored yet: Id 0): source
    /// Health, severity of <paramref name="audit"/>, object <c>{ Kind = Watchdog, Dn = "" }</c>, attribution Resolved
    /// (High) with <paramref name="changedBy"/> as actor, one <see cref="AttributeChange"/> per item
    /// (<c>Attribute</c> = path, <c>NewValues</c> = [<see cref="ConfigChangeItem.Description"/>], no other values).
    /// </summary>
    /// <param name="audit">Result of <see cref="Compare"/>.</param>
    /// <param name="changedBy">The API caller.</param>
    /// <param name="domain">DNS name of the watched domain, when known.</param>
    /// <param name="sourceIpAddress">Remote address of the API connection (SourceMethod "local").</param>
    public WatchEvent CreateEvent(ConfigChangeAudit audit, PrincipalRef changedBy, string? domain = null, string? sourceIpAddress = null)
    {
        ArgumentNullException.ThrowIfNull(audit);
        ArgumentNullException.ThrowIfNull(changedBy);

        var now = _timeProvider.GetUtcNow();
        var eventId = Guid.CreateVersion7(now);
        var hasSource = !string.IsNullOrWhiteSpace(sourceIpAddress);
        return new WatchEvent
        {
            EventId = eventId,
            ChangeSetId = eventId,
            Kind = WatchEventKind.WatchdogConfigurationChanged,
            Severity = audit.Severity,
            Object = new ObjectRef { Dn = string.Empty, Kind = WatchObjectKind.Watchdog },
            Changes = [.. audit.Items.Select(i => new AttributeChange
            {
                Attribute = i.Path,
                ValueKind = AttributeValueKind.Text,
                NewValues = [i.Description],
                NewCount = 1,
            })],
            Domain = domain,
            DetectedAt = now,
            Source = DetectionSource.Health,
            Attribution = new AdMng.Watchdog.Contracts.Events.Attribution
            {
                Status = AttributionStatus.Resolved,
                Confidence = AttributionConfidence.High,
                Actor = changedBy,
                IpAddress = hasSource ? sourceIpAddress!.Trim() : null,
                SourceMethod = hasSource ? "local" : null,
                SourceConfidence = hasSource ? AttributionConfidence.High : AttributionConfidence.None,
                ResolvedAt = now,
            },
        };
    }

    private static string Summarize(IReadOnlyList<ConfigChangeItem> items)
    {
        if (items.Count == 0)
        {
            return "no changes";
        }

        var builder = new StringBuilder();
        foreach (var item in items.Take(MaxSummaryItems))
        {
            if (builder.Length > 0)
            {
                builder.Append("; ");
            }

            builder.Append(item.Path).Append(' ').Append(item.Description);
        }

        if (items.Count > MaxSummaryItems)
        {
            builder.Append(CultureInfo.InvariantCulture, $"; (+{items.Count - MaxSummaryItems} more)");
        }

        return builder.ToString();
    }

    private readonly record struct Segment(string Name, string? Key);

    private readonly record struct Counts(int Added, int Removed, int Modified);

    /// <summary>Lookups into the typed configurations for the severity rules.</summary>
    private sealed class Context
    {
        private readonly HashSet<string> _protectedGroups = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _protectedChannels = new(StringComparer.OrdinalIgnoreCase);
        private readonly bool _protectedAllChannels;

        public Context(WatchdogConfiguration previous, WatchdogConfiguration current)
        {
            Previous = previous;
            Current = current;
            foreach (var rule in (previous.Rules ?? []).Where(r => r is not null && IsProtectedRule(r.Id)))
            {
                var delivery = rule.Delivery ?? new RuleDelivery();
                _protectedGroups.UnionWith((delivery.RecipientGroupIds ?? []).Where(id => id is not null));
                _protectedChannels.UnionWith((delivery.ChannelIds ?? []).Where(id => id is not null));
                _protectedAllChannels |= delivery.AllEnabledChannels;
            }
        }

        public WatchdogConfiguration Previous { get; }

        public WatchdogConfiguration Current { get; }

        public static bool IsProtectedRule(string? id) =>
            id is not null && (Rules.Tier0Catalog.IsTier0Rule(id) || id.Equals(Rules.DefaultRules.Health, StringComparison.OrdinalIgnoreCase));

        public bool IsProtectedChannel(string? id) => _protectedAllChannels || (id is not null && _protectedChannels.Contains(id));

        public bool IsProtectedGroup(string? id) => id is not null && _protectedGroups.Contains(id);

        public static RuleSettings? FindRule(WatchdogConfiguration configuration, string? key) =>
            key is null ? null : (configuration.Rules ?? []).FirstOrDefault(r => r is not null && string.Equals(r.Id, key, StringComparison.OrdinalIgnoreCase));
    }

    private sealed class Walker(Context context)
    {
        public List<ConfigChangeItem> Items { get; } = [];

        public void Diff(Segment[] path, JsonNode? oldNode, JsonNode? newNode)
        {
            if (oldNode is null && newNode is null)
            {
                return;
            }

            if (IsSecretField(oldNode) || IsSecretField(newNode))
            {
                DiffSecret(path, oldNode as JsonObject, newNode as JsonObject);
                return;
            }

            if (oldNode is JsonObject oldObject && newNode is JsonObject newObject)
            {
                foreach (var name in oldObject.Select(p => p.Key).Concat(newObject.Select(p => p.Key)).Distinct(StringComparer.Ordinal))
                {
                    Diff([.. path, new Segment(name, null)], oldObject[name], newObject[name]);
                }

                return;
            }

            if (oldNode is JsonArray oldArray && newNode is JsonArray newArray)
            {
                DiffArray(path, oldArray, newArray);
                return;
            }

            // A nested list stored as null means "empty" (every consumer reads it with ?? []): diff it as an empty list, so
            // e.g. exclusions null → [x] on a tier0 rule is counted (+1) and classified like [] → [x], never hidden.
            if (path.Length > 1 && (oldNode is JsonArray && newNode is null || oldNode is null && newNode is JsonArray))
            {
                DiffArray(path, oldNode as JsonArray ?? [], newNode as JsonArray ?? []);
                return;
            }

            if (JsonNode.DeepEquals(oldNode, newNode))
            {
                return;
            }

            var kind = oldNode is null ? ConfigChangeKind.Added : newNode is null ? ConfigChangeKind.Removed : ConfigChangeKind.Modified;
            Add(path, kind, isCollection: false, default, oldNode, newNode);
        }

        private void DiffArray(Segment[] path, JsonArray oldArray, JsonArray newArray)
        {
            if (JsonNode.DeepEquals(oldArray, newArray))
            {
                return;
            }

            var keyed = IsKeyed(oldArray) && IsKeyed(newArray);
            if (path[^1].Name == "exclusions")
            {
                // Exclusion contents (objects, subtrees, actors, reasons) are not echoed – counts per rule only. Without
                // usable ids the change counts as "modified" (never under-reported on a tier0 rule).
                Counts counts;
                if (keyed)
                {
                    var oldExclusions = Keyed(oldArray);
                    var newExclusions = Keyed(newArray).ToDictionary(i => i.Key, i => i.Node, StringComparer.Ordinal);
                    var oldIds = oldExclusions.Select(i => i.Key).ToHashSet(StringComparer.Ordinal);
                    counts = new Counts(
                        newExclusions.Keys.Count(k => !oldIds.Contains(k)),
                        oldExclusions.Count(i => !newExclusions.ContainsKey(i.Key)),
                        oldExclusions.Count(i => newExclusions.TryGetValue(i.Key, out var n) && !JsonNode.DeepEquals(i.Node, n)));
                }
                else
                {
                    counts = new Counts(Math.Max(0, newArray.Count - oldArray.Count), Math.Max(0, oldArray.Count - newArray.Count), 1);
                }

                if (counts != default)
                {
                    Add(path, ConfigChangeKind.Modified, isCollection: true, counts, oldArray, newArray);
                }

                return;
            }

            if (keyed)
            {
                var oldItems = Keyed(oldArray);
                var newItems = Keyed(newArray);
                var newByKey = newItems.ToDictionary(i => i.Key, i => i.Node, StringComparer.Ordinal);
                var oldKeys = oldItems.Select(i => i.Key).ToHashSet(StringComparer.Ordinal);

                foreach (var (key, node) in oldItems)
                {
                    Segment[] elementPath = [.. path[..^1], path[^1] with { Key = key }];
                    if (newByKey.TryGetValue(key, out var newNode))
                    {
                        Diff(elementPath, node, newNode);
                    }
                    else
                    {
                        Add(elementPath, ConfigChangeKind.Removed, isCollection: false, default, node, null);
                    }
                }

                foreach (var (key, node) in newItems.Where(i => !oldKeys.Contains(i.Key)))
                {
                    Add([.. path[..^1], path[^1] with { Key = key }], ConfigChangeKind.Added, isCollection: false, default, null, node);
                }

                return;
            }

            if (oldArray.Concat(newArray).All(n => n is null or JsonValue))
            {
                // Case-insensitive multiset difference: DNs, attribute names, SIDs, ids, tags and addresses all compare so.
                var balance = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                foreach (var node in oldArray)
                {
                    balance[Text(node)] = balance.GetValueOrDefault(Text(node)) + 1;
                }

                foreach (var node in newArray)
                {
                    balance[Text(node)] = balance.GetValueOrDefault(Text(node)) - 1;
                }

                var counts = new Counts(balance.Values.Where(v => v < 0).Sum(v => -v), balance.Values.Where(v => v > 0).Sum(), 0);
                Add(path, ConfigChangeKind.Modified, isCollection: true, counts, oldArray, newArray);
                return;
            }

            Add(path, ConfigChangeKind.Modified, isCollection: false, default, oldArray, newArray);
        }

        private void DiffSecret(Segment[] path, JsonObject? oldField, JsonObject? newField)
        {
            var oldSet = Bool(oldField, "isSet");
            ConfigChangeKind? kind;
            if (newField is null)
            {
                kind = oldSet ? ConfigChangeKind.SecretCleared : null;
            }
            else if (newField["newValue"] is JsonValue value && value.TryGetValue<string>(out var text) && text.Length > 0)
            {
                kind = oldSet ? ConfigChangeKind.SecretReplaced : ConfigChangeKind.SecretSet;
            }
            else if (Bool(newField, "clear"))
            {
                kind = oldSet ? ConfigChangeKind.SecretCleared : null;
            }
            else
            {
                var newSet = Bool(newField, "isSet");
                kind = oldSet == newSet ? null : newSet ? ConfigChangeKind.SecretSet : ConfigChangeKind.SecretCleared;
            }

            if (kind is { } k)
            {
                Add(path, k, isCollection: false, default, null, null);
            }
        }

        private void Add(Segment[] path, ConfigChangeKind kind, bool isCollection, Counts counts, JsonNode? oldNode, JsonNode? newNode)
        {
            Items.Add(new ConfigChangeItem
            {
                Path = path.Length == 0 ? "(root)" : FormatPath(path),
                Kind = kind,
                Severity = path.Length == 0 ? Severity.Critical : Classify(path, kind, counts, oldNode, newNode),
                IsCollection = isCollection,
                AddedCount = counts.Added,
                RemovedCount = counts.Removed,
                ModifiedCount = counts.Modified,
            });
        }

        private Severity Classify(Segment[] path, ConfigChangeKind kind, Counts counts, JsonNode? oldNode, JsonNode? newNode)
        {
            var key = path[0].Key;
            if (path.Length == 1 && key is null && kind == ConfigChangeKind.Removed)
            {
                return Severity.Critical;                                   // a whole section dropped
            }

            var property = path.Length > 1 ? path[1].Name : null;
            var wholeElementRemoved = path.Length == 1 && kind == ConfigChangeKind.Removed;
            switch (path[0].Name)
            {
                case "directory":
                    return property switch
                    {
                        "ignoredAttributes" or "disabledKinds" or "excludedSubtrees" when counts.Added > 0 || !IsCollection(oldNode, newNode) => Severity.Critical,
                        "criticalObjects" when counts.Removed > 0 || !IsCollection(oldNode, newNode) => Severity.Critical,
                        // The deleted-objects pass finds deleted trusts/PSOs (Domain) and feeds MassDeletion (Security).
                        "watchConfigurationPartition" or "watchSchemaPartition" or "trackDeletedObjects" when TurnedOff(oldNode, newNode) => Severity.Critical,
                        _ => Severity.Medium,
                    };
                case "attribution":
                    // Without attribution no DC Security log is read: SecurityLogCleared, AuditPolicyChanged and
                    // DsrmPasswordChanged (AuditLog, Tier-0) are never detected – like adding them to disabledKinds.
                    return property switch
                    {
                        "enabled" => TurnedOff(oldNode, newNode),
                        "source" => context.Current.Attribution?.Source == AttributionSourceKind.None
                                    && context.Previous.Attribution?.Source != AttributionSourceKind.None,
                        _ => false,
                    }
                        ? Severity.Critical
                        : Severity.High;
                case "scans":
                    return ScanWeakensTier0Detection(path, kind, oldNode, newNode) ? Severity.Critical : Severity.Medium;
                case "notifications":
                    // Global defaults of every rule without its own value (tier0-*/health included): the same weakening as
                    // the per-rule aggregation window / hourly cap.
                    return property switch
                    {
                        "aggregationWindow" => Rose(c => c.Notifications?.AggregationWindow),
                        "highSeverityFlushDelay" => Rose(c => c.Notifications?.HighSeverityFlushDelay),
                        "maxMessagesPerRulePerHour" => Fell(c => c.Notifications?.MaxMessagesPerRulePerHour),
                        _ => false,
                    }
                        ? Severity.Critical
                        : Severity.Medium;
                case "privilegedGroups":
                    return property != "watchListDns" && (counts.Removed > 0 || !IsCollection(oldNode, newNode)) ? Severity.Critical : Severity.Medium;
                case "smtp":
                    return property == "enabled" && TurnedOff(oldNode, newNode) ? Severity.Critical : Severity.High;
                case "channels":
                    return (wholeElementRemoved || (property == "enabled" && path.Length == 2 && TurnedOff(oldNode, newNode)))
                           && (key is null || context.IsProtectedChannel(key))
                        ? Severity.Critical
                        : Severity.High;
                case "recipientGroups":
                    return (wholeElementRemoved || (property == "addresses" && counts.Removed > 0)) && (key is null || context.IsProtectedGroup(key))
                        ? Severity.Critical
                        : Severity.High;
                case "digest":
                    return property is "enabled" or "recipientGroupIds" or "channelIds" ? Severity.High : Severity.Medium;
                case "userMessages":
                    // Who may send in the name of the IT department (README U1b): switching it on or widening the senders.
                    return property is "enabled" or "allowedSenders" ? Severity.High : Severity.Medium;
                case "retention":
                    return Decreased(oldNode, newNode) ? Severity.Critical : Severity.Medium;
                case "rules":
                    return ClassifyRule(path, kind, counts, oldNode, newNode);
                default:
                    return Severity.Medium;
            }
        }

        private Severity ClassifyRule(Segment[] path, ConfigChangeKind kind, Counts counts, JsonNode? oldNode, JsonNode? newNode)
        {
            var key = path[0].Key;
            if (key is null)
            {
                return Severity.Critical;                                   // rules without usable ids cannot be matched
            }

            var isProtected = Context.IsProtectedRule(key);
            if (path.Length == 1)
            {
                if (kind == ConfigChangeKind.Removed && isProtected)
                {
                    return Severity.Critical;
                }

                // A whole rule that sends somewhere or carries exclusions changes recipients / exclusions (High).
                return HasRecipientsOrExclusions(kind == ConfigChangeKind.Removed ? oldNode : newNode) ? Severity.High : Severity.Medium;
            }

            var previous = Context.FindRule(context.Previous, key);
            var current = Context.FindRule(context.Current, key);
            var property = path[1].Name;
            var sub = path.Length > 2 ? path[2].Name : null;
            var weakens = property switch
            {
                "locked" => TurnedOff(oldNode, newNode),
                "exclusions" => counts.Added > 0 || counts.Modified > 0,
                "enabled" => TurnedOff(oldNode, newNode),
                "kinds" or "objectKinds" or "attributes" or "anyTags" => NarrowsAnyList(oldNode, newNode, counts),
                "requiredTags" => counts.Added > 0,
                "scope" => sub switch
                {
                    "includeSubtrees" => NarrowsAnyList(oldNode, newNode, counts),
                    "excludeSubtrees" => counts.Added > 0,
                    _ => kind != ConfigChangeKind.Modified,
                },
                "groups" => current?.Groups is not GroupFilter.Any,
                "groupDns" => counts.Removed > 0 || (current?.Groups == GroupFilter.Explicit && !IsCollection(oldNode, newNode)),
                "minSeverity" => (current?.MinSeverity ?? Severity.Info) > (previous?.MinSeverity ?? Severity.Info),
                "severity" => current?.Severity != Severity.Critical,
                "actors" => sub switch
                {
                    "excludeActors" => counts.Added > 0,
                    "excludeSelfChanges" => TurnedOn(oldNode, newNode),
                    _ => kind != ConfigChangeKind.Modified,
                },
                "delivery" => DeliveryWeakens(sub, kind, counts, oldNode, newNode, previous?.Delivery, current?.Delivery),
                _ => false,
            };

            if (property == "locked")
            {
                return weakens ? Severity.Critical : Severity.Medium;      // any rule: the lock protects the health rule
            }

            if (weakens && isProtected)
            {
                return Severity.Critical;
            }

            return property switch
            {
                "exclusions" => Severity.High,
                "delivery" when sub is "recipientGroupIds" or "channelIds" or "allEnabledChannels" or "notifyManager" => Severity.High,
                _ => Severity.Medium,
            };
        }

        private static bool HasRecipientsOrExclusions(JsonNode? rule) =>
            rule is JsonObject obj
            && (obj["exclusions"] is JsonArray { Count: > 0 }
                || obj["delivery"] is JsonObject delivery
                   && (delivery["recipientGroupIds"] is JsonArray { Count: > 0 }
                       || delivery["channelIds"] is JsonArray { Count: > 0 }
                       || IsTrue(delivery["allEnabledChannels"])
                       || IsTrue(delivery["notifyManager"])));

        /// <summary>Burst detectors and the password-spray detector produce Security (Tier-0) kinds: switching one off,
        /// raising a threshold or shortening a window weakens Tier-0 detection.</summary>
        private bool ScanWeakensTier0Detection(Segment[] path, ConfigChangeKind kind, JsonNode? oldNode, JsonNode? newNode)
        {
            var detector = path.Length > 1 ? path[1].Name : null;
            var setting = path.Length > 2 ? path[2].Name : null;
            Func<WatchdogConfiguration, BurstSettings?>? burst = detector switch
            {
                "massLockout" => c => c.Scans?.MassLockout,
                "massDeletion" => c => c.Scans?.MassDeletion,
                "massComputerDeletion" => c => c.Scans?.MassComputerDeletion,
                _ => null,
            };

            if (burst is not null)
            {
                return setting switch
                {
                    "threshold" => Rose(c => burst(c)?.Threshold),
                    "percentOfEnabledUsers" => Rose(c => burst(c)?.PercentOfEnabledUsers),
                    "window" => Fell(c => burst(c)?.Window),
                    null => kind != ConfigChangeKind.Modified,
                    _ => false,
                };
            }

            return detector == "passwordSpray" && setting switch
            {
                "enabled" => TurnedOff(oldNode, newNode),
                "distinctAccounts" => Rose(c => c.Scans?.PasswordSpray?.DistinctAccounts),
                "window" => Fell(c => c.Scans?.PasswordSpray?.Window),
                null => kind != ConfigChangeKind.Modified,
                _ => false,
            };
        }

        /// <summary>The typed value grew (or appeared) from the previous to the current configuration.</summary>
        private bool Rose<T>(Func<WatchdogConfiguration, T?> value) where T : struct, IComparable<T> =>
            value(context.Current) is { } now && (value(context.Previous) is not { } before || now.CompareTo(before) > 0);

        /// <summary>The typed value shrank (or appeared) from the previous to the current configuration.</summary>
        private bool Fell<T>(Func<WatchdogConfiguration, T?> value) where T : struct, IComparable<T> =>
            value(context.Current) is { } now && (value(context.Previous) is not { } before || now.CompareTo(before) < 0);

        private static bool DeliveryWeakens(
            string? sub, ConfigChangeKind kind, Counts counts, JsonNode? oldNode, JsonNode? newNode, RuleDelivery? previous, RuleDelivery? current) => sub switch
        {
            "recipientGroupIds" or "channelIds" => counts.Removed > 0 || !IsCollection(oldNode, newNode),
            "allEnabledChannels" or "immediate" or "includeInDigest" => TurnedOff(oldNode, newNode),
            "hours" => HoursRank(current?.Hours) < HoursRank(previous?.Hours),
            "aggregationWindow" => current?.AggregationWindow is { } window && (previous?.AggregationWindow is not { } before || window > before),
            "repeatSuppression" => current?.RepeatSuppression is { } period && (previous?.RepeatSuppression is not { } before || period > before),
            "maxMessagesPerHour" => current?.MaxMessagesPerHour is { } cap && (previous?.MaxMessagesPerHour is not { } before || cap < before),
            "notifyManager" => false,
            null => kind != ConfigChangeKind.Modified,
            _ => false,
        };

        private static int HoursRank(DeliveryHours? hours) => hours switch
        {
            DeliveryHours.Always => 2,
            DeliveryHours.Default => 1,
            _ => 0,
        };

        private static bool NarrowsAnyList(JsonNode? oldNode, JsonNode? newNode, Counts counts)
        {
            if (oldNode is not JsonArray oldArray || newNode is not JsonArray newArray)
            {
                return true;                                                // replaced by something that is not a list
            }

            // Empty = any: a first entry narrows; removing an entry narrows unless the list becomes empty again.
            return oldArray.Count == 0 ? newArray.Count > 0 : newArray.Count > 0 && counts.Removed > 0;
        }

        private static bool IsCollection(JsonNode? oldNode, JsonNode? newNode) => oldNode is JsonArray && newNode is JsonArray;

        /// <summary>A number became smaller, or a number was replaced by something that is not one.</summary>
        private static bool Decreased(JsonNode? oldNode, JsonNode? newNode) =>
            oldNode is JsonValue before && before.TryGetValue<double>(out var oldNumber)
            && (newNode is not JsonValue after || !after.TryGetValue<double>(out var newNumber) || newNumber < oldNumber);

        private static bool TurnedOff(JsonNode? oldNode, JsonNode? newNode) => IsTrue(oldNode) && !IsTrue(newNode);

        private static bool TurnedOn(JsonNode? oldNode, JsonNode? newNode) => !IsTrue(oldNode) && IsTrue(newNode);

        private static bool IsTrue(JsonNode? node) => node is JsonValue value && value.TryGetValue<bool>(out var flag) && flag;

        private static bool Bool(JsonObject? node, string name) => IsTrue(node?[name]);

        private static bool IsSecretField(JsonNode? node) =>
            node is JsonObject obj && obj.ContainsKey("isSet") && obj.ContainsKey("clear")
            && obj.All(p => p.Key is "isSet" or "hint" or "newValue" or "clear");

        private static bool IsKeyed(JsonArray array) =>
            array.All(n => n is JsonObject obj && obj["id"] is JsonValue id && id.TryGetValue<string>(out var text) && !string.IsNullOrEmpty(text));

        private static List<(string Key, JsonObject Node)> Keyed(JsonArray array)
        {
            var seen = new Dictionary<string, int>(StringComparer.Ordinal);
            var result = new List<(string, JsonObject)>(array.Count);
            foreach (var node in array.Cast<JsonObject>())
            {
                var id = node["id"]!.GetValue<string>().ToLowerInvariant();
                var occurrence = seen[id] = seen.GetValueOrDefault(id) + 1;
                result.Add((occurrence == 1 ? id : string.Create(CultureInfo.InvariantCulture, $"{id}#{occurrence}"), node));
            }

            return result;
        }

        private static string Text(JsonNode? node) =>
            node is JsonValue value && value.TryGetValue<string>(out var text) ? text : node?.ToJsonString() ?? "null";

        private static string FormatPath(Segment[] path)
        {
            var builder = new StringBuilder();
            foreach (var segment in path)
            {
                if (builder.Length > 0)
                {
                    builder.Append('.');
                }

                builder.Append(segment.Name);
                if (segment.Key is not null)
                {
                    builder.Append('[').Append(segment.Key).Append(']');
                }
            }

            return builder.ToString();
        }
    }
}
