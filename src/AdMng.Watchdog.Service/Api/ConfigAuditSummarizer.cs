using AdMng.Watchdog.Core.Configuration;
using AdMng.Watchdog.Core.Model;

namespace AdMng.Watchdog.Service.Api;

/// <summary>The <c>config_audit</c> summary of a save (spec 17.2): the auditor's change summary plus which secrets were
/// replaced or removed – by name only, never a value.</summary>
public static class ConfigAuditSummarizer
{
    public const int MaxLength = 4000;

    public static string Summarize(ConfigChangeAudit audit, IReadOnlyList<SecretChange> secretChanges)
    {
        ArgumentNullException.ThrowIfNull(audit);
        ArgumentNullException.ThrowIfNull(secretChanges);
        var parts = new List<string>();
        if (audit.HasChanges || secretChanges.Count == 0)
        {
            parts.Add(audit.Summary);
        }

        var replaced = secretChanges.Where(c => c.Value is not null).Select(c => c.Name).Order(StringComparer.Ordinal).ToList();
        var removed = secretChanges.Where(c => c.Value is null).Select(c => c.Name).Order(StringComparer.Ordinal).ToList();
        if (replaced.Count > 0)
        {
            parts.Add("secrets replaced: " + string.Join(", ", replaced));
        }

        if (removed.Count > 0)
        {
            parts.Add("secrets removed: " + string.Join(", ", removed));
        }

        var summary = string.Join("; ", parts);
        return summary.Length <= MaxLength ? summary : summary[..(MaxLength - 1)] + "…";
    }
}
