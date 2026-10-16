using System.ComponentModel;
using AdMng.Watchdog.Contracts.Api;

namespace AdMng.Presentation.Watchdog;

/// <summary>
/// Validation messages of one editor by field: the indexer takes the JSON path relative to the editor
/// (<c>Errors[host]</c>, <c>Errors[oAuth.tenantId]</c>, <c>Errors[delivery.channelIds]</c> – list items count for their
/// list), so a view binds every input to the issue the service reported for it.
/// </summary>
public sealed class WatchdogFieldErrors : INotifyPropertyChanged
{
    private Dictionary<string, string> _errors = new(StringComparer.OrdinalIgnoreCase);

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>The message for <paramref name="field"/> (also the first one of its items, e.g. "url" for "url.newValue"), or null.</summary>
    public string? this[string field] => _errors.TryGetValue(field, out var message) ? message : null;

    /// <summary>Every message (field → text).</summary>
    public IReadOnlyDictionary<string, string> All => _errors;

    public bool HasErrors => _errors.Count > 0;

    /// <summary>Takes the issues under <paramref name="prefix"/> ("smtp.", "rules[3].", "channels[0].").</summary>
    public void Set(IEnumerable<(ConfigIssue Issue, string Text)> issues, string prefix)
    {
        ArgumentNullException.ThrowIfNull(issues);
        ArgumentNullException.ThrowIfNull(prefix);
        var errors = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (issue, text) in issues)
        {
            if (!issue.Path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var relative = issue.Path[prefix.Length..];
            foreach (var key in Keys(relative))
            {
                errors.TryAdd(key, text);
            }
        }

        _errors = errors;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item[]"));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasErrors)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(All)));
    }

    public void Clear() => Set([], string.Empty);

    /// <summary>"delivery.channelIds[0]" → "delivery.channelIds[0]", "delivery.channelIds", "delivery".</summary>
    private static IEnumerable<string> Keys(string path)
    {
        var current = path;
        while (current.Length > 0)
        {
            yield return current;
            var bracket = current.EndsWith(']') ? current.LastIndexOf('[') : -1;
            if (bracket > 0)
            {
                current = current[..bracket];
                continue;
            }

            var dot = current.LastIndexOf('.');
            if (dot <= 0)
            {
                yield break;
            }

            current = current[..dot];
        }
    }
}
