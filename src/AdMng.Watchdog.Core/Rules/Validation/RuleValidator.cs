using AdMng.Watchdog.Contracts.Configuration;

namespace AdMng.Watchdog.Core.Rules.Validation;

/// <summary>
/// Rules (8.3, 12.3): <c>lockedRule</c> (the shipped locked rules exist, stay locked and enabled and keep their kinds;
/// any locked rule stays enabled), <c>exclusionReason</c>, the delivery ranges, and the warnings <c>tier0Exclusion</c>,
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
        }
    }

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
