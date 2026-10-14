using System.Globalization;
using AdMng.Core.Directory;
using AdMng.Testing;
using AdMng.Watchdog.Contracts.Configuration;
using AdMng.Watchdog.Contracts.Events;
using AdMng.Watchdog.Core;
using AdMng.Watchdog.Core.Abstractions;
using AdMng.Watchdog.Core.Detection;
using AdMng.Watchdog.Core.Model;
using AdMng.Watchdog.Core.Scans;
using AdMng.Watchdog.Testing.Stores;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace AdMng.Watchdog.Testing.Scenarios;

/// <summary>
/// A detection scenario: the sample company (<see cref="FakeDirectoryBuilder.AddSampleCompany"/>) with the watchdog
/// infrastructure in a <see cref="FakeDirectorySession"/>, a <see cref="FakeTimeProvider"/>, the scenario store and the
/// recording fakes, wired with <c>AddWatchdogCore()</c> (fakes registered after it, so they win). Methods use
/// <see cref="TestContext.Current"/>'s cancellation token.
/// </summary>
public sealed class DetectionScenario : IAsyncDisposable
{
    /// <summary>The start time of every scenario (Wednesday 14 Oct 2026, 08:00 UTC = 10:00 in Warsaw).</summary>
    public static readonly DateTimeOffset Start = new(2026, 10, 14, 8, 0, 0, TimeSpan.Zero);

    private readonly ServiceProvider _provider;
    private long _cursor;

    /// <summary>Creates the scenario.</summary>
    /// <param name="seed">Extra objects seeded before the engine starts (after the sample company).</param>
    /// <param name="configuration">The configuration (default: <c>new WatchdogConfiguration()</c>).</param>
    /// <param name="services">Extra registrations after the fakes (e.g. an <see cref="ISecurityEventSource"/>).</param>
    public DetectionScenario(
        Action<FakeDirectoryBuilder>? seed = null,
        WatchdogConfiguration? configuration = null,
        Action<IServiceCollection>? services = null)
    {
        Time = new FakeTimeProvider(Start);
        Builder = new FakeDirectoryBuilder(timeProvider: Time);
        Builder.AddWatchdogInfrastructure();
        Company = Builder.AddSampleCompany();
        seed?.Invoke(Builder);
        Store = new ScenarioDetectionStore(Time);
        Sessions = new ScenarioSessionProvider(Directory);
        Config = new ScenarioConfigProvider(configuration);
        Health = new ScenarioHealthRegistry();
        Signals = new ScenarioSignals();

        var collection = new ServiceCollection();
        collection.AddSingleton<TimeProvider>(Time);
        collection.AddWatchdogCore();
        collection.AddSingleton<IDetectionStore>(Store);
        collection.AddSingleton<IEventStore>(Store);
        collection.AddSingleton<IExpiryNoticeStore>(Store);
        collection.AddSingleton<IDirectorySessionProvider>(Sessions);
        collection.AddSingleton<IWatchdogConfigProvider>(Config);
        collection.AddSingleton<IHealthRegistry>(Health);
        collection.AddSingleton<IWatchdogSignals>(Signals);
        services?.Invoke(collection);
        AddRestOfTheHost(collection, Time);
        _provider = collection.BuildServiceProvider();
    }

    public FakeTimeProvider Time { get; }

    /// <summary>
    /// What the service host registers besides the detection fakes, so every <see cref="IWatchdogJob"/> of
    /// <c>AddWatchdogCore()</c> can be resolved: logging, the other stores (an <see cref="InMemoryWatchdogStore"/>) and an
    /// unavailable Security-log source (the Linux host's). TryAdd – registrations of the test win.
    /// </summary>
    private static void AddRestOfTheHost(IServiceCollection collection, TimeProvider time)
    {
        collection.AddLogging();
        var store = new InMemoryWatchdogStore(time);
        collection.TryAddSingleton<INotificationQueueStore>(store);
        collection.TryAddSingleton<IOutboxStore>(store);
        collection.TryAddSingleton<ISecurityEventStore>(store);
        collection.TryAddSingleton<IDigestRunStore>(store);
        collection.TryAddSingleton<IConfigStore>(store);
        collection.TryAddSingleton<ISecretStore>(store);
        collection.TryAddSingleton<IStoreMaintenance>(store);
        collection.TryAddSingleton<ISecurityEventSource>(new ScenarioSecurityEventSource { IsAvailable = false });
    }

    public FakeDirectoryBuilder Builder { get; }

    /// <summary>The fake directory (the PDC unless <see cref="Sessions"/> was switched).</summary>
    public FakeDirectorySession Directory => Builder.Session;

    public SampleCompany Company { get; }

    public ScenarioDetectionStore Store { get; }

    public ScenarioSessionProvider Sessions { get; }

    public ScenarioConfigProvider Config { get; }

    public ScenarioHealthRegistry Health { get; }

    public ScenarioSignals Signals { get; }

    public IServiceProvider Services => _provider;

    public ChangeDetectionEngine Engine => _provider.GetRequiredService<ChangeDetectionEngine>();

    public ScheduledScanJob Scans => _provider.GetRequiredService<ScheduledScanJob>();

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>Runs one detection cycle.</summary>
    public Task<DetectionCycleResult> RunCycleAsync() => Engine.RunCycleAsync(Token);

    /// <summary>Runs <see cref="ChangeDetectionEngine.RunOnceAsync"/> (job semantics: failures become results).</summary>
    public Task<JobResult> RunJobAsync() => Engine.RunOnceAsync(Token);

    /// <summary>Runs the baseline and forgets its events (HealthBaselineCompleted).</summary>
    public async Task<DetectionCycleResult> BaselineAsync()
    {
        var result = await RunCycleAsync().ConfigureAwait(false);
        Assert.Equal(DetectionMode.Baseline, result.Mode);
        TakeEvents();
        return result;
    }

    /// <summary>Runs one cycle and returns the events stored since the previous call.</summary>
    public async Task<IReadOnlyList<WatchEvent>> CycleEventsAsync()
    {
        await RunCycleAsync().ConfigureAwait(false);
        return TakeEvents();
    }

    /// <summary>Advances the settle window and runs cycles until the pending creations are emitted.</summary>
    public async Task<IReadOnlyList<WatchEvent>> SettleAsync()
    {
        var events = new List<WatchEvent>(await CycleEventsAsync().ConfigureAwait(false));
        Advance(Config.Configuration.Directory.CreationSettleWindow + TimeSpan.FromSeconds(1));
        events.AddRange(await CycleEventsAsync().ConfigureAwait(false));
        return events;
    }

    /// <summary>Runs one scheduled scan iteration and returns the events stored since the previous call.</summary>
    public async Task<IReadOnlyList<WatchEvent>> ScanEventsAsync()
    {
        await Scans.RunOnceAsync(Token).ConfigureAwait(false);
        return TakeEvents();
    }

    /// <summary>Events stored since the previous call (by id).</summary>
    public IReadOnlyList<WatchEvent> TakeEvents()
    {
        var events = Store.Events.Where(e => e.Id > _cursor).ToList();
        if (events.Count > 0)
        {
            _cursor = events[^1].Id;
        }

        return events;
    }

    /// <summary>Advances the clock.</summary>
    public void Advance(TimeSpan delta) => Time.Advance(delta);

    /// <summary>Replace modification through the LDAP write path (USN bump, metadata).</summary>
    public Task ReplaceAsync(string dn, string attribute, params object[] values) =>
        Directory.ModifyAsync(dn, [AttributeModification.Replace(attribute, Wire(values))], null, Token);

    /// <summary>Clears an attribute.</summary>
    public Task ClearAsync(string dn, string attribute) =>
        Directory.ModifyAsync(dn, [AttributeModification.Clear(attribute)], null, Token);

    /// <summary>Adds values (e.g. members).</summary>
    public Task AddValuesAsync(string dn, string attribute, params object[] values) =>
        Directory.ModifyAsync(dn, [AttributeModification.Add(attribute, Wire(values))], null, Token);

    /// <summary>Removes values (e.g. members).</summary>
    public Task RemoveValuesAsync(string dn, string attribute, params object[] values) =>
        Directory.ModifyAsync(dn, [AttributeModification.Delete(attribute, Wire(values))], null, Token);

    /// <summary>Deletes an object (tombstone).</summary>
    public Task DeleteAsync(string dn, bool subtree = false) => Directory.DeleteAsync(dn, subtree, Token);

    /// <summary>Moves and/or renames an object; returns the new DN.</summary>
    public Task<string> MoveAsync(string dn, string? newParentDn, string? newRdn = null) => Directory.MoveAsync(dn, newParentDn, newRdn, Token);

    /// <summary>Restores a deleted object.</summary>
    public Task RestoreAsync(string deletedDn, string targetDn) => Directory.RestoreDeletedAsync(deletedDn, targetDn, Token);

    /// <summary>Sets a password (unicodePwd + pwdLastSet).</summary>
    public Task ResetPasswordAsync(string dn) => Directory.ResetPasswordAsync(dn, "Zażółć!Gęślą2026", Token);

    /// <summary>The snapshot row of <paramref name="dn"/> (live rows only).</summary>
    public SnapshotObject? Snapshot(string dn) => Store.FindByDnAsync(dn, Token).GetAwaiter().GetResult();

    public ValueTask DisposeAsync() => _provider.DisposeAsync();

    /// <summary>LDAP wire values: numbers as invariant strings, strings and byte arrays unchanged.</summary>
    private static object[] Wire(object[] values) =>
        [.. values.Select(v => v is IFormattable number and not DateTimeOffset ? number.ToString(null, CultureInfo.InvariantCulture) : v)];
}
