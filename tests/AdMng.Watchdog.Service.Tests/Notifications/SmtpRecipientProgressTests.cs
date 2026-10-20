using AdMng.Watchdog.Service.Notifications;

namespace AdMng.Watchdog.Service.Tests.Notifications;

/// <summary>The in-memory record of the recipients an outbox e-mail already reached (spec 15.6).</summary>
public sealed class SmtpRecipientProgressTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 14, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public void DeliveredAddresses_AreKeptPerMessage_CaseInsensitively_UntilComplete()
    {
        var progress = new SmtpRecipientProgress();

        progress.Add("wiadomosc-1", ["Łucja.Nowak@Contoso.test", "soc@contoso.test"], Now);
        progress.Add("wiadomosc-2", ["jan.kowalski@contoso.test"], Now);

        Assert.Equal(["soc@contoso.test", "łucja.nowak@contoso.test"], progress.Delivered("wiadomosc-1").Order(StringComparer.Ordinal));
        Assert.Contains(SmtpRecipientProgress.Key(" ŁUCJA.nowak@contoso.TEST "), progress.Delivered("wiadomosc-1"));
        progress.Complete("wiadomosc-1");
        Assert.Empty(progress.Delivered("wiadomosc-1"));
        Assert.Single(progress.Delivered("wiadomosc-2"));
    }

    [Fact]
    public void UnfinishedMessages_ExpireAfterTheRetention()
    {
        var progress = new SmtpRecipientProgress();
        progress.Add("stara", ["jan.kowalski@contoso.test"], Now);
        progress.Add("nowsza", ["jan.kowalski@contoso.test"], Now.AddDays(10));

        progress.Add("nowa", ["soc@contoso.test"], Now + SmtpRecipientProgress.Retention + TimeSpan.FromMinutes(1));

        Assert.Empty(progress.Delivered("stara"));
        Assert.Single(progress.Delivered("nowsza"));
        Assert.Single(progress.Delivered("nowa"));
    }

    [Fact]
    public void TheNumberOfUnfinishedMessages_IsCapped_OldestFirst()
    {
        var progress = new SmtpRecipientProgress();
        for (var i = 0; i < SmtpRecipientProgress.MaxMessages + 5; i++)
        {
            progress.Add($"m{i}", ["jan.kowalski@contoso.test"], Now.AddSeconds(i));
        }

        Assert.Empty(progress.Delivered("m0"));
        Assert.Empty(progress.Delivered("m4"));
        Assert.Single(progress.Delivered("m5"));
        Assert.Single(progress.Delivered($"m{SmtpRecipientProgress.MaxMessages + 4}"));
    }
}
