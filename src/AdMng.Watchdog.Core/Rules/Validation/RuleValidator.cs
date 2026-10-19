using AdMng.Watchdog.Contracts.Configuration;
using AdMng.Watchdog.Contracts.Events;

namespace AdMng.Watchdog.Core.Rules.Validation;

/// <summary>
/// Rules (8.3, 12.3): <c>lockedRule</c> (the shipped locked rules exist, stay locked and enabled, keep their kinds and
/// their delivery is not weakened; any locked rule stays enabled), <c>exclusionReason</c>, the delivery ranges, and the
/// warnings <c>tier0Exclusion</c>,
/// <c>noRecipients</c> and <c>smtpDisabledButUsed</c>.
/// </summary>
internal static class RuleValidator
{
    /// <summary>Maximum length of an exclusion reason.</summary>
    public const int MaxReasonLength = 500;

    public static void Validate(ValidationRun run)
    {
        var c = run.Configuration;
        LockedBuiltIns(run, c.Rules);
        for (var i = 0; i < c.Rules.Count; i++)
        {
            if (c.Rules[i] is { } rule)
            {
                Rule(run, ValidationRun.Item("rules", i), rule);
            }
        }

        var usesEmail = c.Rules.Any(r => r is { Enabled: true, Delivery.Immediate: true } && r.Delivery.RecipientGroupIds.Count > 0)
            || (c.Digest.Enabled && c.Digest.RecipientGroupIds.Count > 0);
        if (!c.Smtp.Enabled && usesEmail)
        {
            run.Warning("smtp.enabled", ConfigValidationCodes.SmtpDisabledButUsed,
                "Rules or the digest send to recipient groups, but e-mail (SMTP) is disabled.");
        }
    }

    private static void LockedBuiltIns(ValidationRun run, IReadOnlyList<RuleSettings> rules)
    {
        foreach (var shipped in DefaultRules.Create().Where(r => r.Locked))
        {
            var index = IndexOf(rules, shipped.Id);
            if (index < 0)
            {
                run.Error("rules", ConfigValidationCodes.LockedRule, $"The locked rule \"{shipped.Id}\" cannot be deleted.", shipped.Id);
                continue;
            }

            var rule = rules[index];
            var path = ValidationRun.Item("rules", index);
            if (!rule.Locked)
            {
                run.Error(path + ".locked", ConfigValidationCodes.LockedRule, $"The rule \"{shipped.Id}\" must stay locked.", shipped.Id);
            }

            if (!rule.Kinds.ToHashSet().SetEquals(shipped.Kinds))
            {
                run.Error(path + ".kinds", ConfigValidationCodes.LockedRule, $"The kinds of the locked rule \"{shipped.Id}\" cannot be changed.", shipped.Id);
            }

            LockedDelivery(run, path, rule, shipped);
        }
    }

    /// <summary>
    /// A locked rule whose delivery is switched off is a disabled rule in disguise: compared with the shipped rule,
    /// <c>immediate</c>, <c>includeInDigest</c> and <c>allEnabledChannels</c> cannot be turned off, <c>hours</c> cannot
    /// become narrower, at least one recipient group or channel stays, notifications cannot be suppressed per object,
    /// capped below <c>notifications.maxMessagesPerRulePerHour</c> or aggregated longer than
    /// <c>notifications.aggregationWindow</c>, and a severity override cannot lower them below Critical (throttling exempts
    /// only Critical). Re-targeting to other recipients and strengthening stay allowed.
    /// </summary>
    private static void LockedDelivery(ValidationRun run, string path, RuleSettings rule, RuleSettings shipped)
    {
        var delivery = rule.Delivery;
        var expected = shipped.Delivery;
        var notifications = run.Configuration.Notifications;
        void Weakened(string property, string what) =>
            run.Error(path + "." + property, ConfigValidationCodes.LockedRule, $"The {what} of the locked rule \"{shipped.Id}\" cannot be weakened.", shipped.Id);

        if (expected.Immediate && !delivery.Immediate)
        {
            Weakened("delivery.immediate", "immediate delivery");
        }

        if (expected.IncludeInDigest && !delivery.IncludeInDigest)
        {
            Weakened("delivery.includeInDigest", "digest delivery");
        }

        if (expected.AllEnabledChannels && !delivery.AllEnabledChannels)
        {
            Weakened("delivery.allEnabledChannels", "delivery to every enabled channel");
        }

        if (HoursRank(delivery.Hours) < HoursRank(expected.Hours))
        {
            Weakened("delivery.hours", "delivery hours");
        }

        if (expected.RecipientGroupIds.Count + expected.ChannelIds.Count > 0 && delivery.RecipientGroupIds.Count + delivery.ChannelIds.Count == 0)
        {
            Weakened("delivery.recipientGroupIds", "recipient list");
        }

        if (delivery.RepeatSuppression is { } suppression && suppression > (expected.RepeatSuppression ?? TimeSpan.Zero))
        {
            Weakened("delivery.repeatSuppression", "repeat suppression");
        }

        if (delivery.MaxMessagesPerHour is { } cap && cap < (expected.MaxMessagesPerHour ?? notifications.MaxMessagesPerRulePerHour))
        {
            Weakened("delivery.maxMessagesPerHour", "hourly message cap");
        }

        if (delivery.AggregationWindow is { } window && window > (expected.AggregationWindow ?? notifications.AggregationWindow))
        {
            Weakened("delivery.aggregationWindow", "aggregation window");
        }

        if (shipped.Severity is null && rule.Severity is { } severity && severity < Severity.Critical)
        {
            Weakened("severity", "notification severity");
        }
    }

    private static int HoursRank(DeliveryHours hours) => hours switch
    {
        DeliveryHours.Always => 2,
        DeliveryHours.Default => 1,
        _ => 0,
    };

    private static void Rule(ValidationRun run, string path, RuleSettings rule)
    {
        if (rule.Locked && !rule.Enabled)
        {
            run.Error(path + ".enabled", ConfigValidationCodes.LockedRule, "A locked rule cannot be disabled.", rule.Id);
        }

        var delivery = rule.Delivery;
        if (delivery.AggregationWindow is { } window)
        {
            RangeCheck.Check(run, path + ".delivery.aggregationWindow", window, TimeSpan.Zero, TimeSpan.FromMinutes(60));
        }

        if (delivery.MaxMessagesPerHour is { } cap)
        {
            RangeCheck.Check(run, path + ".delivery.maxMessagesPerHour", cap, 1, 1000);
        }

        if (delivery.RepeatSuppression is { } suppression)
        {
            RangeCheck.Check(run, path + ".delivery.repeatSuppression", suppression, TimeSpan.Zero, TimeSpan.FromHours(24));
        }

        Exclusions(run, path, rule);

        if (Tier0Catalog.IsTier0Rule(rule.Id) && (rule.Exclusions.Count > 0 || rule.Actors.ExcludeActors.Count > 0))
        {
            run.Warning(path + ".exclusions", ConfigValidationCodes.Tier0Exclusion,
                "Exclusions on a Tier-0 rule hide critical changes – they are listed in every digest.", rule.Id);
        }

        if (rule.Enabled && delivery.Immediate && !HasRecipients(run.Configuration, delivery))
        {
            run.Warning(path + ".delivery", ConfigValidationCodes.NoRecipients,
                "The rule notifies immediately, but its recipient groups have no addresses and it has no channels.", rule.Id);
        }
    }

    private static void Exclusions(ValidationRun run, string path, RuleSettings rule)
    {
        for (var j = 0; j < rule.Exclusions.Count; j++)
        {
            var exclusion = rule.Exclusions[j];
            if (exclusion is null)
            {
                continue;
            }

            var exclusionPath = ValidationRun.Item(path + ".exclusions", j);
            if (string.IsNullOrWhiteSpace(exclusion.Reason) || exclusion.Reason.Length > MaxReasonLength)
            {
                run.Error(exclusionPath + ".reason", ConfigValidationCodes.ExclusionReason,
                    $"Every exclusion needs a reason (at most {MaxReasonLength} characters).", ValidationRun.Arg(MaxReasonLength));
            }

            if (string.IsNullOrWhiteSpace(exclusion.CreatedBy))
            {
                run.Error(exclusionPath + ".createdBy", ConfigValidationCodes.ExclusionReason, "Every exclusion records who created it.");
            }
        }
    }

    private static bool HasRecipients(WatchdogConfiguration configuration, RuleDelivery delivery)
    {
        // Duplicate ids are reported separately (duplicateId): any group with the id and addresses counts.
        var hasAddresses = delivery.RecipientGroupIds.Any(id => configuration.RecipientGroups.Any(g =>
            g is not null && string.Equals(g.Id, id, StringComparison.OrdinalIgnoreCase) && g.Addresses.Count > 0));
        var hasChannels = delivery.ChannelIds.Count > 0
            || (delivery.AllEnabledChannels && configuration.Channels.Any(ch => ch is { Enabled: true }));
        return hasAddresses || hasChannels;
    }

    private static int IndexOf(IReadOnlyList<RuleSettings> rules, string id)
    {
        for (var i = 0; i < rules.Count; i++)
        {
            if (rules[i] is { } rule && string.Equals(rule.Id, id, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        return -1;
    }
}
