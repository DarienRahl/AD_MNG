using System.Globalization;
using AdMng.Core.Correspondence.Composition;
using AdMng.Core.Correspondence.Outlook;
using AdMng.Core.Csv;
using AdMng.Core.Messaging.Images;
using AdMng.Presentation.Correspondence;
using AdMng.Presentation.Tests.Dialogs;
using AdMng.Presentation.Tests.TestDoubles;
using AdMng.Presentation.UserMessages;
using AdMng.Testing;
using AdMng.Testing.RichHtml;

namespace AdMng.Presentation.Tests.Correspondence;

/// <summary>
/// The Korespondencja window over the B2 harness for the message, preview, send and responses pages: a real journal and
/// template library in a temporary settings folder, <see cref="FakeOutlookClient"/>, the shell scenario's recording dialogs
/// and platform, signatures that parse (with an image), the Office clipboard images of the U1c fixtures, a recording
/// browser preview and a delay that only moves the manual clock. Recipient rows are put on the recipients page directly
/// (the pages read the rows' own state).
/// </summary>
internal sealed class CorrespondencePagesScenario : IDisposable
{
    private CorrespondencePagesScenario(B2Harness harness, bool withOutlook)
    {
        Harness = harness;
        Images = new MessageImageStore(Paths, harness.Time);
        Templates = new CorrespondenceTemplateLibrary(harness.Settings, Images);
        Journal = new CorrespondenceJournal(Paths, Protector, harness.Time);
        Platform = new CorrespondenceShellScenario.FakePlatform(Path.Combine(Paths.SettingsDirectory, "Pobrane"));
        Services = new CorrespondenceServices(
            Templates,
            Journal,
            new CsvCodec(),
            harness.AuditLog,
            harness.Time,
            withOutlook ? Outlook : null,
            Signatures,
            Images,
            Dialogs,
            Platform,
            Browser,
            delay: (span, token) =>
            {
                token.ThrowIfCancellationRequested();
                Delays.Add(span);
                harness.Time.Advance(span);
                return Task.CompletedTask;
            },
            pastedImages: PastedImages);
        Factory = new CorrespondenceViewModelFactory(harness.Services, Services);
    }

    public B2Harness Harness { get; }

    public TempAppPaths Paths { get; } = new();

    public FakeOutlookClient Outlook { get; } = new();

    public CorrespondenceShellScenario.XorKeyProtector Protector { get; } = new();

    public CorrespondenceShellScenario.RecordingDialogs Dialogs { get; } = new();

    public CorrespondenceShellScenario.FakePlatform Platform { get; }

    public ParsingSignatures Signatures { get; } = new();

    public RecordingBrowser Browser { get; } = new();

    public FakePastedImageSource PastedImages { get; } = new();

    public List<TimeSpan> Delays { get; } = [];

    public MessageImageStore Images { get; }

    public CorrespondenceTemplateLibrary Templates { get; }

    public CorrespondenceJournal Journal { get; }

    public CorrespondenceServices Services { get; }

    public CorrespondenceViewModelFactory Factory { get; }

    public ScriptedDialogService Ui => Harness.Dialogs;

    public static async Task<CorrespondencePagesScenario> CreateAsync(CultureInfo? culture = null, bool withOutlook = true) =>
        new(await B2Harness.CreateAsync(culture ?? CultureInfo.GetCultureInfo("pl-PL")), withOutlook);

    /// <summary>A window (default: empty request), initialized, with its background connection done; the preview refreshes
    /// at once.</summary>
    public async Task<CorrespondenceViewModel> OpenAsync(CorrespondenceRequest? request = null)
    {
        var window = Factory.Create(request ?? new CorrespondenceRequest([]));
        window.Preview.RefreshDelay = TimeSpan.Zero;
        await window.InitializeAsync();
        if (window.Outlook.Connecting is { } connecting)
        {
            await connecting;
        }

        return window;
    }

    /// <summary>A window with a message (subject, body) ready to send.</summary>
    public async Task<CorrespondenceViewModel> OpenWithMessageAsync(string subject = "Zaproszenie na konferencję – {{Miejscowość}}",
        string body = "<p>{{zwrot}},</p><p>zapraszamy {{nazwa}} z miejscowości {{Miejscowość}} na konferencję w Łodzi.</p>")
    {
        var window = await OpenAsync();
        window.Message.Subject = subject;
        window.Message.BodyHtml = body;
        return window;
    }

    /// <summary>A recipient row as an imported file gives it (address normalized, valid).</summary>
    public static CorrespondenceRecipientViewModel Row(string email, string name, params (string Column, string Value)[] fields) =>
        new(email, name, "kontakty.csv", fields.ToDictionary(f => f.Column, f => f.Value, StringComparer.OrdinalIgnoreCase));

    /// <summary>The Polish sample list: three offices with "Miejscowość" and "Kod pocztowy".</summary>
    public static CorrespondenceRecipientViewModel[] Offices() =>
    [
        Row("sekretariat@andrespol.pl", "Urząd Gminy Andrespol", ("Miejscowość", "Andrespol"), ("Kod pocztowy", "95-020")),
        Row("ug@brojce.pl", "Urząd Gminy Brójce", ("Miejscowość", "Brójce"), ("Kod pocztowy", "95-006")),
        Row("kancelaria@zgierz.pl", "Starostwo Powiatowe w Zgierzu", ("Miejscowość", "Zgierz"), ("Kod pocztowy", "")),
    ];

    /// <summary>Puts rows on the recipients page (their columns first, in order).</summary>
    public static void AddRows(CorrespondenceViewModel window, params CorrespondenceRecipientViewModel[] rows)
    {
        foreach (var column in rows.SelectMany(r => r.Fields.Keys))
        {
            if (!window.Recipients.Columns.Contains(column, StringComparer.OrdinalIgnoreCase))
            {
                window.Recipients.Columns.Add(column);
            }
        }

        window.Recipients.Items.AddRange(rows);
    }

    /// <summary>An image of Korespondencja (its larger limit).</summary>
    public static MessageImage Image(int width = 8, int height = 4, byte seed = 0x40, string name = "herb-gminy.png") =>
        MessageImage.Create(MessageImageSamples.Png(width, height, seed), name, maxImageBytes: CorrespondenceLimits.MaxImageBytes);

    public void Dispose() => Paths.Dispose();

    /// <summary>Two signatures; reading one gives a parsed signature with a logo (and the problems set for it).</summary>
    internal sealed class ParsingSignatures : IOutlookSignatureSource
    {
        public const string Kowalski = "Jan Kowalski – podpis";
        public const string Secretariat = "Sekretariat";

        public string? Folder { get; set; } = "C:\\Users\\jkowalski\\AppData\\Roaming\\Microsoft\\Signatures";

        public List<string> Names { get; } = [Kowalski, Secretariat];

        public Dictionary<string, IReadOnlyList<string>> Problems { get; } = new(StringComparer.Ordinal);

        public MessageImage Logo { get; } = Image(6, 3, 0x22, "logo.png");

        public Exception? Failure { get; set; }

        public List<string> Reads { get; } = [];

        public IReadOnlyList<string> List() => [.. Names];

        public Task<SignatureParseResult?> ReadAsync(string name, int ansiCodePage, CancellationToken cancellationToken)
        {
            Reads.Add(name);
            if (Failure is { } failure)
            {
                return Task.FromException<SignatureParseResult?>(failure);
            }

            if (!Names.Contains(name))
            {
                return Task.FromResult<SignatureParseResult?>(null);
            }

            var html = $"<p style=\"color:#1F3864\"><b>{name}</b><br>Urząd Gminy Andrespol</p><p><img src=\"cid:{Logo.ContentId}\" alt=\"logo\"></p>";
            return Task.FromResult<SignatureParseResult?>(new SignatureParseResult(
                new CorrespondenceSignature(name, html, [Logo]),
                Problems.TryGetValue(name, out var problems) ? problems : []));
        }
    }

    /// <summary>"Otwórz w przeglądarce" without a browser: records what would be shown.</summary>
    internal sealed class RecordingBrowser : IBrowserPreview
    {
        public List<(HtmlPreviewContent Content, string Title)> Opened { get; } = [];

        public Task OpenAsync(HtmlPreviewContent content, string title)
        {
            Opened.Add((content, title));
            return Task.CompletedTask;
        }
    }
}
