using AdMng.Watchdog.Contracts.Api;

namespace AdMng.Watchdog.Core.Abstractions;

/// <summary>The read-only HTML event page (links in notifications).</summary>
public interface IEventHtmlRenderer
{
    /// <summary>Full HTML page of one event (same layout as the alert e-mail), language from configuration.</summary>
    string Render(EventDetails details);
}
