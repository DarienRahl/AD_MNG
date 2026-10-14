using AdMng.Watchdog.Contracts.Events;
using AdMng.Watchdog.Core.Model;
using Xunit;

namespace AdMng.Watchdog.Testing.Stores;

// IExpiryNoticeStore + IDigestRunStore
public abstract partial class WatchdogStoreContractTests<TStore>
{
    [Fact]
    public Task ExpiryNotices_AreRecordedOncePerObjectKindExpiryAndThreshold() => WithStoreAsync(async store =>
    {
        var guid = Snapshot().ObjectGuid;
        var expires = Now.AddDays(7).ToFileTime();

        Assert.True(await store.TryRecordAsync(new ExpiryNotice(guid, ExpiryKind.Account, expires, 7, Now), Ct));
        Assert.False(await store.TryRecordAsync(new ExpiryNotice(guid, ExpiryKind.Account, expires, 7, Now.AddHours(1)), Ct));
        Assert.True(await store.TryRecordAsync(new ExpiryNotice(guid, ExpiryKind.Account, expires, 1, Now), Ct));
        Assert.True(await store.TryRecordAsync(new ExpiryNotice(guid, ExpiryKind.Password, expires, 7, Now), Ct));
        Assert.True(await store.TryRecordAsync(new ExpiryNotice(guid, ExpiryKind.Account, Now.AddDays(30).ToFileTime(), 7, Now), Ct));
    });

    [Fact]
    public Task UpcomingExpiries_AreReplacedPerKind_AndListedByExpiry() => WithStoreAsync(async store =>
    {
        var zofia = Upcoming("CN=Zofia Żak,OU=Księgowość,DC=contoso,DC=test", ExpiryKind.Account, Now.AddDays(5));
        var jan = Upcoming("CN=Jan Kowalski,OU=Sprzedaż,DC=contoso,DC=test", ExpiryKind.Account, Now.AddDays(2));
        var service = Upcoming("CN=svc-kopie,OU=Serwisowe,DC=contoso,DC=test", ExpiryKind.Password, Now.AddDays(3)) with { Tags = [EventTags.ServiceAccount] };
        await store.ReplaceUpcomingAsync(ExpiryKind.Account, [zofia, jan], Ct);
        await store.ReplaceUpcomingAsync(ExpiryKind.Password, [service], Ct);

        var all = await store.GetUpcomingAsync(Ct);
        Assert.Equal([jan.Object.Dn, service.Object.Dn, zofia.Object.Dn], all.Select(u => u.Object.Dn));
        Assert.Equal([EventTags.ServiceAccount], all[1].Tags);
        Assert.Equal(ExpiryKind.Password, all[1].Kind);
        Assert.Equal(service.Object.ObjectGuid, all[1].Object.ObjectGuid);
        Assert.Equal(service.Object.SamAccountName, all[1].Object.SamAccountName);

        await store.ReplaceUpcomingAsync(ExpiryKind.Account, [zofia], Ct);

        Assert.Equal([service.Object.Dn, zofia.Object.Dn], (await store.GetUpcomingAsync(Ct)).Select(u => u.Object.Dn));
        await store.ReplaceUpcomingAsync(ExpiryKind.Password, [], Ct);
        Assert.Equal([zofia.Object.Dn], (await store.GetUpcomingAsync(Ct)).Select(u => u.Object.Dn));
    });

    [Fact]
    public Task DigestRuns_ReportTheEndOfTheLastPeriod() => WithStoreAsync(async store =>
    {
        Assert.Null(await store.GetLastPeriodEndAsync(Ct));

        await store.RecordAsync(Now.AddDays(-2), Now.AddDays(-1), 71, [1, 2], Ct);
        await store.RecordAsync(Now.AddDays(-1), Now, 0, [], Ct);

        Assert.Equal(Now, await store.GetLastPeriodEndAsync(Ct));
    });

    private static UpcomingExpiry Upcoming(string dn, ExpiryKind kind, DateTimeOffset expiresAt)
    {
        var snapshot = Snapshot(dn);
        return new UpcomingExpiry { Object = snapshot.ToRef(), Kind = kind, ExpiresAt = expiresAt };
    }
}
