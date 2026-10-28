using System.Text;
using AdMng.Core.Correspondence.Composition;
using AdMng.Core.Correspondence.Outlook;
using AdMng.Testing;

namespace AdMng.App.UiTests.Infrastructure;

/// <summary>
/// The Outlook signatures of the UI tests (U5): one Polish signature with a logo, parsed by the real
/// <see cref="OutlookSignatureParser"/> from an Outlook-like .htm and its <c>_files</c> image – no folder of the operator
/// is read.
/// </summary>
internal sealed class FakeOutlookSignatureSource : IOutlookSignatureSource
{
    /// <summary>The name of the only signature.</summary>
    public const string SignatureName = "Jan Kowalski – Dział IT";

    private const string Html = """
        <html><head><meta charset="utf-8"><style>p.MsoNormal{margin:0;font-family:Calibri,sans-serif;font-size:11pt}</style></head>
        <body><p class=MsoNormal>Z wyrazami szacunku</p>
        <p class=MsoNormal><b>Jan Kowalski</b><br>Kierownik zespołu · Dział IT<br>Contoso sp. z o.o., ul. Piotrkowska 120, 90-006 Łódź<br>tel. +48 42 123 45 67</p>
        <p class=MsoNormal><img width=96 height=32 src="Jan%20Kowalski%20%E2%80%93%20Dzia%C5%82%20IT_files/image001.png" alt="Contoso"></p>
        </body></html>
        """;

    public string? Folder => @"C:\Users\jkowalski\AppData\Roaming\Microsoft\Signatures";

    public IReadOnlyList<string> List() => [SignatureName];

    public Task<SignatureParseResult?> ReadAsync(string name, int ansiCodePage, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!string.Equals(name, SignatureName, StringComparison.Ordinal))
        {
            return Task.FromResult<SignatureParseResult?>(null);
        }

        var logo = MessageImageSamples.Png(96, 32, seed: 0x30);
        var result = OutlookSignatureParser.Parse(
            name,
            Encoding.UTF8.GetBytes(Html),
            relative => relative.EndsWith("image001.png", StringComparison.OrdinalIgnoreCase) ? logo : null,
            ansiCodePage);
        return Task.FromResult<SignatureParseResult?>(result);
    }

    public override string ToString() => nameof(FakeOutlookSignatureSource);
}
