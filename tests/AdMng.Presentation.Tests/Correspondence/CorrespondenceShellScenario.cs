using System.Globalization;
using System.Security.Cryptography;
using AdMng.Core.Correspondence.Composition;
using AdMng.Core.Correspondence.Outlook;
using AdMng.Core.Csv;
using AdMng.Presentation.Correspondence;
using AdMng.Presentation.Services;
using AdMng.Presentation.Tests.Dialogs;
using AdMng.Presentation.Tests.TestDoubles;
using AdMng.Presentation.UserMessages;
using AdMng.Testing;

namespace AdMng.Presentation.Tests.Correspondence;

/// <summary>
/// The Korespondencja window over the B2 harness (Polish sample company, real Core engines, fake directory) with the
/// services of the shell track: a real journal and template library in a temporary settings folder (an XOR key protector
/// instead of DPAPI), <see cref="FakeOutlookClient"/> (or no Outlook at all) and recording doubles of the window's dialogs,
/// the platform and the signatures. The doubles are nested types so that other test files of this namespace can have their
/// own.
/// </summary>
internal sealed class CorrespondenceShellScenario : IDisposable
{
    private CorrespondenceShellScenario(B2Harness harness, bool withOutlook)
    {
        Harness = harness;
        Images = new MessageImageStore(Paths, harness.Time);
        Templates = new CorrespondenceTemplateLibrary(harness.Settings, Images);
        Journal = new CorrespondenceJournal(Paths, Protector, harness.Time);
        Platform = new FakePlatform(Path.Combine(Paths.SettingsDirectory, "Pobrane"));
        Signatures = new FakeSignatures(Path.Combine(Paths.SettingsDirectory, "Signatures"), ["Jan Kowalski – podpis", "Sekretariat"]);
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
            browserPreview: null,
            delay: (span, token) =>
            {
                token.ThrowIfCancellationRequested();
                harness.Time.Advance(span);
                return Task.CompletedTask;
            });
        Factory = new CorrespondenceViewModelFactory(harness.Services, Services);
    }

    public B2Harness Harness { get; }

    public SampleCompany Company => Harness.Company;

    public TempAppPaths Paths { get; } = new();

    public FakeOutlookClient Outlook { get; } = new();

    public XorKeyProtector Protector { get; } = new();

    public RecordingDialogs Dialogs { get; } = new();

    public FakePlatform Platform { get; }

    public FakeSignatures Signatures { get; }

    public MessageImageStore Images { get; }

    public CorrespondenceTemplateLibrary Templates { get; }

    public CorrespondenceJournal Journal { get; }

    public CorrespondenceServices Services { get; }

    public CorrespondenceViewModelFactory Factory { get; }

    public static async Task<CorrespondenceShellScenario> CreateAsync(CultureInfo? culture = null, bool withOutlook = true) =>
        new(await B2Harness.CreateAsync(culture), withOutlook);

    /// <summary>A window for <paramref name="request"/> (default: empty), initialized, with its background connection done.</summary>
    public async Task<CorrespondenceViewModel> OpenAsync(CorrespondenceRequest? request = null)
    {
        var window = Factory.Create(request ?? new CorrespondenceRequest([]));
        await window.InitializeAsync();
        if (window.Outlook.Connecting is { } connecting)
        {
            await connecting;
        }

        return window;
    }

    /// <summary>An image of Korespondencja (its larger limit).</summary>
    public static Core.Messaging.Images.MessageImage Image(int width = 8, int height = 4, byte seed = 0x40, string name = "logo-urzędu.png") =>
        Core.Messaging.Images.MessageImage.Create(MessageImageSamples.Png(width, height, seed), name, maxImageBytes: CorrespondenceLimits.MaxImageBytes);

    public void Dispose() => Paths.Dispose();

    /// <summary>Stands in for DPAPI: XOR with a fixed byte behind a marker; data without the marker is "undecryptable".</summary>
    internal sealed class XorKeyProtector : ICorrespondenceKeyProtector
    {
        private static readonly byte[] Marker = "XOR1"u8.ToArray();

        public int Protected { get; private set; }

        /// <summary>When set, <see cref="Unprotect"/> answers this instead (e.g. a key of a wrong length).</summary>
        public byte[]? UnprotectOverride { get; set; }

        /// <summary>When set, both methods throw it (a host without a protector).</summary>
        public Exception? Failure { get; set; }

        public byte[] Protect(byte[] data)
        {
            if (Failure is { } failure)
            {
                throw failure;
            }

            Protected++;
            return [.. Marker, .. data.Select(b => (byte)(b ^ 0x5A))];
        }

        public byte[] Unprotect(byte[] data)
        {
            if (Failure is { } failure)
            {
                throw failure;
            }

            if (UnprotectOverride is { } answer)
            {
                return [.. answer];
            }

            if (data.Length < Marker.Length || !data.AsSpan(0, Marker.Length).SequenceEqual(Marker))
            {
                throw new CryptographicException("The data cannot be decrypted.");
            }

            return [.. data.Skip(Marker.Length).Select(b => (byte)(b ^ 0x5A))];
        }
    }

    /// <summary>Records the reports and prompts of the window.</summary>
    internal sealed class RecordingDialogs : ICorrespondenceDialogs
    {
        public List<(string Title, string Text)> Reports { get; } = [];

        public Queue<IReadOnlyList<string>> Files { get; } = new();

        public Queue<string?> Prompts { get; } = new();

        public Queue<string?> Folders { get; } = new();

        public Task<IReadOnlyList<string>> OpenFilesAsync(string title, IReadOnlyList<FileFilter> filters) =>
            Task.FromResult(Files.Count > 0 ? Files.Dequeue() : (IReadOnlyList<string>)[]);

        public Task<string?> PromptTextAsync(string title, string message, string initial, bool multiline) =>
            Task.FromResult(Prompts.Count > 0 ? Prompts.Dequeue() : null);

        public Task ShowReportAsync(string title, string text)
        {
            Reports.Add((title, text));
            return Task.CompletedTask;
        }

        public Task<string?> PickFolderAsync(string title) => Task.FromResult(Folders.Count > 0 ? Folders.Dequeue() : null);
    }

    internal sealed class FakePlatform(string? downloads) : ICorrespondencePlatform
    {
        public string? DownloadsFolder { get; set; } = downloads;

        public int AnsiCodePage { get; set; } = 1250;
    }

    /// <summary>Signature names without files (reading one answers null).</summary>
    internal sealed class FakeSignatures(string? folder, IReadOnlyList<string> names) : IOutlookSignatureSource
    {
        public string? Folder { get; set; } = folder;

        public IReadOnlyList<string> Names { get; set; } = names;

        public IReadOnlyList<string> List() => Names;

        public Task<SignatureParseResult?> ReadAsync(string name, int ansiCodePage, CancellationToken cancellationToken) =>
            Task.FromResult<SignatureParseResult?>(null);
    }
}
