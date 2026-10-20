using AdMng.Watchdog.Contracts.Configuration;

namespace AdMng.Watchdog.Service.Api;

/// <summary>
/// Who created an exclusion and when (exclusion hygiene, spec 12.3 – shown in every digest) is recorded by the service,
/// never taken from the client: an exclusion that is new or whose content changed (anything but
/// <see cref="RuleExclusion.CreatedBy"/>/<see cref="RuleExclusion.CreatedAt"/>) gets the caller and the service clock; an
/// unchanged one – same rule id, same exclusion id, same content – keeps the stored values.
/// </summary>
public static class ExclusionProvenance
{
    public static WatchdogConfiguration Apply(WatchdogConfiguration incoming, WatchdogConfiguration previous, string caller, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(incoming);
        ArgumentNullException.ThrowIfNull(previous);
        ArgumentException.ThrowIfNullOrWhiteSpace(caller);
        var stored = (previous.Rules ?? [])
            .Where(r => r is not null)
            .SelectMany(r => (r.Exclusions ?? []).Where(e => e is not null).Select(e => (Rule: r.Id, Exclusion: e)))
            .GroupBy(x => (x.Rule, x.Exclusion.Id))
            .ToDictionary(g => g.Key, g => g.First().Exclusion);

        return incoming with
        {
            Rules = [.. (incoming.Rules ?? []).Select(rule => rule is null ? rule! : rule with
            {
                Exclusions = [.. (rule.Exclusions ?? []).Select(exclusion => exclusion is null
                    ? exclusion!
                    : stored.TryGetValue((rule.Id, exclusion.Id), out var before) && SameContent(before, exclusion)
                        ? exclusion with { CreatedBy = before.CreatedBy, CreatedAt = before.CreatedAt }
                        : exclusion with { CreatedBy = caller, CreatedAt = now })],
            })],
        };
    }

    private static bool SameContent(RuleExclusion a, RuleExclusion b) =>
        a with { CreatedBy = string.Empty, CreatedAt = default } == b with { CreatedBy = string.Empty, CreatedAt = default };
}
