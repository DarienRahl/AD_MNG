using AdMng.Presentation.Correspondence;
using AdMng.Presentation.Services;

namespace AdMng.App.Correspondence;

/// <summary>
/// The computer's facts for Korespondencja (design note 9.4): the Downloads folder (<c>SHGetKnownFolderPath</c> of
/// FOLDERID_Downloads with MailerWPiA's fallbacks) and the ANSI code page of the regional settings. Application singleton;
/// the constructor touches nothing.
/// </summary>
public sealed class WindowsCorrespondencePlatform(IRegionalSettings regional) : ICorrespondencePlatform
{
    private readonly IRegionalSettings _regional = regional ?? throw new ArgumentNullException(nameof(regional));

    /// <summary>Scaffold: null until the App track resolves the known folder.</summary>
    public string? DownloadsFolder => null;

    public int AnsiCodePage => _regional.AnsiCodePage;

    public override string ToString() => nameof(WindowsCorrespondencePlatform);
}
