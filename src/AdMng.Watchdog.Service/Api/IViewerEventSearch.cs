using AdMng.Watchdog.Contracts.Api;

namespace AdMng.Watchdog.Service.Api;

/// <summary>
/// The event query of Minimal viewers (spec 14.1, 17.2): like <c>IEventStore.QueryAsync</c>, but the text filter does
/// not match workstation and IP – fields hidden from them, so a hit or miss must not reveal them. Implemented by the
/// SQLite store; without it the API refuses a text search of Minimal viewers.
/// </summary>
public interface IViewerEventSearch
{
    Task<EventPage> QueryWithoutSourcesAsync(EventQuery query, CancellationToken cancellationToken);
}
