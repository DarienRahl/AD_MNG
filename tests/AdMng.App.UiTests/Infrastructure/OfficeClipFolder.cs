using System.Globalization;
using System.IO;
using AdMng.Testing.RichHtml;

namespace AdMng.App.UiTests.Infrastructure;

/// <summary>
/// An Office clipboard image folder of its own in the temp folder – <c>msohtmlclip&lt;n&gt;\01</c>, the only shape
/// <see cref="Core.Messaging.Images.ClipboardImagePolicy"/> reads images from – with a random <c>&lt;n&gt;</c>, so a real
/// Outlook clipboard (<c>msohtmlclip1</c>) is untouched.
/// </summary>
internal sealed class OfficeClipFolder : IDisposable
{
    /// <summary>The image folder of the <c>outlook-classic</c> fixture.</summary>
    private const string FixtureFolder = "file:///C:/Users/JKOWAL~1/AppData/Local/Temp/msohtmlclip1/01/";

    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "msohtmlclip" + Random.Shared.NextInt64(1_000_000_000, 10_000_000_000).ToString(CultureInfo.InvariantCulture));

    public OfficeClipFolder() => System.IO.Directory.CreateDirectory(Folder);

    /// <summary>The fragment folder (<c>…\msohtmlclip&lt;n&gt;\01</c>).</summary>
    public string Folder => Path.Combine(_root, "01");

    public void Add(string name, byte[] data) => File.WriteAllBytes(Path.Combine(Folder, name), data);

    /// <summary>The Outlook fixture with its images in this folder.</summary>
    public string OutlookHtml() =>
        CfHtmlFixture.Html("outlook-classic").Replace(FixtureFolder, new Uri(Folder + Path.DirectorySeparatorChar).AbsoluteUri, StringComparison.Ordinal);

    public void Dispose()
    {
        try
        {
            System.IO.Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // A virus scanner may still hold a file; the temp folder is cleaned by Windows.
        }
    }
}
