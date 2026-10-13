using System.Diagnostics;
using AdMng.Core.Directory;
using AdMng.Directory.IntegrationTests.Infrastructure;
using Microsoft.Extensions.Logging;

namespace AdMng.Directory.IntegrationTests.Integration;

[Collection(TimingCollection.Name)]
public sealed class TimingTests
{
    [Fact]
    public async Task Connect_IsFastAndLogsWithoutDirectoryData()
    {
        LabSettings.RequireLab();
        var logs = new CapturingLoggerFactory();
        var stopwatch = Stopwatch.StartNew();
        await using var session = await LabFixture.ConnectAsync(
            LabSettings.Plain(LabSettings.AdminUser),
            LabSettings.AdminPassword,
            logs,
            cancellationToken: TestContext.Current.CancellationToken);
        stopwatch.Stop();
        TestContext.Current.TestOutputHelper?.WriteLine($"Connect (bind + RootDSE + schema + identity) took {stopwatch.ElapsedMilliseconds} ms.");

        // Target on a LAN is well below 1 s; the lab container is slower and shared.
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(3), $"Connect took {stopwatch.Elapsed}.");
        Assert.True(session.Schema.Attributes.Count > 1000);
        Assert.Contains(logs.Records, r => r.EventId.Id == 1001 && r.Level == LogLevel.Information);

        // Information and above: no DNs, no passwords.
        foreach (var record in logs.Records.Where(r => r.Level >= LogLevel.Information))
        {
            Assert.DoesNotContain("DC=", record.Message, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(LabSettings.AdminPassword, record.Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task PagedSearch_ThroughputIsNotPollingBound()
    {
        LabSettings.RequireLab();
        var ct = TestContext.Current.CancellationToken;
        await using var session = await LabFixture.ConnectAsync(LabSettings.Plain(LabSettings.AdminUser), LabSettings.AdminPassword, cancellationToken: ct);
        var search = new DirectorySearch
        {
            BaseDn = LabSettings.CompanyOu,
            Filter = "(objectClass=*)",
            Attributes = [Core.ActiveDirectory.Attr.Name, Core.ActiveDirectory.Attr.ObjectClass],
            PageSize = 1000,
        };

        var stopwatch = Stopwatch.StartNew();
        var count = 0;
        await foreach (var _ in session.SearchAsync(search, ct))
        {
            count++;
        }

        TestContext.Current.TestOutputHelper?.WriteLine($"{count} objects in {stopwatch.ElapsedMilliseconds} ms.");
        Assert.True(count > 700);

        // One LDAP message per 100 ms poll (S.DS.P async path on OpenLDAP) would need well over a minute.
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(10), $"Search took {stopwatch.Elapsed}.");
    }

    [Fact]
    public async Task ConcurrentPagedSearches_OnASmallPool_AreCompleteAndFast()
    {
        LabSettings.RequireLab();
        var ct = TestContext.Current.CancellationToken;
        var options = LabSettings.Plain(LabSettings.AdminUser) with { MaxConnections = 1 };
        await using var session = await LabFixture.ConnectAsync(options, LabSettings.AdminPassword, cancellationToken: ct);
        var search = new DirectorySearch
        {
            BaseDn = LabSettings.CompanyOu,
            Filter = "(&(objectCategory=person)(objectClass=user))",
            PageSize = 20,
        };
        var expected = await CountAsync(session, search, ct);
        var stopwatch = Stopwatch.StartNew();

        // More concurrent paged searches than result sets allowed per connection (5) and per server (10).
        var counts = await Task.WhenAll(Enumerable.Range(0, 12).Select(async _ =>
        {
            await Task.Yield();
            return await CountAsync(session, search, ct);
        }));

        TestContext.Current.TestOutputHelper?.WriteLine($"12 x {expected} entries in {stopwatch.ElapsedMilliseconds} ms.");
        Assert.All(counts, c => Assert.Equal(expected, c));
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(20), $"Searches took {stopwatch.Elapsed}.");
    }

    private static async Task<int> CountAsync(IDirectorySession session, DirectorySearch search, CancellationToken ct)
    {
        var count = 0;
        await foreach (var _ in session.SearchAsync(search, ct))
        {
            count++;
        }

        return count;
    }
}
