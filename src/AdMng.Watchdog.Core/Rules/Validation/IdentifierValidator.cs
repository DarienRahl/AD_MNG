using System.Text.RegularExpressions;

namespace AdMng.Watchdog.Core.Rules.Validation;

/// <summary><c>invalidId</c>, <c>duplicateId</c> and <c>unknownReference</c> (8.3); null list entries and missing names
/// are <c>required</c>.</summary>
internal static partial class IdentifierValidator
{
    /// <summary>Reserved channel id: the SMTP channel of notification targets (<c>NotificationTarget.ChannelId</c>).</summary>
    public const string ReservedEmailChannelId = "email";

    public static void Validate(ValidationRun run)
    {
        var c = run.Configuration;
        var groupIds = Unique(run, "recipientGroups", c.RecipientGroups.Select(g => g?.Id).ToList(), GroupOrChannelId(), 40);
        var channelIds = Unique(run, "channels", c.Channels.Select(ch => ch?.Id).ToList(), GroupOrChannelId(), 40);
        Unique(run, "rules", c.Rules.Select(r => r?.Id).ToList(), RuleId(), 60);

        for (var i = 0; i < c.Channels.Count; i++)
        {
            var channel = c.Channels[i];
            var path = ValidationRun.Item("channels", i);
            if (channel is null)
            {
                continue;
            }

            if (string.Equals(channel.Id, ReservedEmailChannelId, StringComparison.OrdinalIgnoreCase))
            {
                run.Error(path + ".id", ConfigValidationCodes.InvalidId, "The channel id \"email\" is reserved.");
            }

            RequiredName(run, path, channel.Name);
        }

        for (var i = 0; i < c.RecipientGroups.Count; i++)
        {
            if (c.RecipientGroups[i] is { } group)
            {
                RequiredName(run, ValidationRun.Item("recipientGroups", i), group.Name);
            }
        }

        for (var i = 0; i < c.Rules.Count; i++)
        {
            var rule = c.Rules[i];
            if (rule is null)
            {
                continue;
            }

            var path = ValidationRun.Item("rules", i);
            RequiredName(run, path, rule.Name);
            Unique(run, path + ".exclusions", rule.Exclusions.Select(e => e?.Id).ToList(), RuleId(), 60);
            References(run, path + ".delivery.recipientGroupIds", rule.Delivery.RecipientGroupIds, groupIds, "recipient group");
            References(run, path + ".delivery.channelIds", rule.Delivery.ChannelIds, channelIds, "channel");
        }

        References(run, "digest.recipientGroupIds", c.Digest.RecipientGroupIds, groupIds, "recipient group");
        References(run, "digest.channelIds", c.Digest.ChannelIds, channelIds, "channel");
    }

    private static HashSet<string> Unique(ValidationRun run, string listPath, IReadOnlyList<string?> ids, Regex syntax, int maxLength)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < ids.Count; i++)
        {
            var path = ValidationRun.Item(listPath, i);
            var id = ids[i];
            if (id is null)
            {
                run.Error(path, ConfigValidationCodes.Required, "The entry is missing or has no id.");
                continue;
            }

            if (!syntax.IsMatch(id))
            {
                run.Error(path + ".id", ConfigValidationCodes.InvalidId,
                    $"Ids use lower-case letters, digits and '-' (1–{maxLength} characters, starting with a letter or digit).",
                    ValidationRun.Arg(maxLength));
            }

            if (!seen.Add(id))
            {
                run.Error(path + ".id", ConfigValidationCodes.DuplicateId, "The id is used more than once.");
            }
        }

        return seen;
    }

    private static void References(ValidationRun run, string listPath, IReadOnlyList<string> ids, HashSet<string> known, string what)
    {
        for (var i = 0; i < ids.Count; i++)
        {
            if (ids[i] is null || !known.Contains(ids[i]))
            {
                run.Error(ValidationRun.Item(listPath, i), ConfigValidationCodes.UnknownReference, $"No {what} with this id exists.");
            }
        }
    }

    private static void RequiredName(ValidationRun run, string path, string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            run.Error(path + ".name", ConfigValidationCodes.Required, "The name is required.");
        }
    }

    [GeneratedRegex("^[a-z0-9][a-z0-9-]{0,59}\\z", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex RuleId();

    [GeneratedRegex("^[a-z0-9][a-z0-9-]{0,39}\\z", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex GroupOrChannelId();
}
