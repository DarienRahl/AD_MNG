using AdMng.Watchdog.Contracts.Api;
using AdMng.Watchdog.Contracts.Configuration;
using AdMng.Watchdog.Core.Retention;

namespace AdMng.Watchdog.Core.Tests.Retention;

public sealed class StorageHealthTests
{
    private const long Mb = 1024 * 1024;

    private static readonly DateTimeOffset Now = new(2026, 10, 14, 8, 0, 0, TimeSpan.Zero);

    private static readonly RetentionSettings Retention = new() { MaxDatabaseMb = 1000 };

    [Theory]
    [InlineData(100, HealthState.Ok)]
    [InlineData(799, HealthState.Ok)]
    [InlineData(800, HealthState.Warning)]
    [InlineData(1000, HealthState.Warning)]
    [InlineData(1001, HealthState.Error)]
    public void Size_IsWarningFrom80PercentAndErrorAboveTheCap(long databaseMb, HealthState expected)
    {
        var check = StorageHealth.SizeCheck(Status(databaseMb * Mb, 50_000 * Mb), Retention, Now);

        Assert.Equal((StorageHealth.SizeCode, expected, Now), (check.Code, check.State, check.CheckedAt));
        Assert.Equal([databaseMb.ToString(System.Globalization.CultureInfo.InvariantCulture), "1000"], check.Args);
        Assert.Equal(expected == HealthState.Error, StorageHealth.IsOverCap(Status(databaseMb * Mb, 0), Retention));
    }

    [Theory]
    [InlineData(2048, HealthState.Ok)]
    [InlineData(1024, HealthState.Ok)]
    [InlineData(1023, HealthState.Error)]
    public void FreeSpace_BelowOneGiB_IsAnError(long freeMb, HealthState expected) =>
        Assert.Equal(expected, StorageHealth.FreeCheck(Status(1, freeMb * Mb), Now).State);

    [Fact]
    public void Limit_IsAtLeastOneMegabyte() =>
        Assert.Equal(Mb, StorageHealth.LimitBytes(new RetentionSettings { MaxDatabaseMb = 0 }));

    private static StorageStatusDto Status(long databaseBytes, long freeBytes) =>
        new() { DataDirectory = @"C:\ProgramData\AdMng\Watchdog", DatabaseBytes = databaseBytes, FreeBytes = freeBytes };
}
