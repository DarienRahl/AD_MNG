using AdMng.Core.Correspondence.Composition;
using AdMng.Core.Correspondence.Outlook;

namespace AdMng.Outlook;

/// <summary>
/// Outlook signatures from <c>%APPDATA%\Microsoft\Signatures</c> (design note 4.1): the file listing and reading of the
/// <c>.htm</c> files and their <c>Name_files</c> images (relative paths only, no "..", no rooted paths, inside the folder),
/// at most <see cref="MaxFileBytes"/> per file, parsed with <see cref="OutlookSignatureParser"/>. No COM; the constructor
/// never touches the file system.
/// </summary>
public sealed class OutlookSignatureFiles : IOutlookSignatureSource
{
    /// <summary>The largest signature or image file read (2 MiB).</summary>
    public const int MaxFileBytes = 2 * 1024 * 1024;

    private readonly string? _folder;

    /// <param name="folder">The signatures folder (tests); null: <c>%APPDATA%\Microsoft\Signatures</c> on Windows, none elsewhere.</param>
    public OutlookSignatureFiles(string? folder = null)
    {
        _folder = folder;
    }

    public string? Folder => _folder ?? DefaultFolder();

    public IReadOnlyList<string> List() => throw new NotImplementedException("U5 T3 outlook");

    public Task<SignatureParseResult?> ReadAsync(string name, int ansiCodePage, CancellationToken cancellationToken) =>
        throw new NotImplementedException("U5 T3 outlook");

    public override string ToString() => nameof(OutlookSignatureFiles);

    private static string? DefaultFolder() => OperatingSystem.IsWindows()
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Microsoft", "Signatures")
        : null;
}
