using AdMng.Core.Audit;
using AdMng.Core.Csv;
using AdMng.Presentation.Dialogs;
using AdMng.Presentation.Tests.AccountReview;
using AdMng.Presentation.Tests.Dialogs;
using AdMng.Presentation.UserMessages;
using AdMng.Testing;
using AdMng.Watchdog.Testing.Client;

namespace AdMng.Presentation.Tests.UserMessages;

/// <summary>
/// The review scenario of U2 (password, account and lock states of the sample company) with messages to users: direct
/// SMTP through <see cref="FakeUserMessageTransport"/>, delays advancing the manual clock (no real waiting), the sender and
/// a test address in the settings.
/// </summary>
internal sealed class MessageScenario
{
    private MessageScenario(ReviewScenario review)
    {
        Review = review;
        var harness = review.Harness;
        Services = new UserMessageServices(
            new UserMessageTemplateLibrary(harness.Settings, harness.Localizer),
            new UserMessageTransportSelector(harness.Settings, Credentials, new WatchdogUserMessageTransport(Watchdog), Transport, Outlook, OutlookTransports),
            new CsvCodec(),
            harness.AuditLog,
            harness.Time,
            (delay, token) =>
            {
                token.ThrowIfCancellationRequested();
                Delays.Add(delay);
                harness.Time.Advance(delay);
                return Task.CompletedTask;
            });
        Factory = new DialogViewModelFactory(
            harness.Services, harness.Engines, harness.Views, harness.AuditLog, new AuditLogExporter(new CsvCodec()), harness.PowerShell,
            harness.Theme, messages: Launcher, userMessages: Services);
    }

    public ReviewScenario Review { get; }

    public B2Harness Harness => Review.Harness;

    public SampleCompany Company => Review.Company;

    public FakeUserMessageTransport Transport { get; } = new();

    public SmtpCredentialCache Credentials { get; } = new();

    /// <summary>Classic Outlook of the "computer" (U5): installed, the sample profile; used only with the choice Outlook.</summary>
    public FakeOutlookClient Outlook { get; } = new();

    /// <summary>The Outlook transport (U5): records the account and "w imieniu" of each transport created.</summary>
    public FakeOutlookUserMessageTransportFactory OutlookTransports { get; } = new();

    /// <summary>Configured with the sample address; <c>Configure(null)</c> makes it "not configured".</summary>
    public FakeWatchdogClient Watchdog { get; } = new(TimeProvider.System);

    public RecordingMessageLauncher Launcher { get; } = new();

    public List<TimeSpan> Delays { get; } = [];

    public UserMessageServices Services { get; }

    public DialogViewModelFactory Factory { get; }

    public static async Task<MessageScenario> CreateAsync(System.Globalization.CultureInfo? culture = null)
    {
        var scenario = new MessageScenario(await ReviewScenario.CreateAsync(culture));
        await scenario.Harness.Settings.UpdateAsync(s =>
        {
            s.UserMessages.Cc = "helpdesk@contoso.test";
            s.UserMessages.ReplyTo = "pomoc@contoso.test";
            s.UserMessages.TestAddress = "operator@contoso.test";
            s.UserMessages.Smtp.Host = "smtp.contoso.test";
            s.UserMessages.Smtp.Port = 587;
        });
        return scenario;
    }

    /// <summary>A message window for <paramref name="dns"/>, initialized.</summary>
    public async Task<UserMessageComposerViewModel> OpenAsync(IReadOnlyList<string> dns, string? templateId = null, int? days = null)
    {
        var composer = Factory.CreateUserMessageComposer(new UserMessageRequest(dns, templateId, days));
        await composer.InitializeAsync();
        return composer;
    }

    /// <summary>The item of <paramref name="name"/> in the plan.</summary>
    public static UserMessageItemViewModel Item(UserMessageComposerViewModel composer, string name) =>
        Assert.Single(composer.AllItems, i => i.Name == name);
}
