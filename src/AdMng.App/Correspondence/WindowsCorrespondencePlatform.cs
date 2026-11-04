using System.IO;
using System.Runtime.InteropServices;
using AdMng.Presentation.Correspondence;
using AdMng.Presentation.Services;

namespace AdMng.App.Correspondence;

/// <summary>
/// The computer's facts for Korespondencja (design note 9.4): the Downloads folder (<c>SHGetKnownFolderPath</c> of
/// FOLDERID_Downloads – on domain accounts it is often redirected to OneDrive or a share, so it is asked for, never
/// built from the profile path – with MailerWPiA's fallbacks <c>%USERPROFILE%\Downloads</c> and Documents) and the ANSI
/// code page of the regional settings. Application singleton; the constructor touches nothing, every call asks anew.
/// </summary>
public sealed class WindowsCorrespondencePlatform(IRegionalSettings regional) : ICorrespondencePlatform
{
    /// <summary>FOLDERID_Downloads.</summary>
    internal static readonly Guid DownloadsFolderId = new("374DE290-123F-4565-9164-39C4925E467B");

    private readonly IRegionalSettings _regional = regional ?? throw new ArgumentNullException(nameof(regional));

    /// <summary>The user's Downloads folder, or null when neither it nor a fallback exists.</summary>
    public string? DownloadsFolder => ResolveDownloads(
        KnownDownloadsFolder,
        System.IO.Directory.Exists,
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments));

    public int AnsiCodePage => _regional.AnsiCodePage;

    public override string ToString() => nameof(WindowsCorrespondencePlatform);

    /// <summary>
    /// MailerWPiA's order: the known folder when it exists, else <c>{profile}\Downloads</c>, else Documents; null when none
    /// of them exists.
    /// </summary>
    internal static string? ResolveDownloads(Func<string?> knownFolder, Func<string, bool> directoryExists, string? userProfile, string? documents)
    {
        ArgumentNullException.ThrowIfNull(knownFolder);
        ArgumentNullException.ThrowIfNull(directoryExists);
        string? known;
        try
        {
            known = knownFolder();
        }
        catch (Exception ex) when (ex is COMException or ExternalException or DllNotFoundException or EntryPointNotFoundException)
        {
            known = null;
        }

        foreach (var candidate in new[] { known, string.IsNullOrEmpty(userProfile) ? null : Path.Combine(userProfile, "Downloads"), documents })
        {
            if (!string.IsNullOrWhiteSpace(candidate) && directoryExists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    /// <summary>The path of FOLDERID_Downloads for the current user, or null.</summary>
    private static string? KnownDownloadsFolder()
    {
        var path = IntPtr.Zero;
        try
        {
            var folderId = DownloadsFolderId;
            return SHGetKnownFolderPath(ref folderId, 0, IntPtr.Zero, out path) == 0 ? Marshal.PtrToStringUni(path) : null;
        }
        finally
        {
            if (path != IntPtr.Zero)
            {
                Marshal.FreeCoTaskMem(path);
            }
        }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHGetKnownFolderPath(ref Guid folderId, uint flags, IntPtr token, out IntPtr path);
}
