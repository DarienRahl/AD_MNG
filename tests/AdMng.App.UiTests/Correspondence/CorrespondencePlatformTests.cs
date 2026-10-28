using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using AdMng.App.Correspondence;
using AdMng.Presentation.Services;

namespace AdMng.App.UiTests.Correspondence;

/// <summary>
/// The Windows services of Korespondencja (U5, design note 9.4): the DPAPI protector of the journal key (round trip, not a
/// pass-through, bound to its entropy, a damaged blob refused) and the platform (the Downloads folder with MailerWPiA's
/// fallbacks, the ANSI code page of the regional settings).
/// </summary>
public sealed class CorrespondencePlatformTests
{
    [Fact]
    public void Dpapi_ProtectsAndUnprotects_TheJournalKey()
    {
        SkipUnlessWindows();
        var protector = new DpapiKeyProtector();
        var key = RandomNumberGenerator.GetBytes(32);

        var blob = protector.Protect(key);

        Assert.NotEqual(key, blob);
        Assert.True(blob.Length > key.Length);
        Assert.Equal(-1, blob.AsSpan().IndexOf(key));   // the key is nowhere in the blob
        Assert.Equal(key, protector.Unprotect(blob));
        Assert.NotEqual(blob, protector.Protect(key));   // a new salt every time
        Assert.Equal(nameof(DpapiKeyProtector), protector.ToString());
    }

    [Fact]
    public void Dpapi_IsBoundToItsEntropy_AndRefusesADamagedBlob()
    {
        SkipUnlessWindows();
        var protector = new DpapiKeyProtector();
        var key = RandomNumberGenerator.GetBytes(32);
        var blob = protector.Protect(key);

        // Without the entropy, or with another one, the key cannot be read.
        Assert.ThrowsAny<CryptographicException>(() => ProtectedData.Unprotect(blob, null, DataProtectionScope.CurrentUser));
        Assert.ThrowsAny<CryptographicException>(() => ProtectedData.Unprotect(blob, Encoding.UTF8.GetBytes("AdMng.Inne"), DataProtectionScope.CurrentUser));
        Assert.Equal(key, ProtectedData.Unprotect(blob, Encoding.UTF8.GetBytes(DpapiKeyProtector.Entropy), DataProtectionScope.CurrentUser));

        var damaged = (byte[])blob.Clone();
        damaged[^5] ^= 0x5A;
        Assert.ThrowsAny<CryptographicException>(() => protector.Unprotect(damaged));
        Assert.ThrowsAny<CryptographicException>(() => protector.Unprotect([1, 2, 3]));
        Assert.Throws<ArgumentNullException>(() => protector.Protect(null!));
        Assert.Throws<ArgumentNullException>(() => protector.Unprotect(null!));
        Assert.Empty(protector.Unprotect(protector.Protect([])));
    }

    [Fact]
    public void Downloads_FollowsMailerWPiAsOrder()
    {
        var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            @"D:\OneDrive - Contoso\Pobrane",
            @"C:\Users\jkowalski\Downloads",
            @"C:\Users\jkowalski\Documents",
        };
        bool Exists(string path) => existing.Contains(path);

        // The known folder (redirected) when it exists.
        Assert.Equal(@"D:\OneDrive - Contoso\Pobrane",
            WindowsCorrespondencePlatform.ResolveDownloads(() => @"D:\OneDrive - Contoso\Pobrane", Exists, @"C:\Users\jkowalski", @"C:\Users\jkowalski\Documents"));

        // Missing, failing or unknown: the profile's Downloads, then Documents, then nothing.
        Assert.Equal(Path.Combine(@"C:\Users\jkowalski", "Downloads"),
            WindowsCorrespondencePlatform.ResolveDownloads(() => @"E:\Nie ma", Exists, @"C:\Users\jkowalski", @"C:\Users\jkowalski\Documents"));
        Assert.Equal(Path.Combine(@"C:\Users\jkowalski", "Downloads"),
            WindowsCorrespondencePlatform.ResolveDownloads(() => throw new COMException("brak"), Exists, @"C:\Users\jkowalski", null));
        Assert.Equal(@"C:\Users\jkowalski\Documents",
            WindowsCorrespondencePlatform.ResolveDownloads(() => null, Exists, @"C:\Users\Inny", @"C:\Users\jkowalski\Documents"));
        Assert.Equal(@"C:\Users\jkowalski\Documents",
            WindowsCorrespondencePlatform.ResolveDownloads(() => "  ", Exists, string.Empty, @"C:\Users\jkowalski\Documents"));
        Assert.Null(WindowsCorrespondencePlatform.ResolveDownloads(() => null, _ => false, @"C:\Users\jkowalski", @"C:\Users\jkowalski\Documents"));
    }

    [Fact]
    public void ThePlatform_OfThisComputer_HasAnExistingDownloadsFolder_AndTheAnsiCodePage()
    {
        SkipUnlessWindows();
        var platform = new WindowsCorrespondencePlatform(new SystemRegionalSettings(CultureInfo.GetCultureInfo("pl-PL"), ansiCodePage: 1250));

        var downloads = platform.DownloadsFolder;

        Assert.True(downloads is null || System.IO.Directory.Exists(downloads), downloads);
        Assert.Equal(1250, platform.AnsiCodePage);
        Assert.Equal(nameof(WindowsCorrespondencePlatform), platform.ToString());
        Assert.Equal(new Guid("374DE290-123F-4565-9164-39C4925E467B"), WindowsCorrespondencePlatform.DownloadsFolderId);
    }

    private static void SkipUnlessWindows() => Assert.SkipUnless(OperatingSystem.IsWindows(), "DPAPI and the known folders exist on Windows only.");
}
