using AdMng.Watchdog.Contracts.Configuration;
using AdMng.Watchdog.Contracts.Events;
using AdMng.Watchdog.Core.Abstractions;
using AdMng.Watchdog.Core.Model;
using Xunit;

namespace AdMng.Watchdog.Testing.Stores;

// IConfigStore + ISecretStore
public abstract partial class WatchdogStoreContractTests<TStore>
{
    private static readonly PrincipalRef Admin = new()
    {
        Sid = "S-1-5-21-1004336348-1177238915-682003330-1105",
        Account = @"CONTOSO\łukasz.wiśniewski",
    };

    [Fact]
    public Task Config_FirstSaveIsVersionOne_AndAStaleVersionConflicts() => WithStoreAsync(async store =>
    {
        var saved = await store.SaveAsync(new WatchdogConfiguration(), 0, Admin, "initial", [], Ct);

        Assert.Equal(1, saved.Version);
        Assert.Equal(1, (await ((IConfigStore)store).GetAsync(Ct))?.Version);
        var conflict = await Assert.ThrowsAsync<ConfigVersionConflictException>(
            () => store.SaveAsync(new WatchdogConfiguration(), 0, Admin, "stale", [], Ct));
        Assert.Equal(0, conflict.Expected);
        Assert.Equal(1, conflict.Actual);
    });

    [Fact]
    public Task Config_Save_StoresTheDocumentWithVersionTimeAndAuthor() => WithStoreAsync(async store =>
    {
        await store.SaveAsync(new WatchdogConfiguration(), 0, Admin, "initial", [], Ct);
        Clock.Advance(TimeSpan.FromMinutes(5));

        var saved = await store.SaveAsync(SampleConfiguration(), 1, Admin, "rules: lockouts added", [], Ct);
        var loaded = await ((IConfigStore)store).GetAsync(Ct);

        Assert.Equal(2, saved.Version);
        Assert.NotNull(loaded);
        Assert.Equal(2, loaded.Version);
        Assert.Equal(Now.AddMinutes(5), loaded.UpdatedAt);
        Assert.Equal(@"CONTOSO\łukasz.wiśniewski", loaded.UpdatedBy);
        Assert.False(loaded.IsRedacted);
        AssertSameJson(SampleConfiguration(), loaded.Configuration);
        Assert.Equal("Pomoc techniczna", loaded.Configuration.RecipientGroups[1].Name);
        Assert.Equal(TimeSpan.FromHours(1), loaded.Configuration.Rules[0].Delivery.RepeatSuppression);
    });

    [Fact]
    public Task Config_SaveAppliesSecretChanges_AndAConflictLeavesEverythingUntouched() => WithStoreAsync(async store =>
    {
        await store.SaveAsync(new WatchdogConfiguration(), 0, Admin, "initial",
            [
                new SecretChange(SecretNames.SmtpPassword, new SecretValue("Hasło!Zażółć2026"), "smtp|mail.contoso.test|587|watchdog|basic"),
                new SecretChange(SecretNames.ChannelUrl("siem"), new SecretValue("https://siem.contoso.test/hooks/ab12?sig=xyz"), "channel|siem|webhook"),
            ],
            Ct);

        await Assert.ThrowsAsync<ConfigVersionConflictException>(() => store.SaveAsync(SampleConfiguration(), 0, Admin, "stale",
            [new SecretChange(SecretNames.SmtpPassword, null, null)], Ct));

        Assert.Equal(1, (await ((IConfigStore)store).GetAsync(Ct))?.Version);
        Assert.Equal("Hasło!Zażółć2026", (await store.GetAsync(SecretNames.SmtpPassword, Ct))?.Value.Reveal());
        Assert.Single((await store.GetAuditAsync(null, 10, Ct)).Items);

        await store.SaveAsync(SampleConfiguration(), 1, Admin, "smtp password cleared; siem url replaced",
            [
                new SecretChange(SecretNames.SmtpPassword, null, null),
                new SecretChange(SecretNames.ChannelUrl("siem"), new SecretValue("https://siem2.contoso.test/hooks/cd34"), "channel|siem|webhook"),
            ],
            Ct);

        Assert.Null(await store.GetAsync(SecretNames.SmtpPassword, Ct));
        var url = await store.GetAsync(SecretNames.ChannelUrl("siem"), Ct);
        Assert.Equal("https://siem2.contoso.test/hooks/cd34", url?.Value.Reveal());
        Assert.Equal("channel|siem|webhook", url?.Binding);
        Assert.Equal(new Dictionary<string, string> { [SecretNames.ChannelUrl("siem")] = "channel|siem|webhook" }, await store.ListAsync(Ct));
    });

    [Fact]
    public Task ConfigAudit_ListsSavesNewestFirst_WithPaging() => WithStoreAsync(async store =>
    {
        var version = 0L;
        foreach (var summary in new[] { "initial", "rules: lockouts changed", "smtp: host changed" })
        {
            version = (await store.SaveAsync(new WatchdogConfiguration(), version, Admin, summary, [], Ct)).Version;
            Clock.Advance(TimeSpan.FromMinutes(1));
        }

        var first = await store.GetAuditAsync(null, 2, Ct);
        var second = await store.GetAuditAsync(first.Items[^1].Id, 2, Ct);

        Assert.Equal(["smtp: host changed", "rules: lockouts changed"], first.Items.Select(a => a.Summary));
        Assert.True(first.HasMore);
        var entry = first.Items[0];
        Assert.Equal(3, entry.Version);
        Assert.Equal(Now.AddMinutes(2), entry.At);
        Assert.Equal(Admin.Sid, entry.BySid);
        Assert.Equal(Admin.Account, entry.ByAccount);
        Assert.Equal(["initial"], second.Items.Select(a => a.Summary));
        Assert.False(second.HasMore);
    });

    [Fact]
    public Task Secrets_SetGetRemoveAndList() => WithStoreAsync(async store =>
    {
        await store.SetAsync(SecretNames.WebhookSigningSecret("siem"), new SecretValue("whsec_MfKQ9r8GKYqrTwjUPD8ILPZIo2LaLaSw"), "webhook|siem|siem.contoso.test", Ct);
        await store.SetAsync(SecretNames.SmtpPassword, new SecretValue("stare-hasło"), "smtp|old", Ct);
        await store.SetAsync(SecretNames.SmtpPassword, new SecretValue("nowe-hasło-ąę"), "smtp|mail.contoso.test|587|watchdog|basic", Ct);

        var password = await store.GetAsync(SecretNames.SmtpPassword, Ct);
        Assert.NotNull(password);
        Assert.Equal("nowe-hasło-ąę", password.Value.Reveal());
        Assert.Equal("smtp|mail.contoso.test|587|watchdog|basic", password.Binding);
        Assert.DoesNotContain("hasło", password.ToString(), StringComparison.Ordinal);
        Assert.Equal(2, (await store.ListAsync(Ct)).Count);

        await store.RemoveAsync(SecretNames.SmtpPassword, Ct);
        await store.RemoveAsync("nieznany", Ct);

        Assert.Null(await store.GetAsync(SecretNames.SmtpPassword, Ct));
        Assert.Equal(SecretNames.WebhookSigningSecret("siem"), Assert.Single(await store.ListAsync(Ct)).Key);
    });
}
