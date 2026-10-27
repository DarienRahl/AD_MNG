using AdMng.Core.Messaging.Images;

namespace AdMng.Core.Tests.Messaging.Images;

/// <summary>Only local drive paths in the temp folder are readable – never UNC, device paths, hosts or streams.</summary>
public class ClipboardImagePolicyTests
{
    private const string Temp = @"C:\Users\jkowalski\AppData\Local\Temp";

    [Theory]
    [InlineData("file:///C:/Users/JKOWAL~1/AppData/Local/Temp/msohtmlclip1/01/clip_image001.png", @"C:\Users\JKOWAL~1\AppData\Local\Temp\msohtmlclip1\01\clip_image001.png")]
    [InlineData("file:///c:/Users/jkowalski/AppData/Local/Temp/msohtmlclip1/01/clip%20image.png", @"C:\Users\jkowalski\AppData\Local\Temp\msohtmlclip1\01\clip image.png")]
    [InlineData("file:///C|/Temp/a.png", @"C:\Temp\a.png")]
    [InlineData("FILE:///C:/Temp/./x/../a.png", @"C:\Temp\a.png")]
    public void LocalDrivePaths_AreAccepted(string uri, string expected) => Assert.Equal(expected, ClipboardImagePolicy.LocalPath(uri, windows: true));

    [Theory]
    [InlineData(@"file://server/share/clip_image001.png")]
    [InlineData(@"file:////server/share/clip_image001.png")]
    [InlineData(@"file://///server/share/a.png")]
    [InlineData(@"file://localhost/C:/Temp/a.png")]
    [InlineData(@"file:///%5C%5Cserver%5Cshare%5Ca.png")]
    [InlineData(@"file:///%5C%5C%3F%5CC:%5CTemp%5Ca.png")]
    [InlineData(@"file:///%5C%5C.%5CPhysicalDrive0")]
    [InlineData(@"file:///C:/Temp/a.png:Zone.Identifier")]
    [InlineData(@"file:///C:/../a.png")]
    [InlineData(@"file:C:/Temp/a.png")]
    [InlineData(@"\\server\share\a.png")]
    [InlineData(@"https://contoso.pl/logo.png")]
    [InlineData(@"file:///Temp/a.png")]
    [InlineData(@"file:///C:/Temp/a%00.png")]
    [InlineData("")]
    public void NetworkDeviceAndOtherPaths_AreRefused(string uri) => Assert.Null(ClipboardImagePolicy.LocalPath(uri, windows: true));

    [Fact]
    public void UnixPaths_ForTheTestsOnLinux()
    {
        Assert.Equal("/tmp/msohtmlclip1/01/clip_image001.png", ClipboardImagePolicy.LocalPath("file:///tmp/msohtmlclip1/01/clip_image001.png", windows: false));
        Assert.Null(ClipboardImagePolicy.LocalPath("file:////server/share/a.png", windows: false));
        Assert.Null(ClipboardImagePolicy.LocalPath("file:///tmp/../../etc/passwd", windows: false));
    }

    [Theory]
    [InlineData(@"C:\Users\jkowalski\AppData\Local\Temp\msohtmlclip1\01\clip_image001.png", true)]
    [InlineData(@"c:\users\JKOWALSKI\appdata\local\temp\a.png", true)]
    [InlineData(@"C:\Users\jkowalski\AppData\Local\TempX\a.png", false)]
    [InlineData(@"C:\Users\jkowalski\AppData\Local\Temp", false)]
    [InlineData(@"C:\Users\jkowalski\Documents\a.png", false)]
    [InlineData(@"D:\Users\jkowalski\AppData\Local\Temp\a.png", false)]
    public void OnlyFilesInsideTheTempFolder(string path, bool inside) => Assert.Equal(inside, ClipboardImagePolicy.IsInside(path, Temp, windows: true));

    [Fact]
    public void ShortNames_MustBeExpandedByTheCaller()
    {
        // 8.3 names are compared only after the file system expanded them (PastedImageFileSource does it); the short and
        // the long spelling are different strings here.
        Assert.False(ClipboardImagePolicy.IsInside(@"C:\Users\JKOWAL~1\AppData\Local\Temp\a.png", Temp, windows: true));
        Assert.True(ClipboardImagePolicy.IsInside(@"C:\Users\JKOWAL~1\AppData\Local\Temp\a.png", @"C:\Users\JKOWAL~1\AppData\Local\Temp\", windows: true));
    }

    [Fact]
    public void FileName_IsTheOnlyPartKept()
    {
        Assert.Equal("clip_image001.png", ClipboardImagePolicy.FileName(@"C:\Users\jkowalski\AppData\Local\Temp\msohtmlclip1\01\clip_image001.png"));
        Assert.Equal("a.png", ClipboardImagePolicy.FileName("/tmp/a.png"));
        Assert.Equal(string.Empty, ClipboardImagePolicy.FileName(null));
    }
}
