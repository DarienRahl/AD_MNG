using AdMng.Watchdog.Service.Hosting;
using AdMng.Watchdog.Service.Tests.Fakes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;

namespace AdMng.Watchdog.Service.Tests.Hosting;

/// <summary>
/// The Application log is shared with every other application on the server (default 20 MB): while a DC is down the
/// jobs log the same warning every cycle, so the same entry is written at most once per window – with the number of
/// suppressed repeats on the next one – while distinct entries and Critical ones always pass.
/// </summary>
public sealed class ThrottlingLoggerProviderTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 17, 18, 0, 0, TimeSpan.Zero);

    [Fact]
    public void TheSameWarning_IsWrittenOncePerWindow_WithTheRepeatCount()
    {
        var clock = new FakeTimeProvider(Start);
        var sink = new TestLog();
        using var provider = new ThrottlingLoggerProvider(sink, clock);
        var logger = provider.CreateLogger("AdMng.Watchdog.Core.Detection.ChangeDetectionEngine");

        for (var i = 0; i < 240; i++)   // one hour at the 15-second poll interval
        {
            logger.LogWarning(new EventId(1001), "Directory unavailable on {Server}: {Code}.", "dc01.contoso.test", "ServerDown");
            clock.Advance(TimeSpan.FromSeconds(15));
        }

        var entries = sink.Entries;
        Assert.Equal(TimeSpan.FromMinutes(15), ThrottlingLoggerProvider.Window);
        Assert.Equal(4, entries.Count);   // at 0, 15, 30 and 45 minutes
        Assert.DoesNotContain("repeated", entries[0].Message, StringComparison.Ordinal);
        Assert.Contains("repeated 59 times", entries[1].Message, StringComparison.Ordinal);
        Assert.All(entries, e => Assert.Equal(1001, e.EventId.Id));
    }

    [Fact]
    public void DistinctEntries_Critical_AndInformation_AlwaysPass()
    {
        var clock = new FakeTimeProvider(Start);
        var sink = new TestLog();
        using var provider = new ThrottlingLoggerProvider(sink, clock);
        var logger = provider.CreateLogger("AdMng.Watchdog.Service.Hosting.JobHost");

        logger.LogWarning(new EventId(2300), "Job {Job} iteration failed: {Code}.", "detection", "ServerDown");
        logger.LogWarning(new EventId(2300), "Job {Job} iteration failed: {Code}.", "scans", "ServerDown");
        logger.LogError(new EventId(1002), "Delivery failing on {Channel}.", "teams-łódź");
        logger.LogCritical(new EventId(1010), "Fatal: {Code}.", "Timeout");
        logger.LogCritical(new EventId(1010), "Fatal: {Code}.", "Timeout");
        logger.LogInformation(new EventId(2600), "Event {Id} viewed by {Account}.", 7, @"CONTOSO\łucja.nowak");
        logger.LogInformation(new EventId(2600), "Event {Id} viewed by {Account}.", 7, @"CONTOSO\łucja.nowak");

        Assert.Equal(7, sink.Entries.Count);
    }

    /// <summary>Warning 2002 answers an administrator's edit of the bootstrap file: a second malformed edit within the
    /// window (the same text) must reach the log too – otherwise the latest entry predates the edit and silence reads as
    /// "the edit was loaded".</summary>
    [Fact]
    public void ABootstrapEditThatCannotBeLoaded_IsNeverSuppressed()
    {
        var clock = new FakeTimeProvider(Start);
        var sink = new TestLog();
        using var provider = new ThrottlingLoggerProvider(sink, clock);
        var logger = provider.CreateLogger(typeof(ConfigurationSetup).FullName!);

        logger.LogWarning(new EventId(ConfigurationSetup.ReloadFailedEventId), "The edited bootstrap file could not be loaded ({ErrorCode}).", "InvalidDataException");
        clock.Advance(TimeSpan.FromMinutes(5));
        logger.LogWarning(new EventId(ConfigurationSetup.ReloadFailedEventId), "The edited bootstrap file could not be loaded ({ErrorCode}).", "InvalidDataException");

        Assert.Equal(2, sink.Entries.Count(e => e.EventId.Id == ConfigurationSetup.ReloadFailedEventId));
        Assert.All(sink.Entries, e => Assert.DoesNotContain("repeated", e.Message, StringComparison.Ordinal));
    }
}
