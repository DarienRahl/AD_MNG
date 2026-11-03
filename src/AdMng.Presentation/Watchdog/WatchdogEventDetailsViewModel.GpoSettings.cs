using AdMng.Core.GroupPolicy.Content;
using AdMng.Presentation.GroupPolicy;
using AdMng.Presentation.GroupPolicy.Settings;
using AdMng.Watchdog.Contracts.Events;

namespace AdMng.Presentation.Watchdog;

/// <summary>
/// GPO change rows in the operator's language (K6, design note 20.1): the rows are filled at once with local groups and
/// well-known principals by name; when the window's session is connected to the event's domain, the domain SIDs the rows
/// name are resolved through it afterwards (one directory search per 50 SIDs) and the rows are filled again. Without a
/// session the SIDs stay. Names live in memory with the shown event only.
/// </summary>
public sealed partial class WatchdogEventDetailsViewModel
{
    /// <summary>The presenter of an event's GPO rows: item names from its <c>@name</c> rows, SIDs from <paramref name="names"/>.</summary>
    private GpoSettingPresenter GpoPresenter(WatchEvent e, IReadOnlyDictionary<string, string>? names)
    {
        var items = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var change in e.Changes.Where(c => !c.Redacted && c.Attribute.EndsWith("@name", StringComparison.OrdinalIgnoreCase)))
        {
            if (GpoSettingKey.Parse(change.Attribute) is { Area: GpoSettingArea.Preference, ItemKey: { } item }
                && (change.NewValues.FirstOrDefault() ?? change.OldValues.FirstOrDefault()) is { Length: > 0 } name)
            {
                items.TryAdd(item, name);
            }
        }

        return new GpoSettingPresenter(_services.Localizer, names) { ItemNames = items };
    }

    /// <summary>Names the domain SIDs of a GPO event's rows through the connected session (nothing without one).</summary>
    private async Task NameGpoSettingsAsync(WatchEvent e, CancellationToken token)
    {
        if (!WatchdogChangeContext.Of(e).GpoSettings || SessionForEvent() is not { } session)
        {
            return;
        }

        var sids = e.Changes.Where(c => !c.Redacted)
            .SelectMany(c => GpoSettingPresenter.Sids(c.Attribute, [.. c.OldValues, .. c.NewValues, .. c.Added, .. c.Removed]))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (sids.Count == 0)
        {
            return;
        }

        var names = await TrusteeNames.ResolveAsync(session, sids, _services.Localizer, token).ConfigureAwait(true);
        if (token.IsCancellationRequested || !ReferenceEquals(Event, e))
        {
            return;
        }

        var presenter = GpoPresenter(e, names);
        var context = WatchdogChangeContext.Of(e);
        var schema = _services.Sessions.Current?.Schema;
        Replace(Changes, e.Changes.Select(c => WatchdogChangeRowViewModel.Create(c, _services.Localizer, schema, context, presenter)));
    }
}
