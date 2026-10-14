using AdMng.Core.Ldap;
using AdMng.Watchdog.Contracts.Events;

namespace AdMng.Watchdog.IntegrationTests.Infrastructure;

/// <summary>Assertions over the events of one step (messages list what was found – kinds and DNs of test objects only).</summary>
internal static class EventAssert
{
    /// <summary>The only event of <paramref name="kind"/> about <paramref name="dn"/>.</summary>
    public static WatchEvent Single(IReadOnlyList<WatchEvent> events, WatchEventKind kind, string dn)
    {
        var matches = Of(events, dn).Where(e => e.Kind == kind).ToList();
        Assert.True(matches.Count == 1, $"expected one {kind} for {dn}, found {matches.Count}: {Describe(events)}");
        return matches[0];
    }

    /// <summary>No event at all about <paramref name="dn"/>.</summary>
    public static void None(IReadOnlyList<WatchEvent> events, string dn)
    {
        var matches = Of(events, dn).ToList();
        Assert.True(matches.Count == 0, $"expected no event for {dn}: {Describe(matches)}");
    }

    /// <summary>The events about <paramref name="dn"/> (object, previous DN or member).</summary>
    public static IEnumerable<WatchEvent> Of(IReadOnlyList<WatchEvent> events, string dn) =>
        events.Where(e => DistinguishedName.AreEqual(e.Object.Dn, dn) || DistinguishedName.AreEqual(e.PreviousDn, dn));

    /// <summary>The change of one attribute in an event.</summary>
    public static AttributeChange Change(WatchEvent watchEvent, string attribute)
    {
        var change = watchEvent.Changes.FirstOrDefault(c => string.Equals(c.Attribute, attribute, StringComparison.OrdinalIgnoreCase));
        Assert.True(change is not null, $"{watchEvent.Kind} has no change of {attribute}: {string.Join(", ", watchEvent.Changes.Select(c => c.Attribute))}");
        return change;
    }

    public static string Describe(IEnumerable<WatchEvent> events) =>
        string.Join("; ", events.Select(e => $"#{e.Id} {e.Kind} {e.Object.Dn}"));
}
