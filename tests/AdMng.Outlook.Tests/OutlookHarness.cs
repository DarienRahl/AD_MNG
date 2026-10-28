using AdMng.Core.Correspondence.Outlook;
using AdMng.Core.Messaging.Images;
using AdMng.Outlook.Tests.FakeOutlook;
using AdMng.Testing;
using Microsoft.Extensions.Time.Testing;

namespace AdMng.Outlook.Tests;

/// <summary>
/// A fake Outlook (the CLR object model of <see cref="FakeOutlook"/>) behind an <see cref="OutlookClient"/> with a private
/// temp root, a manual clock in a fixed +01:00 zone, the log's releaser and no elevation. The profile has three accounts
/// whose SMTP address comes from the three places Outlook offers (SmtpAddress, CurrentUser.Address, the Exchange user).
/// </summary>
internal sealed class OutlookHarness : IAsyncDisposable
{
    public const string Own = "jan.kowalski@contoso.test";
    public const string Secretariat = "sekretariat.lodz@contoso.test";
    public const string Archive = "archiwum.zolc@contoso.test";

    private readonly List<OutlookSession> _sessions = [];

    public OutlookHarness(bool elevated = false)
    {
        Root = Path.Combine(Path.GetTempPath(), "admng-outlook-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
        TempRoot = Path.Combine(Root, "temp");
        Application = new Application();
        var model = Application.NameSpace;
        model.User = new Recipient(Application)
        {
            Address = "/o=ExchangeLabs/ou=Exchange Administrative Group/cn=Recipients/cn=jan",
            Entry = new AddressEntry(Application) { Exchange = new ExchangeUser { PrimarySmtpAddress = Own } },
        };
        model.AccountList.Add("Jan Kowalski", Own);
        model.AccountList.Add("Sekretariat – Łódź", string.Empty, new Recipient(Application) { Address = Secretariat });
        model.AccountList.Add(
            "Archiwum Żółć",
            null,
            new Recipient(Application)
            {
                Address = "/o=ExchangeLabs/cn=archiwum",
                Entry = new AddressEntry(Application) { Exchange = new ExchangeUser { PrimarySmtpAddress = Archive } },
            });
        Provider = new FakeApplicationProvider(Application);
        Time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 28, 9, 0, 0, TimeSpan.Zero));
        Time.SetLocalTimeZone(Zone);
        Provider.Clock = Time.GetUtcNow;
        TempFiles = new OutlookTempFiles(TempRoot, _ => false);
        Signatures = new FakeSignatureSource();
        Client = new OutlookClient(Provider, Time, Signatures, TempFiles, Log.Release, () => elevated);
    }

    /// <summary>A fixed +01:00 zone (no daylight saving), so local times are predictable on every machine.</summary>
    public static TimeZoneInfo Zone { get; } = TimeZoneInfo.CreateCustomTimeZone("AdMng/Test+01", TimeSpan.FromHours(1), "Test +01", "Test +01");

    public string Root { get; }

    public string TempRoot { get; }

    public Application Application { get; }

    public OutlookLog Log => Application.Log;

    public FakeApplicationProvider Provider { get; }

    public FakeTimeProvider Time { get; }

    public OutlookTempFiles TempFiles { get; }

    public FakeSignatureSource Signatures { get; }

    public OutlookClient Client { get; }

    public static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>A PNG of the U1c samples as a message image.</summary>
    public static MessageImage Image(byte seed = 0x40, string fileName = "logo.png") =>
        MessageImage.Create(MessageImageSamples.Png(seed: seed), fileName);

    /// <summary>A message to Anna Nowak with Polish subject and body.</summary>
    public static OutlookMessage Message(params string[] to) => new()
    {
        To = to.Length == 0 ? ["anna.nowak@contoso.test"] : to,
        Subject = "Czy skrzynka „Sekretariat IT” jest nadal potrzebna?",
        HtmlBody = "<html><body><p>Szanowna Pani, zażółć gęślą jaźń.</p></body></html>",
    };

    public async Task<OutlookSession> ConnectAsync()
    {
        var result = await Client.ConnectAsync(Ct);
        Assert.Null(result.Failure);
        var session = Assert.IsType<OutlookSession>(result.Session);
        _sessions.Add(session);
        return session;
    }

    /// <summary>A file in the test folder (an attachment).</summary>
    public string File(string name, string content = "Załącznik – treść")
    {
        var path = Path.Combine(Root, name);
        System.IO.File.WriteAllText(path, content);
        return path;
    }

    /// <summary>The item folders still in the temp root.</summary>
    public IReadOnlyList<string> TempFolders() =>
        Directory.Exists(TempRoot) ? [.. Directory.EnumerateDirectories(TempRoot)] : [];

    /// <summary>Every object handed out by the fake was released exactly as often as it was handed out.</summary>
    public void AssertAllReleased(params object[] alsoReleased)
    {
        var expected = Log.HandedOut.Concat(alsoReleased).GroupBy(value => value, ReferenceEqualityComparer.Instance)
            .ToDictionary(group => group.Key, group => group.Count(), ReferenceEqualityComparer.Instance);
        var actual = Log.Released.GroupBy(value => value, ReferenceEqualityComparer.Instance)
            .ToDictionary(group => group.Key, group => group.Count(), ReferenceEqualityComparer.Instance);
        foreach (var (value, count) in expected)
        {
            Assert.True(actual.TryGetValue(value, out var released) && released == count, $"{value.GetType().Name} handed out {count}×, released {(actual.TryGetValue(value, out var r) ? r : 0)}×");
        }

        Assert.Equal(expected.Values.Sum(), actual.Values.Sum());
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var session in _sessions)
        {
            await session.DisposeAsync();
        }

        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}

/// <summary>An <see cref="IOutlookSignatureSource"/> with fixed names (the diagnostics list them).</summary>
internal sealed class FakeSignatureSource : IOutlookSignatureSource
{
    public string? Folder => "C:\\Users\\jan\\AppData\\Roaming\\Microsoft\\Signatures";

    public List<string> Names { get; } = ["Jan Kowalski – podpis", "Krótki"];

    public IReadOnlyList<string> List() => [.. Names];

    public Task<Core.Correspondence.Composition.SignatureParseResult?> ReadAsync(string name, int ansiCodePage, CancellationToken cancellationToken) =>
        Task.FromResult<Core.Correspondence.Composition.SignatureParseResult?>(null);
}
