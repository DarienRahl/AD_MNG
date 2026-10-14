using AdMng.Watchdog.Contracts.Api;

namespace AdMng.Watchdog.Core.Abstractions;

/// <summary>The read-only HTML event page (links in notifications).</summary>
public interface IEventHtmlRenderer
{
    /// <summary>Full HTML page of one event (same layout as the alert e-mail), language from configuration.</summary>
    string Render(EventDetails details);

    /// <summary>The page as a caller with <paramref name="level"/> may see it (section 14.1: names only for Minimal,
    /// personal data hidden for Standard). The default renders <paramref name="details"/> as given (callers filter
    /// first).</summary>
    string Render(EventDetails details, AdMng.Watchdog.Contracts.Configuration.DetailLevel level) => Render(details);
}
