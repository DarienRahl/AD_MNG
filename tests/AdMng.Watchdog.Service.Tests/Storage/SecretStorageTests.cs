using System.Text;
using AdMng.Watchdog.Contracts.Configuration;
using AdMng.Watchdog.Contracts.Events;
using AdMng.Watchdog.Core.Model;
using AdMng.Watchdog.Service.Secrets;
using AdMng.Watchdog.Service.Storage;
using AdMng.Watchdog.Service.Tests.Infrastructure;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging.Abstractions;

namespace AdMng.Watchdog.Service.Tests.Storage;

public sealed class SecretStorageTests
{
    private const string Password = "Hasło-Zażółć-Gęślą-2026!";

    private static readonly PrincipalRef Admin = new() { Sid = "S-1-5-21-1004336348-1177238915-682003330-1105", Account = @"CONTOSO\łukasz.wiśniewski" };

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Secrets_AreEncryptedAtRest_AndBoundToTheirDestination()
    {
        await using var store = TestStore.Create();

        await store.Store.SaveAsync(new WatchdogConfiguration(), 0, Admin, "initial",
            [new SecretChange(SecretNames.SmtpPassword, new SecretValue(Password), "smtp|mail.contoso.test|587|watchdog|basic")], Ct);
        await store.Store.Maintenance.CompactAsync(Ct);

        var stored = await store.Store.GetAsync(SecretNames.SmtpPassword, Ct);
        Assert.Equal(Password, stored?.Value.Reveal());
        Assert.Equal("smtp|mail.contoso.test|587|watchdog|basic", stored?.Binding);
        foreach (var path in new[] { store.Database.DatabasePath, store.Database.DatabasePath + "-wal", store.Database.DatabasePath + "-journal" })
        {
            if (!File.Exists(path))
            {
                continue;
            }

            var bytes = await ReadSharedAsync(path);
            Assert.True(bytes.AsSpan().IndexOf(Encoding.UTF8.GetBytes(Password)) < 0, $"the password must not be on disk in plain text ({Path.GetFileName(path)})");
            Assert.True(bytes.AsSpan().IndexOf(Encoding.Unicode.GetBytes(Password)) < 0, Path.GetFileName(path));
        }
    }

    /// <summary>Reads a file the store still has open: pooled SQLite connections keep it open, and on Windows a plain
    /// File.ReadAllBytes then fails with a sharing violation.</summary>
    private static async Task<byte[]> ReadSharedAsync(string path)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer, Ct);
        return buffer.ToArray();
    }

    [Fact]
    public async Task ASecretOfAnotherKeyRing_IsReportedUnreadable_WithoutItsValue()
    {
        using var directory = new TestDataDirectory();
        var original = SqliteWatchdogStore.Open(directory.Path, new SecretProtector(new EphemeralDataProtectionProvider()), TimeProvider.System, NullLogger<MigrationRunner>.Instance);
        await original.SetAsync(SecretNames.ChannelUrl("siem"), new SecretValue("https://siem.contoso.test/hooks/ab12?sig=tajne"), "channel|siem|webhook", Ct);
        await original.DisposeAsync();

        var reopened = SqliteWatchdogStore.Open(directory.Path, new SecretProtector(new EphemeralDataProtectionProvider()), TimeProvider.System, NullLogger<MigrationRunner>.Instance);
        try
        {
            var error = await Assert.ThrowsAsync<SecretUnreadableException>(() => reopened.GetAsync(SecretNames.ChannelUrl("siem"), Ct));
            Assert.Equal("channel.siem.url", error.SecretName);
            Assert.DoesNotContain("tajne", error.Message, StringComparison.Ordinal);
            Assert.Equal("channel|siem|webhook", (await reopened.ListAsync(Ct))[SecretNames.ChannelUrl("siem")]);
        }
        finally
        {
            await reopened.DisposeAsync();
        }
    }

    [Fact]
    public async Task AConfigurationThatStillCarriesASecretValue_IsRefused()
    {
        await using var store = TestStore.Create();
        var leaking = new WatchdogConfiguration { Smtp = new SmtpSettings { Password = new SecretField { NewValue = Password } } };

        await Assert.ThrowsAsync<ArgumentException>(() => store.Store.SaveAsync(leaking, 0, Admin, "initial", [], Ct));

        Assert.Null(await ((Core.Abstractions.IConfigStore)store.Store).GetAsync(Ct));
        Assert.Empty((await store.Store.GetAuditAsync(null, 10, Ct)).Items);
    }

    [Fact]
    public async Task StoredDocument_NeverContainsSecretValues_AndRoundTripsDefaults()
    {
        await using var store = TestStore.Create();
        var configuration = new WatchdogConfiguration
        {
            General = new GeneralSettings { InstanceName = "Watchdog – Oddział Gdańsk" },
            Smtp = new SmtpSettings { Host = "mail.contoso.test", Password = new SecretField { IsSet = true } },
        };

        await store.Store.SaveAsync(configuration, 0, Admin, "initial",
            [new SecretChange(SecretNames.SmtpPassword, new SecretValue(Password), "binding")], Ct);

        var json = store.Database.Read(c => SqliteValues.Scalar<string>(c, null, "SELECT json FROM config WHERE id = 1;"))!;
        Assert.DoesNotContain(Password, json, StringComparison.Ordinal);
        var loaded = await ((Core.Abstractions.IConfigStore)store.Store).GetAsync(Ct);
        Assert.Equal("Watchdog – Oddział Gdańsk", loaded?.Configuration.General.InstanceName);
        Assert.True(loaded?.Configuration.Smtp.Password.IsSet);
        Assert.Equal(TimeSpan.FromSeconds(15), loaded?.Configuration.Directory.PollInterval);
    }
}
